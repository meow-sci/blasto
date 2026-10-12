---
type: Mod Feature
title: Panel rows built on the game's ConsoleWidgets kit
description: The side panels draw toggles, presets, log sliders, readouts and status lines with the game's ConsoleWidgets and ConsoleStyle kit, copying the stock Transfer Planner's slider row
mod: ksa-deltavmap
category: ui
techniques: [imgui-window, console-widgets, stock-ui-reuse]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.ConsoleWidgets, KSA.ConsoleStyle, KSA.ImGuiWindow, KSA.TransferPlanner]
tags: [ui, imgui, console-widgets, console-style, stock-look, sliders, panels]
---

The map window's side panels look like the game's own console windows (the Transfer Planner,
the staging panel) because they are drawn with the same widget kit: `KSA.ConsoleWidgets` for
label-and-control rows, region headers, segmented preset buttons, key/value readouts and buttons,
and `KSA.ConsoleStyle` for the value font and the semantic text colors. Mechanism: a small
`ConsoleUi` helper wraps the three row shapes the panels need (checkbox row, logarithmic slider
row, toned status line) around `ConsoleWidgets.BeginRow` / `EndRow`, and the interstellar section
calls the kit directly for headers, presets, readouts and the stock-planner button.[^dvmap-consoleui][^dvmap-interstellarsection]

## How it works

| Panel element | Call | Where |
|---|---|---|
| Toggle row | `ConsoleUi.CheckboxRow` -> `ConsoleWidgets.BeginRow(label)`, `ConsoleWidgets.Checkbox(id, ref value, pending: false)`, `EndRow()` | Route options, view options, transfer-window markers, search isolate[^dvmap-routepanel][^dvmap-twrenderer][^dvmap-mapwindow] |
| Log slider row | `ConsoleUi.LogSliderRow` -> `BeginRow`, `ImGui.SetNextItemWidth(-1)`, `ImGui.SliderFloat("##id", ..., Logarithmic \| NoRoundToFormat \| AlwaysClamp)`, `EndRow` | Cruise speed (`V INF`), capture periapsis, antimatter percentages |
| Section header | `ConsoleWidgets.RegionHeader("INTERSTELLAR LEG")` (upper-cased by the widget) | Interstellar leg, capture, antimatter drive |
| Preset buttons | `ConsoleWidgets.Segmented(id, ReadOnlySpan<string> segments, activeIndex)` returning the selected index | 0.001 C / 0.01 C / 0.1 C / MAX |
| Readout | `ConsoleWidgets.Readout(key, value)` | Interstellar figures, fuel rows |
| Status line | `ConsoleUi.StatusText` -> `ConsoleStyle.PushValueFont()`, `ImGui.TextColored(ConsoleStyle.Positive / Pending / Danger / TextMuted)`, `PopFont()` | Feasibility verdicts, failures |
| Button | `ConsoleWidgets.Button("OPEN STOCK TRANSFER PLANNER")` | Stock planner bridge |

**Copying the stock slider row.** `ConsoleWidgets.SliderFloat` is linear. The stock planner draws
its decade-spanning sliders (max speed, antimatter percentages, target periapsis) with a private
`TransferPlanner.DrawSliderFloatRow`: `BeginRow`, a `"##" + id` label built on the stack,
`SetNextItemWidth(-1f)`, `ImGui.SliderFloat(..., ImGuiSliderFlags.Logarithmic)`, `EndRow`
([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L1460-L1471)).[^ksa-transferplanner]
`ConsoleUi.LogSliderRow` is the same shape with `NoRoundToFormat` (a small speed keeps its precision)
and `AlwaysClamp` added, plus an overload that reports `ImGui.IsItemActive()` so a caller can tell
a drag from its release. The capture-periapsis slider uses that to re-evaluate on change and once
more on release.

**Row layout without the style push.** `ConsoleWidgets.BeginRow` draws the row label itself
(console label font and color, hover wash, a third of the width) on the window draw list and moves
the cursor to the right-hand control column; `EndRow` pads the row to at least
`ConsoleStyle.FrameHeightPx`. `BeginRow`, `Checkbox`, `RegionHeader` and `Readout` call
`ConsoleStyle.EnsureInitialized` themselves, so they work in any ImGui window ([current/decomp/KSA/ConsoleWidgets.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleWidgets.cs#L384-L467)).[^ksa-consolewidgets]
The colors, frame padding, item spacing and label font of a stock console window come from
`ConsoleStyle.PushWidgetStyle` ([current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L575-L639)),
which stock windows push after `ConsoleStyle.BeginWindow` / `BeginBody` (the Transfer Planner does
so at [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L332-L336)) and which the
`ImGuiWindow` base pushes around `DrawContent` only on its `UseConsoleChrome` path
([current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L334-L355)).[^ksa-consolestyle][^ksa-imguiwindow]
`MapWindow` never sets `UseConsoleChrome`, so its content runs on the plain `ImGui.Begin` path
(L248-L271): labels, headers, readouts and the console checkbox are drawn by the kit in console
fonts and colors, while raw ImGui controls inside a row (the log sliders) and the spacing between
rows use the global ImGui style. The `ConsoleUi` header comment says the base window pushes the
console widget style; in build 2026.10.10.5554 it does not for this window.

## Lifecycle and timing

- Hook: `[StarMapAfterGui]` (StarMap's postfix on `Program.OnDrawUiViewports`), main thread,
  `frame:draw-ui`, inside `MapWindow.DrawContent` -> `DrawPanel` (the `##dvmap_panel_col` child)
  and the transfer-window overlay in `DrawCanvas`. Only while the window is shown and the HUD is
  visible.
- The widgets are immediate-mode: a changed value is returned the same frame, and the panels turn
  it into a route re-accumulation or a tree rebuild after the panel returns (see
  [route finding and accumulation](./route-finding-and-accumulation.md)). Nothing is written to
  game state.
- `StatusText` pushes and pops the value font and the wrap position inside one call, so no font
  or style push spans panels.

## Key code

| Path | Role |
|---|---|
| [DeltaVMap/Render/ConsoleUi.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/ConsoleUi.cs) | Checkbox row, log slider row, toned status line |
| [DeltaVMap/Render/InterstellarSection.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/InterstellarSection.cs#L374-L501) | Region headers, segmented presets, sliders, readouts, button |
| [DeltaVMap/Render/RoutePanelRenderer.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/RoutePanelRenderer.cs#L90-L124) | Route option toggles with disabled-hint fallbacks |
| [DeltaVMap/Render/TransferWindowRenderer.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/TransferWindowRenderer.cs#L101-L106) | Marker toggles, including the 3D map-mode opt-in |
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L903) | Search isolate toggle |

## Snippets

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

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `ConsoleWidgets.BeginRow`, `EndRow`, `Checkbox`, `RegionHeader`, `Segmented`, `Readout`, `Button` | [current/decomp/KSA/ConsoleWidgets.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleWidgets.cs) | Stock row layout and controls |
| `ConsoleStyle.PushValueFont`, `PopFont`, `Positive`, `Pending`, `Danger`, `TextMuted` | [current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs) | Value font and semantic colors for status lines |
| `ConsoleStyle.PushWidgetStyle` (not called) | same | The style a console-chrome window pushes; absent here |
| `ImGuiWindow.UseConsoleChrome`, `OnDrawUi` | [current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs) | Which window path pushes the console style |
| `TransferPlanner.DrawSliderFloatRow` (private, not called) | [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs) | The stock pattern `LogSliderRow` copies |

## Pitfalls and version notes

- `ConsoleWidgets` and `ConsoleStyle` are public game UI internals with no stability promise;
  signatures such as `Checkbox(id, ref value, pending)` and `Segmented(id, ReadOnlySpan<string>,
  activeIndex)` are as of build 2026.10.10.5554.
- To get the full stock look, either set `UseConsoleChrome = true` in an `ImGuiWindow` subclass
  constructor (as stock `GroundTrackWindow` and `TargetTrackWindow` do) or wrap the content in
  `ConsoleStyle.PushWidgetStyle` / `PopWidgetStyle`. DeltaVMap does neither; the visual difference
  was not checked in game.
- Every `BeginRow` needs its `EndRow` in the same frame; `EndRow` resets the row's pushed border
  colors and cursor, and a missing one leaves the next row misplaced.
- Pass hidden ids (`"##" + id`) to raw ImGui controls inside a row so the label column is the only
  visible label, as stock does.

## See also

- [Pannable, zoomable ImGui canvas](./subway-map-canvas-rendering.md)
- [Route finding and accumulation](./route-finding-and-accumulation.md)
- [Interstellar routes and fuel](./interstellar-routes-and-fuel.md)
- [Menu-bar entry and stock ImGuiWindow subclass](./map-window-and-menu-entry.md)
- [Stock Transfer Planner bridge](./stock-transfer-planner-bridge.md)
- [Match the stock UI: ConsoleWidgets rows inside an ImGuiWindow subclass without console chrome](../../../howto/ui/windows/match-the-stock-console-ui-style/match-the-stock-console-ui-style-1.md) - how-to article demonstrating this feature

[^dvmap-consoleui]: DeltaVMap/Render/ConsoleUi.cs
[^dvmap-interstellarsection]: DeltaVMap/Render/InterstellarSection.cs
[^dvmap-routepanel]: DeltaVMap/Render/RoutePanelRenderer.cs
[^dvmap-twrenderer]: DeltaVMap/Render/TransferWindowRenderer.cs
[^dvmap-mapwindow]: DeltaVMap/Render/MapWindow.cs
[^ksa-consolewidgets]: KSA.ConsoleWidgets (decompiled, build 2026.10.10.5554)
[^ksa-consolestyle]: KSA.ConsoleStyle (decompiled, build 2026.10.10.5554)
[^ksa-imguiwindow]: KSA.ImGuiWindow (decompiled, build 2026.10.10.5554)
[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
