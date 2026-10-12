---
type: How-To
title: Intercept the Auto burn-mode request and fly the burn with RCS only
description: Patch Vehicle.SetEnum so Auto on an engine-less burn starts an RCS executor, publish an immutable command to the vehicle worker keyed by the shared BurnPlan, and end the burn through the stock completion flags
category: flight-and-orbits
group: autopilot
topic: execute-burns-with-rcs-and-throttle-inputs
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/rcs-burn-execution
  - mods/ksa-advancedflightcomputer/features/rcs-thruster-allocation
techniques: [harmony-prefix, harmony-postfix, conditional-weak-table]
hooks:
  - harmony-prefix KSA.Vehicle.SetEnum
  - harmony-postfix KSA.Universe.ApplyVehicleSolvers
  - harmony-postfix KSA.FlightComputer.ComputeControl
lifecycle: [frame:input, frame:apply-solvers, physics:vehicle-solver]
game_types:
  - KSA.Vehicle
  - KSA.FlightComputer
  - KSA.BurnTarget
  - KSA.BurnPlan
  - KSA.FlightComputerOutput
  - KSA.FlightComputerBurnMode
tags: [rcs, burn, set-enum, worker-command, thruster-allocation, burn-plan, auto-burn]
---

Outcome: the player plans a burn as usual and presses the stock Auto button; if the vehicle has no active, fueled main engine, the burn is flown by translating with RCS thrusters and ends exactly like an engine burn.
KSA-AdvancedFlightComputer implements it.[RCS-only burn execution through the stock Auto button](../../../../mods/ksa-advancedflightcomputer/features/rcs-burn-execution.md) Prerequisites: StarMap and Harmony.

## Background: how the game does it

The Auto button and the burn-mode hotkeys end in `Vehicle.SetEnum(Enum)`, which dispatches on the enum type; a `FlightComputerBurnMode` leg sets the mode.[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6321)
The worker's `FlightComputer.ComputeControl` computes the control output each step from the flight computer copy it received via `CopyFrom`; `FlightComputer.CopyFrom` assigns `BurnPlan`, `VehicleConfig` and `Burn` by reference, so the
vehicle's and the worker's flight computer objects differ while their `BurnPlan` is the same instance.[^ksa-flightcomputer] [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L181-L225) Burn completion is signalled by the two flags
`AutoBurnCompleted` and `AutoBurnStoppedOutOfPropellant`, consumed in `RaisePendingAlerts` (see [Execute planned burns hands-free with the stock Auto burn](../execute-planned-burns-with-the-stock-autopilot/overview.md)).

## Lifecycle and timing

| Step | Where | Thread |
|---|---|---|
| Auto click or hotkey | `Vehicle.SetEnum` prefix; a gauge click is queued in `InputEvents.FlightComputerInputBuffer` during the draw and applied in `ApplyInputEvents` (`frame:input`) | main |
| State machine, publish command, raise end flags | `Universe.ApplyVehicleSolvers` postfix (`frame:apply-solvers`) | main |
| Stock reacts to the flags | `FlightComputer.RaisePendingAlerts` later in `PrepareFrame` | main |
| Pulses | `FlightComputer.ComputeControl` postfix (`physics:vehicle-solver`) | vehicle worker |

The executor sets the end flags after `CopyFrom` wrote the worker results back, and `Vehicle.PrepareWorker` copies the flight computer to the next step only after `RaisePendingAlerts` cleared the flag, so no copy can drop it. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Intercept the trigger.** A prefix on `Vehicle.SetEnum(Enum)` looks only at `FlightComputerBurnMode`; an Auto on a burn that resolves to RCS activates the executor and returns `false` (stock never arms Auto); a click while an execution runs cancels it:

```csharp
// AdvancedFlightComputer/Features/RcsTranslation/RcsDriverPatch.cs L28-L42
/// <summary>
/// Intercepts the BurnMode leg of <see cref="Vehicle.SetEnum"/> (the sink
/// of both the gauge Auto button and the burn-mode hotkeys) so a burn that
/// resolves to RCS engages this executor instead of the stock engine
/// autopilot, and a click while running cancels.
/// </summary>
[HarmonyPatch(typeof(Vehicle), nameof(Vehicle.SetEnum), new Type[] { typeof(Enum) })]
internal static class RcsSetEnumPatch
{
    static bool Prefix(Vehicle __instance, Enum? enumValue)
    {
        if (enumValue is not FlightComputerBurnMode mode)
            return true;
        return RcsExecutor.OnBurnModeSetEnum(__instance, mode);
    }
```

2. **Resolve the method.** Default is the engine when `IsAnyEngineActive()` and `IsAnyEnginePropellantAvailable()`, otherwise RCS; a per-burn override comes from the burn editor. Refuse (with an alert, before touching controls) a burn with no delta-v,
   no usable translation or no axis that serves its direction.
3. **Take control** through the claim and hold Manual (see [Run a closed-loop autopilot for every vehicle each step and arbitrate control with the player](../run-an-autopilot-for-every-vehicle-each-step/overview.md)), enable RCS if needed, take the attitude once and record what to restore.
4. **Run a main-thread state machine** in the post-solver tick: cancel when the burn was deleted or reordered, refresh thruster capability once per second, watch progress, detect attitude takeovers, and end when the game's own `BurnTarget.DeltaVToGoCci` is inside the thrusters' minimum impulse.
5. **Hand a command to the worker** as an immutable record in a table keyed by the shared `BurnPlan`; values that could throw (such as `UniverseTime` from NaN) are built on the main thread:

```csharp
// AdvancedFlightComputer/Features/RcsTranslation/RcsWorkerCommand.cs L40-L59
/// <summary>FlightComputer.CopyFrom shares BurnPlan between the vehicle and worker flight computers. Use that shared reference as the command key because the flight computer instances differ.</summary>
internal static class RcsCommandChannel
{
    private static readonly ConditionalWeakTable<BurnPlan, RcsWorkerCommand> _commands = new();

    public static void Publish(BurnPlan plan, RcsWorkerCommand command)
        => _commands.AddOrUpdate(plan, command);

    public static void Clear(BurnPlan plan)
    {
        // Avoid taking the table write lock when this vehicle has no published command.
        if (_commands.TryGetValue(plan, out _))
            _commands.Remove(plan);
    }

    public static bool TryGet(BurnPlan plan, out RcsWorkerCommand command)
        => _commands.TryGetValue(plan, out command!);

    public static void Reset() => _commands.Clear();
}
```

6. **Write pulses on the worker** from a postfix on `ComputeControl`: zero engine commands, replace the burn target's engine timing with the remaining RCS duration, wait for ignition, check the align gate, convert to-go delta-v to a control-frame impulse and fire, each axis pulse capped at one control period and suppressed below the minimum correcting impulse:

```csharp
// AdvancedFlightComputer/Features/RcsTranslation/RcsComputeControlPatch.cs L12-L38
internal static void Command(FlightComputer fc, in FlightComputerNavigation nav,
                             ref FlightComputerOutput outputs, ref VehicleCommandSink.Receipt receipt)
{
    // Only active executions may override stock engine commands and burn timing.
    if (!RcsCommandChannel.TryGet(fc.BurnPlan, out RcsWorkerCommand cmd) || !cmd.Active)
        return;

    cmd.MarkConsumed();

    // Suppress engine commands even if another caller changed BurnMode directly.
    ZeroEngineCommands(ref outputs);

    BurnTarget? bt = fc.Burn;
    if (bt == null)
        return;

    float3 togo = bt.DeltaVToGoCci;
    float3 impulse = float3.Pack(
        double3.Unpack(togo).Transform(doubleQuat.Concatenate(nav.Ctrl2Body, nav.Body2Cci).Inverse()))
        * fc.TotalMassPropsBody.Mass;

    // Stock UpdateBurnTarget writes engine timing every tick. Replace it with remaining RCS duration. Stale LP geometry uses the group model.
    bool lpUsable = cmd.LpSecondsPerImpulse != null
        && cmd.LpSecondsPerImpulse.Length == fc.VehicleConfig.Thrusters.Count
        && (!cmd.RequireAttitude || !RcsExecutor.OutsideAlignGate(fc));
    bt.BurnDuration = RemainingDurationSec(cmd, impulse, lpUsable);
    bt.IgnitionTime = cmd.IgnitionTime;
```

7. **Choose the allocator.** `Groups` (default) fires the six signed control-axis groups and lets attitude control counter residual torque; `LP` solves a fuel-optimal jet selection over individual thruster wrenches with a self-contained two-phase simplex and falls back to Groups when infeasible
   (see [AdvancedFlightComputer/Features/RcsTranslation/RcsWrenchTable.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsWrenchTable.cs) and [AdvancedFlightComputer/Features/RcsTranslation/RcsLpSolver.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsLpSolver.cs)). Wrenches are computed per nozzle with `RocketCore.ComputeConditions(1f)` and `RocketNozzle.ComputePerformance`, at full throttle because pulses always command throttle 1.
8. **End like stock.** `Complete` releases control, then sets `fc.AutoBurnCompleted = true`; `StopOutOfPropellant` sets `AutoBurnStoppedOutOfPropellant`:

```csharp
// AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.cs L915-L937
// An RCS burn ends through the same flags the vehicle worker raises for a stock Auto burn, so stock FlightComputer.RaisePendingAlerts ends both kinds alike later in Program.PrepareFrame.
// It stops the time warp, removes the burn and loads the next one when another burn follows, and shows its alert, and SharedVehicleHooks then hands the outcome to MultiPass and AutoRemove.
// This driver runs after FlightComputer.CopyFrom has written the worker results back, and Vehicle.PrepareWorker copies this flight computer to the next worker step only after RaisePendingAlerts has cleared the flag, so no copy can drop it in between.
// The flag is set after the release, so a release that throws leaves the burn to the fault cleanup instead of ending it.
internal static void Complete(Vehicle vehicle, FlightComputer fc, RcsExecution exec, float residualMs)
{
    double burnTime = exec.ActiveBurnTimeSec ?? 0.0;
    double burnDv = exec.ActiveBurnDvMs ?? 0.0;
    RcsFuelSummary fuel = ComputeFuelSummary(fc, exec);

    // Stock also leaves a completed burn in Manual.
    exec.ForcedBurnManual = false;
    EndExecution(vehicle, exec);
    RcsBurnOptions? options = exec.FindOptions(burnTime, burnDv);
    if (options != null)
        exec.Options.Remove(options);
    float accumMs = fc.Burn?.DeltaVAccumCci.Length() ?? 0f;
    DefaultCategory.Log.Info(
        $"[AFC] RCS burn complete: vehicle='{vehicle.Id}' " +
        $"accumulated={accumMs:F3}m/s of {burnDv:F2}m/s, residual={residualMs:F3}m/s");
    LogFuel(vehicle, in fuel);
    fc.AutoBurnCompleted = true;
}
```

9. **Give warp a margin** so an Align slew finishes: a prefix on `Universe.AutoWarpTo(UniverseTime, double, bool)` enlarges only the margin of warps aimed at the loaded burn's ignition time.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/RcsTranslation/RcsDriverPatch.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsDriverPatch.cs) | `SetEnum` interception and the per-vehicle tick entry |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.cs) | main-thread executor: resolution, activation, control, completion |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsWorkerCommand.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsWorkerCommand.cs) | immutable worker command and `BurnPlan`-keyed channel |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsComputeControlPatch.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsComputeControlPatch.cs) | worker-side pulse writer |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsWarpPatch.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsWarpPatch.cs) | warp margin for align |

## Variations and gotchas

- **Do not read `Ctrl2Asmb` from the vehicle's flight computer.** It is set on the worker and never copied back; read `Vehicle.Ctrl2Body` instead (it follows "Control From Here").
- **Key worker lookups by a shared reference** (`BurnPlan`, `VehicleConfig`): a table keyed by the vehicle's `FlightComputer` never matches the worker's copy.
- **Pulses write `CommandPulseTime` and only ever raise it**, so stock attitude pulses in the same step are kept.
- **Stock `UpdateBurnTarget` rewrites engine timing every tick**; the RCS writer overwrites `BurnDuration` and `IgnitionTime` with RCS timing after it.
- The LP solve and the part walk run on the main thread at most once per second; the worker only shapes immutable arrays. If the thruster list length changed since publishing, fall back to Groups.

## See also

- [Overview](overview.md); [trim](execute-burns-with-rcs-and-throttle-inputs-2.md); [reflection](execute-burns-with-rcs-and-throttle-inputs-3.md)
- [RCS-only burn execution through the stock Auto button](../../../../mods/ksa-advancedflightcomputer/features/rcs-burn-execution.md), [RCS jet selection with axis groups or a linear program](../../../../mods/ksa-advancedflightcomputer/features/rcs-thruster-allocation.md)
- [Execute planned burns hands-free with the stock Auto burn](../execute-planned-burns-with-the-stock-autopilot/overview.md)

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
