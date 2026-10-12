---
type: How-To
title: "Match the stock UI: ConsoleWidgets rows inside an ImGuiWindow subclass without console chrome"
description: Use the game's public ConsoleWidgets and ConsoleStyle helpers for label-and-control rows, tooltips, buttons, headers and status text in a mod window that keeps the plain ImGuiWindow chrome
category: ui
group: windows
topic: match-the-stock-console-ui-style
approach: 1
difficulty: beginner
prerequisites: [starmap]
demonstrated_by:
  - mods/ksa-deltavmap/features/stock-console-widget-panels
  - mods/ksa-measuretools/features/measure-menu-and-tool-window
techniques: [imgui-window]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.ConsoleStyle, KSA.ConsoleWidgets, KSA.ImGuiWindow, KSA.TransferPlanner, KSA.ImGuiDisabledScope]
tags: [console-style, console-widgets, rows, tooltip, status-text, imguiwindow]
---
KSA's own console-style windows (Transfer Planner, staging, settings) build every row from two public classes: `KSA.ConsoleWidgets` (label-and-control rows, region headers, segmented presets, readouts, buttons) and `KSA.ConsoleStyle` (fonts and a semantic colour palette). A mod window drawn with
raw `ImGui.Checkbox` and `ImGui.Combo` looks foreign next to them: different size, colour and column. KSA-DeltaVMap's side panels and KSA-MeasureTools' tool window use the same widget kit inside an ordinary `ImGuiWindow` subclass, and wrap the three row shapes they need in a small helper. This is the
cheapest way to match the stock look. Demonstrated by KSA-DeltaVMap and KSA-MeasureTools. Prerequisite: StarMap.

## Background: how the game does it

- **Row protocol.** `ConsoleWidgets.BeginRow(label)` draws the label itself (label font, label colour, a hover wash across the row, the left third of the width) on the window draw list and moves the cursor to the right-hand control column; you draw one control; `EndRow()` pads the row to at least `ConsoleStyle.FrameHeightPx`. Row state is static
  (`_rowMin`, `_rowControlWidth`, `_rowHovered`), so always pair `BeginRow` with `EndRow`, including on early returns.[current/decomp/KSA/ConsoleWidgets.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleWidgets.cs#L384-L450)[^ksa-consolewidgets]
- `BeginRow`, `Checkbox`, `RegionHeader` and `Readout` call `ConsoleStyle.EnsureInitialized` themselves, so they work in any ImGui window. `ConsoleWidgets.Tooltip(text)` draws a styled tooltip; `ConsoleWidgets.RowHovered` is true while the pointer is over the current row.[current/decomp/KSA/ConsoleWidgets.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleWidgets.cs#L997-L1012)[^ksa-consolewidgets]
- **Where the rest of the look comes from.** The colours, frame padding, item spacing and label font of a stock console window come from `ConsoleStyle.PushWidgetStyle()` (48 colours, 10 style vars and the label font; `PopWidgetStyle()` pops them), which stock windows push after `ConsoleStyle.BeginWindow`/`BeginBody`.
  `ImGuiWindow` pushes it around `DrawContent` only on its `UseConsoleChrome` path.[current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L575-L650)[^ksa-consolestyle][current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L181-L186)[^ksa-imguiwindow]
  A subclass that does not set `UseConsoleChrome` (both mods here) gets the kit's own fonts and colours for labels, headers, readouts and the console checkbox, while raw ImGui controls inside a row and the spacing between rows use the global ImGui style.
- `ConsoleWidgets.SliderFloat` is linear. The stock Transfer Planner draws its decade-spanning sliders with a **private** `DrawSliderFloatRow`: `BeginRow`, a `"##" + id` label built on the stack, `SetNextItemWidth(-1)`, `ImGui.SliderFloat(..., ImGuiSliderFlags.Logarithmic)`, `EndRow`.[current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L944-L948)[^ksa-transferplanner]

## Lifecycle and timing

Pure immediate-mode ImGui drawing from `DrawContent`, in `frame:draw-ui` via the window's `[StarMapAfterGui]` draw; a changed value is returned the same frame and the panels turn it into a rebuild after the panel returns. Nothing is written to game state. Only while the window is shown and the HUD is visible.
See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and
[the window base class](../subclass-the-stock-imguiwindow/subclass-the-stock-imguiwindow-1.md).

## Steps

1. **Wrap your row shapes** in one helper class. A checkbox row (with an optional tooltip shown while the row is hovered), a logarithmic slider row copied from the planner (adding `NoRoundToFormat` so a small value keeps its precision, `AlwaysClamp`, and an overload that reports `IsItemActive()` so a caller can tell a drag from its release), and a toned status line:

```csharp
// DeltaVMap/Render/ConsoleUi.cs L17-L50
public static bool CheckboxRow(ReadOnlySpan<char> label, ReadOnlySpan<char> id, ref bool value)
{
    ConsoleWidgets.BeginRow(label);
    bool changed = ConsoleWidgets.Checkbox(id, ref value, pending: false);
    ConsoleWidgets.EndRow();
    return changed;
}

// A slider row. ConsoleWidgets.SliderFloat is linear only, so a value that spans decades
// (a cruise speed from km/s to a large fraction of c) uses the ImGui slider with its
// logarithmic flag inside the console row, as the game's own planner does
// (TransferPlanner.DrawSliderFloatRow). id is the hidden ImGui id without the "##". The
// value is not rounded to the display format, so a small speed keeps its precision.
public static bool LogSliderRow(ReadOnlySpan<char> label, ReadOnlySpan<char> id, ref float value, float min, float max, ReadOnlySpan<char> format)
{
    return LogSliderRow(label, id, ref value, min, max, format, out _);
}

// The same row, with active true while the slider is held (ImGui.IsItemActive right after
// the slider, before the row closes), so a caller can defer costly work to the release.
public static bool LogSliderRow(ReadOnlySpan<char> label, ReadOnlySpan<char> id, ref float value, float min, float max, ReadOnlySpan<char> format, out bool active)
{
    ConsoleWidgets.BeginRow(label);
    Span<char> buffer = stackalloc char[64];
    var hidden = new SpanBuilder(buffer);
    hidden.Append("##".AsSpan());
    hidden.Append(id);
    ImGui.SetNextItemWidth(-1f);
    bool changed = ImGui.SliderFloat(hidden, ref value, min, max, format,
        ImGuiSliderFlags.Logarithmic | ImGuiSliderFlags.NoRoundToFormat | ImGuiSliderFlags.AlwaysClamp);
    active = ImGui.IsItemActive();
    ConsoleWidgets.EndRow();
    return changed;
}
```
```csharp
// DeltaVMap/Render/ConsoleUi.cs L52-L76
// A status line in the console value font: muted for information, positive for a good
// verdict, danger for a problem. It wraps at the panel edge, so a long verdict keeps its
// figure in view.
public static void StatusText(string text, StatusTone tone = StatusTone.Muted)
{
    ConsoleStyle.PushValueFont();
    ImGui.PushTextWrapPos(0f);
    switch (tone)
    {
        case StatusTone.Positive:
            ImGui.TextColored(in ConsoleStyle.Positive, text);
            break;
        case StatusTone.Pending:
            ImGui.TextColored(in ConsoleStyle.Pending, text);
            break;
        case StatusTone.Danger:
            ImGui.TextColored(in ConsoleStyle.Danger, text);
            break;
        default:
            ImGui.TextColored(in ConsoleStyle.TextMuted, text);
            break;
    }
    ImGui.PopTextWrapPos();
    ConsoleStyle.PopFont();
}
```

   MeasureTools' version of the checkbox row, with the same shape, is [MeasureTools/Core/ConsoleUi.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Core/ConsoleUi.cs#L22-L31).
2. **Write labels in upper case** at the call site; stock labels are upper case and mixed-case labels look foreign. Keep ids short and unique per control (they go through `ImGui` id hashing).
3. **Use the kit directly for headers, presets, readouts and buttons.** Region headers upper-case their text; `Segmented` returns the selected index; `Readout(key, value)` draws a key/value line; `Button` and `PrimaryButton` take a label, an id and a size (use `ConsoleWidgets.ButtonHeight` for the height):

```csharp
// DeltaVMap/Render/InterstellarSection.cs L420-L432
if (_route.TargetIsHub)
{
    ConsoleWidgets.RegionHeader("CAPTURE".AsSpan());
    float before = _periapsisAu;
    ConsoleUi.LogSliderRow("PERIAPSIS".AsSpan(), "dvisperi".AsSpan(), ref _periapsisAu, MinPeriapsisAu, MaxPeriapsisAu,
        "%.2f AU".AsSpan(), out bool dragging);
    bool released = _periapsisDragging && !dragging;
    _periapsisDragging = dragging;
    if (_periapsisAu != before || released)
        Evaluate();
    if (!Failed)
        DrawRows(_captureRows);
}
```
```csharp
// DeltaVMap/Render/InterstellarSection.cs L483-L501
private static void DrawRows(List<Row> rows)
{
    for (int i = 0; i < rows.Count; i++)
    {
        Row row = rows[i];
        switch (row.Kind)
        {
            case RowKind.Header:
                ConsoleWidgets.RegionHeader(row.Key.AsSpan());
                break;
            case RowKind.Readout:
                ConsoleWidgets.Readout(row.Key.AsSpan(), row.Value.AsSpan());
                break;
            case RowKind.Status:
                ConsoleUi.StatusText(row.Key, row.Tone);
                break;
        }
    }
}
```

4. **Buttons that show a mode.** The active mode gets `PrimaryButton`, the others `Button`; the label doubles as the id so it goes in as a char span (the `ConsoleWidgets` overloads take one directly):

```csharp
// MeasureTools/Features/Measure/MeasureWindow.cs L206-L221
// The label doubles as the widget id, so it goes in as a char span: the
// ConsoleWidgets overloads take one directly, while an ImString would have to be
// converted back per button per frame.
private static void DrawModeButton(ReadOnlySpan<char> label, MeasureMode mode, float width)
{
    bool selected = MeasureState.ToolActive && MeasureState.Mode == mode;
    float2 size = new float2(width, ConsoleWidgets.ButtonHeight);
    bool clicked = selected
        ? ConsoleWidgets.PrimaryButton(label, label, size)
        : ConsoleWidgets.Button(label, label, size);
    if (clicked)
    {
        MeasureState.SetMode(mode);
        MeasureState.SetToolActive(true);
    }
}
```

5. **Disable controls with `ImGui.BeginDisabled`/`EndDisabled`** (nested blocks grey out sub-toggles under a master toggle), and keep your own layout (`Indent`/`Unindent`) outside the rows:

```csharp
// MeasureTools/Features/Measure/MeasureWindow.cs L223-L252
private void DrawSnappingSection(IViewport viewport)
{
    // Snap and the reference body only apply to ruler/protractor picking;
    // surface mode ray-casts the celestial spheres, circle and face-angle
    // modes have their own part picking.
    bool snapControlsApply = MeasureState.Mode == MeasureMode.Ruler || MeasureState.Mode == MeasureMode.Angle;
    ImGui.BeginDisabled(!MeasureState.ToolActive || !snapControlsApply);
    bool snap = MeasureState.SnapEnabled;
    if (ConsoleUi.CheckboxRow("BODIES AND ORBIT LINES".AsSpan(), "MtSnap".AsSpan(), ref snap))
        MeasureState.SetSnapEnabled(snap);
    // Part snapping is a refinement of general snapping, so the sub-controls
    // disable together with the master snap toggle.
    ImGui.BeginDisabled(!snap);
    bool snapParts = MeasureState.PartSnapEnabled;
    if (ConsoleUi.CheckboxRow("PARTS".AsSpan(), "MtSnapParts".AsSpan(), ref snapParts,
            "Pick points on vehicle parts: exact surface points under the cursor, refined by the tiers below. Off: vehicles snap at their center marker only.".AsSpan()))
        MeasureState.SetPartSnapEnabled(snapParts);
    ImGui.Indent();
    ImGui.BeginDisabled(!snapParts);
    bool snapNodes = MeasureState.PartFeatureSnapEnabled;
    if (ConsoleUi.CheckboxRow("NODES, CENTERS, RIMS".AsSpan(), "MtSnapNodes".AsSpan(), ref snapNodes,
            "Point targets: attach nodes, part centers, fitted rim centers, and the mirror of the previous point across the part axis.".AsSpan()))
        MeasureState.SetPartFeatureSnapEnabled(snapNodes);
    bool snapVertices = MeasureState.PartVertexSnapEnabled;
    if (ConsoleUi.CheckboxRow("VERTICES AND EDGES".AsSpan(), "MtSnapVerts".AsSpan(), ref snapVertices,
            "Vertices, feature-edge midpoints, and sliding along feature edges (tank rims and other sharp or boundary edges).".AsSpan()))
        MeasureState.SetPartVertexSnapEnabled(snapVertices);
    ImGui.EndDisabled();
    ImGui.Unindent();
    ImGui.EndDisabled();
```

## Complete example

| File | Role |
|---|---|
| [DeltaVMap/Render/ConsoleUi.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/ConsoleUi.cs) | Checkbox row, log slider row, toned status line |
| [DeltaVMap/Render/InterstellarSection.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/InterstellarSection.cs) | Region headers, segmented presets, sliders, readouts, button |
| [MeasureTools/Core/ConsoleUi.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Core/ConsoleUi.cs) | Console-row helpers for the measure window |
| [MeasureTools/Features/Measure/MeasureWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureWindow.cs#L181-L262) | Mode toolbar and snapping section |

## Variations and gotchas

- **The helper comment can lie.** DeltaVMap's `ConsoleUi` header says the base window pushes the console widget style; in build 2026.10.10.5554 it does not for a window that never sets `UseConsoleChrome`. Explain this to whoever extends the helper, or push the style yourself ([approach 2](match-the-stock-console-ui-style-2.md)).
- **Why `UseConsoleChrome` is off here.** Turning it on makes `OnDrawUi` call the console path: `ConsoleStyle.BeginWindow` with its own bezel and title bar, a toolbar for the menu bar and a footer; `NoSavedSettings` and main-viewport pinning come with it.
- **`ConsoleWidgets` keeps row state in statics.** Never nest rows, and never leave one open across an exception.
- **Stock labels are upper case**; row helpers pass labels through unchanged.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of match-the-stock-console-ui-style](overview.md)
- [Approach 2: plain ImGui.Begin plus ConsoleStyle styling](match-the-stock-console-ui-style-2.md)
- [Approach 3: ConsoleStyle window skin unpinned from the main viewport](match-the-stock-console-ui-style-3.md)
- [Build a mod window on the game's ImGuiWindow base class](../subclass-the-stock-imguiwindow/overview.md)
- [Inject UI into the game's own windows and menus, including the orbit right-click menu](../../menus/inject-ui-into-a-stock-window/overview.md)
- [Build custom-drawn ImGui widgets, icons, trees and alert tiles on the draw list](../../draw-lists/build-custom-drawn-imgui-widgets/overview.md)
- Mod features: [Stock console widget panels](../../../../mods/ksa-deltavmap/features/stock-console-widget-panels.md),
  [Measure menu and tool window](../../../../mods/ksa-measuretools/features/measure-menu-and-tool-window.md)

[^ksa-consolewidgets]: KSA.ConsoleWidgets (decompiled, build 2026.10.10.5554)
[^ksa-consolestyle]: KSA.ConsoleStyle (decompiled, build 2026.10.10.5554)
[^ksa-imguiwindow]: KSA.ImGuiWindow (decompiled, build 2026.10.10.5554)
[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
