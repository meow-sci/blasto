---
type: How-To
title: ShellCommandResolver, WslDistributionDetector and UnixShellDetector feeding a menu cache
description: Detect installed shells and WSL distributions once on a background thread with bounded waits, cache an immutable menu snapshot, and build launch command lines with correct quoting
category: external-io
group: processes
topic: host-and-supervise-an-external-process-from-a-mod
approach: 4
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/purrtty/features/shell-detection-and-command-resolution]
techniques: [background-thread, starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded, thread:background, frame:draw-ui]
game_types: [KSA.Program]
tags: [shell, wsl, detection, quoting, command-line, cache, menu]
---

Outcome: the New Tab and New Window menus list only shells that will actually start: PowerShell, PowerShell Core and cmd if they resolve, a WSL2 submenu with one entry per installed distribution, one entry per shell in `/etc/shells` on Linux and macOS (the user's `$SHELL` marked default), plus a game console and other mods' custom shells. Getting there without stalling the game is the point: `wsl --list` can take seconds on a cold WSL service and a dead network share in `PATH` can block a file probe. Demonstrated by [Shell detection and command resolution](../../../../mods/purrtty/features/shell-detection-and-command-resolution.md) in purrtty. Requires StarMap.

## Background: how the game does it

The game provides no menu cache or process probing; the only game-side fact is that the menu bar is drawn on the main thread in `frame:draw-ui`, where any blocking probe would stall the frame. The mod therefore runs all detection once on a pool thread and publishes an immutable snapshot that the menu reads.

## Lifecycle and timing

| Step | When and where |
|---|---|
| `ShellMenuCache.BeginDetection()` | From the mod initialiser at `startup:all-mods-loaded`; the work runs on a pool thread (`thread:background`); idempotent |
| Menu reads `ShellMenuCache.Current` | `frame:draw-ui`, inside KSA's menu bar; reads only the published snapshot |
| `ResolveShellCommandArgv` / `ResolveShellCommandLine` | On the session-creation pool thread, inside `StartAsync` |
| `Auto` resolution on Windows | Calls `WslDistributionDetector`, cached after the first (background) detection |

## Steps

1. **Describe a launch with one options type.** `ProcessLaunchOptions` carries a `ShellType` (`Auto`, `Wsl`, `PowerShell`, `PowerShellCore`, `Cmd`, `Custom`, `CustomGame`), arguments, working directory, environment overrides (`TERM`, `TERM_PROGRAM`, `COLORTERM`), an optional custom shell id, the initial size and an optional startup command typed into the shell after start. Factory methods build each kind (`CreateWsl(distro)` adds `--distribution <name>`) ([purrTTY.Terminal/Pty/ProcessLaunchOptions.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/ProcessLaunchOptions.cs#L122-L255)).

2. **Resolve a type to a program and argv.** `Auto` on Windows is WSL if at least one distribution exists, else PowerShell, else cmd; on Unix it is `$SHELL` if it exists, else the first of zsh, bash and sh on `PATH`. `Custom` accepts a path or a bare name looked up on `PATH`. `CustomGame` throws: custom shells never go through a process manager ([purrTTY.Terminal/Pty/Process/ShellCommandResolver.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/ShellCommandResolver.cs#L85-L160)).

3. **Quote for the platform.** ConPTY takes one command line, so tokens are joined with the `CommandLineToArgvW` rules (backslashes doubled only before a quote and at the end of the argument). Unix keeps argv discrete for `posix_spawnp`. Never join and re-split:

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

4. **Define availability as resolvability, cached.** The menus can then never offer a shell the launch would reject. The first call per type pays for the `PATH` scan; later calls hit the dictionary:

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

5. **List WSL distributions with every wait bounded.** `wsl.exe` exists on stock Windows even without WSL, so the executable's presence is not evidence. Read UTF-16 output, drain both pipes asynchronously, wait at most 15 s, kill the process tree on timeout, bound the output read too (a grandchild can inherit the stdout handle), return nothing on a non-zero exit, strip the BOM:

   ```csharp
   // purrTTY.Terminal/Pty/WslDistributionDetector.cs L100-L154
           try
           {
               var startInfo = new SystemProcessStartInfo
               {
                   FileName = "wsl",
                   Arguments = "--list --quiet",
                   RedirectStandardOutput = true,
                   RedirectStandardError = true,
                   UseShellExecute = false,
                   CreateNoWindow = true,
                   StandardOutputEncoding = Encoding.Unicode  // WSL outputs UTF-16 with BOM on Windows
               };

               using var process = SystemProcess.Start(startInfo);
               if (process == null)
               {
                   return distributions;
               }

               // Drain both pipes asynchronously (a full stderr pipe would block the
               // child) and bound the wait — a wedged WSL service must not hang the
               // detection thread indefinitely.
               var outputTask = process.StandardOutput.ReadToEndAsync();
               _ = process.StandardError.ReadToEndAsync();

               if (!process.WaitForExit(WslListTimeoutMs))
               {
                   try
                   {
                       process.Kill(entireProcessTree: true);
                   }
                   catch
                   {
                       // Already exited or not killable; nothing more to do.
                   }

                   return distributions;
               }

               // Nonzero exit covers both "WSL not set up" and "no distributions
               // installed" — either way there is nothing to offer in the menus.
               if (process.ExitCode != 0)
               {
                   return distributions;
               }

               // WaitForExit(timeout) does not wait for redirected-output EOF; if a
               // grandchild inherited the stdout handle the read could outlive the
               // child indefinitely. Bound it like everything else here.
               if (!outputTask.Wait(WslListTimeoutMs))
               {
                   return distributions;
               }

               string output = outputTask.Result;
   ```

6. **List Unix shells** from `/etc/shells` plus `$SHELL`, de-duplicated by executable name, with the default marked and listed first, cached for the process lifetime ([purrTTY.Terminal/Pty/UnixShellDetector.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/UnixShellDetector.cs)).

7. **Publish one immutable snapshot from a pool task,** with a fallback if detection fails, so the player is never left with only the game console:

   ```csharp
   // purrTTY.GameMod/ShellMenuCache.cs L34-L61
       public static void BeginDetection()
       {
           if (Interlocked.Exchange(ref s_detectionStarted, 1) != 0)
           {
               return;
           }

           _ = Task.Run(() =>
           {
               try
               {
                   s_snapshot = Detect();
               }
               catch (Exception ex)
               {
                   ModLog.Log.Debug($"purrTTY ShellMenuCache: shell detection failed: {ex.Message}");

                   // Detection failing must not strand the player with only the
                   // Game Console: Auto needs no detection and is the one shell
                   // type valid on every platform.
                   s_snapshot = new Snapshot
                   {
                       Entries = [("Default Shell", ShellType.Auto), ("Game Console", ShellType.CustomGame)],
                       WslDistributions = [],
                       UnixShells = [],
                   };
               }
           });
   ```

   Until the snapshot is published, the menus render a minimal fallback. Detection is free of game logging so it can run in tests.

## Complete example

| File | Role |
|---|---|
| [purrTTY.Terminal/Pty/Process/ShellCommandResolver.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/ShellCommandResolver.cs) | Command-line quoting, per-type resolution, `Auto` fallbacks |
| [purrTTY.Terminal/Pty/WslDistributionDetector.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/WslDistributionDetector.cs) | Bounded `wsl --list --quiet` |
| [purrTTY.Terminal/Pty/UnixShellDetector.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/UnixShellDetector.cs) | `/etc/shells` and `$SHELL` |
| [purrTTY.Terminal/Pty/ShellAvailabilityChecker.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/ShellAvailabilityChecker.cs) | Availability equals resolvability, cached |
| [purrTTY.GameMod/ShellMenuCache.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/ShellMenuCache.cs) | One-shot background snapshot for the menus |
| [purrTTY.Terminal.Tests/PtyInputContractTests.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal.Tests/PtyInputContractTests.cs) | Quoting and argv tests |

## Variations and gotchas

- **Detection results are cached for the process lifetime.** Installing a shell or a WSL distribution mid-game does not update the menus until restart (the Unix detector exposes a force-refresh parameter).
- **Never probe on the frame.** Even a `File.Exists` on `PATH` can block on a dead network share.
- **A false positive is worse than a missing entry,** so availability is defined by running the same resolver the launch uses.
- **Quoting is testable without a shell:** the contract tests assert the argument strings for paths with spaces, quotes and trailing backslashes.

## See also

- [Overview](overview.md)
- [Approach 2: Windows ConPTY](host-and-supervise-an-external-process-from-a-mod-2.md), [Approach 3: POSIX pty](host-and-supervise-an-external-process-from-a-mod-3.md)
- [Shell detection and command resolution](../../../../mods/purrtty/features/shell-detection-and-command-resolution.md)
- [Host a terminal emulator in the game with pty sessions and frame handoff](../../terminals/host-a-terminal-with-pty-sessions-and-frame-handoff/overview.md)
