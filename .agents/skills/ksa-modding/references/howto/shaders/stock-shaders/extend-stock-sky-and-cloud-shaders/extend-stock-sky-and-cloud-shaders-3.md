---
type: How-To
title: "Fix the cloud handover: flow and lift the volumetric map to match the 2D layer"
description: Patch CloudFunctions.glsl so volumetric cloud lookups follow the same flowmap phases as the 2D shell and faint map values are lifted to the opacity the 2D layer draws
category: shaders
group: stock-shaders
topic: extend-stock-sky-and-cloud-shaders
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/realatmospheres/features/volumetric-cloud-map-flow-and-lift]
techniques: [glsl-shader]
hooks: [starmap StarMapBeforeMain]
lifecycle: [startup:starmap-before-main, render:atmosphere]
game_types: [KSA.Atmosphere.Rendering.CloudRenderer]
tags: [glsl, clouds, flowmap, coverage, handover, include, realatmospheres]
---
KSA draws clouds as volumetrics near the planet and as a 2D shell from higher up, cross-fading over a camera-altitude band. Two mismatches show at the handover once a mod pushes that band out (RealAtmospheres moves it to 10,000-14,000 km on the giants): the 2D shell's map streams along its flowmap while the
volumetric lookups of the same map stand still, and faint map values the 2D layer draws as wisps come out nearly invisible in the volumetrics. Both fixes live in the shared include `Clouds/CloudFunctions.glsl`. Needs a text-patched shader tree ([Replace or text-patch the game's GLSL without editing game files](../replace-or-patch-game-glsl-without-editing-game-files/overview.md)).

## Background: how the game does it

- **Shared include.** `CloudFunctions.glsl` is pulled into the volumetric raymarch and the cloud shadow volume; anything added at file scope exists in all of them. The functions to patch are `GetCoverageAndCloudType` and the `GetLocalColor` call ([current/Content/Core/Shaders/Clouds/CloudFunctions.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Clouds/CloudFunctions.glsl#L72-L106), L234).
- **Pipeline specialization constants** such as `twoDimensionalFlowMapSpecialization` ([current/Content/Core/Shaders/Clouds/CloudData.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Clouds/CloudData.glsl#L13)) differ per pipeline: the raymarch has the flowmap constant, the shadow-volume pipelines do not, so the layer's 2D flow displacement being non-zero is the second test.
- **Flow phase** comes from the stock per-frame cloud layer data (`uboCloudLayerDynamicData.twoDimensionalFlowMapPhase`), so volumetrics and the 2D shell stay in step with no C# hook.
- **Coverage chain.** The volumetrics turn a map value into coverage with `min(tile, (0.6 tile + map - 0.6) / 0.4)` and erode it with noise, so map values below `0.6 (1 - tile)` disappear; on Earth a map value of 0.05 comes out at an opacity of 0.002.

## Lifecycle and timing

| Step | When | Value |
|---|---|---|
| `PatchCoverageLift` and `PatchMapFlow` each check all their anchors in `CloudFunctions.glsl` and apply both of their edits or none | `[StarMapBeforeMain]` | `startup:starmap-before-main` |
| The functions run in the volumetric raymarch and the cloud shadow volume | Per cloud pixel, GPU | `render:atmosphere` |

## Steps

1. **Reproduce the 2D layer's flow for a volumetric sample.** The 2D layer samples its map twice, displaced along the flowmap by two loop phases half a loop apart, and cross-fades them. `RaSetMapFlow` does the same for a point on the layer's top shell: read the 2D flowmap at the spherical UV, build the same
   tangent frame, offset by `flow * phase` for both phases and store two directions and a blend:

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

   Source: [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2921-L2947). `GetCoverageAndCloudType` then reads the global map and the detail selector at both directions and mixes them; `GetLocalColor` does the same for the colour map ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2974-L2993)). The detail tile and the noise keep their own flow, because cross-fading two
   distant stretches of the detail tile would flatten the deck twice a loop. On the giants the 2D map displacement is set to 4,000 km per loop (it was 700 km, which moved the map a texel or two), with the loop lengthened to keep the measured wind speed.

2. **Lift faint values by a table.** `RaCoverageLift` maps a coverage value through a table of "the map value whose volumetric opacity equals the 2D layer's", every 0.02, for Earth's lower layer, Earth's cirrus and Mars's dust haze, each told by its layer radii. Above the table's end the map is untouched; on Mars the lift fades where
   the selector marks a storm front:

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

   Source: [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2871-L2887). The tables came from running the engine's coverage chain offline. The call is inserted right after the stock `if (globalCoverage < 0.01) return 0.0;` early-out.

3. **Apply each patch's edits all or nothing.** Count every anchor in `CloudFunctions.glsl`, log and bail unless each is exactly 1, then replace:

   ```csharp
   // ShadowBuilder.cs L750-L766
       private static void PatchCoverageLift(string shadersDir)
       {
           string path = Path.Combine(shadersDir, "Clouds", "CloudFunctions.glsl");
           if (!File.Exists(path)) { Log("WARN: CloudFunctions.glsl missing, Earth's faint clouds stay the game's"); return; }
           string s = ReadLf(path);
           if (s.Contains(Payloads.CoverageLiftMarker)) { Log("faint volumetric clouds already lifted"); return; }
           int func = CountOf(s, Payloads.CoverageLiftFuncAnchor), call = CountOf(s, Payloads.CoverageLiftCallAnchor);
           if (func != 1 || call != 1)
           {
               Log($"WARN: CloudFunctions.glsl coverage anchors found {func}x and {call}x (expected 1 each), Earth's faint clouds stay the game's");
               return;
           }
           s = s.Replace(Payloads.CoverageLiftFuncAnchor, Payloads.CoverageLiftFunc.TrimStart('\n') + Payloads.CoverageLiftFuncAnchor)
                .Replace(Payloads.CoverageLiftCallAnchor, Payloads.CoverageLiftCall);
           File.WriteAllText(path, s);
           Log("Earth's faint clouds and Mars's faint dust lifted in the volumetrics (CloudFunctions.glsl)");
       }
   ```

   Source: [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L750-L766). `PatchMapFlow` does the same for the helper and both lookups.

## Complete example

| File | Role |
|---|---|
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs) | `PatchCoverageLift`, `PatchMapFlow` |
| [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs) | `RaCoverageLift` and its tables, `RaSetMapFlow`, the lookups and call-site patches |

## Variations and gotchas

- **Layers identified by radii to 50 m.** A mod that moves Earth's cloud layers or Mars's dust layer loses the lift with no error, which is the intended failure mode.
- **Flow only in the map, not the tile.** Only the global map, selector and colour flow.
- **A shared include:** three file-scope globals (the flow directions) exist in every pass that includes it.
- **Specialization constants.** The shadow-volume pipelines lack the flowmap constant, so the patch must test the layer's displacement as well.

## See also

- Siblings: [sky march](extend-stock-sky-and-cloud-shaders-1.md), [face lighting](extend-stock-sky-and-cloud-shaders-2.md), [ring shadows](extend-stock-sky-and-cloud-shaders-4.md), [overview](overview.md)
- [Volumetric cloud map flow and faint-cloud lift](../../../../mods/realatmospheres/features/volumetric-cloud-map-flow-and-lift.md), [Cloud layers in the system XML](../../../../mods/realatmospheres/features/cloud-layers-in-system-xml.md)
- [Branch per body inside shared game shaders](../branch-per-body-inside-shared-game-shaders/overview.md)
