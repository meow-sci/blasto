---
type: Game Feature
title: Paint into or rescale the HDR scene with a compute pass shipped as a Shader asset
description: From the pre-bloom hook a mod can read the scene colour and depth and write back into it with compute only, and KSA compiles the mod's .comp files when they are declared as Shader assets
category: rendering
group: vulkan-passes
topic: modify-the-hdr-scene-with-a-compute-pass
demonstrated_by:
  - mods/ksarmory/features/compute-pass-before-bloom
  - mods/ksarmory/features/depth-painted-ground-rings
  - mods/ksarmory/features/compute-shell-tracers
  - mods/ksarmory/features/fireball-light-injection
  - mods/ksarmory/features/burst-flash-and-view-shake
  - mods/ksastructures/features/underground-dark-compute-pass
lifecycle:
  - frame:render
  - render:bloom
  - startup:asset-load
  - startup:all-mods-loaded
  - frame:draw-ui
  - frame:celestials
game_types:
  - KSA.SunbloomRenderer
  - KSA.Rendering.ComputePipelineWrapper
  - KSA.ShaderReference
  - KSA.Rendering.RenderImage
  - KSA.Rendering.BarrierBatch
  - KSA.Rendering.ImageBarrierInfo
  - KSA.Rendering.Lighting.ClusteredLightSystem
  - KSA.PrePassRenderer
  - KSA.GpuTextureSystem
  - KSA.Rendering.Lighting.LightDebug
tags: [compute, hdr, depth, shader-asset, bloom, lighting]
---

At the moment `SunbloomRenderer.Render` is called the main view's scene colour is a resolved HDR image in `General` layout and the scene depth is sampleable, and
bloom, the tonemap composite and ImGui are still to come. A compute dispatch recorded there can draw depth-aware marks, relight areas or add glows that bloom and grade with the
scene, without a graphics pipeline. The shader side is simple because KSA compiles any `.comp` declared as a `Shader` asset, from any mod folder, and wraps pipeline and descriptor
construction in `ComputePipelineWrapper`.

## Engine background

- `SunbloomRenderer.Render(CommandBuffer, IViewport, int)` [current/decomp/KSA/SunbloomRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SunbloomRenderer.cs#L326) is public and is called from `RenderGame` (main view, step 16), `RenderViewport` (secondary
  viewports with atmosphere) and `RenderEditor`; the barriers before it are at [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4910-L4916).
- `ShaderReference` [current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L80-L175) compiles the shader file at load via `ShaderModuleUtils.FromFile` and records include paths.
- `ComputePipelineWrapper` [current/decomp/KSA.Rendering/ComputePipelineWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ComputePipelineWrapper.cs#L12-L135): set 0 `GlobalShaderBindings`, set 1 the wrapper's own images and buffers in a fixed order, external sets after;
  `BindPipeline<T>` binds and pushes constants.
- `BarrierBatch` / `ImageBarrierInfo.Presets` (`StorageReadFc`, `SampledReadC`, `DepthSampledReadVfc`) [current/decomp/KSA.Rendering/ImageBarrierInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ImageBarrierInfo.cs#L35-L57) track image state.
- The light pre-pass images and `LightDebug.Lights` [current/decomp/KSA.Rendering.Lighting/LightDebug.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/LightDebug.cs#L9-L25) for relighting.
- Phases: `render:bloom` in [the render sequence of the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence); the hook itself is approach 4 of
  [Insert a custom Vulkan pass into KSA's frame](../insert-a-custom-pass-into-render-game/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: depth-reading dispatches](modify-the-hdr-scene-with-a-compute-pass-1.md) | ksarmory | Shader assets, wrapper per view, push constants or storage buffer, specialization constants | `render:bloom` | General recipe; many effects; write access not declared to KSA |
| [2: in-place rescale with pre-pass images](modify-the-hdr-scene-with-a-compute-pass-2.md) | ksastructures | `Priority.First` prefix, reflection onto private light images, bindless set | `render:bloom` | Reuses the engine's light result; main view only; depends on private fields |
| [3: far fireball fill](modify-the-hdr-scene-with-a-compute-pass-3.md) | ksarmory | `LightDebug.Lights` plus a compute fill | `frame:lights`, `render:bloom` | Corrects a 3 km culling limit; relies on a debug class and heuristics for albedo |

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Insert a custom Vulkan pass into KSA's frame](../insert-a-custom-pass-into-render-game/overview.md), [Build a Vulkan pipeline compatible with KSA's scene render target](../build-a-vulkan-pipeline-for-the-scene-target/overview.md), [Project decals onto vessels, terrain and clutter](../../textures-and-decals/project-decals-onto-scene-geometry/overview.md), [Add dynamic spot and point lights to the world from a mod](../../lights-and-particles/add-dynamic-lights-to-the-world/overview.md)
- [Include KSA's own GLSL library from a mod shader](../../../shaders/glsl-workflow/include-the-game-shader-library-in-mod-glsl/overview.md), [Compile-check a mod's GLSL without launching the game](../../../shaders/glsl-workflow/check-mod-shaders-offline-with-glslang/overview.md)
- [Recompile a mod's shader while the game runs](../../../shaders/glsl-workflow/reload-a-mod-shader-in-a-running-game/overview.md)
