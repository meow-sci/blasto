---
type: How-To
title: "Colour-grade the frame: pre-ImGui grading passes on the HDR scene"
description: Write colour balance, colour temperature, hue shift, contrast, saturation, overlay and an aspect-correct vignette as short ShaderExtensions passes that return their input at neutral values and are driven by one parameter block each
category: shaders
group: screen-effects
topic: colour-grade-the-frame-and-add-a-vignette
approach: 1
difficulty: intermediate
prerequisites: [starmap, shaderextensions, kittenextensions]
demonstrated_by: [mods/mpfx/features/color-grading-math, mods/mpfx/features/effect-shader-contract]
techniques: [glsl-shader, post-processing]
hooks: [xml-asset PostProcessingShader, starmap StarMapAfterGui]
lifecycle: [render:post-process, frame:draw-ui]
game_types: [KSA.Program, Brutal.Numerics.float4x4]
tags: [colour-grading, lift-gamma-gain, colour-temperature, hue-shift, vignette, hdr, yiq, mpfx]
---
MPFX's grading shaders are short, self-contained and mostly lifted from well-known public sources. Each is a drop-in function for any ShaderExtensions pass. This article collects them with the rules that make them safe to leave running: neutral parameters return the input, HDR-aware pivots, C# doing the unit conversion so each shader is one expression, and one parameter block per
effect. It builds on [Write a fullscreen post-processing fragment shader for KSA](../../post-processing/write-a-post-processing-fragment-shader/overview.md) (the pass contract) and [Feed a ShaderExtensions shader from C# with uniform buffers (std140)](../../post-processing/feed-uniform-buffers-to-a-shaderextensions-shader/overview.md) (the parameter blocks). MPFX runs each effect twice, before and after the UI; the pre-ImGui halves are quoted here.

## Background: how the game does it

The pre-ImGui chain works on `R16G16B16A16SFloat` scene values before the game's tonemap composite, so values above 1.0 exist and contrast should pivot on scene-linear mid-grey 0.18, not 0.5. The post-ImGui chain sees tonemapped display values. In MPFX the effects run in `RenderPassId` order 128 to 135, so colour balance is first, vignette last. With CMAA2 anti-aliasing the pre-ImGui result is not
displayed (see [Choose between pre-ImGui (HDR scene) and post-ImGui (final frame) post-processing](../../post-processing/choose-pre-or-post-imgui-post-processing/overview.md)). `float4x4` rows arrive as GLSL `mat4` columns: `preData[0]` is C# row 0.

## Lifecycle and timing

The shaders run on the GPU in `Program.RenderGame` (`render:post-process`). The parameters are written by MPFX's `[StarMapAfterGui]` method (`frame:draw-ui`) before the passes record in the same frame; only when a setting changes. See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence) and [Send per-frame values and time to a shader with push constants, written at the right point in the frame](../../post-processing/send-push-constants-to-a-shaderextensions-shader/overview.md) for the write point.

## Steps

1. **Lift/gamma/gain from a `mat4`.** C# fills `float4x4` row 0 lift, row 1 gamma, row 2 gain, and `[3,3]` as an enable flag; the shader short-circuits when the flag is 0. It clamps to [0, 1] at the end, so it also clips HDR highlights on the pre side:

   ```glsl
   // MPFX/Shaders/ColorBalancePre.frag L21-L36
   void main()
   {
     vec3 c = texture(In, Uv).rgb;
     if (preData[3][3] == 0)
       Out = vec4(c, 1);
     else
     {
       c = (c + preData[0].rgb); // lift
       c = max(c, 0.0); // avoid negatives
       c = pow(c, 1.0 / preData[1].rgb); // gamma
       c *= preData[2].rgb; // gain
       c = min(c, 1.0);

       Out = vec4(c, 0.25);
     }
   }
   ```

   Source: [MPFX/Shaders/ColorBalancePre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/ColorBalancePre.frag#L21-L36). Gamma near 0 explodes; clamp the slider (MPFX's UI clamps to 0.01, typed input is not clamped).

2. **Colour temperature with luminance preservation.** A rational fit per channel gives an RGB multiplier for a Kelvin value (1000 to 40000); the result is rescaled so Rec. 709 luminance is unchanged, so temperature only tints. `0` means "off":

   ```glsl
   // MPFX/Shaders/ColorTempPre.frag L27-L56
   const float LuminancePreservationFactor = 1.0;

   const float PI2 = 6.2831853071;

   // Valid from 1000 to 40000 K (and additionally 0 for pure full white)
   vec3 colorTemperatureToRGB(const in float temperature){
     // Values from: http://blenderartists.org/forum/showthread.php?270332-OSL-Goodness&p=2268693&viewfull=1#post2268693   
     mat3 m = (temperature <= 6500.0) ? mat3(vec3(0.0, -2902.1955373783176, -8257.7997278925690),
                                             vec3(0.0, 1669.5803561666639, 2575.2827530017594),
                                             vec3(1.0, 1.3302673723350029, 1.8993753891711275)) : 
                                         mat3(vec3(1745.0425298314172, 1216.6168361476490, -8257.7997278925690),
                                                vec3(-2666.3474220535695, -2173.1012343082230, 2575.2827530017594),
                                             vec3(0.55995389139931482, 0.70381203140554553, 1.8993753891711275)); 
     return mix(clamp(vec3(m[0] / (vec3(clamp(temperature, 1000.0, 40000.0)) + m[1]) + m[2]), vec3(0.0), vec3(1.0)), vec3(1.0), smoothstep(1000.0, 0.0, temperature));
   }

   void main()
   {
     vec3 c = texture(In, Uv).rgb;

     // neutral is 6500K
     if (preTemp == 0)
       Out = vec4(c, 1);
     else
     {
       vec3 outCol = c * colorTemperatureToRGB(preTemp); 
       outCol *= mix(1.0, dot(c, vec3(0.2126, 0.7152, 0.0722)) / max(dot(outCol, vec3(0.2126, 0.7152, 0.0722)), 1e-5), LuminancePreservationFactor);
       Out = vec4(outCol, 1);
     }
   }
   ```

   Source: [MPFX/Shaders/ColorTempPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/ColorTempPre.frag#L27-L56) (temperature curve by Benjamin Rosseaux, CC0).

3. **Hue shift in YIQ.** Convert to YIQ, rotate the IQ vector by the hue offset, convert back; luma stays untouched. The C# side uploads radians (degrees `/ 180 * Math.PI`). The rounded constants make the zero rotation only approximately the identity:

   ```glsl
   // MPFX/Shaders/HueShiftPost.frag L20-L51
   // From: https://gist.github.com/mairod/a75e7b44f68110e1576d77419d608786
   vec3 hueShift( vec3 color, float hueAdjust )
   {
     const vec3  kRGBToYPrime = vec3 (0.299, 0.587, 0.114);
     const vec3  kRGBToI      = vec3 (0.596, -0.275, -0.321);
     const vec3  kRGBToQ      = vec3 (0.212, -0.523, 0.311);

     const vec3  kYIQToR     = vec3 (1.0, 0.956, 0.621);
     const vec3  kYIQToG     = vec3 (1.0, -0.272, -0.647);
     const vec3  kYIQToB     = vec3 (1.0, -1.107, 1.704);

     float   YPrime  = dot (color, kRGBToYPrime);
     float   I       = dot (color, kRGBToI);
     float   Q       = dot (color, kRGBToQ);
     float   hue     = atan (Q, I);
     float   chroma  = sqrt (I * I + Q * Q);

     hue += hueAdjust;

     Q = chroma * sin (hue);
     I = chroma * cos (hue);

     vec3    yIQ   = vec3 (YPrime, I, Q);

     return vec3( dot (yIQ, kYIQToR), dot (yIQ, kYIQToG), dot (yIQ, kYIQToB) );
   }

   void main()
   {
     vec4 c = texture(In, Uv);
     Out = vec4(hueShift(c.rgb, postAmount), 1);
   }
   ```

   Source: [MPFX/Shaders/HueShiftPost.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/HueShiftPost.frag#L20-L51) (hue shift by mairod). `atan(0, 0)` is undefined in GLSL, which is the gray-pixel case; a guard such as `chroma < 1e-6 ? color : ...` is safer.

4. **Contrast, saturation and overlay are one-liners with an identity value.** Contrast pivots on scene-linear mid-grey; saturation mixes toward luminance by `1 - k`:

   ```glsl
   // MPFX/Shaders/ContrastPre.frag L25-L35
   vec3 contrastHDR(vec3 color, float contrast)
   {
       const float midGray = 0.18;
       return midGray + contrast * (color - midGray);
   }

   void main()
   {
       vec4 c = texture(In, Uv);
       Out = vec4(contrastHDR(c.rgb, preAmount), 1);
   }
   ```

   ```glsl
   // MPFX/Shaders/SaturationPre.frag L25-L35
   vec3 saturate(vec3 color, float saturation)
   {
       float l = luminance(color);
       return mix(color, vec3(l), saturation);
   }

   void main()
   {
       vec4 c = texture(In, Uv);
       Out = vec4(saturate(c.rgb, 1 - preAmount), 1);
   }
   ```

   Sources: [MPFX/Shaders/ContrastPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/ContrastPre.frag#L25-L35), [MPFX/Shaders/SaturationPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/SaturationPre.frag#L25-L35). Overlay is `mix(c.rgb, color.rgb, color.a)` with `a = 0` as neutral ([MPFX/Shaders/ColorOverlayPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/ColorOverlayPre.frag)); brightness is a multiply by `k = 1`.

5. **Vignette that is a circle on any screen shape.** Normalise UV to [-1, 1], divide x by an aspect factor, remap the distance between inner and outer radius to [0, 1] and mix toward the vignette colour. `z == 0` is the neutral value:

   ```glsl
   // MPFX/Shaders/VignettePre.frag L21-L44
   void main()
   {
     vec4 c = texture(In, Uv);

     if (preData.z == 0)
     {
       Out = c;
     }
     else
     {
       vec2 uv2 = Uv;
       uv2 = (uv2 - 0.5) * 2; // normalize to [-1, 1]
       uv2.x = uv2.x / preData.z; // adjust for screen size

       float d = sqrt(uv2.x * uv2.x + uv2.y * uv2.y); // distance from center
       d = clamp(d, preData.y, preData.x);
       d = d - preData.y;
       d = d / (preData.x - preData.y);

       d = clamp(d, 0, 1);

       Out = vec4(mix(c.rgb, preColor.rgb, d * preColor.a), 1);
     }
   }
   ```

   Source: [MPFX/Shaders/VignettePre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/VignettePre.frag#L21-L44). The C# side writes `z = userAspect * viewport.Y / viewport.X`, so `userAspect = 1` is a circle ([MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L889-L896)); the parameter block is `vec4(outer, inner, aspect, 0)` plus a `vec4` colour.

6. **Write neutral values for disabled effects** (brightness, contrast, saturation 1; temperature, hue 0; overlay and vignette `float4.Zero`; colour balance a zero matrix) so every pass reduces to a copy; see [Send per-frame values and time to a shader with push constants, written at the right point in the frame](../../post-processing/send-push-constants-to-a-shaderextensions-shader/overview.md) article 2.

## Complete example

| File | Role |
|---|---|
| [MPFX/Shaders/ColorBalancePre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/ColorBalancePre.frag), [MPFX/Shaders/ColorTempPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/ColorTempPre.frag), [MPFX/Shaders/HueShiftPost.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/HueShiftPost.frag) | Lift/gamma/gain, temperature, hue |
| [MPFX/Shaders/ContrastPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/ContrastPre.frag), [MPFX/Shaders/SaturationPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/SaturationPre.frag), [MPFX/Shaders/VignettePre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/VignettePre.frag), [MPFX/Shaders/ColorOverlayPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/ColorOverlayPre.frag) | The rest |
| [MPFX/MPFXAssests.xml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXAssests.xml) | RenderPassId 128 to 135 |

## Variations and gotchas

- **HDR versus display.** Pre-side values above 1 are still HDR at passes 128 to 135 (except after colour balance, which clamps); on the post side values are already tonemapped. The same shader math behaves differently on the two sides.
- **Alpha.** `ColorBalancePre.frag` writes alpha 0.25 when enabled and every other shader writes 1; the effect on the composite is unverified.
- **The film grain pass at 136** clamps the HDR image to [0, 1] unconditionally, so everything after pass 136 sees at most 1 ([Add film grain and dither away banding](../add-film-grain-and-blue-noise-dithering/overview.md)).
- **Not visible with CMAA2** for the pre-ImGui halves.

## See also

- Sibling: [camera-feed look](colour-grade-the-frame-and-add-a-vignette-2.md); [overview](overview.md)
- [Color grading shader math](../../../../mods/mpfx/features/color-grading-math.md), [Shader contract for chain passes](../../../../mods/mpfx/features/effect-shader-contract.md)
- [Choose between pre-ImGui (HDR scene) and post-ImGui (final frame) post-processing](../../post-processing/choose-pre-or-post-imgui-post-processing/overview.md), [Feed a ShaderExtensions shader from C# with uniform buffers (std140)](../../post-processing/feed-uniform-buffers-to-a-shaderextensions-shader/overview.md)
