---
type: Mod Feature
title: Effect chain declared as RenderPassId-ordered post-process assets
description: MPFX declares its whole effect stack in one XML asset file as 16 PostProcessingShader (pre-ImGui) and 16 GlobalPostShader (post-ImGui) elements with RenderPassId 128 to 143, sharing parameter buffers between the two sides by Id
mod: mpfx
category: shaders
techniques: [xml-asset, post-processing, glsl-shader]
hooks:
  - xml-asset PostProcessingShader (ShaderExtensions)
  - xml-asset GlobalPostShader (ShaderExtensions)
  - harmony-transpiler KSA.Program.RenderGame (ShaderExtensions)
  - harmony-postfix KSA.Program.RebuildRenderer (ShaderExtensions)
lifecycle: [startup:asset-load, render:post-process]
game_types: [KSA.Program, KSA.SerializedId]
tags: [shaders, post-processing, shaderextensions, xml-asset, render-order, imgui]
---

All of MPFX's rendering is data. One asset file,
[MPFX/MPFXAssests.xml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXAssests.xml), lists 32 fullscreen passes. Each effect has a
`PostProcessingShader` that runs on the HDR game image before the UI is drawn, and a `GlobalPostShader`
with the same `RenderPassId` that runs on the finished swapchain image, UI included. ShaderExtensions
turns each element into one render target and one pipeline, chained in `RenderPassId` order. MPFX
contains no rendering code at all.[^mpfx-assets] [^sx-readme]

## How it works

1. **Ship the file.** The csproj marks `MPFXAssests.xml` with `<ModManifestArray>assets</ModManifestArray>`,
   and the build writes `assets = [ "MPFXAssests.xml" ]` into the generated `mod.toml`
   ([MPFX/MPFX.csproj](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.csproj#L83-L91)). The `Shaders/*.frag` files are copied next to it, and each element's
   `Path` is relative to the mod folder.[^mpfx-csproj]
2. **Two element types, one shape.** `PostProcessingShaderAsset` is `[KxAsset("PostProcessingShader")]`
   and `GlobalPostShaderAsset` is `[KxAsset("GlobalPostShader")]`. Both derive from `ShaderEx`, so they
   accept the same binding child elements, an `int RenderPassId` attribute and an optional
   `VertexShaderID` (default `ScreenspaceVert`). `OnDataLoad` appends each instance to a static
   `AllShaders` list ([ShaderExtensions/PostProcessing/PreImgui/PostProcessingShaderAsset.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PreImgui/PostProcessingShaderAsset.cs#L8-L59)).[^sx-ppasset] [^sx-gpasset]
3. **Ordering.** `PostProcessingChain.Build` sorts by `PassId` (`RenderPassId`). For every shader it
   creates a `RenderTarget` in the source image's format and builds a pass whose input is the previous
   pass's output ([ShaderExtensions/PostProcessing/PostProcessingChain.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingChain.cs#L49-L91)). The pre and post chains are
   separate objects, so the same `RenderPassId` on both sides does not collide.[^sx-chain] MPFX's
   order:

   | RenderPassId | Effect | Pre-ImGui buffer | Post-ImGui buffer |
   |---|---|---|---|
   | 128 | Color balance | `MPFXColorBalanceBuffer` (`MPFXMat3BufferAsset`, Size 1) | same buffer, referenced |
   | 129 | Color temperature | `MPFXColorTempBuffer` | same, referenced |
   | 130 | Hue shift (YIQ) | `MPFXHueShiftBuffer` | same, referenced |
   | 131 | Brightness | `MPFXBrightnessBuffer` | same, referenced |
   | 132 | Contrast | `MPFXContrastBuffer` | same, referenced |
   | 133 | Saturation | `MPFXSaturationBuffer` | same, referenced |
   | 134 | Color overlay | `MPFXColorOverlayBuffer` (`MPFXVec4BufferAsset`) | same, referenced |
   | 135 | Vignette | `MPFXVignettePreBuffer` | own `MPFXVignettePostBuffer` |
   | 136 | Film grain | `MPFXFilmGrainBuffer` + push constant `MPFXFilmgrainPreTimeBuffer` | buffer referenced + own push constant |
   | 137-141 | RGB2HSV, HSV2RGB, SingleColor, SingleColorSmallest, SingleColorSolid | one `MPFXDefaultBufferAsset` each | same, referenced |
   | 142 | Gaussian blur horizontal | `MPFXGaussianBlurPreBuffer` | own `MPFXGaussianBlurPostBuffer` |
   | 143 | Gaussian blur vertical | references 142's buffer | references 142's buffer |

4. **Where the chains run.** ShaderExtensions transpiles `Program.RenderGame`. It replaces the
   `BeginRenderPass` call that opens the swapchain pass with `ImGuiPreRender`, which runs the pre chain
   and then begins the pass. It inserts `GlobalPostShaderHandler.RenderNow` after the last
   `EndRenderPass` ([ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L151-L225)).[^sx-patches] In the game, that swapchain pass
   does `RenderFinalComposite` (tonemap composite, or the CMAA2 final blit) and then
   `ImGuiBackend.Vulkan.RenderDrawData` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4633-L4644), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4946-L4952)).[^ksa-program]
   - **Pre chain** input is `Program._offscreenTarget.ColorImage`, read by reflection. Its format is
     `Program.ColorFormat`, `R16G16B16A16SFloat` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L223)). The result is copied
     back into the offscreen image, so the game's own composite tonemaps the graded HDR image
     ([ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs#L16-L46)).[^sx-prehandler]
     That holds only for the tonemap composite. With CMAA2 anti-aliasing, `RenderAaPreparePasses`
     (line 4947, before the hook) has already tonemapped the offscreen image into CMAA2's own LDR
     target, and `RenderFinalComposite` blits that target instead, so the pre chain still runs but
     its result is not displayed
     ([current/decomp/KSA.Rendering.PostProcessing/Cmaa2Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.PostProcessing/Cmaa2Renderer.cs#L166-L177)).[^ksa-cmaa2]
   - **Post chain** copies the swapchain image (`PresentSrcKHR`) into its own `SourceTarget`, runs, and
     copies the result back ([ShaderExtensions/PostProcessing/PostImgui/GlobalPostShaderHandler.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostImgui/GlobalPostShaderHandler.cs#L54-L71)).
     This chain sees the UI, MPFX's own window included.[^sx-posthandler]
5. **Sharing one buffer between both sides.** A buffer element with `Size` creates a buffer, and one
   with only `Id` is a reference (`IsReference() => Size <= 0`). A reference resolves through the
   type-agnostic `IShaderBinding.Bindings` registry by `KeyHash` of the Id
   ([ShaderExtensions/ShaderBinding.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderBinding.cs#L63-L98)).[^sx-shaderbinding] So most effects hold
   both sides' values in one struct: field `a` for pre, field `b` for post. The post color-balance
   pass even references the `mat4` buffer through an `MPFXDefaultBufferAsset` element, and it still
   resolves because only the Id matters.

## Lifecycle and timing

- **`startup:asset-load`.** The XML is deserialized with the rest of the mod's assets, and each
  element's `OnDataLoad` registers it. ShaderExtensions builds both chains in its
  `Program.RebuildRenderer` postfix (the first recorded frame schedules one) and again inside the
  `RenderGame` hook whenever `Renderer.Extent` no longer matches the post chain's source target
  ([ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L197-L236)). Shaders that still declare `SubpassId` (0 or more) or
  `RequiresUniqueRenderpass="true"` are dropped with a logged error at that build
  ([ShaderExtensions/PostProcessing/PostProcessingChain.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingChain.cs#L264-L283)). The pass list itself
  comes only from `OnDataLoad`, so no pass can be added, removed or reordered without editing the XML
  and restarting.
- **`render:post-process`.** Both chains are recorded inside `Program.RenderGame` (phase `frame:render`),
  after `Universe.UpdateRenderData`. The pre chain sits between the AA prepare passes and the final
  composite, and the post chain sits after the ImGui draw and before
  `_screenshotCapture.OnRenderGameSwapchainGrab`.
- **Every pass runs every frame.** ShaderExtensions passes cannot be disabled ([README.md](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/README.md#L35-L38)), so
  MPFX always records 32 fullscreen draws and three full-screen image copies (one
  back into the offscreen image, two around the post chain) on every frame that records
  `RenderGame`. An effect that is "off" is a pass-through, driven by a neutral value in its buffer.
  Nothing is recorded on frames where `Program.Render` rebuilds the renderer instead, or in the
  vehicle editor (`RenderEditor`).

## Key code

| Path | Role |
|---|---|
| [MPFX/MPFXAssests.xml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXAssests.xml) | The chain: 32 elements, buffer and push-constant children |
| [MPFX/MPFX.csproj](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.csproj#L83-L91) | Ships the XML as a manifest asset and copies `Shaders/*` |
| [ShaderExtensions/PostProcessing/PostProcessingChain.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingChain.cs) (ShaderExtensions) | Orders passes, one target per pass, ping-pong input |
| [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs) (ShaderExtensions) | `RenderGame` transpiler that places the two chains |

## Snippets

```xml
<!-- MPFX/MPFXAssests.xml L1-L15 -->
<?xml version="1.0" encoding="utf-8"?>
<Assets>
    <PostProcessingShader Id="MPFXColorBalanceShaderPre" Path="Shaders/ColorBalancePre.frag" RenderPassId="128">
        <MPFXMat3BufferAsset Id="MPFXColorBalanceBuffer" Size="1" />
    </PostProcessingShader>
    <GlobalPostShader Id="MPFXColorBalanceShaderPost" Path="Shaders/ColorBalancePost.frag" RenderPassId="128">
        <MPFXDefaultBufferAsset Id="MPFXColorBalanceBuffer" />
    </GlobalPostShader>

    <PostProcessingShader Id="MPFXColorTempShaderPre" Path="Shaders/ColorTempPre.frag" RenderPassId="129">
        <MPFXDefaultBufferAsset Id="MPFXColorTempBuffer" Size="1" />
    </PostProcessingShader>
    <GlobalPostShader Id="MPFXColorTempShaderPost" Path="Shaders/ColorTempPost.frag" RenderPassId="129">
        <MPFXDefaultBufferAsset Id="MPFXColorTempBuffer" />
    </GlobalPostShader>
```

```xml
<!-- MPFX/MPFXAssests.xml L52-L66 -->
<PostProcessingShader Id="MPFXVignetteShaderPre" Path="Shaders/VignettePre.frag" RenderPassId="135">
    <MPFXVec4BufferAsset Id="MPFXVignettePreBuffer" Size="1" />
</PostProcessingShader>
<GlobalPostShader Id="MPFXVignetteShaderPost" Path="Shaders/VignettePost.frag" RenderPassId="135">
    <MPFXVec4BufferAsset Id="MPFXVignettePostBuffer" Size="1" />
</GlobalPostShader>

<PostProcessingShader Id="MPFXFilmGrainPre" Path="Shaders/FilmGrainPre.frag" RenderPassId="136">
    <MPFXVec4BufferAsset Id="MPFXFilmGrainBuffer" Size="1" />
    <MPFXPushConstantsFloatBufferAsset Id="MPFXFilmgrainPreTimeBuffer" />
</PostProcessingShader>
<GlobalPostShader Id="MPFXFilmGrainPost" Path="Shaders/FilmGrainPost.frag" RenderPassId="136">
    <MPFXVec4BufferAsset Id="MPFXFilmGrainBuffer" />
    <MPFXPushConstantsFloatBufferAsset Id="MPFXFilmgrainPostTimeBuffer" />
</GlobalPostShader>
```

```xml
<!-- MPFX/MPFXAssests.xml L103-L115 -->
<PostProcessingShader Id="MPFXGaussianBlurShaderHorizontalPre" Path="Shaders/GaussianBlurHorizontal.frag" RenderPassId="142">
    <MPFXGaussianBlurBufferAsset Id="MPFXGaussianBlurPreBuffer" Size="1" />
</PostProcessingShader>
<GlobalPostShader Id="MPFXGaussianBlurShaderHorizontalPost" Path="Shaders/GaussianBlurHorizontal.frag" RenderPassId="142">
    <MPFXGaussianBlurBufferAsset Id="MPFXGaussianBlurPostBuffer" Size="1" />
</GlobalPostShader>

<PostProcessingShader Id="MPFXGaussianBlurShaderVerticalPre" Path="Shaders/GaussianBlurVertical.frag" RenderPassId="143">
    <MPFXGaussianBlurBufferAsset Id="MPFXGaussianBlurPreBuffer" />
</PostProcessingShader>
<GlobalPostShader Id="MPFXGaussianBlurShaderVerticalPost" Path="Shaders/GaussianBlurVertical.frag" RenderPassId="143">
    <MPFXGaussianBlurBufferAsset Id="MPFXGaussianBlurPostBuffer" />
</GlobalPostShader>
```

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `Program.RenderGame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4646) | Transpiled by ShaderExtensions to host both chains |
| `Program.RenderFinalComposite` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4633-L4644) | Tonemaps the offscreen image that the pre chain modified, or blits CMAA2's own LDR target |
| `Program._offscreenTarget`, `Program.ColorFormat` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1510) | HDR source of the pre chain (`R16G16B16A16SFloat`) |
| `KSA.SerializedId.Hash` | via ShaderExtensions | Id-to-`KeyHash` key used to share buffers |

## Pitfalls and version notes

- Passes that share a `RenderPassId` within one chain have no guaranteed order, according to the
  ShaderExtensions README; the code's LINQ `OrderBy` is stable, so ties actually run in load order.
  MPFX gives each effect a unique id per side.
- The pre-ImGui film grain pass clamps its input to [0, 1] before it checks whether grain is enabled
  ([MPFX/Shaders/FilmGrainPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/FilmGrainPre.frag#L250-L256)). Because every pass always runs, this reads
  as clamping the HDR image to [0, 1] on every frame, whether grain is on or not: passes 137 to 143
  and the game's tonemap composite only ever see values up to 1. It changes what is displayed only
  when the tonemap composite is in use (anti-aliasing other than CMAA2). This conclusion comes from
  reading the code, not from a runtime test.[^mpfx-sh-filmgrainpre]
- With CMAA2 anti-aliasing, none of the pre-ImGui effects are visible (see step 4), and composite-mode
  screenshots (`ScreenshotCapture.OnRenderGameCapture`, line 4946) are recorded before either chain.
  See [Pre-ImGui post-processing on the HDR scene image](../../shaderextensions/features/pre-imgui-post-processing.md)
  for the ShaderExtensions side.
- The README's "change the subpass" advice is from an older ShaderExtensions. A `SubpassId` of 0 or
  more, or `RequiresUniqueRenderpass="true"`, now makes ShaderExtensions skip the shader with an
  error. MPFX declares neither.
- The vehicle editor uses `RenderEditor`, which ShaderExtensions does not patch, so no MPFX effect
  appears there.

## See also

- [Shader contract for chain passes](effect-shader-contract.md)
- [Uniform buffer and push constant structs bound by attribute name](duck-typed-buffer-structs.md)
- [Per-frame effect parameter upload](per-frame-parameter-upload.md)
- [Custom XML asset types via KxAsset attributes](../../kittenextensions/features/custom-xml-asset-types.md), the KittenExtensions mechanism that makes these element names exist
- [Choose the stage: a pre-ImGui chain on the RGBA16F scene, copied back before the tonemap](../../../howto/shaders/post-processing/choose-pre-or-post-imgui-post-processing/choose-pre-or-post-imgui-post-processing-1.md) - how-to article demonstrating this feature
- [Declare post-processing assets: paired pre and post passes sharing a RenderPassId and buffer](../../../howto/shaders/post-processing/declare-post-processing-shader-assets-in-xml/declare-post-processing-shader-assets-in-xml-1.md) - how-to article demonstrating this feature
- [Feed a shader with uniform buffers: share one buffer across passes (Size owns it, Id references it)](../../../howto/shaders/post-processing/feed-uniform-buffers-to-a-shaderextensions-shader/feed-uniform-buffers-to-a-shaderextensions-shader-2.md) - how-to article demonstrating this feature

[^mpfx-assets]: MPFX/MPFXAssests.xml
[^sx-readme]: ShaderExtensions README.md
[^mpfx-csproj]: MPFX/MPFX.csproj
[^sx-ppasset]: ShaderExtensions PostProcessingShaderAsset.cs
[^sx-gpasset]: ShaderExtensions GlobalPostShaderAsset.cs
[^sx-chain]: ShaderExtensions PostProcessingChain.cs
[^sx-patches]: ShaderExtensions Patches.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-cmaa2]: KSA.Rendering.PostProcessing.Cmaa2Renderer (decompiled, build 2026.10.10.5554)
[^mpfx-sh-filmgrainpre]: MPFX/Shaders/FilmGrainPre.frag
[^sx-prehandler]: ShaderExtensions PostProcessingHandler.cs
[^sx-posthandler]: ShaderExtensions GlobalPostShaderHandler.cs
[^sx-shaderbinding]: ShaderExtensions ShaderBinding.cs
