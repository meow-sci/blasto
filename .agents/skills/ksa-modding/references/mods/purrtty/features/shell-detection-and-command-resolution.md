---
type: Mod Feature
title: Shell detection and launch command resolution
description: Detect installed shells, WSL distributions and /etc/shells entries once on a background thread with bounded waits, define availability as resolvability, and build Windows command lines with CommandLineToArgvW quoting versus discrete argv on Unix
mod: purrtty
category: external-io
techniques: [background-thread]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded, thread:background, frame:draw-ui]
game_types: []
tags: [shell, wsl, process, windows, linux, macos, menus, caching]
---

The New Tab / New Window menus list only shells that will actually start: PowerShell, PowerShell
Core and cmd if they resolve, a WSL2 submenu with one entry per installed distribution, one entry
per shell in `/etc/shells` on Linux and macOS (the user's `$SHELL` marked default), plus Game Console
and other mods' custom shells. Getting there without stalling the game is the point: `wsl --list`
can take seconds on a cold WSL service and a dead network share in `PATH` can block a file probe.

## How it works

**Launch spec.** `ProcessLaunchOptions` carries `ShellType` (`Auto`, `Wsl`, `PowerShell`,
`PowerShellCore`, `Cmd`, `Custom`, `CustomGame`), arguments, working directory (the factory methods use the user profile; the per-window default
options built by `GhosttySessionManagerFactory` use the game's current directory), environment overrides (`TERM`, `TERM_PROGRAM=ghostty`, `COLORTERM`, ...), the custom shell
id, initial size and an optional `StartupCommand` typed into the shell after start. Factory methods
build each kind (`CreateWsl(distro)` adds `--distribution <name>`).[^launch-options]

**Resolution** (`ShellCommandResolver`):

- `ResolveShellCommandArgv` maps a type to `(path, argv)`. `Auto` on Windows is WSL if at least one
  distribution exists, else PowerShell, else cmd; on Unix `$SHELL` if it exists, else the first of
  zsh, bash, sh on `PATH`. `Custom` accepts a path or a bare name looked up on `PATH`. `CustomGame`
  throws: custom shells never go through a process manager.
- `ResolveShellCommandLine` (ConPTY) joins argv into one command line, quoting each token per the
  `CommandLineToArgvW` rules (backslashes doubled only before a quote and at the end). Unix keeps
  argv discrete for `posix_spawnp`. Never join and re-split.[^resolver]

**Detection**, each cached for the process lifetime and free of game logging so it runs in tests:

- `ShellAvailabilityChecker.IsShellAvailable(type)` is a `ConcurrentDictionary.GetOrAdd` over
  "does the resolver succeed", so the menus can never offer a shell the launch would reject.[^availability]
- `WslDistributionDetector` runs `wsl --list --quiet` (UTF-16 output, BOM stripped), drains both
  pipes, waits at most 15 s, kills the process tree on timeout, and returns no distributions on a
  non-zero exit. `wsl.exe` exists on stock Windows even without WSL, so presence of the executable is
  not evidence.[^wsl-detector]
- `UnixShellDetector` reads `/etc/shells` plus `$SHELL`, de-duplicates by executable name and marks
  the default.[^unix-detector]

**Menu snapshot.** `ShellMenuCache.BeginDetection()` runs all of the above once in `Task.Run` and
publishes an immutable snapshot; on failure it falls back to Default Shell (`Auto`) and Game
Console.[^shell-menu-cache]

## Lifecycle and timing

| Step | When and where |
|---|---|
| `ShellMenuCache.BeginDetection()` | Called from `TerminalMod.InitializeTerminal` at `startup:all-mods-loaded`; work on a pool thread |
| Menu reads `ShellMenuCache.Current` | `frame:draw-ui`, inside KSA's menu bar; reads only the snapshot |
| `ResolveShellCommandArgv/Line` | On the session-creation pool thread, inside `StartAsync` |
| `Auto` resolution on Windows | Calls `WslDistributionDetector`, which is cached after the first (background) detection |

## Key code

| Path | Role |
|---|---|
| [purrTTY.Terminal/Pty/Process/ShellCommandResolver.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/ShellCommandResolver.cs#L19-L75) | Windows command-line quoting |
| [purrTTY.Terminal/Pty/Process/ShellCommandResolver.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/ShellCommandResolver.cs#L85-L160) | Per-type resolution and `Auto` fallbacks |
| [purrTTY.Terminal/Pty/WslDistributionDetector.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/WslDistributionDetector.cs) | Bounded `wsl --list --quiet` |
| [purrTTY.Terminal/Pty/UnixShellDetector.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/UnixShellDetector.cs) | `/etc/shells` + `$SHELL` |
| [purrTTY.Terminal/Pty/ShellAvailabilityChecker.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/ShellAvailabilityChecker.cs#L14-L51) | Availability = resolvability, cached |
| [purrTTY.Terminal/Pty/ProcessLaunchOptions.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/ProcessLaunchOptions.cs#L122-L255) | Launch spec factories and terminal identity env |
| [purrTTY.GameMod/ShellMenuCache.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/ShellMenuCache.cs#L34-L107) | One-shot background snapshot for the menus |
| [purrTTY.Terminal.Tests/PtyInputContractTests.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal.Tests/PtyInputContractTests.cs) | Quoting and argv tests |

## Snippets

```csharp
// purrTTY.Terminal/Pty/Process/ShellCommandResolver.cs L41-L75
    internal static void AppendQuotedArgument(System.Text.StringBuilder builder, string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny(CharsRequiringQuotes) < 0)
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        int pendingBackslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                pendingBackslashes++;
                continue;
            }

            if (c == '"')
            {
                builder.Append('\\', pendingBackslashes * 2 + 1);
                builder.Append('"');
                pendingBackslashes = 0;
                continue;
            }

            builder.Append('\\', pendingBackslashes);
            builder.Append(c);
            pendingBackslashes = 0;
        }

        // Trailing backslashes must be doubled so they don't escape the closing quote.
        builder.Append('\\', pendingBackslashes * 2);
        builder.Append('"');
    }
```

```csharp
// purrTTY.Terminal/Pty/ShellAvailabilityChecker.cs L24-L50
    public static bool IsShellAvailable(ShellType shellType)
    {
        return ShellAvailabilityCache.GetOrAdd(shellType, static type =>
        {
            switch (type)
            {
                case ShellType.Auto: // falls back across shells — always offerable
                case ShellType.Custom: // checked when the path is provided
                case ShellType.CustomGame: // Game Console is always available
                    return true;

                default:
                    try
                    {
                        _ = ShellCommandResolver.ResolveShellCommandArgv(
                            new ProcessLaunchOptions { ShellType = type });
                        return true;
                    }
                    catch
                    {
                        // Resolution failure (including unsupported types) means
                        // the shell cannot be launched.
                        return false;
                    }
            }
        });
    }
```

## Game APIs touched

None. The only game-facing concern is never running these probes on the main thread.

## Pitfalls and version notes

- Any probe that spawns a process or touches the file system can block for seconds; do it once, off
  the render thread, and cache it.
- Offering bare `wsl` with no distribution yields a dead session; gate WSL on a non-empty
  distribution list.
- Quote Windows arguments per `CommandLineToArgvW`; an unquoted `C:\Program Files\...` breaks.
- `TERM_PROGRAM` is not forwarded over SSH; remote guests (gatOS) must export it themselves (comment
  in `ProcessLaunchOptions`).

## See also

- [Game menu with ModMenu fallback](./game-menu-with-modmenu-fallback.md)
- [Hosting Windows shells in a ConPTY](./conpty-shell-hosting.md)
- [POSIX pty spawn without fork](./posix-pty-spawn-without-fork.md)
- [Saved terminal layouts](./saved-terminal-layouts.md) (startup commands)
- [purrTTY overview](../overview.md)
- [ShellCommandResolver, WslDistributionDetector and UnixShellDetector feeding a menu cache](../../../howto/external-io/processes/host-and-supervise-an-external-process-from-a-mod/host-and-supervise-an-external-process-from-a-mod-4.md) - how-to article demonstrating this feature

[^launch-options]: purrTTY.Terminal/Pty/ProcessLaunchOptions.cs
[^resolver]: purrTTY.Terminal/Pty/Process/ShellCommandResolver.cs
[^availability]: purrTTY.Terminal/Pty/ShellAvailabilityChecker.cs
[^wsl-detector]: purrTTY.Terminal/Pty/WslDistributionDetector.cs
[^unix-detector]: purrTTY.Terminal/Pty/UnixShellDetector.cs
[^shell-menu-cache]: purrTTY.GameMod/ShellMenuCache.cs
