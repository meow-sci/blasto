---
type: Mod Feature
title: Stock-styled rows inside game console windows
description: Small wrappers over the game's public ConsoleWidgets and ConsoleStyle reproduce the Transfer Planner's private row helpers, so UI injected into stock windows matches stock fonts, colors and columns
mod: ksa-advancedflightcomputer
category: ui
techniques: [imgui-window]
hooks: []
lifecycle: [frame:draw-ui]
game_types: [KSA.ConsoleWidgets, KSA.ConsoleStyle, KSA.TransferPlanner, KSA.IComboable]
tags: [imgui, consolewidgets, consolestyle, styling, transfer-planner]
---

When a mod draws into a stock window (the Transfer Planning window, the burn editor, the settings
page), raw `ImGui.Checkbox` or `ImGui.Combo` calls render at a different size, color and column
position than the stock rows around them. KSA's console-style windows build every row with the public
`ConsoleWidgets` and `ConsoleStyle` classes, but the convenient row helpers the Transfer Planner uses
(`DrawComboRow`, `DrawCheckboxRow`, ...) are private. AFC's `ConsoleUi` re-creates them on top of the
public API.

## How it works

- **Row protocol.** A stock row is `ConsoleWidgets.BeginRow(label)` (draws the upper-case label in
  the label font in the left third, sets up the control column and hover wash), one control, then
  `ConsoleWidgets.EndRow()` ([current/decomp/KSA/ConsoleWidgets.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleWidgets.cs#L384-L467)). `ConsoleUi.CheckboxRow`,
  `ComboRow<T>`, the string-list `ComboRow` and `InputDoubleRow` follow exactly that protocol
  ([AdvancedFlightComputer/Core/ConsoleUi.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/ConsoleUi.cs#L59-L147)).
- **Combo.** The planner's private `DrawComboControl` uses `ImGui.BeginCombo`; AFC uses the public
  `ConsoleWidgets.BeginComboControl` / `EndComboControl` pair and the same `IComboable` key matching
  (case-insensitive `GetKey()`, falling back to the first option), so it works with stock
  `TransferType` lists ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L1405-L1450)).
- **Numbers.** Typed absolute values (altitudes, angles) use `ImGui.InputDouble` sized to
  `ConsoleWidgets.RowControlWidth` inside a row, because the stock drag widgets need a range.
- **Text colors.** `Muted`, `Positive`, `Warning` push `ConsoleStyle.PushValueFont()` and use the
  palette fields `ConsoleStyle.TextMuted`, `Positive`, `Pending`, `Danger`
  ([current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L61-L69)); the `*Wrapped` variants push the color for
  `ImGui.TextWrapped`.
- **Headers and rules.** Sections start with `ConsoleWidgets.Rule()` and
  `ConsoleWidgets.RegionHeader("UPPER CASE")`, as stock does; labels are passed through unchanged and
  written upper case at the call site to match stock.

AFC uses these rows in the quick-tool, flyby and multi-pass sections of the planner, the RCS burn
editor section and both Mods-settings sections.

## Lifecycle and timing

Pure ImGui drawing, called from inside stock window draw methods that AFC patches
(`TransferPlanner.DrawPlanWindow`, `Burn.DrawBurnEditorWindowContent`, `GameSettings.OnDrawUi`).
All of them run on the main thread in `frame:draw-ui`, inside an open stock window with its widget
style pushed; the helpers must not be called outside such a window because they rely on the pushed
style and the current content width.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Core/ConsoleUi.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/ConsoleUi.cs) | Row and text helpers over `ConsoleWidgets` / `ConsoleStyle` |
| [AdvancedFlightComputer/Features/AutoRemove/AutoRemoveFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoRemove/AutoRemoveFeature.cs#L36-L52) | Minimal section built from these helpers |
| [AdvancedFlightComputer/Features/ManeuverTools/ManeuverToolsWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/ManeuverToolsWindow.cs) | Heaviest user: the quick-tool inputs in the planner |

## Snippets

```csharp
// AdvancedFlightComputer/Core/ConsoleUi.cs L59-L77
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
```

## Game APIs touched

| Member | Use |
|---|---|
| `ConsoleWidgets.BeginRow`, `EndRow`, `Checkbox`, `BeginComboControl`, `EndComboControl`, `RowControlWidth`, `RowHovered`, `Tooltip`, `Rule`, `RegionHeader` ([current/decomp/KSA/ConsoleWidgets.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleWidgets.cs)) | Stock row layout and widgets |
| `ConsoleStyle.PushValueFont`, `PopFont`, `TextMuted`, `Positive`, `Pending`, `Danger` ([current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs)) | Fonts and palette |
| `TransferPlanner.DrawComboRow` (private, [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L1405)) | The stock helper being mirrored |

## Pitfalls and version notes

- `ConsoleWidgets` keeps row state in statics (`_rowMin`, `_rowControlWidth`); always pair
  `BeginRow` with `EndRow` even on early returns.
- Stock labels are upper case; mixed-case labels look foreign next to stock rows.
- The helpers rely on `ConsoleStyle` being initialized (`BeginRow` calls `EnsureInitialized`), which
  is true inside stock windows.

## See also

- [Mods settings page sections](mods-settings-page-sections.md)
- [Plan window transpiler injection](plan-window-transpiler-injection.md)
- [RCS burn editor and gauge UI](rcs-burn-editor-and-gauge-ui.md)
- [Match the stock UI: ConsoleStyle window skin unpinned from the main viewport and mirrored row helpers](../../../howto/ui/windows/match-the-stock-console-ui-style/match-the-stock-console-ui-style-3.md) - how-to article demonstrating this feature

