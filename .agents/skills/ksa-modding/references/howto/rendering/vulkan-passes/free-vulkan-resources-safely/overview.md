---
type: Game Feature
title: Free mod-owned Vulkan resources without device loss
description: The GPU may still read an image or buffer for several frames after the mod stops referencing it, so stop recording first, free after frames in flight or WaitIdle, and never touch the destroyed device at game exit
category: rendering
group: vulkan-passes
topic: free-vulkan-resources-safely
demonstrated_by:
  - mods/purrtty/features/vulkan-resource-teardown-discipline
  - mods/purrtty/features/kitty-graphics-as-imgui-vulkan-textures
  - mods/gatos/features/clutter-texture-override
  - mods/unscience/features/pebbles-asset-upload-submission
  - mods/unscience/features/sphinx-uv-texture-overrides
  - mods/unscience/features/pebbles-workshop-offscreen-preview
lifecycle: [frame:draw-ui, frame:render, frame:prepare]
game_types: [RenderCore.SimpleVkTexture, KSA.ImGuiBackendVulkanImpl, KSA.Program, Core.Renderer]
tags: [vulkan, teardown, frames-in-flight, wait-idle, shutdown]
---

Destroying a Vulkan object while a submitted or recorded command buffer still uses it produces `VK_ERROR_DEVICE_LOST` or an access violation. KSA submits each frame in `PostRender` and keeps two frames in flight, so an
object that was referenced in frame N may be read by the GPU during frame N+1. Mods have three working disciplines, plus one rule for the end of the process.

## Engine background

- `Renderer.MaxFramesInFlight` is 2 [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L28); the frame is recorded in `Render` and submitted in `PostRender` on `Renderer.Graphics`.
- `ImGuiBackendVulkanImpl.AddTexture` / `RemoveTexture` [current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L1119-L1131) register an image for ImGui; the descriptor pool is shared by the whole game.
- `StagingPool.Dispose` always submits and waits [current/decomp/Brutal.VulkanApi.Abstractions/StagingPool.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/StagingPool.cs#L214-L218).
- `Program.Dispose` destroys the device at exit [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1470-L1484); `[StarMapUnload]` runs afterwards, so no device call is legal there.
- `RetiredResourceQueue.Retire(IDisposable)` [current/decomp/KSA.Rendering/RetiredResourceQueue.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RetiredResourceQueue.cs#L26) is the game's own public frame-stamped deferred-destroy queue, drained by `CheckQueued()` in `Program.OnFrame` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2314).
- The phases: stop in `frame:draw-ui`, the GPU reads in `frame:render` and `render:imgui`; see [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: stop recording, free later after WaitIdle](free-vulkan-resources-safely-1.md) | purrtty | Detach now, two-frame countdown, `WaitIdle`, nullable images, no device work at unload | `frame:draw-ui` | Simple and robust for runtime create/destroy; one drain per destroy |
| [2: retire by MaxFramesInFlight + 1](free-vulkan-resources-safely-2.md) | gatos | Counter-based retire queue, no idle | `frame:draw-ui`, `frame:after-on-frame` | No stall; images linger for a few frames |
| [3: publish after fence, retire after WaitIdle](free-vulkan-resources-safely-3.md) | unscience | Fenced upload, idle, swap, dispose in pre-GUI phase | `frame:draw-ui` (BeforeGui) | Atomic swaps and ImGui-safe; blocks on every change |

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Recreate mod render targets when KSA rebuilds its renderer](../handle-renderer-rebuilds/overview.md), [Show a PNG or runtime-generated image in an ImGui window](../../textures-and-decals/show-an-image-in-imgui/overview.md), [Record and submit your own Vulkan work: offscreen renders and one-shot uploads](../submit-your-own-vulkan-commands/overview.md)
- [Clean up on game exit and relaunch the game from a mod](../../../getting-started/lifecycle/clean-up-on-game-exit/overview.md), [Removing Harmony patches safely at runtime and at exit](../../../patching/safety/remove-your-patches-safely-at-runtime-and-at-exit/overview.md)
