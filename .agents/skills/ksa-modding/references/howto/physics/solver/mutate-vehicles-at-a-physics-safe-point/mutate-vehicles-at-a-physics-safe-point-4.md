---
type: How-To
title: Join JobSystems.VehicleSolver before mutating from a frame hook
description: Wait on the vehicle solver scheduler before destroying a vehicle, removing it or teleporting it from a StarMap hook, and queue part failures through the engine's own event instead of splitting directly
category: physics
group: solver
topic: mutate-vehicles-at-a-physics-safe-point
approach: 4
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/gatos/features/welds-vessel-anchoring
  - mods/ksarmory/features/safe-vehicle-destruction-and-part-failure
techniques: [reflection, kinematic-teleport]
hooks: [starmap StarMapAfterGui, reflection KSA.Vehicle._threadWorkerUpdateState]
lifecycle: [frame:draw-ui, frame:apply-solvers]
game_types:
  - KSA.JobSystems
  - KSA.Universe
  - KSA.Vehicle
  - KSA.VehicleUpdateState
  - KSA.PartFailureEvent
  - KSA.PhysicsBubble
  - KSA.ConstraintSim
tags: [physics, vehicle, job-system, wait, destroy, part-failure, barrier, teleport]
---

Outcome: structural edits (destroying a craft, removing a spent stage, teleporting a welded vessel) done from an
ordinary frame hook without the engine throwing "Collection was modified" or applying stale results to a
changed craft. Prerequisite: StarMap for the frame hook. KSArmory uses the barrier before kills and queues part
failures; gatOS uses it before per-frame weld teleports. This is the escape hatch for edits that cannot wait for
a dispatch window ([approaches 1 to 3](overview.md)); it stalls the main thread and it does not make ordinary
state writes stick.

## Background: how the game does it

`PrepareFrame` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141-L2236) calls `JobSystems.VehicleSolver.Wait()` and re-dispatches the
vehicle job at `ExecuteNextVehicleSolvers` before any StarMap hook runs, so every GUI and frame hook sees the
step as in flight. Inside it, `VehicleUpdateTask.Run` fans bubbles out over `JobSystems.VehicleWorkerPool`
inside a `ParallelBatch` [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L261-L298), which is joined before the scheduler goes
idle, so waiting on the scheduler alone is a sufficient barrier. `JobSystems.VehicleSolver` is the public static
scheduler [current/decomp/KSA/JobSystems.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/JobSystems.cs#L1-L60).

Three engine facts make a bare edit unsafe and explain what the barrier buys:

| Fact | Consequence |
|---|---|
| Destroying a vehicle removes its `VehicleUpdateState` from the bubble's `_vehicleStates`, the list the worker enumerates | Without the join the engine throws "Collection was modified" |
| `PhysicsBubble.RemoveVehicle` [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L242-L251) drops the state; the next apply writes results only to vehicles still in the list [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L881-L909) | A detached vehicle's stale result is never applied (this is why a teleport survives, see below) |
| Splitting a vehicle from a hook lands between the worker dispatch and the next apply | Results computed for the whole craft are applied to its fragments (KSArmory measured an `ArgumentOutOfRangeException` from `FlightComputer.UpdateTvcParams` every frame) |

## Lifecycle and timing

The hook runs at `frame:draw-ui` (`StarMapAfterGui`), with the vehicle step in flight. After
`JobSystems.VehicleSolver.Wait()` returns:

- the worker is idle, so bubble lists, the shapes registry and `Vehicle.Teleport` are legal to touch;
- the main-thread vehicle state is still the step applied at the start of the frame, so kinematic and
  flight-computer writes are **not** picked up: the step already snapshotted, and the next
  `ApplyVehicleSolvers` overwrites them;
- the exception is anything that detaches the vehicle from its bubble. `Vehicle.Teleport` calls
  `RemoveFromCurrentBubble`, so the finished step's result for that vehicle is dropped and the teleported
  state stands. The [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md#variations-and-gotchas) works
  through this exception;
- the wait blocks until the step finishes, so physics no longer overlaps the rest of the frame. Take it only
  on frames where something needs it.

Part failures are queued rather than applied, and land one frame later inside `Universe.ApplyVehicleSolvers`
(`VehicleUpdateTask.ApplyResultsToMainThread` calls `ApplyRenderEventsToVehicles`, right after the staged
results) [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L226-L259) [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L919-L930).

## Steps

1. Wrap the barrier once and never let it throw. If the join fails, falling through to the edit leaves the
   race in place, which is still better than taking the frame down:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L2615-L2627
    public static void WaitForVehicleSolvers()
    {
        try
        {
            JobSystems.VehicleSolver?.Wait();
        }
        catch (Exception e)
        {
            // Falling through to the destroy leaves the race in place; taking the frame down is
            // worse.
            Log.Warn($"could not join the vehicle solvers before a kill ({e.GetType().Name})");
        }
    }
```

2. Defer decisions and take the barrier once per batch. KSArmory collects kills during its step and applies
   them at the end of the round: whole-craft kills first (no part is isolated out of a corpse), then part
   failures, one `Wait()` for all of them:

```csharp
// src/KSArmory/Ksa/WeaponSystem.cs L4388-L4421
    private void ApplyPendingKills()
    {
        if (_pendingKills.Count == 0 && _pendingPartKills.Count == 0) return;

        // Join the engine's vehicle solvers before disposing anything. Destroying a vehicle removes
        // it from the list those worker jobs are enumerating, and this hook runs while they are
        // live - see KsaWorld.WaitForVehicleSolvers for the frame order that makes that unavoidable
        // without the barrier.
        //
        // Taken once for the whole batch rather than per kill, which is most of why kills are
        // deferred into this list in the first place.
        KsaWorld.WaitForVehicleSolvers();

        foreach (Vehicle v in _pendingKills)
        {
            if (!KsaWorld.IsAlive(v)) continue;
            Announce($"destroyed {KsaWorld.DisplayName(v)}");
            KsaWorld.Destroy(v, blastSeverity: 50f);
        }

        // Whole-craft kills first: a craft queued dead may also have parts queued from an earlier
        // burst in the same frame, and isolating a part out of a corpse is a split with nothing
        // to split. IsAlive filters those out here rather than the two lists having to agree.
        //
        // These are handed to the engine rather than applied, and land on the next frame.
        foreach ((Vehicle v, List<Part> parts) in _pendingPartKills)
        {
            if (KsaWorld.IsAlive(v)) KsaWorld.TryQueuePartFailure(v, parts);
        }

        // Any round still chasing a corpse loses its lock rather than flying at a dangling ref.
        _pendingKills.Clear();
        _pendingPartKills.Clear();
    }
```

3. Break up a craft with the engine's own machinery (`Universe.DestroyVehicleFromEvent` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1971-L2016)),
   so it explodes and sheds debris. To dispose of one with nothing left behind, use `Universe.DestroyVehicle`
   [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2018-L2040) under `ConstraintSim.UnlockShapesBlocking()`, because removal also mutates the
   shapes registry the worker held for its whole run:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L2644-L2660
    public static void Remove(Vehicle v)
    {
        if (!IsAlive(v)) return;
        try
        {
            WaitForVehicleSolvers();
            using ShapesUnlock shapes = ConstraintSim.UnlockShapesBlocking();

            Universe.DestroyVehicle(v, CrewDisposition.EndMission);
            InvalidateCensus();
        }
        catch (Exception e)
        {
            Log.Warn($"could not remove {DisplayName(v)} from the world: {e.Message}");
        }
    }
```

4. To break parts off a craft that survives, never split from the hook. Queue a `PartFailureEvent` on the
   vehicle's update state, the protocol `PartFailure.Detect` uses, and merge into one the engine already
   queued. Reach the state through the private `Vehicle._threadWorkerUpdateState`, probed once so a rename
   degrades to "destroy whole craft" instead of crashing:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L3361-L3399
    public static bool TryQueuePartFailure(Vehicle v, List<Part> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        if (!IsAlive(v) || parts.Count == 0 || !CanQueuePartFailures) return false;

        try
        {
            if (_updateStateField!.GetValue(v) is not VehicleUpdateState state)
            {
                // No update state means no physics bubble, so nothing is simulating it to break.
                Log.Debug(() => $"no update state on {DisplayName(v)}; its parts were not broken");
                return false;
            }

            // Before the engine splits it, so the pieces it names after this craft are known for
            // wreckage from the first frame they exist.
            Wreckage.Broke(v.Id);

            if (state.PartFailureEvent is { } pending)
            {
                for (int i = 0; i < parts.Count; i++)
                {
                    if (!pending.FailedParts.Contains(parts[i])) pending.FailedParts.Add(parts[i]);
                }

                return true;
            }

            InvalidateCensus();

            state.PartFailureEvent = new PartFailureEvent
            {
                FailedParts = parts,

                // Never set. The fragment guard was asked when the parts were queued, and a craft
                // it condemned was destroyed outright instead of reaching here.
                DestroyWholeVehicle = false,
            };
```

   Ask the engine whether the loss is fatal with `PartFailure.TrippedTheFragmentGuard(failed, total)`
   [current/decomp/KSA/PartFailure.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartFailure.cs#L77-L90) rather than copying its threshold, and destroy the craft whole when it
   trips.

5. For a per-frame teleport (a weld), wait, then teleport. gatOS waits only when at least one weld exists,
   so an unused feature costs nothing:

```csharp
// gatOS.GameMod/Game/Ksa/Welds/WeldManager.cs L106-L114
    public void Update(double dt)
    {
        if (_welds.Count == 0)
            return;

        // Cheap once the workers (queued in PrepareFrame) have finished, which they have by this point.
        JobSystems.VehicleSolver.Wait();

        List<WeldEntry>? toRemove = null;
```

   The comment's claim that the workers "have finished" by the GUI hook is not true in build 2026.10.10.5554;
   the wait is what makes the bubble detach legal (see
   [weld one vessel to another, approach 2](../../kinematics/weld-one-vessel-to-another/weld-one-vessel-to-another-2.md)).

6. In the log, expect nothing on success. A failed join logs `could not join the vehicle solvers before a kill`;
   a failed removal logs `could not remove <craft> from the world`; a moved private field logs
   `Vehicle._threadWorkerUpdateState has moved - part damage is off for this session`.

## Complete example

| Mod | Files |
|---|---|
| KSArmory | [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L2595-L2689), [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L3295-L3400), [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L4388-L4421) |
| gatOS | [gatOS.GameMod/Game/Ksa/Welds/WeldManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Welds/WeldManager.cs#L106-L144), [gatOS.GameMod/Game/Ksa/Welds/WeldEngine.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Welds/WeldEngine.cs#L39-L109) |

## Variations and gotchas

- Destroying leaves debris; removing does not. `DestroyVehicleFromEvent` ends in
  `PartFailure.ShedDebris(vehicle, 12)`, so one destroyed spent stage can leave a dozen vehicles behind.
- **Results staged for the craft must not land on a changed craft.** That is the one rule behind both the
  barrier and the queued part failure: destroying is safe because the destroyed vehicle's staged result has
  nowhere to land; splitting is not.
- A craft without an update state has no physics bubble, so nothing simulates it to break.
- Waiting does not make a velocity or flight-computer write stick; use the windows in
  [approaches 1 to 3](overview.md) for those.
- `JobSystems.WaitAll` joins workers but applies nothing; a write after a bare `WaitAll` is still overwritten
  at the next apply.
- Recognise debris by KSA's naming (`Vehicle.GenerateSplitId` gives `{parentId}_{n}`) if you need to ignore
  fragments; that breaks if the engine changes its naming.
- `JobSystems.VehicleSolver.Wait()` joins only the vehicle scheduler. The `NearestOrbitPointJob` that `PrepareFrame` queued earlier
  (on `JobSystems.NearestOrbitAndPerformanceWorker`) and the orbit and cloth solvers can still be running, and `Vehicle.Teleport`
  disposes cached orbit points that job reads, so a teleport of the controlled vessel from this window can race it (inferred from
  the decomp, not observed). The handoff approach joins that worker first with `JobSystems.NearestOrbitAndPerformanceWorker.Wait()`
  ([approach 3](mutate-vehicles-at-a-physics-safe-point-3.md)).
- The `Wait()` stall can be expensive on heavy scenes. Batch (as KSArmory does), and do nothing on frames
  with no pending work.

## See also

- [Overview and comparison](overview.md)
- [Approach 1](mutate-vehicles-at-a-physics-safe-point-1.md), [approach 2](mutate-vehicles-at-a-physics-safe-point-2.md), [approach 3](mutate-vehicles-at-a-physics-safe-point-3.md)
- Mod features: [gatOS welds](../../../../mods/gatos/features/welds-vessel-anchoring.md), [KSArmory safe vehicle destruction and part failure](../../../../mods/ksarmory/features/safe-vehicle-destruction-and-part-failure.md)
- [Create and free shapes in KSA's global Bepu shape registry](../../colliders/mutate-the-shared-bepu-shape-registry/overview.md) for `UnlockShapesBlocking`
- [Lifecycle reference: settled-window write paths](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md)
