---
type: How-To
title: "Declare post-processing assets: paired pre and post passes sharing a RenderPassId and buffer"
description: Ship a whole effects chain as one XML file with a PostProcessingShader and a GlobalPostShader per effect, ordered by RenderPassId and sharing parameter buffers by Id
category: shaders
group: post-processing
topic: declare-post-processing-shader-assets-in-xml
approach: 1
difficulty: beginner
prerequisites: [starmap, shaderextensions, kittenextensions]
demonstrated_by: [mods/mpfx/features/effect-chain-assets, mods/mpfx/features/separable-gaussian-blur]
techniques: [xml-asset, post-processing]
hooks: [xml-asset PostProcessingShader, xml-asset GlobalPostShader]
lifecycle: [startup:asset-load, render:post-process]
game_types: [KSA.ShaderReference, KSA.SerializedId, KSA.ModLibrary, KSA.AssetBundle]
tags: [xml-asset, render-pass-id, mpfx, effect-chain, shared-buffer, mod-toml]
---
MPFX registers all of its rendering as data: one asset file with 32 elements for 16 effects, each effect
declared twice (before and after the UI) with the same `RenderPassId`. This article shows how to write
and ship a file like that, how a chain is ordered, and how the pre and post halves of an effect share
one parameter buffer so the C# side writes it once. It needs StarMap, KittenExtensions Continued and
ShaderExtensions; the C# that creates the buffer element names is covered in
[Feed a ShaderExtensions shader from C# with uniform buffers (std140)](../feed-uniform-buffers-to-a-shaderextensions-shader/overview.md).

## Background: how the game does it

- The game loads every XML file in a mod's `assets` list into an `AssetBundle` and calls
  `OnDataLoad` on each entry ([current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L84-L108)).
- ShaderExtensions' `PostProcessingShaderAsset` and `GlobalPostShaderAsset` collect themselves into
  static `AllShaders` lists in `OnDataLoad`. The pass list therefore comes only from the XML: nothing can be
  added, removed or reordered at runtime.
- `PostProcessingChain.Build` sorts a chain by `RenderPassId`, makes one render target per shader and
  feeds each pass the previous pass's output
  ([ShaderExtensions/PostProcessing/PostProcessingChain.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingChain.cs#L62-L90)). The pre and post chains are separate
  objects, so the same id on both sides does not collide.

## Lifecycle and timing

| Moment | What happens | Value |
|---|---|---|
| Game start | XML parsed, `OnDataLoad` registers each pass, buffers are created in `ModLibrary.Bind` | `startup:asset-load` |
| First rendered frame and every renderer rebuild | Both chains are built from the registered lists | `frame:prepare` / `frame:render` |
| Every flight frame | Pre chain before the swapchain render pass, post chain after it | `render:post-process` |

Every registered pass records a draw every frame (nothing can be disabled), so an "off" effect is a
pass-through driven by neutral values; see [Write a fullscreen post-processing fragment shader for KSA](../write-a-post-processing-fragment-shader/overview.md).

## Steps

1. **Ship the XML as a manifest asset.** MPFX's csproj marks `MPFXAssests.xml` with
   `<ModManifestArray>assets</ModManifestArray>`, so the generated `mod.toml` carries
   `assets = [ "MPFXAssests.xml" ]`, and copies `Shaders/*` next to it:

   ```xml
   <!-- MPFX/MPFX.csproj L83-L91 -->
   	<ItemGroup>
   		<None Update="$(ModName)Assests.xml">
   			<CopyToOutputDirectory>Always</CopyToOutputDirectory>
   			<ModManifestArray>assets</ModManifestArray>
   		</None>
   		<None Update="Shaders\*">
   			<CopyToOutputDirectory>Always</CopyToOutputDirectory>
   		</None>
   	</ItemGroup>
   ```

   Source: [MPFX/MPFX.csproj](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.csproj#L83-L91). If you write `mod.toml` by hand, add `assets = [ "YourAssets.xml" ]`
   and keep `Path` values relative to the mod folder.

2. **Declare one element per pass.** A buffer child with `Size` creates a buffer; a child with only an
   `Id` references the buffer another element created. That is how both halves of an effect read
   the same struct:

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

   Source: [MPFX/MPFXAssests.xml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXAssests.xml#L1-L15). The reference resolves by the hash of the `Id` only, so the
   post element can use a different element name than the creator (the colour-balance pair does).

3. **Pick `RenderPassId`s with room to grow.** MPFX uses 128 to 143, one id per effect and the same id
   for its pre and post halves. Gaps let another mod or a later version insert an effect between two
   existing ones without renumbering. Keep every id unique inside one chain; equal ids keep load order in practice
   but the README says the order is then not guaranteed.

4. **Chain multi-pass effects by consecutive ids.** A pass samples the previous pass's output without
   any declaration. A two-pass blur is therefore two elements with ids 142 and 143, and the second one
   references the first one's buffer so both read the same radius and weights:

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

   Source: [MPFX/MPFXAssests.xml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXAssests.xml#L103-L115). Vignette and blur have one buffer per side (own `Size`), the
   other effects share one buffer between sides. See [Blur the screen with a two-pass separable Gaussian](../../screen-effects/blur-the-screen-with-a-separable-gaussian/overview.md).

5. **Push constants are children too.** Film grain adds a push-constant element next to its buffer
   (`MPFXPushConstantsFloatBufferAsset`) and a second, separate one on the post side; push-constant
   children take no binding number ([MPFX/MPFXAssests.xml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXAssests.xml#L59-L66)).

## Complete example

| File | Role |
|---|---|
| [MPFX/MPFXAssests.xml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXAssests.xml) | The full chain: ids 128 to 143, pre and post halves |
| [MPFX/MPFX.csproj](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.csproj) | Adds the file to the generated `mod.toml` and copies the shaders |
| [MPFX/Shaders/ColorBalancePre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/ColorBalancePre.frag) | Example of a shader reading the bound buffer |

## Variations and gotchas

- **Every pass always runs.** MPFX records 32 fullscreen draws plus three image copies every frame; an
  effect that is off is a pass-through.
- **The pre-ImGui chain is not visible with CMAA2** anti-aliasing selected; see
  [Choose between pre-ImGui (HDR scene) and post-ImGui (final frame) post-processing](../choose-pre-or-post-imgui-post-processing/overview.md).
- **Subpass attributes.** `SubpassId` or `RequiresUniqueRenderpass` on any element makes ShaderExtensions skip
  it with a log error. MPFX declares neither.
- **Case and paths.** `Path` values are mod-relative; a mismatch in letter case works on Windows and fails elsewhere.
- **Order of children is the binding order** (starting at set 1 binding 2); see
  [Write a fullscreen post-processing fragment shader for KSA](../write-a-post-processing-fragment-shader/overview.md).
- **Vehicle editor.** `RenderEditor` is not patched, so no declared pass appears there.

## See also

- Siblings: [small file with chain, references and ImGuiShader](declare-post-processing-shader-assets-in-xml-2.md), [how references resolve](declare-post-processing-shader-assets-in-xml-3.md), [overview](overview.md)
- [Effect chain declared as RenderPassId-ordered post-process assets](../../../../mods/mpfx/features/effect-chain-assets.md), [Separable Gaussian blur with CPU-computed weights](../../../../mods/mpfx/features/separable-gaussian-blur.md)
- [Feed a ShaderExtensions shader from C# with uniform buffers (std140)](../feed-uniform-buffers-to-a-shaderextensions-shader/overview.md), [Send per-frame values and time to a shader with push constants, written at the right point in the frame](../send-push-constants-to-a-shaderextensions-shader/overview.md)
