---
type: Game Feature
title: Recreate mod render targets when KSA rebuilds its renderer
description: KSA recreates swapchain-sized targets on resize, MSAA changes and high-resolution screenshots, so mod pipelines and images tied to those extents must be rebuilt at the same point
category: rendering
group: vulkan-passes
topic: handle-renderer-rebuilds
demonstrated_by:
  - mods/shaderextensions/features/renderer-rebuild-hook
  - mods/purrtty/features/vulkan-resource-teardown-discipline
  - mods/unscience/features/pebbles-private-clutter-materials
lifecycle: [frame:prepare, frame:render, frame:post-render]
game_types: [KSA.Program, Core.Renderer, KSA.Rendering.RenderTarget]
tags: [vulkan, rebuild, resize, swapchain, msaa, screenshot]
---

The game rebuilds its render targets whenever the window size, present mode or MSAA setting changes, and for high-resolution screenshots. Every view, descriptor and pipeline a mod created against those
targets becomes stale. The mod can follow the game (hook the rebuild), or avoid the problem by owning targets the game never rebuilds.

## Engine background

- `Program.RebuildRenderer(bool skipSwapchain = false)` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5105-L5145): rebuild the swapchain, `Device.WaitIdle()`, resize the main viewport, rebuild `_offscreenTarget`, rebuild every renderer that holds
  views onto it, re-create the composite descriptor.
- Callers: `PrepareFrame` (`frame:prepare`), `Render` on a failed acquire (`frame:render`), `PostRender` on resize (`frame:post-render`), `ScreenshotCapture` for scaled captures; `Program.ScheduleRendererRebuild()` requests one.
- `Renderer.ExtentOverride` [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L42) replaces `Renderer.Extent` during a high-resolution capture [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L96).
- `MaxFramesInFlight` is 2 [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L28), which bounds how long an old image may still be read after a mod stops recording it.
- The full phase list is in [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: postfix RebuildRenderer](handle-renderer-rebuilds-1.md) | shaderextensions | `harmony-postfix` plus forced first rebuild and extent check | `frame:prepare`, `frame:render`, `frame:post-render` | Follows every game rebuild; stalls on `WaitIdle` and has no shutdown path |
| [2: rebuild in place on a live size change](handle-renderer-rebuilds-2.md) | purrtty | Pending resize, countdown, `WaitIdle`, rewrite descriptor | `frame:draw-ui`, `frame:render` | Safe for mod-owned sizes; does not follow game rebuilds |
| [3: re-create the private material pipeline](handle-renderer-rebuilds-3.md) | unscience | Prefix and finalizer re-enter a thread-static construction scope | `frame:dispatch-solvers`, rebuild phases | Keeps mod redirection through game rebuilds; fragile to game code changes |

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Free mod-owned Vulkan resources without device loss](../free-vulkan-resources-safely/overview.md), [Build a Vulkan pipeline compatible with KSA's scene render target](../build-a-vulkan-pipeline-for-the-scene-target/overview.md), [Capture rendered frames from KSA](../capture-rendered-frames/overview.md)
