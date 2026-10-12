---
type: How-To
title: Add shortcuts to the orbit right-click menu with a transpiler anchored on UTF-8 label literals
description: Insert an Advanced Flight Computer submenu before the last EndPopup in BurnContextMenu.Draw and an extra item inside the stock At Periapsis and At Apoapsis submenus by reading the u8 label bytes out of the IL
category: ui
group: menus
topic: inject-ui-into-a-stock-window
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/orbit-context-menu-shortcuts]
techniques: [harmony-transpiler, imgui-menu]
hooks: [harmony-transpiler KSA.BurnContextMenu.Draw, starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.BurnContextMenu, KSA.TransferPlanner, KSA.Program, Brutal.ImGuiApi.ImGui]
tags: [context-menu, orbit, popup, transpiler, u8-literal, il-anchor, submenu]
---
Right-click the controlled vehicle's orbit in flight and KSA shows a small context menu: burn actions, with "At Periapsis", "At Apoapsis" and node submenus when the click is near those points. AFC adds an **Advanced Flight Computer** submenu with four quick-tools (set apoapsis, set periapsis, match inclination, set inclination) and an extra item in the two apse submenus. The shortcuts open the Transfer Planner with the plan type preselected; they do not place a burn at the clicked point. Prerequisites: StarMap, Harmony, and the transpiler basics from [Write a Harmony transpiler](../../../patching/transpilers/write-a-harmony-transpiler/overview.md). Demonstrated by [Orbit context menu shortcuts](../../../../mods/ksa-advancedflightcomputer/features/orbit-context-menu-shortcuts.md).

## Background: how the game does it

Right-click handling is two-staged: `Program.OnMouseButton` calls `BurnContextMenu.OnRightClick()` on press and `BurnContextMenu.TryOpen` on release, and `TryOpen` sets `_openBurnMenuNextFrame` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2024-L2044)[^ksa-ksa-program]). On the next frame `BurnContextMenu.Draw()` opens popup `burn_context_menu`, bails out with `ImGui.EndPopup()` on several early-return paths, and on the orbit path writes each submenu as `ImGui.BeginMenu("At Periapsis"u8)` ... `ImGui.EndMenu()` before closing with one last `ImGui.EndPopup()`:

```csharp
// current/decomp/KSA/BurnContextMenu.cs L311-L317, L339-L347
		if (_burnMenuNearPeriapsis && ImGui.BeginMenu("At Periapsis"u8))
		{
			CelestialPosition point2 = FeaturePoint(in point, _burnMenuPeriapsisTime.Value);
			DrawCountdownHeader(burnMenuVehicle, in point2);
			flag |= DrawBurnActions(burnMenuVehicle, in point2, node: false);
			ImGui.EndMenu();
		}
// ...
		if (_burnMenuNearPeriapsis || _burnMenuNearApoapsis || _burnMenuNearAscending || _burnMenuNearDescending)
		{
			ImGui.Separator();
		}
		if (!(flag | DrawBurnActions(burnMenuVehicle, in point, node: false)))
		{
			DrawPointMarker(in point, point.Orbit.OrbitLineColor, filled: false);
		}
		ImGui.EndPopup();
```

([current/decomp/KSA/BurnContextMenu.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnContextMenu.cs#L259-L348)[^ksa-ksa-burncontextmenu]). `Draw` is called from `OnDrawUiThreadSafe` when the editor is closed ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3228-L3233)). A postfix would run after the popup is closed, and ImGui menu items must be emitted between `BeginPopup` and `EndPopup`; hence a transpiler.

The `u8` suffix compiles to a read-only static data field plus a length: the label is not a managed string in the IL, which is why AFC reads the bytes from the field itself.

## Lifecycle and timing

- **Patch at `startup:all-mods-loaded`**, optional: if it fails, the shortcuts are disabled and the quick-tools stay in the planner's dropdown.
- **Inserted calls run at `frame:draw-ui`**, inside the open popup, on the main thread, from `OnDrawUiThreadSafe`. They only draw menu items and, on click, set static planner state; the planner window itself draws later in the same call ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3228-L3237)).
- See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Target the method and pick the right `EndPopup`.** Earlier `EndPopup` calls belong to early returns (delete-burn menu, parent departure, no patch). The orbit path ends with the **last** call, so scan the whole list and keep the last index, then insert `call BurnMenuLauncher.DrawInline` before it:

```csharp
// AdvancedFlightComputer/Features/ManeuverTools/Patch_BurnContextMenu_Launcher.cs L14-L16, L31-L36, L50-L57, L68-L70
// Insert the call inside the popup because a postfix would run after ImGui.EndPopup.
[HarmonyPatch(typeof(BurnContextMenu), nameof(BurnContextMenu.Draw), new Type[] { })]
internal static class Patch_BurnContextMenu_Launcher
// ...
    [HarmonyTranspiler]
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = new List<CodeInstruction>(instructions);

        var anchor = AccessTools.Method(typeof(ImGui), nameof(ImGui.EndPopup), Type.EmptyTypes);
// ...
        // Earlier EndPopup calls belong to early returns. Insert before the final one.
        int anchorIndex = -1;
        for (int i = 0; i < code.Count; i++)
        {
            if (!code[i].Calls(anchor))
                continue;
            anchorIndex = i;
        }
// ...
        InsertShortcuts(code, anchorIndex, injectTarget);
        return code;
    }
```

2. **Write the inserted menu.** Draw inside `try`/`finally` so `EndMenu` is balanced even if an item handler throws, and refuse for non-vehicle controllers:

```csharp
// AdvancedFlightComputer/Features/ManeuverTools/BurnMenuLauncher.cs L50-L87
    public static void DrawInline()
    {
        if (!Enabled)
            return;

        try
        {
            Vehicle? vehicle = Program.ControlledVehicle;
            if (vehicle == null || vehicle is KittenEva)
                return;

            ImGui.Separator();
            if (!ImGui.BeginMenu("Advanced Flight Computer"u8))
                return;

            try
            {
                if (ImGui.MenuItem("Set Apoapsis..."u8))
                    OpenPlanner(ManeuverTools.KeySetApoapsis, vehicle);
                if (ImGui.MenuItem("Set Periapsis..."u8))
                    OpenPlanner(ManeuverTools.KeySetPeriapsis, vehicle);
                if (ImGui.MenuItem("Match Inclination..."u8))
                    OpenPlanner(ManeuverTools.KeyMatchInclination, vehicle);
                if (ImGui.MenuItem("Set Inclination..."u8))
                    OpenPlanner(ManeuverTools.KeySetInclination, vehicle);
            }
            finally
            {
                // Balance the menu even if an item handler throws.
                ImGui.EndMenu();
            }
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce("burn-menu-launcher:" + ex.GetType().Name,
                $"[AFC] BurnMenuLauncher: {ex}");
        }
    }
```

3. **Find a stock submenu by its label, not its index.** Walk back at most eight instructions from each `BeginMenu(ImString, bool)` call to the `ldsflda`, read the bytes with `RuntimeHelpers.CreateSpan<byte>(field.FieldHandle)` and decode them with the length from the following `ldc.i4`; fall back to trimming trailing zero bytes:

```csharp
// AdvancedFlightComputer/Features/ManeuverTools/Patch_BurnContextMenu_Launcher.cs L131-L139, L160-L183
    private static int FindSubmenu(List<CodeInstruction> code, MethodInfo beginMenu, string heading)
    {
        for (int i = 0; i < code.Count; i++)
        {
            if (code[i].Calls(beginMenu) && ReadMenuHeading(code, i) == heading)
                return i;
        }
        return -1;
    }
// ...
    /// <summary>Reads the UTF-8 literal passed to a <c>BeginMenu</c> call.</summary>
    private static string? ReadMenuHeading(List<CodeInstruction> code, int beginMenuIndex)
    {
        int stop = Math.Max(0, beginMenuIndex - MaxIlGapLabelToBeginMenu);
        for (int i = beginMenuIndex - 1; i >= stop; i--)
        {
            if (code[i].opcode != OpCodes.Ldsflda || code[i].operand is not FieldInfo data)
                continue;
            try
            {
                ReadOnlySpan<byte> bytes = RuntimeHelpers.CreateSpan<byte>(data.FieldHandle);
                int length = i + 1 < code.Count && TryReadInt32(code[i + 1], out int stated)
                             && stated >= 0 && stated <= bytes.Length
                    ? stated
                    : bytes.TrimEnd((byte)0).Length;
                return Encoding.UTF8.GetString(bytes[..length]);
            }
            catch (Exception)
            {
                // This static field does not contain a readable literal.
            }
        }
        return null;
    }
```

4. **Insert an entry before the submenu's `EndMenu`.** To find where the submenu ends, count nested `BeginMenu`/`EndMenu` pairs from its `BeginMenu`:

```csharp
// AdvancedFlightComputer/Features/ManeuverTools/Patch_BurnContextMenu_Launcher.cs L142-L158
    private static int FindSubmenuEnd(List<CodeInstruction> code, MethodInfo beginMenu,
        MethodInfo endMenu, int beginIndex)
    {
        int depth = 0;
        for (int i = beginIndex + 1; i < code.Count; i++)
        {
            if (code[i].Calls(beginMenu))
                depth++;
            else if (code[i].Calls(endMenu))
            {
                if (depth == 0)
                    return i;
                depth--;
            }
        }
        return -1;
    }
```

   Insert in descending index order so earlier anchors do not move; each insertion is `ldc.i4 <submenu>; call BurnMenuLauncher.DrawApsisEntry(int)`. An apse burn changes the opposite apse, so the periapsis submenu offers "Set Apoapsis..." and vice versa:

```csharp
// AdvancedFlightComputer/Features/ManeuverTools/Patch_BurnContextMenu_Launcher.cs L95-L117
        var insertions = new List<(int End, int Submenu)>();
        foreach ((string heading, int submenu) in ApseSubmenus)
        {
            int begin = FindSubmenu(code, beginMenu, heading);
            int end = begin < 0 ? -1 : FindSubmenuEnd(code, beginMenu, endMenu, begin);
            if (end < 0)
            {
                LogHelper.WarnOnce("transpiler-burn-menu-heading-" + heading,
                    "[AFC] BurnMenuLauncher transpiler: no complete '" + heading + "' submenu " +
                    "found in BurnContextMenu.Draw IL; that submenu gets no AFC entry.");
                continue;
            }
            insertions.Add((end, submenu));
        }

        // Insert at descending indices so earlier anchors do not move.
        insertions.Sort((left, right) => right.End.CompareTo(left.End));
        foreach ((int end, int submenu) in insertions)
        {
            InsertBefore(code, end,
                new CodeInstruction(OpCodes.Ldc_I4, submenu),
                new CodeInstruction(OpCodes.Call, apsisTarget));
        }
```

   `InsertBefore` hands the anchor's labels and begin-exception markers to the first inserted instruction so a jump to the anchor runs the new code and a `try` region is not left out ([AdvancedFlightComputer/Core/TranspilerInsertion.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/TranspilerInsertion.cs#L5-L20)):

```csharp
// AdvancedFlightComputer/Core/TranspilerInsertion.cs L5-L20
internal static class TranspilerInsertion
{
    internal static void MoveEntryMarkers(CodeInstruction anchor, CodeInstruction first)
    {
        first.labels.AddRange(anchor.labels);
        anchor.labels.Clear();

        // Begin markers apply before an instruction, while end markers apply after it. Keep end markers on the anchor so the inserted call stays inside its exception region.
        foreach (ExceptionBlock block in anchor.blocks)
        {
            if (block.blockType != ExceptionBlockType.EndExceptionBlock)
                first.blocks.Add(block);
        }
        anchor.blocks.RemoveAll(block => block.blockType != ExceptionBlockType.EndExceptionBlock);
    }
}
```

5. **Open the planner from the item.** Refuse while stock's calculation worker is running (only the stock body polls it), then set the stock planner's static state:

```csharp
// AdvancedFlightComputer/Features/ManeuverTools/BurnMenuLauncher.cs L89-L117
    internal static void OpenPlanner(string typeKey, Vehicle vehicle)
    {
        if (FindType(typeKey) is not TransferType type)
            return;

        // TransferPlanner.DrawPlanWindow polls the stock worker only in its own body. Do not switch to an AFC body while that worker is running.
        if (StockPlanner.TransferBeingCalculated)
        {
            TimedAlert.Create("Transfer calculating; wait for it to finish.", Color.Yellow, 3.0);
            return;
        }

        // The next pass replaces the committed trajectory, so another maneuver can be planned only after all passes finish.
        if (MultiPassRegistry.Has(vehicle.Id))
        {
            TimedAlert.Create("Multi-pass running; cancel it or let it finish first.", Color.Yellow, 3.0);
            return;
        }

        // TransferPlanner.ShowPlanWindow clears stock selection state only when set to false.
        TransferPlanner.ShowPlanWindow = true;
        StockPlanner.SourceBody = new TransferObject(vehicle);
        StockPlanner.SelectType(type);

        // Reset input defaults when a shortcut changes the source or type outside the dropdown.
        ManeuverToolsWindow.OnTypeChanged();
        ManeuverToolsWindow.OnSourceChanged();
        Patch_DrawPlanWindow.OnManeuverContextChanged();
    }
```

   `TransferPlanner.ShowPlanWindow` is a public static property ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L224)).[^ksa-ksa-transferplanner]

6. **Test in flight.** Right-click your orbit: the new submenu appears above the stock actions; near periapsis the stock "At Periapsis" submenu gains the "AFC: Set Apoapsis..." item. Warnings from this patch are logged once each.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/ManeuverTools/Patch_BurnContextMenu_Launcher.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/Patch_BurnContextMenu_Launcher.cs#L14-L206) | Transpiler, label reader, nested-menu matcher |
| [AdvancedFlightComputer/Features/ManeuverTools/BurnMenuLauncher.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/BurnMenuLauncher.cs#L11-L129) | Inserted menu items and `OpenPlanner` |
| [AdvancedFlightComputer/Core/TranspilerInsertion.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/TranspilerInsertion.cs) | Label and exception-marker hand-over |

## Variations and gotchas

- **Match submenus by label, not by index.** A new stock submenu would shift indices and move your entries into the wrong menu.
- **`u8` literals are a compiler detail.** The IL shape (`ldsflda` then `ldc.i4` length) may change with the compiler used to build the game; the reader tolerates a missing length.
- **Use the last `EndPopup`.** Using the first would put the menu in the delete-burn popup.
- **Menu only exists in orbit mode.** Right-clicking a burn gizmo opens the delete-burn popup (`BurnMenuMode.DeleteBurn`), which returns early and never reaches your insertion.
- **Disable when patching fails.** If the transpiler logs a missing anchor, keep the feature reachable another way (AFC keeps the quick-tools in the planner dropdown).
- **Another mod** inserting at the same last `EndPopup` draws as well, in Harmony patch order.
- **ImGui menu ids.** `BeginMenu` labels are ids within the popup; use a label other than the stock ones ("Advanced Flight Computer").

## See also

- [Overview](overview.md)
- [Approach 1: Settings > Mods page sections](inject-ui-into-a-stock-window-1.md)
- [Approach 3: call-site swap in the plan window](inject-ui-into-a-stock-window-3.md)
- [Insert calls into a game method at an IL anchor](../../../patching/transpilers/insert-calls-into-a-game-method-at-an-il-anchor/overview.md)
- [Identify a call by its argument IL](../../../patching/transpilers/identify-a-call-by-its-argument-il/overview.md)
- [Add a plan type to the Transfer Planner](../../../flight-and-orbits/transfers/add-plan-type-to-the-transfer-planner/overview.md)
- Mod feature: [Orbit context menu shortcuts](../../../../mods/ksa-advancedflightcomputer/features/orbit-context-menu-shortcuts.md)

[^ksa-ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-ksa-burncontextmenu]: KSA.BurnContextMenu (decompiled, build 2026.10.10.5554)
[^ksa-ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
