---
type: How-To
title: "Choose the stage: a post-ImGui chain on the presented image via swapchain copies"
description: Run an effect after the UI so it covers the whole frame in display colour, and understand the copies into and out of the swapchain image that make it work
category: shaders
group: post-processing
topic: choose-pre-or-post-imgui-post-processing
approach: 2
difficulty: intermediate
prerequisites: [starmap, shaderextensions, kittenextensions]
demonstrated_by:
  - mods/shaderextensions/features/post-imgui-global-post-shaders
  - mods/shaderextensions/features/cross-pass-image-references
techniques: [post-processing, xml-asset]
hooks: [xml-asset GlobalPostShader, harmony-transpiler KSA.Program.RenderGame (ShaderExtensions)]
lifecycle: [frame:render, render:post-process, render:imgui]
game_types: [KSA.Program, Core.Renderer, KSA.ScreenshotCapture]
tags: [post-processing, post-imgui, swapchain, present-src, film-grain, crt, shaderextensions]
---
Use a `GlobalPostShader` when the effect must cover everything the player sees, including ImGui windows and
the HUD: film grain, a scanline or CRT look, a screen-wide vignette. The input is the tonemapped,
display-referred frame, not HDR. This article shows where the chain runs, how the swapchain image gets
in and out of it, and the Vulkan caveats of build 2026.10.10.5554. MPFX declares the post half of every
effect this way.

## Background: how the game does it

`RenderGame` ends its swapchain render pass with `EndRenderPass`, leaving the colour attachment in
`PresentSrcKHR` (the main pass's final layout, [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L787-L818)). The
swapchain images are created with `TransferSrcBit | ColorAttachmentBit` only
([current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L715)) and the game does not state-track them, so a mod cannot
sample or write them with the game's image-barrier helper. ShaderExtensions therefore keeps its own render
target, copies the swapchain image into it, runs the chain and copies the result back.

## Lifecycle and timing

`frame:render`, immediately after `EndRenderPass` and before `OnRenderGameSwapchainGrab`, so swapchain
screenshots include the effect. Later in the frame `Program.PostRender` submits and presents;
ImGui platform windows are drawn after that and are not processed. The insertion is the last call named
`EndRenderPass` in `RenderGame`, with the `FrameResources` (local 0) and `CommandBuffer` (local 1)
passed by fixed local index. See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

1. **Declare the pass as a `GlobalPostShader`.** Same shader contract as the pre stage:

   ```xml
   <!-- MPFX/MPFXAssests.xml L27-L29 -->
   	<GlobalPostShader Id="MPFXBrightnessShaderPost" Path="Shaders/BrightnessPost.frag" RenderPassId="131">
   		<MPFXDefaultBufferAsset Id="MPFXBrightnessBuffer" />
   	</GlobalPostShader>
   ```

   Source: [MPFX/MPFXAssests.xml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXAssests.xml#L27-L29). The buffer is created by the pre half; here it is referenced by `Id`.

2. **Know what the chain does around your shader.** `RenderNow` copies the swapchain image into a
   `ShaderExtensions_PostImguiSource` target (the surface format and `Renderer.Extent`), runs the chain, and
   copies the final image back, transitioning the untracked swapchain image itself:

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

   Source: [ShaderExtensions/PostProcessing/PostImgui/GlobalPostShaderHandler.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostImgui/GlobalPostShaderHandler.cs#L51-L71). `CopyToRaw` and
   `CopyFromRaw` ([ShaderExtensions/PostProcessing/PostProcessingChain.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingChain.cs#L131-L189)) transition
   `PresentSrcKHR` to a transfer layout and back around each `vkCmdCopyImage`.

3. **Write the shader for LDR input.** Values are 0..1 display colour with the UI burned in. A grain or
   curvature shader that must not touch the HUD does not belong here.

4. **Reach the scene without the UI when you need it.** A post-ImGui shader can reference a pre-ImGui pass
   image with `<PostProcessingInputReference RenderPassId="N" PreImgui="true" />`; the reference rules
   are in [Declare ShaderEx, PostProcessingShader, GlobalPostShader and ImGuiShader assets](../declare-post-processing-shader-assets-in-xml/overview.md). The first pre-ImGui pass's input is the
   offscreen image that the pre chain overwrites, so it holds the processed scene, not the original.

## Complete example

MPFX's post halves: [MPFX/MPFXAssests.xml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXAssests.xml). The mechanism: [ShaderExtensions/PostProcessing/PostImgui/GlobalPostShaderHandler.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostImgui/GlobalPostShaderHandler.cs),
[ShaderExtensions/PostProcessing/PostProcessingChain.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingChain.cs) and [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L168-L191).

## Variations and gotchas

- **The swapchain is not a valid copy destination.** `vkCmdCopyImage` requires `TRANSFER_DST` usage on the
  destination, which build 5554 does not set, so the copy back is outside Vulkan valid usage and relies on the
  driver ignoring the flag. Rendering the last pass directly into the swapchain image would avoid it. Not
  reproduced at runtime.
- **High-resolution screenshots.** For a capture scale above 1 `ScreenshotCapture` sets
  `Renderer.ExtentOverride`, so the chain is sized to the override while the swapchain keeps
  `SwapchainExtent`; both copy regions then exceed the swapchain image. Found by reading the code
  ([current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L234-L240)).
- **Fragile anchor.** The transpiler looks for the last `EndRenderPass` call and uses fixed local indices;
  a game update that reorders `RenderGame` locals would insert wrong arguments, which the author's own plan lists
  as the main risk.
- **Not in the editor or secondary viewports,** and ImGui platform windows are not processed.
- **Display-referred only.** Anything that needs linear HDR belongs in the pre-ImGui stage.

## See also

- Sibling: [pre-ImGui chain](choose-pre-or-post-imgui-post-processing-1.md); [overview](overview.md)
- [Post-ImGui global post shaders on the swapchain image](../../../../mods/shaderextensions/features/post-imgui-global-post-shaders.md), [Cross-pass image references](../../../../mods/shaderextensions/features/cross-pass-image-references.md)
- [Add film grain and dither away banding](../../screen-effects/add-film-grain-and-blue-noise-dithering/overview.md), [Bend the picture with CRT curvature and chromatic aberration](../../screen-effects/bend-the-picture-with-crt-curvature-and-chromatic-aberration/overview.md), [Capture rendered frames from KSA](../../../rendering/vulkan-passes/capture-rendered-frames/overview.md)
