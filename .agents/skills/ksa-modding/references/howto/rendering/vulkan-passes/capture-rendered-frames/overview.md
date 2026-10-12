---
type: Game Feature
title: Capture rendered frames from KSA
description: Grab the finished scene for streaming or automated visual testing without stalling the GPU by recording a copy in the frame command buffer and reading it back when the staging slot is reused, and render part thumbnails at runtime with the game thumbnail pipeline
category: rendering
group: vulkan-passes
topic: capture-rendered-frames
demonstrated_by:
  - mods/gatos/features/screen-stream-frame-capture
  - mods/unscience/features/parts-now-thumbnail-generation
lifecycle: [frame:render, frame:post-render, thread:background, frame:draw-ui, frame:pre-render]
game_types:
  - KSA.Program
  - KSA.ScreenshotCapture
  - Core.Renderer
  - KSA.Rendering.ThumbnailCreator
  - KSA.Rendering.Thumbnails.ThumbnailDynamic
  - KSA.PartThumbnailViewport
tags: [capture, readback, thumbnails, staging, screenshot]
---

Reading pixels back from the GPU safely means recording a copy into a command buffer and not reading the destination before the GPU is done. KSA offers its own `ScreenshotCapture` for scripted screenshots; for a live stream or
for images the game has no API for (a runtime part thumbnail) mods record their own copies. Two techniques are demonstrated: appending the copy to the engine's frame command buffer and reading each slot back when the engine reuses it, and rendering a
thumbnail on a private command buffer with a fence wait.

## Engine background

- The end of `RenderGame` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4945-L4955): offscreen colour to `SampledReadVfc`, `_screenshotCapture.OnRenderGameCapture`, AA passes, swapchain pass with composite and ImGui, swapchain grab, `End()`.
- `ScreenshotCapture` [current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L272) sets `Renderer.ExtentOverride` [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L42) for scaled captures and triggers renderer rebuilds.
- `ThumbnailCreator` [current/decomp/KSA.Rendering/ThumbnailCreator.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ThumbnailCreator.cs#L12) and `ThumbnailDynamic` [current/decomp/KSA.Rendering.Thumbnails/ThumbnailDynamic.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Thumbnails/ThumbnailDynamic.cs#L12) render part thumbnails with a `PartThumbnailViewport` [current/decomp/KSA/PartThumbnailViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartThumbnailViewport.cs#L6).
- Slots: `MaxFramesInFlight` is 2 [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L28), `Program.ResourceFrameIndex` indexes the slot.
- Phases: see [the render sequence of the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence). Driving `ScreenshotCapture` itself is covered in take-screenshots-from-code (other category).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: transpiler before the final End](capture-rendered-frames-1.md) | gatos | Blit and copy recorded in the frame buffer, per-slot staging ring, background encode | `frame:render`, `thread:background` | No stall, no second submit; fragile IL anchor, latency of a couple of frames |
| [2: ThumbnailCreator framing and per-frame submit](capture-rendered-frames-2.md) | unscience | Game thumbnail helpers, private pool, fence wait, optional readback | `frame:draw-ui` before `frame:pre-render` | Reuses game framing; blocks the main thread per part |

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Insert a custom Vulkan pass into KSA's frame](../insert-a-custom-pass-into-render-game/overview.md), [Recreate mod render targets when KSA rebuilds its renderer](../handle-renderer-rebuilds/overview.md), [Record and submit your own Vulkan work: offscreen renders and one-shot uploads](../submit-your-own-vulkan-commands/overview.md), [Reserve or grow game GPU buffers for content added at runtime](../../meshes/grow-game-gpu-buffers-for-runtime-content/overview.md), [Take clean screenshots from code, with a composed camera shot held for the capture](../../../testing-and-tooling/in-game-testing/take-screenshots-from-code/overview.md)
