---
type: Game Feature
title: Raymarch a local volume or analytic sky effect into the HDR scene
description: How to draw smoke, fire, clouds and sky glows with a compute raymarch recorded before bloom, lit and occluded by KSA's own atmosphere and weather data, and priced from the game's GPU profiler
category: shaders
group: compute-effects
topic: raymarch-a-local-volume-into-the-hdr-scene
demonstrated_by: [mods/ksarmory/features/raymarched-mushroom-cloud, mods/ksarmory/features/high-altitude-burst-sky]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame, render:bloom, render:atmosphere]
game_types:
  - KSA.SunbloomRenderer
  - KSA.Rendering.RenderImage
  - KSA.GpuProfiler
  - KSA.ProfilerTag
  - KSA.ViewProjection
  - KSA.AtmosphereRenderer
  - KSA.Atmosphere.Rendering.CloudRenderer
  - KSA.Atmosphere.Rendering.CloudShadowsRenderer
  - KSA.PlanetTransparenciesRenderer
  - KSA.Atmosphere.Rendering.CloudShadowRenderData
  - KSA.PhysicalAtmosphereReference
  - KSA.AtmosphericBody
  - KSA.Celestial
  - KSA.Camera
tags: [raymarch, volume, sdf, temporal, reprojection, atmosphere, weather, profiler, compute, ksarmory]
---
KSA draws its planets, atmospheres and weather clouds with its own raymarched passes. A mod that wants a local volume (a burst's mushroom cloud, a smoke column, a glowing shell) or a sky-wide analytic glow can run the same kind of pass in its own compute shader, write the result into the HDR scene before bloom, and borrow the game's atmosphere LUTs to light it and its weather images to sit behind or in front of the weather. This topic covers KSArmory's approach in three parts: the march and its temporal resolve, the engine data that lights and occludes the volume, and the cheaper analytic dispatches that draw sky effects.

## Engine background

- **The recording point.** A volume drawn here is HDR, blooms and is tonemapped with the scene. `SunbloomRenderer.Render(CommandBuffer, IViewport, int)` is called once per rendered viewport after the planets, weather and translucency have been drawn (Program.cs L4875 renders the planet transparencies, L4915 calls the sunbloom for the main viewport, L4584 for camera windows). See [Paint into or rescale the HDR scene with a compute pass shipped as a Shader asset](../../../rendering/vulkan-passes/modify-the-hdr-scene-with-a-compute-pass/overview.md) for how to hook it and [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).
- **Weather is drawn before the hook and writes no depth.** `PlanetTransparenciesRenderer` runs the atmosphere then the cloud renderer's `RenderWithCompute` (PlanetTransparenciesRenderer.cs L292-L297); the weather clouds are marched into images of their own and composited into the scene colour, so the depth buffer under a cloud still says "ground".
- **Atmosphere data is per viewport.** `AtmosphereRenderer.GetAtmosphereLutsDescriptorSet(int shaderSlot)` returns the descriptor set holding the transmittance, aerial perspective and ambient LUTs for that viewport, and the layout is public too; the scalars the shader needs (planet position and radius, sun position and colour, LUT layer) are in the global set 0 ([current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L275-L281)).
- **The profiler.** `Profiler.Gpu` is a `GpuProfiler` that keeps per-frame timestamped regions; `commandBuffer.TagRegion(ProfilerTag)` brackets a region, `Frames`/`LastCompleteFrameIndex` expose the results ([current/decomp/KSA/GpuProfiler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GpuProfiler.cs#L60-L83)).
- **Planet air.** `PhysicalAtmosphereReference` gives density and pressure by altitude (an isothermal model: sea-level density times `exp(-h / scaleHeight)`) and a boundary height; only atmospheric bodies get a planet block in the global set.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Noise-eroded SDF torus, dome and column with a storage-image history](raymarch-a-local-volume-into-the-hdr-scene-1.md) | KSArmory | `glsl-shader` | `frame:draw-ui`, `render:bloom` | A convincing volume at a few milliseconds; needs a bound, a dither, a temporal resolve and cost measurement |
| [Light and occlude the volume with KSA's atmosphere and weather data](raymarch-a-local-volume-into-the-hdr-scene-2.md) | KSArmory | `reflection`, `glsl-shader` | `render:bloom`, `render:atmosphere` | The volume matches the sky and sits behind weather; relies on private fields and a build-specific descriptor layout |
| [Analytic sky dispatches with a negative-bound tag](raymarch-a-local-volume-into-the-hdr-scene-3.md) | KSArmory | `glsl-shader` | `frame:draw-ui`, `render:bloom` | Cheap full-screen glows and curtains with no scene volume; valid only for the camera's own atmospheric body |

## See also

- [Raymarched mushroom cloud](../../../../mods/ksarmory/features/raymarched-mushroom-cloud.md), [High-altitude burst sky](../../../../mods/ksarmory/features/high-altitude-burst-sky.md), [Compute pass before bloom](../../../../mods/ksarmory/features/compute-pass-before-bloom.md), [Core shader library include](../../../../mods/ksarmory/features/core-shader-library-include.md)
- [Pack parameters and camera data for a compute effect](../pack-parameters-for-a-compute-effect/overview.md), [Include KSA's own GLSL library from a mod shader](../../glsl-workflow/include-the-game-shader-library-in-mod-glsl/overview.md), [Extend KSA's sky and cloud shaders: per-pixel haze and cloud lighting fixes](../../stock-shaders/extend-stock-sky-and-cloud-shaders/overview.md), [Refract and distort the rendered scene, and flash or shake it](../../screen-effects/refract-and-distort-the-scene/overview.md), [Choose a render frame and anchor effects to bodies, parts and terrain](../../../rendering/overlays/choose-a-render-frame-and-anchor-effects/overview.md)
