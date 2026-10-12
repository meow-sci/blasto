---
type: How-To
title: Pad register arguments for variadic C calls such as ioctl on Apple arm64
description: Call a variadic libc function from C# on Apple arm64 by importing it a second time with filler arguments so the real argument lands on the stack where the variadic ABI looks for it
category: external-io
group: processes
topic: bind-and-vendor-native-c-libraries
approach: 3
difficulty: advanced
prerequisites: []
demonstrated_by: [mods/purrtty/features/posix-pty-spawn-without-fork]
techniques: [native-interop]
hooks: []
lifecycle: [thread:background, frame:draw-ui]
game_types: []
tags: [pinvoke, variadic, ioctl, apple-silicon, arm64, abi, libc]
---

Outcome: a plain P/Invoke of a variadic C function works on Linux and Windows and on Intel Macs, then fails on Apple silicon. This is a stand-alone pitfall a reader may search for by name: on Apple arm64, variadic arguments are passed on the stack, so a normal P/Invoke that puts the third argument in a register delivers garbage. The fix is a second import with filler arguments. Demonstrated by [POSIX pty spawn without fork](../../../../mods/purrtty/features/posix-pty-spawn-without-fork.md) in purrtty, where it appears while setting a terminal's window size.

## Background: how the game does it

The game is not involved; the cause is the platform ABI. `ioctl(int fd, unsigned long request, ...)` is declared variadic in C. On Linux and on macOS x64 the anonymous variadic arguments travel in registers like ordinary ones, so declaring `ioctl(int fd, nuint request, ref WinSize winSize)` works. On Apple arm64 the AArch64 Apple ABI passes anonymous variadic arguments on the stack instead. A normal P/Invoke places the third argument in register x2, the callee's `va_arg` reads the stack, and the kernel sees a garbage `winsize*`.

## Lifecycle and timing

The call is made at two moments. While spawning a shell (a pool thread, `thread:background`) the pty winsize is set on the slave descriptor before the child runs, through [purrTTY.Terminal/Pty/Process/UnixPtySpawner.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/UnixPtySpawner.cs#L79). On a grid resize it is set again on the master descriptor from [purrTTY.Terminal/Pty/UnixProcessManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/UnixProcessManager.cs#L296), on whichever thread calls `Resize` (the frame tick, `frame:draw-ui`, in purrTTY). Both call the same `UnixPtyNative.WinSizeIoctl` wrapper.

## Steps

1. **Import the function twice.** The plain import is used everywhere the ABI is register-based. The second import names the same entry point but declares enough named integer arguments to exhaust the argument registers (x0 to x7), so the real pointer is the ninth and lands on the stack exactly where `va_arg` looks:

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

2. **Select the import at runtime** with a single wrapper that the rest of the code calls, so no caller has to know. The test is the OS and the process architecture, not the OS alone.

3. **Keep the failure visible.** The initial window-size set is not fatal (the shell starts with default dimensions until the first resize) but must not fail silently, so the spawner logs a warning when the ioctl fails ([purrTTY.Terminal/Pty/Process/UnixPtySpawner.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/UnixPtySpawner.cs#L74-L86)). On an affected Mac the symptom is a shell that starts with the wrong dimensions or a warning in the log, not a crash.

4. **Repeat for any variadic import.** `fcntl`, `open` with a mode argument and `printf`-style functions share the same trap; the same recipe (a second import with filler arguments, selected for Apple arm64) applies to each. In this reference only `ioctl` needs it ([docs/gotchas.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/docs/gotchas.md#L136-L142)).

## Complete example

| File | Role |
|---|---|
| [purrTTY.Terminal/Pty/Process/UnixPtyNative.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/UnixPtyNative.cs) | Both `ioctl` imports and `WinSizeIoctl` |
| [purrTTY.Terminal/Pty/Process/UnixPtySpawner.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/UnixPtySpawner.cs) | Initial winsize before the spawn |
| [purrTTY.Terminal/Pty/UnixProcessManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/UnixProcessManager.cs) | Resize on the master descriptor |

## Variations and gotchas

- **Linux and macOS x64 keep variadic arguments in registers,** so only Apple arm64 needs the workaround; testing on those platforms will not reveal the bug.
- **Count carefully.** The filler count must make the real argument the first stack argument; if the callee's named parameters change, recount.
- **Alternatives.** A C shim compiled per platform avoids the trick but adds a native library to ship; function pointers with the `__arglist` convention are not portable across .NET runtimes.
- **Document it where it lives.** The comment next to the declaration says why the second import exists; without it a later cleanup will merge the two.

## See also

- [Overview](overview.md)
- [Approach 1: vendored libghostty-vt](bind-and-vendor-native-c-libraries-1.md)
- [POSIX pty spawn without fork](../../../../mods/purrtty/features/posix-pty-spawn-without-fork.md)
- [Host and supervise an external process or virtual machine from a KSA mod](../host-and-supervise-an-external-process-from-a-mod/overview.md)
