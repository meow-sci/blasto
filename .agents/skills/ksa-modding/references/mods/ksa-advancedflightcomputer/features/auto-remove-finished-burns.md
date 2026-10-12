---
type: Mod Feature
title: Remove the last finished Auto burn
description: Remove the completed burn that stock keeps when it is the last one in the plan, acting in a RaisePendingAlerts postfix so it never races stock's own removal
mod: ksa-advancedflightcomputer
category: flight-and-orbits
techniques: [harmony-postfix, toml-config]
hooks: [harmony-postfix KSA.FlightComputer.RaisePendingAlerts]
lifecycle: [frame:prepare]
game_types: [KSA.FlightComputer, KSA.BurnPlan, KSA.Burn, KSA.Constants]
tags: [burns, burn-plan, auto-burn, cleanup, settings, toml]
---

When an Auto burn finishes, stock removes it from the plan only if another burn follows it, so the
last burn of a plan stays behind as a stale node. AFC removes that last finished burn too, for engine
and RCS burns, on every vehicle. Manual burns and burns that stopped for lack of propellant stay.
The feature is small; its value is in hooking the right moment so it never deletes a burn stock is
about to delete as well (the bug that made the old standalone mod also delete the next planned burn).

## How it works

1. Stock: `FlightComputer.RaisePendingAlerts` consumes `AutoBurnCompleted` and calls the private
   `EndAutoBurn`, which removes the first executable burn only when `BurnPlan.BurnCount > 1`
   ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L542-L603)).[^ksa-flightcomputer]
2. AFC's shared [stock burn end detection](stock-burn-end-detection.md) captures the ended burn in a
   prefix and, in the postfix, calls `FinishedBurnRemover.OnBurnCompleted` for completed burns, after
   multi-pass has advanced its own pass.
3. The remover looks the burn up again in the plan with `BurnPlan.TryGetBurnIndex` (burn equality is
   by time and delta-v, so it resolves the plan's own instance) and removes it with
   `FlightComputer.RemoveBurn`. A burn that is no longer planned (stock or multi-pass removed it) is
   left alone ([AdvancedFlightComputer/Features/AutoRemove/FinishedBurnRemover.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoRemove/FinishedBurnRemover.cs)).
4. Settings: one switch, on by default, on the Mods settings page; persisted in
   `Documents/My Games/Kitten Space Agency/mods/AdvancedFlightComputer/autoremove.toml` (built from
   the game's `Constants.DocumentsFolderPath`). The old standalone mod's file is copied once if AFC
   has none ([AdvancedFlightComputer/Features/AutoRemove/AutoRemoveConfig.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoRemove/AutoRemoveConfig.cs)).
5. If the standalone `AutoRemoveFinishedBurns.Mod` type is loaded, AFC warns: that mod removes the
   finished burn before `RaisePendingAlerts`, so stock then removes the next burn as well
   ([AdvancedFlightComputer/Features/AutoRemove/AutoRemoveFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoRemove/AutoRemoveFeature.cs#L21-L28)).

## Lifecycle and timing

`RaisePendingAlerts` runs per vehicle in `Program.PrepareFrame` after the input drain and before the
next vehicle step is dispatched (`frame:prepare`). Workers are joined, so editing `BurnPlan` there is
safe, and the next step sees the edited plan. Acting before `RaisePendingAlerts` (for example right
after the solver apply) is the wrong moment: stock would then remove the next burn too.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/AutoRemove/FinishedBurnRemover.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoRemove/FinishedBurnRemover.cs) | The removal |
| [AdvancedFlightComputer/Features/AutoRemove/AutoRemoveFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoRemove/AutoRemoveFeature.cs) | Block, settings section, standalone warning |
| [AdvancedFlightComputer/Features/AutoRemove/AutoRemoveConfig.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoRemove/AutoRemoveConfig.cs) | One-key TOML and import |
| [AdvancedFlightComputer/Core/SharedVehicleHooks.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SharedVehicleHooks.cs#L57-L86) | Dispatch after `RaisePendingAlerts` |
| [tests/AdvancedFlightComputer.HarnessTests/Features/AutoRemove/AutoRemoveBurnTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/AutoRemove/AutoRemoveBurnTest.cs) | Headless test with an engine burn |
| [tests/AdvancedFlightComputer.HarnessTests/Features/AutoRemove/AutoRemoveRcsTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/AutoRemove/AutoRemoveRcsTest.cs) | Headless test with an RCS burn |

## Snippets

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

## Game APIs touched

| Member | Use |
|---|---|
| `FlightComputer.RaisePendingAlerts`, `EndAutoBurn` ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L542)) | Stock end of burn and its removal rule |
| `BurnPlan.TryGetBurnIndex`, `TryGetBurn` ([current/decomp/KSA/BurnPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnPlan.cs#L321)) | Resolve the plan's instance |
| `FlightComputer.RemoveBurn(Burn)` ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L2194)) | Removal |
| `Constants.DocumentsFolderPath` ([current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281)) | Base folder for the config file |

## Pitfalls and version notes

- Do not remove burns that stopped for lack of propellant: stock keeps them so the player can refuel
  and engage again.
- Removing from `FlightComputer` (not only from `BurnPlan`) keeps the flight computer's loaded target
  consistent; `BurnPlan.RemoveBurnFromFlightComputerOnly` exists for a different purpose.

## See also

- [Stock burn end detection](stock-burn-end-detection.md)
- [Multi-pass execution](multi-pass-execution.md)
- [Mods settings page sections](mods-settings-page-sections.md)
- [Remove the last finished burn from the plan with a RaisePendingAlerts postfix](../../../howto/flight-and-orbits/autopilot/execute-planned-burns-with-the-stock-autopilot/execute-planned-burns-with-the-stock-autopilot-5.md) - how-to article demonstrating this feature

[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
