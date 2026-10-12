---
type: How-To
title: Rebuild targets in place on a live size change
description: Stop recording, wait out the frames in flight, then rebuild the mod's own offscreen target and rewrite its descriptor under a device wait
category: rendering
group: vulkan-passes
topic: handle-renderer-rebuilds
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/purrtty/features/vulkan-resource-teardown-discipline]
techniques: [vulkan-texture]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:render]
game_types: [KSA.Program, Core.Renderer]
tags: [vulkan, resize, descriptor, wait-idle, frames-in-flight]
---

Outcome: a mod-owned image that changes size at runtime (a terminal texture whose grid is resized) is rebuilt safely while the game keeps running: new images and views, a rewritten descriptor,
no use-after-free. The same discipline applies when a mod's targets follow the window: decide when to stop recording, how long to wait, and what the rebuild replaces. Demonstrated by purrTTY's
in-world terminals ([Safe Vulkan teardown and live resize for mod-owned GPU resources](../../../../mods/purrtty/features/vulkan-resource-teardown-discipline.md)). The destroy-side rules are in [free mod-owned Vulkan resources safely](../free-vulkan-resources-safely/overview.md).

## Background: how the game does it

The game keeps `MaxFramesInFlight = 2` [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L28): a command buffer recorded this frame is still executing on the GPU during the next one. Anything the scene pass recorded against the old
image or descriptor is therefore alive for up to two frames after the mod stops recording it. The game's own answer to this is `Device.WaitIdle()` inside `RebuildRenderer` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5105-L5112).

## Lifecycle and timing

| Operation | Thread and phase |
|---|---|
| Request the resize | main thread, `frame:draw-ui` (a dialog or layout apply) |
| Stop recording | immediately: the draw hook iterates the live list and skips instances with a pending resize (`frame:render`) |
| Apply the resize | main thread, start of the next `[StarMapAfterGui]` pass, two frames later, after `Device.WaitIdle()` |

## Steps

1. Commit the new size and mark it pending. If the clamped extent equals the current one, cancel any pending resize and return.

   ```csharp
   // purrTTY.GameMod/InWorld/InWorldTerminalInstance.cs L221-L258
   public bool TrySetGridSize(int cols, int rows)
   {
       if (_disposed || IsFailed || _target == null)
       {
           return false;
       }

       cols = Math.Clamp(cols, 8, 400);
       rows = Math.Clamp(rows, 4, 200);

       // Same extent derivation as the constructor (cell measured on the live
       // ImGui frame's shared atlas, clamped to the GPU texture range).
       var (cellWidth, cellHeight) = InWorldTerminalRenderer.MeasureCell(_config, _catalog, _record.ThemeName);
       int width = Math.Clamp((int)MathF.Ceiling(cols * cellWidth), 256, 4096);
       int height = Math.Clamp((int)MathF.Ceiling(rows * cellHeight), 256, 4096);

       _record.Cols = cols;
       _record.Rows = rows;

       if (width == (int)_target.Extent.Width && height == (int)_target.Extent.Height)
       {
           // The clamped extent is unchanged, and the grid is derived from the
           // texture — nothing to rebuild. Also cancels a superseded pending
           // resize that pointed back at the current extent.
           _pendingResize = null;
           return true;
       }

       // +1: the countdown decrements in Frame() later this same OnAfterGui pass,
       // whereas the teardown queue's first decrement is a frame after Remove —
       // this keeps both drains the same length.
       _pendingResize = (width, height);
       _resizeFramesLeft = InWorldTerminalManager.TeardownDelayFrames + 1;
       return true;
   }

   // Applies a deferred live resize. By now every scene command buffer that could
   // reference the current quad texture has been submitted (RecordDraw skipped while
   ```

2. Stop recording: the postfix that draws the instance skips it while `_pendingResize` is set, so no new command buffer references the old image.

3. Count down the frames in flight. When the countdown reaches zero, wait for the device and rebuild in place: new images, views, render pass, framebuffer and sampler, then rewrite the descriptor set that
   points at the target.

   ```csharp
   // purrTTY.GameMod/InWorld/InWorldTerminalInstance.cs L140-L150
       _registered = TerminalTargetRegistry.Register(this);
   }

   /// <summary>Drives one off-screen terminal frame (which the world-space quad samples).</summary>
   public void Frame(double dt)
   {
       if (_pendingResize is { } extent && --_resizeFramesLeft <= 0)
       {
           ApplyResize(extent.Width, extent.Height);
           _pendingResize = null;
       }
   ```

   ```csharp
   // purrTTY.GameMod/InWorld/InWorldTerminalInstance.cs L264-L270
   private void ApplyResize(int width, int height)
   {
       _renderer.Device.WaitIdle();
       _target!.Resize(width, height);
       _ctx!.Resize(width, height);
       _quad!.RebindTarget();
   }
   ```

4. Keep what stays valid. purrTTY does not rebuild the secondary ImGui backend: its pipeline was created against the first render pass, and a recreated pass with identical formats and sample count is render-pass
   compatible. If you change a format during resize, rebuild those pipelines too.

5. On the next frame the consumer sees the new extent (for a terminal: the engine grid and PTY resize, so the program inside reflows).

## Complete example

| File | Role |
|---|---|
| [purrTTY.GameMod/InWorld/InWorldTerminalInstance.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/InWorldTerminalInstance.cs) | Pending resize, countdown, `ApplyResize` |
| [purrTTY.GameMod/InWorld/OffscreenRenderTarget.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/OffscreenRenderTarget.cs) | `Resize`: new images, views, pass, framebuffer, sampler |
| [purrTTY.GameMod/InWorld/Display/InWorldQuad.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Display/InWorldQuad.cs) | `RebindTarget` rewrites the descriptor |

## Variations and gotchas

- **Two frames is enough** for the frames in flight at this build; if you share objects with the game across more frames, size the delay from the swapchain or frame-resource count.
- **Hold images as nullable.** `ImageEx` is a readonly struct whose `Dispose` throws on `default`; keep allocations as `ImageEx?` and dispose with `?.Dispose()`, especially in a constructor that calls `Resize`.
- **`WaitIdle` stalls the CPU** until the GPU drains; acceptable on resize, not per frame.
- **This is for your own targets.** For targets the game rebuilds, hook the rebuild instead (approach 1).

## See also

- [Overview](overview.md)
- [Approach 1: postfix RebuildRenderer](handle-renderer-rebuilds-1.md)
- [Approach 3: re-create the private material pipeline](handle-renderer-rebuilds-3.md)
- [Safe Vulkan teardown and live resize for mod-owned GPU resources](../../../../mods/purrtty/features/vulkan-resource-teardown-discipline.md)
- [Free mod-owned Vulkan resources without device loss](../free-vulkan-resources-safely/overview.md)
