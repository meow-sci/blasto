---
type: Mod Feature
title: Course colliders registered as ground clutter
description: Static Bepu boxes added to every nearby physics bubble from worker-thread prefixes on ConstraintSim and registered by reflection as infinite-mass ground clutter, the only kind of mod-made static the engine lets a kitten stand on
mod: ksagolf
category: physics
techniques: [harmony-prefix, reflection, bepu-physics, main-thread-sync-point, worker-thread-patch]
hooks:
  - harmony-prefix KSA.Universe.ExecuteNextVehicleSolvers
  - harmony-prefix KSA.ConstraintSim.DetectCollisions
  - harmony-prefix KSA.ConstraintSim.Simulate
  - harmony-prefix KSA.ConstraintSim.TryResetForPool
  - reflection KSA.BubbleClutterStatics._statics
lifecycle: [frame:draw-ui, frame:dispatch-solvers, physics:vehicle-solver, physics:bepu]
game_types:
  - KSA.Universe
  - KSA.ConstraintSim
  - KSA.ConstraintSimPool
  - KSA.BubbleClutterStatics
  - KSA.NarrowPhaseCallbacks
  - KSA.PhysicsBubble
  - KSA.VehicleUpdateTask
  - KSA.VehicleUpdateState
  - KSA.ReadOnlyPhysicsStates
  - KSA.BubbleOrigin
  - KSA.BubbleFrameEx
  - KSA.StaticsShouldntAwakenBodies
  - KSA.ShapesUnlock
  - BepuPhysics.Statics
  - BepuPhysics.StaticDescription
  - BepuPhysics.StaticHandle
  - BepuPhysics.RigidPose
  - BepuPhysics.Collidables.Box
  - BepuPhysics.Collidables.Shapes
  - BepuPhysics.Collidables.TypedIndex
tags:
  - bepu
  - statics
  - clutter
  - collider
  - physics-bubble
  - worker-thread
  - harmony
  - reflection
  - kitten
  - minigolf
---

A minigolf course is drawn from the mod's own meshes, which the physics knows nothing about, so a
kitten would walk straight through it. KSAGolf makes each piece's slab a static Bepu box in every
physics bubble near the course and registers each box as **ground clutter** with infinite mass.
Clutter is one of the three kinds of static the engine's narrow phase lets touch a body, the only
one a mod can add to, and the one kitten locomotion counts as ground.[^golf-coursecollider][^golf-notes]

The interesting part is the threading. Shapes live in one registry shared by every simulation and
writable only between vehicle steps, while each bubble's simulation is stepped on a worker thread.
So shapes are made on the main thread at the sync point and handed over through a volatile
snapshot, and the statics themselves are added from prefixes that run on the worker, inside the
very simulation that is about to detect collisions.

## How it works

1. **Slabs (Sim, no KSA types).** `CourseColliders.Of(course)` turns each placed piece into boxes in
   course-local metres: a flat piece is one box from its felt (`Level * 0.30 m`) down to
   `-PieceShape.BaseDepthM` (0.10 m below the course floor), or one box per tile for a non-rectangular
   piece so its free corner stays free; a ramp is one tilted slab 0.12 m thick spanning its two port
   edges. Rails are left out so a kitten steps across lanes instead of catching on them.[^golf-coursecolliders]
2. **Publish (main thread).** After every edit, `CourseBuilder.Publish` converts each box to the
   body-fixed frame (`LocalToCcf`, axes as a `doubleQuat`) and calls `CourseCollider.Want(body, boxes)`,
   which bumps a version and swaps a volatile `_wanted`.[^golf-coursebuilder]
3. **Shapes (main thread, sync point).** A prefix on `Universe.ExecuteNextVehicleSolvers` sees a new
   version, opens `ConstraintSim.UnlockShapes()`, and adds one `BepuPhysics.Collidables.Box` per
   distinct size (keyed to 0.1 mm, cached for the session, never freed), then swaps a volatile
   `_ready` holding body, box poses and shape indices.
4. **Statics (worker thread, per bubble).** Prefixes on `ConstraintSim.DetectCollisions` and
   `ConstraintSim.Simulate` call `Sync(sim)`: read the bubble's origin from its first vehicle's
   `GetReadOnlyStates().Origin`; if the bubble is in the `Ccf` frame of the course's body and within
   3 km, add each box with `Simulation.Statics.Add(description, ref StaticsShouldntAwakenBodies.Shared)`
   positioned at `centreCcf - PositionBub`, re-apply descriptions when the bubble origin moved, and
   remove them when the course is gone or out of reach.
5. **Register as clutter (reflection).** Each handle is written into the private
   `BubbleClutterStatics._statics` dictionary of that simulation's `ClutterStatics`, with a value
   built once at install by reflection: the 2-argument `ClutterStaticInfo(key, mass)` constructor
   with a default key and `float.PositiveInfinity`.
6. **Recycle.** A prefix on `ConstraintSim.TryResetForPool` drops the per-simulation state, because
   the engine clears a pooled simulation's statics itself. Per-simulation state lives in a
   `ConditionalWeakTable<ConstraintSim, SimState>`.

Why clutter, verified in the decomp:

- `NarrowPhaseCallbacks.AllowContactGeneration` returns true for a static-body pair only if the
  static is clutter (`IsClutterStatic`), a terrain block, or the vehicle's own ground surface
  (`BepuHandles.IsGroundSurface`: its terrain or launch-pad static). Any other static in
  `Simulation.Statics` generates no contacts.[^ksa-narrowphase]
- `ConstraintSim.IsGroundSurfaceFor` uses the same three tests, and it is what the narrow phase's
  kitten ground check and the terrain-contact normal accumulation ask, so clutter is ground for a
  standing kitten.[^ksa-constraintsim][^ksa-narrowphase]
- `BubbleClutterStatics.ReportHit` and `ApplyPendingHits` displace or destroy an instance when the
  impact energy reaches `Mass * DisplaceEnergyPerKg` or `Mass * DestroyEnergyPerKg`; with infinite
  mass that never happens.[^ksa-clutterstatics]
- `BubbleClutterStatics.Clear` ends with `_statics.Clear()` and only removes the statics it created.
  `PhysicsBubble.SyncGroundClutterStatics` calls it on every sync whenever clutter collisions are off
  in settings, the bubble is not `Ccf`, or the planet has no clutter data, so the mod's registration
  can be wiped any frame. Re-registering at the start of every pass restores it before Bepu's
  collision detection runs.[^ksa-clutterstatics][^ksa-physicsbubble]

## Lifecycle and timing

| Hook | Runs in | Thread | Safe to touch |
|---|---|---|---|
| `Want` (from the builder, `[StarMapAfterGui]`) | `frame:draw-ui` | main | Only the mod's volatile snapshot |
| Prefix `Universe.ExecuteNextVehicleSolvers` | `frame:dispatch-solvers`, inside `Program.PrepareFrame` | main | The shared shapes registry, under `UnlockShapes()`; nothing is stepping[^ksa-universe] |
| Prefix `ConstraintSim.DetectCollisions` | `PhysicsBubble.FullPhysicsStepUntil`, `physics:vehicle-solver` | vehicle worker | That bubble's own `Simulation.Statics` and clutter registry[^ksa-physicsbubble] |
| Prefix `ConstraintSim.Simulate` | `PhysicsBubble.FullPhysicsConstrainedStep`, `physics:bepu` | vehicle worker | Same |
| Prefix `ConstraintSim.TryResetForPool` | `ConstraintSimPool.Return` | worker, vehicle-solver job or main | The mod's own per-simulation table only[^ksa-constraintsimpool] |

`VehicleUpdateTask.Run` steps bubbles in contact islands through
`JobSystems.VehicleWorkerPool.ParallelBatch()`; `ContactIsland.Execute` runs `RunStep` per bubble on
a pool worker, which reaches `DetectCollisions` and `Simulate`. Different bubbles run in parallel,
but each `ConstraintSim` belongs to one bubble, so one prefix call owns the simulation it mutates.
The only shared data are the immutable `Ready` snapshot (read once per call through a volatile
field) and the `ConditionalWeakTable`, which is thread-safe.[^ksa-vehicleupdatetask]
Neither prefix may throw: they run inside the physics step, possibly several times per frame per
bubble, so `Sync` is wrapped in a try/catch that logs once. It allocates new statics only when the
course changes, but writing through the non-generic `IDictionary` indexer boxes each `StaticHandle`
key on every pass, a small steady allocation inside the worker step.[^golf-coursecollider]

A simulation is returned to the pool from `PhysicsBubble.RunPostWorkAndCluster` after 30 s of
simulated time without needing it (a worker), from `PhysicsBubble.TryResetForPool` when a bubble is
trimmed inside `VehicleUpdateTask.Run`, or from `Universe.DeserializeSave` via
`DestroyAllPhysicsBubbles` at a save load (the main thread).[^ksa-physicsbubble][^ksa-universe]

Why the statics are not added at the main-thread sync point, where the engine syncs its own
clutter (`SyncGroundClutter` inside `ExecuteNextVehicleSolvers`): the repo's reason is that each
simulation steps on a worker. The decomp adds two more. A bubble's simulation is rented lazily
inside the step (`InitializeConstraintSim` in `FullPhysicsStepUntil`) and bubbles merge and split
inside `VehicleUpdateTask.Run`, so the simulation that will step may not exist at the sync point;
and the engine's clutter sync, which can clear `_statics`, runs in the method body after the
mod's prefix. A pass-time prefix runs after both and before contact generation.

## Key code

| Path | Role |
|---|---|
| [src/KSAGolf/Ksa/CourseCollider.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseCollider.cs#L63-L101) | Install: reflection on `_statics`, the four prefixes |
| [src/KSAGolf/Ksa/CourseCollider.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseCollider.cs#L112-L155) | `Want`, `Place`, `BetweenVehicleSteps`, `ShapeOf` |
| [src/KSAGolf/Ksa/CourseCollider.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseCollider.cs#L157-L241) | Worker-side `Sync`, `Describe`, `Clear`, `TryOrigin`, `BeforeRecycle` |
| [src/KSAGolf/Sim/CourseColliders.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/CourseColliders.cs) | Slab maths: flat boxes, per-tile boxes, tilted ramp slabs |
| [src/KSAGolf/Ksa/CourseBuilder.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseBuilder.cs#L391-L406) | `Publish`: course-local boxes to body-fixed, handed to the collider |
| [tests/KSAGolf.Tests/CourseCollidersTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/CourseCollidersTests.cs) | Slab tops are the felt, a bend's empty corner has nothing, slabs reach into the ground |

## Snippets

```csharp
// src/KSAGolf/Ksa/CourseCollider.cs L67-L86
        try
        {
            _clutterStatics = AccessTools.Field(typeof(BubbleClutterStatics), "_statics");
            Type? info = _clutterStatics?.FieldType.GetGenericArguments() is [_, var value] ? value : null;
            ConstructorInfo? make = info?.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).FirstOrDefault(c => c.GetParameters().Length == 2);
            if (_clutterStatics is null || make is null)
            {
                Log.Warn("kittens walk through courses: KSA's ground clutter no longer has the _statics a course registers in");
                return;
            }

            ParameterInfo[] args = make.GetParameters();
            _solidClutter = make.Invoke([Activator.CreateInstance(args[0].ParameterType), float.PositiveInfinity]);

            _harmony = new Harmony(HarmonyId);
            Patch(typeof(Universe), nameof(Universe.ExecuteNextVehicleSolvers), nameof(BetweenVehicleSteps));
            Patch(typeof(ConstraintSim), nameof(ConstraintSim.DetectCollisions), nameof(BeforeCollisions));
            Patch(typeof(ConstraintSim), nameof(ConstraintSim.Simulate), nameof(BeforeCollisions));
            Patch(typeof(ConstraintSim), nameof(ConstraintSim.TryResetForPool), nameof(BeforeRecycle));
            Installed = true;
```

```csharp
// src/KSAGolf/Ksa/CourseCollider.cs L126-L142
    // Main thread, between vehicle steps: the one moment the shared shape registry can be written.
    private static void BetweenVehicleSteps()
    {
        if (_wanted is not { } wanted || _ready?.Version == wanted.Version) return;

        try
        {
            using ShapesUnlock unlocked = ConstraintSim.UnlockShapes();
            Box[] boxes = [.. wanted.Boxes.Select(b => new Box(b.Centre, ToBepu(b.Orientation), ShapeOf(unlocked.Shapes, b.Size)))];
            _ready = new Ready(wanted.Body, boxes, wanted.Version);
            Log.Info($"course is ground: {boxes.Length} slab(s), {ShapesBySize.Count} shape(s) made so far");
        }
        catch (Exception e)
        {
            Complain($"could not make the course's shapes yet, will try again: {e.GetBaseException().Message}");
        }
    }
```

```csharp
// src/KSAGolf/Ksa/CourseCollider.cs L170-L202
    private static void Sync(ConstraintSim sim)
    {
        Ready? ready = _ready;
        SimState state = States.GetOrCreateValue(sim);

        if (!TryOrigin(sim, out bool ccf, out IParentBody? parent, out double3 bub)) return;

        bool here = ready is { Boxes.Length: > 0 } && ccf && ReferenceEquals(parent, ready.Body)
                    && Vector3Distance(ready.Boxes[0].CentreCcf, bub) < ReachM;
        if (!here)
        {
            if (state.Handles.Length > 0) Clear(sim, state);
            return;
        }

        if (state.Version != ready!.Version)
        {
            Clear(sim, state);
            state.Handles = [.. ready.Boxes.Select(b => sim.Simulation.Statics.Add(Describe(b, bub), ref KSA.StaticsShouldntAwakenBodies.Shared))];
            state.Version = ready.Version;
            state.Bub = bub;
        }
        else if (state.Bub != bub)
        {
            for (int k = 0; k < state.Handles.Length; k++) sim.Simulation.Statics.ApplyDescription(state.Handles[k], Describe(ready.Boxes[k], bub), ref KSA.StaticsShouldntAwakenBodies.Shared);
            state.Bub = bub;
        }

        if (sim.ClutterStatics is { } clutter && _clutterStatics?.GetValue(clutter) is IDictionary registered)
        {
            foreach (StaticHandle handle in state.Handles) registered[handle] = _solidClutter;
        }
    }
```

```csharp
// src/KSAGolf/Sim/CourseColliders.cs L35-L61
            if (ports.Length < 2 || ports[0].Level == ports[1].Level)
            {
                double top = piece.Level * CourseSize.LevelM, bottom = -PieceShape.BaseDepthM;
                ColliderBox Flat(double2 centre, double width, double depth)
                    => new(new double3(centre.X, centre.Y, (top + bottom) / 2.0), new double3(1, 0, 0), new double3(0, 1, 0), new double3(0, 0, 1),
                           new double3(width, depth, top - bottom));

                // A rectangle is one box; a shaped piece, a box per tile, so its empty cells stay empty.
                if (piece.Def.IsRectangle) boxes.Add(Flat(middle, w * c, h * c));
                else boxes.AddRange(piece.Cells().Select(t => Flat(new double2((t.I + 0.5) * c, (t.J + 0.5) * c), c, c)));
                continue;
            }

            double3 a = Edge(ports[0]), b = Edge(ports[1]);
            double3 along = b - a;
            double3 y = Vec.Unit(along);
            double3 x = Vec.Unit(new double3(-along.Y, along.X, 0.0));
            double3 z = Vec.Cross(x, y);
            if (z.Z < 0.0)
            {
                x = -x;
                z = -z;
            }

            double width = (Math.Abs(x.X) * w * c) + (Math.Abs(x.Y) * h * c);
            double3 centre = ((a + b) / 2.0) - (z * (RampThicknessM / 2.0));
            boxes.Add(new ColliderBox(centre, x, y, z, new double3(width, Vec.Len(along), RampThicknessM)));
```

## Game APIs touched

| API | Decomp | Purpose |
|---|---|---|
| `Universe.ExecuteNextVehicleSolvers` / `SyncGroundClutter` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2201) | Patched (prefix) for shape creation; the body syncs the engine's own clutter afterwards |
| `ConstraintSim.DetectCollisions` / `Simulate` | [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L870-L904) | Patched (prefix); each begins a contact pass and runs Bepu collision detection or a timestep |
| `ConstraintSim.TryResetForPool` | [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L193-L216) | Patched (prefix); clears `Simulation.Statics` and the rest for pooling |
| `ConstraintSim.ClutterStatics` / `HandleToState` / `IsGroundSurfaceFor` / `UnlockShapes` | [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L54-L180) | The bubble's clutter registry, its vehicles, the ground test, the registry lock |
| `BubbleClutterStatics._statics` (private) / `ClutterStaticInfo` | [current/decomp/KSA/BubbleClutterStatics.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BubbleClutterStatics.cs#L224) | `Dictionary<StaticHandle, ClutterStaticInfo>` (the value type, a two-field struct of key and mass, is at L85); written by reflection |
| `BubbleClutterStatics.IsClutterStatic` / `ReportHit` / `ApplyPendingHits` / `Clear` | [current/decomp/KSA/BubbleClutterStatics.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BubbleClutterStatics.cs) | Membership test, mass-based displacement and destruction, wholesale clear |
| `NarrowPhaseCallbacks.AllowContactGeneration` | [current/decomp/KSA/NarrowPhaseCallbacks.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/NarrowPhaseCallbacks.cs#L24-L57) | Which statics may touch a body at all |
| `PhysicsBubble.SyncGroundClutterStatics` / `ReturnConstraintSim` / `RunPostWorkAndCluster` | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs) | Engine clutter sync and clearing; simulation return after 30 s idle |
| `VehicleUpdateTask.Run` / `ContactIsland.Execute` | [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs) | Bubbles stepped in parallel on `VehicleWorkerPool` |
| `ConstraintSimPool.Return` | [current/decomp/KSA/ConstraintSimPool.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSimPool.cs) | Calls `TryResetForPool` before pooling |
| `BubbleOrigin.Parent` / `BubFrame` / `PositionBub` | [current/decomp/KSA/BubbleOrigin.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BubbleOrigin.cs#L23-L27) | The bubble's parent body, frame and origin the statics are placed against |
| `Statics.Add<TFilter>` / `ApplyDescription<TFilter>` / `Remove` | [current/decomp/BepuPhysics/Statics.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/BepuPhysics/Statics.cs#L170-L251) | Adding, moving and removing static boxes without waking sleeping bodies |

## Pitfalls and version notes

- **A plain static does nothing.** Adding to `Simulation.Statics` without the clutter registration
  produces no contacts; registering it is the whole trick.[^ksa-narrowphase][^golf-notes]
- **Terrain blocks are not an alternative.** The repo found that `TryRejectBackfaceTerrainContact`
  drops contacts more than about 84 degrees off a terrain block's +Z, so an upright box cannot be
  one.[^golf-notes]
- **The registration does not stick.** Clutter's own `Clear` wipes `_statics`; re-register before
  every pass.
- **Statics are bubble-relative.** Positions are `centreCcf - PositionBub` in `float`; re-apply when
  the bubble origin moves, and only use a bubble whose `BubFrame.IsCcf()` (landed, body-fixed).
- **Recycled simulations.** Without the `TryResetForPool` prefix the mod would believe a pooled
  simulation still holds its handles; the engine has already cleared them.
- **Shapes leak by design.** Box shapes are cached per size and never removed from the shared
  registry; fine for a handful of sizes, not for arbitrary geometry.
- **Reflection target.** `BubbleClutterStatics._statics` and its value type's two-argument
  constructor are private. If they change, `Install` logs "kittens walk through courses" and patches
  nothing; losing it costs only the collision.[^golf-claude]
- **The ball does not use these boxes.** The ball rolls on exact shapes through `CourseSurface`;
  these boxes are for kittens and craft only.
- **Version.** Built against 2026.9.22.5482. `ConstraintSim.cs`, `BubbleClutterStatics.cs` and
  `NarrowPhaseCallbacks.cs` are unchanged in 2026.10.10.5554; the `PhysicsBubble.cs` diff concerns
  acceleration bounds and step diagnostics, not clutter or contact passes.

## See also

- [Launch-site surface by raycast into a copied collider](launch-site-surface-raycast.md)
- [Course surface from exact shapes](course-surface-exact-shapes.md)
- [Top-down minigolf course builder](minigolf-course-builder.md)
- [Course grid model and JSON save file](course-grid-and-save-file.md)
- [Save-load detection](save-load-detection.md)
- [Run code inside the physics step on worker threads](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md) - how-to article demonstrating this feature
- [Worker prefixes on DetectCollisions and Simulate with statics registered as ground clutter](../../../howto/physics/colliders/add-static-colliders-to-physics-bubbles/add-static-colliders-to-physics-bubbles-2.md) - how-to article demonstrating this feature
- [Register course statics as clutter on every pass](../../../howto/physics/colliders/give-ground-clutter-custom-colliders/give-ground-clutter-custom-colliders-4.md) - how-to article demonstrating this feature
- [Create shapes in an ExecuteNextVehicleSolvers prefix and cache by size](../../../howto/physics/colliders/mutate-the-shared-bepu-shape-registry/mutate-the-shared-bepu-shape-registry-1.md) - how-to article demonstrating this feature
- [Volatile snapshot handoff from worker patches to the main thread](../../../howto/physics/solver/write-worker-thread-safe-solver-patches/write-worker-thread-safe-solver-patches-4.md) - how-to article demonstrating this feature

[^golf-coursecollider]: src/KSAGolf/Ksa/CourseCollider.cs
[^golf-coursecolliders]: src/KSAGolf/Sim/CourseColliders.cs
[^golf-coursebuilder]: src/KSAGolf/Ksa/CourseBuilder.cs
[^golf-notes]: docs/KSA-MODDING-NOTES.md (a mod's own static collides only if the engine is told it is ground)
[^golf-claude]: CLAUDE.md (patches whose absence degrades)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-constraintsim]: KSA.ConstraintSim (decompiled, build 2026.10.10.5554)
[^ksa-constraintsimpool]: KSA.ConstraintSimPool (decompiled, build 2026.10.10.5554)
[^ksa-clutterstatics]: KSA.BubbleClutterStatics (decompiled, build 2026.10.10.5554)
[^ksa-narrowphase]: KSA.NarrowPhaseCallbacks (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatetask]: KSA.VehicleUpdateTask (decompiled, build 2026.10.10.5554)
