---
type: Mod Feature
title: RenderPassId-ordered fullscreen pass chain
description: Builds one render target, RenderTechnique pipeline and descriptor set per post-processing shader, ordered by RenderPassId, where each pass samples the previous image through a sampler2D at set 1 binding 0 and draws a four-vertex strip with dynamic rendering
mod: shaderextensions
category: shaders
techniques: [post-processing, vulkan-pipeline, vulkan-texture, glsl-shader]
hooks: [harmony-transpiler KSA.Program.RenderGame, harmony-postfix KSA.Program.RebuildRenderer]
lifecycle: [frame:render, render:post-process]
game_types:
  - RenderCore.RenderTechnique
  - KSA.Rendering.RenderTarget
  - KSA.Rendering.RenderImage
  - KSA.Rendering.ImageBarrierInfo
  - KSA.GlobalShaderBindings
  - KSA.CommandBufferEx
  - KSA.RenderingPresets
  - Brutal.VulkanApi.Abstractions.Presets
tags: [shaders, post-processing, vulkan, dynamic-rendering, render-target, descriptor-sets]
---

Both post-processing stages (pre-ImGui and post-ImGui) are instances of one class,
`PostProcessingChain`. A chain takes a source image and a list of shaders, sorts the shaders by
their `RenderPassId` attribute and gives each one its own full-resolution render target. Pass *n*
samples the output of pass *n-1* (pass 0 samples the source), so a shader is a pure function of the
previous image plus whatever extra bindings it declares.[^sx-chain] [^sx-readme]

```xml
<!-- README.md L47 -->
<GlobalPostShader Id="BlurFrag" Path="Shaders/Blur.frag" RenderPassId="16" />
```

```glsl
// README.md L51-L68
#version 450 core

layout(location = 0) out vec4 outColor;
layout(set = 1, binding = 0) uniform sampler2D Source;
layout(std140, set = 1, binding = 1) uniform ShaderTime {
    uint FrameNumber;
    float DeltaTime;
    float RealTimeSinceStart;
    float TimeSinceStart;
    float TimeWarpSpeed;
} Time;
layout(location = 0) in vec2 Uv;

void main()
{
    vec4 c = texture(Source, Uv);
    outColor = c;
}
```

## How it works

1. **Shader metadata.** Both asset classes implement `IPostProcessingShader` (`PassId` from
   `RenderPassId`, default 0; vertex shader from `VertexShaderID`, default `ScreenspaceVert`, the
   game's `Shaders/PostProcess/screenspace_quad.vert`)
   ([ShaderExtensions/PostProcessing/IPostProcessingShader.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/IPostProcessingShader.cs#L8-L19),
   [ShaderExtensions/PostProcessing/PreImgui/PostProcessingShaderAsset.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PreImgui/PostProcessingShaderAsset.cs#L11-L30),
   [current/Content/Core/DefaultAssets.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/DefaultAssets.xml#L94)).[^sx-ipp] [^sx-preasset] [^ksa-defaultassets]
2. **Legacy filter.** `FilterSupported` drops any shader that still declares `SubpassId >= 0` or
   `RequiresUniqueRenderpass="true"` and logs an error naming the shader; the attributes are kept on
   the classes only so they can be detected
   ([ShaderExtensions/PostProcessing/PostProcessingChain.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingChain.cs#L264-L283)).
3. **Build.** `Build` disposes the previous passes and targets, then, in `OrderBy(PassId)` order,
   creates a `RenderTarget` of `renderer.Extent` in the source image's format with no depth,
   registers the current image as this pass's input, resolves cross-pass references, constructs a
   `PostProcessingPassRenderer` and registers its target as this pass's output
   ([ShaderExtensions/PostProcessing/PostProcessingChain.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingChain.cs#L49-L91)).
4. **Per-pass pipeline.** `PostProcessingPassRenderer` derives from `RenderCore.RenderTechnique`,
   passing its `RenderTarget` as the `IRenderPassInfo`; `RenderTarget.SetupGraphicsPipeline` adds a
   `VkPipelineRenderingCreateInfo`, so the pipeline targets dynamic rendering, not a `VkRenderPass`.
   The descriptor pool and set-1 layout come from the `ShaderEx` with binding 0 as a
   `CombinedImageSampler` written once with the source view and `Program.LinearClampedSampler`. The
   pipeline layout is `[GlobalShaderBindings.DescriptorSetLayout, bindingLayout]` plus the shader's
   push constant range; the pipeline is a triangle strip, back-face culling, no depth test, no
   blending, no vertex input
   ([ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs#L28-L87),
   [current/decomp/RenderCore/RenderTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/RenderTechnique.cs#L59-L118),
   [current/decomp/KSA.Rendering/RenderTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderTarget.cs#L356-L368)).[^sx-passrenderer] [^rc-rendertechnique] [^ksa-rendertarget]
5. **Record.** `Chain.Render` first transitions the source and every cross-referenced image to
   `SampledReadF` with the game's state-tracking `PipelineBarrier2(IRenderImage, ImageBarrierInfo)`,
   then records each pass: `BeginRendering` (load op `DontCare`), bind pipeline, full-extent viewport
   and scissor, push constants, bind sets 0 and 1, `Draw(4, 1, 0, 0)`, `EndRendering` leaving the
   target in `SampledReadF` for the next pass
   ([ShaderExtensions/PostProcessing/PostProcessingChain.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingChain.cs#L96-L107),
   [ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs#L98-L140),
   [current/decomp/KSA/CommandBufferEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CommandBufferEx.cs#L48-L54)).[^ksa-commandbufferex] [^ksa-imagebarrierinfo]
6. **Set 0 is the game's.** Binding the game's global set means a post shader can read the same
   per-viewport uniform data (camera, lighting, celestial, vessel blocks at binding 0, global
   constants at binding 1) as the game's own shaders
   ([current/decomp/KSA/GlobalShaderBindings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GlobalShaderBindings.cs#L13-L59)).[^ksa-globalshaderbindings]

## Lifecycle and timing

- **Build:** in the `RebuildRenderer` postfix and inline from `ImGuiPreRender` when the extent
  changed, see [Rebuilding mod GPU resources](renderer-rebuild-hook.md). Both call
  `Device.WaitIdle()` first.
- **Record:** inside `Program.RenderGame` command recording (`frame:render`), at the two insertion
  points described in [Pre-ImGui post-processing](pre-imgui-post-processing.md) and
  [Post-ImGui global post shaders](post-imgui-global-post-shaders.md). Both points are outside the
  game's swapchain render pass, which is required because `vkCmdBeginRendering` is not allowed inside
  a render pass instance.

## Key code

| Path | Role |
|---|---|
| [ShaderExtensions/PostProcessing/PostProcessingChain.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingChain.cs#L13-L107) | Chain build, ordering, render |
| [ShaderExtensions/PostProcessing/PostProcessingChain.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingChain.cs#L112-L244) | Image copy helpers used by the two stages |
| [ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs#L15-L141) | One fullscreen pass |
| [ShaderExtensions/PostProcessing/IPostProcessingShader.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/IPostProcessingShader.cs#L8-L19) | Shared metadata contract |

## Snippets

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

```csharp
// ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs L40-L73
            var device = renderer.Device;

            DescriptorPool = shader.CreateDescriptorPool(device, VkDescriptorType.CombinedImageSampler);
            bindingLayout = shader.CreateDescriptorSetLayout(
                device,
                new VkDescriptorSetLayoutBinding
                {
                    Binding = 0,
                    DescriptorType = VkDescriptorType.CombinedImageSampler,
                    DescriptorCount = 1,
                    StageFlags = VkShaderStageFlags.FragmentBit,
                });
            bindingSet = device.AllocateDescriptorSet(DescriptorPool, bindingLayout);

            VkDescriptorImageInfo* inputInfo = stackalloc VkDescriptorImageInfo[1];
            inputInfo[0] = new VkDescriptorImageInfo
            {
                ImageView = source.ImageView,
                ImageLayout = VkImageLayout.ShaderReadOnlyOptimal,
                Sampler = Program.LinearClampedSampler,
            };

            shader.UpdateDescriptorSets(device, new VkWriteDescriptorSet
            {
                DstBinding = 0,
                DescriptorType = VkDescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                DstSet = bindingSet,
                ImageInfo = inputInfo,
            });

            var pushConstRanges = shader.CreatePushConstantRanges();
            PipelineLayout = device.CreatePipelineLayout(
                [GlobalShaderBindings.DescriptorSetLayout, bindingLayout], pushConstRanges, null);
```

```csharp
// ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs L89-L93, L121-L139
        public static int getGlobalShaderBindingsCurrentFrame()
        {
            FieldInfo? currentFrameField = typeof(GlobalShaderBindings)?.GetField("currentFrame", BindingFlags.NonPublic | BindingFlags.Static);
            return (int)(currentFrameField?.GetValue(null) ?? 0);
        }
// ...
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

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `RenderTechnique` (ctor, `DescriptorPool`, `PipelineLayout`, `Pipeline`, `CreatePipeline`, `RebuildFrameResources`) | [current/decomp/RenderCore/RenderTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/RenderTechnique.cs#L13-L138) | Pipeline plumbing base class |
| `RenderTarget(ctx, name, extent, colorFormat, depthFormat)`, `BeginRendering`, `EndRendering`, `SetupGraphicsPipeline` | [current/decomp/KSA.Rendering/RenderTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderTarget.cs#L87-L368) | Per-pass color target with dynamic rendering |
| `ImageBarrierInfo.Presets.SampledReadF`, `TransferSrc`, `TransferDst` | [current/decomp/KSA.Rendering/ImageBarrierInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ImageBarrierInfo.cs#L11-L33) | Target layouts for sampling and copies |
| `CommandBufferEx.PipelineBarrier2(IRenderImage, ImageBarrierInfo, ...)` | [current/decomp/KSA/CommandBufferEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CommandBufferEx.cs#L48-L54) | State-tracked layout transitions |
| `GlobalShaderBindings.DescriptorSetLayout`, `DescriptorSet(int)`, `FrameOffset(int, int)` | [current/decomp/KSA/GlobalShaderBindings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GlobalShaderBindings.cs#L47-L59) | Set 0 of every pass |
| `RenderingPresets.ReverseZDepthStencil.NoDepthTest` | [current/decomp/KSA/RenderingPresets.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RenderingPresets.cs#L63) | Depth state preset |
| `ScreenspaceVert` shader asset | [current/Content/Core/DefaultAssets.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/DefaultAssets.xml#L94) | Default fullscreen vertex shader |

## Pitfalls and version notes

- **Dynamic offset count is off by one in build 5554.** The pass passes `1 + N` dynamic offsets, the
  first meant for set 0. In this build `GlobalShaderBindings` set 0 has two plain `UniformBuffer`
  bindings and one descriptor set per frame and viewport, so it consumes no dynamic offsets
  ([current/decomp/KSA/GlobalShaderBindings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GlobalShaderBindings.cs#L81-L133)).
  Vulkan requires the count to equal the dynamic descriptors in the bound sets, so this is a
  validation error. The helper also reflects a field named `currentFrame` while the field is
  `_currentFrame`, so it always returns 0 and the extra offset is `FrameOffset(0, 0) = 0`. All
  offsets are therefore zero and the mod's own dynamic buffers (which need offset 0) happen to be
  correct. The code dates from commit 873b6af, which replaced the removed
  `GlobalShaderBindings.DynamicOffset` (git history of the repository).
- **Set 0 is pinned to shader slot 0** (`DescriptorSet(0)`), the main viewport's slot; this is one
  reason the passes only work for the main window.
- **Equal `RenderPassId`s.** The README says the order between equal ids is not guaranteed; the code
  uses LINQ `OrderBy`, which is stable, so ties run in asset load order.
- **Memory per pass.** Every pass owns a full-size target in the source format. Pre-ImGui passes use
  the game's `R16G16B16A16SFloat`, 8 bytes per pixel, about 63 MiB per pass at 3840x2160.
- **No per-shader toggle.** A chain always runs every supported shader; to disable an effect, branch
  in the shader on a uniform (the README's advice).
- **`RenderTechnique`, not `RenderCore.Pipelines`.** The passes derive from the
  `RenderCore.RenderTechnique` base class and build their descriptor layout by hand through
  `ShaderEx`. Newer game renderers (for example `SuperMeshRenderSystem` and the CMAA2 compute passes)
  use the `RenderCore.Pipelines` family instead (`SimpleGraphicsPipeline`, `SimplePipeline` with
  `CreateLayoutsAndSets` and `RecreatePipeline`), which is the alternative to study for a new mod
  ([current/decomp/RenderCore.Pipelines/SimplePipeline.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Pipelines/SimplePipeline.cs#L164-L203),
  [current/decomp/RenderCore.Pipelines/SimpleGraphicsPipeline.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Pipelines/SimpleGraphicsPipeline.cs#L8-L30)).[^rc-simplepipeline] [^rc-simplegraphicspipeline]
- **Targets use `renderer.Extent`,** not the source image's extent. They match for the main window,
  but differ while `Renderer.ExtentOverride` is set.

## See also

- [Pre-ImGui post-processing on the HDR scene image](pre-imgui-post-processing.md)
- [Post-ImGui global post shaders on the swapchain image](post-imgui-global-post-shaders.md)
- [Cross-pass image references](cross-pass-image-references.md)
- [ShaderEx asset with extra descriptor bindings](shaderex-extra-bindings.md)
- [Declare post-processing assets: input and output references resolved while the chain is built](../../../howto/shaders/post-processing/declare-post-processing-shader-assets-in-xml/declare-post-processing-shader-assets-in-xml-3.md) - how-to article demonstrating this feature
- [Write a post-processing fragment shader: the minimal identity-for-neutral pass (MPFX contract)](../../../howto/shaders/post-processing/write-a-post-processing-fragment-shader/write-a-post-processing-fragment-shader-1.md) - how-to article demonstrating this feature
- [Write a post-processing fragment shader: how a pass is built from RenderTechnique and RenderTarget](../../../howto/shaders/post-processing/write-a-post-processing-fragment-shader/write-a-post-processing-fragment-shader-3.md) - how-to article demonstrating this feature

[^sx-chain]: ShaderExtensions/PostProcessing/PostProcessingChain.cs
[^sx-readme]: ShaderExtensions/README.md
[^sx-ipp]: ShaderExtensions/PostProcessing/IPostProcessingShader.cs
[^sx-preasset]: ShaderExtensions/PostProcessing/PreImgui/PostProcessingShaderAsset.cs
[^ksa-defaultassets]: Content/Core/DefaultAssets.xml (build 2026.10.10.5554)
[^sx-passrenderer]: ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs
[^rc-rendertechnique]: RenderCore.RenderTechnique (decompiled, build 2026.10.10.5554)
[^ksa-rendertarget]: KSA.Rendering.RenderTarget (decompiled, build 2026.10.10.5554)
[^ksa-commandbufferex]: KSA.CommandBufferEx (decompiled, build 2026.10.10.5554)
[^ksa-imagebarrierinfo]: KSA.Rendering.ImageBarrierInfo (decompiled, build 2026.10.10.5554)
[^ksa-globalshaderbindings]: KSA.GlobalShaderBindings (decompiled, build 2026.10.10.5554)
[^rc-simplepipeline]: RenderCore.Pipelines.SimplePipeline (decompiled, build 2026.10.10.5554)
[^rc-simplegraphicspipeline]: RenderCore.Pipelines.SimpleGraphicsPipeline (decompiled, build 2026.10.10.5554)
