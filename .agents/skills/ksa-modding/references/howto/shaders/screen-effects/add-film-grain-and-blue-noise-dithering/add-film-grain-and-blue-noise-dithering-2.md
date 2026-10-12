---
type: How-To
title: "Dither with blue noise: a 128x128x64 STBN texture stacked in a 128x8192 PNG, slice picked from time"
description: Index a stacked spatiotemporal blue-noise texture by integer pixel coordinates and a time-selected slice with texel-centre UVs, and apply it multiplicatively as fine, even grain
category: shaders
group: screen-effects
topic: add-film-grain-and-blue-noise-dithering
approach: 2
difficulty: intermediate
prerequisites: [starmap, shaderextensions, kittenextensions]
demonstrated_by: [mods/ksa-aircrafthud/features/blue-noise-dithering]
techniques: [glsl-shader, post-processing]
hooks: [xml-asset TextureBinding, harmony-postfix KSA.Program.OnFrameEditor]
lifecycle: [frame:viewports, render:post-process]
game_types: [KSA.Program]
tags: [blue-noise, dithering, stbn, grain, texture, banding, aircrafthud]
---
Blue noise has little low-frequency energy, so grain made from it reads as fine and even rather than blotchy, and when the slice changes over time the eye averages it out. KSA_AircraftHUD applies spatiotemporal blue noise as the last step of its glass composite: a 128x128x64 texture stacked in a 128x8192 PNG, indexed by the integer pixel coordinate and a slice chosen from time. This article is the lookup and
its traps. The mod's published asset XML needs the port described in [Write a fullscreen post-processing fragment shader for KSA](../../post-processing/write-a-post-processing-fragment-shader/overview.md); the lookup code itself is current.

## Background: how the game does it

ShaderExtensions binds `<TextureBinding>` textures with the game's linear clamped sampler ([ShaderExtensions/ShaderBinding.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderBinding.cs#L45-L53)), so a lookup that wants exact texels computes texel-centre UVs itself. The shader comment calls this LDR dithering, but ShaderExtensions runs the pass on the game's RGBA16F scene before the tonemap composite, so in practice it acts as film grain that survives tonemapping, not as dithering against the final 8-bit quantisation. To
dither the 8-bit output, put the same lookup in a post-ImGui pass ([Choose between pre-ImGui (HDR scene) and post-ImGui (final frame) post-processing](../../post-processing/choose-pre-or-post-imgui-post-processing/overview.md)).

## Lifecycle and timing

| Step | When | Value |
|---|---|---|
| Time accumulated and the push constant written | The mod's `Program.OnFrameEditor` postfix, inside `DrawHud`, after its early returns (mod HUD disabled, map or free camera, no controlled vehicle) | `frame:viewports` |
| Texture sampled | The last pre-ImGui pass recorded by ShaderExtensions inside `Program.RenderGame` | `render:post-process` |

The postfix itself runs every frame, even with the game UI hidden, but the slice index comes from accumulated player time that `DrawHud` advances only after its early returns, so the grain freezes on one slice whenever `DrawHud` bails out. See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

1. **Bind the texture as an extra sampler** after the fixed bindings. The 128x8192 PNG stacks 64 tiles of 128x128 vertically, one per time slice; the name follows the STBN convention `stbn_<type>_<w>x<h>x<slices>`:

   ```xml
   <!-- HudAssests.xml L14-L19 -->
   	<PostProcessingShader Id="PostProcess" Path="Shaders/PostProcess.frag" RenderPassId="64" SubpassId="0" >
   		<PPPushConstantsBufferAsset Id="PPPushConstantsBuffer2" Size="1" />
   		<PostProcessingInputReference RenderPassId="62" SubpassId="0" />
   		<TextureBinding Path="Textures/stbn_vec3_128x128x64.png" />
   		<TextureBinding Path="Textures/DirtMask.png" />
   	</PostProcessingShader>
   ```

   Source: [HudAssests.xml](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/HudAssests.xml#L14-L19). On the pinned ShaderExtensions the texture is binding 3 after the reference (binding numbers follow document order from 2); in the mod's v0.4.0 numbering it was binding 2 as `noiseTex`. A grayscale twin `stbn_scalar_128x128x64.png` ships but is unused.

2. **Look the noise up by integer pixel and slice with texel-centre UVs:**

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

   Source: [Shaders/Functions.glsl](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/Functions.glsl#L22-L40). `pixel & 127` tiles the 128x128 pattern over the screen, `f` picks the slice (row `(pixel.y & 127) + f * 128` of the 8192-pixel column), the noise is centred (`- 0.5`), scaled by `str` and applied multiplicatively (`color += noise * color`), which behaves like film grain: stronger in bright areas, none in black.

3. **Choose the time source.** The mod pushes `FrameTime`, the sum of `dtPlayer` it accumulates, in a push constant; `frame * 60` advances one slice per 1/60 s of player time, independent of frame rate. The call site runs after both branches of the composite, so the "effect off" state still adds grain:

   ```glsl
   // Shaders/PostProcess.frag L50-L53
   	// RGB Blue Noise (STBN) LDR Dithering
   	color = BlueNoiseDither(color, noiseTex, pp.frame, 0.25);

   	outColor = color;
   ```

   Source: [Shaders/PostProcess.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/PostProcess.frag#L50-L53). A port can drop the C# time push and take the slice from the shared block instead (adapted, not in the mod):

   ```glsl
   // adapted from Shaders/Functions.glsl L26: slice from the ShaderTime block (set 1, binding 1)
   int f = int(Time.FrameNumber) & 63;
   ```

   One slice per rendered frame; if you want a fixed rate regardless of frame rate, keep a time-based index.

## Complete example

| File | Role |
|---|---|
| [Shaders/Functions.glsl](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/Functions.glsl) | `BlueNoiseDither` |
| [Shaders/PostProcess.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/PostProcess.frag) | The call site |
| [HudAssests.xml](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/HudAssests.xml) | The texture binding |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs) | `FrameTime` accumulation and the push write |

## Variations and gotchas

- **Alpha.** `color += vec4(noise, 1) * color` also adds `color.a` to alpha (doubling it): harmless for an opaque scene image, wrong on a translucent target.
- **Strength.** With `str = 0.25` the grain is up to plus or minus 12.5 percent of the pixel value, applied even when the glass effect is off.
- **Mip and filtering.** The texture sampler is linear-clamped; exact texel centres avoid blending neighbours. The PNG must keep its 128x8192 shape.
- **File name case.** The XML asks for `DirtMask.png`; the file is `DirtMask.PNG`, which matters on case-sensitive file systems.
- **Time source.** The accumulated time freezes whenever `DrawHud` returns early (mod HUD disabled, map or free camera, no controlled vehicle), although the `OnFrameEditor` postfix keeps running with the game UI hidden. Use `Time.FrameNumber`, or accumulate before the early returns.

## See also

- Sibling: [procedural grain](add-film-grain-and-blue-noise-dithering-1.md); [overview](overview.md)
- [Spatiotemporal blue-noise dithering](../../../../mods/ksa-aircrafthud/features/blue-noise-dithering.md)
- [Send per-frame values and time to a shader with push constants, written at the right point in the frame](../../post-processing/send-push-constants-to-a-shaderextensions-shader/overview.md), [Blur the screen with a two-pass separable Gaussian](../blur-the-screen-with-a-separable-gaussian/overview.md)
