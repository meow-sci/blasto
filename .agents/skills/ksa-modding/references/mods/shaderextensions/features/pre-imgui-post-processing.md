---
type: Mod Feature
title: Pre-ImGui post-processing on the HDR scene image
description: <PostProcessingShader> passes run on the game's HDR offscreen color image immediately before the swapchain render pass that tonemaps the scene and draws ImGui, then copy their result back into the offscreen image so the game's final composite reads it
mod: shaderextensions
category: shaders
techniques: [post-processing, harmony-transpiler, reflection, vulkan-texture, glsl-shader]
hooks: [harmony-transpiler KSA.Program.RenderGame, reflection KSA.Program._offscreenTarget]
lifecycle: [frame:render, render:post-process]
game_types:
  - KSA.Program
  - KSA.Rendering.RenderTarget
  - KSA.Rendering.PostProcessing.Cmaa2Renderer
  - KSA.ScreenspaceRenderer
  - KSA.ScreenshotCapture
  - KSA.CommandBufferEx
tags: [shaders, post-processing, hdr, vulkan, render-order, harmony]
---

`<PostProcessingShader>` effects see the 3D scene only: planets, vessels, atmosphere, bloom, orbit
lines and gizmos, but no ImGui. They run in linear HDR, before the game's tonemapping composite, so
a shader that wants display-referred colors must do its own tonemapping math or move to the
post-ImGui stage. The XML and GLSL are identical to `<GlobalPostShader>`; only the insertion point
and the source image differ.[^sx-readme] [^sx-preasset]

## How it works

1. **Source image.** `PostProcessingHandler` reads the private `Program._offscreenTarget` field by
   reflection on the singleton `Program.Instance` and uses its `ColorImage` (the resolved,
   single-sample image when MSAA is on) as the chain source
   ([ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs#L16-L33)).
   The target is created with `Program.ColorFormat = R16G16B16A16SFloat`, so the chain targets are
   16-bit float too
   ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L223),
   [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1506-L1525)).[^sx-prehandler] [^ksa-program]
2. **Insertion point.** The [RenderGame transpiler](rendergame-render-pass-transpiler.md) replaces the
   `BeginRenderPass` call that opens the swapchain pass with `Patches.ImGuiPreRender`, which records the
   ImGui window shaders, then `PostProcessingHandler.RenderNow`, then performs the original
   `BeginRenderPass`
   ([ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L196-L225)).[^sx-patches]
3. **Run and copy back.** `RenderNow` returns at once when the chain is empty; otherwise it renders the
   chain and copies the last pass's image into the offscreen color image with `vkCmdCopyImage`
   (`TransferSrc` and `TransferDst` barriers through the game's state tracker)
   ([ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs#L38-L46),
   [ShaderExtensions/PostProcessing/PostProcessingChain.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingChain.cs#L112-L125)).[^sx-chain]
4. **The game composites it.** Inside the swapchain pass, `RenderFinalComposite` runs the tonemap
   composite, whose descriptor was built on `_offscreenTarget.ColorImage.ImageView` in
   `RebuildRenderer`, so it reads the processed image
   ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4633-L4644),
   [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5138-L5141)).

## Lifecycle and timing

`frame:render`, inside `Program.RenderGame` command recording. The tail of `RenderGame` in build
2026.10.10.5554, with the hook marked:

| Line | Call | Phase |
|---|---|---|
| 4927-4936 | `OrbitLinePass.Run`, `GizmoPass.Run`, `GridPass.Run` | drawn into the offscreen target (visible to pre-ImGui shaders) |
| 4937-4944 | `GaugeCanvas.CanvasesRender`, `ImGauge.PrepareRender` | gauge rendering preparation |
| 4945 | `PipelineBarrier2(_offscreenTarget.ColorImage, SampledReadVfc)` | offscreen image ready for sampling |
| 4946 | `_screenshotCapture.OnRenderGameCapture` | composite-mode screenshot (before the hook) |
| 4947 | `RenderAaPreparePasses` | CMAA2 tonemap and edge passes (before the hook) |
| 4948 | `BeginRenderPass(_mainPass)` | **replaced: ImGui window shaders, then the pre-ImGui chain, then the original call** |
| 4949 | `RenderFinalComposite` | tonemap composite or CMAA2 blit into the swapchain |
| 4950 | `ImGuiBackend.Vulkan.RenderDrawData` | ImGui |
| 4951 | `EndRenderPass` | (post-ImGui chain inserted after this) |

The game has no generic post-processing chain; its own screen-space work is the bloom renderers, the
CMAA2 passes and the final composite. The final composite precedes ImGui inside the same render
pass, which is why ShaderExtensions cannot put a pass between them and uses the slot just before
the pass instead. Everything here is command recording on the main thread; the GPU executes it after
`PostRender` submits.

## Key code

| Path | Role |
|---|---|
| [ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs#L12-L49) | Source lookup, rebuild, render and copy back |
| [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L196-L225) | `ImGuiPreRender`, the code that runs in place of `BeginRenderPass` |
| [ShaderExtensions/PostProcessing/PreImgui/PostProcessingShaderAsset.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PreImgui/PostProcessingShaderAsset.cs#L8-L60) | `<PostProcessingShader>` asset |

## Snippets

```csharp
// ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs L14-L46
        private static readonly PostProcessingChain Chain = new(preImgui: true);

        private static readonly FieldInfo OffscreenTargetField =
            typeof(Program).GetField("_offscreenTarget", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static RenderTarget GameOffscreenTarget =>
            (RenderTarget)OffscreenTargetField.GetValue(Program.Instance);

        public static void Rebuild()
        {
            Program.GetRenderer().Device.WaitIdle();

            RenderTarget offscreenTarget = GameOffscreenTarget;
            if (offscreenTarget is null) return;

            Chain.Build(
                "ShaderExtensions_PreImgui",
                PostProcessingChain.FilterSupported(PostProcessingShaderAsset.AllShaders),
                offscreenTarget.ColorImage);
        }

        /// <summary>
        /// Called before the UI renderpass.
        /// </summary>
        public static void RenderNow(CommandBuffer commandBuffer)
        {
            if (Chain.PassCount == 0) return;

            Chain.Render(commandBuffer);

            // Feed the result back into the image the game composites from.
            PostProcessingChain.CopyTo(commandBuffer, Chain.Output, GameOffscreenTarget.ColorImage);
        }
```

```csharp
// current/decomp/KSA/Program.cs L4945-L4954
		commandBuffer2.PipelineBarrier2(_offscreenTarget.ColorImage, ImageBarrierInfo.Presets.SampledReadVfc);
		_screenshotCapture.OnRenderGameCapture(commandBuffer2, resourceFrameIndex, in _compositePushConstantData);
		RenderAaPreparePasses(commandBuffer2, shaderSlot, resourceFrameIndex);
		commandBuffer2.BeginRenderPass(in pRenderPassBegin, contents);
		RenderFinalComposite(commandBuffer2, shaderSlot, resourceFrameIndex);
		ImGuiBackend.Vulkan.RenderDrawData(commandBuffer2);
		commandBuffer2.EndRenderPass();
		_screenshotCapture.OnRenderGameSwapchainGrab(commandBuffer2, frameResources.ColorImage);
		Profiler.Gpu.EndFrame(commandBuffer2);
		commandBuffer2.End();
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `Program._offscreenTarget` (private; public static `Program.OffscreenTarget` also exists) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L131) | Chain source and copy-back destination |
| `Program.ColorFormat` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L223) | HDR format of the scene image |
| `Program.RenderFinalComposite` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4633-L4644) | Consumer of the processed image |
| `Cmaa2Renderer.RenderPreparePasses`, `RenderFinalBlit` | [current/decomp/KSA.Rendering.PostProcessing/Cmaa2Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.PostProcessing/Cmaa2Renderer.cs#L275-L365) | Anti-aliasing path that bypasses the offscreen image |
| `ScreenspaceRenderer.Render` | [current/decomp/KSA/ScreenspaceRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenspaceRenderer.cs#L204-L207) | Composite draw; issues no barrier |
| `ScreenshotCapture.OnRenderGameCapture` | [current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L272-L297) | Composite-mode screenshot recorded before the hook |

## Pitfalls and version notes

- **Invisible with CMAA2 anti-aliasing.** When CMAA2 is enabled, `RenderAaPreparePasses` (line 4947,
  before the hook) already tonemaps the offscreen image into the renderer's own `_sceneLdrTarget`, and
  `RenderFinalComposite` then blits `_sceneLdrTarget` instead of reading the offscreen image
  ([current/decomp/KSA.Rendering.PostProcessing/Cmaa2Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.PostProcessing/Cmaa2Renderer.cs#L166-L177)).
  Reading the decomp, the pre-ImGui result is therefore never displayed with CMAA2 on, although the
  passes still run. Not confirmed in game.[^ksa-cmaa2]
- **Layout after the copy-back.** The game moved the offscreen image to `SampledReadVfc` before the
  hook; the copy-back leaves it in `TRANSFER_DST_OPTIMAL`, and the composite samples it through a
  descriptor declared `ShaderReadOnlyOptimal` without any barrier in between
  ([current/decomp/KSA/ScreenspaceRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenspaceRenderer.cs#L204-L207)).
  That is a layout mismatch validation layers report; it relies on the driver tolerating it. Adding a
  `PipelineBarrier2(GameOffscreenTarget.ColorImage, ImageBarrierInfo.Presets.SampledReadF)` after the
  copy would fix it.[^ksa-screenspacerenderer] [^ksa-commandbufferex]
- **HDR values.** Inputs are linear and unbounded; writing values outside the range the composite
  expects changes exposure and bloom-like highlights.
- **Composite-mode screenshots miss the effect** because `OnRenderGameCapture` records first
  ([current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L272-L297)).[^ksa-screenshotcapture]
- **Main window, flight scene only.** Secondary viewports are composited in `RenderViewport`
  (lines 4612-4624) and the vehicle editor renders through `RenderEditor`; neither is patched.
- **Extra copy per frame.** A full-resolution RGBA16F `vkCmdCopyImage` every frame when any shader is
  installed.
- **Reflection instead of the public property.** Build 5554 exposes `Program.OffscreenTarget`
  (line 456); the private field read keeps working only while the field name is unchanged.

## See also

- [RenderGame transpiler around the swapchain render pass](rendergame-render-pass-transpiler.md)
- [RenderPassId-ordered fullscreen pass chain](post-processing-pass-chain.md)
- [Post-ImGui global post shaders on the swapchain image](post-imgui-global-post-shaders.md)
- [Per-window ImGui post shader](imgui-window-post-shader.md)
- [Transpile Program.RenderGame around the swapchain render pass](../../../howto/rendering/vulkan-passes/insert-a-custom-pass-into-render-game/insert-a-custom-pass-into-render-game-5.md) - how-to article demonstrating this feature
- [Choose the stage: a pre-ImGui chain on the RGBA16F scene, copied back before the tonemap](../../../howto/shaders/post-processing/choose-pre-or-post-imgui-post-processing/choose-pre-or-post-imgui-post-processing-1.md) - how-to article demonstrating this feature

[^sx-readme]: ShaderExtensions/README.md
[^sx-preasset]: ShaderExtensions/PostProcessing/PreImgui/PostProcessingShaderAsset.cs
[^sx-prehandler]: ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^sx-patches]: ShaderExtensions/Patches.cs
[^sx-chain]: ShaderExtensions/PostProcessing/PostProcessingChain.cs
[^ksa-cmaa2]: KSA.Rendering.PostProcessing.Cmaa2Renderer (decompiled, build 2026.10.10.5554)
[^ksa-screenspacerenderer]: KSA.ScreenspaceRenderer (decompiled, build 2026.10.10.5554)
[^ksa-commandbufferex]: KSA.CommandBufferEx (decompiled, build 2026.10.10.5554)
[^ksa-screenshotcapture]: KSA.ScreenshotCapture (decompiled, build 2026.10.10.5554)
