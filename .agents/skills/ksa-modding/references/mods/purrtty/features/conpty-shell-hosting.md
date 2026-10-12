---
type: Mod Feature
title: Hosting Windows shells in a ConPTY from inside the game
description: Spawn PowerShell, cmd or WSL attached to a Windows pseudoconsole from the KSA process, with null std handles, a never-sleeping pipe pump, a queued writer thread and a flush-then-close teardown that never closes a handle another thread is blocked on
mod: purrtty
category: external-io
techniques: [native-interop, background-thread]
hooks: [starmap StarMapAfterGui]
lifecycle: [thread:background, frame:draw-ui]
game_types: []
tags: [windows, conpty, process, pty, shell, threading, native]
---

On Windows, every purrTTY tab that is not the in-game console is a real shell (PowerShell,
PowerShell Core, cmd, or a WSL distribution) running under a ConPTY pseudoconsole created by the
game process. `TerminalSessionFactory` picks `ProcessManager` (ConPTY) on Windows and
`UnixProcessManager` elsewhere, behind the same `IProcessManager` interface.[^session-factory]
The interesting parts are the Win32 details that make a ConPTY work inside a host with redirected
stdio, keep throughput high, and tear down without losing output or racing handles.

## How it works

**Start** (`ProcessManager.StartAsync`):

1. `ConPtyPipeManager.CreatePipesAndPseudoConsole`: two anonymous pipes, then
   `CreatePseudoConsole(size, inputRead, outputWrite, 0, out hPC)`; the two handles given to the
   pseudoconsole are closed immediately, the mod keeps `inputWrite` and `outputRead`.[^pipe-manager]
2. `StartupInfoBuilder.Create()`: a `STARTUPINFOEX` with `dwFlags = STARTF_USESTDHANDLES` and null
   std handles. Without it, Windows clones the parent's non-console std handles into the child, so
   under a parent with redirected stdio (test host, CI, a game launched with pipes) the shell's
   text bypasses the pseudoconsole and the terminal stays blank (gotcha 14).[^startup-info]
3. `AttributeListBuilder`: `InitializeProcThreadAttributeList` (size probe, allocate, init) and
   `UpdateProcThreadAttribute(PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hPC)`, cleaned up in exactly one
   place to avoid a double free.[^attribute-list]
4. `CreateProcessW` with `EXTENDED_STARTUPINFO_PRESENT` (plus `CREATE_UNICODE_ENVIRONMENT` when an
   environment block is passed), the command line built by `ShellCommandResolver` with Windows
   argument quoting (see [shell resolution](./shell-detection-and-command-resolution.md)).
5. Start the output pump, create a `PtyInputQueue` writer thread, bump a generation counter that
   stale exit callbacks and teardowns check.

**Output.** `ConPtyOutputPump` runs a blocking `ReadFile` loop with a 64 KiB buffer on a
`LongRunning` task. It never sleeps: an earlier per-4 KiB `Task.Delay(1)` capped throughput at a
few MB/s and smeared full-screen redraws across frames. Broken pipe or invalid handle ends the loop
quietly. Each read is copied to a fresh array and raised as `DataReceived`, which the session
forwards to `Surface.Write`.[^output-pump]

**Input.** `Write` only enqueues into `PtyInputQueue` (1 MiB cap, overflow reported once per
episode); a dedicated background thread performs the blocking `WriteFile`, because a pipe write
can block indefinitely when the child stops reading and `Write` is called from the main thread.[^input-queue]

**Resize.** `ResizePseudoConsole` under the same lock teardown uses, so the handle cannot be closed
mid-call.

**Teardown** (`CleanupProcess`): snapshot and clear handles under the lock (no-op if the generation
is stale), then `ClosePseudoConsole` first (the only call that makes conhost flush remaining output
and break the pipes), wait up to 2 s for the pump to drain the tail, join the writer thread up to
2 s, cancel the read token outside the lock, and close each pipe handle only if its thread provably
exited. A handle whose thread is stuck is leaked rather than closed, because Win32 reuses handle
values and a late write could land in an unrelated kernel object.[^process-manager][^pty-teardown]

## Lifecycle and timing

| Work | Thread |
|---|---|
| `StartAsync` | Pool thread (`Task.Run` in `GhosttyTerminalController.StartSession` -> `SessionManager.CreateSessionAsync`) |
| `ReadFile` pump | Dedicated long-running thread per session (`thread:background`); hands bytes to `Surface.Write` (inbox lock) |
| `WriteFile` | Dedicated `PtyInputQueue` thread per session |
| `Write` (enqueue), `Resize` | Main thread during `frame:draw-ui` (user input, engine replies inside `BuildFrame`, grid resize in `TerminalWindow.Render`) |
| Teardown | Background: `TerminalSession.Dispose` disposes the surface on the main thread and runs `ProcessManager.Dispose` in `Task.Run`, so closing a tab never stalls a frame |

## Key code

| Path | Role |
|---|---|
| [purrTTY.Terminal/Pty/ProcessManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/ProcessManager.cs#L130-L245) | Start sequence |
| [purrTTY.Terminal/Pty/ProcessManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/ProcessManager.cs#L492-L568) | Flush-then-close teardown with leak-on-stuck |
| [purrTTY.Terminal/Pty/Process/ConPtyPipeManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/ConPtyPipeManager.cs) | Pipes and `CreatePseudoConsole` |
| [purrTTY.Terminal/Pty/Process/StartupInfoBuilder.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/StartupInfoBuilder.cs#L15-L31) | `STARTF_USESTDHANDLES` with null handles |
| [purrTTY.Terminal/Pty/Process/AttributeListBuilder.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/AttributeListBuilder.cs#L17-L64) | Pseudoconsole attribute list |
| [purrTTY.Terminal/Pty/Process/ConPtyOutputPump.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/ConPtyOutputPump.cs#L36-L85) | Never-sleeping `ReadFile` loop |
| [purrTTY.Terminal/Pty/Process/ConPtyNative.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/ConPtyNative.cs) | kernel32 P/Invokes and structs |
| [purrTTY.Terminal/Pty/Process/PtyTeardown.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/PtyTeardown.cs) | Shared bounded-wait helpers |
| [purrTTY.Terminal.Tests/ConPtyProcessManagerTests.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal.Tests/ConPtyProcessManagerTests.cs) | Real cmd.exe tests on the Windows CI leg |

## Snippets

```csharp
// purrTTY.Terminal/Pty/Process/StartupInfoBuilder.cs L15-L31
    internal static ConPtyNative.STARTUPINFOEX Create()
    {
        var startupInfo = new ConPtyNative.STARTUPINFOEX();
        startupInfo.StartupInfo.cb = Marshal.SizeOf<ConPtyNative.STARTUPINFOEX>();

        // STARTF_USESTDHANDLES with null std handles, exactly like node-pty and
        // Windows Terminal: without it, CreateProcess clones the PARENT's std
        // handles into the child (Win 8.1+ behavior when the parent's handles are
        // non-console, even with bInheritHandles=false). A parent with redirected
        // stdio — a test host, CI runner, or the game launched with pipes — then
        // hands the shell ITS pipes instead of the pseudoconsole: console-API
        // calls (title) still reach conpty, but all text output and stdin bypass
        // it, rendering an empty terminal. With the flag set and the handles
        // null, console initialization binds the conpty as the std handles.
        startupInfo.StartupInfo.dwFlags = ConPtyNative.STARTF_USESTDHANDLES;
        return startupInfo;
    }
```

```csharp
// purrTTY.Terminal/Pty/ProcessManager.cs L529-L555
        if (pseudoConsole != IntPtr.Zero)
        {
            ConPtyNative.ClosePseudoConsole(pseudoConsole);
        }

        // Drain: let the pump deliver the tail output (final prompt, a short
        // command's entire result) before its handle goes away.
        bool readerExited = PtyTeardown.WaitForPump(readTask, 2000);
        bool writerExited = PtyTeardown.ShutdownWriter(inputQueue, 2000);

        // Outside the lock — a CTS callback must never run while _processLock is held.
        PtyTeardown.CancelAndDispose(cts);

        // Close a handle only when its pump thread is provably out of it; otherwise
        // leak it — closing a handle another thread is blocked on races Win32 handle
        // recycling (the write could land in an unrelated kernel object).
        if (readerExited)
        {
            if (outputRead != IntPtr.Zero)
            {
                ConPtyNative.CloseHandle(outputRead);
            }
        }
        else
        {
            _logger.LogWarning("ConPTY output pump did not exit in time; leaking the output read handle");
        }
```

## Game APIs touched

None. All calls are Win32 (`kernel32`); the only game coupling is the thread the session is ticked
on.

## Pitfalls and version notes

- ConPTY needs Windows 10 1809 or later (`StartAsync` throws `PlatformNotSupportedException`
  elsewhere).
- Close the pseudoconsole before the pipes and drain afterwards; conhost does not break the pipes on
  its own after the child exits, so waiting for a "natural" EOF stalls.
- Do not cancel the read token before the drain: it is checked between reads, so early cancellation
  loses the flushed tail.
- Kitty graphics through ConPTY: the Window menu's "Diagnose kitty APC bytes" switch logs large
  writes that contain no `ESC _ G` starts, because ConPTY may strip or rewrite APC sequences
  (comment in `GhosttyTerminalSurface`; behavior across Windows builds not verified here).
- Leaking a handle on a stuck thread is deliberate; never "fix" it by closing.

## See also

- [POSIX pty spawn without fork](./posix-pty-spawn-without-fork.md)
- [PTY threads to frame tick handoff](./pty-threads-to-frame-tick-handoff.md)
- [Shell detection and command resolution](./shell-detection-and-command-resolution.md)
- [Custom shell registry and lifecycle](./custom-shell-registry-and-lifecycle.md) (the third `IProcessManager`)
- [purrTTY overview](../overview.md)
- [Windows ConPTY with a never-sleeping ReadFile pump, queued writer and close-first teardown](../../../howto/external-io/processes/host-and-supervise-an-external-process-from-a-mod/host-and-supervise-an-external-process-from-a-mod-2.md) - how-to article demonstrating this feature

[^session-factory]: purrTTY.Terminal/Sessions/TerminalSessionFactory.cs
[^pipe-manager]: purrTTY.Terminal/Pty/Process/ConPtyPipeManager.cs
[^startup-info]: purrTTY.Terminal/Pty/Process/StartupInfoBuilder.cs
[^attribute-list]: purrTTY.Terminal/Pty/Process/AttributeListBuilder.cs
[^output-pump]: purrTTY.Terminal/Pty/Process/ConPtyOutputPump.cs
[^input-queue]: purrTTY.Terminal/Pty/Process/PtyInputQueue.cs
[^process-manager]: purrTTY.Terminal/Pty/ProcessManager.cs
[^pty-teardown]: purrTTY.Terminal/Pty/Process/PtyTeardown.cs
