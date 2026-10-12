---
type: Mod Feature
title: Volumetric cloud map flow and faint-cloud lift
description: Two CloudFunctions.glsl patches that keep volumetric clouds consistent with the 2D layer at the handover, by flowing the map lookups along the 2D flowmap and lifting faint map values through inverse tables
mod: realatmospheres
category: shaders
techniques: [glsl-shader, harmony-prefix]
hooks: [starmap StarMapBeforeMain, harmony-prefix RenderCore.ShaderModuleUtils.FromFile]
lifecycle: [startup:starmap-before-main, render:atmosphere]
game_types: [KSA.Atmosphere.Rendering.CloudRenderer]
tags: [shaders, glsl, clouds, volumetric-clouds, flowmap, coverage, earth, mars, giants]
---

KSA draws clouds as volumetrics near the planet and as a 2D shell from higher up, cross-fading over a
camera-altitude band. Two mismatches showed at the handover once the mod pushed that band out to
10,000-14,000 km on the giants: the 2D shell's map streams along its flowmap while the volumetric
lookups of the same map stood still, and faint map values the 2D layer draws as wisps came out nearly
invisible in the volumetrics. Both are fixed in the shared include `Clouds/CloudFunctions.glsl`.[^ra-payloads]

## How it works

### Map flow

- The 2D layer samples its map twice, displaced along the flowmap by two loop phases half a loop
  apart, and cross-fades them. `RaSetMapFlow` reproduces that for a volumetric sample: it reads the
  2D flowmap at the sample's spherical UV, builds the same tangent frame, offsets the point on the
  layer's top shell by `flow * phase` for both phases, and stores two directions and a blend
  ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2907-L2947)).
- `GetCoverageAndCloudType` then reads the global map and the detail selector at both directions and
  mixes them; `GetLocalColor` does the same for the colour map ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2974-L2993)).
  The detail tile and the noise keep their own flow.
- It only engages where the layer has a 2D flowmap: the raymarch pipeline has the
  `twoDimensionalFlowMapSpecialization` constant, while the shadow-volume pipelines do not, so the layer's
  2D flow displacement being non-zero is the second test ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2916-L2919)).
- The giants' 2D map displacement is set to 4,000 km per loop with the loop lengthened to keep the
  measured wind speed, because at 700 km the map moved a texel or two and nothing showed
  ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2995-L3000)).

### Faint-cloud lift

- The volumetrics turn a map value into coverage with `min(tile, (0.6 tile + map - 0.6) / 0.4)` and
  then erode it, so map values below `0.6 (1 - tile)` disappear; on Earth a map value of 0.05 comes out
  at an opacity of 0.002 ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2828-L2848)).
- `RaCoverageLift` maps the value through a table of "the map value whose volumetric opacity equals
  the 2D layer's", every 0.02, for Earth's lower layer, Earth's cirrus and Mars's dust haze, each told
  by its layer radii; above the table's end the map is untouched. On Mars the lift fades out where the
  selector marks a storm front ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2861-L2887)). The tables came from
  running the engine's coverage chain offline (comment references).
- It is inserted right after the stock `if (globalCoverage < 0.01) return 0.0;` early-out
  ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2852-L2859)).

## Lifecycle and timing

- **`startup:starmap-before-main`.** `PatchCoverageLift` and `PatchMapFlow` each check all their
  anchors in `CloudFunctions.glsl` and apply both of their edits or none
  ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L748-L788)).
- **`render:atmosphere` (GPU).** The functions run in the volumetric raymarch and the cloud shadow
  volume, which include `CloudFunctions.glsl`. The flow phase comes from the stock per-frame cloud
  layer data (`uboCloudLayerDynamicData.twoDimensionalFlowMapPhase`), so volumetrics and the 2D shell
  stay in step with no C# hook.

## Key code

| Path | Role |
|---|---|
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L748-L788) | Both patches, all-or-none per patch |
| [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2861-L2887) | `RaCoverageLift` and its three tables |
| [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2907-L2993) | `RaSetMapFlow`, `RaGlobalMap`, `RaDetailSelector`, and the two call-site patches |

## Snippets

```glsl
// Payloads.cs L2921-L2947 (GLSL inside a C# raw string)
void RaSetMapFlow(vec2 sphericalUV, vec3 cloudSpacePosition)
{
    vec3 cloudNormal = normalize(cloudSpacePosition);
    vec3 flowValue = textureLod(sampler2D(globalTextures[uboCloudLayer.twoDimensionalFlowMapTextureId],
        samplers[uboCloudLayer.linearRepeatSamplerId]), sphericalUV, 0.0).xyz * 2.0 - vec3(1.0);
    flowValue.x = abs(flowValue.x) < 0.015 ? 0.0 : flowValue.x;
    flowValue.y = abs(flowValue.y) < 0.015 ? 0.0 : flowValue.y;
    flowValue.xy *= uboCloudLayer.twoDimensionalFlowMapDisplacement;

    vec3 upVector = vec3(0.0, 0.0, -1.0);
    if (abs(dot(cloudNormal, upVector)) > 0.99999)
        upVector = vec3(0, 1, 0);
    vec3 bitangent = normalize(upVector - cloudNormal * dot(upVector, cloudNormal));
    vec3 tangent = normalize(cross(bitangent, cloudNormal));
    vec3 flow = flowValue.x * tangent + flowValue.y * bitangent;

    float phaseOffset = textureLod(sampler2D(globalTextures[uboCloudLayer.flowMapNoiseTextureId],
        samplers[uboCloudLayer.linearRepeatSamplerId]), sphericalUV, 0.0).r;
    float phase0 = fract(uboCloudLayerDynamicData.twoDimensionalFlowMapPhase + phaseOffset);
    float phase1 = fract(phase0 + 0.5);

    // the 2D layer displaces a point on its shell, the layer's top
    vec3 shellPosition = cloudNormal * uboCloudLayer.topRadius;
    raMapFlowDirection0 = normalize(shellPosition + flow * phase0);
    raMapFlowDirection1 = normalize(shellPosition + flow * phase1);
    raMapFlowBlend = abs((phase0 - 0.5) * 2.0);
}
```

```glsl
// Payloads.cs L2871-L2887 (GLSL inside a C# raw string)
float RaCoverageLift(float coverage, float selector)
{
    bool lower = abs(uboCloudLayer.bottomRadius - 6373000.0) < 50.0 && abs(uboCloudLayer.topRadius - 6379700.0) < 50.0;
    bool cirrus = abs(uboCloudLayer.bottomRadius - 6382000.0) < 50.0 && abs(uboCloudLayer.topRadius - 6383200.0) < 50.0;
    bool dust = abs(uboCloudLayer.bottomRadius - 3381300.0) < 50.0 && abs(uboCloudLayer.topRadius - 3408100.0) < 50.0;
    if (!lower && !cirrus && !dust)
        return coverage;
    float x = coverage / 0.02;
    int last = lower ? 13 : cirrus ? 16 : 20;
    if (x >= float(last))
        return coverage;
    int i = int(x);
    float below = lower ? raEarthLowerLift[i] : cirrus ? raEarthCirrusLift[i] : raMarsDustLift[i];
    float above = lower ? raEarthLowerLift[i + 1] : cirrus ? raEarthCirrusLift[i + 1] : raMarsDustLift[i + 1];
    float lifted = max(coverage, mix(below, above, x - float(i)));
    return dust ? mix(lifted, coverage, smoothstep(0.0, 0.1, selector)) : lifted;
}
```

## Game APIs touched

| Member | Source | Used for |
|---|---|---|
| `GetCoverageAndCloudType` | [current/Content/Core/Shaders/Clouds/CloudFunctions.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Clouds/CloudFunctions.glsl#L72-L106) | Anchors: signature, single-channel line, coverage early-out |
| `GetLocalColor` call | [current/Content/Core/Shaders/Clouds/CloudFunctions.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Clouds/CloudFunctions.glsl#L234) | Colour lookup made to flow |
| Specialization constants | [current/Content/Core/Shaders/Clouds/CloudData.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Clouds/CloudData.glsl#L13) | Per-pipeline switches the patches test |

## Pitfalls and version notes

- **Layers identified by radii to 50 m.** A mod that moves Earth's cloud layers or Mars's dust layer
  loses the lift with no error, which is the intended failure mode.
- **Flow only in the map, not the tile.** Cross-fading two distant stretches of the detail tile would
  flatten the deck twice a loop, so only the global map, selector and colour flow.
- **`CloudFunctions.glsl` is a shared include.** It is pulled into several passes; anything added at
  file scope (the mod adds three globals for the flow directions) exists in all of them.

## See also

- [Cloud layers in the system XML](cloud-layers-in-system-xml.md)
- [Cloud face lighting](cloud-face-lighting.md)
- [Cloud pass shading and ring shadows](cloud-pass-shading-and-ring-shadows.md)
- [Texture and phase asset scripts](texture-and-phase-asset-scripts.md)
- [Fix the cloud handover: flow and lift the volumetric map to match the 2D layer](../../../howto/shaders/stock-shaders/extend-stock-sky-and-cloud-shaders/extend-stock-sky-and-cloud-shaders-3.md) - how-to article demonstrating this feature

[^ra-payloads]: Payloads.cs
