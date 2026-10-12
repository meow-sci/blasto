---
type: How-To
title: "Write a post-processing fragment shader: extra textures and the port from subpasses to dynamic rendering"
description: Bind a dirt mask and a blue-noise texture to a ShaderExtensions pass, share a GLSL include, and port the v0.4.0 subpass-style shaders of KSA_AircraftHUD to the current binding layout
category: shaders
group: post-processing
topic: write-a-post-processing-fragment-shader
approach: 2
difficulty: intermediate
prerequisites: [starmap, shaderextensions, kittenextensions]
demonstrated_by:
  - mods/ksa-aircrafthud/features/glass-post-process-chain
  - mods/shaderextensions/features/shaderex-extra-bindings
  - mods/ksa-aircrafthud/features/blue-noise-dithering
techniques: [glsl-shader, xml-asset, post-processing]
hooks: [xml-asset PostProcessingShader, xml-asset TextureBinding, xml-asset PostProcessingInputReference]
lifecycle: [startup:asset-load, frame:render, render:post-process]
game_types: [KSA.ShaderReference, KSA.TextureReference, KSA.ModLibrary]
tags: [glsl, post-processing, texture-binding, blue-noise, dirt-mask, subpass, port, aircrafthud]
---
A pass often needs more than the previous image: a lookup table, a noise texture, a mask. This
article shows how KSA_AircraftHUD binds a packed normal-and-dirt mask and a stacked blue-noise
texture to its three canopy-glass passes, and, because that mod was written against the older
subpass-based ShaderExtensions, how to port such a shader to the current layout. The mod's shaders do
not load on the pinned ShaderExtensions as published; the port below is adapted and labelled as such.

## Background: how the game does it

`ShaderEx` is a normal game `ShaderReference` (the game compiles the GLSL file) that also owns a list of
child elements, each of which adds one descriptor to the pass's set 1. The game's own shader
references carry no binding information; every game renderer hard-codes its layout. `ShaderEx` moves
the layout into XML.[^shaderextensions-shaderex]

| Binding (set 1) | Content |
|---|---|
| 0 | previous pass image, `sampler2D` |
| 1 | `ShaderTime` uniform block (always present) |
| 2, 3, ... | children in XML document order: `<TextureBinding>`, uniform-buffer elements, `<PostProcessingInputReference>`; push-constant elements take no binding |

Textures are bound with `Program.LinearClampedSampler` in `ShaderReadOnlyOptimal`
([ShaderExtensions/ShaderBinding.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderBinding.cs#L45-L53)), so a lookup that needs exact texels computes texel-centre
UVs itself. The `ShaderEx.OnDataLoad` override forwards the load call to each child because the game
only calls `OnDataLoad` on top-level entries of an asset bundle
([current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L84-L108)).

## Lifecycle and timing

- `startup:asset-load`: the XML is parsed, textures are loaded by the children's `OnDataLoad`, and
  `ModLibrary.Bind` later resolves the binding list on a thread-pool thread
  ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1849-L1861)).
- `frame:render` / `render:post-process`: the descriptor set built from these bindings is bound when
  the pass records inside `Program.RenderGame`; see the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

1. **Add the textures to the mod.** AircraftHUD ships `Textures/DirtMask.PNG` (2560x1440, red and green
   are a tangent-space normal, blue is dirt amount) and `Textures/stbn_vec3_128x128x64.png` (a 128x8192
   stack of 64 spatiotemporal blue-noise tiles). Both are plain image files referenced by path.

2. **Share helper GLSL with an include.** All three shaders start with `#include "Functions.glsl"`, an
   include-guarded file next to them (it begins with `#ifndef ACF_HUD_FUNC_INCLUDED`). Plain relative
   includes of files inside the mod folder work in the game's shader compiler:

   ```glsl
   // Shaders/GaussianBlurH.frag L1-L28 (KSA_AircraftHUD, v0.4.0 binding numbers)
   #version 450 core

   #include "Functions.glsl"

   layout(location = 0) out vec4 outColor;

   layout(set = 1, binding = 0) uniform sampler2D Source;
   layout(set = 1, binding = 1) uniform sampler2D dirtMask;

   layout(location = 0) in vec2 Uv;

   layout(push_constant, std430) uniform Params {
      int enabled;
      float frame;
   } pp;

   void main()
   {
       vec2 curveUv = CurvedUV(Uv, -0.1, 1.12);
       if (pp.enabled == 1)
       {
           vec2 texelSize = 1.0 / vec2(textureSize(Source, 0));
           vec2 refractedUv = RefractionUVs(curveUv, texelSize, dirtMask).rg;
           vec4 blurred = GaussianBlur(Source, refractedUv, texelSize, vec2(1.0, 0.0));
           outColor = blurred;
       }
       else { outColor = texture(Source, curveUv); }
   }
   ```

   Source: [Shaders/GaussianBlurH.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/GaussianBlurH.frag#L1-L28). The binding numbers in this listing are the
   old ones; step 4 gives the current ones.

3. **List the children in the order the GLSL expects.** The original XML (this form is rejected
   by the pinned ShaderExtensions; see step 4):

   ```xml
   <!-- HudAssests.xml L7-L19 (KSA_AircraftHUD) -->
   	<PostProcessingShader Id="GlassBlurH" Path="Shaders/GaussianBlurH.frag" RenderPassId="62" RequiresUniqueRenderpass="true" >
   		<PPPushConstantsBufferAsset Id="PPPushConstantsBuffer0" Size="1" />
   		<TextureBinding Path="Textures/DirtMask.png" />
   	</PostProcessingShader>
   	<PostProcessingShader Id="GlassBlurV" Path="Shaders/GaussianBlurV.frag" RenderPassId="63" RequiresUniqueRenderpass="true" >
   		<PPPushConstantsBufferAsset Id="PPPushConstantsBuffer1" Size="1" />
   	</PostProcessingShader>
   	<PostProcessingShader Id="PostProcess" Path="Shaders/PostProcess.frag" RenderPassId="64" SubpassId="0" >
   		<PPPushConstantsBufferAsset Id="PPPushConstantsBuffer2" Size="1" />
   		<PostProcessingInputReference RenderPassId="62" SubpassId="0" />
   		<TextureBinding Path="Textures/stbn_vec3_128x128x64.png" />
   		<TextureBinding Path="Textures/DirtMask.png" />
   	</PostProcessingShader>
   ```

   Source: [HudAssests.xml](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/HudAssests.xml#L7-L19). The third pass is the composite: it samples the blurred image
   (binding 0), the unblurred scene through the input reference, the noise texture and the dirt mask.
   The reference mechanism is explained in [Declare ShaderEx, PostProcessingShader, GlobalPostShader and ImGuiShader assets](../declare-post-processing-shader-assets-in-xml/overview.md).

4. **Port to dynamic rendering.** Since the dynamic-rendering rewrite ShaderExtensions skips any shader
   that declares `SubpassId` or `RequiresUniqueRenderpass`, with an error naming the shader
   ([ShaderExtensions/PostProcessing/PostProcessingChain.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingChain.cs#L264-L283)). The port is three edits:
   delete both attributes (keep `RenderPassId`), drop `SubpassId` from the input reference, and move
   every extra binding up by one because binding 1 is now the time buffer.

   ```xml
   <!-- adapted from HudAssests.xml L7-L19: attributes removed, order unchanged -->
   <PostProcessingShader Id="GlassBlurH" Path="Shaders/GaussianBlurH.frag" RenderPassId="62">
   	<PPPushConstantsBufferAsset Id="PPPushConstantsBuffer0" Size="1" />
   	<TextureBinding Path="Textures/DirtMask.png" />
   </PostProcessingShader>
   <PostProcessingShader Id="GlassBlurV" Path="Shaders/GaussianBlurV.frag" RenderPassId="63">
   	<PPPushConstantsBufferAsset Id="PPPushConstantsBuffer1" Size="1" />
   </PostProcessingShader>
   <PostProcessingShader Id="PostProcess" Path="Shaders/PostProcess.frag" RenderPassId="64">
   	<PPPushConstantsBufferAsset Id="PPPushConstantsBuffer2" Size="1" />
   	<PostProcessingInputReference RenderPassId="62" />
   	<TextureBinding Path="Textures/stbn_vec3_128x128x64.png" />
   	<TextureBinding Path="Textures/DirtMask.png" />
   </PostProcessingShader>
   ```

   ```glsl
   // adapted from Shaders/PostProcess.frag L7-L11: every extra binding moves up by one
   layout(set = 1, binding = 0) uniform sampler2D Source;
   layout(set = 1, binding = 2) uniform sampler2D PreviousInput; // PostProcessingInputReference
   layout(set = 1, binding = 3) uniform sampler2D noiseTex;      // stbn_vec3_128x128x64.png
   layout(set = 1, binding = 4) uniform sampler2D dirtMask;      // DirtMask.png
   ```

   In `GaussianBlurH.frag` the dirt mask becomes `binding = 2`. The original `PostProcess.frag` keeps
   a commented-out `subpassInput Source` line ([Shaders/PostProcess.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/PostProcess.frag#L7-L11)); with dynamic
   rendering it is a normal `sampler2D` at binding 0 and needs no change.

5. **Index a stacked texture exactly.** The noise texture holds 64 tiles in one column. The lookup
   tiles it over the screen and picks a slice from time, using texel-centre UVs because the sampler
   is linear:

   ```glsl
   // Shaders/Functions.glsl L22-L40
   vec4 BlueNoiseDither(vec4 color, sampler2D tex, float frame, float str)
   {
       ivec2 pixel = ivec2(gl_FragCoord.xy);

       int f = int(frame * 60.0) & 63;

       vec2 uv;

       uv.x = (float(pixel.x & 127) + 0.5) / 128.0;
       uv.y = (float((pixel.y & 127) + f * 128) + 0.5) / 8192.0;

       vec3 noise = texture(tex, uv).rgb;

       noise -= 0.5;
   	noise *= str;
   	color += vec4(noise, 1) * color;

       return color;
   }
   ```

   Source: [Shaders/Functions.glsl](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/Functions.glsl#L22-L40). The full discussion of this effect is in
   [Add film grain and dither away banding](../../screen-effects/add-film-grain-and-blue-noise-dithering/overview.md).

## Complete example

| File | Role |
|---|---|
| [HudAssests.xml](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/HudAssests.xml) | Three chained passes and an `ImGuiShader`; listed under `assets` in [mod.toml](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/mod.toml) |
| [Shaders/GaussianBlurH.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/GaussianBlurH.frag), [Shaders/GaussianBlurV.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/GaussianBlurV.frag) | Horizontal and vertical blur |
| [Shaders/PostProcess.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/PostProcess.frag) | Composite, dirt, aberration, dither |
| [Shaders/Functions.glsl](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/Functions.glsl) | Shared helpers behind the include |

## Variations and gotchas

- **Binding order is XML order.** Reordering children silently swaps textures; push-constant
  elements do not consume a number.
- **Fragment stage only.** Extra bindings are created with `VkShaderStageFlags.FragmentBit`; a custom
  vertex shader cannot read them.
- **Descriptor types.** `ShaderEx` supports `CombinedImageSampler`, `UniformBuffer`,
  `UniformBufferDynamic` and `InputAttachment`; anything else throws `NotSupportedException`.
- **File name case.** The XML asks for `Textures/DirtMask.png`; the file on disk is `DirtMask.PNG`.
  That is fine on a case-insensitive file system and a missing texture elsewhere.
- **`CurvedUV` ignores its arguments.** It reads file-level constants, so the strength and scale
  passed by the callers have no effect ([Shaders/Functions.glsl](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/Functions.glsl#L42-L58)).
- **A pass stays on.** `PostProcess.frag` always runs the dither after its enabled and disabled
  branches, so even the "off" state adds grain.

## See also

- Siblings: [identity-for-neutral pass](write-a-post-processing-fragment-shader-1.md), [RenderTarget per shader](write-a-post-processing-fragment-shader-3.md), [descriptor set per asset](write-a-post-processing-fragment-shader-4.md), [overview](overview.md)
- [Canopy-glass post-process chain](../../../../mods/ksa-aircrafthud/features/glass-post-process-chain.md), [Spatiotemporal blue-noise dithering](../../../../mods/ksa-aircrafthud/features/blue-noise-dithering.md), [ShaderEx asset with extra descriptor bindings](../../../../mods/shaderextensions/features/shaderex-extra-bindings.md)
- [Declare ShaderEx, PostProcessingShader, GlobalPostShader and ImGuiShader assets](../declare-post-processing-shader-assets-in-xml/overview.md), [Refract and distort the rendered scene, and flash or shake it](../../screen-effects/refract-and-distort-the-scene/overview.md), [Add film grain and dither away banding](../../screen-effects/add-film-grain-and-blue-noise-dithering/overview.md), [Send per-frame values and time to a shader with push constants, written at the right point in the frame](../send-push-constants-to-a-shaderextensions-shader/overview.md)

[^shaderextensions-shaderex]: ShaderExtensions/ShaderExtensions/ShaderEx.cs
