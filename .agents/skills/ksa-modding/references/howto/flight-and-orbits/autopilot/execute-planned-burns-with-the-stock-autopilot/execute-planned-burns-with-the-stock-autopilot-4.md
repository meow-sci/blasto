---
type: How-To
title: Detect the end of a stock burn with a RaisePendingAlerts prefix and postfix
description: Capture which planned burn the flight computer is ending in a prefix on RaisePendingAlerts and learn in the postfix whether stock kept it, using Harmony's __state to carry the record
category: flight-and-orbits
group: autopilot
topic: execute-planned-burns-with-the-stock-autopilot
approach: 4
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/stock-burn-end-detection]
techniques: [harmony-prefix, harmony-postfix]
hooks:
  - harmony-prefix KSA.FlightComputer.RaisePendingAlerts
  - harmony-postfix KSA.FlightComputer.RaisePendingAlerts
lifecycle: [frame:prepare]
game_types: [KSA.FlightComputer, KSA.BurnPlan, KSA.Burn, KSA.BurnTarget, KSA.Vehicle, KSA.Program]
tags: [burn-end, raise-pending-alerts, end-auto-burn, harmony-state, identity]
---

Outcome: a callback "this planned burn just ended, completed or out of propellant, and stock did or did not remove it" that several features share. KSA-AdvancedFlightComputer wraps
`FlightComputer.RaisePendingAlerts` for multi-pass and automatic removal.[Detect which planned burn stock just ended](../../../../mods/ksa-advancedflightcomputer/features/stock-burn-end-detection.md) Prerequisites: StarMap and Harmony.

## Background: how the game does it

The game has no burn-ended event. The vehicle worker sets two flags on its copy (`AutoBurnCompleted` in `UpdateBurnTarget`, `AutoBurnStoppedOutOfPropellant` in `ComputeControl`), `CopyFrom` brings them back, and
`RaisePendingAlerts(vehicle)` consumes them on the main thread: it clears each flag, raises the alert (`CompleteAutoBurn` or the propellant warning) and calls `EndAutoBurn`, which stops warp, resets the simulation
speed and removes the first executable burn **only when `BurnCount > 1`**.[^ksa-flightcomputer] [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1240-L1253) [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L505-L512) [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L540-L629)
The loaded `BurnTarget` (`fc.Burn`) is a separate object that outlives its plan entry.

## Lifecycle and timing

`Program.PrepareFrame` calls `RaisePendingAlerts` for every vehicle in frame after `RefreshVehiclesInFrame` and before `ExecuteNextVehicleSolvers`.[^ksa-program] [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2186-L2196)
The workers are joined, so the postfix may edit the `BurnPlan` directly and the next step, dispatched later in the same `PrepareFrame`, sees the change. Because the postfix runs after stock removed what it removes, the mod never deletes
a burn stock is about to delete as well. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Patch the method with a prefix and a postfix** that share state through Harmony's `__state`:

```csharp
// AdvancedFlightComputer/Core/SharedVehicleHooks.cs L122-L135
// FlightComputer.RaisePendingAlerts clears the completion and propellant-stop flags and runs FlightComputer.EndAutoBurn, which removes the first executable burn only while more than one burn is planned.
// The prefix records which burn the flags refer to before they are cleared, and the postfix sees whether stock kept that burn.
[HarmonyPatch(typeof(FlightComputer), nameof(FlightComputer.RaisePendingAlerts), new[] { typeof(Vehicle) })]
private static class PendingAlertsPatch
{
    static void Prefix(FlightComputer __instance, out StockBurnEnd __state)
        => __state = MultiPassEnabled || AutoRemoveEnabled ? StockBurnEnd.Capture(__instance) : default;

    static void Postfix(Vehicle vehicle, StockBurnEnd __state)
    {
        if (__state.Burn != null && !vehicle.IsDisposed)
            OnStockBurnEnded(vehicle, in __state);
    }
}
```

2. **Capture in the prefix** only when a flag is set: take `BurnPlan.FindFirstExecutableBurn()` (the burn `EndAutoBurn` would remove) and record whether it completed:

```csharp
// AdvancedFlightComputer/Core/SharedVehicleHooks.cs L179-L185
public static StockBurnEnd Capture(FlightComputer fc)
{
    if (!fc.AutoBurnCompleted && !fc.AutoBurnStoppedOutOfPropellant)
        return default;
    Burn? burn = fc.BurnPlan.FindFirstExecutableBurn();
    return StockBurnIdentity.IsLoaded(fc.Burn, burn) ? new StockBurnEnd(burn, fc.AutoBurnCompleted) : default;
}
```

3. **Check identity, not reference.** `BurnTarget.ImpulsiveInstant` matched against `Burn.ImpulseTime` within 0.5 s is wide enough for an edit the worker has not copied back yet and narrow enough to separate adjacent burns
   ([AdvancedFlightComputer/Core/StockBurnIdentity.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/StockBurnIdentity.cs)).
4. **Dispatch in the postfix** with `keptByStock` = the captured burn is still the first executable one. Run the sequence consumer first, then removal, each in its own try/catch with a warn-once:

```csharp
// AdvancedFlightComputer/Core/SharedVehicleHooks.cs L57-L86
// The end of a burn, after FlightComputer.RaisePendingAlerts has acted on it. The worker raises the flags for an engine Auto burn and the RCS executor raises them for an RCS burn, so both arrive here.
// MultiPass first, so a finished pass is advanced before AutoRemove looks at the plan.
internal static void OnStockBurnEnded(Vehicle vehicle, in StockBurnEnd ended)
{
    bool keptByStock = ReferenceEquals(vehicle.FlightComputer.BurnPlan.FindFirstExecutableBurn(), ended.Burn);
    if (MultiPassEnabled)
    {
        try
        {
            PassCompletionPatch.OnStockBurnEnded(vehicle, ended.Burn!, ended.Completed, keptByStock);
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce("stock-burn-ended-multipass:" + ex.GetType().Name,
                $"[AFC] MultiPass threw on the end of a stock burn for '{vehicle.Id}': {ex}");
        }
    }
    if (AutoRemoveEnabled && ended.Completed)
    {
        try
        {
            FinishedBurnRemover.OnBurnCompleted(vehicle, ended.Burn!);
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce("stock-burn-ended-autoremove:" + ex.GetType().Name,
                $"[AFC] AutoRemove threw on the end of a stock burn for '{vehicle.Id}': {ex}");
        }
    }
}
```

5. **Cover RCS burns.** The RCS executor sets the same two flags in its tick, so stock's alert, warp stop and removal, and your consumers, treat an RCS burn like an engine burn (see [Execute burns with RCS translation and throttle inputs from code](../execute-burns-with-rcs-and-throttle-inputs/overview.md)).

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Core/SharedVehicleHooks.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SharedVehicleHooks.cs) | `PendingAlertsPatch`, `StockBurnEnd`, `OnStockBurnEnded` dispatch |
| [AdvancedFlightComputer/Core/StockBurnIdentity.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/StockBurnIdentity.cs) | loaded-target identity by impulse time |
| [AdvancedFlightComputer/Features/MultiPass/PassCompletionPatch.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/PassCompletionPatch.cs) | consumer: advances multi-pass executions |
| [AdvancedFlightComputer/Features/AutoRemove/FinishedBurnRemover.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoRemove/FinishedBurnRemover.cs) | consumer: removes the last finished burn |

## Variations and gotchas

- **Do not match by reference.** `fc.Burn` is a separate `BurnTarget`, and `DeleteInvalidBurns` runs before `CopyFrom` writes the worker's target back, so it can outlive its plan entry.
- **Prefix state must be a struct** carried via `out` / `__state`; an exception in either half propagates into `PrepareFrame`, so wrap consumers.
- The mod enables the capture only while a consumer is on (`MultiPassEnabled || AutoRemoveEnabled`), keeping the prefix free otherwise.
- `CompleteAutoBurn` also replaces the next-burn alert text; do not rely on alert text for logic.

## See also

- [Overview](overview.md); [state machine](execute-planned-burns-with-the-stock-autopilot-1.md); [removal](execute-planned-burns-with-the-stock-autopilot-5.md)
- [Detect which planned burn stock just ended](../../../../mods/ksa-advancedflightcomputer/features/stock-burn-end-detection.md)

[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
