---
type: Game Feature
title: Colour-grade the frame and add an aspect-correct vignette
description: Where in KSA's frame a colour grade belongs, the GLSL building blocks MPFX uses for lift/gamma/gain, temperature, hue, contrast, saturation and vignette, and a TV or thermal look for a secondary camera feed
category: shaders
group: screen-effects
topic: colour-grade-the-frame-and-add-a-vignette
demonstrated_by:
  - mods/mpfx/features/color-grading-math
  - mods/mpfx/features/effect-shader-contract
  - mods/ksarmory/features/sensor-look-pass
lifecycle: [render:post-process, frame:draw-ui, frame:celestials, render:bloom]
game_types:
  - KSA.Program
  - Brutal.Numerics.float4x4
  - KSA.SunbloomRenderer
  - KSA.IViewport
  - KSA.Rendering.RenderTarget
  - KSA.Rendering.BarrierBatch
  - KSA.ViewportOptionFlags
tags: [colour-grading, vignette, lift-gamma-gain, colour-temperature, hdr, tonemap, thermal, screen-effect]
---
A colour grade is a handful of per-pixel formulas run on the whole frame. In KSA they have to be placed relative to the tonemap, because the game renders the scene in linear HDR and tonemaps it in the final composite. This topic gives the formulas MPFX uses (each short enough to copy), how they behave on HDR versus display values, and a second technique for a secondary camera feed:
a compute pass on one viewport's HDR image, before bloom and tonemap, that gives a TV or thermal look.

## Engine background

- **Where the tonemap is.** `RenderGame` renders the scene into an RGBA16F offscreen target; `RenderFinalComposite` tonemaps it inside the swapchain render pass, then ImGui is drawn ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4945-L4951)). A ShaderExtensions pre-ImGui pass therefore sees linear HDR values above 1.0, a post-ImGui pass sees display values; see [Choose between pre-ImGui (HDR scene) and post-ImGui (final frame) post-processing](../../post-processing/choose-pre-or-post-imgui-post-processing/overview.md).
- **Bloom runs before the composite.** `SunbloomRenderer.Render(CommandBuffer, IViewport, int)` is public and called once per rendered viewport; `OverallBloomRenderer.Render` follows ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4915-L4916)). A compute pass recorded before bloom has its output bloomed and tonemapped like the colour picture.
- **Matrices.** `Brutal.Numerics.float4x4` stores four row vectors and its indexer is `[row, column]`; a GLSL `mat4` is column-major, so C# row `r` is GLSL column `m[r]`. MPFX packs lift, gamma and gain into rows 0 to 2 of one matrix.

## Choosing a placement

| Need | Placement | Notes |
|---|---|---|
| Grade the 3D scene only, keep the HUD true | Pre-ImGui chain (HDR) | Pivot contrast at scene-linear mid-grey 0.18; decide whether to clamp |
| Grade everything including UI | Post-ImGui chain (display) | Values already tonemapped |
| Look for one secondary camera feed | Compute pass from a `SunbloomRenderer.Render` prefix | Per viewport; before bloom and tonemap |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Pre-ImGui grading passes on the HDR scene](colour-grade-the-frame-and-add-a-vignette-1.md) | MPFX | `glsl-shader` | `render:post-process`, `frame:draw-ui` | One pass per operation, neutral parameters return the input; CMAA2 hides the pre-ImGui result |
| [Two-dispatch reduction and map per viewport with a cached pipeline](colour-grade-the-frame-and-add-a-vignette-2.md) | KSArmory | `vulkan-pipeline` | `render:bloom`, `frame:celestials` | Log-mean measure then map, TV/thermal look for secondary viewports; you own the pipeline and its rebuilds |

## See also

- [Color grading shader math](../../../../mods/mpfx/features/color-grading-math.md), [Shader contract for chain passes](../../../../mods/mpfx/features/effect-shader-contract.md), [TV and thermal look for a camera window, recorded before bloom](../../../../mods/ksarmory/features/sensor-look-pass.md)
- [Choose between pre-ImGui (HDR scene) and post-ImGui (final frame) post-processing](../../post-processing/choose-pre-or-post-imgui-post-processing/overview.md), [Feed a ShaderExtensions shader from C# with uniform buffers (std140)](../../post-processing/feed-uniform-buffers-to-a-shaderextensions-shader/overview.md), [Paint into or rescale the HDR scene with a compute pass shipped as a Shader asset](../../../rendering/vulkan-passes/modify-the-hdr-scene-with-a-compute-pass/overview.md), [Render ImGui UI into a Vulkan texture for use in the 3D world](../../../rendering/textures-and-decals/render-imgui-into-a-texture/overview.md)
