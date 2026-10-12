---
type: How-To
title: A single textured quad via the same hook with its own pipeline setup
description: Port the RenderMainPass quad to a single textured quad with its own buffer and texture plumbing, attached by the part's ego matrix and installed on demand
category: rendering
group: textures-and-decals
topic: draw-a-textured-quad-in-the-world
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/thug-life-world-space-quad]
techniques: [vulkan-pipeline, harmony-postfix]
hooks:
  - harmony-postfix KSA.SuperMeshRenderSystem.RenderMainPass
  - starmap StarMapBeforeGui
  - starmap StarMapAfterOnFrame
lifecycle: [frame:render, render:scene-opaque, frame:draw-ui, frame:after-on-frame]
game_types: [KSA.SuperMeshRenderSystem, KSA.Program, KSA.Part, RenderCore.SimpleVkTexture]
tags: [quad, part-anchor, ego-matrix, render-main-pass, lazy-patch]
---

Outcome: a flat quad that rides a part through flight, drawn by a pipeline built once against the scene target. It is a port of approach 1 into gatOS: the same `RenderMainPass` postfix, different buffer and texture plumbing, a per-entry camera mask, and a Harmony patch installed only while at least one quad exists. This is also the part-anchoring sub-step for quads (ego-space model matrix from
`GetMatrixAsmb2Ego`, computed in the render hook). Demonstrated by gatOS ([World-space textured quad (thug_life)](../../../../mods/gatos/features/thug-life-world-space-quad.md)). Prerequisites: StarMap, Harmony.

## Background: how the game does it

Same as approach 1: `RenderMainPass` [current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L388) is called inside the open offscreen scope [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4837-L4848), `RenderTarget.SetupGraphicsPipeline` [current/decomp/KSA.Rendering/RenderTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderTarget.cs#L356-L392) stamps formats and sample count. `Vehicle.GetMatrixAsmb2Ego(camera)` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1270) gives the vehicle's ego transform.
`RenderMainPass` is also called by `RenderViewport` and `RenderEditor`, so the postfix fires once per rendered viewport.

## Lifecycle and timing

- `render:scene-opaque`: `Program.RenderGame` runs on the main thread inside `Program.OnFrame` (via `Render`), so the postfix, the command drain that adds entries and the per-frame anchor update share one thread and share state without locks.
- `UpdateThugLife()` runs in `DrivePerFrame`: from `StarMapBeforeGui` (`frame:draw-ui`) before rendering, or from `StarMapAfterOnFrame` on hidden-HUD frames, after that frame has rendered. The per-draw model matrix is computed in the postfix itself, so the quad tracks its part either way.
- A per-entry camera mask chooses main, crew-portrait or secondary viewports by testing `Program.RenderedViewport`.

See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Install the patch on demand** on a private Harmony instance (`Harmony("gatos.thug_life")`) when the first entry is created, and unpatch when the last is removed (set `Active = false`, unpatch, then dispose GPU objects). The postfix binds the `commandBuffer` parameter by name and returns at once unless the manager is active; the first draw exception latches the feature off with one log line.

   ```csharp
   // gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeRenderPatches.cs L38-L79
   public static void Apply(Harmony harmony)
   {
       var original = AccessTools.Method(
           typeof(SuperMeshRenderSystem),
           nameof(SuperMeshRenderSystem.RenderMainPass));
       if (original is null)
           throw new MissingMethodException(
               typeof(SuperMeshRenderSystem).FullName,
               nameof(SuperMeshRenderSystem.RenderMainPass));

       var postfix = AccessTools.Method(typeof(ThugLifeRenderPatches), nameof(RenderMainPassPostfix));
       harmony.Patch(original, postfix: new HarmonyMethod(postfix));
   }

   public static void Remove(Harmony harmony)
   {
       var original = AccessTools.Method(
           typeof(SuperMeshRenderSystem),
           nameof(SuperMeshRenderSystem.RenderMainPass));
       var postfix = AccessTools.Method(typeof(ThugLifeRenderPatches), nameof(RenderMainPassPostfix));
       if (original is not null && postfix is not null)
           harmony.Unpatch(original, postfix);
   }

   private static void RenderMainPassPostfix(CommandBuffer commandBuffer)
   {
       if (!ThugLifeManager.Active)
           return;
       try
       {
           ThugLifeManager.Instance?.RecordDraws(commandBuffer);
       }
       catch (Exception ex)
       {
           // A per-frame render exception would spam; log once and let the manager self-disable.
           if (!_loggedFault)
           {
               _loggedFault = true;
               ModLog.Log.Debug($"gatOS thug-life render postfix error (logged once): {ex.Message}");
           }
       }
   }
   ```

2. **Pipeline:** stock `UnlitMeshVert`/`UnlitMeshFrag`, one combined image sampler and a vertex-stage push constant for the MVP, `CullNone`, reverse-Z depth test and write, alpha blend, and `Program.OffscreenTarget.SetupGraphicsPipeline(ref info)`; so the quad follows the engine's anti-aliasing setting.

   ```csharp
   // gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeQuadRenderer.cs L131-L168
   private static VkPipeline BuildPipeline(DeviceEx device, Renderer renderer, VkPipelineLayout layout)
   {
       var shaderRefs = new[]
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
           RasterizationState = Presets.Rasterization.Fill.CullNone, // double-sided
           DepthStencilState = RenderingPresets.ReverseZDepthStencil.DepthTestWrite, // offscreen pass is reverse-Z
           ColorBlendState = Presets.BlendState.BlendColorAlpha,
       };

       // CRITICAL (quad.md): bind to the OFFSCREEN scene target, NOT the swapchain/main pass — the wrong
       // one passes validation but silently breaks depth. Since rev 5154 the offscreen pass is Vulkan
       // dynamic rendering rather than a VkRenderPass, so the target stamps the pipeline itself: it fills
       // VkPipelineRenderingCreateInfo (color/depth/stencil formats), nulls RenderPass and sets
       // RasterizationSamples from its own MSAA state. This is the identical call KSA's own
       // GenericMeshRenderer/PartModelRenderer make, so the quad follows the engine's sample count.
       Program.OffscreenTarget.SetupGraphicsPipeline(ref info);
       return device.CreateGraphicsPipeline(default(VkPipelineCache), info, null);
   }
   ```

3. **Per draw:** the model matrix in ego space from `Vehicle.GetMatrixAsmb2Ego(camera)` and the anchor part's pose, `MVP = model * viewProjection` (row-vector convention), `Program.SetViewport(cmd)`, push constants, `DrawIndexed`:

   ```csharp
   // gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeQuadRenderer.cs L286-L316
   public void RecordDraw(CommandBuffer cmd, ThugLifeEntry entry)
   {
       if (_disposed || !entry.Visible)
           return;

       var camera = Program.GetRenderCamera();
       if (camera == null)
           return;
       if (!TryComputeModelEgo(entry, camera, out var modelEgo))
           return;

       // Row-vector convention (KSA matches .NET / DirectXMath): MVP = model * viewProjection.
       var mvp = modelEgo * camera.MVP.viewProjection;

       cmd.BindPipeline(VkPipelineBindPoint.Graphics, _pipeline);
       VkDescriptorSet setCopy = _descriptorSet;
       cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _pipelineLayout, 0,
           new ReadOnlySpan<VkDescriptorSet>(ref setCopy),
           default(Span<ByteSize32>));

       Program.SetViewport(cmd);
       cmd.PushConstants(_pipelineLayout, VkShaderStageFlags.VertexBit, ByteSize.Zero, mvp);

       VkBuffer vbHandle = _vb.VkBuffer;
       ByteSize64 vbOff = (ByteSize64)_vb.BindOffset;
       cmd.BindVertexBuffers(0,
           new ReadOnlySpan<VkBuffer>(ref vbHandle),
           new ReadOnlySpan<ByteSize64>(ref vbOff));
       cmd.BindIndexBuffer(_ib.VkBuffer, (ByteSize64)_ib.BindOffset, VkIndexType.UInt16);
       cmd.DrawIndexed(_indexCount, 1, 0, 0, 0);
   }
   ```

4. **Re-resolve the anchor part by `InstanceId`** each frame, and drop entries whose vessel despawned.

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeRenderPatches.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeRenderPatches.cs) | Postfix and install/remove |
| [gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeQuadRenderer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeQuadRenderer.cs) | Pipeline and draw |
| [gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeManager.cs#L271-L333) | Lazy lifecycle, patch on demand |

## Variations and gotchas

- **Bind the pipeline to the offscreen scene target, not the swapchain pass;** the wrong target passes validation but breaks depth.
- **Rev 5154 removed `Program.OffScreenPass`;** `SetupGraphicsPipeline` is the replacement, and a different class named `KSA.Rendering.RenderTarget` now exists.
- **`RenderMainPass` gained a leading `IViewport` parameter** in a later build; binding by parameter name made that transparent.
- **Teardown here disposes right after unpatching without waiting;** the gatOS sticker renderer later added `WaitIdle` and a retire queue, the safer pattern.

## See also

- [Overview](overview.md)
- [Approach 1: opaque pixel quads](draw-a-textured-quad-in-the-world-1.md)
- [Approach 3: premultiplied-alpha translucent quad](draw-a-textured-quad-in-the-world-3.md)
- [World-space textured quad (thug_life)](../../../../mods/gatos/features/thug-life-world-space-quad.md)
- [Insert a custom Vulkan pass into KSA's frame](../../vulkan-passes/insert-a-custom-pass-into-render-game/overview.md), [Choose a render frame and anchor effects to bodies, parts and terrain](../../overlays/choose-a-render-frame-and-anchor-effects/overview.md)
