---
type: How-To
title: "Fix cloud lighting: light a thick raymarch step at its first optical depth by bisection"
description: Patch RaymarchCloud.comp so a dense step is lit where its optical depth passes 1, with a short near-sample shadow march toward the Sun, keeping dense decks bright at any step size
category: shaders
group: stock-shaders
topic: extend-stock-sky-and-cloud-shaders
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/realatmospheres/features/cloud-face-lighting]
techniques: [glsl-shader]
hooks: [starmap StarMapBeforeMain]
lifecycle: [startup:starmap-before-main, render:atmosphere]
game_types: [KSA.Atmosphere.Rendering.CloudRenderer, KSA.AtmosphereRenderer]
tags: [glsl, clouds, raymarch, optical-depth, bisection, self-shadowing, realatmospheres]
---
KSA's volumetric cloud raymarch takes one density sample at the end of each step and lights that point, and its steps grow with distance (to 25-30 km on the giants). In a dense deck one step holds tens of optical depths, so the light is looked up kilometres inside the cloud where almost
none arrives, and the deck darkens as the camera backs away. RealAtmospheres lights a thick step where its optical depth passes 1, found by halving the step up to five times. The mod's comment quantifies the fault: Jupiter's dense type renders at 0.79 of its finely stepped brightness from 300 km,
0.41 from 2,000 km and 0.14 from 10,000 km. This article is the recipe for patching the inner loop of a raymarch pass safely. Needs a text-patched shader tree ([Replace or text-patch the game's GLSL without editing game files](../replace-or-patch-game-glsl-without-editing-game-files/overview.md)).

## Background: how the game does it

`RaymarchCloud.comp` grows each step with `stepSizeIncreaseFactor * distanceFromCamera + initialStepSize`, capped at `maxStepSize`, advances the ray, samples `GetCloudDensity` and, for a non-zero density, lights the sample with `GetDensityToLight` and accumulates a weighted `averagePosition`
([current/Content/Core/Shaders/Clouds/RaymarchCloud.comp](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Clouds/RaymarchCloud.comp#L179-L253)). `GetDensityToLight` samples evenly over the way to the layer's top, 2 to 20 km apart, so for a sample at the face of a dense cloud the first light sample is usually already outside it and counts as unshadowed.
`GetCloudDensity` and `GetDensityToLight` are defined in `CloudFunctions.glsl`. The pass runs from the cloud renderer right after the body's atmosphere.

## Lifecycle and timing

| Step | When | Value |
|---|---|---|
| `PatchFaceLight`: four anchors, all or none | `[StarMapBeforeMain]` on the shadow copy of `RaymarchCloud.comp` (stock shader id `RaymarchCloudCompute`) | `startup:starmap-before-main` |
| The raymarch runs per cloud pixel | `PlanetTransparenciesRenderer` after the body's atmosphere | `render:atmosphere` |

## Steps

1. **Pick the layers it applies to.** `RaLitAtFace()` is true for layers that take their colour from a map (`volumetricColorMapSpecialization > 0`, a pipeline specialization constant) and for Venus's deck, told by its layer radii; Earth, Mars and Titan clouds are left as tuned.

2. **Find the face by bisection.** When `cloudDensity * currentStepSize > 2`, halve the step from its start up to five times, accumulating optical depth at each part's far-end density, to find where it passes 1; then take the light, dark-edge term, colour and reported position at that point. The step's opacity stays the game's:

   ```csharp
   // Payloads.cs L3073-L3104 (GLSL built as a C# string)
       public const string FaceLight =
           "        // PATCHED (Real Atmospheres: an optically thick step is lit where its first optical depth ends, not at its far end)\n" +
           "        vec3 raLitPosition = rayPosition;\n" +
           "        float raLitDensity = cloudDensity;\n" +
           "        if (RaLitAtFace() && cloudDensity * currentStepSize > 2.0)\n" +
           "        {\n" +
           "            vec3 raStepStart = rayPosition - worldViewDir * currentStepSize;\n" +
           "            float raNear = 0.0; float raFar = currentStepSize; float raDepth = 0.0;\n" +
           "            int raCuts = clamp(int(ceil(log2(cloudDensity * currentStepSize))), 1, 5);\n" +
           "            for (int raCut = 0; raCut < raCuts; raCut++)\n" +
           "            {\n" +
           "                float raMiddle = 0.5 * (raNear + raFar);\n" +
           "                vec3 raPosition = raStepStart + worldViewDir * raMiddle;\n" +
           "                float raProfile = 0.0; float raBrightness = 1.0; float raNoise = 0.0; vec3 raColor;\n" +
           "                float raDensity = GetCloudDensity(raPosition, planetPosition, innerCloudRadius, outerCloudRadius,\n" +
           "                    noiseSampler, interpolatedMipLevels, vec2(uboCloudLayer.minInvNoiseScale, uboCloudLayer.maxInvNoiseScale),\n" +
           "                    raProfile, raBrightness, raColor, raNoise);\n" +
           "                if (raDepth + raDensity * (raMiddle - raNear) >= 1.0)\n" +
           "                {\n" +
           "                    raFar = raMiddle;\n" +
           "                    raLitPosition = raPosition;\n" +
           "                    raLitDensity = raDensity;\n" +
           "                    dimensionalProfile = raProfile; multipleScatteringBrightness = raBrightness;\n" +
           "                    localColor = raColor; noiseProfile = raNoise;\n" +
           "                }\n" +
           "                else\n" +
           "                {\n" +
           "                    raDepth += raDensity * (raMiddle - raNear);\n" +
           "                    raNear = raMiddle;\n" +
           "                }\n" +
           "            }\n" +
           "        }\n" +
   ```

   Source: [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L3073-L3104).

3. **Add the near-sample self-shadow.** Sum density toward the Sun over the first 7.5 mean free paths (four stretches ending at 0.5, 1.5, 3.5 and 7.5) and dim the light by the larger of that and the engine's value, so a face sample is shadowed by its own cloud:

   ```csharp
   // Payloads.cs L3114-L3131 (GLSL built as a C# string)
       public const string NearLight =
           "            // PATCHED (Real Atmospheres: the cloud's own shadow on the sample, which the light march's long steps miss)\n" +
           "            if (RaLitAtFace())\n" +
           "            {\n" +
           "                float raFree = 1.0 / max(raLitDensity, 1e-9);\n" +
           "                float raNearLight = 0.0; float raFrom = 0.0;\n" +
           "                for (int raPart = 0; raPart < 4; raPart++)\n" +
           "                {\n" +
           "                    float raTo = raFree * (raPart == 0 ? 0.5 : raPart == 1 ? 1.5 : raPart == 2 ? 3.5 : 7.5);\n" +
           "                    float raProfile = 0.0; float raBrightness = 1.0; float raNoise = 0.0; vec3 raColor;\n" +
           "                    raNearLight += (raTo - raFrom) * GetCloudDensity(raLitPosition + lightDirection * (0.5 * (raFrom + raTo)),\n" +
           "                        planetPosition, innerCloudRadius, outerCloudRadius, noiseSampler, interpolatedMipLevels,\n" +
           "                        vec2(uboCloudLayer.minInvNoiseScale, uboCloudLayer.maxInvNoiseScale), raProfile, raBrightness, raColor, raNoise);\n" +
           "                    raFrom = raTo;\n" +
           "                }\n" +
           "                densityToLight = max(densityToLight, raNearLight);\n" +
           "            }\n" +
           "\n" + NearLightAnchor;
   ```

   Source: [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L3114-L3131). The weighted average position then accumulates `raLitPosition` instead of the raw step position ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L3133-L3134)).

4. **Apply all four anchors or none**, and tell the rest of the mod whether it took effect, because the XML values only make sense with it:

   ```csharp
   // ShadowBuilder.cs L814-L835
       private static void PatchFaceLight(string shadersDir)
       {
           _faceLight = false;
           string path = Path.Combine(shadersDir, "Clouds", "RaymarchCloud.comp");
           if (!File.Exists(path)) { Log("WARN: RaymarchCloud.comp missing, dense cloud decks stay lit at each step's far end"); return; }
           string s = ReadLf(path);
           if (s.Contains(Payloads.FaceLightMarker)) { _faceLight = true; Log("dense cloud decks already lit at the face"); return; }
           int light = CountOf(s, Payloads.FaceLightAnchor), position = CountOf(s, Payloads.FaceLightPositionAnchor),
               near = CountOf(s, Payloads.NearLightAnchor), func = CountOf(s, Payloads.FaceLightFuncAnchor);
           if (light != 1 || position != 1 || near != 1 || func != 1)
           {
               Log($"WARN: RaymarchCloud.comp lighting anchors found {light}x, {position}x, {near}x and {func}x (expected 1 each), dense cloud decks stay lit at each step's far end");
               return;
           }
           s = s.Replace(Payloads.FaceLightFuncAnchor, Payloads.FaceLightFunc)
                .Replace(Payloads.FaceLightAnchor, Payloads.FaceLight)
                .Replace(Payloads.NearLightAnchor, Payloads.NearLight)
                .Replace(Payloads.FaceLightPositionAnchor, Payloads.FaceLightPosition);
           File.WriteAllText(path, s);
           _faceLight = true;
           Log("the giants' and Venus's cloud decks lit at the near face of an optically thick step (RaymarchCloud.comp)");
       }
   ```

   Source: [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L814-L835) (the function counts the four anchors and, when any is not exactly 1, logs and returns with `_faceLight` still false). The mod switches the giants' decks to their dense cloud types, and Venus's crowns to their face-lit brightness, in the XML only when `_faceLight` is true.

## Complete example

| File | Role |
|---|---|
| [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs) | The function, face-position, near-light and position payloads (L3052-L3134) |
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs) | `PatchFaceLight` |

## Variations and gotchas

- **Patching a shader's inner loop through anchors is the most fragile kind of edit:** four anchors in the hottest loop of the cloud pass. A mismatch means "dense decks stay lit at each step's far end", and the mod keeps the XML on the soft deck values that look right without the patch.
- **Cost.** Up to five extra density lookups on the step that meets the deck and four per lit sample. Reported result: brightness within 1.03 to 1.17 of the finely stepped value from 300 to 10,000 km on all four giants.
- **Co-tuned constants.** With face lighting the dense types need `MultipleScatteringBrightness` 1.7 to match the 2D map; those XML values only make sense with this patch.
- **Layer identity by radii** (Venus: mean radius + 48 km and + 72 km) breaks if another mod moves the deck; the deck then goes back to stock lighting.

## See also

- Siblings: [sky march](extend-stock-sky-and-cloud-shaders-1.md), [map flow and lift](extend-stock-sky-and-cloud-shaders-3.md), [ring shadows](extend-stock-sky-and-cloud-shaders-4.md), [overview](overview.md)
- [Cloud face lighting](../../../../mods/realatmospheres/features/cloud-face-lighting.md)
- [Raymarch a local volume or analytic sky effect into the HDR scene](../../compute-effects/raymarch-a-local-volume-into-the-hdr-scene/overview.md), [Branch per body inside shared game shaders](../branch-per-body-inside-shared-game-shaders/overview.md)
