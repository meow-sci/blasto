---
type: How-To
title: Postfix SuperMeshRenderSystem.RenderTranslucencyPass with tracked barriers
description: Reopen a dynamic-rendering scope over the scene attachments after atmosphere, clouds and plumes so translucent mod content composites last
category: rendering
group: vulkan-passes
topic: insert-a-custom-pass-into-render-game
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/purrtty/features/draw-after-translucency-pass-postfix]
techniques: [harmony-postfix, vulkan-pipeline]
hooks: [harmony-postfix KSA.SuperMeshRenderSystem.RenderTranslucencyPass]
lifecycle: [frame:render, render:translucency]
game_types: [KSA.SuperMeshRenderSystem, KSA.Rendering.RenderTarget, KSA.Rendering.ImageBarrierInfo, KSA.IViewport]
tags: [harmony, translucency, barriers, dynamic-rendering, scene-attachments]
---

Outcome: translucent mod content that must be hidden behind opaque parts and terrain, yet must survive the
atmosphere, cloud and ocean passes (which repaint scene colour from depth), is drawn at the end of the
translucency stage. A postfix on `SuperMeshRenderSystem.RenderTranslucencyPass` cannot append to the scope the
original already closed, so it declares its barriers and opens a new scope over the same attachments. Demonstrated
by purrTTY's in-world terminal quads ([Injecting world-space draws after KSA's translucency pass](../../../../mods/purrtty/features/draw-after-translucency-pass-postfix.md)). Requires StarMap
and Harmony; a matching pipeline is covered in [build a Vulkan pipeline for the scene target](../build-a-vulkan-pipeline-for-the-scene-target/overview.md).

## Background: how the game does it

`RenderTranslucencyPass(IViewport viewport, CommandBuffer commandBuffer, bool useCustomRenderPass = false)`
[current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L459) begins by barriering the viewport's colour and depth attachments, opens its
own rendering scope when `useCustomRenderPass` is true, draws translucent super meshes and closes the scope again
[current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L463-L471). In `RenderGame` it is called after the ocean, atmosphere and
clouds, volumetric exhausts, MSAA transparencies and part glass (step 14), followed by translucent particles, the
colour resolve, bloom and orbit lines.

```csharp
// current/decomp/KSA/Program.cs L4893-L4904
_transparenciesMsaaResolve.ApplyTransparenciesMsaaIfEnabled(commandBuffer2, RenderedViewport, _offscreenTarget);
PartModelGlass.Shared.WriteCommandsColor(commandBuffer2, RenderedViewport, resourceFrameIndex);
if (IsRaytracingActive && MainViewport.Mode == CameraMode.IVA)
{
	RaytracingRenderer?.PartRaytracingDataTranslucent.WriteCommandsColor(commandBuffer2, RenderedViewport, resourceFrameIndex);
}
SuperMeshRenderSystem.SetCamMatrices(_cameraData[shaderSlot].View, _cameraData[shaderSlot].Projection);
Profiler.MainThread.Begin(Profiler.Tag.RecordSuperMeshTranslucencyPass);
SuperMeshRenderSystem.RenderTranslucencyPass(RenderedViewport, commandBuffer2, useCustomRenderPass: true);
Profiler.MainThread.End();
ParticleSystem?.WriteCommandsColorTranslucent(commandBuffer2, RenderedViewport, resourceFrameIndex);
RenderedViewport.OffscreenTarget.ResolveAttachments(commandBuffer2, inResolveDepth: false);
```

An earlier purrTTY postfixed `RenderMainPass` instead; atmosphere, clouds and ocean then overwrote the quad wherever
it overlapped the planet, leaving a cutout along the planet's silhouette.

## Lifecycle and timing

| What | Where |
|---|---|
| Postfix records the draws | `render:translucency` inside `frame:render` (step 14), main thread, into the frame's single command buffer |
| State the postfix writes | statics set in `frame:draw-ui` by the mod; read later on the same thread, so no locking |
| After it | translucent particles, resolve, underwater, bloom, orbit lines, gizmos, ImGui |

The postfix fires once per call (every visible secondary viewport, the main view, and the editor); all of them
pass `useCustomRenderPass: true`. It does not depend on `Program.DrawUI`, so quads keep drawing when the HUD is hidden.
Since bloom, underwater tint and the MSAA resolve run after this pass, the content is post-processed like glass, and
translucent particles draw over it.

## Steps

1. Patch the method with a postfix named like the original's parameters. In this build the target is
   `(IViewport viewport, CommandBuffer commandBuffer, bool useCustomRenderPass)` while the postfix
   declares `(CommandBuffer, bool, IViewport)`; Harmony injects by name, so keep the names identical.

2. Return unless `useCustomRenderPass`, a viewport is present and your feature is active.

3. Take `viewport.OffscreenTarget.ColorAttachment` and `DepthAttachment` (the MSAA images when multisampled),
   declare their state with the tracked barrier helper, open `BeginRendering` with `LoadOp.Load` / `StoreOp.Store`
   over the viewport size, draw, and `EndRendering` in a `finally`.

   ```csharp
   // purrTTY.GameMod/InWorld/Patches/RenderTranslucencyPassPatch.cs L110-L146
   commandBuffer.PipelineBarrier2(colorImage, ImageBarrierInfo.Presets.ColorAttachmentReadWrite);
   commandBuffer.PipelineBarrier2(depthImage, ImageBarrierInfo.Presets.DepthStencilAttachmentReadWrite);

   var colorAttachment = new VkRenderingAttachmentInfo
   {
       ImageLayout = VkImageLayout.ColorAttachmentOptimal,
       ImageView   = colorImage.ImageView,
       ResolveMode = VkResolveModeFlags.None,
       LoadOp      = VkAttachmentLoadOp.Load,
       StoreOp     = VkAttachmentStoreOp.Store,
   };
   var depthAttachment = new VkRenderingAttachmentInfo
   {
       ImageLayout = VkImageLayout.DepthStencilAttachmentOptimal,
       ImageView   = depthImage.ImageView,
       ResolveMode = VkResolveModeFlags.None,
       LoadOp      = VkAttachmentLoadOp.Load,
       StoreOp     = VkAttachmentStoreOp.Store,
   };
   var renderingInfo = new VkRenderingInfo
   {
       RenderArea          = new VkRect2D(viewport.Size.X, viewport.Size.Y),
       LayerCount          = 1,
       ColorAttachmentCount = 1,
       ColorAttachments    = &colorAttachment,
       DepthAttachment     = &depthAttachment,
   };

   commandBuffer.BeginRendering(in renderingInfo);
   try
   {
       InWorldTerminalManager.Instance?.RecordDrawAll(commandBuffer);
   }
   finally
   {
       commandBuffer.EndRendering();
   }
   ```

4. Declare write access in the barrier. The helper takes the source state from the image's recorded state and records the
   new state as the source for the next barrier, so the declared state must include the writes your draw performs;
   the next consumers are the MSAA resolve and the bloom compute.

5. On failure the postfix logs the exception and clears the active flag, so the error appears once.

## Complete example

| File | Role |
|---|---|
| [purrTTY.GameMod/InWorld/Patches/RenderTranslucencyPassPatch.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Patches/RenderTranslucencyPassPatch.cs) | The postfix, barriers and scope |
| [purrTTY.GameMod/InWorld/Display/InWorldQuad.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Display/InWorldQuad.cs) | Descriptor set and per-quad draw |

The quad pipeline and part/camera anchoring are in [Draw a textured quad attached to a part or the camera](../../textures-and-decals/draw-a-textured-quad-in-the-world/overview.md).

## Variations and gotchas

- **Do not append to a closed scope.** Open a new one with `LoadOp.Load` and the same attachments the original used.
- **Secondary viewports and the editor.** purrTTY draws every quad there too, always with the main camera's
  matrices, because `RecordDrawAll` does not filter by viewport; add an `IsMain()` style filter if that matters.
  Secondary targets are built with the same sample count, so the pipeline stays compatible.
- **Older builds** used classic render passes (`Program.OffScreenPass`); pipelines must now be built for dynamic
  rendering.
- **Pass ordering.** If you need depth-reading content after bloom or gizmos, use approach 1 instead.

## See also

- [Overview](overview.md)
- [Approach 1: ResolveAttachments postfix](insert-a-custom-pass-into-render-game-1.md)
- [Approach 2: RenderMainPass postfix](insert-a-custom-pass-into-render-game-2.md)
- [Approach 4: SunbloomRenderer.Render prefix](insert-a-custom-pass-into-render-game-4.md)
- [Injecting world-space draws after KSA's translucency pass](../../../../mods/purrtty/features/draw-after-translucency-pass-postfix.md)
- [Draw a textured quad attached to a part or the camera](../../textures-and-decals/draw-a-textured-quad-in-the-world/overview.md), [Build a Vulkan pipeline compatible with KSA's scene render target](../build-a-vulkan-pipeline-for-the-scene-target/overview.md)
