---
type: Mod Feature
title: Headless terminal surface with a renderer-neutral frame snapshot
description: A backend that owns the native VT engine on one thread, accepts PTY bytes from any thread into a bounded inbox, and produces a dirty-aware TerminalFrame of pre-resolved cells that any frontend (ImGui window or world texture) can draw
mod: purrtty
category: external-io
techniques: [native-interop, background-thread]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, thread:background]
game_types: []
tags: [terminal, architecture, threading, performance, dirty-tracking, renderer-neutral]
---

The center of purrTTY's design is a seam: the backend (`purrTTY.Terminal`) knows nothing about
ImGui, Vulkan or KSA, and the frontends (`purrTTY.Display`, the in-world renderer) know nothing
about libghostty-vt. The backend exposes `ITerminalSurface` (commands in) and `TerminalFrame` (a
snapshot out: rows of cells with final RGBA colors, cursor, scrollbar, image placements). Because
of this the same surface drives a 2D ImGui window and an off-screen texture on a 3D quad, and the
whole backend is tested headlessly on all three OSes.

## How it works

**Contract.** `Write(ReadOnlySpan<byte>)` is the only thread-safe member (PTY reader threads call
it). Everything else (resize, scroll, selection, theme, key/mouse/paste encoding, `BuildFrame`)
must be called on the frontend tick thread. Events (`PtyReply`, `TitleChanged`, `Bell`,
`ClipboardRequested`, `FrameChanged`) are raised on that thread.[^surface-interface]

**Inbox.** `Write` appends to a byte inbox under a lock. The inbox is capped at 24 MiB. A pump
thread that would overflow it waits on the lock (`Monitor.Wait`, up to 500 ms) for the tick to
drain, which propagates flow control back to the producer; only when that budget is exhausted are
bytes dropped, and the next accepted bytes are prefixed with `CAN ESC \` so the parser cannot
desync on a half-dropped escape sequence. The tick thread itself never waits (a custom shell may
write from it).[^surface][^backpressure-tests]

**`BuildFrame()`** (one call per session per tick):

1. Record the tick thread id; swap the inbox out (at most 8 MiB per tick, remainder next tick) and
   `PulseAll` parked writers.
2. Tee the bytes through the OSC sidecar (OSC 52 / OSC 1), then `Terminal.VTWrite`.
3. Flush engine replies (DA/DSR) via `PtyReply`, then bell and title events.
4. If DEC mode 2026 (synchronized output) is set, return the previous complete frame unchanged,
   for at most 1 s, so apps that batch redraws never tear.
5. `RenderState.Update`, then `PopulateFrame`: skip entirely when the engine reports not dirty,
   refill only dirty rows on a partial update, rebuild all rows and colors on a full update, and
   clear the consumed dirty flags (the engine only raises them).
6. Re-read cursor and scrollbar, scan kitty placements only if the engine mutated, bump
   `Generation` and raise `FrameChanged` only on a real change; record `LastFrameStats`.

**Cells.** `FillCell` decodes the packed cell (`RawCell`), interns grapheme strings in a
per-surface cache (steady-state rebuilds allocate nothing), and resolves fg/bg from the style and
palette exactly like the native renderer, including reverse video and background-only cells, so
the frontend never resolves SGR state (gotcha 4).[^gotchas]

**Identity.** The surface answers Device Attributes as a VT220 with ANSI color and OSC 52 (no
sixel), and sessions export `TERM=xterm-256color`, `TERM_PROGRAM=ghostty`, `COLORTERM=truecolor`
so capability probes enable kitty graphics without needing Ghostty terminfo.

## Lifecycle and timing

- The "tick thread" is the KSA main thread: `BuildFrame` is called from `TerminalWindow.Render`
  (visible windows, every tab every frame), from `GhosttyTerminalController.Update` at about 4 Hz
  for hidden windows, and from the in-world renderer, all inside `TerminalMod.OnAfterUi`
  (`[StarMapAfterGui]`, `frame:draw-ui`).
- `Write` runs on PTY pump threads (`thread:background`); the inbox lock is the only
  synchronization point with the main thread.
- When the HUD is hidden (`Program.DrawUI == false`) StarMap skips `StarMapAfterGui`, so no surface
  is ticked: pump threads fill the inbox, then block in the 500 ms backpressure wait, then drop
  with the CAN/ST heal. (Inferred from the code paths; not observed in game.)
- Session construction happens on a pool thread; `SessionManager.SessionConfigurator` runs before
  the session is published so the theme push and event wiring never race a `BuildFrame` (gotcha 19).

## Key code

| Path | Role |
|---|---|
| [purrTTY.Terminal/ITerminalSurface.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/ITerminalSurface.cs#L16-L32) | The seam and its threading contract |
| [purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs#L100-L161) | Inbox caps, backpressure budget, drop-heal sequence |
| [purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs#L354-L423) | `Write` with backpressure |
| [purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs#L726-L913) | `BuildFrame` |
| [purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs#L950-L1050) | Dirty-aware `PopulateFrame` |
| [purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs#L166-L186) | Device Attributes reply |
| [purrTTY.Terminal/Rendering/TerminalFrame.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Rendering/TerminalFrame.cs) | Snapshot type (`FrameRow`, `FrameCell`, cursor, scrollbar, images) |
| [purrTTY.Terminal.Tests/InboxBackpressureTests.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal.Tests/InboxBackpressureTests.cs) | Backpressure contract tests |

## Snippets

```csharp
// purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs L362-L390
        lock (_sync)
        {
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

```csharp
// purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs L960-L996
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
                }
```

## Game APIs touched

None. The backend references no KSA, ImGui or Vulkan types by design; it is the part of purrTTY
that would port unchanged to another host.

## Pitfalls and version notes

- Never call into the engine from a pump thread; disposing a surface from a pool thread races
  `BuildFrame` (session close is deliberately synchronous on the tick thread).
- Every session must be ticked, including background tabs and hidden windows, or its inbox fills
  (gotcha 18). purrTTY ticks all tabs every frame and hidden windows at about 4 Hz.
- Docs drift: gotcha 18 and a code comment in `BuildFrame` describe an 8 MiB inbox with
  `MaxBytesPerTick == MaxInboxBytes`; the code at this ref uses a 24 MiB inbox and an 8 MiB
  per-tick budget, so the catch-up branch is reachable.[^surface]
- The engine never clears its dirty flags; skipping the clear rebuilds the whole grid every tick
  (gotcha 12).
- Frame arrays are reused; a frontend must not hold rows across ticks.
- In game the surfaces are created without an `ILogger` (the session-manager factory passes none),
  so the backend's warnings, including the inbox-drop warning and a packed-cell layout mismatch,
  go to `NullLogger`; the perf HUD's `DROP:N` counter is the visible signal.

## See also

- [PTY threads to frame tick handoff](./pty-threads-to-frame-tick-handoff.md)
- [Vendored libghostty-vt binding](./vendored-libghostty-vt-binding.md)
- [Terminal grid drawn with ImGui draw lists](./terminal-grid-drawlist-renderer.md)
- [Kitty graphics as ImGui Vulkan textures](./kitty-graphics-as-imgui-vulkan-textures.md)
- [OSC 52 clipboard sidecar](./osc52-clipboard-sidecar.md)
- [Headless test suites](./headless-test-suites.md)
- [purrTTY overview](../overview.md)
- [Bounded byte inbox with backpressure and dirty-aware frame snapshots](../../../howto/external-io/terminals/host-a-terminal-with-pty-sessions-and-frame-handoff/host-a-terminal-with-pty-sessions-and-frame-handoff-2.md) - how-to article demonstrating this feature

[^surface-interface]: purrTTY.Terminal/ITerminalSurface.cs
[^surface]: purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs
[^backpressure-tests]: purrTTY.Terminal.Tests/InboxBackpressureTests.cs
[^gotchas]: purrTTY docs/gotchas.md
