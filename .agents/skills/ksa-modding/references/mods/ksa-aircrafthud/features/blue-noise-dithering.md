---
type: Mod Feature
title: Spatiotemporal blue-noise dithering
description: Samples a 64-slice spatiotemporal blue-noise (STBN) texture stacked into a 128x8192 PNG, picking the slice from a C#-supplied time value, to add per-pixel, per-frame grain that hides banding in the post-processed image
mod: ksa-aircrafthud
category: shaders
techniques: [glsl-shader, post-processing, vulkan-texture]
hooks: [harmony-postfix KSA.Program.OnFrameEditor]
lifecycle: [frame:viewports, render:post-process]
game_types: [KSA.Program]
tags: [shaders, blue-noise, stbn, dithering, film-grain, glsl, texture-binding, post-processing]
---

The last step of the canopy-glass composite adds animated grain from a blue-noise texture instead of
a hash function. Blue noise has little low-frequency energy, so the grain reads as fine and even
rather than blotchy, and because the slice changes over time the eye averages it out. The shader
comment calls it LDR dithering, but ShaderExtensions runs the pass on the game's HDR offscreen
image (`R16G16B16A16SFloat`) before the tonemap composite
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L223)), so it acts as
film grain that survives tonemapping, not as dithering against the final 8-bit quantization.[^ah-functions] [^ksa-program]

## How it works

- **Texture layout.** [Textures/stbn_vec3_128x128x64.png](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Textures/stbn_vec3_128x128x64.png) is a
  128x8192 RGB PNG: 64 tiles of 128x128 stacked vertically, one per time slice. The name follows the
  spatiotemporal blue noise (STBN) convention `stbn_<type>_<w>x<h>x<slices>`; the repo does not say
  where the texture came from. A grayscale twin,
  [Textures/stbn_scalar_128x128x64.png](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Textures/stbn_scalar_128x128x64.png), ships but is not
  referenced anywhere.[^ah-stbn-vec3] [^ah-stbn-scalar]
- **Binding.** `PostProcess` lists it as a `<TextureBinding>`; in v0.4.0 binding order it lands at
  `set 1, binding 2` as `noiseTex` ([HudAssests.xml](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/HudAssests.xml#L14-L19)). ShaderExtensions
  binds texture bindings with the game's linear clamped sampler
  ([ShaderExtensions/ShaderBinding.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderBinding.cs#L39-L53)), so the
  shader computes texel-center UVs itself to get exact texels.[^ah-assets] [^sx-binding]
- **Lookup.** `BlueNoiseDither` tiles the 128x128 pattern over the screen with `pixel & 127`, picks
  slice `f = int(frame * 60) & 63`, and addresses row `(pixel.y & 127) + f * 128` of the 8192-pixel
  column. The noise is centered (`- 0.5`), scaled by `str` and applied multiplicatively
  (`color += noise * color`), which behaves like film grain: stronger in bright areas, none in black
  ([Shaders/Functions.glsl](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/Functions.glsl#L22-L40)).
- **Time source.** `frame` is not a frame number: it is `FrameTime`, the sum of `dtPlayer` the mod
  accumulates in `DrawHud`, pushed through the `PPPushConstantsBuffer` push constant. `frame * 60`
  therefore advances one slice per 1/60 s of player time (wall-clock frame time, capped per frame by
  the minimum target frame rate), independent of frame rate
  ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1017-L1027)).[^ah-main]
- **Use.** `PostProcess.frag` calls `BlueNoiseDither(color, noiseTex, pp.frame, 0.25)` after both the
  enabled and disabled branches ([Shaders/PostProcess.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/PostProcess.frag#L50-L53)).[^ah-postprocess]

## Lifecycle and timing

- **`frame:viewports`.** `FrameTime += dt` and the push constant write happen in the
  `Program.OnFrameEditor` postfix, only on frames where the HUD is drawn.
- **`render:post-process`.** Sampling happens in the last pre-ImGui pass recorded by ShaderExtensions
  inside `Program.RenderGame`.

## Key code

| Path | Role |
|---|---|
| [Shaders/Functions.glsl](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/Functions.glsl#L22-L40) | `BlueNoiseDither` |
| [Shaders/Functions.glsl](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/Functions.glsl#L11-L20) | `ScreenNoise_DEPRECATED`, the earlier UV-tiled lookup |
| [Shaders/PostProcess.frag](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/Shaders/PostProcess.frag#L50-L53) | Call site |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1017) | Time accumulation |

## Snippets

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

## Game APIs touched

None directly. The texture is loaded and bound by ShaderExtensions' `TextureBindingReference`,
which reuses the game's texture loading and `Program.LinearClampedSampler`
([ShaderExtensions/ShaderBinding.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderBinding.cs#L39-L61)).

## Pitfalls and version notes

- `color += vec4(noise, 1) * color` also adds `color.a` to alpha (doubling it). Harmless for an
  opaque scene image, wrong if reused on a translucent target.
- With `str = 0.25` the grain is up to plus or minus 12.5 percent of the pixel value, applied even
  when the glass effect is off.
- Since the slice index comes from accumulated real time and the value is only updated while the
  HUD draws, the grain freezes on one slice whenever the HUD is hidden.
- The pinned ShaderExtensions provides a shared `ShaderTime` uniform (`FrameNumber`, `DeltaTime`,
  `RealTimeSinceStart`, ...) to every `ShaderEx` fragment shader, updated in a `Program.RenderGame`
  prefix ([ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L29-L43)). A port can take
  the slice from `Time.FrameNumber & 63` and drop the C# time push.[^sx-patches]

## See also

- [Canopy-glass post-process chain](glass-post-process-chain.md)
- [ShaderExtensions push constants and uniform buffers from C#](sx-push-constants-and-buffers.md)
- [Write a post-processing fragment shader: extra textures and the port from subpasses to dynamic rendering](../../../howto/shaders/post-processing/write-a-post-processing-fragment-shader/write-a-post-processing-fragment-shader-2.md) - how-to article demonstrating this feature
- [Dither with blue noise: a 128x128x64 STBN texture stacked in a 128x8192 PNG, slice picked from time](../../../howto/shaders/screen-effects/add-film-grain-and-blue-noise-dithering/add-film-grain-and-blue-noise-dithering-2.md) - how-to article demonstrating this feature

[^ah-functions]: KSA_AircraftHUD/Shaders/Functions.glsl
[^ah-postprocess]: KSA_AircraftHUD/Shaders/PostProcess.frag
[^ah-stbn-vec3]: KSA_AircraftHUD/Textures/stbn_vec3_128x128x64.png
[^ah-stbn-scalar]: KSA_AircraftHUD/Textures/stbn_scalar_128x128x64.png
[^ah-assets]: KSA_AircraftHUD/HudAssests.xml
[^ah-main]: KSA_AircraftHUD/AircraftHUDMain.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^sx-binding]: ShaderExtensions ShaderBinding.cs
[^sx-patches]: ShaderExtensions Patches.cs
