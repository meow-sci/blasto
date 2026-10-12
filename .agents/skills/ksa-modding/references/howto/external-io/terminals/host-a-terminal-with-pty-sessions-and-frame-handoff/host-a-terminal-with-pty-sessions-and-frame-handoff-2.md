---
type: How-To
title: Bounded byte inbox with backpressure and dirty-aware frame snapshots
description: Post terminal bytes from any thread into a bounded inbox and build a dirty-aware frame snapshot on the tick without KSA or ImGui types in the backend
category: external-io
group: terminals
topic: host-a-terminal-with-pty-sessions-and-frame-handoff
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/purrtty/features/headless-terminal-surface-and-frame-snapshot]
techniques: [background-thread]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, thread:background]
game_types: [KSA.Program]
tags: [terminal, inbox, backpressure, snapshot, dirty-tracking, testability]
---

Outcome: a terminal backend that knows nothing about ImGui, Vulkan or KSA. Any thread posts bytes into a bounded inbox; once per tick the frontend asks for a `TerminalFrame` snapshot, and the backend does work only when something changed. A byte flood from a chatty program throttles its producer instead of growing memory or shearing escape sequences. Prerequisites: the thread map from [the first article](host-a-terminal-with-pty-sessions-and-frame-handoff-1.md) and a terminal engine (purrTTY uses libghostty-vt). Demonstrated by [Headless terminal surface and frame snapshot](../../../../mods/purrtty/features/headless-terminal-surface-and-frame-snapshot.md) in purrTTY.

## Background: how the game does it

The game contributes only the tick (see article 1); everything else is a mod-side design decision. Two engine properties shape it: the native engine is single-threaded, and a frame must not spend tens of milliseconds parsing a burst. So the contract splits the surface API in two halves:

```csharp
// purrTTY.Terminal/ITerminalSurface.cs L17-L33
/// The renderer-neutral terminal backend contract. Frontends drive it with
/// neutral commands/events and draw the <see cref="TerminalFrame"/> it produces.
/// No ImGui / Vulkan / KSA types appear here by design.
///
/// Threading: <see cref="Write"/> is safe to call from any thread (e.g. a PTY
/// read thread); it queues bytes. Every other member must be called on the
/// frontend tick thread. Queued input is applied during <see cref="BuildFrame"/>.
/// </summary>
public interface ITerminalSurface : IDisposable
{
    int Cols { get; }
    int Rows { get; }

    // ---- IN: data + viewport ----

    /// <summary>Queues terminal output bytes (from the PTY). Thread-safe.</summary>
    void Write(ReadOnlySpan<byte> data);
```

Because the backend references no KSA, ImGui or Vulkan types, the same surface drives a 2D ImGui window and an off-screen texture, and the whole backend is tested headlessly.

## Lifecycle and timing

| Work | Where |
|---|---|
| `Write` | any thread, normally a pty pump (`thread:background`); takes the inbox lock |
| `BuildFrame`, `Resize`, input encoding, selection | the tick thread only, `frame:draw-ui` inside `StarMapAfterGui` |
| Events (`PtyReply`, `TitleChanged`, `Bell`, `ClipboardRequested`, `FrameChanged`) | raised on the tick thread |

The visible window ticks its sessions every frame; hidden windows tick about four times a second. With the HUD hidden nothing ticks, so the inbox fills and then drops by the rules below (derived from the code paths, not observed in game).

## Steps

1. **Size the inbox and its budgets.** The inbox is capped at 24 MiB; a writer that would overflow waits up to 500 ms for the tick to drain; one tick consumes at most 8 MiB so a burst cannot stall the render thread; a drop injects a CAN, ESC, backslash heal sequence so the parser cannot desync across the gap:

   ```csharp
   // purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs L114, L133, L148, L156
       internal const int MaxInboxBytes = 24 * 1024 * 1024;
   // ...
       private static readonly TimeSpan WriteBackpressureBudget = TimeSpan.FromMilliseconds(500);
   // ...
       internal const int MaxBytesPerTick = 8 * 1024 * 1024;
   // ...
       private static readonly byte[] DropResetSeq = { 0x18, 0x1B, (byte)'\\' };
   ```

2. **Write with backpressure, never from the tick.** Only pump threads wait. A write from the tick thread (a custom shell can emit output while being ticked) skips the wait, because it would otherwise deadlock until the timeout. A write that could never fit goes straight to the drop path:

   ```csharp
   // purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs L364-L390
               // Backpressure: when the inbox is full, wait for the tick to drain it
               // instead of shearing the byte stream (a mid-stream drop severs APC
               // starts and prints kitty base64 into cells). Only pump threads wait —
               // never the tick thread itself — and a write that could never fit
               // (larger than the whole inbox) skips straight to the drop path.
               if (Environment.CurrentManagedThreadId != Volatile.Read(ref _tickThreadId)
                   && data.Length + DropResetSeq.Length <= MaxInboxBytes)
               {
                   long waitStart = 0;
                   while (!_disposed
                          && (long)_inboxLen + (_inboxDroppedTail ? DropResetSeq.Length : 0) + data.Length
                              > MaxInboxBytes)
                   {
                       waitStart = waitStart == 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : waitStart;
                       var remaining = WriteBackpressureBudget
                                       - System.Diagnostics.Stopwatch.GetElapsedTime(waitStart);
                       if (remaining <= TimeSpan.Zero || !Monitor.Wait(_sync, remaining))
                       {
                           break; // budget exhausted (or spurious timeout) — fall through to drop
                       }
                   }

                   if (_disposed)
                   {
                       return;
                   }
               }
   ```

3. **Drop as a last resort and say so once.** When the budget is exhausted the surface marks the tail as dropped, counts the episode for the performance HUD and logs only on the transition into the dropped state:

   ```csharp
   // purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs L392-L413
               int prefix = _inboxDroppedTail ? DropResetSeq.Length : 0;
               long required = (long)_inboxLen + prefix + data.Length;
               if (required > MaxInboxBytes)
               {
                   dropped = !_inboxDroppedTail;
                   if (dropped) _inboxDropCount++;
                   _inboxDroppedTail = true;
               }
               else
               {
                   EnsureInboxCapacity((int)required);
                   if (_inboxDroppedTail)
                   {
                       DropResetSeq.CopyTo(_inbox.AsSpan(_inboxLen));
                       _inboxLen += DropResetSeq.Length;
                       _inboxDroppedTail = false;
                   }

                   data.CopyTo(_inbox.AsSpan(_inboxLen));
                   _inboxLen += data.Length;
               }
           }
   ```

4. **Swap the inbox out at the start of the tick.** `BuildFrame` records the tick thread id, swaps two buffers under the lock (so the lock is held for a pointer swap), feeds at most 8 MiB and releases parked writers:

   ```csharp
   // purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs L726-L772
       public TerminalFrame BuildFrame()
       {
           ThrowIfDisposed();
           long t0 = System.Diagnostics.Stopwatch.GetTimestamp();

           // Writers from this thread must never wait on the drain (see Write).
           Volatile.Write(ref _tickThreadId, Environment.CurrentManagedThreadId);

           int inputLen;
           int inboxDropSnap;
           lock (_sync)
           {
               inboxDropSnap = _inboxDropCount;
               if (_inboxLen <= MaxBytesPerTick)
               {
                   inputLen = _inboxLen;
                   if (inputLen > 0)
                   {
                       (_inbox, _scratch) = (_scratch, _inbox);
                       _inboxLen = 0;
                   }
               }
               else
               {
                   // Backlog catch-up: feed at most MaxBytesPerTick this tick so a
                   // large buffered burst cannot stall the render thread for one
                   // giant VTWrite; the rest is consumed on subsequent ticks.
                   // NOTE: with MaxBytesPerTick == MaxInboxBytes this branch is
                   // unreachable — _inboxLen can never exceed MaxInboxBytes.
                   inputLen = MaxBytesPerTick;
                   if (_scratch.Length < inputLen)
                   {
                       _scratch = new byte[inputLen];
                   }

                   Array.Copy(_inbox, 0, _scratch, 0, inputLen);
                   Array.Copy(_inbox, inputLen, _inbox, 0, _inboxLen - inputLen);
                   _inboxLen -= inputLen;
               }

               if (inputLen > 0)
               {
                   // Space was freed: release any pump threads parked in Write's
                   // backpressure wait.
                   Monitor.PulseAll(_sync);
               }
           }
   ```

5. **Feed the engine, then flush replies and events.** Replies the engine generates while parsing (device attributes and status reports) are collected during `VTWrite` and sent after it, bell and title changes become events:

   ```csharp
   // purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs L817, L824-L841
               _terminal.VTWrite(span);
   // ...
           if (_replies.Count > 0)
           {
               var reply = _replies.ToArray();
               _replies.Clear();
               PtyReply?.Invoke(reply);
           }

           if (_bellPending)
           {
               _bellPending = false;
               Bell?.Invoke();
           }

           if (_titlePending)
           {
               _titlePending = false;
               TitleChanged?.Invoke(_terminal.Title ?? string.Empty);
           }
   ```

6. **Populate the frame only where the engine says it changed.** Skip entirely when the render state is clean, refill only dirty rows on a partial update, rebuild everything on a full one, and clear the consumed flags because the engine never does:

   ```csharp
   // purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs L960-L995
           var dirty = _renderState.Dirty;
           if (!force && dirty == RenderStateDirty.False)
           {
               _lastDirtyState = 0;
               return false;
           }

           bool full = force || dirty == RenderStateDirty.Full;
           _lastDirtyState = full ? 2 : 1;
           if (full)
           {
               // Palette / default-color changes always mark the terminal dirty,
               // which yields a Full state — so colors only need re-reading here.
               RefreshColors();
           }

           var defFg = _frame.Colors.DefaultForeground;
           var defBg = _frame.Colors.DefaultBackground;

           Span<byte> utf8Scratch = stackalloc byte[GraphemeScratchBytes];
           var reader = _renderState.CreateFrameReader();
           try
           {
               int rowIdx = 0;
               while (rowIdx < rows && reader.NextRow())
               {
                   bool rowDirty = reader.RowDirty;
                   if (rowDirty)
                   {
                       reader.ClearRowDirty();
                   }

                   if (!full && !rowDirty)
                   {
                       rowIdx++;
                       continue;
   ```

7. **Publish a generation, not a copy.** After the cursor, scrollbar and image placements are refreshed, the surface bumps `Generation` and raises `FrameChanged` only if something really changed; the frontend redraws on a new generation. The frame arrays are reused every tick, so a frontend must not hold rows across ticks:

   ```csharp
   // purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs L884-L895
           bool engineMutated = inputLen > 0 || _pendingChange;
           bool changed = PopulateFrame(_pendingChange);
           _pendingChange = false;
           changed |= RefreshCursorAndScrollbar();
           changed |= PopulateImages(engineMutated);
           long t3 = System.Diagnostics.Stopwatch.GetTimestamp();

           if (changed)
           {
               _frame.Generation = ++_generation;
               FrameChanged?.Invoke();
           }
   ```

## Complete example

| File | Role |
|---|---|
| [purrTTY.Terminal/ITerminalSurface.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/ITerminalSurface.cs) | The seam and its threading contract |
| [purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs) | Inbox, backpressure, `BuildFrame`, dirty-aware `PopulateFrame` |
| [purrTTY.Terminal/Rendering/TerminalFrame.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Rendering/TerminalFrame.cs) | The snapshot type: rows, cells, cursor, scrollbar, image placements |
| [purrTTY.Terminal.Tests/InboxBackpressureTests.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal.Tests/InboxBackpressureTests.cs) | Backpressure contract tests |

## Variations and gotchas

- **Comments drift from constants.** A comment in `BuildFrame` still says the catch-up branch is unreachable because `MaxBytesPerTick == MaxInboxBytes`; at this pin the inbox is 24 MiB and the per-tick budget 8 MiB, so the branch runs. Trust the constants.
- **The inbox is sized for video.** The 24 MiB cap and the 256 MiB Kitty image budget exist so a screen stream's frames park losslessly instead of being cut mid-escape; see [Stream the game screen to a terminal](../stream-the-game-screen-to-a-terminal/overview.md).
- **The engine never clears dirty flags.** Skipping the clear rebuilds the whole grid every tick.
- **Surfaces are created without a logger in game**, so the drop warning goes to a null logger; the performance HUD's drop counter is the visible signal.
- **Never call into the engine from a pump thread**, and never dispose a surface from a pool thread; both race `BuildFrame`.

## See also

- [Overview](overview.md); previous: [the thread map](host-a-terminal-with-pty-sessions-and-frame-handoff-1.md)
- [Headless terminal surface and frame snapshot](../../../../mods/purrtty/features/headless-terminal-surface-and-frame-snapshot.md)
- [Let a terminal program set the system clipboard](../let-a-terminal-program-set-the-system-clipboard/overview.md): a sidecar that scans the same bytes
- [Draw a monospace text grid](../../../ui/draw-lists/draw-a-monospace-text-grid/overview.md)
