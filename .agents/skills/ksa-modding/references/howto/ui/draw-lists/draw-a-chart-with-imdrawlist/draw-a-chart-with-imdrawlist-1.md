---
type: How-To
title: Chart a time series with nice axes, extrema, a dashed threshold and a hover crosshair
description: Draw a self-contained line chart with ImDrawList primitives, including 1-2-5-10 axis ticks, a time axis, area fill, min and max markers, an alert line and a crosshair tooltip
category: ui
group: draw-lists
topic: draw-a-chart-with-imdrawlist
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/kitten-engineer-redux/features/draw-list-line-graph]
techniques: [imgui-drawlist, harmony-postfix]
hooks: [harmony-postfix KSA.Vehicle.OnDrawUi]
lifecycle: [frame:draw-ui]
game_types:
  - Brutal.ImGuiApi.ImDrawListPtr
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImDrawListExtensions
  - KSA.Vehicle
tags: [chart, graph, time-series, axis, tooltip, draw-list]
---
Kitten-Engineer-Redux (KER) shows the history of a telemetry metric as a square chart: title, a NOW / MIN / AVG / MAX row, readable gridlines, a filled line, labelled extrema, the metric's alert threshold as a dashed line and a crosshair tooltip. All of it is `ImDrawListPtr` calls in one function, `GraphView.Draw`, with no ImPlot and no ImGui item. The function takes a `ReadOnlySpan<float>` of evenly spaced samples, so it can be pasted into any custom-drawn panel. Prerequisites: a mod that already has a draw list to draw on (here a Harmony postfix, see [Show live vehicle and editor data in an ImGui window or HUD](../../../../howto/ui/windows/show-live-vehicle-data-in-a-window/overview.md)). Demonstrated by [Draw-list line graph](../../../../mods/kitten-engineer-redux/features/draw-list-line-graph.md).

## Background: how the game does it

The game has no chart widget in its public UI toolkit; its own profiler uses ImPlot ([current/decomp/KSA/ProfilerPlots.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ProfilerPlots.cs#L141-L162)[^ksa-profilerplots]) and everything else the player sees (orbit lines, the FPS counter) goes through draw lists. The wrapper methods KER calls are `ImDrawListExtensions.AddLine`, `AddRectFilled` and `AddText` ([current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L51-L81)[^brutal-imguiapi-imdrawlistextensions]), and `ImGui.CalcTextSize`, `GetMousePos` and `PushFont(font, size)` ([current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L5872-L5875)[^brutal-imguiapi-imgui]). A draw list only records vertices; nothing is drawn until `ImGui.Render()`, so the chart costs CPU for its primitives each frame.

## Lifecycle and timing

`GraphView.Draw` is called from the flight HUD's History section, which runs inside KER's postfix on `Vehicle.OnDrawUi` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3848)[^ksa-vehicle]):

```csharp
// Patch_FlightHud.cs L6-L18
[HarmonyPatch(typeof(Vehicle), nameof(Vehicle.OnDrawUi))]
internal static class Patch_FlightHud
{
    private static void Postfix(Vehicle __instance, GameViewport inViewport)
    {
        if (inViewport != Program.MainViewport)
            return;
        if (Program.ControlledVehicle != __instance)
            return;

        FlightHud.Draw(__instance, inViewport);
    }
}
```

That is `frame:draw-ui`, between `PrepareImGui` and `ImGui.Render()` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2262-L2286)[^ksa-program]), and only on frames where the HUD is shown. The function keeps no state between frames; the samples are filled elsewhere, so the chart reads a span by reference. See [Game loop and mod lifecycle](../../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Take a span of samples and a draw list.** The signature below is the whole contract: `dl` is whichever window draw list the caller is in, `origin`/`width`/`y` the layout cursor, `samples` the series, `sampleInterval` seconds between samples, `thresholdValue` an optional alert level. The function returns the next free `y`:

```csharp
// GraphView.cs L11-L31
public static float Draw(ImDrawListPtr dl, float2 origin, float width, float y,
    string title, string unit, ReadOnlySpan<float> samples, float sampleInterval,
    float? thresholdValue, bool thresholdAbove, bool zeroBaseline)
{
    const float pad = 10f;
    float side = Math.Clamp(width, 220f, 640f);
    float x0 = origin.X + Math.Max(0f, (width - side) * 0.5f);
    float lineHeight = ImGui.GetTextLineHeight();
    ImColor8 labelColor = PanelKit.ToColor(PanelKit.LabelColor);
    ImColor8 valueColor = PanelKit.ToColor(PanelKit.ValueColor);
    ImColor8 gridColor = PanelKit.ToColor(PanelKit.GraphGridColor);

    float2 boxMin = new float2(x0, y);
    float2 boxMax = new float2(x0 + side, y + side);
    dl.AddRectFilled(in boxMin, in boxMax, PanelKit.ToColor(PanelKit.GraphBgColor), PanelKit.CornerRadius);

    ImGui.PushFont(ImGui.GetFont(), ImGui.GetFontSize() * 1.15f);
    float2 titlePos = new float2(x0 + pad, y + 8f);
    dl.AddText(in titlePos, PanelKit.ToColor(PanelKit.HeaderColor), title);
    ImGui.PopFont();
    float titleHeight = lineHeight * 1.15f;
```

2. **Bail out early with fewer than two samples**, and compute min, max (with their indices), average and latest in one pass. The indices later place the extrema labels:

```csharp
// GraphView.cs L33-L62
if (samples.Length < 2)
{
    string waiting = "Collecting samples";
    float2 waitingSize = ImGui.CalcTextSize(waiting);
    float2 waitingPos = new float2(x0 + (side - waitingSize.X) * 0.5f, y + (side - waitingSize.Y) * 0.5f);
    dl.AddText(in waitingPos, labelColor, waiting);
    return y + side + 8f;
}

float min = samples[0];
float max = samples[0];
float sum = 0f;
int minIndex = 0;
int maxIndex = 0;
for (int i = 0; i < samples.Length; i++)
{
    float v = samples[i];
    sum += v;
    if (v < min) { min = v; minIndex = i; }
    if (v > max) { max = v; maxIndex = i; }
}
float average = sum / samples.Length;
float latest = samples[samples.Length - 1];

float statsY = y + 8f + titleHeight + 6f;
float statColumn = (side - pad * 2f) / 4f;
DrawStat(dl, x0 + pad, statsY, "NOW", FormatPrecise(latest), labelColor, valueColor, lineHeight);
DrawStat(dl, x0 + pad + statColumn, statsY, "MIN", FormatPrecise(min), labelColor, valueColor, lineHeight);
DrawStat(dl, x0 + pad + statColumn * 2f, statsY, "AVG", FormatPrecise(average), labelColor, valueColor, lineHeight);
DrawStat(dl, x0 + pad + statColumn * 3f, statsY, "MAX", FormatPrecise(max), labelColor, valueColor, lineHeight);
```

3. **Choose the value axis.** Widen the range to include the threshold, optionally snap to zero, then let `NiceAxis` pick a step of 1, 2, 5 or 10 times a power of ten. The tick count follows the plot height so labels never touch:

```csharp
// GraphView.cs L64-L91
float plotTop = statsY + lineHeight * 2f + 14f;
float plotBottom = y + side - (lineHeight + 14f);
float plotHeight = Math.Max(30f, plotBottom - plotTop);

float rangeMin = min;
float rangeMax = max;
if (thresholdValue.HasValue)
{
    rangeMin = Math.Min(rangeMin, thresholdValue.Value);
    rangeMax = Math.Max(rangeMax, thresholdValue.Value);
}
if (zeroBaseline && rangeMin >= 0f && rangeMin < rangeMax * 0.5f)
    rangeMin = 0f;

int maxTicks = Math.Clamp((int)(plotHeight / (lineHeight * 2.4f)), 3, 8);
NiceAxis(rangeMin, rangeMax, maxTicks, out float axisMin, out float axisMax, out float step);
float axisRange = Math.Max(axisMax - axisMin, 1e-6f);
int tickCount = Math.Clamp((int)MathF.Round(axisRange / step), 1, 20);

float labelWidth = 0f;
for (int i = 0; i <= tickCount; i++)
    labelWidth = Math.Max(labelWidth, ImGui.CalcTextSize(FormatTick(axisMin + i * step, step)).X);

float plotLeft = x0 + labelWidth + 16f;
float plotRight = x0 + side - 14f;
float plotWidth = Math.Max(30f, plotRight - plotLeft);

float YOf(float value) => plotBottom - (value - axisMin) / axisRange * plotHeight;
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

4. **Draw gridlines and labels, measuring as you go.** `CalcTextSize` positions each label right-aligned to the plot, and the widest tick label sets the plot's left edge (step 3 measured it). Then the time axis: choose the first step from `1, 2, 5, 10, 15, 30, 60, 120, 300, 600` seconds that gives at most `plotWidth / 70` divisions, and count labels back from "now" at the right edge:

```csharp
// GraphView.cs L93-L130
for (int i = 0; i <= tickCount; i++)
{
    float value = axisMin + i * step;
    float yy = YOf(value);
    float2 gridStart = new float2(plotLeft, yy);
    float2 gridEnd = new float2(plotRight, yy);
    dl.AddLine(in gridStart, in gridEnd, gridColor);

    string tickLabel = FormatTick(value, step);
    float2 tickSize = ImGui.CalcTextSize(tickLabel);
    float2 tickPos = new float2(plotLeft - 8f - tickSize.X, yy - tickSize.Y * 0.5f);
    dl.AddText(in tickPos, labelColor, tickLabel);
}

float span = (samples.Length - 1) * sampleInterval;
int maxDivisions = Math.Clamp((int)(plotWidth / 70f), 2, 8);
float timeStep = TimeSteps[TimeSteps.Length - 1];
for (int i = 0; i < TimeSteps.Length; i++)
{
    if (span / TimeSteps[i] <= maxDivisions)
    {
        timeStep = TimeSteps[i];
        break;
    }
}
for (float t = 0f; t <= span + 0.001f; t += timeStep)
{
    float xx = plotRight - t / span * plotWidth;
    float2 gridStart = new float2(xx, plotTop);
    float2 gridEnd = new float2(xx, plotBottom);
    dl.AddLine(in gridStart, in gridEnd, gridColor);

    string tickLabel = FormatTime(t);
    float2 tickSize = ImGui.CalcTextSize(tickLabel);
    float tickX = Math.Clamp(xx - tickSize.X * 0.5f, x0 + 2f, x0 + side - tickSize.X - 2f);
    float2 tickPos = new float2(tickX, plotBottom + 5f);
    dl.AddText(in tickPos, labelColor, tickLabel);
}
```

5. **Draw the series.** At most one point per horizontal pixel (index resampling), a translucent filled rectangle per segment down to the axis, and three 1 px lines offset by -1, 0, +1 to fake thickness. The Brutal `AddLine` takes a `thickness` parameter, so one call with `thickness: 3f` is a simpler alternative:

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

6. **Add the dashed threshold line.** There is no dashed primitive; step along x and add a 5 px segment every 10 px. Put the label above the line, or below it near the top edge:

```csharp
// GraphView.cs L139-L156
if (thresholdValue.HasValue)
{
    float thresholdY = YOf(thresholdValue.Value);
    ImColor8 warningColor = PanelKit.ToColor(PanelKit.WarningColor);
    for (float dx = plotLeft; dx < plotRight; dx += 10f)
    {
        float2 dashStart = new float2(dx, thresholdY);
        float2 dashEnd = new float2(Math.Min(dx + 5f, plotRight), thresholdY);
        dl.AddLine(in dashStart, in dashEnd, warningColor);
    }
    string thresholdLabel = $"alert {(thresholdAbove ? ">" : "<")} {FormatPrecise(thresholdValue.Value)}";
    float2 thresholdSize = ImGui.CalcTextSize(thresholdLabel);
    float labelY = thresholdY - thresholdSize.Y - 2f;
    if (labelY < plotTop)
        labelY = thresholdY + 2f;
    float2 thresholdPos = new float2(plotRight - thresholdSize.X - 4f, labelY);
    dl.AddText(in thresholdPos, warningColor, thresholdLabel);
}
```

7. **Add the hover crosshair.** The test is a plain rectangle check on `ImGui.GetMousePos()`; the nearest sample index follows from the x position, and the tooltip box flips to the left near the right edge:

```csharp
// GraphView.cs L194-L233
float2 mouse = ImGui.GetMousePos();
if (mouse.X >= plotLeft && mouse.X <= plotRight && mouse.Y >= plotTop && mouse.Y <= plotBottom)
{
    int hoverIndex = Math.Clamp((int)MathF.Round((mouse.X - plotLeft) / plotWidth * (samples.Length - 1)), 0, samples.Length - 1);
    float hoverValue = samples[hoverIndex];
    float hoverX = plotLeft + plotWidth * hoverIndex / (samples.Length - 1);
    float hoverY = YOf(hoverValue);
    float age = (samples.Length - 1 - hoverIndex) * sampleInterval;

    ImColor8 crossColor = PanelKit.ToColor(new float4(1f, 1f, 1f, 0.35f));
    float2 crossTop = new float2(hoverX, plotTop);
    float2 crossBottom = new float2(hoverX, plotBottom);
    dl.AddLine(in crossTop, in crossBottom, crossColor);

    float2 hoverMin = new float2(hoverX - 4f, hoverY - 4f);
    float2 hoverMax = new float2(hoverX + 4f, hoverY + 4f);
    dl.AddRectFilled(in hoverMin, in hoverMax, valueColor);

    string line1 = unit.Length > 0 ? $"{FormatPrecise(hoverValue)} {unit}" : FormatPrecise(hoverValue);
    string line2 = age < 0.01f ? "now" : $"{FormatTime(age).Replace("-", string.Empty)} ago";
    float2 size1 = ImGui.CalcTextSize(line1);
    float2 size2 = ImGui.CalcTextSize(line2);
    float boxWidth = Math.Max(size1.X, size2.X) + 16f;
    float boxHeight = lineHeight * 2f + 12f;
    float boxX = hoverX + 14f;
    if (boxX + boxWidth > x0 + side - 4f)
        boxX = hoverX - 14f - boxWidth;
    float boxY = Math.Clamp(hoverY - boxHeight - 10f, y + 4f, y + side - boxHeight - 4f);

    float2 borderMin = new float2(boxX - 1f, boxY - 1f);
    float2 borderMax = new float2(boxX + boxWidth + 1f, boxY + boxHeight + 1f);
    dl.AddRectFilled(in borderMin, in borderMax, PanelKit.ToColor(PanelKit.AccentColor), PanelKit.CornerRadius);
    float2 tipMin = new float2(boxX, boxY);
    float2 tipMax = new float2(boxX + boxWidth, boxY + boxHeight);
    dl.AddRectFilled(in tipMin, in tipMax, PanelKit.ToColor(PanelKit.WindowBgColor), PanelKit.CornerRadius);
    float2 line1Pos = new float2(boxX + 8f, boxY + 5f);
    dl.AddText(in line1Pos, valueColor, line1);
    float2 line2Pos = new float2(boxX + 8f, boxY + 5f + lineHeight + 2f);
    dl.AddText(in line2Pos, labelColor, line2);
}
```

8. **Call it from your panel**, supplying a threshold only when an alert exists for the metric:

```csharp
// FlightHud.cs L437-L445
float? threshold = null;
bool thresholdAbove = false;
AlertMetric? alertMetric = AlertForHistory(_historyMetric);
if (alertMetric.HasValue && HudAlerts.TryGetThreshold(alertMetric.Value, out float thresholdValue, out thresholdAbove))
    threshold = thresholdValue;

ReadOnlySpan<float> history = TelemetryHistory.GetSamples(_historyMetric);
y = GraphView.Draw(dl, origin, width, y, graphTitle, unit, history,
    (float)TelemetryHistory.SampleInterval, threshold, thresholdAbove, true);
```

## Complete example

| File | Role |
|---|---|
| [GraphView.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/GraphView.cs) | The chart, axes, formatters and extrema labels |
| [FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L421-L467) | Metric chips, threshold lookup, the call, export buttons |
| [Patch_FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/Patch_FlightHud.cs) | The `Vehicle.OnDrawUi` postfix that provides the draw list |

## Variations and gotchas

- **Hover uses the raw mouse position**, not `IsItemHovered`, so the tooltip also appears when another window covers the chart. Reserve the area with an `InvisibleButton` (approach 2) and test `IsItemHovered` when that matters.
- **Even spacing assumed.** The time axis is `(n - 1) * sampleInterval`; gaps while the HUD was hidden are drawn as continuous.
- **Index resampling drops spikes.** When there are more samples than pixels the loop takes every n-th sample; take min and max per pixel column if short peaks matter.
- **Manual layout.** The function returns the next `y`; mixing it with ImGui widgets needs a `Dummy` of the same height (see [Build custom-drawn ImGui widgets, icons, trees and alert tiles on the draw list](../../../../howto/ui/draw-lists/build-custom-drawn-imgui-widgets/overview.md)).
- **Version.** Checked against build 2026.10.10.5554. The draw-list wrapper signatures (`ImColor8`, `ImString`) are Brutal-specific and may change.

## See also

- [Overview of the chart topic](overview.md)
- [Approach 2: function plot with InvisibleButton space reservation](draw-a-chart-with-imdrawlist-2.md)
- [Approach 3: scope and relief map](draw-a-chart-with-imdrawlist-3.md)
- [Build custom-drawn ImGui widgets, icons, trees and alert tiles on the draw list](../../../../howto/ui/draw-lists/build-custom-drawn-imgui-widgets/overview.md)
- Mod feature: [Draw-list line graph](../../../../mods/kitten-engineer-redux/features/draw-list-line-graph.md)

[^ksa-profilerplots]: KSA.ProfilerPlots (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imdrawlistextensions]: Brutal.ImGuiApi.ImDrawListExtensions (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
