---
type: How-To
title: "Bend the picture: chromatic aberration and radial edge blur in a glass composite pass"
description: Curve the scene UV with a barrel distortion and fringe red and blue by a radius-squared amount in the final pass of a three-pass canopy-glass chain
category: shaders
group: screen-effects
topic: bend-the-picture-with-crt-curvature-and-chromatic-aberration
approach: 2
difficulty: intermediate
prerequisites: [starmap, shaderextensions, kittenextensions]
demonstrated_by: [mods/ksa-aircrafthud/features/glass-post-process-chain]
techniques: [glsl-shader, post-processing]
hooks: [xml-asset PostProcessingShader]
lifecycle: [render:post-process]
game_types: [KSA.Program]
tags: [crt, barrel-distortion, chromatic-aberration, glass, post-processing, aircrafthud, zoom]
---
The same two ideas, applied to the whole scene instead of a window: in the last pass of KSA_AircraftHUD's canopy-glass chain the image is zoomed slightly, aberrated with a radius-squared offset along the radius, curved, refracted through a dirt mask and mixed with the blurred copy at the edges. This article covers the aberration and curvature helpers and how they sit in the composite; the blur is in
[Blur the screen with a two-pass separable Gaussian](../blur-the-screen-with-a-separable-gaussian/overview.md) and the refraction in [Refract and distort the rendered scene, and flash or shake it](../refract-and-distort-the-scene/overview.md). The mod's published XML needs the dynamic-rendering port ([Write a fullscreen post-processing fragment shader for KSA](../../post-processing/write-a-post-processing-fragment-shader/overview.md)).

## Background: how the game does it

Pre-ImGui passes work on the HDR scene before the tonemap, so the effect never touches the HUD or windows. The third pass (`RenderPassId` 64) samples the blurred image through the implicit previous-pass binding and the sharp scene through a `PostProcessingInputReference` to pass 62 ([Declare ShaderEx, PostProcessingShader, GlobalPostShader and ImGuiShader assets](../../post-processing/declare-post-processing-shader-assets-in-xml/overview.md)).

## Lifecycle and timing

The pass records in `Program.RenderGame` before the swapchain render pass (`render:post-process`); its enabled flag is a push constant written each frame. See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

1. **Aberrate along the radius, aspect-correct, with a radius-squared falloff.** Offset red along the direction from the optical centre and blue the opposite way, in pixel units through the texel size; zero at the centre:

   ```glsl
   // Shaders/Functions.glsl L189-L219
   vec4 ChromaticAberration(sampler2D tex, vec2 uv, vec2 texelSize, float amount)
   {
       // Optical center
       vec2 dir = uv * 2.0 - 1.0;

       // Aspect-correct so the aberration is circular
       float aspect = texelSize.y / texelSize.x;
       dir.x *= aspect;

       float r = length(dir);

       if (r < 1e-5)
           return texture(tex, uv);

       dir /= r;

       // Normalize radius (0 center, 1 corners)
       r /= length(vec2(aspect, 1.0));

       // Smooth radial falloff
       float strength = r * r;

       // Offset in pixels
       vec2 offset = dir * (amount * strength) * texelSize;

       vec4 red   = texture(tex, uv + offset);
       vec4 green = texture(tex, uv);
       vec4 blue  = texture(tex, uv - offset);

       return vec4(red.r, green.g, blue.b, green.a);
   }
   ```

   Source: [Shaders/Functions.glsl](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/Functions.glsl#L189-L219). `amount` is in pixels at the corners (the composite uses `-15.0`).

2. **Barrel-distort the UV.** Centre at -1..1, scale, push points outward by `1 + strength * r^2`, return to 0..1:

   ```glsl
   // Shaders/Functions.glsl L41-L58
   const float curveStrength = -0.15; // -0.1
   const float curveScale = 1.15; // 1.12
   vec2 CurvedUV(vec2 uv, float strength, float scale)
   {
       // Center at (-1..1)
       vec2 p = uv * 2.0 - 1.0;

       // Scale around the center
       p *= curveScale;

       // Barrel distortion
       float r2 = dot(p, p);
       p *= 1.0 + curveStrength * r2;

       // Back to 0-1
       return p * 0.5 + 0.5;
   }
   ```

   Source: [Shaders/Functions.glsl](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/Functions.glsl#L41-L58). The `strength` and `scale` parameters are ignored: the helper reads the file-level constants, so the `-0.1, 1.12` that the callers pass have no effect. Use the parameters if you want per-call control.

3. **Compose in the right order.** Zoom the UV in 2.5 percent around the centre, aberrate the blurred source, curve, refract and mix with the sharp copy:

   ```glsl
   // Shaders/PostProcess.frag L25-L39
   	if (pp.enabled == 1)
   	{
   		vec2 zoomUv = Uv - 0.5;
   		zoomUv *= 0.975;
   		zoomUv += 0.5;

   		vec2 texelSize = 1.0 / vec2(textureSize(Source, 0));

   		color = ChromaticAberration(Source, zoomUv, texelSize, -15.0);

   		vec2 curveUv = CurvedUV(zoomUv, -0.1, 1.12);
   		vec4 refracted = RefractionUVs(curveUv, texelSize, dirtMask);

   		vec4 original = texture(PreviousInput, refracted.rg);
   		color = DirtMask(Uv, texelSize, original, color, refracted.b);
   ```

   Source: [Shaders/PostProcess.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/PostProcess.frag#L25-L39). The radial mask in `DirtMask` rises exponentially toward the corners, so the centre stays sharp and the edges soften, which reads as the glass's edge blur.

## Complete example

| File | Role |
|---|---|
| [Shaders/PostProcess.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/PostProcess.frag) | Composite |
| [Shaders/Functions.glsl](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/Functions.glsl) | `ChromaticAberration`, `CurvedUV`, `RadialGradient`, `DirtMask` |

## Variations and gotchas

- **The helper ignores its arguments** (`CurvedUV`), as above.
- **Not visible with CMAA2** anti-aliasing selected for the pre-ImGui chain ([Choose between pre-ImGui (HDR scene) and post-ImGui (final frame) post-processing](../../post-processing/choose-pre-or-post-imgui-post-processing/overview.md)).
- **Grain stays on** after the branch even when the effect is off.
- **Pinned ShaderExtensions skips the mod's XML** until the subpass attributes are removed and bindings are shifted up by one.
- **For a window only,** see the [ImGuiShader article](bend-the-picture-with-crt-curvature-and-chromatic-aberration-1.md): the post-processing chain cannot treat one UI window specially.

## See also

- Siblings: [ImGuiShader window](bend-the-picture-with-crt-curvature-and-chromatic-aberration-1.md), [host mechanics](bend-the-picture-with-crt-curvature-and-chromatic-aberration-3.md), [overview](overview.md)
- [Canopy-glass post-process chain](../../../../mods/ksa-aircrafthud/features/glass-post-process-chain.md)
- [Refract and distort the rendered scene, and flash or shake it](../refract-and-distort-the-scene/overview.md), [Blur the screen with a two-pass separable Gaussian](../blur-the-screen-with-a-separable-gaussian/overview.md)
