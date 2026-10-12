---
type: How-To
title: "Film grain: Ashima/Gustavson noise with soft-light blending on a push-constant clock"
description: Generate grain with a textureless simplex and periodic Perlin noise seeded by a push-constant time, blend it soft-light and fade it in bright areas, with size, strength and clock set per side
category: shaders
group: screen-effects
topic: add-film-grain-and-blue-noise-dithering
approach: 1
difficulty: intermediate
prerequisites: [starmap, shaderextensions, kittenextensions]
demonstrated_by: [mods/mpfx/features/film-grain-and-experimental-effects]
techniques: [glsl-shader, post-processing]
hooks: [xml-asset PostProcessingShader, starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, render:post-process]
game_types: [KSA.Program, KSA.Universe]
tags: [film-grain, simplex-noise, soft-light, push-constants, time-warp, post-processing, mpfx]
---
MPFX's film grain (`RenderPassId` 136) is the only MPFX effect that changes over time. Textureless simplex noise gives a different grain pattern every frame, a soft-light blend puts it on the picture, and a luminance response fades it out in bright areas. Size, intensity and the animation clock are per side (before and after the UI). This article is the recipe and the pieces to copy. You need ShaderExtensions and the parameter
plumbing from [Send per-frame values and time to a shader with push constants, written at the right point in the frame](../../post-processing/send-push-constants-to-a-shaderextensions-shader/overview.md).

## Background: how the game does it

Nothing in the base game provides grain. The noise comes from Stefan Gustavson's and Ian McEwan's textureless GLSL noise (credited in the shader header) (`snoise`, `pnoise`, copied into the shader), so there is no texture binding. The clock is a float push constant per side, advanced on the CPU, because grain should keep moving on pause and the shared `ShaderTime` block offers only wall time and simulation time. The pre-ImGui
variant runs on the HDR scene; the post variant on the display image.

## Lifecycle and timing

| Step | When | Value |
|---|---|---|
| Clocks advance, push constants written | `[StarMapAfterGui]` each frame the HUD is drawn | `frame:draw-ui` |
| Size and strength written to a shared buffer | Same hook, only when a setting changed | `frame:draw-ui` |
| Pass 136 records | `Program.RenderGame`; the push-constant value written this frame is the one used | `render:post-process` |

With the HUD hidden the clocks stop and the grain freezes in place, because the hook does not run. See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

1. **Declare the inputs.** One `vec4` buffer carries `data` (pre size, pre strength, post size, post strength) and `config` (renderer width and height); one float push constant per side carries the clock:

   ```glsl
   // MPFX/Shaders/FilmGrainPre.frag L15-L22
   layout(set = 1, binding = 2) uniform MPFXVec4BufferAsset {
     vec4 data; // preGrainSize, preGrainStrength, postGrainSize, postGrainStrength
     vec4 config; // width, height
   };

   layout(push_constant) uniform MPFXPushConstantsFloatAsset {
     float framenumPre;
   };
   ```

   Source: [MPFX/Shaders/FilmGrainPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/FilmGrainPre.frag#L15-L22). The XML gives the pre and post shaders each their own push-constant element and share the buffer ([MPFX/MPFXAssests.xml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXAssests.xml#L59-L66)).

2. **Generate the grain.** Use the simplex value as the third coordinate of periodic Perlin noise and remap to [0, 1]. A larger "grain size" divides the resolution and makes bigger grains:

   ```glsl
   // MPFX/Shaders/FilmGrainPre.frag L217-L230
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

   Source: [MPFX/Shaders/FilmGrainPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/FilmGrainPre.frag#L217-L230). `snoise` and `pnoise` are the Ashima/Gustavson implementations earlier in the same file (MIT, webgl-noise); copy them with their licence header.

3. **Blend soft-light, fade by luminance, mix by strength.** Soft light keeps the mid-tones; a smoothstep on luminance (0.05 to 2.0 on the HDR pre side) reduces grain in bright areas; strength 0 is the identity, and size 0 returns early:

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

   Source: [MPFX/Shaders/FilmGrainPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/FilmGrainPre.frag#L248-L274). The `clamp(..., 0, 1)` on the first line also caps the HDR image for every later pass (see below).

4. **Advance the clock on the CPU, per side.** `dt` scaled by a multiplier and, optionally, by `Universe.SimulationSpeed`; multiplier 0 freezes the grain:

   ```csharp
   // MPFX/MPFX.cs L128-L138
               if (MPFXPushConstantsFloatBuffer.LookupSpan != null)
               {
                   CurrentProfile.FilmGrainPreTime += (float)dt * CurrentProfile.FilmGrainPreTimeMultiplier * (CurrentProfile.FilmGrainPreTimeWarpMultiplier ? (float)Universe.SimulationSpeed : 1f);
                   CurrentProfile.FilmGrainPostTime += (float)dt * CurrentProfile.FilmGrainPostTimeMultiplier * (CurrentProfile.FilmGrainPostTimeWarpMultiplier ? (float)Universe.SimulationSpeed : 1f);

                   Span<MPFXPushConstantsFloatBuffer> FilmgrainPreTime = MPFXPushConstantsFloatBuffer.LookupSpan(KeyHash.Make("MPFXFilmgrainPreTimeBuffer"));
                   FilmgrainPreTime[0].a = CurrentProfile.FilmGrainPreTime;

                   Span<MPFXPushConstantsFloatBuffer> FilmgrainPostTime = MPFXPushConstantsFloatBuffer.LookupSpan(KeyHash.Make("MPFXFilmgrainPostTimeBuffer"));
                   FilmgrainPostTime[0].a = CurrentProfile.FilmGrainPostTime;
               }
   ```

   Source: [MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L128-L138). Write `float2.Zero` size/strength for a disabled side; the vec4 `config` holds `renderer.Extent` width and height.

## Complete example

| File | Role |
|---|---|
| [MPFX/Shaders/FilmGrainPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/FilmGrainPre.frag), [MPFX/Shaders/FilmGrainPost.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/FilmGrainPost.frag) | Noise library, grain, blend |
| [MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs) | Per-side clocks and the buffer writes |
| [MPFX/MPFXAssests.xml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXAssests.xml) | Elements for both sides |

## Variations and gotchas

- **Clamp side effect.** The pre-ImGui shader clamps its input to [0, 1] before checking whether grain is enabled; because every pass always runs, that caps the HDR image for passes 137 onward and for the tonemap composite whenever this pass exists (derived from the code, not tested). Move the clamp inside the enabled branch if you copy it.
- **Hidden HUD stops the clock.** Write from an `OnFrameEditor` postfix to keep animating with the HUD hidden.
- **Precision.** The clock accumulates in a `float` and never wraps.
- **The experimental passes** (RGB2HSV, HSV2RGB, single-colour variants, ids 137 to 141) live in the same file set; each ends in `mix(c, effect, amount)` so amount 0 is a pass-through.
- **Resolution changes.** The `config` vector is only refreshed when a setting changes.

## See also

- Sibling: [blue-noise texture](add-film-grain-and-blue-noise-dithering-2.md); [overview](overview.md)
- [Film grain and experimental effects](../../../../mods/mpfx/features/film-grain-and-experimental-effects.md)
- [Send per-frame values and time to a shader with push constants, written at the right point in the frame](../../post-processing/send-push-constants-to-a-shaderextensions-shader/overview.md), [Choose between pre-ImGui (HDR scene) and post-ImGui (final frame) post-processing](../../post-processing/choose-pre-or-post-imgui-post-processing/overview.md)
