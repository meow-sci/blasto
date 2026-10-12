---
type: How-To
title: Register plan types and redraw the stock window in a skip-original prefix under the same id, title and signature
description: Insert TransferType entries into TransferPlanner.TransferTypes, take over DrawPlanWindow for your own keys with a prefix that returns false, keep stock's window identity, and replay stock's reset when the type changes
category: flight-and-orbits
group: transfers
topic: add-plan-type-to-the-transfer-planner
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/transfer-planner-custom-plan-types]
techniques: [harmony-prefix, reflection]
hooks:
  - harmony-prefix KSA.TransferPlanner.DrawPlanWindow
  - starmap StarMapAllModsLoaded
  - starmap StarMapUnload
lifecycle: [startup:all-mods-loaded, frame:draw-ui, event:mod-unload]
game_types: [KSA.TransferPlanner, KSA.TransferType, KSA.ConsoleStyle, KSA.ConsoleWidgets]
tags: [transfer-planner, plan-type, skip-original, console-style, window-identity, prefix]
---

Outcome: AFC adds **Set Periapsis**, **Set Apoapsis**, **Match Inclination** and **Set Inclination** to the PLAN TYPE dropdown and takes over the two stock circularize types. The player never sees a second window: picking an AFC type swaps the body in place, in the same position and size.
[Custom plan types in the stock Transfer Planner](../../../../mods/ksa-advancedflightcomputer/features/transfer-planner-custom-plan-types.md) Prerequisites: StarMap and Harmony.

## Background: how the game does it

See the [overview](overview.md). The combo iterates the public `TransferPlanner.TransferTypes`; the window is `ConsoleStyle.BeginWindow("transfer-planning", "TRANSFER PLANNING", "KSA-TRJ", ...)`. Planner state is static and main-thread only. `_showPlanWindow` is a private field written while the window is open, while closing goes through the public `ShowPlanWindow` setter because it also clears stock's transfer state.[^ksa-transferplanner]
[current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L225-L260) [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L325-L333)

## Lifecycle and timing

- **Injection:** `[StarMapAllModsLoaded]`, inside the mod's patch block; `TransferTypes` is a static initializer, so it already exists.
- **Drawing:** `TransferPlanner.DrawPlanWindow` is called from `Program.OnDrawUiThreadSafe`, main thread, `frame:draw-ui`, only while the HUD is drawn. The stock porkchop and refine tasks run on the `ThreadPool` and publish into `TransferInfo`, which is why AFC checks `_transferBeingCalculated` before resetting state.[^ksa-program] [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3232-L3236)
- **Removal:** `[StarMapUnload]`. Under StarMap it runs after the game has exited and after StarMap's id-less `Harmony.UnpatchAll()`, so removing the types there is tidiness, not protection for a live frame.

See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Add the entries,** guarded against double insertion, after the last circularize entry or at the end:

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

2. **Take over the window with a prefix that returns false** for your keys and true for everything else; also return false after an exception, so stock never begins the same window after a partial draw:

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

3. **Keep stock's window identity:** call `ConsoleStyle.BeginWindow` with the stock id, title, signature and size, so ImGui keeps one window and its placement when the player switches between stock and your types. Follow the stock body layout (`BeginBody`, `PushWidgetStyle`, region headers, rows, footer with a primary CREATE button) using the console widgets.
4. **Use the same dropdown.** The PLAN TYPE row iterates the same list. On a change, replay the reset stock applies in its own combo: clear `_transferCalculated`, the destination and the selected porkchop entry, pick the starting time unit and invoke the private `SetTransferInfo`:

```csharp
// AdvancedFlightComputer/Core/StockPlanner.cs L70-L83
/// <summary>
/// Switches the plan type with the same reset <see cref="TransferPlanner.DrawPlanWindow"/> applies when the player changes it in stock's own combo. That clears the calculated transfer, the destination and the selected porkchop entry, and selects the time unit stock starts the type with, which is years for an interstellar plan and minutes otherwise.
/// <c>TransferPlanner.SetTransferInfo</c> then rebuilds the transfer info for the new type, and for a stock type with a destination it picks the time unit from the Hohmann estimate.
/// The caller does not switch while a stock porkchop calculation runs, because only stock's own combo cancels that worker.
/// </summary>
public static void SelectType(KSA.TransferType type)
{
    TransferType = type;
    TransferCalculated = false;
    Write(GameReflection.TransferPlanner_destinationBodyRef, new TransferObject(-1));
    Write(GameReflection.TransferPlanner_selectedEntryRef, null);
    SelectTimeUnit(type.GetKey() == Features.ManeuverTools.ManeuverTools.KeyStockInterstellar ? TimeUnit.Years : TimeUnit.Minutes);
    GameReflection.TransferPlanner_SetTransferInfo?.Invoke(null, null);
}
```

   Do not switch while a stock porkchop calculation runs, because only stock's combo cancels that worker.
5. **Write the window state correctly:** `_showPlanWindow` directly while open; close through the public `ShowPlanWindow` setter.
6. **Add a postfix** on the same method for world-space plan markers for every type.
7. **Undo cleanly.** If the window patch is missing, remove the injected types so the dropdown never offers a type with no body; on unload remove them and, if one is selected, select the first stock type (only observable by a loader that unloads a mod from a running game; under StarMap the process is already exiting).

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/ManeuverTools/ManeuverTools.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/ManeuverTools.cs) | keys, injection, removal, handled-type checks |
| [AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.cs) | skip-original prefix, window and body, postfix |
| [AdvancedFlightComputer/Core/StockPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/StockPlanner.cs) | typed access to private planner state, `SelectType` |
| [AdvancedFlightComputer/Features/ManeuverTools/ManeuverToolsWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/ManeuverToolsWindow.cs) | inputs for the four types |

## Variations and gotchas

- **Another mod's types.** Look types up by key, not index, because other mods add plan types too.
- **A skip-original prefix hides the stock body for every handled type;** future stock additions to the body will not appear for your types.
- **Drop plan state** when the selected type is not yours so a stale preview does not draw (`DropPlanState`).
- The planner window only draws while the HUD is visible.

## See also

- [Overview](overview.md); [transpiler injection](add-plan-type-to-the-transfer-planner-2.md)
- [Custom plan types in the stock Transfer Planner](../../../../mods/ksa-advancedflightcomputer/features/transfer-planner-custom-plan-types.md)
- [Create, chain and read stock maneuver nodes from code](../../autopilot/create-and-read-maneuver-nodes/overview.md)

[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
