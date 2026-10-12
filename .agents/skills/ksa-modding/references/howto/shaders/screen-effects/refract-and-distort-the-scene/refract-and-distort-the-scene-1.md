---
type: How-To
title: "Refract the scene: normal-mask UV offsets and luminance-gated dirt in a fragment pass"
description: Offset the sample position by a tangent-space normal read from a packed mask texture, and gate a dirt amount by luminance so bright light lights the smudges
category: shaders
group: screen-effects
topic: refract-and-distort-the-scene
approach: 1
difficulty: intermediate
prerequisites: [starmap, shaderextensions, kittenextensions]
demonstrated_by: [mods/ksa-aircrafthud/features/glass-post-process-chain]
techniques: [glsl-shader, post-processing]
hooks: [xml-asset PostProcessingShader, xml-asset TextureBinding]
lifecycle: [render:post-process]
game_types: [KSA.Program]
tags: [refraction, normal-map, dirt-mask, glass, glare, post-processing, aircrafthud]
---
KSA_AircraftHUD makes the scene look like it is seen through scratched canopy glass by reading a mask texture: the red and green channels hold a tangent-space normal that bends the sample position, and the blue channel holds how much dirt is at that pixel. The dirt only shows where the blurred scene is bright, so glare lights the smudges. This article is the maths and the binding; the surrounding chain is
described in [Blur the screen with a two-pass separable Gaussian](../blur-the-screen-with-a-separable-gaussian/overview.md) and [Declare ShaderEx, PostProcessingShader, GlobalPostShader and ImGuiShader assets](../../post-processing/declare-post-processing-shader-assets-in-xml/overview.md).

## Background: how the game does it

Nothing in the game provides this. The mask `Textures/DirtMask.PNG` is a 2560x1440 RGB image used as packed data, not colour: `rg * 2 - 1` is a normal (scaled by `RefractionStrength = 50` texels), `b` is the dirt amount, and the shader also reads alpha, which an RGB PNG supplies as 1. ShaderExtensions binds it as an extra sampler on set 1 after the fixed bindings, with the game's linear clamped sampler.

## Lifecycle and timing

Pre-ImGui chain, `render:post-process`: the HUD stays sharp because the passes work on the scene image. The switch is a push constant written from the `OnFrameEditor` postfix (`frame:viewports`). See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

1. **Bind the mask** as a `<TextureBinding>` on the pass(es) that read it (here the horizontal blur and the composite). Binding numbers follow document order after the fixed bindings; see [Write a fullscreen post-processing fragment shader for KSA](../../post-processing/write-a-post-processing-fragment-shader/overview.md) for the numbers on the pinned ShaderExtensions.

2. **Offset the UV by the mask's normal.** The offset is in texels so its size is resolution independent:

   ```glsl
   // Shaders/Functions.glsl L81-L94
   const float RefractionStrength = 50.0;

   // normal.x, normal.y, dirt, alpha
   vec4 RefractionUVs(vec2 uv, vec2 texelSize, sampler2D dirtMask)
   {
   	vec4 dirtMaskRGBA = texture(dirtMask, uv);

       // tangent-space normal
       vec2 n = dirtMaskRGBA.rg * 2.0 - 1.0;
       vec2 refractOffset = n * RefractionStrength * texelSize;
       vec2 refractedUv = uv + refractOffset;

       return vec4(refractedUv, dirtMaskRGBA.b, dirtMaskRGBA.a);
   }
   ```

   Source: [Shaders/Functions.glsl](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/Functions.glsl#L81-L94). The first pass (horizontal blur) samples from the refracted UV, so the blur itself carries the distortion.

3. **Gate the dirt by luminance and add it as glow.** The dirt amount is multiplied by a smoothstep of the blurred scene's luminance, added to a radial mask that mixes the sharp scene toward half-brightness blur, and the blur scaled by the dirt is added so smudges glow in bright light:

   ```glsl
   // Shaders/Functions.glsl L96-L112
   const float brightness = 0.5;
   vec4 DirtMask(vec2 uv, vec2 texelSize, vec4 color, vec4 blur, float dirt)
   {
       float lum = Luminance(blur.rgb);
   	float lumMask = smoothstep(0.02, 0.2, lum);

       float mask = RadialGradient(uv, texelSize);

   	dirt *= lumMask;

       float finalMask = clamp(mask + dirt, 0.0, 1.0);

       color = mix(color, brightness * blur, finalMask);
       color += dirt * (blur * 12.0);

       return color;
   }
   ```

   Source: [Shaders/Functions.glsl](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/Functions.glsl#L96-L112).

4. **Use the refracted UV for the sharp scene too.** In the composite the sharp image is sampled at the refracted position and then mixed with the blurred, aberrated copy:

   ```glsl
   // Shaders/PostProcess.frag L35-L39
   		vec2 curveUv = CurvedUV(zoomUv, -0.1, 1.12);
   		vec4 refracted = RefractionUVs(curveUv, texelSize, dirtMask);

   		vec4 original = texture(PreviousInput, refracted.rg);
   		color = DirtMask(Uv, texelSize, original, color, refracted.b);
   ```

   Source: [Shaders/PostProcess.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/PostProcess.frag#L35-L39).

## Complete example

| File | Role |
|---|---|
| [Shaders/Functions.glsl](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/Functions.glsl) | `RefractionUVs`, `DirtMask`, `RadialGradient` |
| [Shaders/PostProcess.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/PostProcess.frag) | Composite |
| [HudAssests.xml](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/HudAssests.xml) | Chain and texture bindings |

## Variations and gotchas

- **File name case.** The XML asks for `DirtMask.png`; the file is `DirtMask.PNG`.
- **Mask is data.** Do not let anything gamma-convert or compress the mask; red and green are a normal and blue is an amount.
- **Pinned ShaderExtensions rejects the published asset file** until the subpass attributes are removed.
- **Grain stays on** in the "off" state because the dither runs after the branch.
- **Pre-ImGui only.** Not visible with CMAA2 anti-aliasing.

## See also

- Siblings: [compute shock ring](refract-and-distort-the-scene-2.md), [flash and shake](refract-and-distort-the-scene-3.md), [overview](overview.md)
- [Canopy-glass post-process chain](../../../../mods/ksa-aircrafthud/features/glass-post-process-chain.md)
- [Bend the picture with CRT curvature and chromatic aberration](../bend-the-picture-with-crt-curvature-and-chromatic-aberration/overview.md), [Blur the screen with a two-pass separable Gaussian](../blur-the-screen-with-a-separable-gaussian/overview.md)
