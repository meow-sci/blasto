---
type: Game Feature
title: Show a PNG or runtime-generated image in an ImGui window
description: Decode or build pixels, upload them as a Vulkan texture, register with the ImGui Vulkan backend and draw with ImGui.Image or a draw list, then update, evict and release the texture without crashing a frame in flight
category: rendering
group: textures-and-decals
topic: show-an-image-in-imgui
demonstrated_by:
  - mods/compendium/features/logo-image-as-imgui-texture
  - mods/purrtty/features/kitty-graphics-as-imgui-vulkan-textures
lifecycle: [startup:immediate-load, frame:draw-ui, render:imgui]
game_types:
  - RenderCore.SimpleVkTexture
  - Brutal.TextureApi.TextureLoader
  - KSA.ImGuiBackendVulkanImpl
  - RenderCore.TextureAsset
tags: [imgui, image, texture, descriptor-pool, upload]
---

ImGui draws images by `ImTextureRef`, which in KSA's Vulkan backend is a raw descriptor-set handle. A mod gets an image into a window by producing a `SimpleVkTexture`, asking the backend for a descriptor set (`AddTexture`) and passing the returned reference to `ImGui.Image` or `ImDrawListPtr.AddImage`. The cost is lifetime management: the descriptor pool is shared by the whole game,
and the GPU may sample an image for several frames after the mod stops drawing it.

## Engine background

- `ImGuiBackendVulkanImpl.AddTexture` / `RemoveTexture` [current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L1119-L1131); the backend is `ImGuiBackend.Vulkan` with a 1000-set pool [current/decomp/KSA/ImGuiBackend.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackend.cs#L15).
- `ImGui.Image` [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L6359) and `ImDrawListExtensions.AddImage` [current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L611) record the texture reference into draw data; the ImGui render pass samples it at step 25 of `RenderGame` (`render:imgui`).
- Upload: `TextureAsset` and `SimpleVkTexture` [current/decomp/RenderCore/SimpleVkTexture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/SimpleVkTexture.cs#L131), `StagingPool` [current/decomp/Brutal.VulkanApi.Abstractions/StagingPool.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/StagingPool.cs#L214-L218). See [the render sequence](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: one static PNG, uploaded once](show-an-image-in-imgui-1.md) | compendium | `TextureAsset` -> `SimpleVkTexture` -> `AddTexture` -> `ImGui.Image` | `startup:immediate-load`, `frame:draw-ui` | Twenty lines; never released |
| [2: many changing images, in-place refresh and LRU eviction](show-an-image-in-imgui-2.md) | purrtty | Texture cache, content versions, deferred delete | `frame:draw-ui`, `render:imgui` | Safe for video-like use; more bookkeeping |

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Upload an image or procedural pixels as a GPU texture](../upload-an-image-as-a-vulkan-texture/overview.md), [Free mod-owned Vulkan resources without device loss](../../vulkan-passes/free-vulkan-resources-safely/overview.md), [Record and submit your own Vulkan work: offscreen renders and one-shot uploads](../../vulkan-passes/submit-your-own-vulkan-commands/overview.md)
