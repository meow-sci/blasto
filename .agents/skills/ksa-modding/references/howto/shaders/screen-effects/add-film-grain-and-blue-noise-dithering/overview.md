---
type: Game Feature
title: Add film grain and dither away banding
description: Two ways to add noise to the final image in KSA, procedural simplex grain blended soft-light with a push-constant clock, and a spatiotemporal blue-noise texture indexed by pixel and time slice
category: shaders
group: screen-effects
topic: add-film-grain-and-blue-noise-dithering
demonstrated_by:
  - mods/mpfx/features/film-grain-and-experimental-effects
  - mods/ksa-aircrafthud/features/blue-noise-dithering
lifecycle: [frame:draw-ui, frame:viewports, render:post-process]
game_types: [KSA.Program, KSA.Universe]
tags: [film-grain, dithering, blue-noise, noise, banding, post-processing, push-constants, hdr]
---
Noise on the final image does two jobs: it gives the picture a filmic texture, and it hides banding in smooth gradients (a darkened sky, a heavy blur). KSA has no grain pass, so mods add one as a ShaderExtensions pass. This topic compares a procedural approach (simplex noise, no texture) with a texture approach (spatiotemporal blue noise indexed by integer pixel
and a time slice), and covers the clock each uses.

## Engine background

- **Where the picture is quantised.** The scene is RGBA16F HDR; the final composite tonemaps it and the swapchain image is 8-bit, so banding appears at the last step. A pre-ImGui pass (HDR) adds noise that the tonemap then maps; a post-ImGui pass adds it to display values and covers the UI ([Choose between pre-ImGui (HDR scene) and post-ImGui (final frame) post-processing](../../post-processing/choose-pre-or-post-imgui-post-processing/overview.md)).
- **Clocks.** `Program.FrameNumber` is a `ulong` incremented at the end of `OnFrame` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2332)); `Universe.SimulationSpeed` is the warp rate ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L122)). ShaderExtensions also exposes `ShaderTime` ([Send per-frame values and time to a shader with push constants, written at the right point in the frame](../../post-processing/send-push-constants-to-a-shaderextensions-shader/overview.md)).
- **Texture sampling.** ShaderExtensions binds textures with the game's linear clamped sampler, so exact texel lookups need texel-centre UVs.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Ashima/Gustavson noise with soft-light blending on a push-constant clock](add-film-grain-and-blue-noise-dithering-1.md) | MPFX | `glsl-shader` | `frame:draw-ui`, `render:post-process` | No texture; tunable size, strength and clock per side; the noise function is long and the pre side clamps HDR |
| [128x128x64 STBN texture stacked in a 128x8192 PNG](add-film-grain-and-blue-noise-dithering-2.md) | KSA_AircraftHUD | `glsl-shader` | `frame:viewports`, `render:post-process` | Even, fine grain from one texture lookup; needs a texture asset and an exact-texel lookup; the mod's published XML is skipped by the pinned ShaderExtensions until ported |

## See also

- [Film grain and experimental effects](../../../../mods/mpfx/features/film-grain-and-experimental-effects.md), [Spatiotemporal blue-noise dithering](../../../../mods/ksa-aircrafthud/features/blue-noise-dithering.md)
- [Send per-frame values and time to a shader with push constants, written at the right point in the frame](../../post-processing/send-push-constants-to-a-shaderextensions-shader/overview.md), [Write a fullscreen post-processing fragment shader for KSA](../../post-processing/write-a-post-processing-fragment-shader/overview.md), [Blur the screen with a two-pass separable Gaussian](../blur-the-screen-with-a-separable-gaussian/overview.md), [Declare ShaderEx, PostProcessingShader, GlobalPostShader and ImGuiShader assets](../../post-processing/declare-post-processing-shader-assets-in-xml/overview.md)
