---
type: Mod Feature
title: Destroy vehicles and break off parts safely from a frame hook
description: Kills are batched behind one JobSystems.VehicleSolver.Wait barrier, part losses are queued into the vehicle's private update state as a PartFailureEvent that the engine applies with its own crash damage, disposal uses DestroyVehicle under a shapes lock, and debris is recognised by KSA's split naming
mod: ksarmory
category: vehicles-and-parts
techniques: [reflection, job-barrier, deferred-application, engine-event-queue, split-id-lineage, fragment-guard]
hooks: [starmap StarMapAfterGui (via StepOnce), reflection KSA.Vehicle._threadWorkerUpdateState]
lifecycle: [frame:draw-ui, frame:apply-solvers]
game_types:
  - KSA.Universe
  - KSA.VehicleDestructionEvent
  - KSA.CrewDisposition
  - KSA.PartFailure
  - KSA.PartFailureEvent
  - KSA.VehicleUpdateState
  - KSA.PhysicsBubble
  - KSA.VehicleUpdateTask
  - KSA.JobSystems
  - KSA.ConstraintSim
  - KSA.Vehicle
tags: [destruction, part-failure, threading, job-system, debris, reflection]
---

A weapons mod has to remove things from KSA's world from inside a frame hook, while the engine's
vehicle worker is running on another thread. KSArmory found three distinct operations with three
different safe recipes, and wrapped each in `KsaWorld`: **destroy** a craft (break it up, leave
debris), **break parts off** a surviving craft, and **remove** a craft without leaving anything
(disposing of a spent stage).[^ksaworld] A fourth piece recognises the debris the engine creates so
fire control does not waste a belt on falling fragments.[^wreckage]

## How it works

| Operation | Recipe | Why |
|---|---|---|
| Barrier | `KsaWorld.WaitForVehicleSolvers()` = `JobSystems.VehicleSolver?.Wait()` | Disposing a vehicle removes it from its `PhysicsBubble`'s vehicle-state list, which the worker task enumerates off the main thread; without the join the engine throws "Collection was modified". `PrepareFrame` waits on the same scheduler (Program.cs L2158) and re-dispatches it at `ExecuteNextVehicleSolvers` (L2220) before any StarMap hook fires, so no hook sits inside the safe window; the mod takes the barrier itself[^ksaworld][^ksa-program][^ksa-jobsystems] |
| Destroy | `Universe.DestroyVehicleFromEvent(v, new VehicleDestructionEvent { Cause = Collision, PeakGLoad = severity, PeakDynamicPressure = severity })` | Runs the engine's own failure machinery, so the craft breaks up with KSA's explosion and debris[^ksaworld][^ksa-universe] |
| Break parts | Reflect `Vehicle._threadWorkerUpdateState` (verified to be a `VehicleUpdateState`) and set its public `PartFailureEvent { FailedParts, DestroyWholeVehicle = false }`, merging into one the engine already queued | Splitting a vehicle from a hook lands between the worker dispatch and the next apply, so results computed for the whole craft are applied to its fragments (measured as an `ArgumentOutOfRangeException` from `FlightComputer.UpdateTvcParams` every frame). The queued event is applied by `PhysicsBubble.ApplyRenderEventsToVehicles` right after the staged results, the same order as KSA's own crash damage[^ksaworld][^ksa-updatestate][^ksa-physicsbubble][^ksa-updatetask] |
| Fatal loss | `PartFailure.TrippedTheFragmentGuard(failed, total)` | Asks the engine's own rule (`failed >= max(2, total * 0.5)`) instead of copying the threshold; a condemned craft is destroyed whole[^ksaworld][^ksa-partfailure] |
| Remove | Barrier, then `using ConstraintSim.UnlockShapesBlocking()`, then `Universe.DestroyVehicle(v, CrewDisposition.EndMission)` | `DestroyVehicleFromEvent` ends in `PartFailure.ShedDebris(vehicle, 12)`, which can leave up to twelve vehicles behind a spent stage; removal also mutates the shapes registry the worker holds[^ksaworld][^ksa-universe][^ksa-constraintsim] |

### Batching

A `WeaponSystem` never kills during its step: it collects `_pendingKills` and `_pendingPartKills`
and `ApplyPendingKills` runs at the end of the round update, taking the barrier **once per batch**,
destroying whole craft first (so no part is isolated out of a corpse), then queueing part failures.[^weapon-system]

### Recognising wreckage

`Vehicle.GenerateSplitId` names a split-off piece `{parentId}_{n}`, picking the first `n` not
already in the system (a piece carrying a named control part takes that name instead).[^ksa-vehicle]
`TryQueuePartFailure` records the craft's Id in `KsaWorld.Wreckage` *before* the engine splits it;
`Wreckage.IsPiece` then walks a name's `_digits` suffixes back to any recorded ancestor, and the
radar ignores such contacts.[^ksaworld][^wreckage][^radar] Flown before this, a Phalanx fired about
480 shells at the pieces of one drone.[^wreckage]

## Lifecycle and timing

- **Decide and apply: `frame:draw-ui`.** Detonations, fuses and blast damage run inside
  `StepSimulation`, normally from the `[StarMapAfterGui]` postfix, while the vehicle worker
  dispatched by this frame's `PrepareFrame` is running. `ApplyPendingKills` joins it before
  disposing anything.[^weapon-system][^entry]
- **Part failures land at `frame:apply-solvers` of the next frame**, inside
  `Universe.ApplyVehicleSolvers` (`VehicleUpdateTask.ApplyResultsToMainThread` ->
  `ApplyRenderEventsToVehicles`), after the worker's synchronized results.[^ksa-updatetask][^ksa-physicsbubble]
- **Save load** clears `KsaWorld.Wreckage` along with everything else keyed on the old world
  (`ForgetTheWorld`).[^entry]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L2595-L2689) | `WaitForVehicleSolvers`, `Remove`, `Destroy` |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L3295-L3400) | Fragment guard, `CanQueuePartFailures` probe, `TryQueuePartFailure` |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs) | `ApplyPendingKills`: one barrier per batch |
| [src/KSArmory/Sim/Wreckage.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Wreckage.cs) | Lineage from split names |

## Snippets

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

## Game APIs touched

| Member | Use |
|---|---|
| `JobSystems.VehicleSolver` ([current/decomp/KSA/JobSystems.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/JobSystems.cs#L14)) `.Wait()` | Join the vehicle worker |
| `Universe.DestroyVehicleFromEvent` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1971)), `DestroyVehicle(Vehicle, CrewDisposition)` (L2018) | Break up vs remove |
| `PartFailure.TrippedTheFragmentGuard`, `ShedDebris` ([current/decomp/KSA/PartFailure.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartFailure.cs#L77)) | Engine rule for fatal loss; debris pieces |
| `VehicleUpdateState.PartFailureEvent` ([current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L80)), `PartFailureEvent.FailedParts`/`DestroyWholeVehicle` ([current/decomp/KSA/PartFailureEvent.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartFailureEvent.cs)) | The engine's own failure queue |
| `Vehicle._threadWorkerUpdateState` (private) | Reflected to reach the update state |
| `PhysicsBubble.ApplyRenderEventsToVehicles` ([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L919)), `VehicleUpdateTask.ApplyResultsToMainThread` ([current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L226)) | Where the queued event is applied |
| `ConstraintSim.UnlockShapesBlocking()` ([current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L135)) | Shapes registry lock for removal |
| `Vehicle.GenerateSplitId(Span<char>)` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1814)) | `{Id}_{n}` naming for debris |

## Pitfalls and version notes

- **Never call `PartFailureEvent.Apply` or split a vehicle from a hook**; queue it.[^ksaworld]
- **Destroying leaves debris; removing does not.** Use `DestroyVehicle` with `EndMission` to dispose
  of a spent stage, or one destroy can add a dozen vehicles to a bubble.[^ksaworld]
- **The reflection is probed once** and, if `_threadWorkerUpdateState` moves or changes type, the
  mod falls back to destroying whole craft for the session: a worse weapon, not a crash.[^ksaworld]
- **A craft without an update state** has no physics bubble, so nothing simulates it to break.[^ksaworld]
- **Wreckage lineage depends on KSA's naming**; if the engine stops naming pieces `{parent}_{n}`,
  wreckage is engaged like any other contact.[^wreckage]
- The mod's own comments cite older `Program.cs` line numbers for the barrier; in build
  2026.10.10.5554 the wait is at L2158 and the dispatch at L2220.[^ksaworld][^ksa-program]

## See also

- [Blast damage and part health](./blast-damage-and-part-health.md)
- [Mod-simulated rounds](./mod-simulated-rounds.md)
- [Radar search and lock](./radar-search-and-lock.md)
- [Fire control and target allocation](./fire-control-and-target-allocation.md)
- [Blast shove impulse](./blast-shove-impulse.md)
- [Consume applied sim step](./consume-applied-sim-step.md)
- [Join JobSystems.VehicleSolver before mutating from a frame hook](../../../howto/physics/solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-4.md) - how-to article demonstrating this feature
- [Destroy vehicles and fail parts safely from a frame hook](../../../howto/vehicles-and-parts/vehicles/change-vehicles-without-racing-the-solver/change-vehicles-without-racing-the-solver-3.md) - how-to article demonstrating this feature

[^ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^weapon-system]: src/KSArmory/Ksa/WeaponSystem.cs
[^wreckage]: src/KSArmory/Sim/Wreckage.cs
[^radar]: src/KSArmory/Ksa/Radar.cs
[^entry]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-partfailure]: KSA.PartFailure (decompiled, build 2026.10.10.5554)
[^ksa-updatestate]: KSA.VehicleUpdateState (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^ksa-updatetask]: KSA.VehicleUpdateTask (decompiled, build 2026.10.10.5554)
[^ksa-jobsystems]: KSA.JobSystems (decompiled, build 2026.10.10.5554)
[^ksa-constraintsim]: KSA.ConstraintSim (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
