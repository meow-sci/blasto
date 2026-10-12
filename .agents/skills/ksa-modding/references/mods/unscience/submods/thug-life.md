---
type: Submod
title: Thug Life
description: Draws the pixel-art sunglasses meme as a textured quad anchored to any vessel part or kitten face, recorded with stock unlit shaders inside the scene's opaque pass from a postfix on SuperMeshRenderSystem.RenderMainPass
mod: unscience
submod: thug-life
tags: [rendering, vulkan, textured-quad, procedural-texture, part-anchor, harmony]
mod_id: thug-life
---

Thug Life ("Thug Life - Sunglasses Anchor") is the repository's minimal worked example of custom Vulkan
drawing inside KSA's own render pass. A 15x4 sunglasses texture is generated from an ASCII pattern (no image
file ships), uploaded as a `SimpleVkTexture`, and drawn as a quad anchored to a part or sub-part in its local
frame, using the game's stock `UnlitMesh` shaders. GPU resources are created lazily on first use because the
offscreen target does not exist at load time. An "animate thug" button slides a pre-tuned pair onto an EVA
kitten's face.

## Hosting

- Manifest id `thug-life`, entry assembly `MeowSci.ThugLife` (development host only).
- Standalone host: F12 toggles the window; its Patcher applies `HotkeyGuard` and `ThugLifeRenderPatches`.
- In unscience: `ThugLifeSubmod` is constructed in `Mod.cs` and `Patcher.cs` applies
  `ThugLifeRenderPatches` (the same shared `Apply`/`Remove` pair).

## Features

| Feature | Category | Lifecycle | Summary |
|---|---|---|---|
| [Upload a procedural pixel-art texture and cut it out with geometry](../features/thug-life-procedural-texture.md) | rendering | `frame:draw-ui` | Thug Life builds its sunglasses image from an ASCII grid into an R8G8B8A8 SimpleVkTexture with a staging upload and a nearest-neighbour sampler, and emits one small quad per opaque texel because the stock unlit shader always writes alpha 1 |
| [Draw a part-anchored textured quad inside the scene's opaque pass](../features/thug-life-textured-quad-render-pass.md) | rendering | `render:scene-opaque`, `frame:draw-ui`, `startup:all-mods-loaded` | Thug Life postfixes SuperMeshRenderSystem.RenderMainPass to record its own Vulkan draws with the stock UnlitMesh shaders into the offscreen MSAA pass, builds its pipeline lazily against Program.OffscreenTarget, and computes an ego-space MVP per entry from the camera of the viewport being rendered |

## Library classes

`ThugLifeSubmod` (+ `.Persistence`), `ThugLifeRenderManager` (static active instance for the render
postfix; entry list and lazily created GPU resources), `ThugLifeQuadRenderer` (pipeline, descriptor, vertex
and index buffers, per-frame MVP), `ThugLifeTextureFactory`, `ThugLifeTexturePattern`, `ThugLifeRenderPatches`,
`ThugLifeEntry`, `ThugLifeSlide`, `KittenGlassesPreset`.

## Scene saves and persistence

Record `thug-life` (default order 100): per quad, its anchor as a
[stable part reference](../features/lib-save-part-identity.md), position, rotation, size and visibility.
Reset removes entries but keeps the shared renderer resources; a missing anchor keeps the record retained
with a warning. No render resources are serialized.

## Further reading

- [thug-life/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/thug-life/README.md)
- [thug-life.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/thug-life.lib/README.md)
- [scope/pixel-grids-and-render.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/pixel-grids-and-render.md)

## Entry points

- [thug-life/mod.toml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/thug-life/mod.toml)
- [thug-life/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/thug-life/Mod.cs)
- [thug-life/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/thug-life/Patcher.cs)
- [thug-life.lib/ThugLifeRenderPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/thug-life.lib/ThugLifeRenderPatches.cs)
- [thug-life.lib/ThugLifeSubmod.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/thug-life.lib/ThugLifeSubmod.Persistence.cs)

## See also

- [unscience overview](../overview.md)
- [Graffiti](./graffiti.md) (projected decals, another custom render pass)
- [Humble Arteest](./humble-arteest.md)
