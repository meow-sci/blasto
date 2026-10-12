---
type: How-To
title: Program.OffscreenTarget.SetupGraphicsPipeline, created lazily
description: Build a graphics pipeline that matches the scene target's dynamic-rendering formats and sample count by letting RenderTarget.SetupGraphicsPipeline stamp them, created on first use and rebuilt after renderer rebuilds
category: rendering
group: vulkan-passes
topic: build-a-vulkan-pipeline-for-the-scene-target
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/unscience/features/thug-life-textured-quad-render-pass
  - mods/gatos/features/thug-life-world-space-quad
techniques: [vulkan-pipeline]
hooks: [starmap StarMapAfterGui, harmony-postfix KSA.SuperMeshRenderSystem.RenderMainPass]
lifecycle: [frame:draw-ui, frame:render, render:scene-opaque]
game_types: [KSA.Program, KSA.Rendering.RenderTarget, KSA.ShaderReference]
tags: [vulkan, pipeline, dynamic-rendering, setup-graphics-pipeline, lazy]
---

Outcome: a `VkPipeline` that is valid inside the game's scene pass, with the correct colour and depth formats and sample
count, without hand-writing any of the dynamic-rendering structures. The game's own mesh renderers do the same: they fill
a `VkGraphicsPipelineCreateInfo` and let the target stamp the rest. Demonstrated by unscience Thug Life
([Draw a part-anchored textured quad inside the scene's opaque pass](../../../../mods/unscience/features/thug-life-textured-quad-render-pass.md)) and gatOS ([World-space textured quad (thug_life)](../../../../mods/gatos/features/thug-life-world-space-quad.md)).
Requires StarMap; the pass that records the draws is in
[insert a custom Vulkan pass](../insert-a-custom-pass-into-render-game/overview.md) (approach 2).

## Background: how the game does it

Since the scene moved to dynamic rendering, `Program.OffscreenTarget` (a `RenderTarget`)
[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L456) describes the scene attachments, and `RenderTarget.SetupGraphicsPipeline(ref VkGraphicsPipelineCreateInfo info)`
[current/decomp/KSA.Rendering/RenderTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderTarget.cs#L356-L392) chains a `VkPipelineRenderingCreateInfo` carrying the colour, depth and stencil
formats onto `info.Next`, sets `info.RenderPass = VkRenderPass.NullHandle`, fills `MultisampleState` with the target's sample
count, and supplies a viewport state if none is set. The structures it points at are owned by the target and overwritten on
each call.

```csharp
// current/decomp/KSA.Rendering/RenderTarget.cs L356-L392
public unsafe void SetupGraphicsPipeline(ref VkGraphicsPipelineCreateInfo info)
{
	ThrowIfDisposed();
	_colorFormatPtr?.Dispose();
	_colorFormatPtr = ((ColorAttachmentCount > 0) ? new PtrOwner<VkFormat>(ColorFormat) : null);
	_renderingCreateInfoPtr.Value = new VkPipelineRenderingCreateInfo
	{
		ColorAttachmentCount = ColorAttachmentCount,
		ColorAttachmentFormats = ((ColorAttachmentCount > 0) ? _colorFormatPtr : null),
		DepthAttachmentFormat = DepthFormat,
		StencilAttachmentFormat = StencilFormat,
		ViewMask = 0
	};
	VkPipelineMultisampleStateCreateInfo vkPipelineMultisampleStateCreateInfo2;
	if (info.MultisampleState == null)
	{
		VkPipelineMultisampleStateCreateInfo vkPipelineMultisampleStateCreateInfo = new VkPipelineMultisampleStateCreateInfo();
		vkPipelineMultisampleStateCreateInfo.SampleShadingEnable = false;
		vkPipelineMultisampleStateCreateInfo.MinSampleShading = 0f;
		vkPipelineMultisampleStateCreateInfo.SampleMask = null;
		vkPipelineMultisampleStateCreateInfo.AlphaToCoverageEnable = false;
		vkPipelineMultisampleStateCreateInfo.AlphaToOneEnable = false;
		vkPipelineMultisampleStateCreateInfo2 = vkPipelineMultisampleStateCreateInfo;
	}
	else
	{
		vkPipelineMultisampleStateCreateInfo2 = *info.MultisampleState;
	}
	VkPipelineMultisampleStateCreateInfo vkPipelineMultisampleStateCreateInfo3 = vkPipelineMultisampleStateCreateInfo2;
	vkPipelineMultisampleStateCreateInfo3.RasterizationSamples = Samples;
	_multisampleStatePtr.Value = vkPipelineMultisampleStateCreateInfo3;
	_renderingCreateInfoPtr.Value.Next = info.Next;
	info.Next = (VkPipelineRenderingCreateInfo*)_renderingCreateInfoPtr;
	info.RenderPass = VkRenderPass.NullHandle;
	info.MultisampleState = _multisampleStatePtr;
	if (info.ViewportState == null)
	{
```

## Lifecycle and timing

| Step | When | Why |
|---|---|---|
| Create the pipeline | the first user action (Thug Life's window runs in `[StarMapAfterGui]`, `frame:draw-ui`), never at `StarMapAllModsLoaded` | `Program.OffscreenTarget` is created in the `Program` constructor after `ModLibrary.LoadAll` returns, so it is null at `startup:all-mods-loaded` |
| Use it | `frame:render`, `render:scene-opaque`, inside the hook that records the draws | The scope's formats are those of the same target |
| Rebuild | after `Program.RebuildRenderer` if the sample count may have changed | see [Recreate mod render targets when KSA rebuilds its renderer](../handle-renderer-rebuilds/overview.md) |

See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for the startup sequence.

## Steps

1. Get the shader stages. The stock unlit mesh shaders give a textured pipeline with no GLSL of your own:
   `ModLibrary.Get<ShaderReference>("UnlitMeshVert")` and `"UnlitMeshFrag"`, turned into stages with
   `RenderTechnique.CreateShaderStages`. The vertex shader takes `inPos` (location 0), `inUV` (location 1) and a
   push-constant `worldViewProjMatrix`.

2. Fill the create-info with the game's presets: renderer's dynamic state and viewport state, triangle list,
   `CullNone`, reverse-Z depth test and write, alpha blend.

3. Call `Program.OffscreenTarget.SetupGraphicsPipeline(ref info)` **immediately** before `CreateGraphicsPipeline`; do not
   set `RenderPass`, `Subpass` or `MultisampleState` yourself.

   ```csharp
   // thug-life.lib/ThugLifeQuadRenderer.cs L112-L154
   var shaderRefs = new ShaderReference[]
   {
       ModLibrary.Get<ShaderReference>("UnlitMeshVert"),
       ModLibrary.Get<ShaderReference>("UnlitMeshFrag"),
   };
   var stages = RenderTechnique.CreateShaderStages(device, shaderRefs.AsSpan());

   var vertexInput = new VertexInput(1, 2)
       .AddBinding(0, ByteSize.Of<QuadVertex>(), VkVertexInputRate.Vertex)
       .AddAttribute(0, 0, VkFormat.R32G32B32SFloat, ByteSize.Zero)
       .AddAttribute(1, 0, VkFormat.R32G32SFloat, ByteSize.Of<float3>())
       .Check();

   var info = new VkGraphicsPipelineCreateInfo
   {
       Layout = layout,
       StageCount = stages.Count,
       Stages = stages,
       DynamicState = renderer.DynamicStateInfo,
       ViewportState = renderer.ViewportState,
       VertexInputState = vertexInput,
       InputAssemblyState = Presets.InputAssembly.TriangleList,
       RasterizationState = Presets.Rasterization.Fill.CullNone,
       DepthStencilState = RenderingPresets.ReverseZDepthStencil.DepthTestWrite,
       ColorBlendState = Presets.BlendState.BlendColorAlpha,
   };

   // KSA 2026.8.19.5261 migrated the main scene pass from classic Vulkan render
   // passes to dynamic rendering. Program.OffScreenPass (RenderPassState, with
   // .Pass/.SampleCount) no longer exists; the offscreen target is now
   // Program.OffscreenTarget (RenderTarget : IRenderPassInfo), and pass
   // compatibility is established by SetupGraphicsPipeline, which chains a
   // VkPipelineRenderingCreateInfo describing the colour/depth formats onto
   // pNext, sets RenderPass to VK_NULL_HANDLE, and fills in MultisampleState
   // with the target's sample count. So RenderPass/Subpass/MultisampleState must
   // NOT be set by hand here — this call supplies all three. Must stay
   // immediately before CreateGraphicsPipeline: the structures it points pNext
   // at are owned and overwritten by the RenderTarget on each call.
   // This mirrors the game's own main-pass pipelines (GenericMeshRenderer,
   // PartModelRenderer, PartModelGlass).
   Program.OffscreenTarget.SetupGraphicsPipeline(ref info);

   return device.CreateGraphicsPipeline(default(VkPipelineCache), info, null);
   ```

4. Create lazily. The unscience manager brings the GPU objects up in the first `Add` and keeps the draw gate off until then:

   ```csharp
   // thug-life.lib/ThugLifeRenderManager.cs L91-L115
   private bool EnsureGpuResources()
   {
       if (!IsReady) return false;
       if (IsGpuReady) return true;

       try
       {
           var renderer = Program.GetRenderer();
           _texture = new ThugLifeTextureFactory(renderer);
           _quad = new ThugLifeQuadRenderer(renderer, _texture);
           Active = true;
           _lastError = null;
           Console.WriteLine("thug-life: render manager initialized");
           return true;
       }
       catch (Exception ex)
       {
           _lastError = $"init failed: {ex.Message}";
           Console.WriteLine($"thug-life: {_lastError}");
           _disabledDueToError = true;
           Active = false;
           DisposeGpuResources();
           return false;
       }
   }
   ```

5. If the call throws `NullReferenceException` at startup you built too early; the unscience README records the "init
   failed: Object reference not set" error this fixed.

## Complete example

| File | Role |
|---|---|
| [thug-life.lib/ThugLifeQuadRenderer.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/thug-life.lib/ThugLifeQuadRenderer.cs#L112-L154) | Pipeline built against the offscreen target |
| [gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeQuadRenderer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeQuadRenderer.cs#L131-L168) | gatOS port, same call |

## Variations and gotchas

- **Draw it where the target matches.** A pipeline stamped from `Program.OffscreenTarget` fits every viewport's scene scope,
  because viewport-owned offscreen targets are created with the main target's colour format and `GameSettings.GetSampleCount()`.
- **Do not build against the swapchain pass.** The wrong target passes validation but breaks depth.
- **Stale sample count.** The pipeline keeps the sample count it was built with; after a renderer rebuild with a different MSAA
  setting, destroy and recreate it.
- **Older builds.** Before the dynamic-rendering migration the scene was a classic render pass (`Program.OffScreenPass`) and
  pipelines named it in `RenderPass`; code written for those builds must switch to `SetupGraphicsPipeline`.
- **Pipeline destroy.** Destroy only after `WaitIdle` ([free mod-owned Vulkan resources safely](../free-vulkan-resources-safely/overview.md)).

## See also

- [Overview](overview.md)
- [Approach 2: hand-built pipeline with premultiplied alpha](build-a-vulkan-pipeline-for-the-scene-target-2.md)
- [Approach 3: free-standing pipeline for private targets](build-a-vulkan-pipeline-for-the-scene-target-3.md)
- [Draw a part-anchored textured quad inside the scene's opaque pass](../../../../mods/unscience/features/thug-life-textured-quad-render-pass.md), [World-space textured quad (thug_life)](../../../../mods/gatos/features/thug-life-world-space-quad.md)
- [Insert a custom Vulkan pass into KSA's frame](../insert-a-custom-pass-into-render-game/overview.md), [Draw a textured quad attached to a part or the camera](../../textures-and-decals/draw-a-textured-quad-in-the-world/overview.md)
