---
type: Mod Feature
title: Measured density tables in the atmosphere shaders
description: Rewrites the six exponential density evaluations in KSA's atmosphere shaders to one injected function that reads generated per-body ln-density tables keyed by radius and scale height
mod: realatmospheres
category: shaders
techniques: [glsl-shader, harmony-prefix, code-generation]
hooks: [starmap StarMapBeforeMain, harmony-prefix RenderCore.ShaderModuleUtils.FromFile]
lifecycle: [startup:starmap-before-main, render:atmosphere]
game_types:
  - RenderCore.ShaderModuleUtils
  - KSA.AtmosphereRenderer
  - KSA.Atmosphere.Rendering.ShaderStructs.AtmosphereData
tags: [shaders, glsl, atmosphere, rayleigh, mie, density, lut, mars, titan, venus]
---

The stock atmosphere shaders compute density as `exp(-altitude / scaleHeight)` for Rayleigh and Mie,
in two variants, at six places across four files: the transmittance and multiple-scattering LUT
functions, the aerial-perspective LUT, the per-pixel atmosphere pass and the god rays
([current/Content/Core/Shaders/Atmosphere/AtmosphereFunctions.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/AtmosphereFunctions.glsl#L56),
[current/Content/Core/Shaders/Atmosphere/Atmosphere.comp](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/Atmosphere.comp#L125)).
Real Atmospheres injects one function, `AtmosphereDensityFalloff`, and rewrites all six sites to call
it. Because every LUT and the per-pixel pass integrate the same function, transmittance, multiple
scattering and the sky stay consistent with each other.[^ra-payloads]

## How it works

1. **Inject the helper** before the include guard's `#endif` in `AtmosphereData.glsl`, so every shader
   that includes the atmosphere data sees it. The generated table GLSL replaces a
   `// __RA_DENSITY_TABLES__` placeholder inside the helper
   ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L627-L646)).
2. **Rewrite the sites.** Two stock expressions (`DensA` with `atmosphereData.*.w`, `DensB` with
   local scale-height variables) become calls that also pass the phase asymmetry, `bottomRadius` and
   `ozoneExtent` ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2274-L2289)). Each file needs its exact count
   (2, 1, 1, 2) or is skipped ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L648-L661)).
3. **Recognise the body in GLSL.** `RaDensityTable(bodyRadius, scaleHeight)` returns a table index
   only when the radius is within 20 km of a known body and the scale height matches the value the mod
   wrote into that body's XML to within 50 m; otherwise -1 and the stock exponential is used
   ([AtmosphereTables.Generated.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/AtmosphereTables.Generated.cs#L565-L588)). The radius test
   tolerates a moved `StartHeight` (the sibling Real Surfaces mod lowers it below the terrain floor);
   the scale-height test turns the table off if anyone else edits that atmosphere.
4. **Look up the table.** All tables are one `const float raDensityLn[2130]` array of `ln(rho/rho0)` on
   even altitude steps, with per-table offset, count and step arrays; `RaTableDensity` interpolates
   linearly in the log and continues the last slope past the top
   ([AtmosphereTables.Generated.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/AtmosphereTables.Generated.cs#L340-L347),
   [AtmosphereTables.Generated.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/AtmosphereTables.Generated.cs#L561-L596)). Rayleigh tables:
   Earth, Venus, Mars, Titan, Pluto, Jupiter, Saturn, Uranus, Neptune, Triton; Mie tables: Venus,
   Titan, Pluto, Jupiter, Saturn.
5. **Mars dust.** For the Mars sentinel (`miePhaseFunctionAsymmetry.x < -0.5`) the Mie density is a
   two-part double exponential, a compact layer at 0.44 of the scale height and an extended one at
   2.2, measured from Mars's datum radius rather than the sky's bottom in Hellas, so dust pools in the
   basins ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2219-L2241)).
6. **Titan's colour by altitude.** `RaRayleighCoefficients` keeps the XML colour for every body except
   Titan, whose Rayleigh coefficients blend to a gas-like lambda^-4 colour above 280 km; 13 Rayleigh
   extinction and scattering occurrences in four files are rewritten to call it, all-or-nothing per file
   ([AtmosphereTables.Generated.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/AtmosphereTables.Generated.cs#L598-L610),
   [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2291-L2313)).
7. **Bands for thin hazes.** For Pluto and Triton the haze-band factor is multiplied into the LUT
   density here (see [Haze-band sky march](haze-band-sky-march.md)).

## Lifecycle and timing

- **`startup:starmap-before-main`.** Text patches applied to the shadow copy; the shaders compile from
  it during `startup:asset-load` (see [Shader file redirect](shader-file-redirect.md)).
- **`render:atmosphere` (GPU).** The function runs inside the atmosphere compute passes that
  `PlanetTransparenciesRenderer` records for each atmospheric body: the transmittance,
  multiple-scattering and ambient LUTs (only when a body's LUTs are invalid), the sky and aerial LUTs,
  god rays and the compose pass ([current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L1044-L1094)).[^ksa-atmosphererenderer]
  The per-body inputs (`bottomRadius`, scale heights, asymmetry) come from the uniform buffer filled by
  `FillPlanetAtmosphereData` on the main thread during recording.

## Key code

| Path | Role |
|---|---|
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L623-L662) | `PatchMarsDensity`: inject helper, rewrite six sites |
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L664-L689) | `PatchRayleighColour` |
| [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L1992-L2272) | The helper: hashes and noise, haze-band factor, `RaBaseDensity`, `AtmosphereDensityFalloff` |
| [AtmosphereTables.Generated.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/AtmosphereTables.Generated.cs#L338-L611) | Generated `VisualDensityGlsl`: tables, keys, lookup, Titan colour |

## Snippets

```glsl
// Payloads.cs L2221-L2241 (GLSL inside a C# raw string)
vec2 RaBaseDensity(float seaLevelAltitude, float rayleighScaleHeight, float mieScaleHeight, vec3 mieAsymmetry, float bodyRadius)
{
    // compact 4 km + extended 20 km layers, expressed as fractions of the (working)
    // stock mieScaleHeight (=9 km) so this is UNIT-AGNOSTIC - seaLevelAltitude is in
    // METERS in this build, so a literal /4.0 capped dust to a 4-metre layer (bug).
    // Mars's dust is measured from the datum, the mean surface (r = 3389.5 km), not from the
    // sky's bottom at the floor of Hellas: it thickens into the basins and thins over the heights.
    float dustAltitude = seaLevelAltitude + (abs(bodyRadius - 3389500.0) < 20000.0 ? bodyRadius - 3389500.0 : 0.0);
    float mie = mieAsymmetry.x < -0.5
        ? 0.75 * exp(1.0 - exp(dustAltitude / (0.44 * mieScaleHeight))) + 0.25 * exp(1.0 - exp(dustAltitude / (2.2 * mieScaleHeight)))
        : exp(-seaLevelAltitude / mieScaleHeight);
    // Titan's absorber: the haze's shape up to the top of the main haze, then a fast drop
    int mieTable = RaMieDensityTable(bodyRadius, mieScaleHeight);
    if (mieTable >= 0)
        mie = RaTableDensity(mieTable, seaLevelAltitude);
    int rayTable = RaDensityTable(bodyRadius, rayleighScaleHeight);
    float ray = rayTable >= 0
        ? RaTableDensity(rayTable, seaLevelAltitude)
        : exp(-seaLevelAltitude / rayleighScaleHeight);
    return vec2(ray, mie);
}
```

```csharp
// Payloads.cs L2277-L2289
    public const string DensAStock = "exp(-seaLevelAltitude / vec2(atmosphereData.rayleighCoefficientsAndScaleHeight.w, atmosphereData.mieCoefficientsAndScaleHeight.w))";
    public const string DensAPatch = "AtmosphereDensityFalloff(seaLevelAltitude, atmosphereData.rayleighCoefficientsAndScaleHeight.w, atmosphereData.mieCoefficientsAndScaleHeight.w, atmosphereData.miePhaseFunctionAsymmetry.xyz, atmosphereData.bottomRadius, atmosphereData.ozoneExtent)";
    public const string DensBStock = "exp(-seaLevelAltitude / vec2(rayleighScaleHeight, mieScaleHeight))";
    public const string DensBPatch = "AtmosphereDensityFalloff(seaLevelAltitude, rayleighScaleHeight, mieScaleHeight, atmosphereData.miePhaseFunctionAsymmetry.xyz, atmosphereData.bottomRadius, atmosphereData.ozoneExtent)";

    // (file, stock, patch, expected-count) for the density sites
    public static readonly (string File, string Stock, string Patch, int Count)[] MarsDensitySites =
    {
        ("Atmosphere/AtmosphereFunctions.glsl", DensAStock, DensAPatch, 2),
        ("Atmosphere/AerialPerspectiveLut.comp", DensAStock, DensAPatch, 1),
        ("Atmosphere/Atmosphere.comp", DensBStock, DensBPatch, 1),
        ("Atmosphere/Godrays.glsl", DensBStock, DensBPatch, 2),
    };
```

```glsl
// AtmosphereTables.Generated.cs L590-L596 (GLSL inside a C# raw string)
float RaTableDensity(int table, float seaLevelAltitude)
{
    float x = max(seaLevelAltitude, 0.0) / raDensityStep[table];
    int i = min(int(x), raDensityCount[table] - 2);
    int o = raDensityOffset[table] + i;
    return exp(mix(raDensityLn[o], raDensityLn[o + 1], x - float(i)));
}
```

## Game APIs touched

| Member | Source | Used for |
|---|---|---|
| `struct AtmosphereData` (GLSL) | [current/Content/Core/Shaders/Atmosphere/AtmosphereData.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/AtmosphereData.glsl#L4-L17) | `bottomRadius`, scale heights in `.w`, `ozoneExtent`, `miePhaseFunctionAsymmetry` |
| `AtmosphereData` (C#) | [current/decomp/KSA.Atmosphere.Rendering.ShaderStructs/AtmosphereData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Atmosphere.Rendering.ShaderStructs/AtmosphereData.cs#L6-L52) | The matching host struct |
| `AtmosphereRenderer.FillPlanetAtmosphereData` | [current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L1136-L1164) | `BottomRadius = MeanRadius + StartHeight`, scale heights in metres |
| Density sites | [current/Content/Core/Shaders/Atmosphere/AerialPerspectiveLut.comp](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/AerialPerspectiveLut.comp#L69), [current/Content/Core/Shaders/Atmosphere/Godrays.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/Godrays.glsl#L66) | Rewritten expressions |

## Pitfalls and version notes

- **Units.** `seaLevelAltitude` and the scale heights are in metres in this build; an early version
  used kilometre constants and squeezed Mars's dust into a 4 m layer (comment in the snippet).
- **Large constant arrays.** A 2130-entry `const float[]` in an include compiles into every shader that
  includes `AtmosphereData.glsl`; shaderc accepts it, but it lengthens compile time for all of them.
- **Visual and flight tables are separate.** The shader tables are normalized to the visual bottom
  and stepped evenly; the flight tables are absolute and decimated. Both come from the same generator
  run, so they agree.
- **LUT-only bodies cannot vary horizontally.** The sky-view LUT has no azimuth axis
  ([current/Content/Core/Shaders/Atmosphere/SkyLut.comp](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/SkyLut.comp#L40-L48)),
  so any density modulation added here is concentric; that is why the haze bands need their own
  per-pixel march.

## See also

- [Measured flight atmospheres](measured-flight-atmospheres.md)
- [Measured-atmosphere code generator](measured-atmosphere-generator.md)
- [Mie phase sentinel dispatch](mie-phase-sentinel-dispatch.md)
- [Haze-band sky march](haze-band-sky-march.md)
- [Branch per body in shared shaders: measured ln-density tables recognised by radius and an exact scale height](../../../howto/shaders/stock-shaders/branch-per-body-inside-shared-game-shaders/branch-per-body-inside-shared-game-shaders-1.md) - how-to article demonstrating this feature

[^ra-payloads]: Payloads.cs
[^ksa-atmosphererenderer]: KSA.AtmosphereRenderer (decompiled, build 2026.10.10.5554)
