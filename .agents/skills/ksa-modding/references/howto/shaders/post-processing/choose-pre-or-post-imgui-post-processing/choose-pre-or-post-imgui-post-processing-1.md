---
type: How-To
title: "Choose the stage: a pre-ImGui chain on the RGBA16F scene, copied back before the tonemap"
description: Put an effect before the UI so it works on the linear HDR scene, and know why it vanishes with CMAA2 and what layout the offscreen image is left in
category: shaders
group: post-processing
topic: choose-pre-or-post-imgui-post-processing
approach: 1
difficulty: intermediate
prerequisites: [starmap, shaderextensions, kittenextensions]
demonstrated_by: [mods/shaderextensions/features/pre-imgui-post-processing, mods/mpfx/features/effect-chain-assets]
techniques: [post-processing, xml-asset, reflection]
hooks:
  - xml-asset PostProcessingShader
  - harmony-transpiler KSA.Program.RenderGame (ShaderExtensions)
  - reflection KSA.Program._offscreenTarget (ShaderExtensions)
lifecycle: [frame:render, render:post-process, render:aa]
game_types:
  - KSA.Program
  - KSA.Rendering.RenderTarget
  - KSA.Rendering.PostProcessing.Cmaa2Renderer
  - KSA.ScreenshotCapture
tags: [post-processing, hdr, pre-imgui, tonemap, cmaa2, offscreen-target, layout]
---
Use a `PostProcessingShader` when the effect should act on the 3D scene only: colour grading,
blur, glass, anything that should leave the HUD crisp or that benefits from HDR values above 1.0. This
article explains where the pre-ImGui chain runs, why its result reaches the screen, and the two ways it
can fail to: CMAA2 anti-aliasing and a layout mismatch. MPFX declares the pre half of every effect this way.

## Background: how the game does it

The game renders the scene into `_offscreenTarget` (RGBA16F). Inside the swapchain render pass,
`RenderFinalComposite` runs the tonemap composite, whose descriptor was built on
`_offscreenTarget.ColorImage.ImageView` in `RebuildRenderer`; then ImGui is drawn. Nothing between
the scene and the composite is customisable in the base game, so ShaderExtensions swaps the
`BeginRenderPass` call for its own method, which runs the chain first and then performs the original call.
[ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L151-L225) is the transpiler and the replacement method. The
tail of `RenderGame` is tabulated in the topic overview and in the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Lifecycle and timing

`frame:render`, main thread, inside `Program.RenderGame`. The pre-ImGui chain runs after the scene, bloom,
orbit lines, gizmos and the CMAA2 prepare passes (`render:aa`) have been recorded and before the swapchain
render pass begins. A full-resolution RGBA16F copy is recorded every frame when any pre-ImGui shader is
installed. Parameters must be written before `RenderGame` starts recording, which is why mods write them from
`StarMapAfterGui` or the `OnFrameEditor` postfix; see [Send per-frame values and time to a shader with push constants, written at the right point in the frame](../send-push-constants-to-a-shaderextensions-shader/overview.md).

## Steps

1. **Declare the pass as a `PostProcessingShader`.** Nothing else differs from a post-ImGui pass:

   ```xml
   <!-- MPFX/MPFXAssests.xml L24-L26 -->
   	<PostProcessingShader Id="MPFXBrightnessShaderPre" Path="Shaders/BrightnessPre.frag" RenderPassId="131">
   		<MPFXDefaultBufferAsset Id="MPFXBrightnessBuffer" Size="1" />
   	</PostProcessingShader>
   ```

   Source: [MPFX/MPFXAssests.xml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXAssests.xml#L24-L26).

2. **Understand the source image.** `PostProcessingHandler` reads the private `Program._offscreenTarget` by
   reflection and builds the chain on its `ColorImage`; after the last pass it copies the result back
   into the same image, so the game's composite tonemaps your result:

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

   Source: [ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs#L14-L46). The private field read keeps
   working only while the field name is unchanged (build 5554 also exposes a public
   `Program.OffscreenTarget`).

3. **Write the shader for HDR input.** Pre-ImGui inputs are linear and unbounded. Decide for every
   operation whether to clamp; MPFX's HSV passes clamp to [0, 1] on the pre side only, and contrast
   pivots on mid-grey 0.18 rather than 0.5. One MPFX film-grain shader clamps unconditionally before it
   checks whether grain is enabled ([MPFX/Shaders/FilmGrainPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/FilmGrainPre.frag#L250-L256)); because every pass always runs,
   that caps everything after pass 136 at 1 whether grain is on or not (derived from the code, not tested).

4. **Test with MSAA, not CMAA2.** With CMAA2 selected, `RenderAaPreparePasses` (line 4947) has already
   tonemapped the offscreen image into CMAA2's LDR target and the final blit reads that target, so the
   chain still runs but its result is not what is displayed
   ([current/decomp/KSA.Rendering.PostProcessing/Cmaa2Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.PostProcessing/Cmaa2Renderer.cs#L164-L177)). The default is MSAA
   ([current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L1303)), which takes the normal composite path. Tell
   players to avoid CMAA2 or move the effect to the post-ImGui stage.

## Complete example

MPFX's pre-ImGui halves: [MPFX/MPFXAssests.xml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXAssests.xml). The mechanism: [ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs)
and [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs).

## Variations and gotchas

- **Layout after the copy-back.** The game moved the offscreen image to `SampledReadVfc` before the hook;
  the copy-back leaves it in `TRANSFER_DST_OPTIMAL`, and the composite samples it through a descriptor
  declared `ShaderReadOnlyOptimal` with no barrier between
  ([current/decomp/KSA/ScreenspaceRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenspaceRenderer.cs#L204-L207)). Validation layers report this; it relies on the driver
  tolerating it. Not seen at runtime.
- **Composite-mode screenshots miss the effect** because `OnRenderGameCapture` is recorded before the hook.
- **Scope.** Main window, flight scene only: secondary viewports are composited in `RenderViewport` and the
  vehicle editor renders through `RenderEditor`; neither is patched.
- **HDR values.** Writing values outside the range the composite expects changes exposure and highlights.

## See also

- Sibling: [post-ImGui chain](choose-pre-or-post-imgui-post-processing-2.md); [overview](overview.md)
- [Pre-ImGui post-processing on the HDR scene image](../../../../mods/shaderextensions/features/pre-imgui-post-processing.md), [Effect chain declared as RenderPassId-ordered post-process assets](../../../../mods/mpfx/features/effect-chain-assets.md)
- [Colour-grade the frame and add an aspect-correct vignette](../../screen-effects/colour-grade-the-frame-and-add-a-vignette/overview.md), [Refract and distort the rendered scene, and flash or shake it](../../screen-effects/refract-and-distort-the-scene/overview.md), [Insert a custom Vulkan pass into KSA's frame](../../../rendering/vulkan-passes/insert-a-custom-pass-into-render-game/overview.md)
