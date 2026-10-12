---
type: How-To
title: "Write a post-processing fragment shader: how a pass is built from RenderTechnique and RenderTarget"
description: What ShaderExtensions builds around your fragment shader - a RenderTarget used as the pipeline's pass info, a descriptor set and a bind order - so a shader that does not show can be debugged
category: shaders
group: post-processing
topic: write-a-post-processing-fragment-shader
approach: 3
difficulty: advanced
prerequisites: [starmap, shaderextensions, harmony]
demonstrated_by: [mods/shaderextensions/features/post-processing-pass-chain]
techniques: [vulkan-pipeline, post-processing]
hooks:
  - harmony-transpiler KSA.Program.RenderGame
  - harmony-postfix KSA.Program.RebuildRenderer
  - reflection KSA.Program._offscreenTarget
lifecycle: [frame:prepare, frame:render, render:post-process]
game_types:
  - RenderCore.RenderTechnique
  - KSA.Rendering.RenderTarget
  - KSA.Rendering.ImageBarrierInfo
  - KSA.CommandBufferEx
  - KSA.RenderingPresets
  - KSA.GlobalShaderBindings
  - KSA.Program
tags:
  - vulkan
  - render-technique
  - render-target
  - dynamic-rendering
  - descriptor-set
  - debugging
  - shaderextensions
---
You normally never write this code: you write a fragment shader and an XML element. This article is
for the day a pass does not appear, or you want to build your own fullscreen pass without
ShaderExtensions. It walks through what ShaderExtensions creates for each shader, in the order it
creates it, with the game types involved. Everything here comes from
[ShaderExtensions/PostProcessing/PostProcessingChain.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingChain.cs) and
[ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs), read against build 2026.10.10.5554.

## Background: how the game does it

The game's own fullscreen work (tonemap composite, bloom, CMAA2) is recorded by renderers in
`Program.RenderGame`. A pipeline for such work needs a vertex and a fragment shader, a pipeline
layout built from descriptor set layouts, and a description of the attachments it renders into.

- **`RenderTechnique`** is the abstract base: it compiles the shader stages from `ShaderReference`
  objects, owns a descriptor pool, a pipeline layout and a pipeline, and its protected
  `CreatePipeline(...)` builds a graphics pipeline from an `IRenderPassInfo`
  ([current/decomp/RenderCore/RenderTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/RenderTechnique.cs#L59-L118)).
- **`RenderTarget`** is both a colour target and an `IRenderPassInfo`
  ([current/decomp/KSA.Rendering/RenderTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderTarget.cs#L8)); its `SetupGraphicsPipeline` adds a
  `VkPipelineRenderingCreateInfo` so the pipeline uses dynamic rendering
  ([current/decomp/KSA.Rendering/RenderTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderTarget.cs#L356-L368)).
- **`GlobalShaderBindings`** owns set 0 for every pipeline: `DescriptorSetLayout`, one
  `DescriptorSet(viewportIndex)` per viewport and frame in flight
  ([current/decomp/KSA/GlobalShaderBindings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GlobalShaderBindings.cs#L47-L59)).
- **State tracking.** `CommandBuffer.PipelineBarrier2(IRenderImage, ImageBarrierInfo)` transitions an
  image to a named state and skips the barrier if the image is already there
  ([current/decomp/KSA/CommandBufferEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CommandBufferEx.cs#L48-L54)).

## Lifecycle and timing

| Step | When | Value |
|---|---|---|
| Chains are (re)built | In a `Program.RebuildRenderer` postfix, and inline from the replacement for `BeginRenderPass` when the renderer extent changed | `frame:prepare` / `frame:render` |
| Passes record | Inside `Program.RenderGame`, before the swapchain render pass begins (pre-ImGui) and after it ends (post-ImGui) | `frame:render`, `render:post-process` |
| Build calls `Device.WaitIdle()` first | Everything GPU-side that referenced the old targets is finished | - |
| First call of the replacement method | Calls `Program.ScheduleRendererRebuild()` once (it sets `RendererRebuildNeeded`), so the game soon runs `RebuildRenderer` and the postfix rebuilds both chains; it builds them inline first only if the post-ImGui source target is missing or has the wrong size | `frame:render` |

Dynamic rendering (`vkCmdBeginRendering`) is not allowed inside a render pass instance, which is why
both chain insertion points sit outside the game's swapchain pass; see the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence) for steps 23
to 26. A renderer rebuild destroys the game's images, so the passes are rebuilt in the
`RebuildRenderer` postfix ([ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L227-L236)); the general problem is
[Recreate mod render targets when KSA rebuilds its renderer](../../../rendering/vulkan-passes/handle-renderer-rebuilds/overview.md).

## Steps

1. **One `RenderTarget` per shader.** The chain sorts shaders by `RenderPassId` and creates a target of
   `renderer.Extent` in the source image's format with no depth, registers the previous image as this
   pass's input, and builds a pass:

   ```csharp
   // ShaderExtensions/PostProcessing/PostProcessingChain.cs L62-L90
               Source = source;

               RenderImage current = source;
               int index = 0;

               foreach (T shader in shaders.OrderBy(s => s.PassId))
               {
                   RenderTarget target = new(
                       renderer,
                       $"{name}_Pass{shader.PassId}_{index}",
                       renderer.Extent,
                       source.Format,
                       VkFormat.Undefined);
                   targets.Add(target);

                   // The input of this shader is whatever the previous stage produced.
                   PostProcessingInputResolver.RegisterInput(preImgui, shader.PassId, current);
                   PostProcessingInputResolver.Resolve(shader);
                   CollectSampledReferences(shader);

                   PostProcessingPassRenderer pass = new(
                       renderer, current, target, shader, shader.VertexShaderReference);
                   passes.Add(pass);

                   PostProcessingInputResolver.RegisterOutput(preImgui, shader.PassId, target.ColorImage);

                   current = target.ColorImage;
                   index++;
               }
   ```

   Source: [ShaderExtensions/PostProcessing/PostProcessingChain.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingChain.cs#L62-L90).

2. **Derive from `RenderTechnique` and pass the target as the pass info.** The constructor passes
   `[vertexShader, shader]` as the stages and the `RenderTarget` as the `IRenderPassInfo`. Then it
   builds the set-1 layout (binding 0 as a combined image sampler fed by the previous image, binding 1
   the time buffer, then the XML children), allocates the one set, writes the source view with
   `Program.LinearClampedSampler` and creates the pipeline layout `[GlobalShaderBindings.DescriptorSetLayout,
   bindingLayout]` plus the shader's push-constant ranges:

   ```csharp
   // ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs L71-L87
               var pushConstRanges = shader.CreatePushConstantRanges();
               PipelineLayout = device.CreatePipelineLayout(
                   [GlobalShaderBindings.DescriptorSetLayout, bindingLayout], pushConstRanges, null);

               RebuildFrameResources();
           }

           protected override VertexInput MakeVertexInput() => null;

           protected override void OnRebuildFrameResources() => CreatePipeline(
               VkPrimitiveTopology.TriangleStrip,
               VkCullModeFlags.BackBit,
               VkFrontFace.CounterClockwise,
               VkPolygonMode.Fill,
               RenderingPresets.ReverseZDepthStencil.NoDepthTest,
               Presets.BlendState.BlendNone,
               out Pipeline);
   ```

   Source: [ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs#L71-L87). The pipeline is a
   triangle strip with no vertex input, no depth test and no blending, which matches the four
   vertices of `ScreenspaceVert`.

3. **Record the chain.** `Chain.Render` first transitions the source and every cross-referenced image
   to `ImageBarrierInfo.Presets.SampledReadF`, then each pass begins rendering with `DontCare` load
   ops, binds the pipeline, sets a full-extent viewport and scissor, pushes constants, binds sets 0
   and 1 and draws four vertices:

   ```csharp
   // ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs L121-L139
               shader.PushConstants(commandBuffer, PipelineLayout);

               var dynamicOffsetCount = 1;
               foreach (var binding in shader.Bindings)
                   if (binding.DescriptorType == VkDescriptorType.UniformBufferDynamic)
                       dynamicOffsetCount += binding.DescriptorCount;

               Span<Brutal.ByteSize32> dynamicOffsets = stackalloc Brutal.ByteSize32[dynamicOffsetCount];
               dynamicOffsets.Clear();
               dynamicOffsets[0] = GlobalShaderBindings.FrameOffset(0, getGlobalShaderBindingsCurrentFrame());

               commandBuffer.BindDescriptorSets(
                   VkPipelineBindPoint.Graphics, PipelineLayout, 0,
                   [GlobalShaderBindings.DescriptorSet(0), bindingSet],
                   dynamicOffsets);

               commandBuffer.Draw(4, 1, 0, 0);

               target.EndRendering(commandBuffer, ImageBarrierInfo.Presets.SampledReadF, default);
   ```

   Source: [ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs#L121-L139).

4. **Where the chain's input comes from.** For the pre-ImGui stage the handler reads the game's private
   `_offscreenTarget` by reflection and feeds the chain result back into that image so the game's
   composite tonemaps it:

   ```csharp
   // ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs L38-L46
           public static void RenderNow(CommandBuffer commandBuffer)
           {
               if (Chain.PassCount == 0) return;

               Chain.Render(commandBuffer);

               // Feed the result back into the image the game composites from.
               PostProcessingChain.CopyTo(commandBuffer, Chain.Output, GameOffscreenTarget.ColorImage);
           }
   ```

   Source: [ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs#L38-L46).

## Debugging checklist

| Symptom | Likely cause |
|---|---|
| Log line `[ShaderExtensions] Skipping post processing shader '<Id>'` | The asset still declares `SubpassId` or `RequiresUniqueRenderpass` |
| `No pre-imgui post-processing input found for RenderPassId N` thrown during a rebuild | An input or output reference names a pass that is not built yet (later or missing id) |
| Nothing changes with CMAA2 selected | The pre-ImGui result is written back after CMAA2 already tonemapped the offscreen image; see [Choose between pre-ImGui (HDR scene) and post-ImGui (final frame) post-processing](../choose-pre-or-post-imgui-post-processing/overview.md) |
| Effect missing in the vehicle editor or a secondary camera | Only `RenderGame` for the main window is patched |
| Validation error about dynamic offset count | See the gotcha below |

## Complete example

[ShaderExtensions/PostProcessing/PostProcessingChain.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingChain.cs), [ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs) and
[ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs) contain the whole mechanism (about 450 lines).

## Variations and gotchas

- **Dynamic offset count is one too many in build 5554.** The pass supplies `1 + N` dynamic offsets, the
  first meant for set 0, but in this build set 0 has two plain `UniformBuffer` bindings and no dynamic
  descriptors ([current/decomp/KSA/GlobalShaderBindings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GlobalShaderBindings.cs#L98-L123)). Vulkan requires the count to
  equal the dynamic descriptors in the bound sets. A second quirk cancels the visible effect: the helper
  reflects a field named `currentFrame` while the game's field is `_currentFrame`, so every offset is 0.
  This is from reading the code, not from a validation-layer run.
- **Set 0 is pinned to `DescriptorSet(0)`**, the main viewport's slot.
- **Memory.** Each pass owns a full-size target in the source format. The pre-ImGui chain uses the
  game's RGBA16F, 8 bytes per pixel.
- **An alternative to study.** Newer game renderers use the `RenderCore.Pipelines` family
  (`SimplePipeline`, `SimpleGraphicsPipeline`) instead of `RenderTechnique`; see
  [Build a Vulkan pipeline compatible with KSA's scene render target](../../../rendering/vulkan-passes/build-a-vulkan-pipeline-for-the-scene-target/overview.md).

## See also

- Siblings: [identity-for-neutral pass](write-a-post-processing-fragment-shader-1.md), [extra textures and port](write-a-post-processing-fragment-shader-2.md), [descriptor set per asset](write-a-post-processing-fragment-shader-4.md), [overview](overview.md)
- [RenderPassId-ordered fullscreen pass chain](../../../../mods/shaderextensions/features/post-processing-pass-chain.md), [Pre-ImGui post-processing on the HDR scene image](../../../../mods/shaderextensions/features/pre-imgui-post-processing.md), [Rebuilding mod GPU resources with the game renderer](../../../../mods/shaderextensions/features/renderer-rebuild-hook.md)
- [Insert a custom Vulkan pass into KSA's frame](../../../rendering/vulkan-passes/insert-a-custom-pass-into-render-game/overview.md), [Recreate mod render targets when KSA rebuilds its renderer](../../../rendering/vulkan-passes/handle-renderer-rebuilds/overview.md)
