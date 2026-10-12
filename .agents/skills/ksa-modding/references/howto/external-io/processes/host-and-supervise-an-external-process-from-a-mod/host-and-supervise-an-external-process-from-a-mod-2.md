---
type: How-To
title: Windows ConPTY with a never-sleeping ReadFile pump, queued writer and close-first teardown
description: Start a real Windows shell under a pseudoconsole created by the game process, with a blocking ReadFile pump, a queued input writer and a teardown that closes the pseudoconsole first and leaks handles still in use
category: external-io
group: processes
topic: host-and-supervise-an-external-process-from-a-mod
approach: 2
difficulty: advanced
prerequisites: []
demonstrated_by: [mods/purrtty/features/conpty-shell-hosting]
techniques: [native-interop, background-thread]
hooks: []
lifecycle: [thread:background, frame:draw-ui]
game_types: [KSA.Program]
tags: [conpty, windows, pseudoconsole, pinvoke, handles, teardown, pump]
---

Outcome: on Windows every non-console terminal tab is a real shell (PowerShell, PowerShell Core, cmd or a WSL distribution) running under a ConPTY pseudoconsole created by the game process. The article covers the Win32 details that make a ConPTY work inside a host with redirected stdio, keep throughput high and tear down without losing output or racing handles. Demonstrated by [ConPTY shell hosting](../../../../mods/purrtty/features/conpty-shell-hosting.md) in purrtty. The manager sits behind an `IProcessManager` interface; a session factory picks the ConPTY manager on Windows and the Unix manager (next article) elsewhere ([purrTTY.Terminal/Sessions/TerminalSessionFactory.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Sessions/TerminalSessionFactory.cs)).

## Background: how the game does it

Nothing in KSA is involved; the game process simply owns the handles. What matters is that the frame thread must never block on a pipe: a `WriteFile` to a child that stopped reading can block forever, and a `ReadFile` is blocking by nature. So both live on their own threads and the frame only enqueues input.

## Lifecycle and timing

| Work | Thread |
|---|---|
| `StartAsync` | Pool thread (`Task.Run` in `GhosttyTerminalController.StartSession`, [purrTTY.Display/Ghostty/GhosttyTerminalController.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/GhosttyTerminalController.cs#L217-L222)) |
| `ReadFile` pump | Dedicated long-running thread per session (`thread:background`); hands bytes to the terminal surface through its inbox |
| `WriteFile` | Dedicated `PtyInputQueue` thread per session |
| `Write` (enqueue), `Resize` | Main thread during `frame:draw-ui` (user input, engine replies, grid resize) |
| Teardown | Background: `TerminalSession.Dispose` disposes the surface on the main thread and runs `ProcessManager.Dispose` in `Task.Run`, so closing a tab never stalls a frame |

How the pumped bytes reach the frame, and what happens with the HUD hidden, is the subject of [Host a terminal emulator in the game with pty sessions](../../terminals/host-a-terminal-with-pty-sessions-and-frame-handoff/overview.md).

## Steps

1. **Create two anonymous pipes and the pseudoconsole.** Close the two handles you gave to the pseudoconsole at once; keep only the input write end and the output read end:

   ```csharp
   // purrTTY.Terminal/Pty/Process/ConPtyPipeManager.cs L37-L66
               // Create input pipe
               if (!ConPtyNative.CreatePipe(out inputReadHandle, out inputWriteHandle, IntPtr.Zero, 0))
               {
                   throw new ProcessStartException($"Failed to create input pipe: {Marshal.GetLastWin32Error()}");
               }

               // Create output pipe
               if (!ConPtyNative.CreatePipe(out outputReadHandle, out outputWriteHandle, IntPtr.Zero, 0))
               {
                   throw new ProcessStartException($"Failed to create output pipe: {Marshal.GetLastWin32Error()}");
               }

               // Create pseudoconsole
               int result = ConPtyNative.CreatePseudoConsole(size, inputReadHandle, outputWriteHandle, 0, out pseudoConsole);
               if (result != 0)
               {
                   throw new ProcessStartException($"Failed to create pseudoconsole: {result}");
               }

               // Close the handles that were passed to the pseudoconsole (as per Microsoft docs)
               ConPtyNative.CloseHandle(inputReadHandle);
               ConPtyNative.CloseHandle(outputWriteHandle);

               // Return only the handles we keep (input write and output read)
               return new PipeHandles(
                   IntPtr.Zero,  // inputReadHandle closed
                   inputWriteHandle,
                   outputReadHandle,
                   IntPtr.Zero,  // outputWriteHandle closed
                   pseudoConsole);
   ```

2. **Build a `STARTUPINFOEX` with `STARTF_USESTDHANDLES` and null handles.** Without the flag Windows clones the parent's non-console standard handles into the child. Under a parent with redirected stdio (a test host, CI, a game launched with pipes) the shell's text then bypasses the pseudoconsole and the terminal stays blank:

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

3. **Attach the pseudoconsole through a process attribute list.** Probe the size, allocate, initialise, then update with `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`. Clean up in exactly one place so no failure path frees the list twice (a double `FreeHGlobal` silently corrupts the native heap):

   ```csharp
   // purrTTY.Terminal/Pty/Process/AttributeListBuilder.cs L17-L64
       internal static IntPtr CreateAttributeListWithPseudoConsole(IntPtr pseudoConsole)
       {
           // Probe for the size of the attribute list
           IntPtr attributeListSize = IntPtr.Zero;
           ConPtyNative.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);

           // Allocate memory for the attribute list
           IntPtr attributeList = Marshal.AllocHGlobal(attributeListSize);
           bool initialized = false;

           // Cleanup happens in exactly one place (the catch) so no failure path can
           // free the list twice — a double FreeHGlobal silently corrupts the native heap.
           try
           {
               // Initialize the attribute list
               if (!ConPtyNative.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
               {
                   throw new ProcessStartException($"Failed to initialize attribute list: {Marshal.GetLastWin32Error()}");
               }

               initialized = true;

               // Set the pseudoconsole attribute
               if (!ConPtyNative.UpdateProcThreadAttribute(
                       attributeList,
                       0,
                       ConPtyNative.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                       pseudoConsole,
                       IntPtr.Size,
                       IntPtr.Zero,
                       IntPtr.Zero))
               {
                   throw new ProcessStartException($"Failed to set pseudoconsole attribute: {Marshal.GetLastWin32Error()}");
               }

               return attributeList;
           }
           catch
           {
               if (initialized)
               {
                   ConPtyNative.DeleteProcThreadAttributeList(attributeList);
               }

               Marshal.FreeHGlobal(attributeList);
               throw;
           }
       }
   ```

4. **Create the process with `EXTENDED_STARTUPINFO_PRESENT`** (plus `CREATE_UNICODE_ENVIRONMENT` when an environment block is passed), building the command line with Windows argument quoting (see [approach 4](host-and-supervise-an-external-process-from-a-mod-4.md)). Wrap the process handle in a `Process` whose exit handler captures this start's generation, so a late exit callback can never tear down a restarted session ([purrTTY.Terminal/Pty/ProcessManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/ProcessManager.cs#L130-L245)).

5. **Pump output without ever sleeping.** A blocking `ReadFile` loop with a 64 KiB buffer on a `LongRunning` task. An earlier per-4 KiB `Task.Delay(1)` capped throughput at a few MB/s and smeared full-screen redraws across frames. Copy each read to a fresh array before raising the event; treat a broken pipe or invalid handle as the normal end of the loop:

   ```csharp
   // purrTTY.Terminal/Pty/Process/ConPtyOutputPump.cs L36-L85
       private static void ReadOutputLoop(
           IntPtr outputHandle,
           Action<byte[]> onDataReceived,
           Action<Exception, string> onProcessError,
           CancellationToken cancellationToken)
       {
           // Large enough to drain a fast producer (full-screen TUI redraws) in few
           // syscalls; ConPTY's internal pipe buffer is on this order anyway.
           const int bufferSize = 64 * 1024;
           byte[] buffer = new byte[bufferSize];

           try
           {
               while (!cancellationToken.IsCancellationRequested && outputHandle != IntPtr.Zero)
               {
                   if (ConPtyNative.ReadFile(outputHandle, buffer, bufferSize, out uint bytesRead, IntPtr.Zero))
                   {
                       if (bytesRead == 0)
                       {
                           // End of stream
                           break;
                       }

                       // Create a copy of the data to avoid buffer reuse issues
                       byte[] data = new byte[bytesRead];
                       Array.Copy(buffer, 0, data, 0, (int)bytesRead);

                       // Raise the DataReceived event (ConPTY output is never "error" stream)
                       onDataReceived(data);
                   }
                   else
                   {
                       int error = Marshal.GetLastWin32Error();
                       if (error is 109 or 6) // ERROR_BROKEN_PIPE / ERROR_INVALID_HANDLE (teardown closed the pipe)
                       {
                           break;
                       }

                       onProcessError(
                           new InvalidOperationException($"ReadFile failed with error {error}"),
                           $"Error reading from ConPTY output: Win32 error {error}");
                       break;
                   }
               }
           }
           catch (Exception ex)
           {
               onProcessError(ex, $"Error reading from ConPTY output: {ex.Message}");
           }
       }
   ```

6. **Queue input; write on a dedicated thread.** `Write` only enqueues into `PtyInputQueue` (1 MiB cap, overflow reported once per episode, input dropped rather than growing without bound); a background thread performs the blocking `WriteFile`:

   ```csharp
   // purrTTY.Terminal/Pty/Process/PtyInputQueue.cs L49-L80
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
   ```

7. **Resize under the same lock teardown uses.** `ResizePseudoConsole` is called under the process lock so the handle cannot be closed mid-call ([purrTTY.Terminal/Pty/ProcessManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/ProcessManager.cs#L331-L360)).

8. **Tear down in a fixed order.** Snapshot and clear the handles under the lock (a stale generation makes this a no-op), call `ClosePseudoConsole` first (the only call that makes conhost flush remaining output and break the pipes), wait up to 2 s for the pump to drain the tail, join the writer up to 2 s, cancel the read token outside the lock, and close each pipe handle only if its thread provably exited:

   ```csharp
   // purrTTY.Terminal/Pty/ProcessManager.cs L529-L568
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

           if (writerExited)
           {
               if (inputWrite != IntPtr.Zero)
               {
                   ConPtyNative.CloseHandle(inputWrite);
               }
           }
           else
           {
               _logger.LogWarning("ConPTY input writer did not exit in time; leaking the input write handle");
           }
       }
   ```

   A handle whose thread is stuck is leaked rather than closed, because Win32 reuses handle values and a late write could land in an unrelated kernel object.

## Complete example

| File | Role |
|---|---|
| [purrTTY.Terminal/Pty/ProcessManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/ProcessManager.cs) | Start sequence, resize, flush-then-close teardown |
| [purrTTY.Terminal/Pty/Process/ConPtyPipeManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/ConPtyPipeManager.cs) | Pipes and `CreatePseudoConsole` |
| [purrTTY.Terminal/Pty/Process/StartupInfoBuilder.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/StartupInfoBuilder.cs) | `STARTF_USESTDHANDLES` with null handles |
| [purrTTY.Terminal/Pty/Process/AttributeListBuilder.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/AttributeListBuilder.cs) | Pseudoconsole attribute list |
| [purrTTY.Terminal/Pty/Process/ConPtyOutputPump.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/ConPtyOutputPump.cs) | Never-sleeping `ReadFile` loop |
| [purrTTY.Terminal/Pty/Process/PtyTeardown.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/PtyTeardown.cs) | Shared bounded-wait helpers |
| [purrTTY.Terminal/Pty/Process/PtyInputQueue.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/PtyInputQueue.cs) | Bounded queue and writer thread |
| [purrTTY.Terminal.Tests/ConPtyProcessManagerTests.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal.Tests/ConPtyProcessManagerTests.cs) | Real `cmd.exe` tests on the Windows CI leg |

## Variations and gotchas

- **Generation counters.** A counter bumped on each start lets stale exit callbacks, waiter threads and teardowns check themselves and do nothing after a restart.
- **Bounded waits everywhere.** Pump waits never throw; a false return means leak the handle.
- **Closing the pseudoconsole first** is not optional: it is what lets the final prompt and a short command's whole result reach the pump.
- **Windows 10 1809 or later** is required for ConPTY; the manager throws `PlatformNotSupportedException` elsewhere.
- **Tests need a real shell.** The ConPTY tests run `cmd.exe` on the Windows CI leg.

## See also

- [Overview](overview.md)
- [Approach 3: POSIX pty](host-and-supervise-an-external-process-from-a-mod-3.md)
- [ConPTY shell hosting](../../../../mods/purrtty/features/conpty-shell-hosting.md)
- [Host a terminal emulator in the game with pty sessions and frame handoff](../../terminals/host-a-terminal-with-pty-sessions-and-frame-handoff/overview.md)
- [Bind and vendor native C libraries with layout-exact P/Invoke](../bind-and-vendor-native-c-libraries/overview.md)
