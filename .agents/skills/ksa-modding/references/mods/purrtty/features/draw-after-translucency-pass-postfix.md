---
type: Mod Feature
title: Injecting world-space draws after KSA's translucency pass
description: A Harmony postfix on SuperMeshRenderSystem.RenderTranslucencyPass that issues tracked image barriers and opens its own dynamic-rendering scope over the scene's color and depth attachments, so custom translucent geometry draws after atmosphere, clouds and ocean have repainted the frame
mod: purrtty
category: rendering
techniques: [harmony-postfix, vulkan-pipeline]
hooks: [harmony-postfix KSA.SuperMeshRenderSystem.RenderTranslucencyPass]
lifecycle: [frame:render, render:translucency]
game_types:
  - KSA.SuperMeshRenderSystem
  - KSA.Program
  - KSA.Rendering.RenderTarget
  - KSA.Rendering.ImageBarrierInfo
  - KSA.CommandBufferEx
  - KSA.IViewport
  - KSA.PartModelGlass
tags: [vulkan, harmony, render-order, dynamic-rendering, barriers, translucency, in-world]
---

A mod that wants geometry in the 3D scene has to pick a moment in KSA's render pipeline and get
the Vulkan synchronization right for it. purrTTY's in-world terminals are translucent quads that
must be hidden behind opaque parts and terrain, yet survive the atmosphere, cloud and ocean passes,
which repaint scene color from the depth buffer. The answer is a postfix on the pass KSA itself
uses for translucent scene geometry, reopening a rendering scope of its own.

## How it works

**Where.** `Program.RenderGame` (flight scene, build 2026.10.10.5554) records, in order:
`SuperMeshRenderSystem.RenderMainPass` (opaque meshes), end of the opaque scope, ocean
(`_oceanRenderer.Render`), atmosphere and clouds (`_planetTransparenciesRenderer.Render`, a compute
pass), volumetric exhausts, `PartModelGlass.Shared.WriteCommandsColor`,
**`SuperMeshRenderSystem.RenderTranslucencyPass(..., useCustomRenderPass: true)`**, translucent
particles, MSAA resolve, underwater, sun bloom, overall bloom, selected-part outline, then orbit
lines, gizmos, grid, gauges and ImGui.[^ksa-program] An earlier purrTTY postfixed `RenderMainPass`;
atmosphere, clouds and ocean then overwrote the quad wherever it overlapped the planet, giving a
cutout along the planet's silhouette (gotcha 32).[^gotchas]

**What the postfix does** (`RenderTranslucencyPassPatch.Postfix(CommandBuffer commandBuffer, bool
useCustomRenderPass, IViewport viewport)`):[^translucency-patch]

1. Return unless `useCustomRenderPass`, a viewport, and at least one live in-world terminal
   (`InWorldTerminalManager.Active`).
2. Take `viewport.OffscreenTarget.ColorAttachment` and `DepthAttachment` (`RenderImage`, already the
   MSAA images when multisampled).[^ksa-render-target]
3. Barriers through KSA's tracked-state helper: `commandBuffer.PipelineBarrier2(colorImage,
   ImageBarrierInfo.Presets.ColorAttachmentReadWrite)` and the depth equivalent
   `DepthStencilAttachmentReadWrite`. The helper takes the source from the image's recorded state
   and records the new state as the source for the next barrier, so the declared state must include
   the writes the quad performs; since KSA 5482 the next consumer is the MSAA resolve and bloom
   compute.[^ksa-cmdbuf-ex][^ksa-barrier-info]
4. `BeginRendering` with color and depth `LoadOp.Load` / `StoreOp.Store` over the viewport size;
   these are exactly the attachments and ops `RenderTranslucencyPass` uses for its own scope, which
   it has already closed with `EndRendering` before returning.[^ksa-supermesh]
5. `InWorldTerminalManager.Instance.RecordDrawAll(commandBuffer)`, then `EndRendering` in a
   `finally`. A failure logs the exception and sets `Active = false` so it does not spam every frame;
   per-instance failures inside `RecordDrawAll` retire only that instance.[^inworld-manager]

## Lifecycle and timing

- `render:translucency` inside `frame:render`: `Program.Render` runs on the main thread after
  `frame:pre-render` and `UpdateRenderingResources`, and `RenderGame` records the frame's single
  graphics command buffer that `PostRender` submits. The postfix appends to that command buffer; it
  does not submit.
- The postfix fires once per call of the pass, not once per frame. In a flight frame,
  `RenderGame` first calls `RenderViewport` for every visible secondary viewport (each calls
  `RenderTranslucencyPass` after its own atmosphere and sun bloom), then records the main view, whose
  call sits after `PartModelGlass` and before translucent particles, the MSAA resolve, underwater,
  bloom, orbit lines, gizmos and ImGui. In the vehicle editor the only call is in `RenderEditor`.[^ksa-program]
  All calls pass `useCustomRenderPass: true`, so purrTTY's guard does not filter any of them out.
- `InWorldTerminalManager.Active`/`Instance` are plain statics written on the main thread in
  `frame:draw-ui`; the postfix reads them later on the same thread, so no locking is needed.
- The patch is applied at `startup:all-mods-loaded` as an optional patch (see
  [failure-isolated patching](./failure-isolated-harmony-patching.md)).
- It does not depend on `Program.DrawUI`: quads keep drawing when the HUD is hidden. Their
  textures do not update then, because the off-screen terminal render runs in `StarMapAfterGui`,
  which `Program.OnFrame` skips while `DrawUI` is false (see
  [render-to-texture](./offscreen-imgui-context-render-to-texture.md)).

## Key code

| Path | Role |
|---|---|
| [purrTTY.GameMod/InWorld/Patches/RenderTranslucencyPassPatch.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Patches/RenderTranslucencyPassPatch.cs#L60-L155) | The postfix |
| [purrTTY.GameMod/InWorld/InWorldTerminalManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/InWorldTerminalManager.cs#L225-L245) | `RecordDrawAll` with per-instance failure isolation |
| [purrTTY.GameMod/InWorld/Display/InWorldQuad.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Display/InWorldQuad.cs#L134-L172) | Per-quad draw commands |
| [.claude/skills/upgrade-ksa/SKILL.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/.claude/skills/upgrade-ksa/SKILL.md) | Lists this as the highest-risk render coupling and what to diff |

## Snippets

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

The game's own scope that the postfix mirrors (5554):

```csharp
// current/decomp/KSA/SuperMeshRenderSystem.cs L463-L471
			if (useCustomRenderPass)
			{
				RenderTarget offscreenTarget = viewport.OffscreenTarget;
				AtmosphereRenderer planetAtmosphereRenderer = Program.PlanetAtmosphereRenderer;
				Span<VkImageMemoryBarrier2> inImages = stackalloc VkImageMemoryBarrier2[5];
				BarrierBatch barrierBatch = new BarrierBatch(inImages);
				barrierBatch.Add(offscreenTarget.ColorAttachment, ImageBarrierInfo.Presets.ColorAttachmentReadWrite);
				barrierBatch.Add(offscreenTarget.DepthAttachment, ImageBarrierInfo.Presets.DepthStencilAttachmentReadWrite);
				barrierBatch.Add(planetAtmosphereRenderer.AmbientTarget, ImageBarrierInfo.Presets.SampledReadF);
```

## Game APIs touched

| API | Where | Purpose |
|---|---|---|
| `SuperMeshRenderSystem.RenderTranslucencyPass(IViewport viewport, CommandBuffer commandBuffer, bool useCustomRenderPass = false)` | [current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L459-L520) | Patch target; opens and closes its own dynamic-rendering scope |
| `Program.RenderViewport` call site (each visible secondary viewport) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4595-L4606) | Secondary-view order around the target |
| `Program.RenderGame` call site (main view) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4893-L4935) | Main-view order around the target |
| `Program.RenderEditor` call site | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5052-L5055) | Editor order |
| `IViewport.OffscreenTarget` -> `RenderTarget.ColorAttachment` / `DepthAttachment` / `Samples` | [current/decomp/KSA.Rendering/RenderTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderTarget.cs#L40-L57) | Attachments to render into |
| `CommandBufferEx.PipelineBarrier2(CommandBuffer, IRenderImage, ImageBarrierInfo, ...)` | [current/decomp/KSA/CommandBufferEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CommandBufferEx.cs#L48-L54) | Tracked-state image barrier |
| `ImageBarrierInfo.Presets.ColorAttachmentReadWrite`, `DepthStencilAttachmentReadWrite` | [current/decomp/KSA.Rendering/ImageBarrierInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ImageBarrierInfo.cs#L19-L27) | Layout, access and stage for the scope |
| `CommandBuffer.BeginRendering(in VkRenderingInfo)` / `EndRendering` | `Brutal.VulkanApi` | Dynamic rendering |

## Pitfalls and version notes

- Parameter order changed: in 5554 the target is `(IViewport viewport, CommandBuffer commandBuffer,
  bool useCustomRenderPass)` while the postfix declares `(CommandBuffer, bool, IViewport)`. Harmony
  injects by parameter name, so it still binds; keep the names identical to the original.
- A postfix cannot append draws to a scope the original already closed; open a new one with
  `LoadOp.Load` and the same attachments.
- Declare write access in the barrier you issue, or later passes that read the color image through
  KSA's tracked state may miss your writes.
- The target also runs for secondary viewports (`Program.RenderViewport`: the four optional
  500x500 camera windows and the two crew-portrait views, when visible) and in the vehicle editor
  (`Program.RenderEditor`). purrTTY draws every quad there too, always with the main camera's
  matrices, because `RecordDrawAll` does not filter by viewport; the upgrade skill notes `IsMain()`
  as the filter to add. Secondary targets are built with the same `GameSettings.GetSampleCount()`, so
  the pipeline stays compatible; what the quad looks like in those views is not verified in game.
- Since KSA 5482 bloom, underwater tint and the MSAA resolve run after this pass, so the quad is
  post-processed like glass; translucent particles are recorded right after it and draw over it.
- Older builds used classic render passes (`Program.OffScreenPass`, removed in rev 5154); pipelines
  must now be built for dynamic rendering (see [the pipeline](./world-space-quad-pipeline-custom-shader.md)).

## See also

- [World-space textured quad pipeline](./world-space-quad-pipeline-custom-shader.md)
- [Render-to-texture with a second ImGui context](./offscreen-imgui-context-render-to-texture.md)
- [Vulkan resource teardown discipline](./vulkan-resource-teardown-discipline.md)
- [Failure-isolated Harmony patching](./failure-isolated-harmony-patching.md)
- [KSA upgrade coupling review skill](./ksa-upgrade-coupling-review-skill.md)
- [purrTTY overview](../overview.md)
- [Premultiplied-alpha translucent quad after the translucency pass](../../../howto/rendering/textures-and-decals/draw-a-textured-quad-in-the-world/draw-a-textured-quad-in-the-world-3.md) - how-to article demonstrating this feature
- [Postfix SuperMeshRenderSystem.RenderTranslucencyPass with tracked barriers](../../../howto/rendering/vulkan-passes/insert-a-custom-pass-into-render-game/insert-a-custom-pass-into-render-game-3.md) - how-to article demonstrating this feature

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^gotchas]: purrTTY docs/gotchas.md
[^translucency-patch]: purrTTY.GameMod/InWorld/Patches/RenderTranslucencyPassPatch.cs
[^ksa-render-target]: KSA.Rendering.RenderTarget (decompiled, build 2026.10.10.5554)
[^ksa-cmdbuf-ex]: KSA.CommandBufferEx (decompiled, build 2026.10.10.5554)
[^ksa-barrier-info]: KSA.Rendering.ImageBarrierInfo (decompiled, build 2026.10.10.5554)
[^ksa-supermesh]: KSA.SuperMeshRenderSystem (decompiled, build 2026.10.10.5554)
[^inworld-manager]: purrTTY.GameMod/InWorld/InWorldTerminalManager.cs
