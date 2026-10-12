---
type: How-To
title: Sample the live density and pressure model by altitude
description: Plot a body's atmospheric density and pressure against altitude by sampling the game's own PhysicalAtmosphereReference functions at 101 points, so the chart follows any mod that replaces the profile
category: celestials
group: systems-and-stars
topic: read-body-properties-and-orbit-elements
approach: 3
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/compendium/features/atmosphere-profile-graph, mods/compendium/features/derived-body-properties]
techniques: [imgui-drawlist]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.PhysicalAtmosphereReference, KSA.AtmosphereReference, KSA.Celestial]
tags: [atmosphere, density, pressure, chart, imgui-drawlist]
---
Outcome: an "Atmospheric Properties" window with a density and a pressure chart and a slider for an altitude. Demonstrated by Compendium ([Atmosphere profile graph](../../../../mods/compendium/features/atmosphere-profile-graph.md), [Derived body properties](../../../../mods/compendium/features/derived-body-properties.md)). The chart-drawing technique is generic (see [draw a chart with ImDrawList](../../../ui/draw-lists/draw-a-chart-with-imdrawlist/overview.md)).

## Background: how the game does it

`PhysicalAtmosphereReference` has `GetAtmosphericDensityAtAltitude(m)` and `GetAtmosphericPressureAtAltitude(m)`, which in build 5554 are exponentials: sea-level value times `exp(-h / ScaleHeight)` ([current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L80-L88)). Its `Height` is the boundary where density reaches `1e-9 kg/m3` or pressure `1e-4 Pa` ([current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L42-L47)). A mod that replaces the profile (Real Atmospheres patches both functions, see [generate measured atmosphere tables](../../atmospheres-and-rings/generate-measured-atmosphere-tables-offline/overview.md)) is picked up by calling these functions rather than recomputing the exponential.

## Lifecycle and timing

Everything runs in `[StarMapAfterGui]` (`frame:draw-ui`), between ImGui `NewFrame` and `Render`; the chart reads only template data loaded with the system, so there is no timing concern with the solvers. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Guard for a missing atmosphere:** `BodyTemplate.AtmosphereReference?.Physical` is null for airless bodies.

2. **Sample 101 points from 0 to `Physical.Height`** through the game functions, tracking maxima for scaling, and convert pressure to the chosen unit:

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

3. **Reserve the chart area with `ImGui.InvisibleButton`** so layout advances, then draw into the window draw list: a filled panel, grid lines with `CalcTextSize` labels, axes, the curve as consecutive `AddLine` segments, and a vertical line plus a cross at the selected altitude.

4. **Show the derived numbers from the same template:** atmosphere height and sea-level pressure come from `BodyTemplate.AtmosphereReference.Physical` (`Height.InKilometers()`, `SeaLevelPressure`):

```csharp
// src/CdmAttachInfoBodyJsonData.cs L270-L281
    // If body has an atmosphere true/false
    bodyJsonData.HasAtmosphere = (bodyCelestial.BodyTemplate.AtmosphereReference != null) ? true : false;

    // Atmosphere height & SL pressure
    if (bodyCelestial.BodyTemplate.AtmosphereReference != null)
    {
        string atmosphereHeightKm = bodyCelestial.BodyTemplate.AtmosphereReference.Physical.Height.InKilometers().ToString("N1");
        bodyJsonData.AtmosphereHeightText = new ImString($"Atmosphere Height: {atmosphereHeightKm} km");

        bodyJsonData.SLPressureText = new ImString("Sea Level Pressure: " + bodyCelestial.BodyTemplate.AtmosphereReference.Physical.SeaLevelPressure.Atm.ToString("F3") + " atm");
    }
}    
```

## Complete example

| File | Role |
|---|---|
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L1094-L1153) | Window, sampling, slider, unit combo |
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L1169-L1233) | Draw-list chart |

## Variations and gotchas

- **The chart plots whatever the model returns.** A patched model with a boundary mismatch shows as a cut-off curve.
- **101 samples per frame** is cheap, but allocate arrays once if you chart every frame.
- **Units:** KSA stores pressure in pascals; `GetAtmosphericPressure(Camera)` returns atmospheres.

## See also

- [Overview](overview.md); [approach 2](read-body-properties-and-orbit-elements-2.md); [approach 4](read-body-properties-and-orbit-elements-4.md)
- [Atmosphere profile graph](../../../../mods/compendium/features/atmosphere-profile-graph.md)
- [Generate measured atmosphere tables](../../../celestials/atmospheres-and-rings/generate-measured-atmosphere-tables-offline/overview.md)
