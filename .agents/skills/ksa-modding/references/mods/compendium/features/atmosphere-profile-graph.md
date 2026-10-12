---
type: Mod Feature
title: Atmosphere density and pressure graphs
description: A secondary ImGui window samples a body's physical atmosphere model at 101 altitudes every frame and plots density and pressure curves with grid, axis labels and a draggable altitude marker on the window draw list, with six pressure units
mod: compendium
category: ui
techniques: [imgui-window, imgui-drawlist]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Celestial
  - KSA.CelestialTemplate
  - KSA.AtmosphereReference
  - KSA.PhysicalAtmosphereReference
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImDrawListExtensions
tags: [imgui, drawlist, chart, atmosphere, celestials]
---

For a body with an atmosphere, the "More Celestial Properties" section has an ATMOSPHERIC GRAPHS
button that opens an "Atmospheric Properties" window. It shows the height of the atmosphere, a slider
for an altitude, the density and pressure there, and two line charts (density and pressure against
height above the surface), with a unit combo for pressure (Pa, kPa, atm, bar, psi, mmHg). It is a
compact, dependency-free way to chart game data with plain ImGui draw-list calls.

## How it works

1. **Open.** The button is a `ConsoleWidgets.Button` inside `PushID`/`PopID` keyed by the body id; it
   stores the body and opens the popup flag ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L645-L656)).[^cmp-compendiummain]
2. **Window.** `DrawAtmosphereDensityGraphPopup` is called every frame right after the main window's
   `Begin`. It returns unless the flag is set and the body has
   `BodyTemplate.AtmosphereReference?.Physical`, then opens its own window with
   `ImGui.Begin("Atmospheric Properties", ref open)` and a first-use size
   ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L1094-L1112)). ImGui allows beginning a different window while another is
   open; it is drawn as a separate top-level window.
3. **Sample the model.** 101 samples from 0 to `Physical.Height` call
   `GetAtmosphericDensityAtAltitude` and `GetAtmosphericPressureAtAltitude`, tracking the maxima for
   scaling; pressure is converted to the selected unit ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L1114-L1167)). In
   build 2026.10.10.5554 both are exponentials of altitude over the atmosphere's scale height
   ([current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L80-L88)).[^ksa-physicalatmospherereference]
4. **Reserve space, then draw.** `DrawAtmosphereChart` takes the cursor's screen position, reserves a
   full-width, 270 px area with `ImGui.InvisibleButton` (so layout advances and the area is hit-testable),
   and draws into `ImGui.GetWindowDrawList()`: a filled panel, five grid lines each way with labels
   placed using `CalcTextSize`, two axes, axis titles, the curve as consecutive `AddLine` segments, and a
   vertical line plus a cross at the selected altitude ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L1169-L1233),
   [current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L51-L80), [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L6255)).[^ksa-brutal-imguiapi-imdrawlistextensions]
   [^ksa-brutal-imguiapi-imgui] Colors are `ImColor8` byte RGBA values.

## Lifecycle and timing

- **`frame:draw-ui`.** Everything runs inside the `[StarMapAfterGui]` window (StarMap's postfix on
  `Program.OnDrawUiViewports`), between ImGui `NewFrame` and `Render`
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2261-L2286)).[^ksa-program] Draw-list commands recorded here are rendered in the
  ImGui pass of the same frame.
- **State it reads.** Only the body's template data, which is loaded once with the system; no
  simulation state, so there is no timing concern with the solvers.

## Key code

| Path | Role |
|---|---|
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L645-L656) | Open button |
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L1094-L1153) | Window, sampling, slider, unit combo |
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L1155-L1167) | Pressure unit conversion |
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L1169-L1233) | Draw-list chart |

## Snippets

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

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `CelestialTemplate` via `Celestial.BodyTemplate`, `AstronomicalTemplate.AtmosphereReference` | [current/decomp/KSA/AstronomicalTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AstronomicalTemplate.cs#L57) | Find the atmosphere |
| `AtmosphereReference.Physical` | [current/decomp/KSA/AtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereReference.cs#L10-L11) | Physical model |
| `PhysicalAtmosphereReference.Height`, `GetAtmosphericDensityAtAltitude`, `GetAtmosphericPressureAtAltitude` | [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L23-L88) | Samples |
| `ImGui.GetWindowDrawList`, `InvisibleButton`, `GetCursorScreenPos`, `CalcTextSize` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L5648) | Canvas |
| `ImDrawListPtr.AddLine`, `AddRectFilled`, `AddText` | [current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L51-L222) | Drawing |

## Pitfalls and version notes

- **Allocates and samples every frame.** Two arrays and 204 model evaluations (101 altitudes times two,
  plus the selected altitude) per frame while the window is open; caching per body and unit would remove both.
- **Linear axes.** With an exponential profile, a linear scale flattens the upper atmosphere into the
  axis; a log scale would show it.
- **Division by the height.** The selection marker divides by the atmosphere height in km; a zero-height
  atmosphere would produce a NaN coordinate.
- **Tied to the main window.** The popup is only drawn while the Compendium window is open and not
  collapsed, because it is called from inside that window's code path.

## See also

- [Body statistics derived from live game data](derived-body-properties.md)
- [Celestial compendium window](celestial-info-window.md)
- [Pointer lines to the selected body](body-pointer-overlay.md)
- [Sample the live density and pressure model by altitude](../../../howto/celestials/systems-and-stars/read-body-properties-and-orbit-elements/read-body-properties-and-orbit-elements-3.md) - how-to article demonstrating this feature
- [Plot a game function against altitude with InvisibleButton space reservation](../../../howto/ui/draw-lists/draw-a-chart-with-imdrawlist/draw-a-chart-with-imdrawlist-2.md) - how-to article demonstrating this feature

[^cmp-compendiummain]: Compendium: src/CompendiumMain.cs
[^ksa-physicalatmospherereference]: KSA.PhysicalAtmosphereReference (decompiled, build 2026.10.10.5554)
[^ksa-brutal-imguiapi-imdrawlistextensions]: Brutal.ImGuiApi.ImDrawListExtensions (decompiled, build 2026.10.10.5554)
[^ksa-brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
