---
type: Game Feature
title: Upload an image or procedural pixels as a GPU texture
description: Load PNG or KTX bytes or generate pixels in code into a SimpleVkTexture with mip levels and a sampler and expose it to shaders through a bindless slot
category: rendering
group: textures-and-decals
topic: upload-an-image-as-a-vulkan-texture
demonstrated_by:
  - mods/gatos/features/clutter-texture-override
  - mods/unscience/features/graffiti-bindless-decal-textures
  - mods/unscience/features/thug-life-procedural-texture
lifecycle: [frame:draw-ui, frame:viewports]
game_types:
  - RenderCore.SimpleVkTexture
  - Brutal.TextureApi.TextureLoader
  - KSA.BindlessTextures
  - RenderCore.Systems.BindlessTextureLibrary
tags: [texture, upload, bindless, mips, staging, sampler]
---

Every texture the game samples is a `SimpleVkTexture` (image, view, allocation) reached either through a bindless slot, a descriptor set of the pipeline, or an ImGui descriptor. A mod gets bytes onto the GPU with the game's own loaders and helpers: decode with `TextureLoader`, wrap in a `TextureAsset`, build a
`SimpleVkTexture` through a staging pool, or fill an empty `SimpleVkTexture` with raw pixels.

## Engine background

- Decode: `TextureLoader.LoadFromMemory` [current/decomp/Brutal.TextureApi/TextureLoader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.TextureApi/TextureLoader.cs#L135), `TextureAsset` and `TextureAsset.LoadOptions` [current/decomp/RenderCore/TextureAsset.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/TextureAsset.cs#L61-L109).
- Image: `SimpleVkTexture` constructors for assets (mips, `CreateOptions`) and for raw pixels (`UploadData`) [current/decomp/RenderCore/SimpleVkTexture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/SimpleVkTexture.cs#L131-L383).
- Bindless: `BindlessTextureLibrary.AddTexture` / `SetTexture` / `FreeTexture` [current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs#L186-L212); `Program.Instance.BindlessTextures` has 1024 slots [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L830). Shared samplers: `Program.LinearClampedSampler` and `Program.PointClampedSampler` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L468-L472).
- Timing: uploads on the main thread in `frame:draw-ui` (or `frame:viewports` for command drains); sampling in `frame:render`. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: decode and re-point an existing bindless slot](upload-an-image-as-a-vulkan-texture-1.md) | gatos | `TextureLoader` + `SimpleVkTexture` + `SetTexture` | `frame:draw-ui` | Replace stock content with no shader change; one slot affects all materials |
| [2: library textures claimed into the bindless table](upload-an-image-as-a-vulkan-texture-2.md) | unscience | `AddTexture`, write-time cache, retire queue | `frame:draw-ui`, `event:load` | Shader needs only a `uint`; uses scarce slots |
| [3: procedural pixel art](upload-an-image-as-a-vulkan-texture-3.md) | unscience | Raw pixels, staging upload, nearest sampler | `frame:draw-ui` | No decoder needed; blocking submit |

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Replace a game texture or model texture with a user PNG at runtime](../replace-a-game-texture-at-runtime/overview.md), [Show a PNG or runtime-generated image in an ImGui window](../show-an-image-in-imgui/overview.md), [Free mod-owned Vulkan resources without device loss](../../vulkan-passes/free-vulkan-resources-safely/overview.md), [Draw a textured quad attached to a part or the camera](../draw-a-textured-quad-in-the-world/overview.md)
