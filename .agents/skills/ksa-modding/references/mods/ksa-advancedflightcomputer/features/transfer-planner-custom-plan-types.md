---
type: Mod Feature
title: Custom plan types in the stock Transfer Planner
description: Insert mod entries into TransferPlanner.TransferTypes and take over the window for those keys with a skip-original prefix that redraws the stock window under the same id, title and signature
mod: ksa-advancedflightcomputer
category: flight-and-orbits
techniques: [harmony-prefix, harmony-postfix, reflection, imgui-window]
hooks:
  - harmony-prefix KSA.TransferPlanner.DrawPlanWindow
  - harmony-postfix KSA.TransferPlanner.DrawPlanWindow
  - reflection KSA.TransferPlanner._transferType
  - reflection KSA.TransferPlanner._showPlanWindow
  - reflection KSA.TransferPlanner.SetTransferInfo
lifecycle: [startup:all-mods-loaded, frame:draw-ui, event:mod-unload]
game_types: [KSA.TransferPlanner, KSA.TransferType, KSA.ConsoleStyle, KSA.ConsoleWidgets, KSA.OrbitalTransfers]
tags: [transfer-planner, maneuver-planning, imgui, prefix, plan-types]
---

AFC adds **Set Periapsis**, **Set Apoapsis**, **Match Inclination** and **Set Inclination** to the
PLAN TYPE dropdown of the stock Transfer Planning window, and also takes over the two stock
circularize types so they can be split into passes. The player never sees a second window: picking an
AFC type swaps the window body in place, in the same position and size.

## How it works

1. **Add the entries.** `TransferPlanner.TransferTypes` is a public static `List<TransferType>`
   (`Hohmann`, `Circularize Apoapsis`, `Circularize Periapsis`, `Interstellar`) that stock's combo
   iterates ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L53-L59)). `ManeuverTools.InjectTransferTypes` inserts four
   `new TransferType(key, name)` after the last circularize entry (or at the end if those are gone),
   guarded against double insertion ([AdvancedFlightComputer/Features/ManeuverTools/ManeuverTools.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/ManeuverTools.cs#L22-L44)).
2. **Take over the window.** A prefix on `TransferPlanner.DrawPlanWindow(IGameViewport)` reads the
   selected type through the validated `_transferType` accessor. For any other key it returns `true`
   and stock draws. For a handled key it draws its own window and returns `false`, also after an
   exception, so stock never begins the same window after a partial AFC draw
   ([AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.cs#L39-L68)).
3. **Same window identity.** AFC calls `ConsoleStyle.BeginWindow("transfer-planning", "TRANSFER PLANNING", "KSA-TRJ", ...)`
   with the stock id, title, signature and size, so ImGui keeps one window and its placement when the
   player switches between stock and AFC types ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L325-L333)). The body
   follows the stock layout (`BeginBody`, `PushWidgetStyle`, region headers, rows, footer with a
   primary CREATE button) using [stock-styled rows](stock-styled-console-ui.md).
4. **Same dropdown.** The PLAN TYPE row iterates the same `TransferPlanner.TransferTypes` list. On a
   change, `StockPlanner.SelectType` replays the reset stock applies in its own combo: clear
   `_transferCalculated`, the destination and the selected porkchop entry, pick the starting time unit
   and invoke the private `SetTransferInfo` ([AdvancedFlightComputer/Core/StockPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/StockPlanner.cs#L70-L83)).
   AFC does not switch while a stock porkchop calculation runs, because only stock's combo cancels
   that worker.
5. **Close semantics.** The `_showPlanWindow` field is written directly while open; closing goes
   through the public `TransferPlanner.ShowPlanWindow` setter because it also clears stock's transfer
   state.
6. **Postfix.** A postfix on the same method draws AFC's world-space plan markers for every type
   (see [planner preview trajectories](planner-preview-trajectories.md)).
7. **Undo.** If the window patch is missing, `DisableManeuverTools` removes the injected types so the
   dropdown never offers a type with no window body; on unload the types are removed and, if one is
   selected, the first stock type is selected ([AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L153-L158)).

## Lifecycle and timing

- Injection: `[StarMapAllModsLoaded]`, inside the ManeuverTools patch block. `TransferTypes` is a
  static initializer, so it already exists.
- Drawing: `TransferPlanner.DrawPlanWindow` is called from `Program.OnDrawUiThreadSafe`
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3232-L3236)),[^ksa-program] main thread, `frame:draw-ui`, only while the HUD is
  drawn. Planner state is static and main-thread only; the stock porkchop and refine tasks run on the
  `ThreadPool` and publish results into `TransferInfo`, which is why AFC checks
  `_transferBeingCalculated` before resetting state.
- Removal: `[StarMapUnload]`.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/ManeuverTools/ManeuverTools.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/ManeuverTools.cs) | Keys, injection, removal, handled-type checks |
| [AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.cs) | Skip-original prefix, window and body, postfix |
| [AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.Commit.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.Commit.cs) | Footer states and CREATE |
| [AdvancedFlightComputer/Core/StockPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/StockPlanner.cs) | Typed access to private planner state, `SelectType` |
| [AdvancedFlightComputer/Features/ManeuverTools/ManeuverToolsWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/ManeuverToolsWindow.cs) | Inputs for the four AFC types |

## Snippets

```csharp
// AdvancedFlightComputer/Features/ManeuverTools/ManeuverTools.cs L22-L39
    public static void InjectTransferTypes()
    {
        var types = TransferPlanner.TransferTypes;

        if (types.Exists(t => t.GetKey() == KeySetPeriapsis))
            return;

        // The orbit tools follow stock's circularize entries, ahead of the interstellar planner, and go to the end if those entries are gone.
        int insertAt = types.FindLastIndex(t => IsCircularizeType(t.GetKey())) + 1;
        if (insertAt == 0)
            insertAt = types.Count;
        types.InsertRange(insertAt,
        [
            new TransferType(KeySetPeriapsis, "Set Periapsis"),
            new TransferType(KeySetApoapsis, "Set Apoapsis"),
            new TransferType(KeyMatchInclination, "Match Inclination"),
            new TransferType(KeySetInclination, "Set Inclination"),
        ]);
```

```csharp
// AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.cs L39-L68
    static bool Prefix(IGameViewport inViewport)
    {
        if (StockPlanner.TransferType is not TransferType transferType)
            return true;

        if (!ManeuverToolsFeature.IsHandledType(transferType.GetKey()))
        {
            DropPlanState();
            return true;
        }

#if DEBUG
        using var _perf = new PerfTracker.Scope("Patch_DrawPlanWindow.Prefix");
#endif

        try
        {
            DrawWindow(inViewport, transferType);
        }
        catch (Exception ex)
        {
            // Separate plan types must not silence each other after a draw failure.
            string typeKey = transferType.GetKey();
            LogHelper.WarnOnce($"maneuvertools-draw:{typeKey}:{ex.GetType().Name}",
                $"[AFC] ManeuverTools Prefix (plan type '{typeKey}', "
                + $"source '{_lastSource?.Id ?? "none"}'): {ex}");
            // Do not let stock begin the same window after a partial AFC draw.
        }
        return false;
    }
```

## Game APIs touched

| Member | Use |
|---|---|
| `TransferPlanner.TransferTypes` ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L53)) | Public list behind the PLAN TYPE combo |
| `TransferType(string key, string name)` ([current/decomp/KSA/TransferType.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferType.cs)) | Entry type (`IComboable`) |
| `TransferPlanner.DrawPlanWindow(IGameViewport)` ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L305)) | Prefixed and postfixed window |
| `TransferPlanner._transferType`, `_showPlanWindow`, `_transferCalculated`, `_transferBeingCalculated`, `SetTransferInfo` | Private state and reset |
| `ConsoleStyle.BeginWindow`, `BeginBody`, `BeginFooter`, `EndWindow` ([current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs)) | Stock console window frame |

## Pitfalls and version notes

- Return `false` from the prefix even when your draw threw, or stock opens a second `Begin` on the same
  ImGui window in the same frame.
- Do not cache `TransferType` instances across unload; remove your entries and reselect a stock type,
  because stock cannot draw a body for a key it does not know.
- `DrawPlanWindow` also carries the transpiler of
  [plan window transpiler injection](plan-window-transpiler-injection.md); both patch the same method
  from different classes in different owners.

## See also

- [Apse and inclination quick tools](apse-and-inclination-quick-tools.md)
- [Planner burn node creation](planner-burn-node-creation.md)
- [Orbit context menu shortcuts](orbit-context-menu-shortcuts.md)
- [Validated reflection handles](validated-reflection-handles.md)
- [Register plan types and redraw the stock window in a skip-original prefix under the same id, title and signature](../../../howto/flight-and-orbits/transfers/add-plan-type-to-the-transfer-planner/add-plan-type-to-the-transfer-planner-1.md) - how-to article demonstrating this feature

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
