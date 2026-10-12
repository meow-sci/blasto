---
type: Game Feature
title: Replace a game texture or model texture with a user PNG at runtime
description: Swap what an existing material samples by re-pointing the bindless slot the game already uses or by rebuilding a mod-owned mesh resource with a PNG override and live UV remap, and give mod-built render objects a private material buffer
category: rendering
group: textures-and-decals
topic: replace-a-game-texture-at-runtime
demonstrated_by:
  - mods/gatos/features/clutter-texture-override
  - mods/unscience/features/sphinx-uv-texture-overrides
  - mods/unscience/features/pebbles-private-clutter-materials
lifecycle: [frame:viewports, frame:draw-ui, frame:render, frame:dispatch-solvers]
game_types:
  - RenderCore.Systems.BindlessTextureLibrary
  - KSA.TextureReference
  - RenderCore.SimpleVkTexture
  - KSA.ClutterRenderer
  - KSA.Program
tags: [texture, replace, bindless, material, uv, clutter]
---

Game textures are reached through bindless slots stored in material data. To change what an existing material shows, either write a different image view into the slot it already uses (cheap, but it changes every material using that slot), or, for objects the mod builds itself, own the resource and replace it. Where a mod must create new materials next to stock ones, it can redirect the
game's own lookups while it constructs its objects.

## Engine background

- `TextureReference` [current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs#L20) claims a slot in `DoLoad` (`AddTexture`, L149) and exposes `BindlessHandle` and `ImageView`; `BindlessTextureLibrary.SetTexture(handle, imageView, layout)` [current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs#L201) rewrites a slot (update-after-bind, partially bound [current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs#L22)).
- Ground clutter materials: `GroundClutterRenderer.MaterialBuffer` [current/decomp/KSA/GroundClutterRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterRenderer.cs#L320) and `GetMaterialIndex`, bound into pipelines when `ClutterEcotypeRenderData` creates its renderers.
- Timing: slot writes in `frame:draw-ui` before the scene records; sampling in `frame:render`. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: SetTexture on the stock slot, deferred destruction](replace-a-game-texture-at-runtime-1.md) | gatos | `bindless-slot-swap` | `frame:viewports`, `frame:draw-ui` | Instant and reversible, no patches; affects every material using the slot |
| [2: PNG override rebuilds the private resource; UV remap re-uploads vertices](replace-a-game-texture-at-runtime-2.md) | unscience | Private resource, original vertices kept | `frame:draw-ui` | Per-object control; blocking uploads and `WaitIdle` |
| [3: thread-static construction context](replace-a-game-texture-at-runtime-3.md) | unscience | Transpiler and prefix redirection, shader variant | `frame:dispatch-solvers` | Private materials beside stock; fragile to game code changes |

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Upload an image or procedural pixels as a GPU texture](../upload-an-image-as-a-vulkan-texture/overview.md), [Free mod-owned Vulkan resources without device loss](../../vulkan-passes/free-vulkan-resources-safely/overview.md), [Recreate mod render targets when KSA rebuilds its renderer](../../vulkan-passes/handle-renderer-rebuilds/overview.md)
- [Patch vehicle paint and canopy shaders in memory at compile time](../../../shaders/stock-shaders/patch-game-shaders-in-memory-at-compile-time/overview.md)
