---
type: Mod Feature
title: Film grain and experimental effects
description: Animated film grain driven by a per-side push-constant clock that can follow time warp, built on Ashima/Gustavson GLSL noise with soft-light blending and luminance falloff, plus the self-described experimental RGB2HSV, HSV2RGB and SingleColor passes kept under a Fun stuff header
mod: mpfx
category: shaders
techniques: [glsl-shader, post-processing, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, xml-asset PostProcessingShader / GlobalPostShader (ShaderExtensions)]
lifecycle: [frame:draw-ui, render:post-process]
game_types: [KSA.Universe, Core.Renderer]
tags: [shaders, glsl, film-grain, noise, push-constants, hsv, experimental, time-warp]
---

Film grain is the only MPFX effect that changes over time, and the only one that uses a push constant.
Next to it the repo carries five passes that the README puts in parentheses: "just some stuff I wanted
to try during development, they are not meant for real use but I wanted to include them anyway". The
window groups them under a "Fun stuff" collapsing header.[^mpfx-readme]

## How it works

### Film grain (`RenderPassId` 136)

1. **Inputs.** The shared `MPFXFilmGrainBuffer` (`vec4 data` = pre size, pre strength, post size, post
   strength; `vec4 config` = renderer width, height) feeds both sides. Each side has its own push
   constant block with one float, the side's clock ([MPFX/Shaders/FilmGrainPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/FilmGrainPre.frag#L15-L22),
   [MPFX/MPFXAssests.xml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXAssests.xml#L59-L66)).[^mpfx-sh-filmgrainpre] [^mpfx-assets]
2. **Clock.** Each frame, `AfterGui` adds `dt * multiplier * (warp ? Universe.SimulationSpeed : 1)` to
   the side's time and writes it through the push-constant span ([MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L128-L138)).
   Multiplier 0 freezes the grain. With time-warp scaling on, the grain speeds up with the simulation.[^mpfx-main]
3. **Noise.** `grain(uv, resolution / size, time)` evaluates Ashima/Gustavson simplex noise (`snoise`,
   MIT, webgl-noise) at `(uv * res / 2.5, time)`. It uses that as the third coordinate of a periodic
   Perlin noise `pnoise` and remaps the result to [0, 1]. A larger "Grainsize" divides the resolution
   and makes bigger grains.
4. **Blend.** A soft-light blend of the grain over the color is faded out in bright areas by
   `smoothstep(0.05, 2.0, luma)²` on the pre side (HDR headroom) and `smoothstep(0.05, 1.0, luma)²` on the
   post side, then mixed by "Intensity" ([MPFX/Shaders/FilmGrainPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/FilmGrainPre.frag#L248-L274)).
5. **Off switch.** Grain size 0 (`data.x` pre, `data.z` post) returns early. C# writes `float2.Zero` for
   a disabled side.

### Experimental passes (`RenderPassId` 137-141)

| Pass | What it does | Parameter |
|---|---|---|
| RGB2HSV | Converts RGB to HSV and shows the HSV triple as RGB | Blend 0-1 |
| HSV2RGB | Treats the input RGB as an HSV triple and converts it to RGB | Blend 0-1 |
| SingleColor | Keeps only the largest channel (or the tied channels) of each pixel | Blend 0-1 |
| SingleColorSmallest | Drops the largest channel and keeps the others | Blend 0-1 |
| SingleColorSolid | Like SingleColor but sets the kept channels to 1 | Blend 0-1 |

The HSV pair uses the common branchless `rgb2hsv`/`hsv2rgb` functions. On the pre side they clamp the
input to [0, 1] first ("HDR is used until imgui") ([MPFX/Shaders/HSV2RGBEffectsPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/HSV2RGBEffectsPre.frag#L20-L48)).
Each pass ends with `mix(c, effect, amount)`, so amount 0 is a pass-through.[^mpfx-sh-hsv2rgbeffectspre]

## Lifecycle and timing

- **`frame:draw-ui`.** The film grain clocks advance in `[StarMapAfterGui]`, which only runs while the HUD
  is shown. With the HUD hidden, the grain stops animating but stays visible, because the buffers keep
  their values and the passes keep running.
- **`render:post-process`.** ShaderExtensions records each pass's push constant from the value MPFX
  wrote earlier in the same frame, so the grain advances exactly once per rendered frame.
- `Universe.SimulationSpeed` is read on the main thread during UI drawing. It reflects the warp rate
  the player currently has selected ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L122-L131)).[^ksa-universe]

## Key code

| Path | Role |
|---|---|
| [MPFX/Shaders/FilmGrainPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/FilmGrainPre.frag) | Noise library, grain, soft light, luminance response |
| [MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L128-L138) | Per-side grain clocks |
| [MPFX/Shaders/SingleColorPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/SingleColorPre.frag), [MPFX/Shaders/SingleColorSmallestPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/SingleColorSmallestPre.frag), [MPFX/Shaders/SingleColorSolidPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/SingleColorSolidPre.frag) | Channel-isolation experiments |
| [MPFX/Shaders/RGB2HSVEffectsPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/RGB2HSVEffectsPre.frag) | Color-space visualization |

## Snippets

```glsl
// MPFX/Shaders/FilmGrainPre.frag L217-L231
float grain(vec2 texCoord, vec2 resolution, float frame, float multiplier) {
  vec2 mult = texCoord * resolution;
  float offset = snoise(vec3(mult / multiplier, frame));
  float n1 = pnoise(vec3(mult, offset), vec3(1.0/texCoord * resolution, 1.0));
  return n1 / 2.0 + 0.5;
}

float grain(vec2 texCoord, vec2 resolution, float frame) {
  return grain(texCoord, resolution, frame, 2.5);
}

float grain(vec2 texCoord, vec2 resolution) {
  return grain(texCoord, resolution, 0.0);
}
```

```glsl
// MPFX/Shaders/FilmGrainPre.frag L248-L274
void main()
{
  vec3 c = clamp(texture(In, Uv).rgb, 0, 1);

  if (data.x == 0)
  {
    Out = vec4(c, 1);
    return;
  }

  vec3 g = vec3(grain(Uv, config.xy / data.x, framenumPre));

  //blend the noise over the background, 
  //i.e. overlay, soft light, additive
  vec3 color = blendSoftLight(c, g);

  //get the luminance of the background
  float luminance = luma(c);

  //reduce the noise based on some 
  //threshold of the background luminance
  float response = smoothstep(0.05, 2.0, luminance);
  color = mix(color, c, pow(response, 2.0));
  color = mix(c, color, data.y);

  Out = vec4(color, 1.0);
}
```

```glsl
// MPFX/Shaders/HSV2RGBEffectsPre.frag L20-L48
// All components are in the range [0…1], including hue.
vec3 rgb2hsv(vec3 c)
{
    vec4 K = vec4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
    vec4 p = mix(vec4(c.bg, K.wz), vec4(c.gb, K.xy), step(c.b, c.g));
    vec4 q = mix(vec4(p.xyw, c.r), vec4(c.r, p.yzx), step(p.x, c.r));

    float d = q.x - min(q.w, q.y);
    float e = 1.0e-10;
    return vec3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
}

// All components are in the range [0…1], including hue.
vec3 hsv2rgb(vec3 c)
{
    vec4 K = vec4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
    vec3 p = abs(fract(c.xxx + K.xyz) * 6.0 - K.www);
    return c.z * mix(K.xxx, clamp(p - K.xxx, 0.0, 1.0), c.y);
}

void main()
{
    vec4 c = texture(In, Uv);

    vec3 col = vec3(clamp(c.r, 0.0, 1.0), clamp(c.g, 0.0, 1.0), clamp(c.b, 0.0, 1.0));

    vec3 color = hsv2rgb(col); // HDR is used until imgui
    Out = vec4(mix(c.rgb, color, preAmount), 1);
}
```

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `Universe.SimulationSpeed` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L122-L131) | Optional time-warp scaling of the grain clock |
| `Renderer.Extent` | via `Program.GetRenderer()` | Grain resolution in `config.xy` |

## Pitfalls and version notes

- `FilmGrainPre.frag` clamps the input to [0, 1] before the enable check, so the pre-ImGui grain pass
  clips HDR values even when grain is off. The pass always runs, so every later pre pass and the
  tonemap composite see the clipped image (with CMAA2 anti-aliasing the pre chain is not displayed at
  all; see [Effect chain declared as RenderPassId-ordered post-process assets](effect-chain-assets.md)).
  This conclusion comes from the code, not from a runtime test.
- The clocks are fields of the profile (`[XmlIgnore]`), so switching profiles switches clocks, and the
  copy constructor does not copy them: a duplicated profile's grain restarts from time 0
  ([MPFX/MPFXProfile.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXProfile.cs#L147-L158)).[^mpfx-profile]
- `SingleColorPre.frag` and `SingleColorPost.frag` write `c.g` into blue in the "blue is largest" branch
  ([MPFX/Shaders/SingleColorPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/SingleColorPre.frag#L72-L75)). The Solid variant writes 1 there and is unaffected.[^mpfx-sh-singlecolorpre]
- Grain resolution is uploaded only when a setting changes, so after a window resize it uses the old
  size until the next edit.

## See also

- [Per-frame effect parameter upload](per-frame-parameter-upload.md)
- [Color grading shader math](color-grading-math.md)
- [Shader contract for chain passes](effect-shader-contract.md)
- [Push constants for a shader: per-side clock and strengths in a settings-driven mod](../../../howto/shaders/post-processing/send-push-constants-to-a-shaderextensions-shader/send-push-constants-to-a-shaderextensions-shader-2.md) - how-to article demonstrating this feature
- [Film grain: Ashima/Gustavson noise with soft-light blending on a push-constant clock](../../../howto/shaders/screen-effects/add-film-grain-and-blue-noise-dithering/add-film-grain-and-blue-noise-dithering-1.md) - how-to article demonstrating this feature

[^mpfx-readme]: MPFX README.md
[^mpfx-sh-filmgrainpre]: MPFX/Shaders/FilmGrainPre.frag
[^mpfx-assets]: MPFX/MPFXAssests.xml
[^mpfx-main]: MPFX/MPFX.cs
[^mpfx-sh-hsv2rgbeffectspre]: MPFX/Shaders/HSV2RGBEffectsPre.frag
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^mpfx-sh-singlecolorpre]: MPFX/Shaders/SingleColorPre.frag
[^mpfx-profile]: MPFX/MPFXProfile.cs
