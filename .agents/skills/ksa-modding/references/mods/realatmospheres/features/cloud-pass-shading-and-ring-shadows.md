---
type: Mod Feature
title: Cloud pass shading and ring shadows
description: Shader patches to the 2D cloud pass (terminator fade, map-coloured skylight, opaque Venus shell, thin Triton shell), a column-limited thin-cloud ground shadow, and ring shadows added to both cloud passes
mod: realatmospheres
category: shaders
techniques: [glsl-shader, harmony-prefix]
hooks: [starmap StarMapBeforeMain, harmony-prefix RenderCore.ShaderModuleUtils.FromFile]
lifecycle: [startup:starmap-before-main, render:atmosphere, render:planet]
game_types: [KSA.Atmosphere.Rendering.CloudRenderer, KSA.AtmosphereRenderer]
tags: [shaders, glsl, clouds, 2d-clouds, rings, shadows, terminator, venus, triton, saturn]
---

A set of small, independent shader edits around KSA's cloud passes. Each one targets a visible fault
with the stock data or with the mod's own decks, identifies the affected layer inside the shared shader
by its radii where it is body-specific, and is applied or skipped on its own.[^ra-payloads]

## How it works

| Patch | Shader | What it does | Code |
|---|---|---|---|
| Terminator fade | `Clouds/2DCloud.comp` | After the Lambertian term, fades the 2D shell across the solar-zenith band where the planet's shadow climbs from the layer's base to its top (thin unshaded layers) or over about 11 degrees (layers shaded like a surface), instead of the transmittance LUT's knife edge | [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2530-L2556), [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L992-L1004) |
| Skylight by map colour | `Clouds/2DCloud.comp` | Keeps the texture colour before the layer tint (`raMapColour`) and multiplies the added sky ambient by it, so a giant's map is not veiled; white masks are unchanged | [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2558-L2580), [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L1006-L1019) |
| Triton's thin shell | `Clouds/2DCloud.comp` | On Triton's layer (told by its radii) multiplies the colour by 2.65 so a Color of 1 is a white surface, and sets opacity to `1 - exp(-density * share * path)` from a tabulated volumetric column share and the line-of-sight path through the shell | [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2582-L2631), [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L1021-L1034) |
| Venus's opaque shell | `Clouds/2DCloud.comp` | On Venus's layer, alpha = 1 and the map's thin regions shade the cloud toward a measured under-colour, so no blue gas shows through the markings | [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2633-L2671), [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L1036-L1049) |
| Thin-cloud ground shadow | `Clouds/CloudShadows.glsl` | Above the 2D switch the stock ground shadow is `1 - coverage`; on Triton's layer it is clamped to the layer's densest column from the shadow-density LUT (about 1%) | [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2673-L2697), [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L790-L806) |
| Ring shadows on clouds | `Clouds/2DCloud.comp`, `Clouds/RaymarchCloud.comp` | Includes `../Common/RingShadows.glsl` and multiplies cloud colour by the rings' transmittance where the stock applies the eclipse shadow | [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L3136-L3207), [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L711-L746) |

Ring-shadow details worth copying:

- **Reuse the game's helper.** `RingShadows.glsl` already provides `TraceToRingPlane` and
  `GetRingShadowTransmittance`, used by the atmosphere and planet shaders
  ([current/Content/Core/Shaders/Common/RingShadows.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/RingShadows.glsl#L22-L70)).
  The ring data is already in the atmosphere uniform buffer both cloud passes bind
  (`useRingShadows`, `ringTextureId`, `ringNormalAndInnerRadius`, `ringOuterRadius`)
  ([current/Content/Core/Shaders/Atmosphere/AtmosphereDataUbo.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/AtmosphereDataUbo.glsl#L11-L33)).
- **Mip selection without derivatives.** Compute shaders have no `dFdx`, so the patch traces two
  neighbouring points one pixel apart along the camera's right and up axes and picks the mip from how
  far the ring coordinate moves ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L3161-L3188)).
- **Only when the build supports it, never twice.** The patch is skipped if `RingShadows.glsl` or
  `ringTextureId` is missing, and per file if the game already mentions `RingShadow` or
  `useRingShadows` there ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L714-L736)).
- **Interplay with the side channel.** The mod also uses the ring slots for its haze turn, but only on
  ring-less bodies and with `useRingShadows == 0`, so this patch's `useRingShadows == 0` early return
  keeps the two apart (see [Atmosphere uniform side channel](atmosphere-uniform-side-channel.md)).

## Lifecycle and timing

- **`startup:starmap-before-main`.** `2DCloud.comp` gets its four patches through one `PatchFile`
  call chain, the others through their own functions ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L64-L79)).
- **`render:atmosphere` (GPU).** Both cloud passes run from `PlanetTransparenciesRenderer` after the
  atmosphere ([current/decomp/KSA/PlanetTransparenciesRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetTransparenciesRenderer.cs#L292-L297)).
- **`render:planet` (GPU).** `CloudShadows.glsl` is included by the surface shaders (planet, ground
  clutter, ocean, static objects), so the thin-shadow clamp runs where the ground is shaded.

## Key code

| Path | Role |
|---|---|
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L64-L70) | 2D cloud chain: terminator, skylight, gain, Venus shell |
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L711-L746) | `PatchCloudRingShadows` |
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L790-L806) | `PatchThinShadow` |
| [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2530-L2697) | 2D cloud and thin-shadow payloads |
| [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L3136-L3207) | Ring-shadow function and sites |

## Snippets

```glsl
// Payloads.cs L3162-L3188 (GLSL inside a C# raw string)
float RaCloudRingShadow(vec3 position)
{
    if (atmosphereDataUbo.useRingShadows == 0)
        return 1.0;

    vec3 planetPosition = atmosphereDataUbo.planetPositionAndPackedScreenStartCoords.xyz;
    vec3 directionToSun = normalize(global.lighting.sunPosition.xyz - planetPosition);
    vec3 ringNormal = atmosphereDataUbo.ringNormalAndInnerRadius.xyz;
    float innerRadius = atmosphereDataUbo.ringNormalAndInnerRadius.w;
    float outerRadius = atmosphereDataUbo.ringOuterRadius;
    float invWidth = 1.0 / max(outerRadius - innerRadius, 1.0);

    // One pixel at this distance, along the camera's right and up axes
    float pixel = 2.0 * length(position) / (abs(global.camera.projection[1][1]) * float(global.camera.screenHeight));
    vec3 right = global.camera.inverseView[0].xyz;
    vec3 up = global.camera.inverseView[1].xyz;

    float coord = RaRingCoord(position, planetPosition, directionToSun, ringNormal, innerRadius, invWidth);
    float stepX = RaRingCoord(position + right * pixel, planetPosition, directionToSun, ringNormal, innerRadius, invWidth) - coord;
    float stepY = RaRingCoord(position + up * pixel, planetPosition, directionToSun, ringNormal, innerRadius, invWidth) - coord;
    float texels = max(abs(stepX), abs(stepY)) * float(textureSize(sampler2D(
        globalTextures[atmosphereDataUbo.ringTextureId], samplers[atmosphereDataUbo.ringSamplerId]), 0).x);

    return GetRingShadowTransmittance(globalTextures[atmosphereDataUbo.ringTextureId],
        samplers[atmosphereDataUbo.ringSamplerId], position, planetPosition, directionToSun, ringNormal,
        innerRadius, outerRadius, log2(max(texels, 1.0)));
}
```

```glsl
// Payloads.cs L2547-L2555 (GLSL inside a C# raw string)
        {
            float raSunCos = dot(normalize(cloudPosition - planetPosition), lightDirection);
            float raTop = min(atmosphereDataUbo.planetRadius / uboCloudLayer.topRadius, 1.0);
            float raBase = min(atmosphereDataUbo.planetRadius / uboCloudLayer.bottomRadius, 1.0);
            float raShadowAtTop = -sqrt(1.0 - raTop * raTop);      // the Sun's zenith cosine as the shadow reaches it
            float raShadowAtBase = -sqrt(1.0 - raBase * raBase);
            float raShaded = smoothstep(0.0, 0.5, uboCloudLayer.twoDimensionalCloudLambertian);
            color.rgb *= smoothstep(mix(raShadowAtTop, -0.12, raShaded), mix(raShadowAtBase + 0.02, 0.08, raShaded), raSunCos);
        }
```

## Game APIs touched

| Member | Source | Used for |
|---|---|---|
| 2D cloud lighting block | [current/Content/Core/Shaders/Clouds/2DCloud.comp](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Clouds/2DCloud.comp#L296-L357) | Anchors: own colour, light direction, Lambertian, sky ambient, eclipse shadow |
| Raymarch eclipse shadow | [current/Content/Core/Shaders/Clouds/RaymarchCloud.comp](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Clouds/RaymarchCloud.comp#L534-L535) | Ring shadow applied after it |
| Ground cloud shadow | [current/Content/Core/Shaders/Clouds/CloudShadows.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Clouds/CloudShadows.glsl#L153-L165) | `transmittance = 1.0 - globalCoverage` anchor |
| `TraceToRingPlane`, `GetRingShadowTransmittance` | [current/Content/Core/Shaders/Common/RingShadows.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/RingShadows.glsl#L22-L70) | Reused ring helper |
| Ring fields in `AtmosphereData` | [current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L1154-L1162) | Filled only for bodies with `RingsReference` |

## Pitfalls and version notes

- **KSA 2026.9.22 added ring shadows on the atmosphere and surface, not clouds.** The mod's opaque
  Saturn deck hid them, hence this patch; a later game build that shades clouds itself is detected
  and left alone ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L3136-L3139)).
- **Radii as identifiers** (Venus 6,099.8 and 6,123.8 km; Triton 1,357.4 and 1,359.9 km) follow from the
  mod's own XML; change the XML and these patches stop matching their layer.
- **Venus's shade depends on the skylight patch.** The Venus edit shades `raMapColour` only if the
  skylight edit created it, checked by marker at build time ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L1037-L1049)).

## See also

- [Cloud face lighting](cloud-face-lighting.md)
- [Volumetric cloud map flow and faint-cloud lift](volumetric-cloud-map-flow-and-lift.md)
- [Relief-softened terminator](relief-softened-terminator.md)
- [Cloud layers in the system XML](cloud-layers-in-system-xml.md)
- [Include the game's shader library: a relative include of a stock helper inside a patched copy](../../../howto/shaders/glsl-workflow/include-the-game-shader-library-in-mod-glsl/include-the-game-shader-library-in-mod-glsl-3.md) - how-to article demonstrating this feature
- [Reuse stock helpers in a cloud shader: include RingShadows.glsl and select mips without derivatives](../../../howto/shaders/stock-shaders/extend-stock-sky-and-cloud-shaders/extend-stock-sky-and-cloud-shaders-4.md) - how-to article demonstrating this feature

[^ra-payloads]: Payloads.cs
