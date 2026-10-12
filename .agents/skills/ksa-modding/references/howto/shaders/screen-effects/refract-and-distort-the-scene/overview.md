---
type: Game Feature
title: Refract and distort the rendered scene, and flash or shake it
description: Fake glass, lenses and shock rings by sampling the scene at an offset, in a fragment pass with a normal mask or in compute passes that copy the HDR scene and write it back displaced, plus whiteouts and view shake without touching the camera
category: shaders
group: screen-effects
topic: refract-and-distort-the-scene
demonstrated_by:
  - mods/ksa-aircrafthud/features/glass-post-process-chain
  - mods/ksarmory/features/shock-front-refraction
  - mods/ksarmory/features/burst-flash-and-view-shake
lifecycle: [render:post-process, render:bloom, frame:draw-ui, frame:after-on-frame]
game_types:
  - KSA.Program
  - KSA.SunbloomRenderer
  - KSA.Rendering.ComputePipelineWrapper
  - KSA.Rendering.RenderImage
  - RenderCore.ReverseDepthBufferUtils
  - KSA.ScreenshotCapture
  - KSA.IGameViewport
  - KSA.GpuRegionExtensions
tags: [refraction, distortion, shock-wave, whiteout, view-shake, compute, hdr, post-processing]
---
Refraction in a screen-space effect is one idea: read the scene at a position that differs from the pixel being written. A normal map offsets the lookup to fake dirty glass; a lens function offsets it along a ring to fake a shock wave; a global shift, rotation and slight zoom fakes a camera shake. This topic shows three ways to do it in KSA: in a ShaderExtensions fragment pass,
and in two compute passes on the HDR scene that also flash and shake the view without moving the camera.

## Engine background

- **Two homes for the work.** A ShaderExtensions pass reads the previous pass's image through a sampler and writes a new target ([Write a fullscreen post-processing fragment shader for KSA](../../post-processing/write-a-post-processing-fragment-shader/overview.md)). A compute pass recorded from a `SunbloomRenderer.Render` prefix gets storage access to the viewport's HDR colour image and a sampleable depth image before bloom and tonemap
  ([current/decomp/KSA/SunbloomRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SunbloomRenderer.cs#L326)); the result is exposed like the rest of the frame.
- **Reverse-Z depth.** KSA's projection is reverse-Z (`CreatePerspectiveFieldOfViewReverseZ`), so device depth 1 is the near plane and 0 is the far plane and what untouched sky reads as ([current/decomp/RenderCore/ReverseDepthBufferUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ReverseDepthBufferUtils.cs#L8)).
- **You cannot read and write the same storage pixels safely.** A displacement pass therefore copies the scene into a private storage image first, then writes each pixel back from the copy.
- **UI is not part of the scene image.** `Program.OnFrame` guards the ImGui pass with `if (DrawUI)` and `ScreenshotCapture` clears that flag, so an effect that must survive screenshots is written into the scene rather than drawn as ImGui ([current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L225-L233)).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Normal-mask UV offsets and luminance-gated dirt in a fragment pass](refract-and-distort-the-scene-1.md) | KSA_AircraftHUD | `glsl-shader` | `render:post-process` | Cheapest and simplest; a static mask texture, no depth or geometry knowledge; the mod's published XML is skipped by the pinned ShaderExtensions until ported |
| [Scene copy then displaced write-back in two compute dispatches](refract-and-distort-the-scene-2.md) | KSArmory | `glsl-shader` | `render:bloom` | Analytic lens where the view ray grazes a sphere; depth-aware; needs a compute pipeline, a scene copy and barriers |
| [Flash-only mode of the cloud shader plus a shock-pass shake](refract-and-distort-the-scene-3.md) | KSArmory | `glsl-shader` | `render:bloom`, `frame:draw-ui` | Whiteout and shake driven by a game-free model on the simulation clock; nothing written to the camera |

## See also

- [Canopy-glass post-process chain](../../../../mods/ksa-aircrafthud/features/glass-post-process-chain.md), [The blast front drawn as a bend in the light, by copying the scene and writing it back at an offset](../../../../mods/ksarmory/features/shock-front-refraction.md), [A burst's whiteout and a blast front's view shake, painted into the scene image instead of moving the camera](../../../../mods/ksarmory/features/burst-flash-and-view-shake.md)
- [Blur the screen with a two-pass separable Gaussian](../blur-the-screen-with-a-separable-gaussian/overview.md), [Raymarch a local volume or analytic sky effect into the HDR scene](../../compute-effects/raymarch-a-local-volume-into-the-hdr-scene/overview.md), [Paint into or rescale the HDR scene with a compute pass shipped as a Shader asset](../../../rendering/vulkan-passes/modify-the-hdr-scene-with-a-compute-pass/overview.md), [Choose between pre-ImGui (HDR scene) and post-ImGui (final frame) post-processing](../../post-processing/choose-pre-or-post-imgui-post-processing/overview.md)
