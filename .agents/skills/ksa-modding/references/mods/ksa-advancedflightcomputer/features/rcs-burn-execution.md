---
type: Mod Feature
title: RCS-only burn execution through the stock Auto button
description: Intercept the Auto burn-mode request, drive the burn from a main-thread state machine, hand an immutable command to the vehicle worker keyed by the shared BurnPlan, and end it through the stock completion flags
mod: ksa-advancedflightcomputer
category: flight-and-orbits
techniques: [harmony-prefix, harmony-postfix, conditional-weak-table, save-sidecar]
hooks:
  - harmony-prefix KSA.Vehicle.SetEnum
  - harmony-postfix KSA.Universe.ApplyVehicleSolvers
  - harmony-postfix KSA.FlightComputer.ComputeControl
  - harmony-prefix KSA.Universe.AutoWarpTo
lifecycle: [frame:input, frame:apply-solvers, physics:vehicle-solver]
game_types:
  - KSA.Vehicle
  - KSA.FlightComputer
  - KSA.BurnTarget
  - KSA.BurnPlan
  - KSA.FlightComputerOutput
  - KSA.Universe
tags: [rcs, maneuver-node, auto-burn, worker-thread, time-warp, closed-loop]
---

The player plans a burn as usual and presses the stock **Auto** button. If the vehicle has no active,
fueled main engine (or the burn's execution override says RCS), AFC flies the burn by translating
with RCS thrusters instead of handing it to the engine autopilot, and ends it exactly like an engine
burn: time warp stops, the stock alert shows, the next burn loads. The mechanism splits work between
a main-thread executor and an allocation-free worker-side writer.

## How it works

1. **Intercept the trigger.** A prefix on `Vehicle.SetEnum(Enum)` (the sink of the gauge Auto button
   and the burn-mode hotkeys) looks only at `FlightComputerBurnMode` values. Auto on a burn that
   resolves to RCS activates the executor and returns `false` (stock never arms Auto); a click while
   an execution runs cancels it ([AdvancedFlightComputer/Features/RcsTranslation/RcsDriverPatch.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsDriverPatch.cs#L28-L42),
   [AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.cs#L141-L189)).
2. **Resolve the method.** Default resolves to the engine when `IsAnyEngineActive()` and
   `IsAnyEnginePropellantAvailable()`, otherwise RCS; a per-burn override (Default/Engine/RCS)
   comes from the burn editor ([AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.cs#L49-L56)).
   Activation refuses (with an alert, before touching controls) a burn with no delta-v, no usable
   translation, or no axis that serves its direction.
3. **Take control.** The executor claims the vehicle, holds Manual through `StockBurnMode`, enables
   RCS if needed, takes the attitude once (rate hold for Hold, a burn-relative target for Align) and
   records what to restore (see [vehicle control ownership](vehicle-control-ownership.md)).
4. **Main-thread state machine.** `RcsExecutor.Tick` runs in the
   [post-solver vehicle tick](post-solver-vehicle-tick.md). It cancels when the burn was deleted or
   reordered (identity by impulse time), refreshes thruster capability and estimates once per
   second, watches progress (no-progress and align timeouts), detects attitude takeovers, and ends the
   burn when the game's own `BurnTarget.DeltaVToGoCci` is inside the thrusters' minimum impulse
   ([AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.cs#L699-L760)).
5. **Hand-off to the worker.** Each tick publishes an immutable `RcsWorkerCommand` (ignition time,
   attitude gate, per-axis forces and minimum correcting impulses, or an LP firing pattern). The key
   is the `BurnPlan`, because `FlightComputer.CopyFrom` copies the `BurnPlan` reference into the
   worker's flight computer while the flight computer objects differ; a `ConditionalWeakTable` keyed
   on it needs no locking on the read side ([AdvancedFlightComputer/Features/RcsTranslation/RcsWorkerCommand.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsWorkerCommand.cs#L40-L59),
   [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L181-L201)).[^ksa-flightcomputer] All values that could throw (such
   as `UniverseTime` from NaN) are built on the main thread.
6. **Worker writer.** `RcsComputeControlPatch.Command` runs from the
   [command sink](flight-computer-command-sink.md) after stock `ComputeControl`. It zeroes engine
   commands, replaces the burn target's engine timing with the remaining RCS duration, waits for
   ignition (requesting a wake), checks the Align gate, converts the to-go delta-v into a control-frame
   impulse and fires: each axis pulse is capped at one control period (0.1 s) and suppressed below the
   minimum correcting impulse, so pulses shrink as the remaining delta-v approaches zero
   ([AdvancedFlightComputer/Features/RcsTranslation/RcsComputeControlPatch.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsComputeControlPatch.cs#L12-L70)).
   Thruster commands are written as `CommandPulseTime` (see [RCS thruster allocation](rcs-thruster-allocation.md)).
7. **End like stock.** `Complete` releases control and then sets `fc.AutoBurnCompleted = true`;
   `StopOutOfPropellant` sets `AutoBurnStoppedOutOfPropellant`. Stock `RaisePendingAlerts` then stops
   warp, shows the alert and removes or keeps the burn as for an engine burn
   ([AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.cs#L915-L952)).
8. **Warp margin.** A prefix on `Universe.AutoWarpTo(UniverseTime, double, bool)` enlarges only the
   margin of warps aimed at the loaded burn's ignition time, so warp stops early enough for an Align
   slew ([AdvancedFlightComputer/Features/RcsTranslation/RcsWarpPatch.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsWarpPatch.cs)).
9. **Persistence.** Per-burn options and the live execution (including `attitude_yielded`) are saved
   in `rcs-exec.toml`, keyed by save and vehicle (see [save-scoped registries](save-scoped-sidecar-registries.md)).

## Lifecycle and timing

| Step | Where | Thread |
|---|---|---|
| Auto click or hotkey | `Vehicle.SetEnum` prefix; a gauge click is queued in `InputEvents.FlightComputerInputBuffer` during the draw and applied in `InputEvents.ApplyInputEvents` (`frame:input`) ([current/decomp/KSA/GaugeButtonFlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonFlightComputer.cs#L130-L142), [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L592-L605)) | Main |
| State machine, publish command, raise end flags | `Universe.ApplyVehicleSolvers` postfix (`frame:apply-solvers`) | Main |
| Stock reacts to the flags | `FlightComputer.RaisePendingAlerts` later in `PrepareFrame` | Main |
| Pulses | `FlightComputer.ComputeControl` postfix (`physics:vehicle-solver`) | Vehicle worker |

The executor's comment spells out why the flag survives: the driver runs after `FlightComputer.CopyFrom`
wrote the worker results back, and `Vehicle.PrepareWorker` copies the flight computer to the next
step only after `RaisePendingAlerts` cleared the flag.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/RcsTranslation/RcsDriverPatch.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsDriverPatch.cs) | `SetEnum` interception and the per-vehicle tick entry |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.cs) | Main-thread executor: resolution, activation, control, completion |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.Faults.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.Faults.cs) | Fault cleanup and retry |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsWorkerCommand.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsWorkerCommand.cs) | Immutable worker command and `BurnPlan`-keyed channel |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsComputeControlPatch.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsComputeControlPatch.cs) | Worker-side pulse writer |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsWarpPatch.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsWarpPatch.cs) | Warp margin for align |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsExecution.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsExecution.cs) | Per-vehicle execution state |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsCapability.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsCapability.cs) | Thruster and propellant capability snapshot |

## Snippets

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

## Game APIs touched

| Member | Use |
|---|---|
| `Vehicle.SetEnum(Enum)` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6321)) | Burn-mode requests from button and hotkeys |
| `FlightComputer.CopyFrom` ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L181)) | Shares `BurnPlan` with the worker copy (channel key) |
| `BurnTarget.DeltaVToGoCci`, `DeltaVTargetCci`, `BurnDuration`, `IgnitionTime`, `ImpulsiveInstant` ([current/decomp/KSA/BurnTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnTarget.cs)) | Closed-loop progress and timing |
| `FlightComputer.AutoBurnCompleted`, `AutoBurnStoppedOutOfPropellant`, `RCSMode`, `LastThrustTime`, `ErrorAngles`, `AngleDeadband` | Completion flags, RCS enable, align gate |
| `Universe.AutoWarpTo(UniverseTime, double, bool)` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs)) | Warp-to-burn margin |

## Pitfalls and version notes

- Returning `false` from a `SetEnum` prefix also skips stock's navball-frame update; AFC restores the
  frame itself on the paths that skip it.
- `FlightComputer.ComputeControl` stops a dry Auto burn only from its ignition time; AFC mirrors that
  so the player can refuel during the coast.
- A zero `DeltaVTargetCci` satisfies the stock completion dot product without delivering delta-v;
  treat it as a withdrawn burn.
- Estimates for later burns use the current vehicle; stock only loads the first executable burn as
  its target, so staging between burns is not forecast.

## See also

- [RCS thruster allocation](rcs-thruster-allocation.md)
- [RCS burn editor and gauge UI](rcs-burn-editor-and-gauge-ui.md)
- [Stock burn end detection](stock-burn-end-detection.md)
- [Multi-pass execution](multi-pass-execution.md) (passes can fly as RCS burns)
- [Flight-computer worker copy channels](flight-computer-worker-copy-channels.md) (why the channel is keyed by `BurnPlan`)
- [Intercept the Auto burn-mode request and fly the burn with RCS only](../../../howto/flight-and-orbits/autopilot/execute-burns-with-rcs-and-throttle-inputs/execute-burns-with-rcs-and-throttle-inputs-1.md) - how-to article demonstrating this feature

[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
