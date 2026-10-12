---
type: Mod Feature
title: Detect which planned burn stock just ended
description: A prefix on FlightComputer.RaisePendingAlerts captures the completed or propellant-stopped burn in Harmony state before stock clears the flags, and the postfix learns whether EndAutoBurn kept it
mod: ksa-advancedflightcomputer
category: flight-and-orbits
techniques: [harmony-prefix, harmony-postfix]
hooks:
  - harmony-prefix KSA.FlightComputer.RaisePendingAlerts
  - harmony-postfix KSA.FlightComputer.RaisePendingAlerts
lifecycle: [frame:prepare]
game_types: [KSA.FlightComputer, KSA.BurnPlan, KSA.Burn, KSA.BurnTarget, KSA.Program]
tags: [burns, auto-burn, flight-computer, harmony-state, burn-plan]
---

Several AFC features react to "an Auto burn just ended": multi-pass schedules the next pass,
automatic burn removal deletes the last finished burn. The game has no event for this. It signals
the end with two flags the vehicle worker sets, consumes them in `RaisePendingAlerts`, and
`EndAutoBurn` may remove the burn from the plan, all in one call. AFC wraps that call: the prefix
records which burn ended and how, the postfix sees what stock did with it.

## How it works

1. **How stock ends a burn.** On the vehicle worker, `FlightComputer.UpdateBurnTarget` sets
   `AutoBurnCompleted = true` and drops `BurnMode` to Manual when the delta-v to go is used up
   ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1242-L1253)); `ComputeControl` sets
   `AutoBurnStoppedOutOfPropellant = true` (and Manual) when every active engine is dry
   ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L507-L512)). Later, on the main thread, `RaisePendingAlerts(vehicle)`
   clears each flag, shows the alert and calls the private `EndAutoBurn`, which stops warp, resets
   the simulation speed and removes the first executable burn **only when the plan holds more than
   one burn** ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L542-L603)).[^ksa-flightcomputer]
2. **Prefix with `__state`.** `StockBurnEnd.Capture(fc)` returns nothing unless a flag is set; it then
   takes `BurnPlan.FindFirstExecutableBurn()` (the burn `EndAutoBurn` would remove) and records it
   with `Completed = AutoBurnCompleted`. The record goes into Harmony's per-call `__state`
   ([AdvancedFlightComputer/Core/SharedVehicleHooks.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SharedVehicleHooks.cs#L122-L135)).
3. **Identity check.** The flight computer's loaded `BurnTarget` is a separate object that outlives
   its plan entry, so a reference is not proof. `StockBurnIdentity.IsLoaded` matches
   `BurnTarget.ImpulsiveInstant` against `Burn.ImpulseTime` within 0.5 s, wide enough for an edit the
   worker has not copied back yet and narrow enough to separate adjacent burns
   ([AdvancedFlightComputer/Core/StockBurnIdentity.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/StockBurnIdentity.cs)).
4. **Postfix.** If a burn was captured and the vehicle is not disposed,
   `OnStockBurnEnded(vehicle, ended)` computes `keptByStock` (is the captured burn still the first
   executable one?) and dispatches: multi-pass first, so a finished pass advances before automatic
   removal looks at the plan, then automatic removal for completed burns. Each consumer is wrapped in
   try/catch with a warn-once ([AdvancedFlightComputer/Core/SharedVehicleHooks.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SharedVehicleHooks.cs#L57-L86)).
5. **RCS burns reuse the path.** The RCS executor sets the same two flags itself in the post-solver
   tick, so stock's alert, warp stop and removal, and AFC's consumers, all treat an RCS burn like an
   engine burn (see [RCS burn execution](rcs-burn-execution.md)).

## Lifecycle and timing

`Program.PrepareFrame` calls `vehicle.FlightComputer.RaisePendingAlerts(vehicle)` for every vehicle
in the frame after `InputEvents.ApplyInputEvents` and `RefreshVehiclesInFrame`, and before
`ExecuteNextVehicleSolvers` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2189-L2195)).[^ksa-program] The workers are joined,
so the postfix may edit the `BurnPlan` directly; the next step, dispatched later in the same
`PrepareFrame`, sees the change. Because the postfix runs after stock removed what it removes, AFC
never deletes a burn that stock is about to delete as well.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Core/SharedVehicleHooks.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SharedVehicleHooks.cs) | `PendingAlertsPatch`, `StockBurnEnd`, `OnStockBurnEnded` dispatch |
| [AdvancedFlightComputer/Core/StockBurnIdentity.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/StockBurnIdentity.cs) | Loaded-target identity by impulse time |
| [AdvancedFlightComputer/Features/MultiPass/PassCompletionPatch.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/PassCompletionPatch.cs) | Consumer: advances multi-pass executions |
| [AdvancedFlightComputer/Features/AutoRemove/FinishedBurnRemover.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoRemove/FinishedBurnRemover.cs) | Consumer: removes the last finished burn |

## Snippets

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

## Game APIs touched

| Member | Use |
|---|---|
| `FlightComputer.RaisePendingAlerts(Vehicle)` ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L542)) | Hooked; consumes the flags and ends the burn |
| `FlightComputer.EndAutoBurn()` (private, [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L589)) | Removes the first executable burn only if `BurnCount > 1` |
| `FlightComputer.AutoBurnCompleted`, `AutoBurnStoppedOutOfPropellant`, `Burn` | Flags and loaded target |
| `BurnPlan.FindFirstExecutableBurn()` ([current/decomp/KSA/BurnPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnPlan.cs)) | The burn stock ends |
| `BurnTarget.ImpulsiveInstant` ([current/decomp/KSA/BurnTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnTarget.cs)), `Burn.ImpulseTime` ([current/decomp/KSA/Burn.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Burn.cs#L113)) | Identity match |

## Pitfalls and version notes

- Read the flags in the prefix: stock clears them inside the method.
- Stock keeps the last burn of a plan (it only removes when another burn follows), which is why
  automatic removal exists; check `keptByStock` before removing anything yourself.
- For interstellar burns `ImpulseTime` is half the burn after `Burn.Time`; compare against
  `ImpulseTime`, as `BurnTarget.UpdateFromBurn` does.
- The worker raises at most one flag per frame (it drops to Manual with it), so one record per call
  is enough.

## See also

- [Post-solver vehicle tick](post-solver-vehicle-tick.md)
- [Multi-pass execution](multi-pass-execution.md)
- [Automatic removal of finished burns](auto-remove-finished-burns.md)
- [Vehicle control ownership](vehicle-control-ownership.md)
- [Detect the end of a stock burn with a RaisePendingAlerts prefix and postfix](../../../howto/flight-and-orbits/autopilot/execute-planned-burns-with-the-stock-autopilot/execute-planned-burns-with-the-stock-autopilot-4.md) - how-to article demonstrating this feature

[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
