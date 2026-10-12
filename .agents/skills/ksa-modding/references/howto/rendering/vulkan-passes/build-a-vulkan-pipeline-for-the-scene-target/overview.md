---
type: Game Feature
title: Build a Vulkan pipeline compatible with KSA's scene render target
description: A pipeline recorded inside KSA's scene pass must match its colour and depth formats, sample count and dynamic-rendering setup, and offscreen work of the mod's own needs a standalone render-pass-less pipeline
category: rendering
group: vulkan-passes
topic: build-a-vulkan-pipeline-for-the-scene-target
demonstrated_by:
  - mods/unscience/features/thug-life-textured-quad-render-pass
  - mods/gatos/features/thug-life-world-space-quad
  - mods/purrtty/features/world-space-quad-pipeline-custom-shader
  - mods/unscience/features/pebbles-workshop-offscreen-preview
lifecycle: [frame:draw-ui, frame:render, render:scene-opaque, render:translucency]
game_types: [KSA.Program, KSA.Rendering.RenderTarget, RenderCore.ShaderModuleUtils, KSA.ShaderReference]
tags: [vulkan, pipeline, dynamic-rendering, scene-target, glsl]
---

Since KSA moved the scene to Vulkan dynamic rendering there is no `VkRenderPass` to be compatible with: a pipeline is
compatible with a scope when the formats of its `VkPipelineRenderingCreateInfo` and its sample count match the attachments
that scope renders into. Pipelines for the scene must be built against the scene target; pipelines for the mod's own images
are free-standing.

## Engine background

- `Program.OffscreenTarget` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L456) is a `RenderTarget` with `ColorAttachment`, `DepthAttachment` and `Samples`
  [current/decomp/KSA.Rendering/RenderTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderTarget.cs#L40-L57).
- `RenderTarget.SetupGraphicsPipeline(ref VkGraphicsPipelineCreateInfo)` [current/decomp/KSA.Rendering/RenderTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderTarget.cs#L356-L392) stamps the
  rendering create-info, the sample count and a null render pass.
- `ShaderModuleUtils.FromString` / `FromFile` [current/decomp/RenderCore/ShaderModuleUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ShaderModuleUtils.cs#L79) compile GLSL with the bundled shaderc, with default
  include callbacks.
- The target exists only after `ModLibrary.LoadAll`; at `startup:all-mods-loaded` it is null. Scene draws happen in the phases listed in
  [the render sequence of the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence): opaque (step 9, MSAA scope) and translucency (step 14).
- GLSL compile helpers for shaders and includes: [compile GLSL at runtime](../../../shaders/glsl-workflow/compile-glsl-at-runtime/overview.md) and [include the game shader library in mod GLSL](../../../shaders/glsl-workflow/include-the-game-shader-library-in-mod-glsl/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: SetupGraphicsPipeline, created lazily](build-a-vulkan-pipeline-for-the-scene-target-1.md) | unscience, gatOS | Game helper stamps formats, stock unlit shaders | `render:scene-opaque` | Least code; blend and fragment behaviour limited to presets and stock shaders |
| [2: hand-built dynamic-rendering pipeline](build-a-vulkan-pipeline-for-the-scene-target-2.md) | purrtty | Chained rendering info, custom premultiplied fragment shader | `render:translucency` | Full control of blend; you own the shader module and must track the target |
| [3: standalone targets and pipeline](build-a-vulkan-pipeline-for-the-scene-target-3.md) | unscience | Own images, embedded GLSL | none (private command buffer) | No coupling to the scene; no scene depth or lighting |

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Insert a custom Vulkan pass into KSA's frame](../insert-a-custom-pass-into-render-game/overview.md), [Draw a textured quad attached to a part or the camera](../../textures-and-decals/draw-a-textured-quad-in-the-world/overview.md), [Recreate mod render targets when KSA rebuilds its renderer](../handle-renderer-rebuilds/overview.md), [Paint into or rescale the HDR scene with a compute pass shipped as a Shader asset](../modify-the-hdr-scene-with-a-compute-pass/overview.md)
- [Compile and write GLSL at runtime for a mod's own pipeline](../../../shaders/glsl-workflow/compile-glsl-at-runtime/overview.md), [Include KSA's own GLSL library from a mod shader](../../../shaders/glsl-workflow/include-the-game-shader-library-in-mod-glsl/overview.md)
