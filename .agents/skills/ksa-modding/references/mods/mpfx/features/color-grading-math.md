---
type: Mod Feature
title: Color grading shader math
description: "Reusable grading formulas from MPFX's shaders: lift, gamma and gain in a mat4 with an on/off flag, a 1000 to 40000 K temperature tint with luminance preservation, YIQ hue rotation, mid-gray HDR contrast, Rec. 709 saturation, overlay and an aspect-corrected vignette"
mod: mpfx
category: shaders
techniques: [glsl-shader, post-processing]
hooks: [xml-asset PostProcessingShader / GlobalPostShader (ShaderExtensions)]
lifecycle: [render:post-process]
game_types: [KSA.Program, Brutal.Numerics.float4x4]
tags: [shaders, glsl, color-grading, lift-gamma-gain, color-temperature, hue-shift, vignette, hdr]
---

MPFX's grading shaders are short, self-contained and mostly lifted from well-known public sources. The
temperature curve is credited to Benjamin Rosseaux (CC0, shadertoy 4sc3D7), and the hue shift to a
GitHub gist by mairod. Each one is a drop-in function for any ShaderExtensions pass. The C# side
does the unit conversions (degrees to radians, aspect correction) so that each shader is a single
expression.[^mpfx-sh-colortemppre] [^mpfx-sh-hueshiftpost]

## How it works

| Effect | Formula (per pixel `c`) | Neutral | Parameters from C# |
|---|---|---|---|
| Lift/gamma/gain | `c = max(c + lift, 0); c = pow(c, 1 / gamma); c *= gain; c = min(c, 1)` | `[3][3] == 0` short-circuits | `mat4`: column 0 lift, 1 gamma, 2 gain (C# rows 0-2), `[3][3]` enable |
| Color temperature | `c * K(T)`, rescaled so Rec. 709 luminance is unchanged | `T == 0` | Kelvin, 1000-40000 |
| Hue shift | RGB to YIQ, rotate the IQ vector by `θ`, back to RGB | `θ = 0` (approximately identity) | Radians (UI degrees `/ 180 * π`) |
| Brightness | `c * k` | `k = 1` | Multiplier 0.01-4 |
| Contrast | `0.18 + k * (c - 0.18)` (`contrastHDR`; an unused `contrastSDR` pivots at 0.5) | `k = 1` | 0.01-4 |
| Saturation | `mix(c, vec3(dot(c, (0.2126, 0.7152, 0.0722))), 1 - k)` | `k = 1` | 0.01-4 |
| Color overlay | `mix(c, color.rgb, color.a)` | `a = 0` | `vec4` from `ColorPicker4` |
| Vignette | `d = length(((uv - 0.5) * 2) / (aspect, 1))`, remapped from [inner, outer] to [0, 1], then `mix(c, color.rgb, d * color.a)` | `z == 0` | `vec4(outer, inner, aspect, 0)` + `vec4` color |

Notes on the math:

- **Lift/gamma/gain in one `mat4`.** C# fills a `float4x4` row by row: row 0 lift, row 1 gamma, row 2
  gain, and `W.W` is the enable flag (comment: "last value used as on/off toggle"). Because
  `float4x4` stores rows in sequence and GLSL `mat4` is column-major, the shader reads them as
  `preData[0].rgb`, `preData[1].rgb`, `preData[2].rgb` and `preData[3][3]`
  ([current/decomp/Brutal.Numerics/float4x4.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Numerics/float4x4.cs#L422-L428)). Pre and post are the two
  `mat4`s of one buffer.[^ksa-float4x4] [^mpfx-sh-colorbalancepre]
- **Temperature.** `colorTemperatureToRGB` evaluates a rational fit `m[0] / (T + m[1]) + m[2]` per
  channel, with one coefficient set at or below 6500 K and another above. It clamps to [0, 1] and fades
  to white below 1000 K. The result multiplies the color, and `LuminancePreservationFactor = 1.0`
  rescales it back to the input luminance. Temperature only tints, it never darkens.
- **YIQ hue.** `atan(Q, I)` gives the hue angle and `sqrt(I² + Q²)` the chroma. Adding the offset and
  converting back leaves luma (Y) untouched. The forward and inverse constants are rounded to three
  decimals, so the round trip with `θ = 0` is only approximately the identity.
- **Contrast pivot.** The pivot at 0.18 is scene-linear mid-gray, which suits the pre-ImGui HDR input.
  The post shader uses the same function on display values.
- **Vignette aspect.** The UI writes `z = userAspect * viewport.Y / viewport.X`, and the shader divides
  `uv.x` by it, so `userAspect = 1` gives a circle on any screen shape
  ([MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L889-L896)).[^mpfx-main]

## Lifecycle and timing

All of this runs on the GPU inside ShaderExtensions' passes in `Program.RenderGame` (`render:post-process`).
Effects at `RenderPassId` 128-135 run in that order, both before ImGui (on the `R16G16B16A16SFloat`
offscreen image, before `RenderFinalComposite` tonemaps it) and after ImGui (on the swapchain image)
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4948-L4951)). The same math therefore behaves differently on the two
sides. Pre-side values above 1 are still HDR at passes 128 to 135 (except after color balance, which
clamps with `min(c, 1.0)`); the film grain pass at 136 then clamps to [0, 1] every frame. Post-side
values are already tonemapped. With CMAA2 anti-aliasing the pre-side result is not displayed (see
[Effect chain declared as RenderPassId-ordered post-process assets](effect-chain-assets.md)).[^ksa-program]

## Key code

| Path | Role |
|---|---|
| [MPFX/Shaders/ColorBalancePre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/ColorBalancePre.frag) | Lift/gamma/gain from a `mat4` |
| [MPFX/Shaders/ColorTempPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/ColorTempPre.frag) | Kelvin tint with luminance preservation |
| [MPFX/Shaders/HueShiftPost.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/HueShiftPost.frag) | YIQ hue rotation |
| [MPFX/Shaders/ContrastPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/ContrastPre.frag), [MPFX/Shaders/SaturationPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/SaturationPre.frag) | Contrast and saturation |
| [MPFX/Shaders/VignettePre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/VignettePre.frag), [MPFX/Shaders/ColorOverlayPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/ColorOverlayPre.frag) | Vignette and overlay |

## Snippets

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

```glsl
// MPFX/Shaders/VignettePre.frag L20-L44

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

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `Brutal.Numerics.float4x4` constructor and `[row, column]` indexer | [current/decomp/Brutal.Numerics/float4x4.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Numerics/float4x4.cs#L88-L107) | Packing lift/gamma/gain rows for the shader |
| `Program.ColorFormat` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L223) | Explains why the pre side sees HDR values |

## Pitfalls and version notes

- GLSL leaves `atan(y, x)` undefined when both arguments are 0, which is exactly the case for gray
  pixels in the hue shift. MPFX multiplies the result by a zero chroma, which only helps if the driver
  returns a finite angle. A guard such as `chroma < 1e-6 ? color : ...` is safer.
- `ColorBalancePre.frag` writes alpha 0.25 when enabled, and every other shader writes 1. The effect on
  the composite is unverified.
- The color balance clamp `min(c, 1.0)` also clips HDR highlights on the pre side, before tonemapping.
- `pow(c, 1.0 / gamma)` with gamma near 0 explodes. The UI clamps the slider to 0.01, but typed input
  through the row's `InputFloat` is not clamped.

## See also

- [Shader contract for chain passes](effect-shader-contract.md)
- [Separable Gaussian blur with CPU-computed weights](separable-gaussian-blur.md)
- [Film grain and experimental effects](film-grain-and-experimental-effects.md)
- [Feed a shader with uniform buffers: Pack=1 structs with manual std140 padding, one block per effect](../../../howto/shaders/post-processing/feed-uniform-buffers-to-a-shaderextensions-shader/feed-uniform-buffers-to-a-shaderextensions-shader-1.md) - how-to article demonstrating this feature
- [Colour-grade the frame: pre-ImGui grading passes on the HDR scene](../../../howto/shaders/screen-effects/colour-grade-the-frame-and-add-a-vignette/colour-grade-the-frame-and-add-a-vignette-1.md) - how-to article demonstrating this feature

[^mpfx-sh-colortemppre]: MPFX/Shaders/ColorTempPre.frag
[^mpfx-sh-hueshiftpost]: MPFX/Shaders/HueShiftPost.frag
[^ksa-float4x4]: Brutal.Numerics.float4x4 (decompiled, build 2026.10.10.5554)
[^mpfx-sh-colorbalancepre]: MPFX/Shaders/ColorBalancePre.frag
[^mpfx-main]: MPFX/MPFX.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
