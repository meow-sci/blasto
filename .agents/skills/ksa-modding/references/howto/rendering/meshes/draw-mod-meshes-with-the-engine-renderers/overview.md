---
type: Game Feature
title: Draw your own meshes through the engine's mesh renderers
description: Hand the game your meshes as StaticMeshRenderables, extra part-model instances or private buffers inside the static-object pipeline so lighting, shadows and culling come for free
category: rendering
group: meshes
topic: draw-mod-meshes-with-the-engine-renderers
demonstrated_by:
  - mods/ksagolf/features/mod-mesh-draw-hook
  - mods/unscience/features/sphinx-static-object-render-hooks
  - mods/ksarmory/features/round-body-draw-hooks
  - mods/unscience/features/rocky-mcrock-face-interleaved-mesh-clone
lifecycle: [startup:all-mods-loaded, frame:render, render:scene-opaque, render:shadows, render:prepass]
game_types:
  - KSA.StaticMeshRenderable
  - KSA.SuperMeshRenderSystem
  - KSA.PartModel
  - KSA.StaticObjectRenderer
  - KSA.PartModelRenderer
  - KSA.MeshReference
tags: [mesh, static-mesh, part-model, static-object, buckets, viewport]
---
A mod that wants geometry in the world has two options: write its own pipeline (see [build a Vulkan pipeline for the scene target](../../vulkan-passes/build-a-vulkan-pipeline-for-the-scene-target/overview.md)) or give the game's renderers something to draw. The second route keeps PBR shading, shadows from the sun and local lights, ambient occlusion, atmosphere lighting, depth and normal prepass participation and viewport handling. The price is that you must submit at the moment the renderer will consume the submission, for every viewport, every frame.

This topic compares four ways in, ordered from least to most Vulkan code.

## Engine background

- **SuperMesh buckets.** `SuperMeshRenderSystem` [current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L546-L554) keeps per-view `MeshPass` buckets (`OpaquePrePass`, `TranslucentPrePass`, `MainOpaque`, `Translucency`) and a `ShadowRenderablePool`. `ClearBuckets(IViewport)` empties them at the start of each viewport's frame; `StaticMeshRenderable.Draw(ViewHandle)` [current/decomp/KSA/StaticMeshRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticMeshRenderable.cs#L56-L90) appends to them.
- **Part models.** `PartModel.AddInstance` [current/decomp/KSA/PartModel.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModel.cs#L465-L491) queues an instance of a part template's shared mesh; `PartModelRenderer.UpdateRenderData` [current/decomp/KSA/PartModelRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModelRenderer.cs#L807-L827) uploads every model's list. Vehicles queue their parts from `Vehicle.UpdateRenderData` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3707-L3726).
- **Static objects.** `StaticObjectRenderer` [current/decomp/KSA/StaticObjectRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObjectRenderer.cs#L275-L370) binds the full PBR state, then draws the `StaticObjectModel` buckets.
- **Where it all runs.** `Program.UpdateRenderingResources` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4337-L4377)) fills per-frame instance data for every visible viewport; `RenderGame` clears the SuperMesh buckets (L4689), records prepass, shadows, the offscreen scene scope with `PartModelRenderer.ColorData`, `StaticObjectRenderer.WriteCommands` and `SuperMeshRenderSystem.RenderMainPass`, then translucency. See the RenderGame pass table in [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence) (rows 1a, 2, 6, 9, 14).

Because secondary viewports (camera windows, crew portraits) run the same code, every approach below must place its geometry in the camera-relative (ego) frame of the viewport it is called for; see [choose a render frame and anchor effects](../../overlays/choose-a-render-frame-and-anchor-effects/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: StaticMeshRenderable from a ClearBuckets postfix](draw-mod-meshes-with-the-engine-renderers-1.md) | KSAGolf | `harmony-postfix` on `SuperMeshRenderSystem.ClearBuckets`, `glb-model` | `render:scene-opaque`, `render:shadows` | No Vulkan code; lit, shadowed, depth-tested. Mesh must be a declared glTF; only the main view casts cascade shadows |
| [2: private buffers inside the static-object pipeline](draw-mod-meshes-with-the-engine-renderers-2.md) | unscience | `harmony-postfix` on `StaticObjectRenderer` colour, prepass and render-data methods | `render:prepass`, `render:scene-opaque` | Full static-object lighting and receive-only shadows for arbitrary geometry; heavy descriptor and buffer code, fragile to binding changes |
| [3: extra part-model instances](draw-mod-meshes-with-the-engine-renderers-3.md) | KSArmory | `harmony-prefix` on `PartModelRenderer.UpdateRenderData`, `part-model-instancing` | `frame:render`, `render:scene-opaque` | One matrix per instance, shadows included, many copies cheap; limited to meshes that exist as part templates |
| [4: clone interleaved part meshes](draw-mod-meshes-with-the-engine-renderers-4.md) | unscience | `reflection` + `MeshReference.Bind` | `frame:draw-ui`, `render:scene-opaque` | Lets another renderer (rings) consume part and glTF geometry; reflection on a compiler-generated field, lifetime care |

Pick by what you already have: a glTF file and no Vulkan experience means approach 1; many copies of an existing part mesh (projectiles, debris) means 3; custom vertex data that still must look like a placed building means 2; geometry for a non-mesh renderer means 4.

## See also

- [Make static objects cast sun and lamp shadows with invisible casters](../add-invisible-shadow-casters/overview.md)
- [Reserve or grow game GPU buffers for content added at runtime](../grow-game-gpu-buffers-for-runtime-content/overview.md)
- [Choose a render frame and anchor effects to bodies, parts and terrain](../../overlays/choose-a-render-frame-and-anchor-effects/overview.md)
- [Build a Vulkan pipeline compatible with KSA's scene render target](../../vulkan-passes/build-a-vulkan-pipeline-for-the-scene-target/overview.md)
- [Free Vulkan resources safely](../../vulkan-passes/free-vulkan-resources-safely/overview.md)
- The topic on adding or restyling planetary rings (celestials category, not yet written)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Add planetary rings to a body or restyle existing rings at runtime](../../../celestials/atmospheres-and-rings/add-or-restyle-planetary-rings/overview.md) - related topic

