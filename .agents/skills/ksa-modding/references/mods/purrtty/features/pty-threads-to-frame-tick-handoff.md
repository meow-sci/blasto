---
type: Mod Feature
title: Handing PTY threads' output to the game frame
description: The thread map that lets blocking PTY readers, writers and slow shell spawns feed a single-threaded engine that is only touched from the KSA main thread inside StarMapAfterGui, with bounded queues in both directions and synchronous close on the tick thread
mod: purrtty
category: external-io
techniques: [background-thread, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, thread:background]
game_types: [KSA.Program]
tags: [threading, concurrency, pty, backpressure, lifecycle, starmap]
---

A terminal mod has to bridge two worlds: blocking OS I/O that must live on its own threads, and a
native engine plus ImGui/Vulkan state that must be touched from one thread. purrTTY makes the KSA
main thread, inside the `[StarMapAfterGui]` callback, the only "tick thread", and connects
everything else to it through two bounded queues and a construction hook. Any mod that hosts a
process, a socket or a VM console in KSA faces the same problem.

## How it works

**Threads per session**

| Thread | Created by | Does | Talks to the tick thread through |
|---|---|---|---|
| Spawn | `Task.Run` in `GhosttyTerminalController.StartSession` | `SessionManager.CreateSessionAsync` -> configure -> `InitializeAsync` (spawn, startup command) -> publish | `SessionConfigurator` runs before publication; publication is a locked insert into the session list |
| Output pump | `LongRunning` task (ConPTY `ReadFile`, Unix `poll`/`read`) | Reads up to 64 KiB, raises `DataReceived` | `Surface.Write` -> inbox under a lock, 24 MiB cap, writer waits up to 500 ms when full |
| Input writer | `PtyInputQueue` background thread | Blocking `WriteFile` / `write(2)` | `BlockingCollection<byte[]>`, 1 MiB cap, drop on overflow with one error report |
| Exit waiter (Unix) | `LongRunning` task | `waitpid`, drain, cleanup | `ProcessExited` event (handled without touching the surface) |
| Teardown | `Task.Run` in `TerminalSession.Dispose` | `ProcessManager.Dispose` (may block seconds) | Events are detached first, so it cannot call back |
| Shell detection | `Task.Run` in `ShellMenuCache` | PATH, WSL, `/etc/shells` probes once | `volatile` snapshot reference |

**The tick** (main thread, every frame the HUD is visible):

- `TerminalWindow.Render` calls `Surface.BuildFrame()` for the active tab and every background tab;
  `GhosttyTerminalController.Update` drains hidden windows every 0.25 s; the in-world renderer ticks
  its own session.[^terminal-window][^controller]
- Inside `BuildFrame`, engine replies (`PtyReply`) are written back through the input queue, so the
  tick thread never blocks on a PTY write; user input goes the same way via `session.SendInput`.
- Closing a tab is synchronous on the tick thread: `TerminalSession.Dispose` detaches events,
  disposes the native surface there, and only then backgrounds the slow process teardown.[^session]

**Lock discipline.** The surface's inbox lock is the only lock the pump and the tick share. The tick
thread records its id at the top of `BuildFrame`; a `Write` from that thread (a custom shell such as
the Game Console can emit output while being ticked) never waits, which avoids self-deadlock.[^surface]

## Lifecycle and timing

- Tick point: `[StarMapAfterGui]` is a StarMap postfix on `Program.OnDrawUiViewports`, which
  `Program.OnFrame` calls only `if (DrawUI)`, after `ImGui.NewFrame` and before `ImGui.Render`
  (`frame:draw-ui`).[^starmap-program-patcher][^ksa-program] Physics solvers for the next step are
  already running on worker threads at this point; purrTTY does not touch simulation state here
  except reading vehicle transforms for in-world quads.
- `TerminalMod.OnAfterUi` calls `_controller.Update(dt)` and `_controller.Render()` every frame,
  even while the terminal is hidden, so hidden sessions keep draining and the input-gate flag is
  cleared.[^terminal-mod]
- HUD hidden: `OnDrawUiViewports` is not called, so no session is ticked. Pumps then park in the
  500 ms backpressure wait and eventually drop with the CAN/ST heal (inferred from the code; a
  long HUD-off period with a chatty shell loses output).
- Game exit: `[StarMapUnload]` disposes the controller, which closes sessions; process teardown runs
  in the background and the OS reaps anything left.

## Key code

| Path | Role |
|---|---|
| [purrTTY.GameMod/TerminalMod.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMod.cs#L68-L125) | The tick entry point |
| [purrTTY.Display/Ghostty/GhosttyTerminalController.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/GhosttyTerminalController.cs#L217-L231) | Session start on a pool thread |
| [purrTTY.Display/Ghostty/GhosttyTerminalController.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/GhosttyTerminalController.cs#L350-L379) | Hidden-window drain cadence |
| [purrTTY.Display/Ghostty/TerminalWindow.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.cs#L506-L521) | Ticking background tabs |
| [purrTTY.Terminal/Sessions/SessionManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Sessions/SessionManager.cs#L126-L224) | Configure before publish, disposal re-check after slow spawn |
| [purrTTY.Terminal/Sessions/TerminalSession.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Sessions/TerminalSession.cs#L200-L268) | Stream wiring and synchronous close |
| [purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs#L726-L772) | Inbox swap and writer release |
| [purrTTY.Terminal/Pty/Process/PtyInputQueue.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/PtyInputQueue.cs#L16-L105) | Bounded input queue and writer thread |
| [purrTTY.Terminal.Tests/TerminalSessionWiringTests.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal.Tests/TerminalSessionWiringTests.cs) | Data-flow tests through a headless custom shell |

## Snippets

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

```csharp
// purrTTY.Terminal/Sessions/SessionManager.cs L166-L178
        try
        {
            // Configure (theme push, surface event wiring) before the session is
            // published: after publication the tick thread may already be inside
            // BuildFrame on this surface, and the engine is single-threaded.
            SessionConfigurator?.Invoke(session);
            await session.InitializeAsync(effectiveOptions, cancellationToken);
        }
        catch
        {
            session.Dispose();
            throw;
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

## Game APIs touched

| API | Where | Purpose |
|---|---|---|
| `Program.OnFrame` / `OnDrawUiViewports` / `DrawUI` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2287) | The frame slot the tick runs in, and when it is skipped |

## Pitfalls and version notes

- Do the slow part (spawn, WSL start-up, SSH connect) off the main thread, but publish the session to
  the render path only after it is fully configured; a `SessionCreated` subscriber runs after
  publication and possibly on a pool thread, so it must not touch the surface (gotcha 19).[^gotchas]
- Re-check disposal after the slow await: a window closed during a 10 s WSL start would otherwise
  leak a live PTY.
- Never write to a PTY from the main thread; never wait on the inbox from the thread that drains it.
- Tick every session, visible or not, or the inbox fills (gotcha 18).
- The design assumes one tick thread. If you tick surfaces from both a StarMap hook and a Harmony
  render patch, make sure both run on the main thread (they do in KSA's `OnFrame`).

## See also

- [Headless terminal surface and frame snapshot](./headless-terminal-surface-and-frame-snapshot.md)
- [Hosting Windows shells in a ConPTY](./conpty-shell-hosting.md)
- [POSIX pty spawn without fork](./posix-pty-spawn-without-fork.md)
- [Game console shell](./game-console-shell.md)
- [Custom shell registry and lifecycle](./custom-shell-registry-and-lifecycle.md)
- [purrTTY overview](../overview.md)
- [Map every thread to one StarMapAfterGui tick with configure-before-publish sessions](../../../howto/external-io/terminals/host-a-terminal-with-pty-sessions-and-frame-handoff/host-a-terminal-with-pty-sessions-and-frame-handoff-1.md) - how-to article demonstrating this feature

[^terminal-window]: purrTTY.Display/Ghostty/TerminalWindow.cs
[^controller]: purrTTY.Display/Ghostty/GhosttyTerminalController.cs
[^session]: purrTTY.Terminal/Sessions/TerminalSession.cs
[^surface]: purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs
[^starmap-program-patcher]: StarMap.Core/Patches/ProgramPatcher.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^terminal-mod]: purrTTY.GameMod/TerminalMod.cs
[^gotchas]: purrTTY docs/gotchas.md
