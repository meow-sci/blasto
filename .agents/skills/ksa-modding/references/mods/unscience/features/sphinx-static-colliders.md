---
type: Mod Feature
title: Automatic box or mesh colliders for imported statics with scoped Bepu hooks
description: Sphinx gives each placed GLB a global Bepu Box or two-sided Mesh chosen by conservative closed-box detection, adds per-bubble static handles from a ConstraintSim.BeginStaticObjectPass postfix, and admits them to contact generation and ground-contact logic only for its own handles
mod: unscience
submod: sphinx
category: physics
techniques: [bepu-physics, harmony-postfix, harmony-prefix, harmony-transpiler, reflection]
hooks:
  - harmony-postfix KSA.ConstraintSim.BeginStaticObjectPass
  - harmony-postfix KSA.ConstraintSim.UpdateSimForSnappedOrigin
  - harmony-postfix KSA.ConstraintSim.IsGroundSurfaceFor
  - harmony-prefix KSA.ConstraintSim.TryResetForPool
  - harmony-prefix KSA.ConstraintSim.Dispose
  - harmony-transpiler KSA.NarrowPhaseCallbacks.AllowContactGeneration (internal type looked up by name)
  - "starmap StarMapBeforeGui (via unscience ISubmod.Update: collider build and retirement after solver waits)"
lifecycle: [physics:bepu, physics:vehicle-solver, frame:draw-ui]
game_types:
  - KSA.ConstraintSim
  - KSA.NarrowPhaseCallbacks
  - KSA.BepuHandles
  - KSA.ShapesUnlock
  - KSA.PhysicsBubble
  - KSA.VehicleUpdateState
  - KSA.BubbleOrigin
  - KSA.BubbleFrame
  - KSA.ConstraintSimPool
  - KSA.JobSystems
  - BepuPhysics.Collidables.Box
  - BepuPhysics.Collidables.Mesh
  - BepuPhysics.Statics
  - BepuPhysics.StaticDescription
tags: [bepu, colliders, statics, mesh-collider, contact-filter, physics-bubble, transpiler, glb]
---

Placed [Sphinx statics](./sphinx-glb-statics-placement.md) are solid by default: vessels can land on a
roof and EVA kittens can walk through an imported doorway. In Auto mode a model that is exactly a closed
axis-aligned box gets a cheap fitted `Box`; anything else keeps its triangles as a two-sided Bepu `Mesh`,
so openings stay open. The player can force Mesh, Fitted box or Off, and each entry reports the shape
it actually got.[^sphinx-readme]

Mechanism in one line: each entry owns one shape in KSA's global Bepu shape registry, built on the main
thread after waiting for the solvers; a postfix on `ConstraintSim.BeginStaticObjectPass` (and on
`UpdateSimForSnappedOrigin`) adds, moves or removes a `StaticHandle` for that shape in every surface
physics bubble near it; and a transpiler on the internal `NarrowPhaseCallbacks.AllowContactGeneration`
plus a postfix on `ConstraintSim.IsGroundSurfaceFor` treat only those handles, in that exact simulation,
as ground.

## How it works

**Shape choice** ([sphinx.lib/CollisionGeometry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/CollisionGeometry.cs#L15-L56)).
`IsBox` classifies every vertex as one of the eight bounds corners within a relative tolerance of
1e-5 of the size; any vertex that is not a corner, any degenerate triangle, or any triangle not lying on
one face plane fails. Each face must then hold exactly two distinct triangles (keyed by their corner
bitmask, so a duplicated reversed backface collapses into the same key) that share the face diagonal, not
an edge. A missing face, an interior vertex or overlapping face triangles keep mesh collision.[^collision-geometry]

| Mode | Shape | Limits and fallbacks |
|---|---|---|
| Auto | Fitted `Box` when `IsBox`, else two-sided `Mesh` | Above 100,000 source triangles: fitted box plus a visible warning |
| Mesh | Two-sided `Mesh` | Above 100,000 triangles: the edit is rejected |
| Box | Fitted `Box` from scaled bounds | Fills every opening and encloses disconnected parts |
| Off | None | Decoration only |

All modes: 20 km scaled diagonal and 100 km local-centre offset per entry, and a 500,000-triangle budget
across all entries. Degenerate triangles are skipped; a mesh with none left is rejected. Texture alpha
never cuts holes.[^static-collider][^sphinx-readme]

**Shape build** ([sphinx.lib/StaticCollider.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/StaticCollider.cs#L23-L85)).
The collider is baked from the imported host mesh (`ClutterAssets.ResolveMesh(...).HostPrimitives`, the
same data the renderer uses) with the same `PlacementMath.GroundedLocal` matrix as the visible model,
relative to the transformed bounds centre so coordinates stay small. A box uses the scaled bounds size,
that centre and the rotation from `Matrix4x4.Decompose`. A mesh writes every triangle twice, once per
winding, into a buffer from the registry's `BufferPool`, because Bepu mesh triangles are one-sided. The
shape is added to `ShapesUnlock.Shapes` inside `using var unlock = ConstraintSim.UnlockShapes()`, and
freed with `RemoveAndDispose` on retirement. The managed tests check 200 random XYZ transforms for
collider and render agreement.[^static-collider][^collision-checks]

**Per-bubble handles** ([sphinx.lib/SphinxPhysics.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxPhysics.cs)).
A `ConcurrentDictionary<ConstraintSim, Bubble>` maps each simulation to its entry-to-handle map and
handle set. `Sync(sim, entries)`:

1. Reads the bubble origin from any vessel in `HandleToState`; a bubble whose frame is not
   `BubbleFrame.Ccf` (body-fixed) is cleared.[^ksa-bubble-origin]
2. For each visible entry with a collider on the bubble's parent body, computes the pose in CCF
   (anchor plus collider centre rotated by `GroundPlacement.FrameCcf`), subtracts the double-precision
   `origin.PositionBub` before converting to Bepu floats, and keeps it only if some bubble vessel is within
   collider radius + 2 km.
3. Adds a static with `Simulation.Statics.Add`, or `ApplyDescription` when pose or shape changed, and
   removes handles for entries no longer wanted. Removal uses Bepu's default awakening, so bodies resting
   on a removed static wake.[^sphinx-physics]

**Hooks** ([sphinx.lib/SphinxPhysicsPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxPhysicsPatches.cs)),
installed from `SphinxPatches.Apply` together with the render hooks:[^sphinx-patches]

| Target | Kind | Purpose |
|---|---|---|
| `ConstraintSim.BeginStaticObjectPass()` | postfix | `Sync` before each substep's collision work |
| `ConstraintSim.UpdateSimForSnappedOrigin(VehicleUpdateState)` | postfix | `Sync` after a bubble origin snap moved everything |
| `ConstraintSim.IsGroundSurfaceFor(VehicleUpdateState, StaticHandle)` | postfix | `__result |= SphinxPhysics.Owns(sim, handle)`: ground friction, terrain-contact bookkeeping and EVA walking surfaces |
| `ConstraintSim.TryResetForPool()` / `Dispose()` | prefix | Drop the bubble's handles before KSA clears or reuses the simulation |
| `NarrowPhaseCallbacks.AllowContactGeneration(int, CollidableReference, CollidableReference, ref float)` | transpiler | Replace the single `BepuHandles.IsGroundSurface` call with `IsSurface(ref handles, handle, this.Sim)` |

Why the filter transpiler is required: stock `AllowContactGeneration` lets a vehicle collide with a
static only if it is a clutter static, a terrain block, or `BepuHandles.IsGroundSurface` (the vehicle's
own terrain patch or `StaticObjectCollider`); any other static never generates contacts.[^ksa-narrow-phase][^ksa-bepu-handles]
The transpiler loads the callback struct's own `Sim` field, so the check is scoped to the simulation
running the callback without thread-local state or per-contact reflection, and it throws unless exactly
one call site was replaced.[^sphinx-physics-patches]

**Edits and retirement** ([sphinx.lib/SphinxSubmod.Editing.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxSubmod.Editing.cs#L92-L113)).
A transform or collider-mode edit builds the replacement first (inside the 500,000-triangle budget
minus this entry), then detaches the entry from every bubble and disposes the old shape, then assigns.
Hide, remove, clear, body loss and unload all call `SphinxPhysics.Detach` before freeing the shape.
Texture edits never touch physics.[^sphinx-editing]

## Lifecycle and timing

| Work | Thread and moment | Lifecycle |
|---|---|---|
| Build, replace, detach, dispose shapes | Main thread in `ISubmod.Update` after `JobSystems.VehicleSolver.Wait()` and `ClothSolvers.Wait()` | `frame:draw-ui` |
| `Sync` (add, move, remove handles) | Bubble worker, `BeginStaticObjectPass` at the start of each substep (`PhysicsBubble.cs` L2220) and in `PopulateBepuStatesFromKinematic` (L2928); after origin snaps (L2534) | `physics:vehicle-solver`, `physics:bepu` |
| Contact filter, ground checks | Bepu narrow-phase workers | `physics:bepu` |
| Handle cleanup | `ConstraintSimPool.Return` and `VehicleUpdateTask.ReturnBubble` call `TryResetForPool`; `Dispose` when not poolable | `physics:vehicle-solver` |

Main-thread waits are mandatory because KSA locks the global shape registry for the whole vehicle
update (`ConstraintSim.BeginVehicleUpdate` sets the state to -1, and `UnlockShapes` throws "cannot be
mutated while the vehicle update is stepping").[^ksa-constraint-sim] Each `ConstraintSim` is stepped by
one worker at a time, so a bubble's map is only mutated by its own worker, while main-thread `Detach`
runs only after the waits. Narrow-phase callbacks only read the handle sets.[^scope-statics]

`Sync` runs at the start of every substep, which keeps poses correct across origin snaps and vessels
moving in and out of the 2 km range without a separate timer. It is cheap when nothing changed (pose and
shape are compared before `ApplyDescription`).

## Key code

| Path | Role |
|---|---|
| [sphinx.lib/CollisionGeometry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/CollisionGeometry.cs) | Closed-box detection, triangle transform, 100,000 limit |
| [sphinx.lib/StaticCollider.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/StaticCollider.cs) | Global Box/Mesh shape build and disposal |
| [sphinx.lib/SphinxPhysics.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxPhysics.cs) | Per-simulation handle maps, `Sync`, `Owns`, `Detach`, `Clear` |
| [sphinx.lib/SphinxPhysicsPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxPhysicsPatches.cs) | Five method patches and the contact-filter transpiler |
| [sphinx.lib/SphinxSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxSubmod.cs#L107-L125) | `WaitForPhysics`, `SyncCollision`, `BuildCollider` budget |
| [sphinx.tests/CollisionGeometryChecks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.tests/CollisionGeometryChecks.cs) | Box detection and collider/render alignment checks |
| [scope/statics.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/statics.md) | Authors' physics contract and per-build verification |

## Snippets

Conservative closed-box test:

```csharp
// sphinx.lib/CollisionGeometry.cs L15-L56
    public static bool IsBox(IReadOnlyList<CollisionTriangle> triangles, Vector3 min, Vector3 max)
    {
        var size = max - min;
        if (size.X <= 0 || size.Y <= 0 || size.Z <= 0) return false;
        var tolerance = Vector3.Max(size * .00001f, new Vector3(.000001f));
        var faces = Enumerable.Range(0, 6).Select(_ => new HashSet<int>()).ToArray();
        foreach (var triangle in triangles)
        {
            int a = Corner(triangle.A), b = Corner(triangle.B), c = Corner(triangle.C);
            if (a < 0 || b < 0 || c < 0 || a == b || b == c || a == c) return false;
            int face = -1;
            for (int axis = 0; axis < 3; axis++)
                if ((a & (1 << axis)) == (b & (1 << axis)) && (a & (1 << axis)) == (c & (1 << axis)))
                    face = axis * 2 + ((a >> axis) & 1);
            if (face < 0) return false;
            faces[face].Add((1 << a) | (1 << b) | (1 << c));
            if (faces[face].Count > 2) return false;
        }
        foreach (var face in faces)
        {
            if (face.Count != 2) return false;
            var pair = face.ToArray();
            int shared = pair[0] & pair[1];
            var corners = Enumerable.Range(0, 8).Where(i => (shared & (1 << i)) != 0).ToArray();
            // Two triangles must share the face diagonal, not an edge (which could overlap).
            if (corners.Length != 2 || BitOperations.PopCount((uint)(corners[0] ^ corners[1])) != 2) return false;
        }
        return true;
        // ...
    }
```

Two-sided mesh into the global registry:

```csharp
// sphinx.lib/StaticCollider.cs L60-L79
        var transformed = CollisionGeometry.Transform(triangles, matrix, center);
        if (transformed.Length > remainingTriangles)
            throw new ArgumentException("Sphinx has reached 500,000 collision triangles. Remove statics or use Fitted box.");
        // Bepu triangles are one-sided. Both windings make imported surfaces solid from either side.
        unlock.BufferPool.Take<Triangle>(checked(transformed.Length * 2), out var buffer);
        Mesh native = default;
        bool ownsMesh = false;
        try
        {
            for (int i = 0; i < transformed.Length; i++)
            {
                var t = transformed[i];
                buffer[i*2] = new Triangle(t.A, t.B, t.C);
                buffer[i*2+1] = new Triangle(t.A, t.C, t.B);
            }
            native = new Mesh(buffer, Vector3.One, unlock.BufferPool); ownsMesh = true;
            var handle = unlock.Shapes.Add(in native);
            return new StaticCollider { Shape = handle, Center = center, Radius = size.Length() * .5f,
                TriangleCount = transformed.Length, Description = $"Mesh: {transformed.Length:N0} triangles (two-sided)" };
        }
```

Per-bubble handle sync (runs on the bubble's worker):

```csharp
// sphinx.lib/SphinxPhysics.cs L24-L64
    public static void Sync(ConstraintSim sim, IReadOnlyList<SphinxEntry> entries)
    {
        if (entries.Count == 0) { Clear(sim); return; }
        var state = sim.HandleToState.Values.FirstOrDefault();
        if (state == null || state.Origin.BubFrame != BubbleFrame.Ccf) { Clear(sim); return; }
        var origin = state.Origin;
        var bubble = Bubbles.GetOrAdd(sim, _ => new Bubble());
        var wanted = new HashSet<SphinxEntry>();
        foreach (var entry in entries)
        {
            if (!entry.Visible || entry.Collider is not { } collider || !ReferenceEquals(entry.Anchor.Body, origin.Parent)) continue;
            var frame = GroundPlacement.FrameCcf(entry.Anchor, entry.Align);
            var center = collider.Center;
            var localCenter = new double3(center.X, center.Y, center.Z);
            var orientation = doubleQuat.CreateFromRotationMatrix(frame);
            var position = entry.Anchor.PositionCcf + localCenter.Transform(orientation);
            // Include actual collider reach; a large building's anchor can be far from its nearest wall.
            if (!sim.HandleToState.Values.Any(v =>
                (position - origin.PositionBub - v.GetReadOnlyStates().Kinematic.PositionPhys).Length() <= collider.Radius + 2000)) continue;
            wanted.Add(entry);
            // ...
            if (bubble.Entries.TryGetValue(entry, out var handle))
            {
                var current = sim.Simulation.Statics[handle];
                if (current.Pose.Position != description.Pose.Position || current.Pose.Orientation != description.Pose.Orientation || current.Shape != description.Shape)
                    sim.Simulation.Statics.ApplyDescription(handle, in description);
            }
            else
            {
                handle = sim.Simulation.Statics.Add(in description);
                bubble.Entries.Add(entry, handle); bubble.Handles.Add(handle);
            }
        }
        foreach (var entry in bubble.Entries.Keys.Where(e => !wanted.Contains(e)).ToArray()) Remove(sim, bubble, entry);
    }
```

The scoped ground filter: one call site replaced, the callback's own simulation passed in:

```csharp
// sphinx.lib/SphinxPhysicsPatches.cs L51-L71
    private static IEnumerable<CodeInstruction> ContactFilter(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
    {
        var original = AccessTools.Method(typeof(BepuHandles), nameof(BepuHandles.IsGroundSurface));
        var replacement = AccessTools.Method(typeof(SphinxPhysicsPatches), nameof(IsSurface));
        var sim = AccessTools.Field(__originalMethod.DeclaringType, "Sim")
            ?? throw new MissingFieldException("KSA.NarrowPhaseCallbacks", "Sim");
        int replaced = 0;
        foreach (var instruction in input)
        {
            if (!instruction.Calls(original)) { yield return instruction; continue; }
            var load = new CodeInstruction(OpCodes.Ldarg_0);
            load.MoveLabelsFrom(instruction); load.MoveBlocksFrom(instruction);
            yield return load;
            yield return new CodeInstruction(OpCodes.Ldfld, sim);
            yield return new CodeInstruction(OpCodes.Call, replacement);
            replaced++;
        }
        if (replaced != 1) throw new InvalidOperationException("Sphinx expected one stock ground filter call; collision hooks need a game-update review.");
    }
    private static bool IsSurface(ref BepuHandles handles, StaticHandle handle, ConstraintSim sim)
        => handles.IsGroundSurface(handle) || SphinxPhysics.Owns(sim, handle);
```

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `NarrowPhaseCallbacks.AllowContactGeneration` (internal struct, field `Sim`) | [current/decomp/KSA/NarrowPhaseCallbacks.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/NarrowPhaseCallbacks.cs#L24-L57) | Contact filter; one `IsGroundSurface` call at L56 |
| `BepuHandles.IsGroundSurface(StaticHandle)` | [current/decomp/KSA/BepuHandles.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BepuHandles.cs#L118-L121) | Stock rule: own terrain patch or `StaticObjectCollider` |
| `ConstraintSim.IsGroundSurfaceFor` | [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L168-L185) | Postfix target; callers at L1186, L1227 and `NarrowPhaseCallbacks` L150 (kitten walking surfaces) |
| `ConstraintSim.UnlockShapes`, `BeginVehicleUpdate` | [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L111-L133) | Global shape registry lock |
| `ConstraintSim.TryResetForPool`, `Dispose` | [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L193-L236) | Statics cleared and simulation reused or nulled |
| `ConstraintSim.UpdateSimForSnappedOrigin`, `BeginStaticObjectPass`, `HandleToState`, `Simulation` | [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L309-L548) | Sync points and per-bubble state |
| `PhysicsBubble` substep loop and origin snap | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L2210-L2222) | Where `BeginStaticObjectPass` is called |
| `BubbleOrigin.{Parent, BubFrame, PositionBub}` | [current/decomp/KSA/BubbleOrigin.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BubbleOrigin.cs#L23-L27) | Body match, CCF frame, float-precision rebasing |
| `ConstraintSimPool.Return` | [current/decomp/KSA/ConstraintSimPool.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSimPool.cs#L23-L28) | Pool reset or dispose |
| `VehicleUpdateTask.ReturnBubble` | [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L188-L194) | Bubble pooling |

## Pitfalls and version notes

- `AllowContactGeneration` and `IsGroundSurfaceFor` are `[MethodImpl(AggressiveInlining)]` members, the
  first on a value-type Bepu callback. A Harmony patch only changes call sites that the JIT did not
  inline, so the authors keep live collision acceptance open; nothing here was run in game.[^scope-statics]
- The transpiler's one-call invariant is the update tripwire. If a future build adds or removes an
  `IsGroundSurface` call, `Apply` throws, `SphinxPatches.Apply` removes everything and `Ready` stays
  false, so placement is refused instead of silently non-solid.[^sphinx-patches]
- Removal is by patch method, not by Harmony id: `SphinxPhysicsPatches.Remove` walks
  `Harmony.GetAllPatchedMethods()` and unpatches only methods declared on its own class, so it never
  strips other features' patches from the supermod's shared Harmony instance.[^sphinx-physics-patches]
- Static handles are only created in CCF bubbles on the anchor's body. Orbital (CCI) bubbles never see
  the statics, and on-rails or time-warp behavior follows the game's surface physics.[^sphinx-readme]
- Moving or removing a static under a landed vessel that is asleep or on rails does not wake it; the
  authors note the game's `WakeVehiclesSupportedBy` only handles a departing vessel.[^scope-statics]
- Collider edits rebuild a private shape and can hitch for large meshes; the 100,000 per-model triangle
  limit is independent of the two-million-vertex render limit. Simplify walkable interiors.
- Since build 5482 the stock filter has early returns for clutter statics and clutter bodies, so loose
  ground clutter also collides with Sphinx statics (authors' note).[^scope-statics]

## See also

- [Body-fixed GLB statics placement](./sphinx-glb-statics-placement.md)
- [Pebbles clutter colliders](./pebbles-clutter-colliders.md) (the other mod-owned static colliders)
- [Collider-only scaling through native owned shapes](./godzilla-collider-scaling.md)
- [Per-vessel collision suppression by bracketing ConstraintSim passes](./garrys-torch-scoped-collision-suppression.md)
- [Sphinx sub-mod](../submods/sphinx.md) and [mod overview](../overview.md)
- [Bracket the non-inlined methods that enclose an inlined struct callback](../../../howto/patching/internals/patch-around-aggressive-inlining/patch-around-aggressive-inlining-4.md) - how-to article demonstrating this feature
- [Unpatch by patch method on a shared Harmony instance](../../../howto/patching/safety/remove-your-patches-safely-at-runtime-and-at-exit/remove-your-patches-safely-at-runtime-and-at-exit-3.md) - how-to article demonstrating this feature
- [Per-bubble static handles from a BeginStaticObjectPass postfix](../../../howto/physics/colliders/add-static-colliders-to-physics-bubbles/add-static-colliders-to-physics-bubbles-1.md) - how-to article demonstrating this feature
- [Add and free statics' shapes only while the solver is idle](../../../howto/physics/colliders/mutate-the-shared-bepu-shape-registry/mutate-the-shared-bepu-shape-registry-2.md) - how-to article demonstrating this feature

[^sphinx-readme]: sphinx/README.md
[^collision-geometry]: sphinx.lib/CollisionGeometry.cs
[^static-collider]: sphinx.lib/StaticCollider.cs
[^collision-checks]: sphinx.tests/CollisionGeometryChecks.cs
[^ksa-bubble-origin]: KSA.BubbleOrigin (decompiled, build 2026.10.10.5554)
[^sphinx-physics]: sphinx.lib/SphinxPhysics.cs
[^sphinx-patches]: sphinx.lib/SphinxPatches.cs
[^ksa-narrow-phase]: KSA.NarrowPhaseCallbacks (decompiled, build 2026.10.10.5554)
[^ksa-bepu-handles]: KSA.BepuHandles (decompiled, build 2026.10.10.5554)
[^sphinx-physics-patches]: sphinx.lib/SphinxPhysicsPatches.cs
[^sphinx-editing]: sphinx.lib/SphinxSubmod.Editing.cs
[^ksa-constraint-sim]: KSA.ConstraintSim (decompiled, build 2026.10.10.5554)
[^scope-statics]: scope/statics.md
