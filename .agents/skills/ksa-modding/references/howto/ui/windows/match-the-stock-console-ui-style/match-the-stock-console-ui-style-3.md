---
type: How-To
title: "Match the stock UI: ConsoleStyle window skin unpinned from the main viewport and mirrored row helpers"
description: Wrap ConsoleStyle.BeginWindow with pinToMainViewport false so a console-skinned panel can leave the game window, and re-create the Transfer Planner's private row helpers on the public ConsoleWidgets
category: ui
group: windows
topic: match-the-stock-console-ui-style
approach: 3
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/guidance-panel-and-overlays
  - mods/ksa-advancedflightcomputer/features/stock-styled-console-ui
techniques: [imgui-window]
hooks:
  - harmony-postfix KSA.TransferPlanner.DrawPlanWindow
  - harmony-postfix KSA.Burn.DrawBurnEditorWindowContent
  - harmony-transpiler KSA.GameSettings.OnDrawUi
  - starmap StarMapAfterGui
lifecycle: [frame:draw-ui]
game_types: [KSA.ConsoleStyle, KSA.ConsoleWidgets, KSA.TransferPlanner, KSA.KSAColor]
tags: [console-style, begin-window, pin-to-main-viewport, rows, combo, inject, afc]
---
KSA-AdvancedFlightComputer uses the game's console look in two ways. Its Guidance panel is a full console-skinned window (bezel, title strip, close button, footer) that can be dragged **out of the game window** onto another monitor, because it passes `pinToMainViewport: false` to `ConsoleStyle.BeginWindow`. And everywhere it adds rows to windows the
game draws (the Transfer Planner, the burn editor, the settings page) it re-creates the planner's private row helpers on top of the public `ConsoleWidgets`, so injected UI is indistinguishable from stock. Demonstrated by KSA-AdvancedFlightComputer. Prerequisites: StarMap and Harmony.

## Background: how the game does it

- `ConsoleStyle.BeginWindow(id, title, signature, ref open, initialSize, extraFlags, sizeCond, pinToMainViewport = true)` returns false when the window is closed (it ends the ImGui window itself); on true you must pair it with `EndWindow()`. With `pinToMainViewport` true it constrains the size to the work area, calls
  `SetNextWindowViewport(mainViewport.ID)` and clamps the window into the work area; with false none of that happens, so the window can be moved to another platform viewport. It always adds `NoTitleBar | NoCollapse | NoBackground | NoSavedSettings`.[current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L652-L666)[^ksa-consolestyle][current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L678-L755)[^ksa-consolestyle]
- `BeginBody()`/`EndBody()` and `BeginFooter()`/`EndFooter()` lay out the content, body and footer areas; `PushWidgetStyle()` supplies the colours and spacing.[current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L843-L943)[^ksa-consolestyle]
- The Transfer Planner's convenient row helpers (`DrawComboRow`, `DrawCheckboxRow`, `DrawSliderFloatRow`) are **private**, but they are built from public pieces: `ConsoleWidgets.BeginRow`/`EndRow`, `Checkbox`, `BeginComboControl`/`EndComboControl`, `RowControlWidth`, `RowHovered`, `Tooltip`, `Rule`, `RegionHeader`.[current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L339)[^ksa-transferplanner][current/decomp/KSA/ConsoleWidgets.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleWidgets.cs#L739-L790)[^ksa-consolewidgets]

## Lifecycle and timing

The Guidance panel is drawn from AFC's per-frame hooks in `frame:draw-ui` (a window of its own, opened from an "AFC Guidance" menu). The row helpers are called from inside stock window draw methods that AFC patches (`TransferPlanner.DrawPlanWindow`, `Burn.DrawBurnEditorWindowContent`, `GameSettings.OnDrawUi`), all on the
main thread in `frame:draw-ui`, **inside an open stock window with its widget style pushed**; they must not be called outside such a window because they rely on the pushed style and the current content width. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Define identity and size once**, scaled by `ImGuiHelper.InterfaceScale`:

```csharp
// AdvancedFlightComputer/Features/Guidance/Ui/Panel.cs L17-L31
private const string PanelId = "afc-guidance";
private const string PanelTitle = "AFC GUIDANCE";
private const string PanelSignature = "AFC-GNC";

private static float PanelWidthPx => 460f * ImGuiHelper.InterfaceScale;
private static float PanelHeightPx => 720f * ImGuiHelper.InterfaceScale;

/// <summary>
/// Whether the panel is drawn. Off at start, so a game start shows no guidance window until the player opens it from the AFC Guidance menu. The window's close button writes it too. Hiding the panel does not stop guidance, that is the Enabled switch in the same menu.
/// </summary>
internal static bool PanelVisible;

// Which tab the BUTTONS act on. They are drawn above the tab bar, so they read the selection the bar made last frame, one frame of lag on a tab switch, and the alternative is drawing the commit controls below the content they commit.
private static GuidanceTab _panelTab = GuidanceTab.Ascent;
private static LandingSubTab _landingSubTab = LandingSubTab.Powered;
```

2. **Begin the console window unpinned.** The size applies on first use only (`ImGuiCond.FirstUseEver`) so a player resize sticks for the session. `BeginWindow` ends the ImGui window itself when it returns false; when it returns true you must reach `EndWindow` in a `finally`:

```csharp
// AdvancedFlightComputer/Features/Guidance/Ui/Panel.cs L46-L72
// ConsoleStyle.BeginWindow closes the ImGui window itself when it returns false. The size applies on the first use only, so a resize by the player sticks for the session.
bool open = true;
if (!ConsoleStyle.BeginWindow(PanelId, PanelTitle, PanelSignature, ref open,
        new float2(PanelWidthPx, PanelHeightPx), ImGuiWindowFlags.None,
        ImGuiCond.FirstUseEver, pinToMainViewport: false))
    return;

try
{
    if (!open)
    {
        PanelVisible = false;
        return;
    }

    ConsoleStyle.BeginBody();
    ConsoleStyle.PushWidgetStyle();
    try
    {
        DrawGuidancePanelBody(vehicle, orbit, parent, bodyRadius);
    }
    finally
    {
        ConsoleStyle.PopWidgetStyle();
        ConsoleStyle.EndBody();
    }

```
```csharp
// AdvancedFlightComputer/Features/Guidance/Ui/Panel.cs L87-L91
    finally
    {
        ConsoleStyle.EndWindow();
    }
}
```

3. **Put the one control that must always be reachable in the footer**, where no scroll or fold can hide it:

```csharp
// AdvancedFlightComputer/Features/Guidance/Ui/Panel.cs L73-L85
// The release request stays available regardless of the selected tab, so it sits in the footer where no scroll or fold can hide it.
ConsoleStyle.BeginFooter();
try
{
    if (ImGui.Button("RELEASE GUIDANCE"))
    {
        ResetFlightComputer();
    }
}
finally
{
    ConsoleStyle.EndFooter();
}
```

4. **Mirror the planner's private rows.** A stock row is `BeginRow(label)`, one control, `EndRow()`. Re-create checkbox, combo (with the same case-insensitive key matching the planner uses, so it works with stock `TransferType` lists), string-list combo and a typed-number row:

```csharp
// AdvancedFlightComputer/Core/ConsoleUi.cs L59-L112
public static bool CheckboxRow(ReadOnlySpan<char> label, ReadOnlySpan<char> id, ref bool value,
    ReadOnlySpan<char> tooltip = default)
{
    ConsoleWidgets.BeginRow(label);
    bool changed = ConsoleWidgets.Checkbox(id, ref value, pending: false);
    if (tooltip.Length > 0 && ConsoleWidgets.RowHovered)
        ConsoleWidgets.Tooltip(tooltip);
    ConsoleWidgets.EndRow();
    return changed;
}

public static bool ComboRow<T>(ReadOnlySpan<char> label, ReadOnlySpan<char> id,
    ref T value, IReadOnlyList<T> options) where T : IComboable
{
    ConsoleWidgets.BeginRow(label);
    bool changed = ComboControl(id, ref value, options);
    ConsoleWidgets.EndRow();
    return changed;
}

private static bool ComboControl<T>(ReadOnlySpan<char> id, ref T value,
    IReadOnlyList<T> options) where T : IComboable
{
    bool changed = false;
    int current = -1;
    for (int i = 0; i < options.Count; i++)
    {
        if (options[i].GetKey().Equals(value.GetKey(), StringComparison.OrdinalIgnoreCase))
        {
            current = i;
            value = options[i];
            break;
        }
    }
    if (current < 0 && options.Count > 0)
        current = 0;

    string preview = current >= 0 ? options[current].GetName() : "N/A";
    if (!ConsoleWidgets.BeginComboControl(id, preview.AsSpan(), pending: false))
        return false;

    for (int i = 0; i < options.Count; i++)
    {
        T option = options[i];
        bool selected = option.GetKey().Equals(value.GetKey(), StringComparison.OrdinalIgnoreCase);
        if (ImGui.Selectable(option.GetName(), selected, ImGuiSelectableFlags.None, (float2?)null))
        {
            value = option;
            changed = true;
        }
    }
    ConsoleWidgets.EndComboControl();
    return changed;
}
```

5. **Use the palette fields for status text** with the value font: `TextMuted`, `Positive`, `Pending` (warning), `Danger`, and the wrapped variants that push the colour for `TextWrapped`:

```csharp
// AdvancedFlightComputer/Core/ConsoleUi.cs L15-L57
internal static class ConsoleUi
{
    public static void Muted(ReadOnlySpan<char> text)
    {
        ConsoleStyle.PushValueFont();
        ImGui.TextColored(in ConsoleStyle.TextMuted, text);
        ConsoleStyle.PopFont();
    }

    public static void Positive(ReadOnlySpan<char> text)
    {
        ConsoleStyle.PushValueFont();
        ImGui.TextColored(in ConsoleStyle.Positive, text);
        ConsoleStyle.PopFont();
    }

    public static void Warning(ReadOnlySpan<char> text)
    {
        ConsoleStyle.PushValueFont();
        ImGui.TextColored(in ConsoleStyle.Pending, text);
        ConsoleStyle.PopFont();
    }

    public static void DangerWrapped(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, in ConsoleStyle.Danger);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    public static void WarningWrapped(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, in ConsoleStyle.Pending);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    public static void MutedWrapped(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, in ConsoleStyle.TextMuted);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }
```

6. **Start sections with `ConsoleWidgets.Rule()` and `RegionHeader("UPPER CASE")`**, as stock does; labels are passed through unchanged and written upper case at the call site.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Ui/Panel.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/Panel.cs#L10-L91) | Console window shell, unpinned |
| [AdvancedFlightComputer/Core/ConsoleUi.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/ConsoleUi.cs) | Row and text helpers over `ConsoleWidgets` / `ConsoleStyle` |
| [AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.cs#L86-L90) | A pinned `BeginWindow` for the plan window |

## Variations and gotchas

- **Unpinned means unclamped.** The panel can be dragged entirely off the game window and, with `NoSavedSettings`, comes back at the default position next session.
- **`ConsoleWidgets` keeps row state in statics** (`_rowMin`, `_rowControlWidth`); pair `BeginRow` with `EndRow` even on early returns.
- **Stock labels are upper case**; mixed-case labels look foreign next to stock rows.
- **The helpers rely on `ConsoleStyle` being initialized** (`BeginRow` calls `EnsureInitialized`), which is true inside stock windows.
- **Inside stock windows only** for the row helpers; calling them in an arbitrary window without `PushWidgetStyle` gives the kit's fonts and colours but the global spacing (see [approach 1](match-the-stock-console-ui-style-1.md)).
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of match-the-stock-console-ui-style](overview.md)
- [Approach 1: ConsoleWidgets rows inside an ImGuiWindow subclass](match-the-stock-console-ui-style-1.md)
- [Approach 2: plain ImGui.Begin plus PushWidgetStyle](match-the-stock-console-ui-style-2.md)
- [Inject UI into the game's own windows and menus, including the orbit right-click menu](../../menus/inject-ui-into-a-stock-window/overview.md)
- Mod features: [Guidance panel and overlays](../../../../mods/ksa-advancedflightcomputer/features/guidance-panel-and-overlays.md),
  [Stock-styled console UI](../../../../mods/ksa-advancedflightcomputer/features/stock-styled-console-ui.md)

[^ksa-consolestyle]: KSA.ConsoleStyle (decompiled, build 2026.10.10.5554)
[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
[^ksa-consolewidgets]: KSA.ConsoleWidgets (decompiled, build 2026.10.10.5554)
