---
type: Mod Feature
title: POSIX pty shells spawned without fork
description: Allocate a pty with posix_openpt and launch the shell with posix_spawnp (SETSID, slave opened as stdin, signal state reset) so a multi-gigabyte, heavily threaded game process never forks, with the arm64 variadic ioctl fix and a poll-based reader
mod: purrtty
category: external-io
techniques: [native-interop, background-thread]
hooks:
  - starmap StarMapAfterGui
  - module-initializer purrTTY.Core.Terminal.Process.UnixPtyNative.RegisterResolver
lifecycle: [thread:background, frame:draw-ui]
game_types: []
tags: [linux, macos, pty, posix-spawn, process, shell, native, arm64]
---

On Linux and macOS each shell tab runs on a real pseudo-terminal, so job control, `SIGWINCH`
resizes and full-screen TUIs behave as in a desktop terminal. The notable choice is that the game
process never calls `fork()`: forking a large, heavily threaded process risks allocator-lock
deadlocks in the child and `ENOMEM` under strict overcommit, so the shell is started with
`posix_spawnp`, which implementations run vfork-style (gotcha 16).[^gotchas] All of it is plain
`libc` P/Invoke from C#, with no helper binary.

## How it works

**Spawn** (`UnixPtySpawner.Spawn`):

1. `posix_openpt(O_RDWR | O_NOCTTY)`, `grantpt`, `unlockpt`, `ptsname_r` for the slave path.
2. Open the slave with `O_NOCTTY` in the parent only to `ioctl(TIOCSWINSZ)` the initial size, since
   the winsize must be set on a slave fd to stick; close it right after the spawn returns.
3. File actions: optional `posix_spawn_file_actions_addchdir_np` (glibc 2.29+ / macOS 10.15+,
   skipped with `EntryPointNotFoundException` otherwise), `addopen(0, slavePath, O_RDWR)`, then
   `adddup2(0, 1)` and `adddup2(0, 2)`.
4. Attributes: `POSIX_SPAWN_SETSID | SETSIGDEF | SETSIGMASK`, an empty mask and all signals reset
   to default. A new session leader that opens a tty acquires it as its controlling terminal on
   both Linux and macOS; the signal reset matters because .NET ignores `SIGPIPE` process-wide and
   `SIG_IGN` survives `exec`, which would break pipelines in the shell.
5. `posix_spawnp(&pid, shell, actions, attr, argv, envp)` with a merged environment (parent env,
   launch overrides such as `TERM=xterm-256color`, `TERM_PROGRAM=ghostty`, `COLORTERM=truecolor`,
   and `SHELL` pointed at the program when it is a known shell).[^spawner][^launch-options]

**Platform constants** differ per OS and are chosen at runtime (`O_NOCTTY`, `TIOCSWINSZ`,
`POSIX_SPAWN_SETSID`); opaque spawn structs are oversized zeroed buffers that the init functions
construct in place.

**The arm64 `ioctl` trap.** `ioctl` is variadic. On Apple arm64, variadic arguments are passed on
the stack while a normal P/Invoke puts the third argument in register x2, so the kernel reads a
garbage `winsize*`. `WinSizeIoctl` calls a second import of `ioctl` with six dummy `nint` arguments
so the real pointer lands on the stack; other platforms use the plain three-argument form.[^unix-native]

**I/O and lifecycle** (`UnixProcessManager`):

- Reader: `UnixPtyOutputPump` on a `LongRunning` task, `poll(fd, 100 ms)` then `read` into a 64 KiB
  buffer. It never sleeps while data flows; the poll timeout exists only so cancellation can be
  observed without closing the fd under a blocked `read`. EOF (macOS) or `EIO` (Linux) means the
  slave side is gone; `POLLNVAL` exits too.[^unix-pump]
- Writer: `PtyInputQueue` thread doing `write(2)` loops (handles `EINTR`).
- Waiter: a `LongRunning` task blocks in `waitpid`, decodes the exit status (`128 + signal` for a
  signaled death), lets the reader drain up to 2 s, cleans up, then raises `ProcessExited`.
- Stop: `kill(-pid, SIGHUP)` to the session's process group, `SIGKILL` after 2 s.
- Cleanup closes the master fd only after both the reader and the writer joined; otherwise the fd is
  leaked on purpose (fd reuse race). A generation counter makes stale waiter or teardown calls no-ops
  after a restart.[^unix-manager]
- Resize: `TIOCSWINSZ` on the master under the state lock; the kernel delivers `SIGWINCH`.

## Lifecycle and timing

| Work | Thread |
|---|---|
| Spawn | Pool thread from session creation (`Task.Run`) |
| `poll`/`read` pump, `waitpid` waiter, `write` writer | Three dedicated background threads per session (`thread:background`) |
| Enqueue input, resize | Main thread in `frame:draw-ui` (`TerminalWindow.Render` and the encoders) |
| PTY bytes to the engine | Pump calls `Surface.Write` (inbox lock); drained by `BuildFrame` on the main thread |

## Key code

| Path | Role |
|---|---|
| [purrTTY.Terminal/Pty/Process/UnixPtySpawner.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/UnixPtySpawner.cs#L27-L104) | pty allocation and initial winsize |
| [purrTTY.Terminal/Pty/Process/UnixPtySpawner.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/UnixPtySpawner.cs#L106-L218) | `posix_spawnp` with file actions and attributes |
| [purrTTY.Terminal/Pty/Process/UnixPtyNative.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/UnixPtyNative.cs#L129-L145) | Variadic `ioctl` workaround |
| [purrTTY.Terminal/Pty/Process/UnixPtyOutputPump.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/UnixPtyOutputPump.cs#L31-L120) | poll-based reader |
| [purrTTY.Terminal/Pty/UnixProcessManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/UnixProcessManager.cs#L110-L234) | Start and stop |
| [purrTTY.Terminal/Pty/UnixProcessManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/UnixProcessManager.cs#L336-L376) | `waitpid` waiter |
| [purrTTY.Terminal.Tests/UnixProcessManagerTests.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal.Tests/UnixProcessManagerTests.cs) | Real `/bin/sh` tests on macOS and Linux CI |

## Snippets

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

```csharp
// purrTTY.Terminal/Pty/Process/UnixPtySpawner.cs L146-L179
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
```

## Game APIs touched

None; this is `libc` only. The game matters only as the parent process whose size and threads make
`fork()` dangerous and whose signal dispositions must not leak into the shell.

## Pitfalls and version notes

- Never `fork()` or `forkpty()` from the game process.
- Reset signal dispositions in the child; inherited `SIG_IGN` for `SIGPIPE` breaks `cmd | head`.
- Set the initial size on a slave fd; setting it on the master before the slave opens does not stick.
- Do not `DllImport("libc")` without a resolver; see [native library resolver](./per-platform-native-library-resolver.md).
- `posix_spawn_file_actions_addchdir_np` is missing on old glibc; the shell then starts in the game's
  working directory.
- Only osx-arm64 and linux-x64 natives are shipped for the VT engine; the pty code itself is
  architecture-neutral apart from the arm64 `ioctl` path.

## See also

- [Hosting Windows shells in a ConPTY](./conpty-shell-hosting.md)
- [PTY threads to frame tick handoff](./pty-threads-to-frame-tick-handoff.md)
- [Shell detection and command resolution](./shell-detection-and-command-resolution.md)
- [Per-platform native library resolver](./per-platform-native-library-resolver.md)
- [purrTTY overview](../overview.md)
- [Pad register arguments for variadic C calls such as ioctl on Apple arm64](../../../howto/external-io/processes/bind-and-vendor-native-c-libraries/bind-and-vendor-native-c-libraries-3.md) - how-to article demonstrating this feature
- [POSIX pty via posix_openpt and posix_spawnp with a poll reader and a waitpid thread](../../../howto/external-io/processes/host-and-supervise-an-external-process-from-a-mod/host-and-supervise-an-external-process-from-a-mod-3.md) - how-to article demonstrating this feature

[^gotchas]: purrTTY docs/gotchas.md
[^spawner]: purrTTY.Terminal/Pty/Process/UnixPtySpawner.cs
[^launch-options]: purrTTY.Terminal/Pty/ProcessLaunchOptions.cs
[^unix-native]: purrTTY.Terminal/Pty/Process/UnixPtyNative.cs
[^unix-pump]: purrTTY.Terminal/Pty/Process/UnixPtyOutputPump.cs
[^unix-manager]: purrTTY.Terminal/Pty/UnixProcessManager.cs
