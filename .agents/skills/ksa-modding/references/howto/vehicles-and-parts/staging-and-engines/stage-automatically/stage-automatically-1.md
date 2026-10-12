---
type: How-To
title: Stage on burnout around Universe.ApplyVehicleSolvers without losing the Auto burn
description: Sample before the solver results are applied, evaluate after, stage through a per-vehicle state machine and take the stock out-of-propellant stop back while the new engines report their first tick
category: vehicles-and-parts
group: staging-and-engines
topic: stage-automatically
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/automatic-staging]
techniques: [harmony-prefix, harmony-postfix, state-machine]
hooks:
  - harmony-prefix KSA.Universe.ApplyVehicleSolvers
  - harmony-postfix KSA.Universe.ApplyVehicleSolvers
  - harmony-prefix KSA.Vehicle.PrepareWorker
  - harmony-prefix KSA.SequenceList.ActivateNextSequence
lifecycle: [frame:apply-solvers, frame:dispatch-solvers]
game_types:
  - KSA.Universe
  - KSA.SequenceList
  - KSA.FlightComputer
  - KSA.BurnTarget
  - KSA.EngineController
  - KSA.Vehicle
tags: [staging, burnout, auto-burn, state-machine]
---
Outcome: an armed vehicle activates its next engine sequence the moment its active engines run dry (or on request, or in a
cascade when the new stage is also dry), and a running Auto burn survives the swap. Prerequisites: StarMap and Harmony.
Demonstrated by [ksa-advancedflightcomputer/automatic-staging](../../../../mods/ksa-advancedflightcomputer/features/automatic-staging.md).

## Background: how the game does it

See the [overview](overview.md). The three engine facts that shape the code: the solver results are applied in
`Universe.ApplyVehicleSolvers`; a burnout is an edge that the applied results destroy (so it must be sampled before); and
`ComputeControl` ends an Auto burn when every active engine reads dry, which a newly lit engine does on its first tick
[current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L498-L513)[^ksa-flightcomputer].

## Lifecycle and timing

Everything runs on the main thread in `frame:apply-solvers`, with the workers joined. The prefix samples the pre-apply state, the
postfix evaluates (so the activation's buffered effects are drained by `InputEvents.ApplyInputEvents` later the same frame,
before `RaisePendingAlerts` can act on a stop). The seed patch runs in `frame:dispatch-solvers` as a prefix on
`Vehicle.PrepareWorker`. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md) and [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-2.md).

## Steps

1. **Bracket the solver apply.** The prefix records, for each armed vehicle, the burn mode and whether an active engine had
   propellant; the postfix runs the state machine for every vehicle:

```csharp
// AdvancedFlightComputer/Core/SharedVehicleHooks.cs L108-120
    // Program.PrepareFrame joins the workers before this call, then drains input events, then lets FlightComputer.RaisePendingAlerts end the burns the workers or the RCS executor finished or stopped.
    // The prefix runs before the worker results overwrite the burn state the staging detector compares against.
    [HarmonyPatch(typeof(Universe), nameof(Universe.ApplyVehicleSolvers), new Type[0])]
    private static class ApplySolversPatch
    {
        static void Prefix()
        {
            if (AutoStageEnabled)
                StagingDetector.Sample();
        }

        static void Postfix() => TickVehicles(LoadedVehicles.All);
    }
```

2. **Sample** the edges that applying the results destroys:

```csharp
// AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs L104-117
    // Both values are edges the applied worker results destroy, and ApplyInputEvents drains in between, so an engine the player shut down would otherwise look like a burnout.
    internal static void Sample()
    {
        foreach (KeyValuePair<Vehicle, StagingState> entry in _states)
        {
            StagingState state = entry.Value;
            Vehicle vehicle = entry.Key;
            state.Sampled = state.Active && !vehicle.IsDisposed;
            if (!state.Sampled)
                continue;
            state.SampledBurnMode = vehicle.FlightComputer.BurnMode;
            state.SampledHadPropellant = StagingHelpers.HasActiveEngineWithPropellant(vehicle);
        }
    }
```

3. **Decide.** From `Monitoring`: a caller's request stages; otherwise a burnout edge (no propellant now, had some at the
   sample or the stock stop is set, burn not complete, a later engine row exists) stages:

```csharp
// AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs L174-191
        switch (state.State)
        {
            case StagingState.Phase.Monitoring:
                if (state.Requested)
                {
                    state.Requested = false;
                    state.SpentJettisonSince = double.NaN;
                    ExecuteStaging(vehicle, state, fc, RunningAutoBurn(state, fc), "requested");
                }
                // The stock stop counts as a burnout edge too, because the worker can see the engines dry before this frame's sample did.
                else if (!hasPropellant
                    && (state.SampledHadPropellant || fc.AutoBurnStoppedOutOfPropellant)
                    && !IsBurnComplete(fc)
                    && StagingHelpers.HasNextEngineSequence(vehicle))
                {
                    state.SpentJettisonSince = double.NaN;
                    ExecuteStaging(vehicle, state, fc, RunningAutoBurn(state, fc), "burnout");
                }
```

4. **Stage, guarded.** Refuse if the row would separate the last control module, otherwise call the activation and enter a wait
   phase; `AwaitingPropagation` counts worker frames until the new engines are fed, then either returns to `Monitoring` or
   cascades to the next row if they are dry too:

```csharp
// AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs L436-471, L488-493
    private static void ExecuteStaging(Vehicle vehicle, StagingState state, FlightComputer fc,
        BurnTarget? heldBurn, string trigger)
    {
        if (JettisonAnalysis.WouldSeparateLastControl(vehicle))
        {
            // Once per row: the triggers keep asking every frame while the row stands.
            if (!state.HeldForControl)
            {
                state.HeldForControl = true;
                DefaultCategory.Log.Warning(
                    $"[AFC] AutoStage held on '{vehicle.Id}': the next sequence would separate the last control module. Stage it by hand if that is wanted.");
                TimedAlert.Create("AutoStage held: the next sequence would separate the control module", Color.Yellow, 4.0);
            }
            // The stop stands, so stock ends the burn it cannot continue.
            state.HeldBurn = null;
            state.State = StagingState.Phase.Monitoring;
            return;
        }
        state.HeldForControl = false;

        if (DebugConfig.AutoStage)
        {
            string dvInfo = fc.Burn != null
                ? $"dV remaining = {fc.Burn.DeltaVToGoCci.Length():F1} m/s"
                : "no burn planned";
            string mode = heldBurn != null ? "Auto burn" : "no Auto burn";
            DefaultCategory.Log.Debug($"[AFC] AutoStage staging '{vehicle.Id}' on {trigger} ({mode}): {dvInfo}");
        }

        state.HeldBurn = heldBurn;
        FlushPendingStaging(vehicle, state);

        PendingStaging? pending = StagingExecution.ActivateNextSequenceSplit(vehicle);
        state.Activations++;

        KeepAutoBurn(vehicle, state, fc);
// ...
        else
        {
            state.ActivationEngines = null;
            BeginPropagation(state, Universe.GetElapsedSeconds());
        }
    }
```

5. **Take the stop back.** While staging is in flight the stock stop is cleared each frame, Auto is given back, and the denied
   ignition flag cleared, but only while the held burn target is still the loaded one and not complete:

```csharp
// AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs L503-525
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

6. **Seed the new engines** so the first worker tick does not see them dry: write `IsPropellantAvailable` into the committed
   state in a `PrepareWorker` prefix (main thread, before the snapshot), only during an Auto burn:

```csharp
// AdvancedFlightComputer/Features/AutoStage/NewEngineSeedPatch.cs L13-52
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

        if (seeded > 0 && DebugConfig.AutoStage)
            DefaultCategory.Log.Debug($"[AFC] AutoStage reports {seeded} newly lit engine(s) on '{vehicle.Id}' as fueled for the first worker tick.");
        return seeded;
    }
```

7. **Notice the player's own staging** during an Auto burn with a prefix and postfix on `SequenceList.ActivateNextSequence`
   (the mod's own staging does not call it) and hold the burn until the activation lands:

```csharp
// AdvancedFlightComputer/Features/AutoStage/SequenceListPatches.cs L10-26
    [HarmonyPatch(typeof(SequenceList), nameof(SequenceList.ActivateNextSequence), new[] { typeof(Vehicle) })]
    internal static class ActivateNextSequencePatch
    {
        static void Prefix(SequenceList __instance, Vehicle vehicle, out PlayerStaging __state)
            => __state = new PlayerStaging(__instance.ActiveSequence,
                vehicle?.FlightComputer.BurnMode == FlightComputerBurnMode.Auto
                    ? StagingHelpers.EnginesLitByNextRow(__instance)
                    : null);

        // Stock activates a row only when that changes ActiveSequence.
        static void Postfix(SequenceList __instance, Vehicle vehicle, PlayerStaging __state)
        {
            StagingHelpers.InvalidateSequenceCache();
            if (__state.Engines != null && vehicle != null && __instance.ActiveSequence != __state.ActiveSequence)
                StagingDetector.OnPlayerStaged(vehicle, __state.Engines);
        }
    }
```
```csharp
// AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs L291-309
    // SequenceList.ActivateNextSequence switched these engines on while an Auto burn ran. AutoStage's own staging does not call it, so this is the player's staging key.
    // The activation only queues, so the burn is held from here on, through the frame the activation lands in and the frame the stale stop of the new engines arrives in.
    internal static void OnPlayerStaged(Vehicle vehicle, List<EngineController> engines)
    {
        FlightComputer fc = vehicle.FlightComputer;
        if (vehicle.IsDisposed || fc.BurnMode != FlightComputerBurnMode.Auto || fc.Burn == null)
            return;
        StagingState state = StateOf(vehicle);
        // A staging waiting out its delays already holds the burn on every frame, and its propagation follows.
        if (state.State == StagingState.Phase.AwaitingIgnition)
            return;
        state.HeldBurn = fc.Burn;
        state.ActivationEngines = engines;
        state.ActivationFrames = 0;
        state.State = StagingState.Phase.AwaitingActivation;
        if (DebugConfig.AutoStage)
            DefaultCategory.Log.Debug(
                $"[AFC] AutoStage holds the Auto burn on '{vehicle.Id}' across a player staging that lights {engines.Count} engine(s).");
    }
```

## Complete example

- [AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs)[^ksa-advancedflightcomputer-stagingdetector]
- [AdvancedFlightComputer/Features/AutoStage/NewEngineSeedPatch.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/NewEngineSeedPatch.cs)[^ksa-advancedflightcomputer-newengineseedpatch]
- [AdvancedFlightComputer/Features/AutoStage/SequenceListPatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/SequenceListPatches.cs)[^ksa-advancedflightcomputer-sequencelistpatches]
- [AdvancedFlightComputer/Features/AutoStage/StagingHelpers.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingHelpers.cs)[^ksa-advancedflightcomputer-staginghelpers]
- [AdvancedFlightComputer/Core/SharedVehicleHooks.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SharedVehicleHooks.cs#L38-L55)[^ksa-advancedflightcomputer-sharedvehiclehooks]

## Variations and gotchas

- **Paused frames.** A paused frame runs no worker step, so the propagation counter only advances when sim time advances.
- **Burn completion.** The mod does not stage a finished burn; completion is read from `AutoBurnCompleted` or a reversed delta-v
  [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L542-L573):

```csharp
// AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs L527-540
    // True in the frame stock reports the burn complete, and after it for a burn that stock completes by the delta-V reversal, because what is left to go then no longer points along the target.
    // A fixed-duration burn reads complete only in that frame, because FlightComputer.RaisePendingAlerts clears the flag and the reversal does not apply to it.
    // Burn outlives its BurnPlan entry and is saved, so a vehicle can load with a zero DeltaVTargetCci, which would pass the overshoot test forever and disable both triggers for the flight.
    private static bool IsBurnComplete(FlightComputer fc)
    {
        BurnTarget? burn = fc.Burn;
        if (burn == null)
            return false;
        if (fc.AutoBurnCompleted)
            return true;
        if (burn.DeltaVTargetCci.IsNearlyZero())
            return false;
        return float3.Dot(burn.DeltaVToGoCci, burn.DeltaVTargetCci) <= 0f;
    }
```

- **Control guard.** Staging past the last control module is held with an alert instead (article 2).
- **Disposed vehicles.** States are dropped when a vehicle disposes (`ForgetVehicle`) and pending activations are fired on
  unload, see [article 3](stage-automatically-3.md).
- **Ordering assumptions.** The mod's own header comment documents that `ComputeControl` runs before `Rocket.UpdateRockets` in
  every worker frame; re-check that after game updates ([AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs#L9-L24)).

## See also

- [Overview](overview.md), [Article 2](stage-automatically-2.md), [Article 3](stage-automatically-3.md)
- [ksa-advancedflightcomputer/automatic-staging](../../../../mods/ksa-advancedflightcomputer/features/automatic-staging.md)
- [Ignite engines, activate the next stage, fire a decoupler or undock from code](../stage-a-vehicle-from-code/overview.md), [Compute engine thrust, Isp and mass flow at ambient pressure and invert for throttle](../compute-engine-thrust-at-ambient-pressure/overview.md)

[^ksa-advancedflightcomputer-stagingdetector]: KSA-AdvancedFlightComputer: AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs
[^ksa-advancedflightcomputer-sequencelistpatches]: KSA-AdvancedFlightComputer: AdvancedFlightComputer/Features/AutoStage/SequenceListPatches.cs
[^ksa-advancedflightcomputer-staginghelpers]: KSA-AdvancedFlightComputer: AdvancedFlightComputer/Features/AutoStage/StagingHelpers.cs
[^ksa-flightcomputer]: current/decomp/KSA/FlightComputer.cs (decompiled, build 2026.10.10.5554)
[^ksa-advancedflightcomputer-newengineseedpatch]: KSA-AdvancedFlightComputer: AdvancedFlightComputer/Features/AutoStage/NewEngineSeedPatch.cs
[^ksa-advancedflightcomputer-sharedvehiclehooks]: KSA-AdvancedFlightComputer: AdvancedFlightComputer/Core/SharedVehicleHooks.cs
