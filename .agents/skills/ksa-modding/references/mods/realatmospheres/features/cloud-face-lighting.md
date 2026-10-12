---
type: Mod Feature
title: Cloud face lighting
description: Lights an optically thick volumetric cloud step where its first optical depth ends, found by bisection, and adds a short self-shadow march, so dense decks keep their brightness from any distance
mod: realatmospheres
category: shaders
techniques: [glsl-shader, harmony-prefix]
hooks: [starmap StarMapBeforeMain, harmony-prefix RenderCore.ShaderModuleUtils.FromFile]
lifecycle: [startup:starmap-before-main, render:atmosphere]
game_types: [KSA.Atmosphere.Rendering.CloudRenderer, KSA.PlanetTransparenciesRenderer]
tags: [shaders, glsl, clouds, volumetric-clouds, raymarch, self-shadowing, giants, venus]
---

KSA's volumetric cloud raymarch takes one density sample at the end of each step and lights that
point, and its steps grow with distance (on the giants to 25-30 km). In a dense deck one such step
holds tens of optical depths, so the light is looked up kilometres inside the cloud where almost none
arrives, and the deck darkens as the camera backs away. The mod's comment quantifies it: Jupiter's dense
type at 0.79 of its finely stepped brightness from 300 km, 0.41 from 2,000 km and 0.14 from 10,000 km
([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L3002-L3036)). This is the README's "extended cloud self-shadowing".[^ra-payloads]

## How it works

- **Which layers.** `RaLitAtFace()` is true for layers that take their colour from a map
  (`volumetricColorMapSpecialization > 0`, a pipeline specialization constant declared in
  [current/Content/Core/Shaders/Clouds/CloudData.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Clouds/CloudData.glsl#L13): the giants' decks) and for Venus's deck, told by its layer
  radii; Earth, Mars and Titan clouds are left as tuned ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L3038-L3065)).
- **Face position.** When `cloudDensity * currentStepSize > 2`, the step from its start is halved up
  to five times (as many as bring a part down to about one mean free path), accumulating optical depth
  at each part's far-end density, to find where it passes 1. Light, the dark-edge term, colour and the
  reported position are then taken at that point (`raLitPosition`); the step's opacity stays the
  game's ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L3073-L3109)).
- **Near-sample self-shadow.** The engine's light march (`GetDensityToLight`) samples 2-20 km apart,
  so the first light sample usually leaves a dense cloud immediately and the sample looks unshadowed.
  The patch also sums density toward the Sun over the first 7.5 mean free paths (four stretches ending
  at 0.5, 1.5, 3.5 and 7.5) and uses the larger of that and the engine's value
  ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L3111-L3131)).
- **Position for compositing.** The weighted average position accumulates `raLitPosition` instead of
  the raw step position ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L3133-L3134)).
- **All four anchors or none,** and the XML only switches Jupiter's and the other giants' decks to
  their dense cloud types, and Venus's crowns to their face-lit brightness, when this patch applied
  (`_faceLight`) ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L808-L835)).

Result reported in the comment: brightness within 1.03-1.17 of the finely stepped value from 300 to
10,000 km on all four giants, at the cost of up to five extra density lookups on the step that meets
the deck and four per lit sample.

## Lifecycle and timing

- **`startup:starmap-before-main`.** `PatchFaceLight` edits the shadow `Clouds/RaymarchCloud.comp`
  (stock shader id `RaymarchCloudCompute`,
  [current/Content/Core/DefaultAssets.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/DefaultAssets.xml#L249)).
- **`render:atmosphere` (GPU).** The cloud raymarch is dispatched by the cloud renderer from
  `PlanetTransparenciesRenderer` right after the body's atmosphere
  ([current/decomp/KSA/PlanetTransparenciesRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetTransparenciesRenderer.cs#L292-L297)).[^ksa-planettransparenciesrenderer]

## Key code

| Path | Role |
|---|---|
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L808-L835) | `PatchFaceLight`: four anchors, all or none; sets `_faceLight` |
| [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L3052-L3134) | Function, face-position, near-light and position payloads |

## Snippets

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

## Game APIs touched

| Member | Source | Used for |
|---|---|---|
| Raymarch loop of `RaymarchCloud.comp` | [current/Content/Core/Shaders/Clouds/RaymarchCloud.comp](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Clouds/RaymarchCloud.comp#L148-L260) | Anchors: function comment, lighting block, light-ray dithering comment, `averagePosition` accumulation |
| `GetCloudDensity`, `GetDensityToLight` (GLSL) | [current/Content/Core/Shaders/Clouds/CloudFunctions.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Clouds/CloudFunctions.glsl#L207), [current/Content/Core/Shaders/Clouds/CloudFunctions.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Clouds/CloudFunctions.glsl#L316) | Defined in the shared cloud include; called again at the face and along the Sun direction |
| `PlanetTransparenciesRenderer` cloud dispatch | [current/decomp/KSA/PlanetTransparenciesRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetTransparenciesRenderer.cs#L292-L297) | When the pass runs |

## Pitfalls and version notes

- **Patching a shader's inner loop through anchors** is the most fragile kind of edit: four anchors in
  the hottest loop of the cloud pass. The mod treats a mismatch as "dense decks stay lit at each
  step's far end" and keeps the XML on the soft deck values that look right without the patch.
- **Brightness constants co-tuned.** With face lighting the dense types needed
  `MultipleScatteringBrightness` 1.7 to match the 2D map; those XML values only make sense with this
  patch ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L1401-L1430)).
- **Layer identity by radii** (Venus: mean radius + 48 km and + 72 km) breaks if another mod moves the
  deck; the deck then simply goes back to stock lighting.

## See also

- [Cloud layers in the system XML](cloud-layers-in-system-xml.md)
- [Volumetric cloud map flow and faint-cloud lift](volumetric-cloud-map-flow-and-lift.md)
- [Cloud pass shading and ring shadows](cloud-pass-shading-and-ring-shadows.md)
- [Fix cloud lighting: light a thick raymarch step at its first optical depth by bisection](../../../howto/shaders/stock-shaders/extend-stock-sky-and-cloud-shaders/extend-stock-sky-and-cloud-shaders-2.md) - how-to article demonstrating this feature

[^ra-payloads]: Payloads.cs
[^ksa-planettransparenciesrenderer]: KSA.PlanetTransparenciesRenderer (decompiled, build 2026.10.10.5554)
