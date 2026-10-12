---
type: How-To
title: Volatile snapshot handoff from worker patches to the main thread
description: Publish an immutable snapshot from the main thread through a volatile field, read it once per call in prefixes on ConstraintSim passes and KittenLocomotion.StepGrounded, and keep all mutable state per simulation
category: physics
group: solver
topic: write-worker-thread-safe-solver-patches
approach: 4
difficulty: advanced
prerequisites: [harmony]
demonstrated_by:
  - mods/ksagolf/features/course-colliders-as-clutter
  - mods/ksastructures/features/first-person-kitten-view
techniques: [worker-thread-patch, harmony-prefix, harmony-postfix, reflection]
hooks:
  - harmony-prefix KSA.ConstraintSim.DetectCollisions
  - harmony-prefix KSA.ConstraintSim.Simulate
  - harmony-prefix KSA.ConstraintSim.TryResetForPool
  - harmony-prefix KSA.KittenLocomotion.StepGrounded
  - harmony-prefix KSA.Universe.ExecuteNextVehicleSolvers
lifecycle: [physics:vehicle-solver, physics:bepu, frame:dispatch-solvers, frame:draw-ui]
game_types:
  - KSA.ConstraintSim
  - KSA.KittenLocomotion
  - KSA.PhysicsBubble
  - KSA.Universe
  - KSA.VehicleUpdateTask
  - KSA.BubbleClutterStatics
tags: [physics, worker-thread, volatile, snapshot, bepu, kitten, locomotion, conditionalweaktable]
---

Outcome: a mod hands the worker-thread solver a changing input (a set of boxes, a look direction) without any
lock, and the worker patch does its work using only that snapshot plus state it owns. Prerequisite: Harmony.
KSAGolf adds static Bepu boxes to each physics bubble's simulation from prefixes on the collision passes;
KSAStructures steers a first-person kitten by replacing one field of the locomotion inputs.

## Background: how the game does it

`VehicleUpdateTask.Run` steps bubbles in contact islands through `JobSystems.VehicleWorkerPool`
[current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L261-L298); `ContactIsland.Execute` runs `RunStep` per bubble on a pool worker,
which reaches `ConstraintSim.DetectCollisions` and `ConstraintSim.Simulate` [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L870-L904).
Different bubbles run in parallel, but each `ConstraintSim` belongs to one bubble, so a prefix call owns the
simulation it mutates. Kitten grounded movement is computed on the worker by `KittenLocomotion.StepGrounded`,
called from `PhysicsBubble` through `KittenLocomotion.Advance` [current/decomp/KSA/KittenLocomotion.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenLocomotion.cs).

The shared Bepu shape registry is the one shared mutable object: it can be written only between vehicle steps
([approach 1 of the shapes topic](../../colliders/mutate-the-shared-bepu-shape-registry/overview.md)). So shapes
are made on the main thread and only handles and poses are created on the worker.

## Lifecycle and timing

| Hook | Runs in | Thread | Safe to touch |
|---|---|---|---|
| `Want(...)` from the builder (`[StarMapAfterGui]`) | `frame:draw-ui` | main | Only the mod's volatile snapshot |
| Prefix `Universe.ExecuteNextVehicleSolvers` | `frame:dispatch-solvers` | main | The shapes registry under `ConstraintSim.UnlockShapes()`; nothing is stepping |
| Prefix `ConstraintSim.DetectCollisions` | `PhysicsBubble.FullPhysicsStepUntil`, `physics:vehicle-solver` | worker | That bubble's own `Simulation.Statics` and clutter registry |
| Prefix `ConstraintSim.Simulate` | `FullPhysicsConstrainedStep`, `physics:bepu` | worker | Same |
| Prefix `ConstraintSim.TryResetForPool` | `ConstraintSimPool.Return` | worker or main | The mod's own per-simulation table only |
| Prefix and postfix `KittenLocomotion.StepGrounded` | `physics:vehicle-solver` | worker | The kitten's own control inputs and result |

The contract between the sides:

| Direction | Mechanism |
|---|---|
| Main to worker | An immutable record in a `volatile` field, replaced whole, read once per call into a local |
| Worker private state | Keyed per `ConstraintSim` in a `ConditionalWeakTable`; one prefix call owns its simulation |
| Worker to engine | Written into the engine's own structures for that step (statics, the locomotion result); nothing is sent back to the main thread |
| Failure | Every worker prefix catches everything; a throw would mark the bubble failed |

## Steps

1. Declare the snapshot as records and the volatile fields. `Wanted` is what the main thread asks for, `Ready` is
   what the main thread built at the sync point (shapes made), and `SimState` is per-simulation worker state:

```csharp
// src/KSAGolf/Ksa/CourseCollider.cs L36-L56
    private sealed record Box(double3 CentreCcf, Quaternion Orientation, TypedIndex Shape);

    private sealed record Wanted(Celestial Body, (double3 Centre, doubleQuat Orientation, double3 Size)[] Boxes, int Version);

    private sealed record Ready(Celestial Body, Box[] Boxes, int Version);

    private sealed class SimState
    {
        public StaticHandle[] Handles = [];
        public int Version = -1;
        public double3 Bub;
    }

    private static readonly ConditionalWeakTable<ConstraintSim, SimState> States = new();
    private static readonly Dictionary<(int, int, int), TypedIndex> ShapesBySize = [];

    private static Harmony? _harmony;
    private static volatile Wanted? _wanted;
    private static volatile Ready? _ready;
    private static int _version;
    private static bool _complained;
```

2. Publish from the main thread by swapping the reference and bumping a version. The worker compares versions to
   know when to rebuild:

```csharp
// src/KSAGolf/Ksa/CourseCollider.cs L112-L119
    /// <summary>The course's slabs as boxes on a body, from the main thread; null takes them away.</summary>
    public static void Want(Celestial? body, IEnumerable<(double3 Centre, doubleQuat Orientation, double3 Size)> boxes)
    {
        _version++;
        (double3, doubleQuat, double3)[] list = [.. boxes];
        _wanted = body is null || list.Length == 0 ? null : new Wanted(body, list, _version);
        if (_wanted is null) _ready = new Ready(null!, [], _version);
    }
```

3. Turn `Wanted` into `Ready` at the sync point (the `ExecuteNextVehicleSolvers` prefix), where creating shapes
   is legal, and swap `_ready` in one assignment. The worker never sees a half-built `Ready`:

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

4. On the worker, read the volatile once into a local, keep per-simulation state in the weak table, and wrap the
   call so it can never throw. Patch both collision passes with the same prefix; the sync is idempotent, so
   running it twice per step is harmless:

```csharp
// src/KSAGolf/Ksa/CourseCollider.cs L157-L168
    // A worker thread, before a bubble's collision pass. Nothing here may throw.
    private static void BeforeCollisions(ConstraintSim __instance)
    {
        try
        {
            Sync(__instance);
        }
        catch (Exception e)
        {
            Complain($"the course's ground could not be synced: {e.GetBaseException().Message}");
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

5. Drop per-simulation state when the engine recycles the simulation. The engine clears a pooled simulation's
   statics itself, so the table entry must go with them:

```csharp
// src/KSAGolf/Ksa/CourseCollider.cs L240-L241
    // The engine clears a recycled simulation's statics itself; what was held for it here goes with them.
    private static void BeforeRecycle(ConstraintSim __instance) => States.Remove(__instance);
```

6. For a value the locomotion needs every step, use a tiny volatile array so three floats publish atomically.
   KSAStructures converts the main-thread heading once and stores the reference:

```csharp
// src/KSAStructures/Ksa/FirstPersonHook.cs L73-L76
    public static double3? Heading
    {
        set => _heading = value is { } h && Vec.IsFinite(h) ? [(float)h.X, (float)h.Y, (float)h.Z] : null;
    }
```

   The prefix replaces one field of the kitten's own copy of the inputs, and the postfix pins the facing to the
   look. Only the flown kitten's inputs carry a camera, so other kittens are left alone, and both methods keep
   a bare `catch` because the camera's own forward is the only failure allowed:

```csharp
// src/KSAStructures/Ksa/FirstPersonHook.cs L140-L175
    // The inputs are the kitten's own copy, written from the camera every step, so replacing the forward
    // changes nothing but this step's walk.
    private static void BeforeStepGrounded(ref CharacterControlInputs inputs)
    {
        try
        {
            if (_heading is { Length: 3 } h) inputs.CameraForwardCce = new float3(h[0], h[1], h[2]);
        }
        catch
        {
            // The camera's own forward is the only failure allowed.
        }
    }

    // Only the flown kitten's inputs carry a camera, so the rest are left alone.
    private static void AfterStepGrounded(ref LocomotionCommand __result, in CharacterControlInputs inputs,
                                          in LocomotionFacts facts, ref LocomotionState state)
    {
        try
        {
            if (_heading is null || inputs.CameraForwardCce.LengthSquared() < 1e-6f) return;

            float3 forward = float3.Transform(inputs.CameraForwardCce, facts.Cce2Phys);
            float3 up = facts.UpDirPhys;
            float3 flat = forward - (up * float3.Dot(forward, up));
            if (flat.LengthSquared() < 0.0025f) return;

            flat = flat.Normalized();
            __result.FacingDirPhys = flat;
            state.FacingDirPhys = flat;
        }
        catch
        {
            // A facing left as KSA chose it is the only failure allowed.
        }
    }
```

7. If a worker patch must send a result to the main thread, apply the same rule in reverse (adapted guidance,
   not mod code): build an immutable record on the worker, publish it by assigning a `volatile` field, and let
   the main thread read it once per frame into a local. Neither mod needs this, so there is no code to quote.

## Complete example

| Mod | Files |
|---|---|
| KSAGolf | [src/KSAGolf/Ksa/CourseCollider.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseCollider.cs#L63-L241), [src/KSAGolf/Sim/CourseColliders.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/CourseColliders.cs) |
| KSAStructures | [src/KSAStructures/Ksa/FirstPersonHook.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPersonHook.cs#L46-L175), [src/KSAStructures/Ksa/FirstPerson.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPerson.cs#L32-L139) |

## Variations and gotchas

- Statics are bubble-relative `float` poses: re-apply descriptions when the bubble origin moves, and only use a
  bubble whose frame is `Ccf`. The registration as clutter does not stick (the engine's own `Clear` can wipe
  it), so it is repeated on every pass.
- Writing through the non-generic `IDictionary` indexer boxes each `StaticHandle` key on every pass, a small
  steady allocation inside the worker step. Know the cost before copying the pattern.
- Without the `TryResetForPool` prefix the mod would believe a pooled simulation still holds its handles.
- Box shapes are cached per size and never freed from the shared registry; fine for a handful of sizes.
- `volatile` gives visibility of the reference, not of the data behind it: never mutate a record after
  publishing it.
- The `BubbleClutterStatics._statics` dictionary and its value type's constructor are private; if they change,
  `Install` logs "kittens walk through courses" and patches nothing.
- A worker prefix on a method called per kitten must filter early: `StepGrounded` runs for every grounded
  kitten, and only the flown one has a camera in its inputs.

## See also

- [Overview and comparison](overview.md)
- [Approach 3: concurrent registries](write-worker-thread-safe-solver-patches-3.md), [approach 6: immutable commands](write-worker-thread-safe-solver-patches-6.md)
- Mod features: [KSAGolf course colliders as clutter](../../../../mods/ksagolf/features/course-colliders-as-clutter.md), [KSAStructures first-person kitten view](../../../../mods/ksastructures/features/first-person-kitten-view.md)
- [Add mod-owned static colliders to physics bubbles](../../colliders/add-static-colliders-to-physics-bubbles/overview.md) and [use ground clutter as colliders](../../colliders/give-ground-clutter-custom-colliders/overview.md)
