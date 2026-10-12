---
type: Game Feature
title: "Extend KSA's sky and cloud shaders: per-pixel haze and cloud lighting fixes"
description: How the sky and volumetric cloud passes are structured, and four text-patch recipes that add a per-pixel march over the sky LUTs and fix cloud lighting, handover and ring shadows
category: shaders
group: stock-shaders
topic: extend-stock-sky-and-cloud-shaders
demonstrated_by:
  - mods/realatmospheres/features/haze-band-sky-march
  - mods/realatmospheres/features/cloud-face-lighting
  - mods/realatmospheres/features/volumetric-cloud-map-flow-and-lift
  - mods/realatmospheres/features/cloud-pass-shading-and-ring-shadows
lifecycle: [startup:starmap-before-main, render:atmosphere, render:planet]
game_types: [KSA.Atmosphere.Rendering.CloudRenderer, KSA.AtmosphereRenderer]
tags: [glsl, sky, atmosphere, clouds, raymarch, compute, text-patch, realatmospheres]
---
KSA's sky and clouds are compute shaders, not geometry: `Atmosphere.comp` integrates the sky for each screen pixel from look-up tables, and `RaymarchCloud.comp` marches volumetric cloud layers, with `2DCloud.comp` drawing the distant 2D shell.
Because they are plain GLSL under `Content/Core/Shaders`, a mod can extend them by text patching the copies it feeds to the compiler ([Replace or text-patch the game's GLSL without editing game files](../replace-or-patch-game-glsl-without-editing-game-files/overview.md)). This topic is the catalogue of such edits that RealAtmospheres
makes, grouped by the problem each solves.

## Engine background

- **Where they run.** `PlanetTransparenciesRenderer.RenderTransparencies` records the atmosphere passes and then the cloud raymarch for each atmospheric body
  ([current/decomp/KSA/PlanetTransparenciesRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetTransparenciesRenderer.cs#L292-L297)); the order in the frame is under `render:atmosphere` in the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).
- **The sky LUT has no azimuth.** The sky-view LUT is indexed by view zenith and the angle to the Sun only ([current/Content/Core/Shaders/Atmosphere/SkyLut.comp](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/SkyLut.comp#L40-L49)), so it cannot hold structure that varies around the planet; per-pixel work needs its own march.
- **Cloud marching.** `RaymarchCloud.comp` takes one density sample at the end of each step, lights it with `GetDensityToLight`, and its steps grow with camera distance ([current/Content/Core/Shaders/Clouds/RaymarchCloud.comp](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Clouds/RaymarchCloud.comp#L179-L253)). Shared functions live in `CloudFunctions.glsl`,
  included by the raymarch and the cloud shadow volume.
- **Shared helpers to reuse.** `RingShadows.glsl` provides `TraceToRingPlane` and `GetRingShadowTransmittance`, and the ring data is already in the atmosphere uniform buffer the cloud passes bind.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Raymarch inserted in Atmosphere.comp, blended by tangent altitude](extend-stock-sky-and-cloud-shaders-1.md) | RealAtmospheres | `glsl-shader` | `startup:starmap-before-main`, `render:atmosphere` | Horizontal structure the LUTs cannot hold; per-pixel cost, culled where invisible |
| [Light a thick step at its first optical depth by bisection](extend-stock-sky-and-cloud-shaders-2.md) | RealAtmospheres | `glsl-shader` | `startup:starmap-before-main`, `render:atmosphere` | Dense decks keep brightness at large step sizes; four anchors in the hottest loop |
| [Flow and lift the volumetric map to match the 2D layer](extend-stock-sky-and-cloud-shaders-3.md) | RealAtmospheres | `glsl-shader` | `startup:starmap-before-main`, `render:atmosphere` | Fixes the 2D/volumetric handover; layers are told apart by radii |
| [Include RingShadows.glsl and select mips without derivatives](extend-stock-sky-and-cloud-shaders-4.md) | RealAtmospheres | `glsl-shader` | `startup:starmap-before-main`, `render:atmosphere`, `render:planet` | Reuses stock helpers where data is already bound; needs a compute-friendly mip choice |

## See also

- [Haze-band sky march](../../../../mods/realatmospheres/features/haze-band-sky-march.md), [Cloud face lighting](../../../../mods/realatmospheres/features/cloud-face-lighting.md), [Volumetric cloud map flow and faint-cloud lift](../../../../mods/realatmospheres/features/volumetric-cloud-map-flow-and-lift.md), [Cloud pass shading and ring shadows](../../../../mods/realatmospheres/features/cloud-pass-shading-and-ring-shadows.md)
- [Replace or text-patch the game's GLSL without editing game files](../replace-or-patch-game-glsl-without-editing-game-files/overview.md), [Include KSA's own GLSL library from a mod shader](../../glsl-workflow/include-the-game-shader-library-in-mod-glsl/overview.md), [Branch per body inside shared game shaders](../branch-per-body-inside-shared-game-shaders/overview.md), [Raymarch a local volume or analytic sky effect into the HDR scene](../../compute-effects/raymarch-a-local-volume-into-the-hdr-scene/overview.md)
