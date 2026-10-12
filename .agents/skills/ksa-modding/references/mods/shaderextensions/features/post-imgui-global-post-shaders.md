---
type: Mod Feature
title: Post-ImGui global post shaders on the swapchain image
description: <GlobalPostShader> passes run after the game's swapchain render pass ends, copying the finished swapchain image (scene plus ImGui) into a tracked render target, running the chain on it and copying the result back into the swapchain image before it is presented
mod: shaderextensions
category: shaders
techniques: [post-processing, harmony-transpiler, vulkan-texture, glsl-shader]
hooks: [harmony-transpiler KSA.Program.RenderGame]
lifecycle: [frame:render, render:post-process]
game_types:
  - KSA.Program
  - Core.Renderer
  - Brutal.VulkanApi.Abstractions.FrameResources
  - KSA.Rendering.RenderTarget
  - KSA.ScreenshotCapture
tags: [shaders, post-processing, swapchain, vulkan, imgui, harmony]
---

`<GlobalPostShader>` effects see the final frame exactly as the player would: the tonemapped scene
with every ImGui window, HUD element and menu on top. Typical uses are full-screen color grading,
film grain or a scanline effect that must also cover the UI. The asset is identical to
`<PostProcessingShader>` apart from the element name and the insertion point.[^sx-readme] [^sx-postasset]

## How it works

1. **Insertion point.** The [RenderGame transpiler](rendergame-render-pass-transpiler.md) inserts
   `GlobalPostShaderHandler.RenderNow(commandBuffer2, frameResources, 0)` right after the last
   `EndRenderPass` in `RenderGame`, the one that closes the swapchain pass. It loads the two values
   with `ldloc.1` and `ldloc.0`; in the build 5554 IL, local 0 is the `FrameResources` and local 1 the
   `CommandBuffer`
   ([ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L168-L191)).[^sx-patches] [^ksa-dll]
2. **Source target.** `Rebuild` keeps a `RenderTarget` called `ShaderExtensions_PostImguiSource` in
   the swapchain's surface format (`Renderer.ColorFormat`) and `Renderer.Extent`, recreated when the
   extent changes, and builds the chain on it
   ([ShaderExtensions/PostProcessing/PostImgui/GlobalPostShaderHandler.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostImgui/GlobalPostShaderHandler.cs#L20-L49)).[^sx-posthandler]
3. **Copy in, run, copy out.** `RenderNow` returns when the chain is empty. Otherwise it copies the
   swapchain image (`FrameResources.ColorImage`, a raw `VkImage` the game does not state-track) into
   the source target, renders the chain, and copies the result back. For the raw image it writes its
   own `VkImageMemoryBarrier2`s: `PresentSrcKHR` to `TransferSrcOptimal` and back for the read,
   `PresentSrcKHR` to `TransferDstOptimal` and back for the write, all with `AllCommands` stages
   ([ShaderExtensions/PostProcessing/PostImgui/GlobalPostShaderHandler.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostImgui/GlobalPostShaderHandler.cs#L54-L71),
   [ShaderExtensions/PostProcessing/PostProcessingChain.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingChain.cs#L131-L244)).[^sx-chain]
4. **Why `PresentSrcKHR`.** The game's main render pass ends with the color attachment in
   `PresentSrcKHR` ([current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L787-L818)), so that
   is the layout the image is in when `RenderNow` runs and the layout presentation expects
   afterwards.[^core-renderer]

## Lifecycle and timing

- **`frame:render`, after the ImGui draw.** Order at the tail of `Program.RenderGame` in build 5554:
  `BeginRenderPass` (line 4948), `RenderFinalComposite` (4949), `ImGuiBackend.Vulkan.RenderDrawData`
  (4950), `EndRenderPass` (4951), **`GlobalPostShaderHandler.RenderNow` (inserted)**,
  `_screenshotCapture.OnRenderGameSwapchainGrab` (4952), `Profiler.Gpu.EndFrame`, `End`
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4945-L4955)).[^ksa-program]
- **Submission:** `Program.PostRender` (`frame:post-render`) submits the frame with
  `Renderer.TrySubmitFrame`; when ImGui multi-viewports are enabled it then renders the platform
  windows with `ImGui.RenderPlatformWindowsDefault`, outside this hook, so those OS windows are not
  post-processed
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2336-L2358)).

## Key code

| Path | Role |
|---|---|
| [ShaderExtensions/PostProcessing/PostImgui/GlobalPostShaderHandler.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostImgui/GlobalPostShaderHandler.cs#L13-L79) | Source target, rebuild, copy in, chain, copy out |
| [ShaderExtensions/PostProcessing/PostProcessingChain.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingChain.cs#L131-L189) | `CopyToRaw`, `CopyFromRaw` for untracked swapchain images |
| [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L168-L191) | IL insertion after `EndRenderPass` |
| [ShaderExtensions/PostProcessing/PostImgui/GlobalPostShaderAsset.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostImgui/GlobalPostShaderAsset.cs#L8-L60) | `<GlobalPostShader>` asset |

## Snippets

```csharp
// ShaderExtensions/PostProcessing/PostImgui/GlobalPostShaderHandler.cs L51-L71
        /// <summary>
        /// Called after the UI renderpass has finished.
        /// </summary>
        public static void RenderNow(CommandBuffer commandBuffer, FrameResources destFrameResources, int dynamicOffset = 0)
        {
            if (Chain.PassCount == 0) return;

            PostProcessingChain.CopyFromRaw(
                commandBuffer,
                destFrameResources.ColorImage,
                VkImageLayout.PresentSrcKHR,
                SourceTarget.ColorImage);

            Chain.Render(commandBuffer);

            PostProcessingChain.CopyToRaw(
                commandBuffer,
                Chain.Output,
                destFrameResources.ColorImage,
                VkImageLayout.PresentSrcKHR);
        }
```

```csharp
// ShaderExtensions/PostProcessing/PostProcessingChain.cs L131-L158
        public static unsafe void CopyToRaw(
            CommandBuffer commandBuffer,
            RenderImage source,
            VkImage destination,
            VkImageLayout destinationLayout)
        {
            commandBuffer.PipelineBarrier2(source, ImageBarrierInfo.Presets.TransferSrc);

            VkImageSubresourceRange range = new()
            {
                AspectMask = VkImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            };

            Transition(commandBuffer, destination, range, destinationLayout, VkImageLayout.TransferDstOptimal);

            commandBuffer.CopyImage(
                srcImage: source.Image,
                srcImageLayout: VkImageLayout.TransferSrcOptimal,
                dstImage: destination,
                dstImageLayout: VkImageLayout.TransferDstOptimal,
                pRegions: [MakeRegion(source)]);

            Transition(commandBuffer, destination, range, VkImageLayout.TransferDstOptimal, destinationLayout);
        }
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `FrameResources.ColorImage` | [current/decomp/Brutal.VulkanApi.Abstractions/FrameResources.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/FrameResources.cs#L5-L13) | The acquired swapchain image |
| `Renderer.ColorFormat`, `Renderer.Extent`, `Renderer.SwapchainExtent` | [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L96-L102) | Source target format and size |
| Swapchain `ImageUsage` | [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L715) | `TransferSrcBit` and `ColorAttachmentBit` only |
| Main render pass `FinalLayout = PresentSrcKHR` | [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L787-L818) | Layout the copies start and end in |
| `ScreenshotCapture.OnRenderGameSwapchainGrab` | [current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L334-L359) | Swapchain screenshot, recorded after the inserted call |

## Pitfalls and version notes

- **The swapchain is not a valid copy destination.** Build 5554 creates swapchain images with
  `TransferSrcBit | ColorAttachmentBit` only
  ([current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L715)). `vkCmdCopyImage` requires
  `TRANSFER_DST` usage on the destination, so the copy back is outside Vulkan valid usage and
  validation layers report it; it depends on the driver ignoring the usage flags. Rendering the last
  pass directly into the swapchain image as a color attachment would avoid it.
- **High-resolution screenshots.** For a capture scale above 1, `ScreenshotCapture` sets
  `Renderer.ExtentOverride` and calls `RebuildRenderer(skipSwapchain: true)`
  ([current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L234-L240)).
  The postfix then sizes the source target and chain to `Renderer.Extent` (the override) while the
  swapchain keeps `SwapchainExtent`, so both copy regions exceed the swapchain image. Found by
  reading the code, not reproduced.[^ksa-screenshotcapture]
- **Swapchain-grab screenshots include the effect,** because the grab is recorded after the inserted
  call.
- **Display-referred input.** The chain runs in the surface format, after tonemapping, so values are
  LDR and the UI is part of the image.
- **Fragile anchor.** The insertion point is "the last call named `EndRenderPass`" and the operands
  are fixed local indices; the compatibility plan calls these the main risk on game updates and the
  unused `dynamicOffset` argument a placeholder to remove.[^sx-compatplan]
- **Not in the editor or secondary viewports,** same as the pre-ImGui stage.

## See also

- [RenderGame transpiler around the swapchain render pass](rendergame-render-pass-transpiler.md)
- [RenderPassId-ordered fullscreen pass chain](post-processing-pass-chain.md)
- [Pre-ImGui post-processing on the HDR scene image](pre-imgui-post-processing.md)
- [Cross-pass image references](cross-pass-image-references.md)
- [Transpile Program.RenderGame around the swapchain render pass](../../../howto/rendering/vulkan-passes/insert-a-custom-pass-into-render-game/insert-a-custom-pass-into-render-game-5.md) - how-to article demonstrating this feature
- [Choose the stage: a post-ImGui chain on the presented image via swapchain copies](../../../howto/shaders/post-processing/choose-pre-or-post-imgui-post-processing/choose-pre-or-post-imgui-post-processing-2.md) - how-to article demonstrating this feature

[^sx-readme]: ShaderExtensions/README.md
[^sx-postasset]: ShaderExtensions/PostProcessing/PostImgui/GlobalPostShaderAsset.cs
[^sx-patches]: ShaderExtensions/Patches.cs
[^ksa-dll]: KSA.dll IL (build 2026.10.10.5554, disassembled with ilspycmd)
[^sx-posthandler]: ShaderExtensions/PostProcessing/PostImgui/GlobalPostShaderHandler.cs
[^sx-chain]: ShaderExtensions/PostProcessing/PostProcessingChain.cs
[^core-renderer]: Core.Renderer (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-screenshotcapture]: KSA.ScreenshotCapture (decompiled, build 2026.10.10.5554)
[^sx-compatplan]: ShaderExtensions/KSA-2026.9.7-compatibility-plan.md
