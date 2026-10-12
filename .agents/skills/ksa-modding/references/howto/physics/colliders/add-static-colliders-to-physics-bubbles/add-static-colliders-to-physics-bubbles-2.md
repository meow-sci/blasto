---
type: How-To
title: Worker prefixes on DetectCollisions and Simulate with statics registered as ground clutter
description: Add Bepu box statics to each Ccf bubble from prefixes on the constraint-sim passes and register them as infinite-mass ground clutter, so the stock filter already treats them as ground for kittens and vessels
category: physics
group: colliders
topic: add-static-colliders-to-physics-bubbles
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksagolf/features/course-colliders-as-clutter]
techniques: [worker-thread-patch, reflection, harmony-prefix]
hooks:
  - harmony-prefix KSA.ConstraintSim.DetectCollisions
  - harmony-prefix KSA.ConstraintSim.Simulate
  - harmony-prefix KSA.ConstraintSim.TryResetForPool
  - harmony-prefix KSA.Universe.ExecuteNextVehicleSolvers
lifecycle: [physics:vehicle-solver, physics:bepu, frame:dispatch-solvers]
game_types: [KSA.ConstraintSim, KSA.BubbleClutterStatics, KSA.PhysicsBubble, BepuPhysics.Statics, KSA.KittenEva]
tags: [physics, bepu, statics, colliders, clutter, kittens, worker-thread, reflection, golf]
---

Outcome: a minigolf course is solid for kittens and craft without touching the contact filter. KSAGolf makes each
piece's slab a static Bepu box in every physics bubble near the course and registers each box as **ground clutter**
with infinite mass. Clutter is one of the three kinds of static the engine's narrow phase lets touch a body, the only
one a mod can add to, and the one kitten locomotion counts as ground. Prerequisites: StarMap and Harmony. The ball does not use
these boxes (it rolls on [exact analytic shapes](../../mod-simulation/prevent-tunnelling-of-fast-bodies/prevent-tunnelling-of-fast-bodies-3.md)).

## Background: how the game does it

Why clutter, verified in the decomp:

- `NarrowPhaseCallbacks.AllowContactGeneration` returns true for a static-body pair only if the static is clutter,
  a terrain block, or the vehicle's own ground surface; any other static generates no contacts.[^ksa-narrowphasecallbacks]
- `ConstraintSim.IsGroundSurfaceFor` uses the same tests, so clutter is ground for a standing kitten.[^ksa-constraintsim]
- `BubbleClutterStatics.ReportHit` and `ApplyPendingHits` displace or destroy an instance when impact energy reaches
  `Mass * DisplaceEnergyPerKg` or `Mass * DestroyEnergyPerKg`; with infinite mass that never happens.[^ksa-bubbleclutterstatics]
- `BubbleClutterStatics.Clear` ends with `_statics.Clear()`, and `PhysicsBubble.SyncGroundClutterStatics` calls it on
  every sync whenever clutter collisions are off in settings, the bubble is not `Ccf`, or the planet has no clutter
  data, so the registration can be wiped any frame. Re-registering at the start of every pass restores it before
  Bepu's collision detection runs.

Terrain blocks are not an alternative: `TryRejectBackfaceTerrainContact` drops contacts more than about 84 degrees
off a terrain block's +Z, so an upright box cannot be one.

## Lifecycle and timing

| Hook | Runs in | Thread | Safe to touch |
|---|---|---|---|
| `Want` (from the builder, `StarMapAfterGui`) | `frame:draw-ui` | main | Only the mod's volatile snapshot |
| Prefix `Universe.ExecuteNextVehicleSolvers` | `frame:dispatch-solvers` | main | The shared shapes registry, under `UnlockShapes()` (see [registry approach 1](../mutate-the-shared-bepu-shape-registry/mutate-the-shared-bepu-shape-registry-1.md)) |
| Prefixes `ConstraintSim.DetectCollisions`, `Simulate` | `PhysicsBubble.FullPhysicsStepUntil` / `FullPhysicsConstrainedStep` | vehicle worker | That bubble's own `Simulation.Statics` and clutter registry |
| Prefix `ConstraintSim.TryResetForPool` | `ConstraintSimPool.Return` | worker or main | The mod's per-simulation table only |

Bubbles run in parallel on pool workers but each `ConstraintSim` belongs to one bubble, so one prefix call owns the
simulation it mutates; the only shared data are an immutable `Ready` snapshot (read once per call through a volatile
field) and a `ConditionalWeakTable`. Neither prefix may throw: they run inside the physics step, possibly several
times per frame per bubble. Why not add the statics at the main-thread sync point: a bubble's simulation is rented
lazily inside the step and bubbles merge and split inside `VehicleUpdateTask.Run`, so the simulation that will step may not
exist yet; and the engine's clutter sync, which can clear `_statics`, runs after the mod's prefix on the main thread.
A pass-time prefix runs after both and before contact generation. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Compute the boxes in a KSA-free class. A flat piece is one box from its felt down to 0.10 m below the course
   floor, or one box per tile for a non-rectangular piece so its free corner stays free; a ramp is one tilted slab 0.12 m
   thick spanning its two port edges. Rails are left out so a kitten steps across lanes instead of catching on them:

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

2. Get hold of the private clutter registry by reflection: `BubbleClutterStatics._statics` is a
   `Dictionary<StaticHandle, ClutterStaticInfo>`, and the value type's two-argument constructor (key, mass) is built
   once with a default key and `float.PositiveInfinity`. If either moved, log a warning and install nothing (losing it
   costs only the collision):

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

3. Sync from the worker prefix. Read the bubble origin from its first vehicle; use the bubble only if it is `Ccf`, its
   parent is the course's body, and it is within 3 km. Add each box positioned at `centreCcf - PositionBub` with
   `Statics.Add(description, ref StaticsShouldntAwakenBodies.Shared)`, re-apply descriptions when the origin moved,
   remove them when the course is gone, and **re-register every handle as clutter every pass**:

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

4. Recycle. A prefix on `ConstraintSim.TryResetForPool` drops the per-simulation state, because the engine clears a
   pooled simulation's statics itself and the mod would otherwise believe it still holds its handles. Keep the
   per-simulation state in a `ConditionalWeakTable<ConstraintSim, SimState>`.
5. Publish after every edit: convert each course-local box to the body-fixed frame and hand the list to the collider,
   which bumps a version and swaps a volatile snapshot (`CourseBuilder.Publish`).

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Ksa/CourseCollider.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseCollider.cs#L63-L241) | Install, `Want`, `BetweenVehicleSteps`, worker `Sync`, `Describe`, `Clear`, `BeforeRecycle` |
| [src/KSAGolf/Sim/CourseColliders.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/CourseColliders.cs) | Slab maths |
| [src/KSAGolf/Ksa/CourseBuilder.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseBuilder.cs#L391-L406) | `Publish` |
| [tests/KSAGolf.Tests/CourseCollidersTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/CourseCollidersTests.cs) | Slab tops are the felt; a bend's empty corner has nothing |

## Variations and gotchas

- A plain static does nothing: the clutter registration is the whole trick.
- Statics are bubble-relative floats; only use a bubble whose frame is `Ccf` (landed, body-fixed).
- Writing through the non-generic `IDictionary` indexer boxes each `StaticHandle` key every pass: a small steady
  allocation inside the worker step.
- Infinite mass means the static never displaces or breaks; a lower mass would make it behave like a rock.
- Reflection target: `BubbleClutterStatics._statics` and its value's constructor are private.
- Built against 2026.9.22.5482; `ConstraintSim.cs`, `BubbleClutterStatics.cs` and `NarrowPhaseCallbacks.cs` are unchanged in
  2026.10.10.5554.

## See also

- [Overview and comparison](overview.md)
- [Per-bubble statics with a contact-filter transpiler](add-static-colliders-to-physics-bubbles-1.md)
- Mod feature: [KSAGolf course colliders as clutter](../../../../mods/ksagolf/features/course-colliders-as-clutter.md)
- [Use ground clutter as colliders](../give-ground-clutter-custom-colliders/overview.md)

[^ksa-narrowphasecallbacks]: KSA.NarrowPhaseCallbacks (decompiled, build 2026.10.10.5554)
[^ksa-constraintsim]: KSA.ConstraintSim (decompiled, build 2026.10.10.5554)
[^ksa-bubbleclutterstatics]: KSA.BubbleClutterStatics (decompiled, build 2026.10.10.5554)
