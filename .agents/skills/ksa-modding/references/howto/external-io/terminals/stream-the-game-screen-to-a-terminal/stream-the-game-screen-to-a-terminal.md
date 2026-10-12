---
type: How-To
title: Staging-buffer readback on slot reuse, background KittyEncoder, fixed image id
description: Encode captured game frames into Kitty graphics units on a worker task, replace one fixed image id and pace the encoder to what the terminal consumes
category: external-io
group: terminals
topic: stream-the-game-screen-to-a-terminal
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/gatos/features/screen-stream-frame-capture]
techniques: [harmony-transpiler, background-thread]
hooks: [harmony-transpiler KSA.Program.RenderGame, harmony-prefix KSA.GameSettings.UiPixelCulling]
lifecycle: [frame:render, thread:background]
game_types: [KSA.Program, Core.Renderer]
tags: [kitty-graphics, screen-stream, terminal, encoder, backpressure, readback]
---

Outcome: the game's rendered frame appears inside any Kitty-graphics-capable terminal, including a tab in the game itself, by writing Kitty image escape sequences to the terminal's stdout. This article covers the encoding, delivery and pacing half: a raw BGRA frame arrives from the render thread, a background worker turns it into a self-contained Kitty unit that replaces one fixed image id, and the rate follows what the terminal can consume. The capture half (an IL transpiler before the final `End()` and a staging ring) is covered in [Capture rendered frames from KSA](../../../rendering/vulkan-passes/capture-rendered-frames/capture-rendered-frames-1.md). Prerequisites: StarMap, Harmony, a terminal that implements the Kitty graphics protocol. Demonstrated by [Screen stream frame capture](../../../../mods/gatos/features/screen-stream-frame-capture.md) in gatOS.

## Background: how the game does it

The game renders the scene into an offscreen colour image and composites it, plus ImGui, into the swapchain image near the end of `Program.RenderGame`. The offscreen image therefore never contains the UI, and the stream reads it through commands recorded into the engine's own command buffer, so the end of the method matters:

```csharp
// current/decomp/KSA/Program.cs L4946-L4954
		_screenshotCapture.OnRenderGameCapture(commandBuffer2, resourceFrameIndex, in _compositePushConstantData);
		RenderAaPreparePasses(commandBuffer2, shaderSlot, resourceFrameIndex);
		commandBuffer2.BeginRenderPass(in pRenderPassBegin, contents);
		RenderFinalComposite(commandBuffer2, shaderSlot, resourceFrameIndex);
		ImGuiBackend.Vulkan.RenderDrawData(commandBuffer2);
		commandBuffer2.EndRenderPass();
		_screenshotCapture.OnRenderGameSwapchainGrab(commandBuffer2, frameResources.ColorImage);
		Profiler.Gpu.EndFrame(commandBuffer2);
		commandBuffer2.End();
```

The engine cycles two frame slots; the capture ring is indexed by the same slot and read back when the slot comes round again, by which time the engine has waited on that slot's fence:

```csharp
// current/decomp/Core/Renderer.cs L28
	public int MaxFramesInFlight = 2;
```

```csharp
// current/decomp/KSA/Program.cs L2359
		ResourceFrameIndex = (ResourceFrameIndex + 1) % _renderer.MaxFramesInFlight;
```

KSA also has its own screenshot hooks in this method (`_screenshotCapture.OnRenderGameCapture`, `OnRenderGameSwapchainGrab`); the stream is a separate consumer of the same frame, not a use of that facility.[^ksa-program-cs]

## Lifecycle and timing

| Work | Where |
|---|---|
| Record the blit and copy into the engine's command buffer, read back the slot's previous copy, `SubmitFrame` | the transpiler hook inside `RenderGame` on the render (main) thread, `frame:render`; throttled to the stream FPS |
| Encode: swizzle, zlib, base64, Kitty framing | one worker task (`thread:background`) |
| Publish and fan out | volatile swap plus a `TaskCompletionSource`; readers never lock |
| Deliver | the 9p read handlers of the `stream` file, on 9p session tasks (`thread:background`) |
| Consume | the guest terminal (an SSH shell in the guest, shown in a purrTTY tab or an external terminal) |

The hook is cheap when the stream is off: it checks one boolean chain and returns. The capture only runs while the stream is enabled and at least one reader has the file open (`HasReaders`).

## Steps

1. **Gate everything on demand.** The injected call returns immediately unless the hook is bound, has not faulted, the stream is enabled and somebody is reading. A capture fault disables the feature for the session rather than unwinding into the engine's frame:

   ```csharp
   // gatOS.GameMod/Game/Ksa/DisplayRenderPatch.cs L55-L57, L162-L181
       private static bool IsCapturing
           => _active && !_faulted && _capture is not null
              && _surface is { Settings.Enabled: true, HasReaders: true };
   // ...
       internal static void OnRenderGameRecorded(Program program, CommandBuffer cb)
       {
           if (!IsCapturing)
               return;
           var capture = _capture;
           var surface = _surface;
           if (capture is null || surface is null)
               return; // Unbind() raced the check above

           try
           {
               capture.MaybeRecord(program, cb, surface);
           }
           catch (Exception ex)
           {
               _faulted = true;
               ModLog.Log.Error("gatOS screen stream disabled after a capture error "
                                + "(see <LogsDir>/display-capture.log for the per-step trace)", ex);
           }
       }
   ```

2. **Keep the stream parameters live and cheap to read.** Fps, size and encoding live in a lock-free object that control files and HTTP fields write and the render thread reads every frame. The stream defaults to off, 15 Hz, 320 by 180, zlib-deflated RGBA:

   ```csharp
   // gatOS.SimFs/Display/DisplaySettings.cs L44-L52
       public DisplaySettings(bool enabled = false, int fps = 15, int width = 320, int height = 180,
           DisplayEncoding encoding = DisplayEncoding.RgbaZlib)
       {
           _enabled = enabled;
           _fps = ClampFps(fps);
           _width = ClampEdge(width);
           _height = ClampEdge(height);
           _encoding = (int)encoding;
       }
   ```

3. **Hand the frame off with one copy.** After the slot's previous copy is read back, the capture passes the small BGRA block to the surface. `SubmitFrame` runs on the render thread and only copies the block into a reused buffer under a short lock and signals the worker; an undelivered earlier frame is overwritten (drop-old):

   ```csharp
   // gatOS.GameMod/Game/Ksa/FrameCapture.cs L430-L439
       private void Readback(int idx, DisplaySurface surface)
       {
           var dstW = _dstW[idx];
           var dstH = _dstH[idx];

           if (_mode == CaptureMode.GpuBlit)
           {
               var bytes = dstW * dstH * 4;
               surface.SubmitFrame(dstW, dstH, _mapped[idx].AsSpan<byte>()[..bytes]);
           }
   ```

   ```csharp
   // gatOS.SimFs/Display/DisplaySurface.cs L219-L240
       public void SubmitFrame(int width, int height, ReadOnlySpan<byte> bgra)
       {
           if (_stopping.IsCancellationRequested || width <= 0 || height <= 0)
               return;
           var needed = width * height * 4;
           if (bgra.Length < needed)
               return;

           lock (_inLock)
           {
               if (_inBuffer.Length < needed)
                   _inBuffer = new byte[needed];
               bgra[..needed].CopyTo(_inBuffer);
               _inWidth = width;
               _inHeight = height;
               _inHasFrame = true;
           }

           // Swap first, then complete (SnapshotStore ordering): a worker that captured the old signal
           // wakes and re-checks _inHasFrame; one already on the new signal waits for the next submit.
           Interlocked.Exchange(ref _inSignal, NewSignal()).TrySetResult();
       }
   ```

4. **Pace the encoder to demand.** When readers exist but none is parked waiting for the next frame (all still draining the previous one), the worker drops the captured frame without encoding it, since drop-old would discard it anyway. The encode rate therefore falls to the rate the transport really consumes:

   ```csharp
   // gatOS.SimFs/Display/DisplaySurface.cs L354-L363
                   // Demand pacing (P3): encoding a frame no reader is waiting for is pure waste under
                   // saturation — drop-old means it would be skipped anyway. Readers mid-drain re-park
                   // when they finish and pick up the NEXT capture (≤ one capture interval of added
                   // latency). The bootstrap frame (sequence 0 → 1) always encodes so the feed starts
                   // even before the first read parks, and the dump/debug branch above is exempt.
                   if (_current.Sequence > 0 && HasReaders && Volatile.Read(ref _parkedWaiters) == 0)
                   {
                       Interlocked.Increment(ref _encodeSkips);
                       continue;
                   }
   ```

5. **Suppress identical frames, but keep a heartbeat.** A byte-identical frame is coalesced (an exact compare), so a still scene costs no encoding and no wire. A keyframe is still due about once a second, when a reader is new, or when size or encoding change, so late joiners and a terminal that lost its image recover quickly:

   ```csharp
   // gatOS.SimFs/Display/DisplaySurface.cs L373-L382
                   var keyframeDue = Volatile.Read(ref _forceKeyframe) == 1
                                     || width != _kfWidth || height != _kfHeight || encoding != _kfEncoding
                                     || Stopwatch.GetTimestamp() - _lastKeyframeTs >= KeyframeIntervalTicks;
                   if (!keyframeDue && _current.Sequence > 0
                       && width == _previousWidth && height == _previousHeight
                       && _previousPixels.AsSpan(0, length).SequenceEqual(work.AsSpan(0, length)))
                   {
                       Interlocked.Increment(ref _staticSkips);
                       continue;
                   }
   ```

6. **Choose keyframe or replace form, then encode into a pooled buffer.** `a=T` transmits and displays (it creates the placement); `a=t` only transmits and replaces the stored image. Both use the same fixed image id. The unit is encoded straight into a pooled buffer, so the steady state allocates nothing:

   ```csharp
   // gatOS.SimFs/Display/DisplaySurface.cs L393-L424
                   // One fixed image id, re-transmitted each frame with no delete (the terminal replaces
                   // the image in place — see KittyEncoder). Keyframe (a=T) only when a placement must be
                   // [re]created; steady state is a=t replace frames (no per-frame placement churn).
                   var display = Interlocked.Exchange(ref _forceKeyframe, 0) == 1
                                 || width != _kfWidth || height != _kfHeight || encoding != _kfEncoding
                                 || Stopwatch.GetTimestamp() - _lastKeyframeTs >= KeyframeIntervalTicks;

                   // Encode straight into a pooled frame buffer (zero steady-state allocation; the
                   // buffer returns to the pool when the publisher and every reader release the frame).
                   var buffer = FrameBuffers.Rent(KittyEncoder.GetMaxEncodedLength(width, height, encoding));
                   int encodedLength;
                   try
                   {
                       using (EncodeStat.Measure())
                           encodedLength = KittyEncoder.EncodeFrame(
                               width, height, work.AsSpan(0, length), encoding, display, KittyEncoder.VideoImageId, buffer);
                   }
                   catch
                   {
                       FrameBuffers.Return(buffer);
                       throw;
                   }

                   if (display)
                   {
                       _lastKeyframeTs = Stopwatch.GetTimestamp();
                       _kfWidth = width;
                       _kfHeight = height;
                       _kfEncoding = encoding;
                   }

                   Publish(buffer, encodedLength, FrameBuffers);
   ```

7. **Frame the Kitty unit.** The encoder swizzles BGRA to RGBA with a SIMD shuffle, optionally zlib-deflates, then writes cursor-save and home, the chunked `ESC _ G ... ESC \` escapes (all control keys on the first chunk, `m=1` while more follow, `m=0` on the last) and cursor-restore. Chunks carry 3000 raw bytes, which encode to exactly 4000 base64 characters with no interior padding, so each escape stays under the 4096 byte limit terminals and ptys apply:

   ```csharp
   // gatOS.SimFs/Display/KittyEncoder.cs L238-L276
       private static int WriteFrame(Span<byte> dest, int width, int height, int imageId, bool zlib,
           bool display, ReadOnlySpan<byte> payload)
       {
           var pos = 0;
           dest[pos++] = Esc;
           dest[pos++] = (byte)'7'; // save cursor
           dest[pos++] = Esc;
           dest[pos++] = (byte)'[';
           dest[pos++] = (byte)'H'; // home

           var offset = 0;
           var first = true;
           // An empty payload (impossible here — frames have pixels) would still need one m=0 escape.
           do
           {
               var take = Math.Min(ChunkRawBytes, payload.Length - offset);
               var last = offset + take >= payload.Length;

               dest[pos++] = Esc;
               dest[pos++] = (byte)'_';
               dest[pos++] = (byte)'G';
               pos += first
                   ? WriteFirstHeader(dest[pos..], width, height, imageId, zlib, display, more: !last)
                   : WriteContinuationHeader(dest[pos..], last);
               dest[pos++] = (byte)';';
               Base64.EncodeToUtf8(payload.Slice(offset, take), dest[pos..], out _, out var written);
               pos += written;
               dest[pos++] = Esc;
               dest[pos++] = (byte)'\\';

               offset += take;
               first = false;
           }
           while (offset < payload.Length);

           dest[pos++] = Esc;
           dest[pos++] = (byte)'8'; // restore cursor
           return pos;
       }
   ```

   The first chunk's header carries the control keys; only keyframes carry the placement id and `C=1` (do not advance the cursor):

   ```csharp
   // gatOS.SimFs/Display/KittyEncoder.cs L278-L307
       private static int WriteFirstHeader(Span<byte> dest, int width, int height, int imageId, bool zlib,
           bool display, bool more)
       {
           // a=T transmit+display (keyframe) or a=t transmit-only (replace frame), q=2 suppress
           // responses, f=32 RGBA, i the fixed image id, s/v pixel dims (required for raw/zlib RGBA),
           // o=z when deflated. p (placement id) and C=1 (don't advance the cursor) belong to the
           // display step, so only keyframes carry them.
           var pos = WriteAscii(dest, 0, "a=");
           dest[pos++] = display ? (byte)'T' : (byte)'t';
           pos = WriteAscii(dest, pos, ",q=2,i=");
           pos = WriteInt(dest, pos, imageId);
           if (display)
           {
               pos = WriteAscii(dest, pos, ",p=");
               pos = WriteInt(dest, pos, imageId);
           }

           pos = WriteAscii(dest, pos, ",f=32");
           if (zlib)
               pos = WriteAscii(dest, pos, ",o=z");
           pos = WriteAscii(dest, pos, ",s=");
           pos = WriteInt(dest, pos, width);
           pos = WriteAscii(dest, pos, ",v=");
           pos = WriteInt(dest, pos, height);
           if (display)
               pos = WriteAscii(dest, pos, ",C=1");
           pos = WriteAscii(dest, pos, ",m=");
           dest[pos++] = more ? (byte)'1' : (byte)'0';
           return pos;
       }
   ```

8. **Replace, never delete.** Re-transmitting an existing id replaces the stored image at commit, so the previous frame stays visible while the next one loads. The earlier delete-then-transmit design blanked the stream at almost every terminal tick. The unit contains no line feed, so a cooked pty cannot corrupt it:

   ```csharp
   // gatOS.SimFs/Display/KittyEncoder.cs L54, L64, L71
       public const int VideoImageId = 1;
   // ...
       private const int ChunkBytes = 4000;
   // ...
       private const int ChunkRawBytes = 3000;
   ```

9. **Publish by reference swap and let each reader skip ahead.** The worker publishes the newest frame; a reader waits for any frame with a higher sequence, so a slow reader resumes at the latest frame. Frames are pooled and reference-counted so many readers share one buffer:

   ```csharp
   // gatOS.SimFs/Display/DisplaySurface.cs L464-L471
       private void Publish(byte[] bytes, int length, ArrayPool<byte>? pool)
       {
           var frame = new EncodedFrame(Interlocked.Increment(ref _sequence), bytes, length, pool);
           var previous = _current;
           _current = frame;
           Interlocked.Exchange(ref _outSignal, NewSignal()).TrySetResult();
           previous.Release(); // the current-frame slot's reference to the replaced frame
       }
   ```

   The delivery file never returns a zero-byte read, so `cat` renders frame after frame forever; the kernel only completes a guest `read` when its buffer fills, so latency is buffer size divided by data rate:

   ```csharp
   // gatOS.SimFs/Display/DisplayStreamFile.cs L99-L114
           public async ValueTask<int> ReadAsync(ulong offset, Memory<byte> destination, CancellationToken ct)
           {
               await _readGate.WaitAsync(ct).ConfigureAwait(false);
               try
               {
                   await EnsurePendingAsync(ct).ConfigureAwait(false);
                   var length = Math.Min(destination.Length, _pending.Length - _pendingAt);
                   _pending.Memory.Slice(_pendingAt, length).Span.CopyTo(destination.Span);
                   _pendingAt += length;
                   return length; // always > 0 — never EOF
               }
               finally
               {
                   _readGate.Release();
               }
           }
   ```

10. **Let the terminal's consumption close the loop.** The receiving surface bounds its inbox at 24 MiB, makes producers wait up to 500 ms rather than drop, and raises the Kitty image storage budget so a same-id replace happens in place. The SSH output pump ([gatOS.Ssh/SshShellChannel.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Ssh/SshShellChannel.cs#L13-L20)) is a dedicated thread that blocks when the surface blocks; the SSH window then fills and the guest's `cat` blocks on its stdout, so reads of the 9p file stop, the encoder has no parked reader and skips frames at the source:

   ```csharp
   // purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs L104-L114, L116-L124
       // Safety net: the inbox never grows past this. The PTY pumps never sleep
       // (by design) and the inbox only drains on the tick, so a session that is
       // not being ticked (hidden terminal, inactive tab) would otherwise grow
       // without bound under chatty output. The frontend now ticks every session,
       // and a pump thread that fills the inbox now WAITS for the drain
       // (backpressure, see Write) — so hitting the drop path means the tick has
       // stalled past the wait budget; drop is the last resort, not the regulator.
       // Sized to hold >=3 full gatOS screen-stream frame units at their largest
       // (~7 MB at 1440x900 raw), so the lossless park — not the byte-severing
       // drop — stays the steady mechanism across GC hitches (perf plan P5).
       internal const int MaxInboxBytes = 24 * 1024 * 1024;
   // ...
       // The kitty-graphics image storage budget handed to the native engine at
       // construction. The lib-artifact native defaults to 10 MB — below TWO frames
       // of a large kitty video stream (the gatOS screen stream reaches ~5.2 MB per
       // 1440x900 raw frame, and ghostty's limit check runs BEFORE the same-id
       // replace frees the old copy), so every video frame triggered a full
       // evict+realloc of the image and leaked a tracked pin through the eviction
       // path. 256 MiB restores the true in-place replace the fixed-id video
       // pattern is designed around (gatOS PERF_IMPROVEMENT_PLAN.md P0.2, R2/R3).
       internal const ulong KittyImageStorageLimitBytes = 256UL * 1024 * 1024;
   ```

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/DisplayRenderPatch.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/DisplayRenderPatch.cs) | IL transpiler, capture gate, UI-culling prefix |
| [gatOS.GameMod/Game/Ksa/FrameCapture.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/FrameCapture.cs) | In-band blit and copy, slot ring, deferred readback, hand-off to the surface |
| [gatOS.SimFs/Display/DisplaySurface.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Display/DisplaySurface.cs) | Latest-frame feed, encode worker, demand pacing, static-frame suppression, pooled frames |
| [gatOS.SimFs/Display/KittyEncoder.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Display/KittyEncoder.cs) | Kitty framing, keyframes, zlib, SIMD swizzle |
| [gatOS.SimFs/Display/DisplayStreamFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Display/DisplayStreamFile.cs) | Multi-reader never-EOF file |
| [gatOS.SimFs/Display/DisplaySettings.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Display/DisplaySettings.cs) | Live parameters |

## Variations and gotchas

- **UI culling punches holes in the capture.** The offscreen image has no UI, but the engine's UI coverage mask stamps the near plane into depth under opaque ImGui areas and later passes skip those tiles, so the captured scene shows them as unshaded gaps. gatOS reports `GameSettings.UiPixelCulling()` as false while the stream is live; the mechanism and its fragility are explained in [Capture rendered frames from KSA](../../../rendering/vulkan-passes/capture-rendered-frames/overview.md).
- **Never submit a private command buffer or wait for the device per frame to read back.** Record into the engine's buffer and read the slot when it is reused.
- **A terminal that fails the escape budget corrupts the base64.** Keep every escape well below 4096 bytes; 4000 characters per chunk leaves room for the first header.
- **Rendering stays on the main thread.** Only the encode and delivery run elsewhere; the render thread does a bulk copy and a signal.
- **Check the pacing counters.** `EncodeSkips` and `StaticSkips` tell you whether the transport or a still scene is limiting the rate.
- **This path is version-sensitive.** It depends on the tail of `RenderGame` and on `ColorImage` being non-null; the transpiler leaves the method unchanged if the injection site is not found.

## See also

- [Overview](overview.md)
- [Screen stream frame capture](../../../../mods/gatos/features/screen-stream-frame-capture.md)
- [Capture rendered frames from KSA](../../../rendering/vulkan-passes/capture-rendered-frames/overview.md)
- [Host a terminal with pty sessions and frame handoff](../host-a-terminal-with-pty-sessions-and-frame-handoff/overview.md): the inbox and the SSH output pump this stream stresses
- [Show an image in ImGui](../../../rendering/textures-and-decals/show-an-image-in-imgui/overview.md)

[^ksa-program-cs]: KSA decomp: KSA/Program.cs (build 2026.10.10.5554)
