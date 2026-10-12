---
type: How-To
title: POSIX pty via posix_openpt and posix_spawnp with a poll reader and a waitpid thread
description: Start a shell on a Unix pseudo-terminal from a large multithreaded game process without calling fork, with a poll-based reader, a queued writer, a waitpid thread and a SIGHUP then SIGKILL stop
category: external-io
group: processes
topic: host-and-supervise-an-external-process-from-a-mod
approach: 3
difficulty: advanced
prerequisites: []
demonstrated_by: [mods/purrtty/features/posix-pty-spawn-without-fork]
techniques: [native-interop, background-thread]
hooks: []
lifecycle: [thread:background, frame:draw-ui]
game_types: [KSA.Program]
tags: [pty, posix, posix_spawn, libc, pinvoke, linux, macos, waitpid]
---

Outcome: on Linux and macOS each shell tab runs on a real pseudo-terminal, so job control, `SIGWINCH` resizes and full-screen TUIs behave as in a desktop terminal. The notable choice is that the game process never calls `fork()`: forking a large, heavily threaded process risks allocator-lock deadlocks in the child and `ENOMEM` under strict overcommit, so the shell is started with `posix_spawnp`, which implementations run vfork-style. All of it is plain libc P/Invoke from C#, with no helper binary. Demonstrated by [POSIX pty spawn without fork](../../../../mods/purrtty/features/posix-pty-spawn-without-fork.md) in purrtty.

## Background: how the game does it

The game exposes nothing here. The relevant constraints are those of the host process: it is large and heavily multithreaded, and .NET ignores `SIGPIPE` process-wide, a disposition (`SIG_IGN`) that survives `exec` and would break pipelines in the child shell unless it is reset in the spawn attributes.

## Lifecycle and timing

| Work | Thread |
|---|---|
| Spawn | Pool thread from session creation (`Task.Run`) |
| `poll`/`read` pump, `waitpid` waiter, `write` writer | Three dedicated background threads per session (`thread:background`) |
| Enqueue input, resize | Main thread in `frame:draw-ui` |
| PTY bytes to the engine | The pump forwards to the terminal surface's inbox; the frame drains it. See [the terminal topic](../../terminals/host-a-terminal-with-pty-sessions-and-frame-handoff/overview.md) |

## Steps

1. **Allocate the pty and read its slave path:** `posix_openpt(O_RDWR | O_NOCTTY)`, `grantpt`, `unlockpt`, `ptsname_r`. Open the slave in the parent only to `ioctl(TIOCSWINSZ)` the initial size, because the winsize must be set on a slave fd to stick, and close it right after the spawn returns:

   ```csharp
   // purrTTY.Terminal/Pty/Process/UnixPtySpawner.cs L36-L66
           int masterFd = UnixPtyNative.posix_openpt(UnixPtyNative.O_RDWR | UnixPtyNative.O_NOCTTY);
           if (masterFd < 0)
           {
               throw new ProcessStartException(
                   $"posix_openpt failed: {Marshal.GetLastPInvokeErrorMessage()}", shellPath);
           }

           try
           {
               if (UnixPtyNative.grantpt(masterFd) != 0)
               {
                   throw new ProcessStartException(
                       $"grantpt failed: {Marshal.GetLastPInvokeErrorMessage()}", shellPath);
               }

               if (UnixPtyNative.unlockpt(masterFd) != 0)
               {
                   throw new ProcessStartException(
                       $"unlockpt failed: {Marshal.GetLastPInvokeErrorMessage()}", shellPath);
               }

               var nameBuffer = new byte[256];
               if (UnixPtyNative.ptsname_r(masterFd, nameBuffer, (nuint)nameBuffer.Length) != 0)
               {
                   throw new ProcessStartException(
                       $"ptsname_r failed: {Marshal.GetLastPInvokeErrorMessage()}", shellPath);
               }

               int nameLength = Array.IndexOf(nameBuffer, (byte)0);
               string slavePath = Encoding.UTF8.GetString(nameBuffer, 0, nameLength < 0 ? nameBuffer.Length : nameLength);

   ```

2. **Describe the child with spawn file actions.** Optionally `posix_spawn_file_actions_addchdir_np` (glibc 2.29+ and macOS 10.15+, skipped on `EntryPointNotFoundException`), then open the slave as fd 0 and duplicate it onto fds 1 and 2:

   ```csharp
   // purrTTY.Terminal/Pty/Process/UnixPtySpawner.cs L132-L155
               if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
               {
                   try
                   {
                       ThrowIfSpawnError(
                           UnixPtyNative.posix_spawn_file_actions_addchdir_np(fileActions, NullTerminated(workingDirectory)),
                           "posix_spawn_file_actions_addchdir_np", shellPath);
                   }
                   catch (EntryPointNotFoundException)
                   {
                       // glibc < 2.29: the shell starts in the game's working directory instead.
                   }
               }

               // Child: setsid (new session) + open slave as fd 0 → controlling tty, then
               // mirror onto stdout/stderr. The parent never opens the slave, so the only
               // slave fds belong to the child and EOF/EIO on the master tracks its exit.
               ThrowIfSpawnError(
                   UnixPtyNative.posix_spawn_file_actions_addopen(fileActions, 0, NullTerminated(slavePath), UnixPtyNative.O_RDWR, 0),
                   "posix_spawn_file_actions_addopen", shellPath);
               ThrowIfSpawnError(UnixPtyNative.posix_spawn_file_actions_adddup2(fileActions, 0, 1),
                   "posix_spawn_file_actions_adddup2", shellPath);
               ThrowIfSpawnError(UnixPtyNative.posix_spawn_file_actions_adddup2(fileActions, 0, 2),
                   "posix_spawn_file_actions_adddup2", shellPath);
   ```

3. **Set spawn attributes:** a new session (`POSIX_SPAWN_SETSID`), an empty signal mask and all signals reset to default. A new session leader that opens a tty acquires it as its controlling terminal on both Linux and macOS:

   ```csharp
   // purrTTY.Terminal/Pty/Process/UnixPtySpawner.cs L157-L186
               ThrowIfSpawnError(UnixPtyNative.posix_spawnattr_init(attr), "posix_spawnattr_init", shellPath);
               attrInit = true;

               var emptySigset = new byte[UnixPtyNative.SigsetBufferSize];
               var fullSigset = new byte[UnixPtyNative.SigsetBufferSize];
               Array.Fill(fullSigset, (byte)0xFF);
               ThrowIfSpawnError(UnixPtyNative.posix_spawnattr_setsigmask(attr, emptySigset),
                   "posix_spawnattr_setsigmask", shellPath);
               ThrowIfSpawnError(UnixPtyNative.posix_spawnattr_setsigdefault(attr, fullSigset),
                   "posix_spawnattr_setsigdefault", shellPath);
               ThrowIfSpawnError(
                   UnixPtyNative.posix_spawnattr_setflags(attr, (short)(
                       UnixPtyNative.POSIX_SPAWN_SETSID |
                       UnixPtyNative.POSIX_SPAWN_SETSIGDEF |
                       UnixPtyNative.POSIX_SPAWN_SETSIGMASK)),
                   "posix_spawnattr_setflags", shellPath);

               IntPtr argv = BuildNativeStringArray(
                   [shellPath, .. arguments], nativeStrings, out argvHandle);
               IntPtr envp = BuildNativeStringArray(
                   BuildEnvironment(environmentOverrides, shellPath), nativeStrings, out envpHandle);

               int rc = UnixPtyNative.posix_spawnp(out int pid, NullTerminated(shellPath), fileActions, attr, argv, envp);
               if (rc != 0)
               {
                   throw new ProcessStartException(
                       $"posix_spawnp('{shellPath}') failed: {Marshal.GetPInvokeErrorMessage(rc)}", shellPath);
               }

               return pid;
   ```

   Platform constants (`O_NOCTTY`, `TIOCSWINSZ`, `POSIX_SPAWN_SETSID`) differ per OS and are chosen at runtime; opaque spawn structs are oversized zeroed buffers that the init functions construct in place. The environment is the parent's, plus launch overrides such as `TERM=xterm-256color`, `TERM_PROGRAM=ghostty` and `COLORTERM=truecolor`, with `SHELL` pointed at the program when it is a known shell ([purrTTY.Terminal/Pty/ProcessLaunchOptions.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/ProcessLaunchOptions.cs#L122-L255)).

4. **Resolve `libc` yourself.** The logical name `libc` does not reliably `dlopen` on glibc (the real library is `libc.so.6`; `libc.so` is a linker script from the dev package), so a `DllImportResolver` registered from a module initializer maps it before the first P/Invoke:

   ```csharp
   // purrTTY.Terminal/Pty/Process/UnixPtyNative.cs L22-L62
       private const string Libc = "libc";

       private static bool s_resolverRegistered;

       // Same auto-registration pattern as Ghostty.Vt's NativeLibraryResolver: the
       // resolver must be installed before any libc P/Invoke in this assembly runs.
       [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255",
           Justification = "Intentional: registers the libc DllImportResolver before first P/Invoke")]
       [ModuleInitializer]
       internal static void RegisterResolver()
       {
           if (s_resolverRegistered || OperatingSystem.IsWindows())
           {
               return;
           }

           s_resolverRegistered = true;
           NativeLibrary.SetDllImportResolver(typeof(UnixPtyNative).Assembly, Resolve);
       }

       private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
       {
           if (libraryName != Libc)
           {
               return IntPtr.Zero; // fall through to default probing (e.g. kernel32 on Windows)
           }

           string[] candidates = OperatingSystem.IsMacOS()
               ? ["/usr/lib/libSystem.B.dylib", "libSystem.B.dylib"]
               : ["libc.so.6", "libc.so", "libc"];

           foreach (string candidate in candidates)
           {
               if (NativeLibrary.TryLoad(candidate, out IntPtr handle))
               {
                   return handle;
               }
           }

           return IntPtr.Zero;
       }
   ```

5. **Read with `poll` then `read`.** The reader is a `LongRunning` task with a 64 KiB buffer; it never sleeps while data flows. The 100 ms `poll` timeout exists only so cancellation can be observed without closing the fd under a blocked `read`. End of file (macOS) or `EIO` (Linux) means the slave side is gone, and `POLLNVAL` exits too ([purrTTY.Terminal/Pty/Process/UnixPtyOutputPump.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/UnixPtyOutputPump.cs#L31-L120)).

6. **Write from a queue thread** (`PtyInputQueue`, `write(2)` loops that handle `EINTR`) exactly as in the ConPTY article.

7. **Wait for the child on its own thread.** `waitpid` blocks (retrying on `EINTR`), the status is decoded (`128 + signal` for a signalled death), the reader gets up to 2 s to drain the tail, cleanup runs and then `ProcessExited` is raised. A generation check makes a stale waiter a no-op after a restart:

   ```csharp
   // purrTTY.Terminal/Pty/UnixProcessManager.cs L336-L376
       private void WaitForExit(int pid, int generation)
       {
           int status = 0;
           int result;
           do
           {
               result = UnixPtyNative.waitpid(pid, out status, 0);
           } while (result < 0 && Marshal.GetLastPInvokeError() == UnixPtyNative.EINTR);

           int exitCode = result == pid ? UnixPtyNative.DecodeExitStatus(status) : 0;

           Task? readerTask;
           lock (_stateLock)
           {
               if (generation != _generation)
               {
                   _logger.LogDebug("Stale waiter for pid {Pid} superseded by restart; ignoring exit", pid);
                   return;
               }

               _exited = true;
               _exitCode = exitCode;
               readerTask = _readerTask;
           }

           // Normal case: the dead child held the only slave fds, so the reader hits
           // EOF/EIO by itself once the buffered output is delivered. The timeout only
           // matters when an orphaned grandchild still holds the slave open.
           try
           {
               readerTask?.Wait(2000);
           }
           catch
           {
               // reader errors are reported through ProcessError
           }

           CleanupProcess(generation);

           ProcessExited?.Invoke(this, new ProcessExitedEventArgs(exitCode, pid));
       }
   ```

8. **Stop with SIGHUP, then SIGKILL.** The child is a session leader, so its process group id equals its pid and a negative pid signals the whole group; escalate after 2 s:

   ```csharp
   // purrTTY.Terminal/Pty/UnixProcessManager.cs L196-L233
       public async Task StopAsync(CancellationToken cancellationToken = default)
       {
           ThrowIfDisposed();

           int pid;
           bool running;
           Task? waiterTask;
           int generation;
           lock (_stateLock)
           {
               pid = _pid;
               running = _pid != 0 && !_exited;
               waiterTask = _waiterTask;
               generation = _generation;
           }

           try
           {
               if (running && pid != 0)
               {
                   // The child was made a session leader (SETSID), so its pgid == pid and
                   // a negative pid signals the whole group.
                   SignalProcessGroup(pid, UnixPtyNative.SIGHUP);

                   if (waiterTask != null && await Task.WhenAny(waiterTask, Task.Delay(2000, cancellationToken)) != waiterTask)
                   {
                       SignalProcessGroup(pid, UnixPtyNative.SIGKILL);
                       if (waiterTask != null)
                       {
                           await Task.WhenAny(waiterTask, Task.Delay(2000, cancellationToken));
                       }
                   }
               }
           }
           finally
           {
               CleanupProcess(generation);
           }
   ```

9. **Close the master fd only after the reader and the writer joined;** otherwise leak the fd on purpose (the fd-reuse race, as with handles on Windows). Resize is `TIOCSWINSZ` on the master under the state lock; the kernel delivers `SIGWINCH`.

## The variadic ioctl trap on Apple arm64

`ioctl` is variadic. On Apple arm64 anonymous variadic arguments are passed on the stack, while a normal P/Invoke puts the third argument in register x2, so the kernel reads a garbage `winsize` pointer. The fix is a second import of the same function with six dummy `nint` arguments so the real pointer lands on the stack. It is covered as its own pitfall in [Bind and vendor native C libraries with layout-exact P/Invoke](../bind-and-vendor-native-c-libraries/overview.md):

   ```csharp
   // purrTTY.Terminal/Pty/Process/UnixPtyNative.cs L129-L145
       [DllImport(Libc, SetLastError = true)]
       private static extern int ioctl(int fd, nuint request, ref WinSize winSize);

       // ioctl(2) is variadic, and on Apple arm64 the variadic ABI passes anonymous
       // arguments on the STACK while a normal P/Invoke puts the third argument in x2 —
       // the kernel then reads a garbage winsize pointer. Eight named filler arguments
       // exhaust x0–x7 so the real pointer lands at sp, exactly where va_arg looks.
       // (Linux and macOS x64 keep variadic args in registers, so the plain form works.)
       [DllImport(Libc, EntryPoint = "ioctl", SetLastError = true)]
       private static extern int ioctl_appleArm64(
           int fd, nuint request, nint x2, nint x3, nint x4, nint x5, nint x6, nint x7, ref WinSize winSize);

       /// <summary>ioctl(fd, TIOCSWINSZ/TIOCGWINSZ) with the correct ABI per platform.</summary>
       internal static int WinSizeIoctl(int fd, nuint request, ref WinSize winSize)
           => OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
               ? ioctl_appleArm64(fd, request, 0, 0, 0, 0, 0, 0, ref winSize)
               : ioctl(fd, request, ref winSize);
   ```

## Complete example

| File | Role |
|---|---|
| [purrTTY.Terminal/Pty/Process/UnixPtySpawner.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/UnixPtySpawner.cs) | pty allocation, initial winsize, `posix_spawnp` with file actions and attributes |
| [purrTTY.Terminal/Pty/Process/UnixPtyNative.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/UnixPtyNative.cs) | libc imports, resolver, variadic `ioctl` workaround |
| [purrTTY.Terminal/Pty/Process/UnixPtyOutputPump.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/UnixPtyOutputPump.cs) | poll-based reader |
| [purrTTY.Terminal/Pty/UnixProcessManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/UnixProcessManager.cs) | Start, stop, waiter |
| [purrTTY.Terminal.Tests/UnixProcessManagerTests.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal.Tests/UnixProcessManagerTests.cs) | Real `/bin/sh` tests on macOS and Linux CI |

## Variations and gotchas

- **No fork, ever,** in a process this large and threaded; if you need a feature `posix_spawn` lacks, add a spawn file action rather than a pre-exec hook.
- **Parent never opens the slave for long.** With only the child holding slave fds, EOF or `EIO` on the master tracks the child's exit; an orphaned grandchild can keep the slave open, which is why the reader wait after `waitpid` is bounded.
- **Variadic ABI.** Linux and macOS x64 keep variadic arguments in registers, so only Apple arm64 needs the workaround.
- **Process group signals.** `kill(-pid, ...)` reaches the whole session group; killing only the pid would leave children.

## See also

- [Overview](overview.md)
- [Approach 2: Windows ConPTY](host-and-supervise-an-external-process-from-a-mod-2.md)
- [POSIX pty spawn without fork](../../../../mods/purrtty/features/posix-pty-spawn-without-fork.md)
- [Bind and vendor native C libraries with layout-exact P/Invoke](../bind-and-vendor-native-c-libraries/overview.md)
- [Host a terminal emulator in the game with pty sessions and frame handoff](../../terminals/host-a-terminal-with-pty-sessions-and-frame-handoff/overview.md)
