---
type: Game Feature
title: Blur the screen with a two-pass separable Gaussian
description: How to blur the whole frame with a horizontal and a vertical ShaderExtensions pass, whether the weights come from the CPU or from a fixed non-linear tap distribution, and how to keep a sharp copy for a composite
category: shaders
group: screen-effects
topic: blur-the-screen-with-a-separable-gaussian
demonstrated_by: [mods/mpfx/features/separable-gaussian-blur, mods/ksa-aircrafthud/features/glass-post-process-chain]
lifecycle: [frame:draw-ui, render:post-process]
game_types: [KSA.Rendering.RenderImage, KSA.Program]
tags: [blur, gaussian, separable, post-processing, weights, glass, shaderextensions]
---
A Gaussian blur is the textbook case for two fullscreen passes: blur horizontally into one render target, then vertically from that target. Each pass reads `2r + 1` texels instead of `(2r + 1)^2`. With ShaderExtensions the second pass simply samples the previous pass's output, so the chain is two XML elements with consecutive `RenderPassId`s. This topic shows two variants: weights computed
on the CPU and uploaded in a uniform block (a user-adjustable radius), and a fixed non-linear tap distribution baked into the shader that also bends the sample position (a glass effect).

## Engine background

- **Pass chaining.** Each ShaderExtensions pass gets its own render target and samples the previous pass's output at `set = 1, binding = 0`; the same `.frag` can serve both the pre-ImGui and post-ImGui chains ([README.md](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/README.md#L47-L68)). The details are in [Declare ShaderEx, PostProcessingShader, GlobalPostShader and ImGuiShader assets](../../post-processing/declare-post-processing-shader-assets-in-xml/overview.md).
- **Where to blur.** The pre-ImGui chain blurs the HDR scene before tonemapping and leaves the UI sharp; the post-ImGui chain blurs the finished frame, UI included ([Choose between pre-ImGui (HDR scene) and post-ImGui (final frame) post-processing](../../post-processing/choose-pre-or-post-imgui-post-processing/overview.md)).
- **The sampler.** Pass inputs are bound with `Program.LinearClampedSampler`, so clamping UVs half a texel inside the edge keeps border texels from bleeding.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [CPU-computed normalized weights up to radius 20 in a std140 block](blur-the-screen-with-a-separable-gaussian-1.md) | MPFX | `glsl-shader` | `frame:draw-ui`, `render:post-process` | Radius is a runtime setting; weights are computed once per change; sigma is tied to the radius |
| [Non-linear tap distribution and a refracted horizontal pass](blur-the-screen-with-a-separable-gaussian-2.md) | KSA_AircraftHUD | `glsl-shader` | `render:post-process` | No weights to upload; wide blur with few taps; the horizontal pass also refracts, and the composite needs the pre-blur scene by input reference; the mod's published XML is skipped by the pinned ShaderExtensions until ported |

## See also

- [Separable Gaussian blur with CPU-computed weights](../../../../mods/mpfx/features/separable-gaussian-blur.md), [Canopy-glass post-process chain](../../../../mods/ksa-aircrafthud/features/glass-post-process-chain.md)
- [Declare ShaderEx, PostProcessingShader, GlobalPostShader and ImGuiShader assets](../../post-processing/declare-post-processing-shader-assets-in-xml/overview.md), [Refract and distort the rendered scene, and flash or shake it](../refract-and-distort-the-scene/overview.md), [Add film grain and dither away banding](../add-film-grain-and-blue-noise-dithering/overview.md)
