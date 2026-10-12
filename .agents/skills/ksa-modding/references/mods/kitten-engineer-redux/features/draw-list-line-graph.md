---
type: Mod Feature
title: Draw-list line graph
description: A self-contained time-series chart drawn entirely with ImDrawList primitives, with nice-number axis ticks, a time axis, area fill, min/max/now markers, a dashed alert threshold line and a hover crosshair tooltip, without ImPlot
mod: kitten-engineer-redux
category: ui
techniques: [imgui-drawlist]
hooks: [harmony-postfix KSA.Vehicle.OnDrawUi]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImDrawListPtr]
tags: [imgui, drawlist, graph, chart, plot, telemetry, hover, tooltip]
---

The History section shows a square chart of the selected telemetry metric with a title, a NOW / MIN
/ AVG / MAX stat row, gridlines with readable tick labels, a filled line, labelled extrema, the alert
threshold for that metric as a dashed warning-coloured line, and a crosshair tooltip ("12.3 kN, 45s
ago") under the mouse. KSA ships an ImPlot binding, but this graph uses only the window draw list,
so its look follows the mod's own colour settings and it can be dropped into any custom-drawn panel.

## How it works

`GraphView.Draw(dl, origin, width, y, title, unit, samples, sampleInterval, threshold, thresholdAbove, zeroBaseline)`
([GraphView.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/GraphView.cs#L11-L236)):[^ker-graphview]

1. **Box.** A square `side = clamp(width, 220, 640)` centred in the available width, filled with the
   graph background colour. The title is drawn in a 1.15x font via `ImGui.PushFont(ImGui.GetFont(), size)`.
   With fewer than two samples it prints "Collecting samples" and returns.
2. **Stats.** One pass over the span gives min, max (with indices), average and latest.
3. **Value axis.** The range is widened to include the threshold and, if `zeroBaseline` and all values
   are non-negative and the minimum is below half the maximum, snapped to zero. `NiceAxis` picks a step
   of 1, 2, 5 or 10 times a power of ten for at most `plotHeight / (2.4 * lineHeight)` ticks (3 to 8)
   and floors/ceils the ends to multiples of it ([GraphView.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/GraphView.cs#L304-L322)).
   Tick labels use k/M suffixes and as many decimals as the step needs; the plot's left edge moves
   right by the widest label.
4. **Time axis.** The span is `(n - 1) * sampleInterval`; the step is the first of 1, 2, 5, 10, 15, 30,
   60, 120, 300, 600 s giving at most `plotWidth / 70` divisions; labels count back from "now" at the
   right edge.
5. **Series.** At most one point per horizontal pixel (the span is index-resampled to `plotWidth`
   points). Each segment adds a filled rectangle down to the axis in a 16 % alpha copy of the line
   colour and three 1 px `AddLine` passes offset by -1, 0 and +1 px to fake a thicker line.
6. **Markers.** Small filled squares at the latest point and at the min and max samples, with
   clamped "min x" and "max x" labels.
7. **Threshold.** If an enabled alert exists for the metric, a dashed line (5 px on, 5 px off) at its
   value and an "alert < x" label.
8. **Hover.** If `ImGui.GetMousePos()` is inside the plot rectangle, the nearest sample index is found
   from the x position, and a vertical crosshair, a point marker and a two-line tooltip box (accent
   border, window background) are drawn, flipped to the left near the right edge.

The function returns the next free y coordinate, like every other `PanelKit` widget, so the caller
continues its manual layout below it.

## Lifecycle and timing

- Drawn from the flight HUD's History section callback, inside the `Vehicle.OnDrawUi` postfix
  (`frame:draw-ui`), on whichever window draw list the section is currently in (docked HUD or popped-out
  window) ([FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L421-L445)).[^ker-flighthud]
- It reads the sample span by reference and keeps no state between frames; hover is recomputed each
  frame from the mouse position, so it needs no ImGui item or ID.

## Key code

| Path | Role |
|---|---|
| [GraphView.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/GraphView.cs) | The chart, axis and formatting helpers |
| [FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L421-L467) | Metric chips, threshold lookup, graph call, export buttons |

## Snippets

```csharp
// GraphView.cs L158-L182
        float4 lineBase = PanelKit.GraphLineColor;
        ImColor8 lineColor = PanelKit.ToColor(lineBase);
        ImColor8 fillColor = PanelKit.ToColor(new float4(lineBase.X, lineBase.Y, lineBase.Z, 0.16f));
        int pointCount = Math.Min(samples.Length, Math.Max(2, (int)plotWidth));
        float2 previous = default;
        for (int k = 0; k < pointCount; k++)
        {
            int index = (int)MathF.Round(k * (samples.Length - 1) / (float)(pointCount - 1));
            float px = plotLeft + plotWidth * k / (pointCount - 1);
            float py = YOf(samples[index]);
            float2 point = new float2(px, py);
            if (k > 0)
            {
                float2 fillMin = new float2(previous.X, py);
                float2 fillMax = new float2(px, plotBottom);
                dl.AddRectFilled(in fillMin, in fillMax, fillColor);
                for (int pass = -1; pass <= 1; pass++)
                {
                    float2 passStart = new float2(previous.X, previous.Y + pass);
                    float2 passEnd = new float2(point.X, point.Y + pass);
                    dl.AddLine(in passStart, in passEnd, lineColor);
                }
            }
            previous = point;
        }
```

```csharp
// GraphView.cs L304-L322
    private static void NiceAxis(float min, float max, int maxTicks, out float axisMin, out float axisMax, out float step)
    {
        float range = max - min;
        if (range < 1e-6f)
        {
            float pad = Math.Max(Math.Abs(max) * 0.1f, 1f);
            min -= pad;
            max += pad;
            range = max - min;
        }

        float rough = range / Math.Max(1, maxTicks - 1);
        float magnitude = MathF.Pow(10f, MathF.Floor(MathF.Log10(rough)));
        float normalized = rough / magnitude;
        float nice = normalized <= 1f ? 1f : normalized <= 2f ? 2f : normalized <= 5f ? 5f : 10f;
        step = nice * magnitude;
        axisMin = MathF.Floor(min / step) * step;
        axisMax = MathF.Ceiling(max / step) * step;
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `ImDrawListPtr.AddText(in float2, ImColor8, ImString)` and `AddLine` / `AddRectFilled` | [current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L211) | All drawing |
| `ImGui.PushFont(ImFontPtr, float)` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L5872) | Title at 1.15x size |
| `ImGui.ColorConvertFloat4ToU32(in float4)` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8105) | `float4` settings colours to packed colours |
| `ImGui.CalcTextSize`, `GetMousePos`, `GetTextLineHeight` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs) | Layout and hover |

No KSA types are involved; the chart is pure Brutal ImGui.[^brutal-imgui] [^brutal-drawlistext]

## Pitfalls and version notes

- Hover detection uses the raw mouse position, not `ImGui.IsItemHovered`, so the tooltip also shows
  when another window covers the graph.
- The time axis assumes evenly spaced samples; gaps while the HUD was hidden are drawn as continuous.
- Index resampling takes every n-th sample, so short spikes can disappear when there are more samples
  than pixels; take the min and max per pixel column if that matters.
- The three-pass line trick costs three line primitives per segment; the Brutal wrapper's
  `AddLine(..., float thickness = 1f)` ([current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L51))
  draws a thick line in one call.

## See also

- [Telemetry history and CSV export](telemetry-history-and-csv-export.md)
- [HUD stat tiles and threshold alerts](hud-widgets-and-threshold-alerts.md)
- [Draw-list widget kit with pop-out sections](draw-list-widget-kit.md)
- [Chart a time series with nice axes, extrema, a dashed threshold and a hover crosshair](../../../howto/ui/draw-lists/draw-a-chart-with-imdrawlist/draw-a-chart-with-imdrawlist-1.md) - how-to article demonstrating this feature

[^ker-graphview]: GraphView.cs
[^ker-flighthud]: FlightHud.cs
[^brutal-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^brutal-drawlistext]: Brutal.ImGuiApi.ImDrawListExtensions (decompiled, build 2026.10.10.5554)
