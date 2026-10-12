---
type: How-To
title: "Smuggle data into stock shaders: cut the stock MiePhaseFunction and insert a dispatcher on asymmetry sentinels"
description: Select new shader behaviour from out-of-range values of an existing XML parameter and ship lookup data as generated GLSL source, with no new uniform, binding or texture
category: shaders
group: stock-shaders
topic: send-extra-data-to-stock-shaders-without-new-uniforms
approach: 4
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/realatmospheres/features/mie-phase-sentinel-dispatch
  - mods/realatmospheres/features/texture-and-phase-asset-scripts
techniques: [glsl-shader, xml-patch]
hooks: [starmap StarMapBeforeMain]
lifecycle: [startup:starmap-before-main, startup:asset-load, render:atmosphere]
game_types:
  - KSA.MieScatteringReference
  - KSA.AtmosphereRenderer
  - KSA.Atmosphere.Rendering.ShaderStructs.AtmosphereData
tags: [sentinel, dispatch, glsl, lookup-table, generated-source, mie-phase, atmosphere, realatmospheres]
---
Sometimes the data you want to send is a mode, not a number: this body uses a different phase function. If an XML parameter already reaches the shader unmodified, a value outside its physical range can act as a flag. RealAtmospheres does
this with the Mie phase asymmetry to give Mars a dust phase function and Pluto a two-part haze phase, and it ships the Mie-theory lookup table inside the GLSL source so no texture or binding is needed. You need a text-patched
shader tree ([Replace or text-patch the game's GLSL without editing game files](../replace-or-patch-game-glsl-without-editing-game-files/overview.md)) and the XML values set in your system files.

## Background: how the game does it

The stock `MiePhaseFunction(float cosLight, vec3 asymmetry)` is one Cornette-Shanks lobe per colour channel ([current/Content/Core/Shaders/Atmosphere/AtmosphereFunctions.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/AtmosphereFunctions.glsl#L12-L22)):

```glsl
// current/Content/Core/Shaders/Atmosphere/AtmosphereFunctions.glsl L12-L22
// Cornette-Shanks aproximation for mie phase function
// With a different asymmetry per-component
vec3 MiePhaseFunction(float cosLight, vec3 asymmetry)
{
    vec3 asymmetrySq = asymmetry * asymmetry;
    
    vec3 denom = 1.0 + asymmetrySq - 2.0 * asymmetry * cosLight;
    float numerator = 1.0 + cosLight * cosLight;

    return 1.5 / (4.0 * PI) * (1.0 - asymmetrySq) * pow(denom, vec3(-1.5)) * numerator / (2.0 + asymmetrySq);
}
```

The asymmetry comes from the body's XML `<PhaseFunctionAsymmetry X Y Z>` (`MieScatteringReference.PhaseFunctionAsymmetry`, a `Vector3Reference` defaulting to 0.8). `AtmosphereRenderer.FillPlanetAtmosphereData` copies it unmodified into the uniform buffer
(`MiePhaseFunctionAsymmetry = new float4(atmoRef.Visual.MieScattering.PhaseFunctionAsymmetry, 0f)`, [current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L1143)), so a value written in XML reaches the shader with no C# hook. Every shader that includes
`AtmosphereFunctions.glsl` (the LUT passes, the sky, the haze march) calls this one function.

## Lifecycle and timing

| Step | When | Value |
|---|---|---|
| Text patch of the shader copy | In `[StarMapBeforeMain]`, before `Program.Main` | `startup:starmap-before-main` |
| Compilation of the patched file | `ModLibrary.LoadAll` | `startup:asset-load` |
| Dispatcher evaluated per sample | In the atmosphere compute passes; branches are uniform per draw (one body per dispatch) | `render:atmosphere` |

## Steps

1. **Choose sentinels no tuned body would use.** The stock function divides by `2 + g^2` and raises `1 + g^2 - 2 g cos` to -1.5, so real asymmetries are in (-1, 1) and Mars-like dust is flagged by `asymmetry.x < -0.5`, Pluto's two-part haze by
   `asymmetry.z < -1.5`. Mars's Mie block sets `X="-1" Y="-1" Z="-1"` ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L39-L50)); Pluto's generated block sets `X="0.853" Y="0.721" Z="-2"`, X being the lobe asymmetry and Y its share of the light ([AtmosphereTables.Generated.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/AtmosphereTables.Generated.cs#L286)). The in-game atmosphere editor shows the flags as asymmetries, and editing them live
   changes which branch runs.

2. **Write the dispatcher.** Branch on the sentinels first, then fall through to the stock formula unchanged:

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

   Source: [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L1966-L1989).

3. **Cut the stock span and insert the block.** `PatchAtmosphereFunctions` finds the stock comment `// Cornette-Shanks aproximation`, the function signature after it, and the closing `\n}`, and replaces everything between with the new block. If the anchor is missing it logs and leaves the file stock; if
   the marker is already present it does nothing:

   ```csharp
   // ShadowBuilder.cs L606-L621
       private static string PatchAtmosphereFunctions(string s)
       {
           if (s.Contains(Payloads.PhaseMarker))
               return s; // already patched

           int a = s.IndexOf(Payloads.StockPhaseStart, StringComparison.Ordinal);
           int sig = a < 0 ? -1 : s.IndexOf(Payloads.PhaseFnSignature, a, StringComparison.Ordinal);
           int b = sig < 0 ? -1 : s.IndexOf("\n}", sig, StringComparison.Ordinal);
           if (b < 0)
           {
               Log("WARN: MiePhaseFunction anchor not found in AtmosphereFunctions.glsl, skipping");
               return s;
           }
           b += "\n}".Length;
           return s[..a] + _phaseBlock + s[b..];
       }
   ```

   Source: [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L606-L621).

4. **Ship lookup data as GLSL source instead of a texture.** An offline script computes the exact Mie phase per channel (361 samples, 0 to 180 degrees) and writes a `const vec3[]` plus the lookup function. Splicing it into the dispatcher needs no binding, no sampler and no descriptor change:

   ```python
   # make_mars_phase_lut.py L96-L115
   entries = ",\n    ".join(
       f"vec3({phase[0][a]:.5g}, {phase[1][a]:.5g}, {phase[2][a]:.5g})" for a in range(N_ANG)
   )
   glsl = f"""// ---- Martian dust phase (LEVEL 2: exact bhmie Mie LUT, Schneegans et al. 2024) ----
   // Exact per-angle P(theta) per RGB for feldspar+hematite dust (bimodal log-normal).
   // NO knee compression - the double-exp dust density tames the sunset instead.
   // {N_ANG} samples, 0..180 deg. Generated by make_mars_phase_lut.py - do not hand-edit.
   #define MARS_DUST_LUT_N {N_ANG}
   const vec3 MARS_DUST_PHASE_LUT[MARS_DUST_LUT_N] = vec3[MARS_DUST_LUT_N](
       {entries}
   );

   vec3 MarsDustPhaseFunction(float cosLight)
   {{
       float theta = acos(clamp(cosLight, -1.0, 1.0));            // 0..PI
       float t = theta / PI * float(MARS_DUST_LUT_N - 1);
       int i = int(t);
       int j = min(i + 1, MARS_DUST_LUT_N - 1);
       return mix(MARS_DUST_PHASE_LUT[i], MARS_DUST_PHASE_LUT[j], t - float(i));
   }}
   ```

   Source: [make_mars_phase_lut.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_mars_phase_lut.py#L96-L115). At launch the mod uses the generated file if `assets/MarsDustPhaseLut.glsl` exists, otherwise an embedded three-lobe Henyey-Greenstein fit. The LUT file ends with its own copy of `MiePhaseFunction`, which is cut off so the mod's single
   dispatcher is always the one compiled:

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

   Source: [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L56-L62). Deleting the asset falls back to level 1.

5. **Let the same flag gate related code.** `RaBaseDensity` uses the double-exponential dust profile only when `mieAsymmetry.x < -0.5`, so one XML flag switches the phase and density together (see [Branch per body inside shared game shaders](../branch-per-body-inside-shared-game-shaders/overview.md)).

## Complete example

| File | Role |
|---|---|
| [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs) | Dispatcher text, level-1 dust phase, markers |
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs) | Level choice, span replacement |
| [make_mars_phase_lut.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_mars_phase_lut.py) | Offline Mie computation and GLSL emission |

## Variations and gotchas

- **Flags must stay out of the physical range.** A body that happens to use `X = -0.6` for back-scattering would be routed to Mars's dust.
- **Level-1 knee removed.** The checked-in `mars_dust_phase.glsl` still divides by a knee, while the copy embedded in `Payloads.cs` returns the raw phase.
- **The generated file is a build artifact.** `assets/` is git-ignored in the repository; only the scripts and the list of shipped outputs are checked in. Regenerate and re-check it offline ([Compile-check a mod's GLSL without launching the game](../../glsl-workflow/check-mod-shaders-offline-with-glslang/overview.md)).
- **Windows-only tooling paths** in the helper scripts (the game install and NVIDIA Texture Tools are hard-coded).

## See also

- Siblings: [temporary value swap](send-extra-data-to-stock-shaders-without-new-uniforms-1.md), [state-flag bits](send-extra-data-to-stock-shaders-without-new-uniforms-2.md), [material marker](send-extra-data-to-stock-shaders-without-new-uniforms-3.md), [overview](overview.md)
- [Mie phase sentinel dispatch](../../../../mods/realatmospheres/features/mie-phase-sentinel-dispatch.md), [Texture and phase asset scripts](../../../../mods/realatmospheres/features/texture-and-phase-asset-scripts.md)
- [Branch per body inside shared game shaders](../branch-per-body-inside-shared-game-shaders/overview.md), [Compile-check a mod's GLSL without launching the game](../../glsl-workflow/check-mod-shaders-offline-with-glslang/overview.md)
