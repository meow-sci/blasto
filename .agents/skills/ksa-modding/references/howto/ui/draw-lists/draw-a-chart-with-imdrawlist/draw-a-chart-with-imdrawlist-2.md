---
type: How-To
title: Plot a game function against altitude with InvisibleButton space reservation
description: Sample PhysicalAtmosphereReference at 101 altitudes and draw density and pressure curves in a normal ImGui window using a reserved canvas, grid, labels and a selected-altitude marker
category: ui
group: draw-lists
topic: draw-a-chart-with-imdrawlist
approach: 2
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/compendium/features/atmosphere-profile-graph]
techniques: [imgui-drawlist, imgui-window]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types:
  - Brutal.ImGuiApi.ImDrawListPtr
  - Brutal.ImGuiApi.ImGui
  - KSA.PhysicalAtmosphereReference
  - KSA.AtmosphereReference
  - KSA.Celestial
tags: [chart, function-plot, atmosphere, altitude, invisible-button, draw-list]
---
Compendium's "Atmospheric Properties" window plots atmospheric density and pressure against height above the surface, with a slider that moves a marker along each curve and a unit combo for pressure. The chart is a 65-line function that draws into the window's own draw list, and it shows the minimum recipe for any function-of-a-game-variable plot: sample the game, reserve a canvas, map data to pixels, draw. Demonstrated by [Atmosphere density and pressure graphs](../../../../mods/compendium/features/atmosphere-profile-graph.md). Prerequisite: a StarMap mod with a `[StarMapAfterGui]` method (see [Organise a mod window: tabs, button tabs, tables and pop-out sections](../../../../howto/ui/windows/build-a-tabbed-imgui-window/overview.md) for window structure).

## Background: how the game does it

A celestial's atmosphere is data, not a simulation: `AtmosphereReference.Physical` holds a `PhysicalAtmosphereReference` with a sea-level density and pressure and a `ScaleHeight`, and both public queries are single exponentials of altitude ([current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L80-L88)[^ksa-physicalatmospherereference]):

```csharp
// current/decomp/KSA/PhysicalAtmosphereReference.cs L80-L88
public double GetAtmosphericPressureAtAltitude(double altitudeInMeters)
{
	return (double)SeaLevelPressure * Math.Exp((0.0 - altitudeInMeters) / ScaleHeight.InMeters());
}

public double GetAtmosphericDensityAtAltitude(double altitudeInMeters)
{
	return (double)SeaLevelDensity * Math.Exp((0.0 - altitudeInMeters) / ScaleHeight.InMeters());
}
```

`Height` (the boundary height, computed in `CalculateBoundaryHeight` from the greater of the heights where density falls to 1e-9 and pressure to 1e-4, [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L43-L47)[^ksa-physicalatmospherereference]) is the natural x range. The body template exposes the data as `BodyTemplate.AtmosphereReference` ([current/decomp/KSA/AtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereReference.cs#L10-L11)[^ksa-atmospherereference]), which is null for airless bodies.

## Lifecycle and timing

Everything runs in `frame:draw-ui`, from Compendium's `[StarMapAfterGui]` method, which draws the main window and, from inside it, the graph popup (the call is made right after `ImGui.Begin("Compendium")`). The data is static template data loaded with the system, so there is no solver-timing concern. When the HUD is hidden `StarMapAfterGui` does not fire and the window is not drawn. See [Game loop and mod lifecycle](../../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Open the chart from a button.** A `ConsoleWidgets.Button` inside `PushID`/`PopID` keyed by the body id stores the body and sets the window flag:

```csharp
// src/CompendiumMain.cs L649-L656
ImGui.PushID(new ImString($"DensityGraph_{celestial.Id}"));
if (ConsoleWidgets.Button("ATMOSPHERIC GRAPHS"))
{
    atmosphereGraphCelestial = celestial as Celestial;
    showAtmosphereDensityWindow = atmosphereGraphCelestial != null;
    atmosphereGraphSelectedAltitudeKm = 0f;
}
ImGui.PopID();
```

2. **Guard the window each frame.** Return unless the flag is set and the body has a physical atmosphere, then begin a separate top-level window (ImGui allows beginning a new window while another is open, so the popup can be called from inside the main window's body). `ImGuiCond.FirstUseEver` sets the size only once:

```csharp
// src/CompendiumMain.cs L1094-L1112
private static void DrawAtmosphereDensityGraphPopup()
{
    if (!showAtmosphereDensityWindow || atmosphereGraphCelestial == null)
    {
        return;
    }

    var atmosphere = atmosphereGraphCelestial.BodyTemplate.AtmosphereReference?.Physical;
    if (atmosphere == null)
    {
        return;
    }

    ImGui.SetNextWindowSize(atmosphereGraphWindowSize, ImGuiCond.FirstUseEver);
    if (!ImGui.Begin("Atmospheric Properties", ref showAtmosphereDensityWindow, ImGuiWindowFlags.None))
    {
        ImGui.End();
        return;
    }
```

3. **Sample the game model at 101 evenly spaced altitudes**, tracking the maximum for axis scaling. Unit conversion happens at sampling time:

```csharp
// src/CompendiumMain.cs L1114-L1130
double cutoffMeters = atmosphere.Height.InMeters();
const int sampleCount = 101;
var densities = new float[sampleCount];
var pressures = new float[sampleCount];
float maximumDensity = 0f;
float maximumPressure = 0f;

for (int index = 0; index < sampleCount; index++)
{
    double altitudeMeters = cutoffMeters * index / (sampleCount - 1);
    float density = (float)atmosphere.GetAtmosphericDensityAtAltitude(altitudeMeters);
    float pressure = ConvertPressureFromPascals((float)atmosphere.GetAtmosphericPressureAtAltitude(altitudeMeters), atmosphereGraphPressureUnitIndex);
    densities[index] = density;
    pressures[index] = pressure;
    maximumDensity = Math.Max(maximumDensity, density);
    maximumPressure = Math.Max(maximumPressure, pressure);
}
```

```csharp
// src/CompendiumMain.cs L1155-L1167
private static float ConvertPressureFromPascals(float pascals, int unitIndex)
{
    return unitIndex switch
    {
        0 => pascals,
        1 => pascals / 1_000f,
        2 => pascals / 101_325f,
        3 => pascals / 100_000f,
        4 => pascals / 6_894.757f,
        5 => pascals / 133.322387f,
        _ => pascals
    };
}
```

4. **Reserve the canvas and compute the plot rectangle.** `InvisibleButton` advances the ImGui layout cursor by the full canvas and makes the area hit-testable, while the margins leave room for labels. Take the draw list afterwards:

```csharp
// src/CompendiumMain.cs L1171-L1191
float2 canvasPosition = ImGui.GetCursorScreenPos();
float2 canvasSize = new float2(ImGui.GetContentRegionAvail().X, 270f);
const float leftMargin = 110f;
const float topMargin = 20f;
const float rightMargin = 20f;
const float bottomMargin = 55f;
float2 plotTopLeft = new float2(canvasPosition.X + leftMargin, canvasPosition.Y + topMargin);
float2 plotBottomRight = new float2(canvasPosition.X + canvasSize.X - rightMargin, canvasPosition.Y + canvasSize.Y - bottomMargin);
float plotWidth = plotBottomRight.X - plotTopLeft.X;
float plotHeight = plotBottomRight.Y - plotTopLeft.Y;
float valueRange = maximumValue > 0f ? maximumValue : 1f;

ImGui.InvisibleButton(canvasId, canvasSize, ImGuiButtonFlags.None);
var drawList = ImGui.GetWindowDrawList();
var panelColor = new ImColor8(18, 28, 36, 255);
var gridColor = new ImColor8(57, 78, 88, 255);
var axisColor = new ImColor8(190, 210, 220, 255);
var labelColor = new ImColor8(210, 220, 226, 255);
var selectionColor = new ImColor8(255, 202, 92, 255);

drawList.AddRectFilled(canvasPosition, new float2(canvasPosition.X + canvasSize.X, canvasPosition.Y + canvasSize.Y), panelColor, 0f, ImDrawFlags.None);
```

5. **Grid and labels.** Five divisions each way; label x with the height in km and y with the value, using `CalcTextSize` to right-align y labels:

```csharp
// src/CompendiumMain.cs L1193-L1208
const int tickCount = 5;
for (int tick = 0; tick <= tickCount; tick++)
{
    float fraction = tick / (float)tickCount;
    float x = plotTopLeft.X + plotWidth * fraction;
    float y = plotBottomRight.Y - plotHeight * fraction;
    drawList.AddLine(new float2(x, plotTopLeft.Y), new float2(x, plotBottomRight.Y), gridColor, 1f);
    drawList.AddLine(new float2(plotTopLeft.X, y), new float2(plotBottomRight.X, y), gridColor, 1f);

    string heightLabel = (cutoffKilometers * fraction).ToString("N1");
    string valueLabel = (valueRange * fraction).ToString("N2");
    drawList.AddText(new float2(x - 12f, plotBottomRight.Y + 10f), labelColor, new ImString(heightLabel));
    ImString valueLabelText = new ImString(valueLabel);
    float valueLabelWidth = ImGui.CalcTextSize(valueLabelText).X;
    drawList.AddText(new float2(plotTopLeft.X - valueLabelWidth - 8f, y - ImGui.GetFontSize() * 0.5f), labelColor, valueLabelText);
}
```

6. **Axes and titles**, then the curve as consecutive `AddLine` segments with 2 px thickness. Values are clamped into `[0, valueRange]` so a stray sample cannot draw outside the plot:

```csharp
// src/CompendiumMain.cs L1210-L1215
drawList.AddLine(plotTopLeft, new float2(plotTopLeft.X, plotBottomRight.Y), axisColor, 2f);
drawList.AddLine(new float2(plotTopLeft.X, plotBottomRight.Y), plotBottomRight, axisColor, 2f);
drawList.AddText(new float2(plotTopLeft.X + plotWidth * 0.5f - 65f, canvasPosition.Y + canvasSize.Y - 24f), labelColor, new ImString("Height above surface (km)"));
ImString axisTitle = new ImString(verticalAxisTitle);
float axisTitleWidth = ImGui.CalcTextSize(axisTitle).X;
drawList.AddText(new float2(plotTopLeft.X + (plotWidth - axisTitleWidth) * 0.5f, canvasPosition.Y + 2f), labelColor, axisTitle);
```

```csharp
// src/CompendiumMain.cs L1217-L1232
for (int index = 1; index < values.Length; index++)
{
    float previousValue = Math.Clamp(values[index - 1], 0f, valueRange);
    float value = Math.Clamp(values[index], 0f, valueRange);
    float previousX = plotTopLeft.X + plotWidth * (index - 1) / (values.Length - 1);
    float x = plotTopLeft.X + plotWidth * index / (values.Length - 1);
    float previousY = plotBottomRight.Y - plotHeight * previousValue / valueRange;
    float y = plotBottomRight.Y - plotHeight * value / valueRange;
    drawList.AddLine(new float2(previousX, previousY), new float2(x, y), curveColor, 2f);
}

float selectedX = plotTopLeft.X + plotWidth * selectedAltitudeKilometers / cutoffKilometers;
float selectedY = plotBottomRight.Y - plotHeight * Math.Clamp(selectedValue, 0f, valueRange) / valueRange;
drawList.AddLine(new float2(selectedX, plotTopLeft.Y), new float2(selectedX, plotBottomRight.Y), selectionColor, 1.5f);
drawList.AddLine(new float2(selectedX - 5f, selectedY), new float2(selectedX + 5f, selectedY), selectionColor, 2f);
drawList.AddLine(new float2(selectedX, selectedY - 5f), new float2(selectedX, selectedY + 5f), selectionColor, 2f);
```

7. **Draw the selected-altitude marker** (the vertical line and cross at the end of step 6), driven by the `SliderFloat("Selected altitude (km)")` above the chart.

## Complete example

| File | Role |
|---|---|
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L1094-L1153) | Window, sampling, slider, unit combo |
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L1169-L1233) | `DrawAtmosphereChart` |
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L645-L656) | Open button |

## Variations and gotchas

- **Resampling every frame.** The mod samples 202 function calls each frame; they are cheap exponentials. For an expensive source (terrain heights) cache the samples, as in [approach 3](draw-a-chart-with-imdrawlist-3.md).
- **Linear density axis.** Density spans many orders of magnitude, so the curve hugs the floor above a few scale heights. Plot `log10` of the value or use a fraction of scale height for a readable profile.
- **No hover and no gridline decimals.** Tick labels use fixed `N1`/`N2` formats and are not rounded to nice values; [approach 1](draw-a-chart-with-imdrawlist-1.md) shows how to fix both.
- **Colours.** `ImColor8` takes byte RGBA values.
- **Version.** `PhysicalAtmosphereReference` is the build 2026.10.10.5554 model; a different atmosphere model in a later build changes both the sampled values and `Height`.

## See also

- [Overview of the chart topic](overview.md)
- [Approach 1: time series with nice axes](draw-a-chart-with-imdrawlist-1.md)
- [Approach 3: scope and relief map](draw-a-chart-with-imdrawlist-3.md)
- Mod feature: [Atmosphere density and pressure graphs](../../../../mods/compendium/features/atmosphere-profile-graph.md)

[^ksa-physicalatmospherereference]: KSA.PhysicalAtmosphereReference (decompiled, build 2026.10.10.5554)
[^ksa-atmospherereference]: KSA.AtmosphereReference (decompiled, build 2026.10.10.5554)
