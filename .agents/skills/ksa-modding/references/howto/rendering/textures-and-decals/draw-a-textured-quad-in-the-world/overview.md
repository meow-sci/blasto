---
type: Game Feature
title: Draw a textured quad attached to a part or the camera
description: Put an image, sticker, screen or pixel sprite into the 3D scene as a world-space quad with depth testing, tied to a part transform or a view-space billboard
category: rendering
group: textures-and-decals
topic: draw-a-textured-quad-in-the-world
demonstrated_by:
  - mods/unscience/features/thug-life-textured-quad-render-pass
  - mods/unscience/features/thug-life-procedural-texture
  - mods/gatos/features/thug-life-world-space-quad
  - mods/purrtty/features/world-space-quad-pipeline-custom-shader
  - mods/purrtty/features/draw-after-translucency-pass-postfix
  - mods/purrtty/features/anchor-quad-to-part-or-camera
lifecycle: [frame:render, render:scene-opaque, render:translucency, frame:draw-ui]
game_types: [KSA.SuperMeshRenderSystem, KSA.Program, KSA.Part, RenderCore.SimpleVkTexture]
tags: [quad, billboard, texture, world-space, part-anchor, pipeline]
---

A quad in the world needs three decisions: **when** it is recorded (inside the opaque pass, depth-tested and anti-aliased with the scene, or after the translucency pass so atmosphere cannot overwrite it), **what pipeline** draws it (the stock unlit shaders, or your own for premultiplied alpha), and **what it is attached to** (a part's ego matrix, composed
every frame, or the camera's view space). All three approaches reuse the stock `UnlitMesh.vert` and a push-constant MVP.

## Engine background

- Hooks: `SuperMeshRenderSystem.RenderMainPass` [current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L388) (inside the opaque scope, step 9) and `RenderTranslucencyPass` [current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L459) (step 14); both fire once per rendered viewport. See [the render sequence of the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).
- Pipeline: `RenderTarget.SetupGraphicsPipeline` [current/decomp/KSA.Rendering/RenderTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderTarget.cs#L356-L392) or a hand-chained rendering create-info.
- Anchors: `Vehicle.GetMatrixAsmb2Ego(camera)` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1270), `Part.PositionEgo`, `Part.Asmb2Ego`; `Camera.MVP.viewProjection` / `.projection` for billboards.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: opaque pixel quads with stock shaders](draw-a-textured-quad-in-the-world-1.md) | unscience | `RenderMainPass` postfix, geometry as transparency | `render:scene-opaque` | Simple and depth-correct; no real alpha, capped texel count |
| [2: single textured quad, own plumbing](draw-a-textured-quad-in-the-world-2.md) | gatos | Same hook, patch installed on demand, camera mask | `render:scene-opaque` | Minimal; same alpha limits |
| [3: premultiplied translucent quad after translucency](draw-a-textured-quad-in-the-world-3.md) | purrtty | Custom shader, reopened scope, part or camera anchor, ray picking | `render:translucency` | True translucency and interaction; more pipeline and barrier code |

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Insert a custom Vulkan pass into KSA's frame](../../vulkan-passes/insert-a-custom-pass-into-render-game/overview.md), [Build a Vulkan pipeline compatible with KSA's scene render target](../../vulkan-passes/build-a-vulkan-pipeline-for-the-scene-target/overview.md), [Choose a render frame and anchor effects to bodies, parts and terrain](../../overlays/choose-a-render-frame-and-anchor-effects/overview.md), [Upload an image or procedural pixels as a GPU texture](../upload-an-image-as-a-vulkan-texture/overview.md), [Render ImGui UI into a Vulkan texture for use in the 3D world](../render-imgui-into-a-texture/overview.md), [Turn the mouse cursor into a world ray and pick points on a construction plane](../../../ui/input/cursor-to-world-ray/overview.md)
