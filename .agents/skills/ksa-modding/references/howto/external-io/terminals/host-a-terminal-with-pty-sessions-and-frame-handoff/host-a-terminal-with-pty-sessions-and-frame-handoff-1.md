---
type: How-To
title: Map every thread to one StarMapAfterGui tick with configure-before-publish sessions
description: Keep blocking pty reads, writes and spawns on background threads while the terminal engine, ImGui and the clipboard are touched only from one StarMapAfterGui tick
category: external-io
group: terminals
topic: host-a-terminal-with-pty-sessions-and-frame-handoff
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/purrtty/features/pty-threads-to-frame-tick-handoff]
techniques: [background-thread, starmap-lifecycle]
hooks:
  - harmony-prefix KSA.Program.OnKey
  - starmap StarMapAfterGui
  - starmap StarMapAllModsLoaded
  - starmap StarMapUnload
lifecycle: [frame:input, frame:draw-ui, thread:background, startup:all-mods-loaded]
game_types: [KSA.Program]
tags: [terminal, threading, pty, backpressure, tick, starmap]
---

Outcome: a terminal window in the game whose blocking work (reading a pty, writing to it, waiting for the child, spawning a slow shell) lives on background threads, while the terminal engine, ImGui and the clipboard are touched from exactly one place: a `[StarMapAfterGui]` tick. This is the thread map for the whole topic; the other three articles build on its rules. Prerequisites: StarMap, an ImGui-drawn terminal frontend and a process or shell source ([Host and supervise an external process or virtual machine](../../processes/host-and-supervise-an-external-process-from-a-mod/overview.md)). Demonstrated by [Handing PTY threads' output to the game frame](../../../../mods/purrtty/features/pty-threads-to-frame-tick-handoff.md) in purrTTY.

## Background: how the game does it

KSA runs everything that touches ImGui on the main thread inside `Program.OnFrame`. The UI block is entered only while the HUD is shown:

```csharp
// current/decomp/KSA/Program.cs L2264-L2269
		Profiler.MainThread.Begin(Profiler.Tag.DrawUi);
		if (DrawUI)
		{
			OnDrawUiFrame(dtPlayer);
			OnDrawUiViewports(dtPlayer);
		}
```

StarMap turns `OnDrawUiViewports` into the `[StarMapAfterGui]` hook with a Harmony postfix, so every `StarMapAfterGui` method runs on the main thread once per frame, after `PrepareImGui` and before `ImGui.Render`, and never on frames where `DrawUI` is false:[^starmap-programpatcher-cs]

```csharp
// StarMap.Core/Patches/ProgramPatcher.cs L26-L36
        [HarmonyPatch(OnAfterDrawUiMethodName)]
        [HarmonyPostfix]
        public static void AfterOnDrawUi(double dt)
        {
            var methods = StarMapCore.Instance?.Loader.ModRegistry.Get<StarMapAfterGuiAttribute>() ?? [];

            foreach (var (_, @object, method) in methods)
            {
                method.Invoke(@object, [dt]);
            }
        }
```

The terminal engine (libghostty-vt through a vendored binding) is a native, single-threaded state machine, and the pty reads and writes block. Those two facts force the design: blocking I/O stays on background threads, and one tick thread owns the engine.[^purrtty-terminalsession-cs]

## Lifecycle and timing

Thread inventory per session (the session start, pump and exit threads are the ones described in [Host and supervise an external process](../../processes/host-and-supervise-an-external-process-from-a-mod/overview.md)):

| Thread | Created by | Does | Talks to the tick through |
|---|---|---|---|
| Spawn | `Task.Run` in `GhosttyTerminalController.StartSession` | Create the session, configure it, spawn the shell, publish it | Configure runs before publication; publication is a locked insert |
| Output pump | `LongRunning` task in the ConPTY and Unix pty managers | Read the pty and raise `DataReceived` | `Surface.Write` appends to the inbox under a lock (24 MiB cap, bounded wait when full) |
| Input writer | `PtyInputQueue` background thread | Blocking `WriteFile` or `write(2)` | `BlockingCollection<byte[]>`, 1 MiB cap, drop on overflow with one error report |
| Exit waiter (Unix) | `LongRunning` task | `waitpid`, drain, clean up | `ProcessExited` event, handled without touching the surface |
| Teardown | `Task.Run` in `TerminalSession.Dispose` | Dispose the process manager (may block for seconds) | Events are detached first, so it cannot call back |
| Shell detection | `Task.Run` in the shell menu cache | PATH, WSL and `/etc/shells` probes, once | A `volatile` snapshot reference |

The tick runs at `frame:draw-ui` on the main thread, every frame the HUD is visible. The physics step for the next frame is already running on workers at that point and the terminal does not touch simulation state. While the HUD is hidden no tick runs: the pumps fill the inbox, park in the bounded wait and then drop (by the code paths; not observed in game). On exit, `[StarMapUnload]` disposes the controller, which closes the sessions; see [Clean up on game exit](../../../getting-started/lifecycle/clean-up-on-game-exit/overview.md).

## Steps

1. **Install one tick.** `OnAfterUi` is the only entry point that touches ImGui or the engine. It runs the dialogs, the toggle hotkey, then `Update` and `Render` on the controller every frame, even while the terminal is hidden: `Update` drains hidden sessions and `Render` clears the input-capture flag when hidden:

   ```csharp
   // purrTTY.GameMod/TerminalMod.cs L68-L71, L102-L117
       [StarMapAfterGui]
       public void OnAfterUi(double dt)
       {
           if (!_isInitialized || _isDisposed)
   // ...
               // Update runs even while hidden: it drains hidden sessions' PTY
               // backlogs on a low cadence (their inboxes otherwise grow unbounded
               // under chatty output). Render() is also called unconditionally and
               // early-outs when hidden — that early-out clears the game-key gate
               // (_anyTerminalActive). Calling it only while visible would strand
               // the flag `true` after hiding a focused terminal, killing the
               // game's entire keyboard pipeline until the terminal is shown again.
               if (_controller != null)
               {
                   _controller.Update((float)dt);
                   _controller.Render();
               }

               // In-world render-to-texture loop (prototype; gated by PURRTTY_INWORLD).
               // No-op until the renderer/quad subsystem has been initialized.
               _inWorld?.OnAfterGui(dt);
   ```

2. **Start sessions on a pool thread.** Shell spawn can take seconds (WSL start-up, an SSH connect), so the controller never awaits it on the tick. A failure is reported to the window, which shows it instead of a blank tab:

   ```csharp
   // purrTTY.Display/Ghostty/GhosttyTerminalController.cs L217-L231
       private static void StartSession(TerminalWindow window, ProcessLaunchOptions? launchOptions, string? title)
       {
           _ = Task.Run(async () =>
           {
               try
               {
                   await window.Sessions.CreateSessionAsync(title, launchOptions);
               }
               catch (Exception ex)
               {
                   ModLog.Log.Error($"GhosttyTerminalController: failed to start session: {ex.Message}");
                   window.NotifySessionStartFailed(ex.Message);
               }
           });
       }
   ```

3. **Configure before you publish, and re-check at publication.** After a session is in the list, the tick may already be inside `BuildFrame` on its surface, so theme and event wiring happen first. The slow spawn also means the window may have closed or a second create may have passed the entry cap, so both are checked again under the lock:

   ```csharp
   // purrTTY.Terminal/Sessions/SessionManager.cs L168-L172, L185-L197
               // Configure (theme push, surface event wiring) before the session is
               // published: after publication the tick thread may already be inside
               // BuildFrame on this surface, and the engine is single-threaded.
               SessionConfigurator?.Invoke(session);
               await session.InitializeAsync(effectiveOptions, cancellationToken);
   // ...
               // Re-check disposal: shell spawn (WSL spin-up etc.) can take seconds
               // and the owning window may have closed in the meantime. Publishing
               // into a disposed manager would leak a live PTY process forever.
               if (_disposed)
               {
                   disposedDuringInit = true;
               }
               else if (_sessions.Count >= _maxSessions)
               {
                   // Two overlapping creates can both pass the entry cap check —
                   // the lock is released across the slow spawn await — so the cap
                   // must be re-validated at publication, like disposal above.
                   capExceededDuringInit = true;
   ```

4. **Let the pump write straight into the surface, and send engine replies through the queue.** `Surface.Write` is the one thread-safe surface member. The reverse direction (device-attribute replies the engine generates during `BuildFrame`) is written through the process manager, which enqueues rather than writes:

   ```csharp
   // purrTTY.Terminal/Sessions/TerminalSession.cs L200-L210
       // PTY output → surface (runs on the PTY read thread; Surface.Write is thread-safe).
       private void OnProcessDataReceived(object? sender, DataReceivedEventArgs e) => Surface.Write(e.Data.Span);

       // Engine replies → PTY (runs on the tick thread during BuildFrame).
       private void OnSurfacePtyReply(byte[] bytes)
       {
           if (ProcessManager.IsRunning)
           {
               ProcessManager.Write(bytes);
           }
       }
   ```

5. **Keep the tick off blocking writes.** `IProcessManager.Write` is called on the tick (user input and engine replies), so it must never reach `write(2)` directly. `PtyInputQueue.Write` copies the chunk, enforces the cap and returns; the dedicated writer thread does the blocking write:

   ```csharp
   // purrTTY.Terminal/Pty/Process/PtyInputQueue.cs L48-L82
       /// <summary>Queues a copy of <paramref name="data"/> for the writer thread. Never blocks.</summary>
       internal void Write(ReadOnlySpan<byte> data)
       {
           if (data.IsEmpty || _failed)
           {
               return;
           }

           if (Volatile.Read(ref _pendingBytes) + data.Length > MaxPendingBytes)
           {
               if (!_overflowReported)
               {
                   _overflowReported = true;
                   _onError(
                       new ProcessWriteException(
                           $"PTY input queue overflow (> {MaxPendingBytes} bytes pending); dropping input"),
                       "PTY input queue overflow; the child is not reading its input");
               }

               return;
           }

           _overflowReported = false;
           Interlocked.Add(ref _pendingBytes, data.Length);
           try
           {
               _queue.Add(data.ToArray());
           }
           catch (InvalidOperationException)
           {
               // CompleteAdding raced this enqueue during teardown — the session is
               // going away, dropping the chunk is fine.
               Interlocked.Add(ref _pendingBytes, -data.Length);
           }
       }
   ```

6. **Tick every session, visible or not.** Pumps never sleep and an inbox drains only inside `BuildFrame`, so an unticked chatty tab would grow until the cap. The window ticks all its other tabs each frame; the controller drains hidden windows about four times a second from the same tick:

   ```csharp
   // purrTTY.Display/Ghostty/TerminalWindow.cs L506-L521
           // Tick every non-active tab too: the PTY pumps never sleep and a
           // surface inbox only drains inside BuildFrame, so an unticked chatty
           // background tab grows its inbox until the safety cap drops output.
           // Dirty tracking makes ticking a quiet session nearly free. (Indexed:
           // see DrainSessions.)
           var allSessions = Sessions.Sessions;
           for (int i = 0; i < allSessions.Count; i++)
           {
               if (!ReferenceEquals(allSessions[i], session))
               {
                   // Nothing uploads a background tab's kitty pixels — skip the hash + decode
                   // until it becomes the active tab again (the surface re-scans on re-enable).
                   allSessions[i].Surface.DecodeKittyImages = false;
                   allSessions[i].Surface.BuildFrame();
               }
           }
   ```

   ```csharp
   // purrTTY.Display/Ghostty/GhosttyTerminalController.cs L359-L378
           // While hidden, Render() never runs, so the surfaces are never ticked
           // and PTY output would pile up in their inboxes (htop, a running build).
           // Drain them on a low cadence — called on the same (tick) thread as
           // Render, preserving the single-threaded engine invariant.
           if (!_isVisible && !_disposed)
           {
               _hiddenDrainTimer += deltaTime;
               if (_hiddenDrainTimer >= 0.25f)
               {
                   _hiddenDrainTimer = 0f;
                   foreach (var window in _windows)
                   {
                       window.DrainSessions();
                   }
               }
           }
           else
           {
               _hiddenDrainTimer = 0f;
           }
   ```

7. **Close on the tick thread, tear down in the background.** Disposing the native surface from a pool thread would race a concurrent `BuildFrame`, so a close disposes it synchronously on the tick. The process teardown can block (graceful wait, then kill, then `ClosePseudoConsole`), so it is detached from the events and queued on the pool:

   ```csharp
   // purrTTY.Terminal/Sessions/TerminalSession.cs L239-L267
           // Detach both streams first so neither the surface nor the (about to be
           // orphaned) process manager can raise events back into a disposed session.
           ProcessManager.ProcessExited -= OnProcessExited;
           ProcessManager.DataReceived -= OnProcessDataReceived;
           ProcessManager.ProcessError -= OnProcessError;
           Surface.PtyReply -= OnSurfacePtyReply;
           Surface.TitleChanged -= OnSurfaceTitleChanged;

           // The libghostty surface is native and single-threaded (CLAUDE.md gotcha #1),
           // so dispose it synchronously on the current (tick) thread.
           Surface.Dispose();

           // Fire-and-forget the PTY/process teardown. Stopping a shell can block for
           // seconds — graceful WaitForExit(2s) then Kill, plus ClosePseudoConsole,
           // which can wedge on a slow child such as WSL2 — and closing a terminal or
           // exiting the game must not stall on it. The manager is reaped on a
           // background thread (or by the OS if the game exits first); its events are
           // already detached so the orphan can't call back in.
           _ = Task.Run(() =>
           {
               try
               {
                   ProcessManager.Dispose();
               }
               catch (Exception ex)
               {
                   _logger.LogDebug(ex, "Background PTY teardown for session {SessionId} failed", Id);
               }
           });
   ```

## Complete example

| File | Role |
|---|---|
| [purrTTY.GameMod/TerminalMod.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMod.cs) | The tick entry point |
| [purrTTY.Display/Ghostty/GhosttyTerminalController.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/GhosttyTerminalController.cs) | Session start on a pool thread, hidden-window drain cadence |
| [purrTTY.Display/Ghostty/TerminalWindow.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.cs) | Ticking background tabs |
| [purrTTY.Terminal/Sessions/SessionManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Sessions/SessionManager.cs) | Configure before publish, disposal re-check |
| [purrTTY.Terminal/Sessions/TerminalSession.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Sessions/TerminalSession.cs) | Stream wiring and synchronous close |
| [purrTTY.Terminal/Pty/Process/PtyInputQueue.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/PtyInputQueue.cs) | Bounded input queue and writer thread |
| [purrTTY.Terminal.Tests/TerminalSessionWiringTests.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal.Tests/TerminalSessionWiringTests.cs) | Data-flow tests through a headless custom shell |

## Variations and gotchas

- **Never write to a pty from the tick, never wait on the inbox from the tick.** The surface records the tick thread id and lets a write from that thread skip the backpressure wait, because a custom shell such as the game console can emit output while being ticked ([the surface article](host-a-terminal-with-pty-sessions-and-frame-handoff-2.md)).
- **Tick hidden sessions too.** Skipping them is the classic failure: output piles up in the inbox until it overflows.
- **HUD hidden means no tick.** `StarMapAfterGui` does not fire when `DrawUI` is false; a mod that must keep running while the HUD is hidden needs another hook ([Draw UI while the HUD is hidden](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md)).
- **A `SessionCreated` subscriber runs after publication** and possibly on a pool thread, so it must not touch the surface; configure in the session configurator instead.
- **Keep game hotkeys from firing while the terminal has focus.** Typed keys also reach KSA's own handlers. GLFW delivers them to `Program.OnKey` from inside `Glfw.PollEvents` in `PrepareFrame` (`frame:input`, before this frame's tick), so purrTTY prefixes `Program.OnKey` and returns false while `IsAnyTerminalActive` or the in-world quad has focus ([purrTTY.GameMod/Patcher.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Patcher.cs#L155-L212)). The flag is written by the previous frame's tick (`Render` clears it when the window is hidden, which is one reason the tick calls `Render` every frame), so the gate lags focus changes by a frame. A release is forwarded only for a key whose press the game saw, otherwise releasing a toggle key typed into the terminal would fire it. Details: [Gate game input while a terminal is focused](../../../../mods/purrtty/features/gate-game-input-while-terminal-focused.md).
- **One tick thread is an invariant.** If you tick surfaces from a StarMap hook and from a Harmony render patch, both run on the main thread in `OnFrame`, which is what keeps the invariant true.

## See also

- [Overview](overview.md); next: [the surface and its inbox](host-a-terminal-with-pty-sessions-and-frame-handoff-2.md)
- [Handing PTY threads' output to the game frame](../../../../mods/purrtty/features/pty-threads-to-frame-tick-handoff.md)
- [Host and supervise an external process or virtual machine](../../processes/host-and-supervise-an-external-process-from-a-mod/overview.md)
- [Marshal commands from background threads to the game thread](../../control/marshal-commands-to-the-game-thread/overview.md): the same rule for commands

[^starmap-programpatcher-cs]: StarMap/StarMap.Core/Patches/ProgramPatcher.cs
[^purrtty-terminalsession-cs]: purrtty/purrTTY.Terminal/Sessions/TerminalSession.cs
