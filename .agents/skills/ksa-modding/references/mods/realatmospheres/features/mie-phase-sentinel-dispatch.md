---
type: Mod Feature
title: Mie phase sentinel dispatch
description: Replaces MiePhaseFunction with a dispatcher that treats out-of-range XML asymmetry values as flags, selecting a Mie-theory Mars dust phase (fit or GLSL LUT) or a two-part Pluto haze phase without new uniforms
mod: realatmospheres
category: shaders
techniques: [glsl-shader, xml-patch, code-generation]
hooks: [starmap StarMapBeforeMain, harmony-prefix RenderCore.ShaderModuleUtils.FromFile]
lifecycle: [startup:starmap-before-main, render:atmosphere]
game_types: [KSA.AtmosphereRenderer, KSA.MieScatteringReference]
tags: [shaders, glsl, mie, phase-function, mars, pluto, lut, sentinel]
---

The stock phase function is one Cornette-Shanks lobe per colour channel, parameterized by the XML
`<PhaseFunctionAsymmetry X Y Z>` ([current/Content/Core/Shaders/Atmosphere/AtmosphereFunctions.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/AtmosphereFunctions.glsl#L12-L22)).
Martian dust (a blue halo around the Sun, blue sunsets) and Pluto's haze (as bright at 20 degrees of
phase as at 67, then 37 times brighter by 167) cannot be fitted by one lobe. The mod replaces the
function with a dispatcher and uses asymmetry values no real lobe would have as flags: `X < -0.5`
means Mars dust, `Z < -1.5` means "forward lobe of asymmetry X carrying share Y, rest isotropic".[^ra-payloads]

## How it works

- **Replace the span.** `PatchAtmosphereFunctions` cuts from the stock comment
  `// Cornette-Shanks aproximation` to the end of `MiePhaseFunction` and inserts the Mars dust phase
  block followed by the dispatcher ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L606-L621)). Every shader
  that includes `AtmosphereFunctions.glsl` (the LUTs, the sky, the haze march) gets it.
- **Two levels for Mars.** If `assets/MarsDustPhaseLut.glsl` exists, its exact per-angle Mie phase
  (361 samples from 0 to 180 degrees, per channel) is used; otherwise the embedded three-lobe
  Henyey-Greenstein fit. The LUT file ends with its own copy of `MiePhaseFunction`, which is cut off
  so that the mod's single dispatcher (with the Pluto branch) is always the one compiled
  ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L51-L62)). Deleting the asset falls back to level 1.
- **Flags come from the XML.** Mars's Mie block sets `PhaseFunctionAsymmetry X="-1" Y="-1" Z="-1"`
  ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L39-L50)); Pluto's sets `Z="-2"` with X and Y as lobe asymmetry and
  share ([AtmosphereTables.Generated.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/AtmosphereTables.Generated.cs#L286)). The renderer passes
  the vector unmodified into the uniform buffer
  ([current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L1143)),[^ksa-atmosphererenderer]
  so no new uniform, binding or C# hook is needed.
- **The same flag gates the dust density.** `RaBaseDensity` uses the double-exponential dust profile
  only when `mieAsymmetry.x < -0.5` (see
  [Measured density tables in the atmosphere shaders](measured-density-tables-in-shaders.md)).

## Lifecycle and timing

- **`startup:starmap-before-main`** for the text patch, **`startup:asset-load`** for compilation.
- **`render:atmosphere` (GPU).** `MiePhaseFunction` is evaluated per sample in the atmosphere compute
  passes. The dispatcher's branches are uniform per draw (one body per dispatch), so they cost little
  divergence.

## Key code

| Path | Role |
|---|---|
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L51-L62) | Choose level 1 or level 2, cut the LUT file's own dispatcher |
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L606-L621) | Replace the stock span |
| [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L1922-L1990) | Level-1 dust phase and the dispatcher |
| [mars_dust_phase.glsl](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/mars_dust_phase.glsl) | Generator output the level-1 constants were copied from |
| [make_mars_phase_lut.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_mars_phase_lut.py#L96-L134) | Writes the level-2 LUT file |

## Snippets

```glsl
// Payloads.cs L1966-L1989 (GLSL inside a C# raw string)
// Cornette-Shanks aproximation for mie phase function
// With a different asymmetry per-component
// PATCHED (Mie dust): asymmetry.x < -0.5 selects the Martian dust phase above (Level 1 fit or Level 2 LUT).
// PATCHED (Pluto's haze): asymmetry.z < -1.5 selects a forward Henyey-Greenstein lobe of asymmetry x carrying the
// share y of the light, the rest isotropic: the haze's measured phase function (Cheng et al. 2017), which one lobe
// cannot fit (as bright at 20 degrees of phase as at 67, then 5x by 148 and 37x by 167).
vec3 MiePhaseFunction(float cosLight, vec3 asymmetry)
{
    if (asymmetry.x < -0.5)
        return MarsDustPhaseFunction(cosLight);
    if (asymmetry.z < -1.5)
    {
        float g = asymmetry.x;
        return vec3(asymmetry.y * (1.0 - g * g) / (4.0 * PI * pow(1.0 + g * g - 2.0 * g * cosLight, 1.5))
                    + (1.0 - asymmetry.y) / (4.0 * PI));
    }

    vec3 asymmetrySq = asymmetry * asymmetry;

    vec3 denom = 1.0 + asymmetrySq - 2.0 * asymmetry * cosLight;
    float numerator = 1.0 + cosLight * cosLight;

    return 1.5 / (4.0 * PI) * (1.0 - asymmetrySq) * pow(denom, vec3(-1.5)) * numerator / (2.0 + asymmetrySq);
}
```

```csharp
// ShadowBuilder.cs L56-L62
        string lut = Path.Combine(assetsDir, "MarsDustPhaseLut.glsl");
        string mars = File.Exists(lut) ? ReadLf(lut).Trim('\n') : Payloads.PatchedPhaseBlock;
        int ownDispatch = mars.IndexOf(Payloads.StockPhaseStart, StringComparison.Ordinal);
        if (ownDispatch >= 0)
            mars = mars[..ownDispatch].TrimEnd('\n');
        _phaseBlock = mars + "\n\n" + Payloads.MiePhaseDispatch;
        if (File.Exists(lut)) Log("Mars phase: LEVEL 2 (exact bhmie LUT)");
```

## Game APIs touched

| Member | Source | Used for |
|---|---|---|
| `MiePhaseFunction(float, vec3)` (GLSL) | [current/Content/Core/Shaders/Atmosphere/AtmosphereFunctions.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/AtmosphereFunctions.glsl#L12-L22) | Replaced |
| `AtmosphereData.MiePhaseFunctionAsymmetry` | [current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L1136-L1164) | Carries the XML vector, including the flag values |

## Pitfalls and version notes

- **Flags must stay out of the physical range.** The stock function divides by `2 + g^2` and
  `pow(1 + g^2 - 2 g cos, -1.5)`; a body that happens to use `X = -0.6` for back-scattering would be
  routed to Mars's dust. Choose sentinels no tuned body would use.
- **The in-game atmosphere editor shows the flags as asymmetries.** Editing them live changes which
  branch runs.
- **Level-1 knee removed.** The checked-in `mars_dust_phase.glsl` still divides by a knee
  (`p / (1 + p / 8)`), while the copy embedded in `Payloads.cs` returns the raw phase; the comment says
  the double-exponential dust density now tames the sunset instead
  ([mars_dust_phase.glsl](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/mars_dust_phase.glsl#L16-L23), [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L1951-L1958)).

## See also

- [Measured density tables in the atmosphere shaders](measured-density-tables-in-shaders.md)
- [Texture and phase asset scripts](texture-and-phase-asset-scripts.md)
- [Per-body atmosphere optics in the system XML](atmosphere-optics-in-system-xml.md)
- [Smuggle data into stock shaders: cut the stock MiePhaseFunction and insert a dispatcher on asymmetry sentinels](../../../howto/shaders/stock-shaders/send-extra-data-to-stock-shaders-without-new-uniforms/send-extra-data-to-stock-shaders-without-new-uniforms-4.md) - how-to article demonstrating this feature

[^ra-payloads]: Payloads.cs
[^ksa-atmosphererenderer]: KSA.AtmosphereRenderer (decompiled, build 2026.10.10.5554)
