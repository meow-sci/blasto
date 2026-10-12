---
type: Mod Feature
title: Automatic staging that keeps Auto burns alive
description: A per-vehicle state machine sampled before and evaluated after the vehicle-solver apply stages on burnout, cascades through empty rows, refuses to drop the last control module, and takes back the stock out-of-propellant stop so an Auto burn survives the staging
mod: ksa-advancedflightcomputer
category: vehicles-and-parts
techniques: [harmony-prefix, harmony-postfix, reflection]
hooks:
  - harmony-prefix KSA.Universe.ApplyVehicleSolvers
  - harmony-postfix KSA.Universe.ApplyVehicleSolvers
  - harmony-prefix KSA.Vehicle.PrepareWorker
  - harmony-prefix KSA.SequenceList.ActivateNextSequence
  - harmony-postfix KSA.SequenceList.ActivateNextSequence
  - harmony-postfix KSA.SequenceList.ResetCaches
  - reflection KSA.SequenceList.ActiveSequence
  - reflection KSA.SequenceList._updatingSequence
lifecycle: [frame:apply-solvers, frame:input, frame:dispatch-solvers]
game_types:
  - KSA.SequenceList
  - KSA.Sequence
  - KSA.Vehicle
  - KSA.FlightComputer
  - KSA.BurnTarget
  - KSA.EngineController
  - KSA.EngineControllerState
  - KSA.PartTree
tags: [staging, sequences, auto-burn, engines, solid-motors, state-machine]
---

Arm AUTOSTAGE on a vehicle and the next sequence activates whenever the active engines run dry,
again when the new stage is empty or only decouplers, and also while the rest of the stack still
burns if the next row would only shed burnt-out boosters (see [spent stage drop](spent-stage-drop.md)).
It works during stock Auto burns without aborting them, which is the hard part: in KSA a freshly lit
engine looks dry for one worker tick, and stock ends an Auto burn as soon as every active engine is
dry.

## How it works

### State machine per vehicle

`StagingDetector` keeps a `StagingState` per `Vehicle` with phases Monitoring, AwaitingIgnition
(delays running), AwaitingActivation (player staged during an Auto burn) and AwaitingPropagation
(new engines not yet reported fueled). The header comment lists every transition
([AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs#L9-L25)). The switch belongs to
the vehicle, so an armed craft keeps staging while the player controls another one.

### Sample, then evaluate

- **Prefix on `Universe.ApplyVehicleSolvers`**: `Sample()` records the burn mode and "had an active
  fueled engine" for every armed vehicle, because both are edges that applying the worker results
  destroys ([AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs#L104-L117)).
- **Postfix** (first in the [post-solver vehicle tick](post-solver-vehicle-tick.md)): `Evaluate()`
  fires pending delayed modules, then evaluates each armed vehicle: burnout, a caller's request, a
  dwell-gated spent-jettison check, the cascade when the new stage cannot thrust, and the
  control-module guard ("the next sequence would separate the last control module": held with an
  alert, the player stages by hand).

### Keeping the Auto burn

- **Take the stop back.** While a staging is in flight for the loaded burn target, `KeepAutoBurn`
  gives Auto back through `StockBurnMode.GiveBackAuto(..., takesStopBack: true)`, clears
  `FlightComputer.AutoBurnStoppedOutOfPropellant` and `BurnTarget.LastIgnitionDenied`, all before
  `RaisePendingAlerts` reads them later in the same `PrepareFrame`
  ([AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs#L495-L525)). The stock rules it
  undoes: `ComputeControl` drops Auto to Manual after a second denied ignition and when every active
  engine is dry ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L500-L512)).[^ksa-flightcomputer]
- **Seed the new engine.** In every worker frame `FlightComputer.ComputeControl` runs before
  `Rocket.UpdateRockets`, so a newly activated engine still carries `IsPropellantAvailable = false`
  for its first tick. A prefix on `Vehicle.PrepareWorker` (main thread, before the worker snapshot)
  writes the value `UpdateRockets` would compute into the committed state with
  `GetModuleAndAllMutableStatesForInitialization`, leaving `WasActive` false so the first update
  pass still runs ([AdvancedFlightComputer/Features/AutoStage/NewEngineSeedPatch.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/NewEngineSeedPatch.cs)).
- **Player staging.** A prefix/postfix on `SequenceList.ActivateNextSequence` (which AFC's own
  staging never calls) records the engines the row lights during an Auto burn and holds the burn
  until those activations land in the next frame's input drain
  ([AdvancedFlightComputer/Features/AutoStage/SequenceListPatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/SequenceListPatches.cs)).

### Activating a row without the stock method

`StagingExecution.ActivateNextSequenceSplit` re-implements `SequenceList.ActivateNextSequence`
([current/decomp/KSA/SequenceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequenceList.cs#L171-L206)) so decouplers and engines can be held back independently
(see [staging delays](staging-delays.md)): it sets the private `ActiveSequence` setter and the
private `_updatingSequence` guard by reflection (the public `SetActiveSequence` rewrites `Activated`
list-wide), walks `Part.InSequence(n)` modules in the same reverse order as
`Part.ActivateSubtreeInStage`, then `ResetCaches` and `RemoveSpentSequences`. It never drains input
or rebuilds part trees: `module.Activate` only queues, and `PrepareFrame` drains the queue and
`PartTree.FlushDirtyDerived` rebuilds before the next step
([AdvancedFlightComputer/Features/AutoStage/StagingExecution.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingExecution.cs#L13-L99)).

### Other details

- `StagingHelpers` calls `PartTree.EnsureDerived(DerivedData.SolidMotorStacks)` before reading
  solid-motor fuel, because the hooks run before `PrepareFrame` flushes dirty part trees.
- If the standalone AutoStage mod is installed (its gauge enum `AutoStageToggle` is present), AFC's
  stager stays off, since two stagers would fire two rows on one burnout.
- Guidance uses `StagingDetector.Arm` once per flight and `RequestStaging` for cues only its plan
  knows (cold ignition, reserve boundary); the answer is `Queued`, `Deferred` or `Busy`
  ([AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs#L41-L84)).
- A load disarms every vehicle (`SaveScopedState.ResetAll`), and unload flushes held modules first
  (which, under StarMap, happens after the game loop has ended).

## Lifecycle and timing

| Hook | Lifecycle | Why |
|---|---|---|
| `ApplyVehicleSolvers` prefix / postfix | `frame:apply-solvers` | Before/after comparison of the finished step; take back stock's stop before `RaisePendingAlerts` |
| `SequenceList.ActivateNextSequence` prefix / postfix | `frame:input` (the staging key is applied in `InputEvents.ApplyInputEvents`) | Detect the player's staging |
| `Vehicle.PrepareWorker` prefix | `frame:dispatch-solvers` | Last main-thread write before the worker copies engine states |

Delays are deadlines in simulation time, not countdowns fed by frame delta, because `Evaluate` also
runs while the game is paused.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs) | State machine, sample/evaluate, burn hold, API for guidance |
| [AdvancedFlightComputer/Features/AutoStage/StagingExecution.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingExecution.cs) | Split re-implementation of `ActivateNextSequence` |
| [AdvancedFlightComputer/Features/AutoStage/NewEngineSeedPatch.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/NewEngineSeedPatch.cs) | First-tick propellant seed for new engines |
| [AdvancedFlightComputer/Features/AutoStage/SequenceListPatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/SequenceListPatches.cs) | Player staging detection and cache invalidation |
| [AdvancedFlightComputer/Features/AutoStage/StagingHelpers.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingHelpers.cs) | Engine surveys, solid-motor derived data |
| [AdvancedFlightComputer/Core/VehiclePropellant.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/VehiclePropellant.cs) | `IsFueled` from mole and rocket-core states |
| [AdvancedFlightComputer/Features/AutoStage/AutoStageFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/AutoStageFeature.cs) | Patch block, standalone detection, settings registration |
| [tests/AdvancedFlightComputer.HarnessTests/Features/AutoStage/AutoStageAutoBurnHoldTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/AutoStage/AutoStageAutoBurnHoldTest.cs) | Headless test: an Auto burn survives a staging |

## Snippets

```csharp
// AdvancedFlightComputer/Features/AutoStage/NewEngineSeedPatch.cs L13-L47
[HarmonyPatch(typeof(Vehicle), nameof(Vehicle.PrepareWorker), new[] { typeof(SimStep) })]
internal static class NewEngineSeedPatch
{
    static void Prefix(Vehicle __instance) => Seed(__instance);

    // Only during an Auto burn, the one mode in which the stale tick ends what the player set up.
    internal static int Seed(Vehicle vehicle)
    {
        FlightComputer fc = vehicle.FlightComputer;
        if (vehicle.IsDisposed || fc.BurnMode != FlightComputerBurnMode.Auto || fc.Burn == null)
            return 0;
        PartTree? tree = vehicle.Parts;
        if (tree?.States == null
            || !ModuleStateful<EngineController, EngineControllerState, EngineControllerGlobalState, EmptyStruct>
                .TryGetFrom(tree.States, out var engines))
            return 0;

        int seeded = 0;
        bool derived = false;
        foreach (var engine in engines.ModulesAndStates)
        {
            if (!engine.Module.IsActive || engine.State.WasActive)
                continue;
            if (!derived)
            {
                tree.EnsureDerived(DerivedData.SolidMotorStacks);
                derived = true;
            }
            bool fueled = VehiclePropellant.IsFueled(engine.Module, tree.Moles.States, tree.RocketCores.States, out _, out _);
            if (fueled == engine.State.IsPropellantAvailable)
                continue;
            engines.GetModuleAndAllMutableStatesForInitialization(engine.Module).State.IsPropellantAvailable = fueled;
            if (fueled)
                seeded++;
        }
```

```csharp
// AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs L503-L525
    private static void KeepAutoBurn(Vehicle vehicle, StagingState state, FlightComputer fc)
    {
        BurnTarget? held = state.HeldBurn;
        if (held == null)
            return;
        if (!ReferenceEquals(fc.Burn, held) || IsBurnComplete(fc))
        {
            state.HeldBurn = null;
            return;
        }
        bool takesBack = fc.BurnMode == FlightComputerBurnMode.Manual || fc.AutoBurnStoppedOutOfPropellant;
        if (fc.BurnMode == FlightComputerBurnMode.Manual && !StockBurnMode.GiveBackAuto(vehicle, held, takesStopBack: true))
        {
            // A vehicle without control cannot fly the burn, so stock ends it.
            state.HeldBurn = null;
            return;
        }
        fc.AutoBurnStoppedOutOfPropellant = false;
        // The stop came with a denied ignition. FlightComputer.ComputeControl drops an Auto burn to Manual without any flag when the next ignition is denied too,
        // so the denial is cleared with the stop. A next dry tick then raises the stop again, and when the hold lets it stand, FlightComputer.RaisePendingAlerts ends the burn with its alert.
        if (takesBack)
            held.LastIgnitionDenied = false;
    }
```

## Game APIs touched

| Member | Use |
|---|---|
| `SequenceList.ActivateNextSequence(Vehicle)` ([current/decomp/KSA/SequenceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequenceList.cs#L171)) | Stock staging; patched for detection and re-implemented |
| `SequenceList.ActiveSequence` (private setter), `_updatingSequence`, `ResetCaches`, `RemoveSpentSequences` ([current/decomp/KSA/SequenceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequenceList.cs)) | Row activation internals |
| `FlightComputer.AutoBurnStoppedOutOfPropellant`, `BurnTarget.LastIgnitionDenied` ([current/decomp/KSA/BurnTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnTarget.cs#L36)) | Stock stop taken back |
| `EngineControllerState.IsPropellantAvailable`, `WasActive` ([current/decomp/KSA/EngineControllerState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineControllerState.cs)) | First-tick seed |
| `PartTree.EnsureDerived(DerivedData)` ([current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L521)) | Solid-motor stacks before fuel reads |
| `Rocket.UpdateRockets` ([current/decomp/KSA/Rocket.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Rocket.cs#L75)) | Stock pass whose first result is seeded |

## Pitfalls and version notes

- Do not call `PartTree` rebuilds or drain activation buffers from the solver-apply hook. AFC's comment
  says doing so "would mutate a list PhysicsBubble is iterating"; in build 2026.10.10.5554 the vehicle
  workers are already joined when the `ApplyVehicleSolvers` postfix runs, so the concrete reason is
  order rather than a live race: stock drains `IActivateInputBuffer` in `ApplyInputEvents` (with
  per-vehicle decoupler batching in `InputEvents.ApplyIActivateBatched`) and rebuilds dirty trees in
  `PartTree.FlushDirtyDerived` just before dispatch, and queueing keeps AFC's activations on that path
  ([current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1046-L1118)).
- A row marked activated is skipped by stock forever; a held module dropped by a bug can never fire
  again, which is why pending modules are fired before anything else in each tick. The unload flush
  does not help in practice under StarMap 4dc60f8: `[StarMapUnload]` runs only after the game's
  `Main` has returned, so queued activations are never drained (see
  [feature-gated patch sets](feature-gated-patch-sets.md)).
- `Burn` objects are saved and can load with a zero `DeltaVTargetCci`, which would satisfy the
  overshoot completion test forever; AFC guards that case.
- Do not install the standalone AutoStage mod next to AFC.

## See also

- [Spent stage drop](spent-stage-drop.md)
- [Staging delays](staging-delays.md)
- [AUTOSTAGE gauge button](autostage-gauge-button.md)
- [Vehicle control ownership](vehicle-control-ownership.md)
- [UPFG ascent guidance](upfg-ascent-guidance.md) (arms staging and requests rows)
- [Stage on burnout around Universe.ApplyVehicleSolvers without losing the Auto burn](../../../howto/vehicles-and-parts/staging-and-engines/stage-automatically/stage-automatically-1.md) - how-to article demonstrating this feature

[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
