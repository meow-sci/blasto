---
type: How-To
title: Transpiler before the final End, blit into per-slot staging buffers, readback on slot reuse
description: Record a downscaling blit and a buffer copy into the frame command buffer just before it ends, then read each slot back when the engine reuses it, with no stall and no second submit
category: rendering
group: vulkan-passes
topic: capture-rendered-frames
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/gatos/features/screen-stream-frame-capture]
techniques: [harmony-transpiler, vulkan-buffer]
hooks:
  - harmony-transpiler KSA.Program.RenderGame
  - harmony-prefix KSA.GameSettings.UiPixelCulling
  - starmap StarMapAllModsLoaded
lifecycle: [frame:render, frame:post-render, thread:background]
game_types: [KSA.Program, KSA.ScreenshotCapture, Core.Renderer, KSA.Rendering.RenderTarget]
tags: [harmony, transpiler, capture, blit, readback, staging-ring, streaming]
---

Outcome: a live, frame-rate-limited, downscaled copy of the game picture (without UI) available to the CPU for streaming or automated visual testing, without waiting for the GPU or submitting a second command buffer. gatOS uses it to encode frames
with the Kitty graphics protocol for `/sim/display/stream`. The technique is: append copy commands to the engine's own command buffer at the very end of `RenderGame`, give each frame-in-flight slot its own host-visible staging buffer, and read slot N back when
slot N comes round again. Demonstrated by [Screen stream frame capture](../../../../mods/gatos/features/screen-stream-frame-capture.md). Requires StarMap and Harmony. Driving KSA's own `ScreenshotCapture` for clean scripted frames lives in the take-screenshots-from-code topic.

## Background: how the game does it

In this build the tail of `RenderGame` is: full resolve, grid, gauges, a barrier taking the offscreen colour image to `SampledReadVfc`, `_screenshotCapture.OnRenderGameCapture`, the AA passes, the swapchain pass with composite and ImGui, the swapchain grab, and finally
`commandBuffer2.End()`:

```csharp
// current/decomp/KSA/Program.cs L4945-L4955
	commandBuffer2.PipelineBarrier2(_offscreenTarget.ColorImage, ImageBarrierInfo.Presets.SampledReadVfc);
	_screenshotCapture.OnRenderGameCapture(commandBuffer2, resourceFrameIndex, in _compositePushConstantData);
	RenderAaPreparePasses(commandBuffer2, shaderSlot, resourceFrameIndex);
	commandBuffer2.BeginRenderPass(in pRenderPassBegin, contents);
	RenderFinalComposite(commandBuffer2, shaderSlot, resourceFrameIndex);
	ImGuiBackend.Vulkan.RenderDrawData(commandBuffer2);
	commandBuffer2.EndRenderPass();
	_screenshotCapture.OnRenderGameSwapchainGrab(commandBuffer2, frameResources.ColorImage);
	Profiler.Gpu.EndFrame(commandBuffer2);
	commandBuffer2.End();
}
```

At that point the offscreen colour image is resolved, shader-readable, outside any render pass and free of UI. The engine already waits on a frame slot's fence before reusing that slot, so a copy recorded for slot N is complete the next time slot N is recorded
([current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L28): two frames in flight). KSA also has its own capture machinery, `ScreenshotCapture` [current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L272), which sets `Renderer.ExtentOverride` for scaled captures [current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L237).

## Lifecycle and timing

| Step | Where |
|---|---|
| Install the transpiler | `[StarMapAllModsLoaded]` (`startup:all-mods-loaded`) |
| Record blit and copy | `frame:render`: the inserted call runs at the end of `RenderGame`, on the main thread, before submission |
| GPU executes the copy | with the frame, after `PostRender` submits it |
| Read slot N back | when slot N is reused, about `MaxFramesInFlight` frames later |
| Encode and deliver | background task (`thread:background`); only the newest frame is published |

See [the render sequence](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence) for the surrounding steps and [handle renderer rebuilds](../handle-renderer-rebuilds/overview.md): `Renderer.Extent` follows an extent override during a scaled capture.

## Steps

1. **Transpile `RenderGame`** to find the last one-argument `End` declared in `Brutal.VulkanApi` (the frame command buffer's `End()`), copy the instruction that loads its receiver, and insert `ldarg.0; <load commandBuffer>; call OnRenderGameRecorded` before it, moving any branch labels
   onto the inserted code. If the site is not found, return the IL unchanged and leave the feature off.

   ```csharp
   // gatOS.GameMod/Game/Ksa/DisplayRenderPatch.cs L214-L255
   private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
   {
       var codes = new List<CodeInstruction>(instructions);

       var callIdx = -1;
       for (var i = codes.Count - 1; i >= 1; i--)
       {
           if (codes[i].opcode == OpCodes.Call
               && codes[i].operand is MethodInfo mi
               && mi.Name == "End"
               && mi.GetParameters().Length == 1
               && mi.DeclaringType?.Namespace == "Brutal.VulkanApi")
           {
               callIdx = i;
               break;
           }
       }

       if (callIdx < 1)
       {
           ModLog.Log.Warn("Display hook: CommandBuffer.End() call not found in RenderGame; "
                           + "the screen stream is disabled (capture not injected).");
           return codes;
       }

       var receiverLoad = codes[callIdx - 1]; // loads commandBuffer2 (the End receiver) by value
       var hook = AccessTools.Method(typeof(DisplayRenderPatch), nameof(OnRenderGameRecorded));
       var inserts = new List<CodeInstruction>
       {
           new(OpCodes.Ldarg_0),                                      // Program this
           new(receiverLoad.opcode, receiverLoad.operand),           // commandBuffer2
           new(OpCodes.Call, hook),
       };

       // Carry any branch labels from the receiver load onto our first inserted instruction so a jump
       // to that position still runs the hook (then falls through into the original End sequence).
       inserts[0].labels.AddRange(receiverLoad.labels);
       receiverLoad.labels.Clear();

       codes.InsertRange(callIdx - 1, inserts);
       return codes;
   }
   ```

2. **Throttle and gate** in the hook: respect the configured stream rate, require a stream consumer, require `Program.MainViewport?.OffscreenTarget` with a non-null `ColorImage` (a depth-only target has none), and clamp the destination size to the source so you only shrink.

   ```csharp
   // gatOS.GameMod/Game/Ksa/FrameCapture.cs L136-L183
   public void MaybeRecord(Program program, CommandBuffer cb, DisplaySurface surface)
   {
       var settings = surface.Settings;

       var now = Stopwatch.GetTimestamp();
       var minTicks = Stopwatch.Frequency / Math.Max(1, settings.Fps);
       if (_lastCaptureTs != 0 && now - _lastCaptureTs < minTicks)
           return; // throttle to the configured stream FPS, decoupled from the game frame rate

       var renderer = Program.GetRenderer();
       if (renderer is null)
           return;
       if (Program.MainViewport?.OffscreenTarget is not { } offscreen)
           return;
       // Since rev 5154 the offscreen target is the dynamic-rendering KSA.Rendering.RenderTarget, whose
       // ColorImage is nullable (a depth-only target has none). No color attachment => nothing to capture.
       if (offscreen.ColorImage is not { } color)
           return;

       var srcExtent = offscreen.Extent;
       if (srcExtent.Width <= 0 || srcExtent.Height <= 0)
           return;

       if (_mode == CaptureMode.Undecided)
       {
           _mode = DetectMode(renderer.PhysicalDevice, color.Format);
           Trace($"capture mode: {_mode} (offscreen format {color.Format})");
           // Surface the decision in the main log (GP6): a driver silently missing blit support
           // would otherwise eat 30-80 ms per captured frame on the render thread unnoticed.
           if (_mode == CaptureMode.CpuFullRes)
               ModLog.Log.Warn("gatOS display capture: GPU blit unsupported by this driver/format — "
                               + "falling back to the slow CPU convert path.");
           else
               ModLog.Log.Info("gatOS display capture: GPU blit downscale active.");
       }

       // Clamp the downscale target to the source so we only ever shrink.
       var dstW = Math.Clamp(settings.Width, 1, srcExtent.Width);
       var dstH = Math.Clamp(settings.Height, 1, srcExtent.Height);

       using (surface.CaptureStat.Measure()) // render-thread cost: record + the deferred copy hand-off
           RecordInto(renderer.Allocator, cb, program.ResourceFrameIndex, renderer.MaxFramesInFlight,
               color.Image, srcExtent.Width, srcExtent.Height, dstW, dstH, surface);
       _lastCaptureTs = now;
   }

   /// <summary>
   ///     One-time capability probe: the blit path needs BLIT_SRC + linear filtering on the offscreen
   ```

3. **Record the copy** into the engine's command buffer: transition the offscreen image `SampledReadVfc -> TransferSrc` and a small scratch image to `TransferDst` (KSA's `TransitionImages2` and `ImageBarrierInfo.Presets`), `BlitImage` with linear filtering into a `B8G8R8A8_UNORM` scratch image
   (downscale and format in one GPU op), `CopyImageToBuffer` into this slot's host-visible staging buffer, then restore the offscreen layout. If the driver lacks blit support for the format pair, fall back to a full-resolution copy and CPU conversion.

4. **Index the staging ring by `Program.ResourceFrameIndex`** and map each buffer once, keeping it mapped. When a slot comes round again the previous copy is complete and can be read from the persistent mapping with no wait of your own.

5. **Encode off the render thread.** `SubmitFrame` on the render thread swaps a buffer and signals; a worker does the swizzle, compression and framing, publishing only the latest frame so slow readers skip ahead.

6. **Neutralise UI culling.** KSA stamps near-plane depth under opaque ImGui windows (`UiCoverageMaskSystem`), which would punch black holes into a capture taken before the UI composite. gatOS prefixes `GameSettings.UiPixelCulling()` (its only caller is that system) to return
   `false` while the stream is live, leaving the saved setting untouched.

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/DisplayRenderPatch.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/DisplayRenderPatch.cs) | Transpiler, UI-culling prefix, hook entry |
| [gatOS.GameMod/Game/Ksa/FrameCapture.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/FrameCapture.cs) | Blit, copy, staging ring, readback |
| [gatOS.SimFs/Display/KittyEncoder.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Display/KittyEncoder.cs) | Encoder on the worker task |

## Variations and gotchas

- **Never submit your own command buffer or call `WaitIdle` per frame to read back;** an out-of-band approach was shown to crash the game. Record into the engine's buffer and read a slot when it is reused.
- **Map staging buffers once.** Re-mapping per frame can unmap a shared VMA block.
- **Anchor on `End()`, not on the AA or composite calls.** The transpiler still targets the final `End()` after the build added a KSA screenshot-capture call just before the AA passes.
- **`ColorImage` can be null** for depth-only targets; the capture checks it.
- **Transpiler fragility.** If the `End` site moves, the feature stays off (with a warning); nothing breaks.
- **Only one pass of capture per frame** and only the main viewport's offscreen target.

## See also

- [Overview](overview.md)
- [Approach 2: ThumbnailCreator framing and per-frame submit](capture-rendered-frames-2.md)
- [Screen stream frame capture](../../../../mods/gatos/features/screen-stream-frame-capture.md)
- [Insert a custom Vulkan pass into KSA's frame](../insert-a-custom-pass-into-render-game/overview.md), [Recreate mod render targets when KSA rebuilds its renderer](../handle-renderer-rebuilds/overview.md), [Record and submit your own Vulkan work: offscreen renders and one-shot uploads](../submit-your-own-vulkan-commands/overview.md), [Take clean screenshots from code, with a composed camera shot held for the capture](../../../testing-and-tooling/in-game-testing/take-screenshots-from-code/overview.md)
