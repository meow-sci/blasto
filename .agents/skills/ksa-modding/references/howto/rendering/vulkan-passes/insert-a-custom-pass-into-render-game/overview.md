---
type: Game Feature
title: Insert a custom Vulkan pass into KSA's frame
description: KSA records one frame as scene, resolve, bloom and composite stages, and a mod can hook a stage boundary with a Harmony postfix or prefix, or rewrite RenderGame, to record its own work where depth, colour and light are in the state it needs
category: rendering
group: vulkan-passes
topic: insert-a-custom-pass-into-render-game
demonstrated_by:
  - mods/unscience/features/graffiti-projected-decal-pass
  - mods/gatos/features/sticker-projected-decals
  - mods/unscience/features/thug-life-textured-quad-render-pass
  - mods/gatos/features/thug-life-world-space-quad
  - mods/purrtty/features/draw-after-translucency-pass-postfix
  - mods/ksarmory/features/compute-pass-before-bloom
  - mods/ksastructures/features/underground-dark-compute-pass
  - mods/shaderextensions/features/rendergame-render-pass-transpiler
  - mods/shaderextensions/features/pre-imgui-post-processing
  - mods/shaderextensions/features/post-imgui-global-post-shaders
lifecycle:
  - frame:render
  - render:scene-opaque
  - render:translucency
  - render:grid
  - render:bloom
  - render:post-process
game_types: [KSA.Program, KSA.SuperMeshRenderSystem, KSA.Rendering.RenderTarget, KSA.SunbloomRenderer]
tags: [vulkan, render-pass, harmony, render-game, scene, translucency, bloom]
---

`Program.RenderGame` records the whole frame into one primary command buffer on the main thread. There is no renderer
registry, event or delegate list inside it: a mod that wants its own draws or dispatches in the frame patches a
specific call that already exists. Which call decides what the mod can read (single-sample depth, HDR colour, light
images), whether a rendering scope is open, and whether its content is depth-tested, anti-aliased, bloomed or tonemapped.

## Engine background

`RenderGame` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4646-L4955) runs, in order: prepass, ambient occlusion, shadows, planet generation, an
opaque offscreen scope (sky, planets, part models, `SuperMeshRenderSystem.RenderMainPass` at L4839, gizmo meshes), particles,
ocean, atmosphere, exhaust, translucency (`RenderTranslucencyPass` at L4901, then the colour-only resolve), `SunbloomRenderer.Render`
(L4915), overall bloom, orbit lines, gizmos, a full resolve (`RenderTarget.ResolveAttachments` L4932), grid, gauges, AA, and then the
swapchain render pass with the composite and ImGui. The authoritative step table is the
[render sequence of the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

- Target and pass types: `RenderTarget.ResolveAttachments` [current/decomp/KSA.Rendering/RenderTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderTarget.cs#L315),
  `SuperMeshRenderSystem.RenderMainPass` [current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L388) and `RenderTranslucencyPass`
  [current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L459), `SunbloomRenderer.Render` [current/decomp/KSA/SunbloomRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SunbloomRenderer.cs#L326).
- Steps 1 to 22 use dynamic rendering or compute on offscreen targets; only the composite and ImGui are inside the single
  swapchain render pass, where scopes and copies are illegal.
- Hooks run once per call, not once per frame: secondary viewports and the editor reach the same sub-renderers.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: postfix ResolveAttachments](insert-a-custom-pass-into-render-game-1.md) | unscience, gatOS | `harmony-postfix` | `render:grid` window | Single-sample depth is sampleable (decals); no depth test, must filter viewports |
| [2: postfix RenderMainPass](insert-a-custom-pass-into-render-game-2.md) | unscience, gatOS | `harmony-postfix` | `render:scene-opaque` | Draws in the open MSAA scope, depth-tested; pipeline must match the target |
| [3: postfix RenderTranslucencyPass](insert-a-custom-pass-into-render-game-3.md) | purrtty | `harmony-postfix` | `render:translucency` | Composites after atmosphere and plumes; needs own scope and barriers |
| [4: prefix SunbloomRenderer.Render](insert-a-custom-pass-into-render-game-4.md) | ksarmory, ksastructures | `harmony-prefix` | `render:bloom` | Compute on HDR colour before bloom; public method, no IL matching |
| [5: transpile RenderGame](insert-a-custom-pass-into-render-game-5.md) | shaderextensions | `harmony-transpiler` | `render:post-process`, `render:imgui` | Only way around the swapchain pass; fragile, one mod per anchor |

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Draw a textured quad attached to a part or the camera](../../textures-and-decals/draw-a-textured-quad-in-the-world/overview.md), [Project decals onto vessels, terrain and clutter](../../textures-and-decals/project-decals-onto-scene-geometry/overview.md), [Paint into or rescale the HDR scene with a compute pass shipped as a Shader asset](../modify-the-hdr-scene-with-a-compute-pass/overview.md), [Build a Vulkan pipeline compatible with KSA's scene render target](../build-a-vulkan-pipeline-for-the-scene-target/overview.md), [Capture rendered frames from KSA](../capture-rendered-frames/overview.md)
- [Choose between pre-ImGui (HDR scene) and post-ImGui (final frame) post-processing](../../../shaders/post-processing/choose-pre-or-post-imgui-post-processing/overview.md)
