---
type: How-To
title: "Blur the screen: a non-linear tap distribution and a refracted horizontal pass"
description: Blur with a fixed 97-tap Gaussian whose taps are squared toward the centre, bend the sample position through a normal mask in the horizontal pass, and composite the sharp scene with an input reference
category: shaders
group: screen-effects
topic: blur-the-screen-with-a-separable-gaussian
approach: 2
difficulty: intermediate
prerequisites: [starmap, shaderextensions, kittenextensions]
demonstrated_by: [mods/ksa-aircrafthud/features/glass-post-process-chain]
techniques: [glsl-shader, post-processing]
hooks: [xml-asset PostProcessingShader, xml-asset PostProcessingInputReference]
lifecycle: [render:post-process, frame:viewports]
game_types: [KSA.Rendering.RenderImage, KSA.Program]
tags: [blur, gaussian, tap-distribution, refraction, glass, composite, aircrafthud]
---
KSA_AircraftHUD blurs the scene with no uploaded weights at all. It samples the source 97 times along one axis (48 each way plus the centre) at positions that bunch up near the centre, weights each tap by a Gaussian of its distance in pixels, and shifts the horizontal pass's sample position through a normal map to fake glass. A third pass then composites the blur with the sharp
scene. This article shows that variant. The pinned ShaderExtensions rejects the mod's published XML (it uses the removed subpass attributes); the port is covered in [Write a fullscreen post-processing fragment shader for KSA](../../post-processing/write-a-post-processing-fragment-shader/overview.md).

## Background: how the game does it

A two-dimensional Gaussian is separable, so a horizontal pass followed by a vertical pass over the first pass's output gives the same result at `2 x taps` instead of `taps^2` reads per pixel. ShaderExtensions passes have exactly that shape: pass 62 reads the scene, pass 63 reads pass 62's output; a later pass can read the scene again through `<PostProcessingInputReference RenderPassId="62" />`
([Declare ShaderEx, PostProcessingShader, GlobalPostShader and ImGuiShader assets](../../post-processing/declare-post-processing-shader-assets-in-xml/overview.md)). A pass sampling its previous output through `Program.LinearClampedSampler` can use fractional offsets freely.

## Lifecycle and timing

Passes record in `RenderPassId` order in `Program.RenderGame` (`render:post-process`). The enabled flag that switches the whole chain is a push constant written from the mod's `Program.OnFrameEditor` postfix (`frame:viewports`), so it works with the HUD hidden. See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence) and [Send per-frame values and time to a shader with push constants, written at the right point in the frame](../../post-processing/send-push-constants-to-a-shaderextensions-shader/overview.md).

## Steps

1. **Use a fixed tap count with a non-linear distance.** Tap positions are `sign(t) * |t|^2` over `[-1, 1]` scaled by `BlurRadius` pixels, so most taps sit near the centre and a few reach far; the weight is a Gaussian of the pixel distance `x`:

   ```glsl
   // Shaders/Functions.glsl L114-L150
   const int GaussianSamples = 48;      // Samples on each side
   const float GaussianSigma = 16.0;    // Gaussian weight falloff
   const float BlurRadius = 512.0;      // Blur radius in pixels
   const float BlurExponent = 2.0;     // 1=linear, 2=quadratic, 3=cubic

   vec4 GaussianBlur(sampler2D source, vec2 uv, vec2 texelSize, vec2 direction)
   {
       vec2 minUv = 0.5 * texelSize;
       vec2 maxUv = 1.0 - minUv;

       float twoSigma2 = 2.0 * GaussianSigma * GaussianSigma;

       vec4 blurred = vec4(0.0);
       float weightSum = 0.0;

       for (int i = -GaussianSamples; i <= GaussianSamples; i++)
       {
           float t = float(i) / float(GaussianSamples);

           // Non-linear sample distribution
           float distance = sign(t) * pow(abs(t), BlurExponent);

           float x = distance * BlurRadius;
           float weight = exp(-(x * x) / twoSigma2);

           vec2 offset = direction * distance * BlurRadius * texelSize;

           blurred += texture(
               source,
               clamp(uv + offset, minUv, maxUv)
           ) * weight;

           weightSum += weight;
       }

       return blurred / weightSum;
   }
   ```

   Source: [Shaders/Functions.glsl](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/Functions.glsl#L114-L150). With `sigma = 16` px the weights vanish beyond roughly 48 px, so the 512 px reach is nominal; the many centre taps carry the visible blur. Normalising by `weightSum` keeps brightness. `direction` is `(1, 0)` for the horizontal pass and `(0, 1)` for the vertical one.

2. **Bend the horizontal pass's sample position.** The mod curves the UV and offsets it by a tangent-space normal read from a packed mask, then blurs from the refracted position; the vertical pass is a plain blur:

   ```glsl
   // Shaders/GaussianBlurH.frag L17-L28
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

   ```glsl
   // Shaders/GaussianBlurV.frag L16-L26
   void main()
   {
       if (pp.enabled == 1)
       {
           vec2 texelSize = 1.0 / vec2(textureSize(Source, 0));
           // vec2 curveUv = CurvedUV(Uv, -0.1, 1.12);
           vec4 blurred = GaussianBlur(Source, Uv, texelSize, vec2(0.0, 1.0));
           outColor = blurred;
       }
       else { outColor = texture(Source, Uv); }
   }
   ```

   Sources: [Shaders/GaussianBlurH.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/GaussianBlurH.frag#L17-L28), [Shaders/GaussianBlurV.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/GaussianBlurV.frag#L16-L26). The refraction itself is the topic of [Refract and distort the rendered scene, and flash or shake it](../refract-and-distort-the-scene/overview.md).

3. **Declare the chain and give the composite the sharp scene.** Pass 64 samples the blurred image and, through an input reference to pass 62, the unblurred scene, so a radial mask can mix sharp centre with blurred edges (original XML, which the pinned ShaderExtensions skips until `SubpassId` and `RequiresUniqueRenderpass` are removed):

   ```xml
   <!-- HudAssests.xml L7-L19 -->
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

   Source: [HudAssests.xml](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/HudAssests.xml#L7-L19). The composite mixes `color` toward `brightness * blur` with a mask that rises exponentially toward the corners ([Shaders/Functions.glsl](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/Functions.glsl#L60-L112)), so the centre stays sharp and the edges soften.

## Complete example

| File | Role |
|---|---|
| [Shaders/Functions.glsl](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/Functions.glsl) | `GaussianBlur`, `RadialGradient`, `DirtMask` |
| [Shaders/GaussianBlurH.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/GaussianBlurH.frag), [Shaders/GaussianBlurV.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/GaussianBlurV.frag) | The two blur passes |
| [Shaders/PostProcess.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/PostProcess.frag) | Composite |
| [HudAssests.xml](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/HudAssests.xml) | Chain and reference |

## Variations and gotchas

- **Nominal versus effective radius.** The reach is 512 px but sigma 16 px, so beyond about 48 px taps contribute nothing; lower `GaussianSamples` or `BlurRadius` to save texture reads.
- **Pinned ShaderExtensions rejects the file as published** (`SubpassId`, `RequiresUniqueRenderpass`); extra bindings shift up by one when ported.
- **Grain when disabled.** The composite still adds dither noise in the "off" state.
- **Cost.** 97 reads per pixel per pass at full resolution.

## See also

- Sibling: [CPU weights](blur-the-screen-with-a-separable-gaussian-1.md); [overview](overview.md)
- [Canopy-glass post-process chain](../../../../mods/ksa-aircrafthud/features/glass-post-process-chain.md)
- [Refract and distort the rendered scene, and flash or shake it](../refract-and-distort-the-scene/overview.md), [Add film grain and dither away banding](../add-film-grain-and-blue-noise-dithering/overview.md), [Declare ShaderEx, PostProcessingShader, GlobalPostShader and ImGuiShader assets](../../post-processing/declare-post-processing-shader-assets-in-xml/overview.md)
