---
type: How-To
title: "Extend the sky shader: a per-pixel raymarch inserted into Atmosphere.comp and blended by tangent altitude"
description: Insert a function before main() and branch the sky path in Atmosphere.comp so haze bodies march per pixel over the azimuth-free LUT result, with the cost culled where the effect cannot show
category: shaders
group: stock-shaders
topic: extend-stock-sky-and-cloud-shaders
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/realatmospheres/features/haze-band-sky-march]
techniques: [glsl-shader]
hooks: [starmap StarMapBeforeMain]
lifecycle: [startup:starmap-before-main, render:atmosphere]
game_types: [KSA.AtmosphereRenderer]
tags: [glsl, sky, atmosphere, raymarch, haze, lut, compute, culling, realatmospheres]
---
New Horizons saw some twenty stacked layers in Pluto's haze, and Titan shows detached layers in its upper haze. KSA's sky comes from a sky-view LUT with no azimuth axis, so it cannot show structure that varies around the planet. RealAtmospheres adds the structure with a per-pixel march
for haze bodies, blended over the LUT result. This article is the recipe: insert a march function that mirrors the stock integration, branch the sky path, blend by tangent altitude so no seam shows, and cull the work. Needs a text-patched shader tree
([Replace or text-patch the game's GLSL without editing game files](../replace-or-patch-game-glsl-without-editing-game-files/overview.md)); the clock and switch the march reads come from [Smuggle data into stock shaders through unused uniforms, state bits and sentinel values](../send-extra-data-to-stock-shaders-without-new-uniforms/overview.md).

## Background: how the game does it

`Atmosphere.comp` computes the sky for each pixel. Its sky branch (`if (!sampleAerialPerspective)`, [current/Content/Core/Shaders/Atmosphere/Atmosphere.comp](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/Atmosphere.comp#L465-L474)) takes `inscatter` from the sky LUT, which is indexed by view zenith and the angle to the Sun ([current/Content/Core/Shaders/Atmosphere/SkyLut.comp](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/SkyLut.comp#L40-L49)).
The stock integration steps `LUT_RAYMARCHING_ITERATIONS` (50) times with the transmittance and multiple-scattering LUTs ([current/Content/Core/Shaders/Atmosphere/AtmosphereLuts.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/AtmosphereLuts.glsl#L4)). The atmosphere compute passes run from `PlanetTransparenciesRenderer` while the frame is recorded, for the body being drawn.

## Lifecycle and timing

| Step | When | Value |
|---|---|---|
| `PatchHazeBandMarch` inserts the function and the sky branch into the shadow `Atmosphere.comp`, both anchors or neither | `[StarMapBeforeMain]` | `startup:starmap-before-main` |
| The march runs per sky pixel of a haze body | The per-pixel atmosphere compute pass | `render:atmosphere` |
| LUT-baked bands are computed when the transmittance and multiple-scattering LUTs are regenerated | Which is why switching the bands clears the kept LUTs | `render:atmosphere` |

## Steps

1. **Insert the function and patch the sky branch, both anchors or neither.** The insertion anchors on `void main()\n{` and the sky path on the stock branch text; if either does not match exactly once, skip the whole feature and log:

   ```csharp
   // ShadowBuilder.cs L693-L709
       private static void PatchHazeBandMarch(string shadersDir)
       {
           string path = Path.Combine(shadersDir, "Atmosphere", "Atmosphere.comp");
           if (!File.Exists(path)) { Log("WARN: Atmosphere.comp missing, skipping haze-band march"); return; }
           string s = ReadLf(path);
           if (s.Contains("RaymarchAtmosphereHorizontal")) { Log("haze-band march already applied"); return; }

           if (CountOf(s, Payloads.HazeBandMarchAnchor) != 1)
           { Log("WARN: Atmosphere.comp main() anchor not found, skipping haze-band march"); return; }
           if (CountOf(s, Payloads.HazeBandSkyStock) != 1)
           { Log("WARN: Atmosphere.comp sky-path anchor not found, skipping haze-band march"); return; }

           s = s.Replace(Payloads.HazeBandMarchAnchor, Payloads.HazeBandMarchFunc.Trim('\n') + "\n\n" + Payloads.HazeBandMarchAnchor);
           s = s.Replace(Payloads.HazeBandSkyStock, Payloads.HazeBandSkyPatch);
           File.WriteAllText(path, s);
           Log("horizontal haze-band march applied (sky)");
       }
   ```

   Source: [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L693-L709). The `void main()\n{` anchor matches only because KSA 2026.9.22 added a line at the top of `main`; keep anchors to the smallest stable text.

2. **Mirror the stock integrator exactly.** `RaymarchAtmosphereHorizontal` reuses the stock LUT lookups and step weights, so with the band term off it equals the LUT result; any drift shows as a seam where the two blend. Inside the loop, modulate the density by the band factor at each step's real direction, rotated about the body's spin axis by the haze angle,
   and skip steps that cannot contribute:

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

   Source: [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2432-L2443). The band field is a density multiplier built from hash and value noise ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2041-L2182)); with `horizDir = vec3(0)` it is the altitude-only form that the LUT density also uses, so the LUT result and the march agree.

3. **Blend, do not replace, and hide the seam.** Keep the LUT result as the base. Blend the march in by a weight that rises with the ray's tangent altitude, so rays grazing near the surface keep the LUT; keep the LUT transmittance:

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

   Source: [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2491-L2525).

4. **Control the cost.** The march is skipped when its blend weight is under 2%, when the LUT says the haze at that pixel is too dim for a band to show, per step when the step's possible contribution is under 0.2% of the pixel's light, and it stops once 99.8% of the light behind is absorbed
   ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2324-L2342), [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2426-L2460)). The mod also gives the player a switch that turns the march off entirely.

## Complete example

| File | Role |
|---|---|
| [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs) | Band factor, haze-body tests, the march function, anchors and the patched sky branch |
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs) | `PatchHazeBandMarch` |

## Variations and gotchas

- **Adapted to 2026.8.3.** The LUTs became per-planet `sampler2DArray`s indexed by `lutLayer` and per-planet data moved to a uniform buffer, so the march takes `lutLayer`; check these shapes after each game update.
- **Expensive by nature.** 50 steps with noise per sky pixel; the culling is what keeps it playable.
- **LUT-baked bands for thin hazes.** For Pluto and Triton the altitude-only band factor is also multiplied into the LUT density; Titan is excluded because its sky and aerial LUTs resolve altitude differently and the bands produced a seam at the disc edge.
- **Anchors.** `main()` anchor and the sky-branch text are both required; a changed game shader disables the feature and logs, the game stays stock.

## See also

- Siblings: [face lighting](extend-stock-sky-and-cloud-shaders-2.md), [map flow and lift](extend-stock-sky-and-cloud-shaders-3.md), [ring shadows](extend-stock-sky-and-cloud-shaders-4.md), [overview](overview.md)
- [Haze-band sky march](../../../../mods/realatmospheres/features/haze-band-sky-march.md), [Atmosphere uniform side channel](../../../../mods/realatmospheres/features/atmosphere-uniform-side-channel.md)
- [Branch per body inside shared game shaders](../branch-per-body-inside-shared-game-shaders/overview.md), [Raymarch a local volume or analytic sky effect into the HDR scene](../../compute-effects/raymarch-a-local-volume-into-the-hdr-scene/overview.md)
