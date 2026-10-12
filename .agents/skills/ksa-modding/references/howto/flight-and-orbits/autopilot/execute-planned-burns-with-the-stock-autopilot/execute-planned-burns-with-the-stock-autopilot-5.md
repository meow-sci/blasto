---
type: How-To
title: Remove the last finished burn from the plan with a RaisePendingAlerts postfix
description: Delete the stale node stock leaves behind after the last planned burn completes, at the one moment where doing so cannot also delete the next burn
category: flight-and-orbits
group: autopilot
topic: execute-planned-burns-with-the-stock-autopilot
approach: 5
difficulty: beginner
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/auto-remove-finished-burns]
techniques: [harmony-postfix]
hooks: [harmony-postfix KSA.FlightComputer.RaisePendingAlerts]
lifecycle: [frame:prepare]
game_types: [KSA.FlightComputer, KSA.BurnPlan, KSA.Burn, KSA.Vehicle]
tags: [burn-plan, remove-burn, raise-pending-alerts, auto-burn, cleanup]
---

Outcome: after an Auto burn finishes, the node is gone from the plan even when it was the last one. Stock removes a finished burn only when another burn follows it; KSA-AdvancedFlightComputer
removes the last one too, for engine and RCS burns, on every vehicle.[Remove the last finished Auto burn](../../../../mods/ksa-advancedflightcomputer/features/auto-remove-finished-burns.md) Prerequisites: StarMap and Harmony; build on
[burn-end detection](execute-planned-burns-with-the-stock-autopilot-4.md).

## Background: how the game does it

`RaisePendingAlerts` consumes `AutoBurnCompleted` and, through `CompleteAutoBurn` and `EndAutoBurn`, removes the first executable burn only when `BurnPlan.BurnCount > 1`.[^ksa-flightcomputer]
[current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L540-L629) Removal is `FlightComputer.RemoveBurn(burn)`. Burn equality in the plan is by time and delta-v, so resolve the plan's own instance with `BurnPlan.TryGetBurnIndex` and `TryGetBurn(index, ...)` before removing.[current/decomp/KSA/BurnPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnPlan.cs#L201-L210)
[current/decomp/KSA/BurnPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnPlan.cs#L321)

## Lifecycle and timing

`frame:prepare`: the postfix runs per vehicle after the input drain and before the next step is dispatched; workers are joined, so editing `BurnPlan` is safe and the next step sees it. Acting before `RaisePendingAlerts` (for example
right after the solver apply) is the wrong moment: stock would then remove the next burn as well. That was the bug of the older standalone mod, which removed the finished burn first. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Get the ended burn** from the shared prefix/postfix capture ([approach 4](execute-planned-burns-with-the-stock-autopilot-4.md)), and call your remover only for `Completed` burns (burns that stopped for lack of propellant stay).
2. **Re-resolve the burn** in the plan and remove it only if it is still there (stock or multi-pass may have removed it):

```csharp
// AdvancedFlightComputer/Features/AutoRemove/FinishedBurnRemover.cs L7-L28
// Removes the finished burn stock leaves in the plan.
// FlightComputer.EndAutoBurn removes a finished burn itself while another burn follows it, so what reaches this class still planned is the last burn of the plan. Engine auto-burns and RCS burns end the same way, because the RCS executor raises FlightComputer.AutoBurnCompleted too.
internal static class FinishedBurnRemover
{
    // Called after FlightComputer.RaisePendingAlerts, so a burn still in the plan is one stock kept.
    // Every vehicle, like the stock removal, and only while the burn is still in the plan, because MultiPass can have removed its own pass first.
    // Burn equality is by time and delta-V, so the plan's own instance is resolved and removed.
    internal static void OnBurnCompleted(Vehicle vehicle, Burn burn)
    {
        if (!AutoRemoveConfig.Enabled)
            return;
        FlightComputer fc = vehicle.FlightComputer;
        int index = fc.BurnPlan.TryGetBurnIndex(burn);
        if (index < 0 || !fc.BurnPlan.TryGetBurn(index, out Burn? planBurn) || planBurn == null)
            return;

        if (DebugConfig.AutoRemove)
            DefaultCategory.Log.Debug(
                $"[AFC] AutoRemove: '{vehicle.Id}' finished its last planned burn (dv={planBurn.DeltaVVlf.Length():F2}m/s); removing it from the plan.");

        fc.RemoveBurn(planBurn);
    }
```

3. **Order relative to other consumers.** Run multi-pass first so a finished pass advances before removal looks at the plan.
4. **Add one setting** (on by default) persisted in a small TOML under the mod's documents folder (built from `Constants.DocumentsFolderPath`); import the old standalone mod's file once if yours has none.
5. **Warn about the old mod.** If the standalone `AutoRemoveFinishedBurns.Mod` type is loaded, warn: it removes the finished burn before `RaisePendingAlerts`, so stock then removes the next burn too.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/AutoRemove/FinishedBurnRemover.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoRemove/FinishedBurnRemover.cs) | the removal |
| [AdvancedFlightComputer/Features/AutoRemove/AutoRemoveFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoRemove/AutoRemoveFeature.cs) | block, settings section, standalone warning |
| [AdvancedFlightComputer/Features/AutoRemove/AutoRemoveConfig.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoRemove/AutoRemoveConfig.cs) | one-key TOML and import |
| [tests/AdvancedFlightComputer.HarnessTests/Features/AutoRemove/AutoRemoveBurnTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/AutoRemove/AutoRemoveBurnTest.cs) | headless test with an engine burn |

## Variations and gotchas

- **Only when the burn is the last.** A burn that stock kept is exactly the last planned burn; anything else was already removed by `EndAutoBurn`.
- **Manual burns stay**, since they never set `AutoBurnCompleted`.
- **Removal edits shared state.** `RemoveBurn` is safe here only because the workers are joined; never call it from a draw hook.
- RCS burns finish through the same flags, so the same remover covers them.

## See also

- [Overview](overview.md); [burn-end detection](execute-planned-burns-with-the-stock-autopilot-4.md)
- [Remove the last finished Auto burn](../../../../mods/ksa-advancedflightcomputer/features/auto-remove-finished-burns.md)
- [Create, chain and read stock maneuver nodes from code](../create-and-read-maneuver-nodes/overview.md)

[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
