---
type: Mod Feature
title: Haze-band sky march
description: Drifting haze bands for Titan, Pluto and Triton, baked into the LUT density as shells for thin hazes and added to sky pixels by a per-pixel raymarch blended in only where a band could be visible
mod: realatmospheres
category: shaders
techniques: [glsl-shader, harmony-prefix]
hooks:
  - starmap StarMapBeforeMain
  - harmony-prefix RenderCore.ShaderModuleUtils.FromFile
  - harmony-prefix KSA.AtmosphereRenderer.FillPlanetAtmosphereData
lifecycle: [startup:starmap-before-main, render:atmosphere]
game_types: [KSA.AtmosphereRenderer, KSA.PlanetTransparenciesRenderer]
tags: [shaders, glsl, atmosphere, raymarch, haze, titan, pluto, triton, performance]
---

New Horizons saw some twenty stacked layers in Pluto's haze, and Titan shows detached layers in its
upper haze. KSA's sky is drawn from a sky-view LUT indexed only by view zenith and the angle to the
Sun, so it has no azimuth axis and cannot hold structure that varies around the planet
([current/Content/Core/Shaders/Atmosphere/SkyLut.comp](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/SkyLut.comp#L40-L48)).
Real Atmospheres does two things: it multiplies a band pattern into the haze density the LUTs
integrate (concentric shells, cheap), and for sky pixels of a haze body it runs its own per-pixel
march with a 3D-noise band field that undulates, breaks up, turns with the body and evolves with
sim time, blending that over the LUT result.[^ra-payloads]

## How it works

- **The band field.** `PlutoHazeBandFactor(altitude, horizDir, evolveT, bottomRadius)` returns a
  density multiplier that cuts the smooth haze into bands, each densest just under its ragged "lid" and
  keeping its column, with sub-layers, regional spacing, patchy coverage and per-band strength from
  hash and value noise ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2041-L2182)). With `horizDir = vec3(0)` it is the
  altitude-only form for the LUTs. Titan uses deeper bands (40 km) and none below 28 km.
- **Baked into the LUTs for thin hazes.** `AtmosphereDensityFalloff` applies the altitude-only factor
  to the Mie density for Pluto and Triton when the bands are on; Titan is excluded because its sky and
  aerial-perspective LUTs resolve altitude differently and the bands produced a seam at the disc edge
  ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2243-L2269)).
- **Per-pixel march for the sky.** `RaymarchAtmosphereHorizontal`, inserted before `main()` in
  `Atmosphere.comp`, mirrors the stock integration (transmittance LUT, multiple-scattering LUT, same
  step weights) but evaluates the band factor at each step's real direction, rotated about the body's
  spin axis by the haze angle ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2322-L2467)). It modulates Rayleigh on
  Titan (where the bulk haze is) and Mie on Pluto and Triton.
- **Blend, do not replace.** In the stock sky branch (`!sampleAerialPerspective`) the LUT result is
  computed as before; for haze bodies with the bands on, the march result is mixed in by a weight that
  rises with the ray's tangent altitude, so rays grazing near the surface keep the LUT and no seam
  shows at the geometry edge. Transmittance stays the LUT's ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2484-L2527)).
- **Cost control.** The march is skipped when its blend weight is under 2%, when the LUT says the
  haze at that pixel is too dim for a band to show, per step when the step's possible contribution is
  under 0.2% of the pixel's light, and it stops once 99.8% of the light behind is absorbed
  ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2324-L2342), [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2426-L2460)). The comment
  records the player-reported cost at Pluto that motivated this.
- **Switch and clock.** Whether bands are on, the animation clock and the spin come from the
  [Atmosphere uniform side channel](atmosphere-uniform-side-channel.md).

## Lifecycle and timing

- **`startup:starmap-before-main`.** `PatchHazeBandMarch` inserts the function and the sky branch into
  the shadow `Atmosphere.comp`, both anchors or neither ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L691-L709)).
- **`render:atmosphere` (GPU).** The march runs in the per-pixel atmosphere compute pass for the body
  being drawn; the LUT-baked bands are computed when the transmittance and multiple-scattering LUTs are
  (re)generated, which is why switching the bands clears the kept LUTs.

## Key code

| Path | Role |
|---|---|
| [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2041-L2211) | Band factor, haze-body tests, band switch |
| [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2243-L2269) | Bands baked into LUT density (Pluto, Triton) |
| [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2322-L2467) | Per-pixel horizontal march |
| [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2469-L2527) | Anchors and the patched sky branch |
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L691-L709) | `PatchHazeBandMarch` |

## Snippets

```glsl
// Payloads.cs L2491-L2525 (GLSL inside a C# raw string)
        if (RaBodyHasHaze(atmosphereData.bottomRadius) && RaHazeBandsOn(atmosphereData.ozoneExtent))
        {
            // Base above = the sky LUT, which is continuous with the aerial LUT at the disk edge
            // (so no seam there). Blend in the per-pixel banded march ONLY for rays grazing high in
            // the halo (tangent altitude above the band region); rays grazing near the surface keep
            // the LUT, hiding the march-vs-LUT brightness step that was seaming at the geometry edge.
            float rayT = max(dot(planetPosition, worldViewDir), 0.0);
            float tangentAlt = length(planetPosition - worldViewDir * rayT) - atmosphereData.bottomRadius;
            // Triton's haze is a fraction as deep as Pluto's (11 km scale height, seen to 30 km): by 28 km there is
            // 8% of it left, so the march comes in over its first band instead, from just above its terrain (which
            // tops out 2 km over the sky's bottom).
            bool raTriton = RaHazeBodyIsTriton(atmosphereData.bottomRadius);
            float bandStart = atmosphereData.bottomRadius > 2000000.0 ? 30000.0 : raTriton ? 2500.0 : 3000.0;  // Titan high, Pluto low
            float bandRange = atmosphereData.bottomRadius > 2000000.0 ? 110000.0 : raTriton ? 8000.0 : 25000.0; // WIDE on Titan: spreads any
                                                                                            // base march-vs-LUT difference out so it can't read as a line
            float wMarch = smoothstep(bandStart, bandStart + bandRange, tangentAlt);
            // only where it can be seen (see raHazeBlendLeast above): not for a sliver of a share, and not where
            // the haze the LUT has just given is too dim for a band to show in it
            wMarch = max(wMarch - raHazeBlendLeast, 0.0) / (1.0 - raHazeBlendLeast);
            float raPixelLight = max(inscatter.r, max(inscatter.g, inscatter.b));
            float raSunLight = max(global.lighting.sunColor.r, max(global.lighting.sunColor.g, global.lighting.sunColor.b));
            wMarch *= smoothstep(raHazeSeenFrom, raHazeSeenBy, raPixelLight * raSunLight);
            if (wMarch > 0.0)
            {
                float raBandDist = inAtmosphereDistance;
                if (planetIntersections.x >= 0.0)
                    raBandDist = min(startDistance + inAtmosphereDistance, planetIntersections.x) - startDistance;
                vec3 raMInscatter, raMTransmittance;
                RaymarchAtmosphereHorizontal(worldViewDir, startDistance, raBandDist, planetPosition,
                    global.lighting.sunPosition.xyz, atmosphereDataUbo.planetRadius, atmosphereData,
                    float(atmosphereDataUbo.atmosphereLutLayer), LUT_RAYMARCHING_ITERATIONS, raPixelLight, raMInscatter, raMTransmittance);
                inscatter = mix(inscatter, raMInscatter, wMarch);
                // keep the LUT transmittance (don't mix) - avoids a transmittance step at the blend edge
            }
        }
```

```glsl
// Payloads.cs L2432-L2443 (GLSL inside a C# raw string)
        vec3 raStepGives = totalTransmittance * stepSize
            * (raTitan ? densityFalloff.x * rayleigh * (lightReceived * rayleighPhase + multipleScatteringLight)
                       : densityFalloff.y * atmosphereData.mieCoefficientsAndScaleHeight.xyz * (lightReceived * miePhase + multipleScatteringLight));
        if (max(raStepGives.r, max(raStepGives.g, raStepGives.b)) > raLeast)
        {
            vec3 hazeDir = raRotateAxis(zenithDirection, spinAxis, rotAngle);           // rotate the haze field over time
            float band = PlutoHazeBandFactor(seaLevelAltitude, hazeDir, evolveT, atmosphereData.bottomRadius); // vertical + horizontal + time
            if (raTitan)
                densityFalloff.x *= band;   // Titan etc.: Rayleigh is the bright bulk haze
            else
                densityFalloff.y *= band;   // Pluto, Triton: Mie is the forward haze
        }
```

## Game APIs touched

| Member | Source | Used for |
|---|---|---|
| Sky branch of `Atmosphere.comp` `main()` | [current/Content/Core/Shaders/Atmosphere/Atmosphere.comp](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/Atmosphere.comp#L401-L480) | Anchor `if (!sampleAerialPerspective)`; locals `inAtmosphereDistance`, `startDistance`, `planetIntersections` reused |
| `SkyLut.comp` parameterization | [current/Content/Core/Shaders/Atmosphere/SkyLut.comp](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/SkyLut.comp#L40-L48) | Why a LUT cannot hold horizontal bands |
| `AtmosphereDataUbo` | [current/Content/Core/Shaders/Atmosphere/AtmosphereDataUbo.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/AtmosphereDataUbo.glsl#L11-L33) | `planetRadius`, `atmosphereLutLayer`, ring slots |
| `AtmosphereRenderer.RenderWithCompute` | [current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L1044-L1094) | LUT regeneration only while a body's LUTs are invalid |

## Pitfalls and version notes

- **Mirror the stock integrator exactly.** The march reuses the stock LUT lookups and step weighting
  so that, with the band term off, it matches the LUT result; any drift between the two shows as a
  seam where they blend.
- **Anchor note.** The insertion matches only `void main()\n{` because KSA 2026.9.22 added a
  ring-shadow shared-memory write as the first line of `main` ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2469-L2472)).
- **Adapted to 2026.8.3.** The LUTs became per-planet `sampler2DArray`s indexed by `lutLayer` and
  per-planet data moved to the uniform buffer; the march takes `lutLayer` for that reason
  ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2349-L2351)).
- **Expensive by nature.** It takes `LUT_RAYMARCHING_ITERATIONS` (50) steps with noise per sky pixel
  ([current/Content/Core/Shaders/Atmosphere/AtmosphereLuts.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/AtmosphereLuts.glsl#L4));
  the culling above is what keeps it playable, and the player switch turns it off entirely.

## See also

- [Atmosphere uniform side channel](atmosphere-uniform-side-channel.md)
- [Measured density tables in the atmosphere shaders](measured-density-tables-in-shaders.md)
- [ModMenu page and TOML settings](modmenu-page-and-toml-settings.md)
- [Extend the sky shader: a per-pixel raymarch inserted into Atmosphere.comp and blended by tangent altitude](../../../howto/shaders/stock-shaders/extend-stock-sky-and-cloud-shaders/extend-stock-sky-and-cloud-shaders-1.md) - how-to article demonstrating this feature

[^ra-payloads]: Payloads.cs
