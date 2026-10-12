---
type: Game Feature
title: Project decals onto vessels, terrain and clutter
description: Draw an image, a sticker or a damage mark onto arbitrary surfaces by reconstructing the scene position from the depth buffer in the post-resolve window or a compute pass, or use the terrain's own decal modifiers
category: rendering
group: textures-and-decals
topic: project-decals-onto-scene-geometry
demonstrated_by:
  - mods/unscience/features/graffiti-projected-decal-pass
  - mods/unscience/features/graffiti-bindless-decal-textures
  - mods/gatos/features/sticker-projected-decals
  - mods/ksarmory/features/bullet-hole-decals
  - mods/ksastructures/features/terrain-decal-pits
lifecycle: [frame:render, render:grid, render:bloom, startup:all-mods-loaded]
game_types: [KSA.Rendering.RenderTarget, KSA.DecalModifierReference, KSA.TerrainReference]
tags: [decal, projected, depth, reverse-z, terrain, damage]
---

A projected (deferred-style) decal is not a mesh: it rasterises a volume, reads the scene depth under each covered pixel, rebuilds the world position, and paints where that position lies inside the volume. It therefore wraps curvature and relief on hulls, canopies, terrain and ground clutter alike. The game also has its own decal system for terrain, which changes the
ground itself rather than the picture.

## Engine background

- Seam: `RenderTarget.ResolveAttachments` [current/decomp/KSA.Rendering/RenderTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderTarget.cs#L315), called after `GizmoPass.Run` and before `GridPass.Run` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4927-L4936) (`render:grid` window): single-sample reverse-Z depth and colour are both current and not bound as attachments.
- Alternative: a compute dispatch before bloom (`render:bloom`), with the same reverse-Z depth ([modify the HDR scene with a compute pass](../../vulkan-passes/modify-the-hdr-scene-with-a-compute-pass/overview.md)).
- Terrain: `DecalModifierReference` [current/decomp/KSA/DecalModifierReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DecalModifierReference.cs#L9) on `TerrainReference.ProceduralModifiers` / `TerrainModifiers` [current/decomp/KSA/TerrainReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TerrainReference.cs#L11-L15), consumed once at startup.
- Anchors must be recomposed per frame ([approach 6](../../overlays/choose-a-render-frame-and-anchor-effects/overview.md)). See [the render sequence](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: unit-cube volumes, reverse-Z reconstruction](project-decals-onto-scene-geometry-1.md) | unscience | `ResolveAttachments` postfix, box per decal, bindless texture | `render:grid` | Image decals on anything; main viewport only |
| [2: sticker pass, runtime GLSL with the game library](project-decals-onto-scene-geometry-2.md) | gatos | Same seam, patch only while stickers exist | `render:grid` | Same, with geodetic anchors and picking |
| [3: procedural bullet holes by compute](project-decals-onto-scene-geometry-3.md) | ksarmory | Compute from depth, sub-part-frame storage | `render:bloom` | No texture or hook of its own; every viewport; procedural look only |
| [4: engine terrain DecalModifier](project-decals-onto-scene-geometry-4.md) | ksastructures | XML deserialized into the terrain at load | `startup:all-mods-loaded` | Real terrain change incl. physics; load window only |

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Insert a custom Vulkan pass into KSA's frame](../../vulkan-passes/insert-a-custom-pass-into-render-game/overview.md), [Choose a render frame and anchor effects to bodies, parts and terrain](../../overlays/choose-a-render-frame-and-anchor-effects/overview.md), [Upload an image or procedural pixels as a GPU texture](../upload-an-image-as-a-vulkan-texture/overview.md)
- [Include KSA's own GLSL library from a mod shader](../../../shaders/glsl-workflow/include-the-game-shader-library-in-mod-glsl/overview.md), [Turn the mouse cursor into a world ray and pick points on a construction plane](../../../ui/input/cursor-to-world-ray/overview.md)
