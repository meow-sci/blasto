---
type: Game Feature
title: Choose between pre-ImGui (HDR scene) and post-ImGui (final frame) post-processing
description: What a PostProcessingShader and a GlobalPostShader each see, where they run relative to the tonemap composite, CMAA2 and the UI, and how to pick the stage for an effect
category: shaders
group: post-processing
topic: choose-pre-or-post-imgui-post-processing
demonstrated_by:
  - mods/shaderextensions/features/pre-imgui-post-processing
  - mods/mpfx/features/effect-chain-assets
  - mods/shaderextensions/features/post-imgui-global-post-shaders
  - mods/shaderextensions/features/cross-pass-image-references
lifecycle: [frame:render, render:post-process, render:aa, render:imgui]
game_types:
  - KSA.Program
  - KSA.Rendering.RenderTarget
  - KSA.Rendering.PostProcessing.Cmaa2Renderer
  - KSA.ScreenshotCapture
  - Core.Renderer
tags: [post-processing, hdr, tonemap, imgui, cmaa2, swapchain, shaderextensions]
---
ShaderExtensions gives a mod two places to run a fullscreen pass, and they see very different images.
A `PostProcessingShader` works on the linear HDR scene before the game tonemaps it; a
`GlobalPostShader` works on the finished swapchain image with the UI already drawn. The element name
is the only thing you change in the XML, but the choice decides colour range, whether the HUD is
affected, whether the effect is visible with CMAA2, and which image layout the shader inherits.

## Engine background

The tail of `Program.RenderGame` in build 2026.10.10.5554, with both mod insertion points:

| Line | Call | What it means |
|---|---|---|
| 4945 | `PipelineBarrier2(_offscreenTarget.ColorImage, SampledReadVfc)` | The offscreen scene (RGBA16F) is ready for sampling |
| 4946 | `_screenshotCapture.OnRenderGameCapture` | Composite-mode screenshot is recorded here, before any mod pass |
| 4947 | `RenderAaPreparePasses` | CMAA2 tonemaps the offscreen image into its own LDR target and runs edge passes |
| 4948 | `BeginRenderPass` on the swapchain pass | **Pre-ImGui chain runs in place of this call** |
| 4949 | `RenderFinalComposite` | Tonemap composite, or the CMAA2 final blit |
| 4950 | `ImGuiBackend.Vulkan.RenderDrawData` | The UI is drawn |
| 4951 | `EndRenderPass` | **Post-ImGui chain runs right after** |
| 4952 | `_screenshotCapture.OnRenderGameSwapchainGrab` | Swapchain-grab screenshot, after the post chain |

Source: [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4945-L4954). See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence) for the full sequence.

- **Offscreen format.** The scene image is `R16G16B16A16SFloat` (`Program.ColorFormat`,
  [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L223)), so pre-ImGui values are linear and can exceed 1.0.
- **Swapchain image.** The main render pass ends with the colour attachment in `PresentSrcKHR`, and the
  swapchain images are created with `TransferSrcBit | ColorAttachmentBit` only
  ([current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L715)).
- **CMAA2.** With CMAA2 enabled, `Cmaa2Renderer` builds a `ScreenspaceRenderer` that tonemaps
  `offscreenTarget.ColorImage` into `_sceneLdrTarget`, and its final blit reads that target
  ([current/decomp/KSA.Rendering.PostProcessing/Cmaa2Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.PostProcessing/Cmaa2Renderer.cs#L164-L177)).
  `RenderFinalComposite` chooses the blit when CMAA2 is enabled
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4633-L4644)).

## Choosing

| Question | Pre-ImGui (`PostProcessingShader`) | Post-ImGui (`GlobalPostShader`) |
|---|---|---|
| Colour range | Linear HDR, values above 1.0 survive | Display-referred, already tonemapped |
| Does it affect the UI | No, ImGui is drawn on top afterwards | Yes, the whole frame including windows |
| CMAA2 selected | Result is not displayed (read from the code, not confirmed in game) | Works |
| MSAA (default) or no AA | Works | Works |
| Composite-mode screenshot | Not captured (recorded earlier) | Not captured (recorded earlier) |
| Swapchain-grab screenshot | Captured | Captured |
| Typical effects | Colour grading, bloom-like, blur, glass | Film grain, CRT curvature, UI-covering vignette |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Pre-ImGui chain on the RGBA16F scene](choose-pre-or-post-imgui-post-processing-1.md) | ShaderExtensions, MPFX | `post-processing` | `frame:render`, `render:post-process`, `render:aa` | HDR input, UI untouched; invisible with CMAA2 and leaves the offscreen image in a transfer layout |
| [Post-ImGui chain on the presented image](choose-pre-or-post-imgui-post-processing-2.md) | ShaderExtensions | `post-processing` | `frame:render`, `render:post-process`, `render:imgui` | Covers the UI; needs copies in and out of the swapchain, which lacks `TRANSFER_DST` usage in 5554 |

## See also

- [Pre-ImGui post-processing on the HDR scene image](../../../../mods/shaderextensions/features/pre-imgui-post-processing.md), [Post-ImGui global post shaders on the swapchain image](../../../../mods/shaderextensions/features/post-imgui-global-post-shaders.md), [RenderGame transpiler around the swapchain render pass](../../../../mods/shaderextensions/features/rendergame-render-pass-transpiler.md)
- [Write a fullscreen post-processing fragment shader for KSA](../write-a-post-processing-fragment-shader/overview.md), [Declare ShaderEx, PostProcessingShader, GlobalPostShader and ImGuiShader assets](../declare-post-processing-shader-assets-in-xml/overview.md), [Insert a custom Vulkan pass into KSA's frame](../../../rendering/vulkan-passes/insert-a-custom-pass-into-render-game/overview.md), [Capture rendered frames from KSA](../../../rendering/vulkan-passes/capture-rendered-frames/overview.md), [Refract and distort the rendered scene, and flash or shake it](../../screen-effects/refract-and-distort-the-scene/overview.md)
