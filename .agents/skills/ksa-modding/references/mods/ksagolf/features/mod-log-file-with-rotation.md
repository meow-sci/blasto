---
type: Mod Feature
title: Mod log file with rotation and a verbose toggle
description: A per-mod log file beside KSA's own logs, truncated each session with the previous session kept as a prev log, batched writes with a flush timer, and a runtime Debug toggle instead of a debug build
mod: ksagolf
category: getting-started
techniques: [starmap-lifecycle, background-thread]
hooks: [starmap StarMapImmediateLoad, starmap StarMapUnload]
lifecycle: [startup:immediate-load, thread:background]
game_types: [KSA.Vehicle, KSA.Constants]
tags: [logging, diagnostics, log-rotation, verbose, cross-platform, startup]
---

KSAGolf writes everything it does to `KSAGolf.log` in KSA's user `Logs` folder, starts that file
fresh each session, and keeps the session before as `KSAGolf.prev.log`. A release build logs at
Info; ticking "Verbose log" on the panel drops the threshold to Debug at runtime, so a player can
produce a detailed bug report without a special build. The first lines of every session record the
mod version, the KSA build it was compiled for and the build that is actually running.

It is a small, dependency-free static class (`Ksa/Log.cs`) that any StarMap mod can copy.

## How it works

| Concern | Implementation |
|---|---|
| Location | First existing directory among `<MyDocuments>/My Games/Kitten Space Agency/Logs`, `$XDG_DATA_HOME` (or `~/.local/share`)`/Kitten Space Agency/Logs`, the same without `Logs`, `~/.config/Kitten Space Agency/Logs`, `~/My Games/Kitten Space Agency/Logs`, then the temp directory; resolved once[^golf-log] |
| Rotation | On first use: copy an existing `KSAGolf.log` to `KSAGolf.prev.log` (overwrite), then truncate with a session header[^golf-log] |
| Levels | `Debug`, `Info`, `Warn`, `Error`, `Off`; `Threshold` defaults to Debug under `#if DEBUG`, Info otherwise[^golf-log] |
| Cheap debug lines | `Debug(Func<string>)` skips building the string when below threshold |
| Batching | Lines append to a `StringBuilder` under a lock; flushed when 100 ms have passed, 64 KB are pending, or the line is Warn or Error |
| Quiet sessions | A `System.Threading.Timer` flushes every 100 ms on a thread-pool thread, so the tail reaches disk even when nothing else logs |
| Failure | Any write exception sets `_fileBroken` and the file is abandoned; `Console.WriteLine` still gets every line with a `[KSAGolf]` prefix |
| Shutdown | `[StarMapUnload]` calls `Log.Shutdown()` last, which disposes the timer and flushes[^golf-mod] |
| Verbose toggle | The panel checkbox sets `Log.Threshold = VerboseLog ? Debug : Info` and logs the change[^golf-ui] |
| Build identity | `Build.Version` from `AssemblyInformationalVersion` (commit hash stripped), `Build.KsaBuild` from the `KsaBuild` assembly metadata stamped from `ksa-assemblies.lock`, `Build.KsaRunning` from `typeof(KSA.Vehicle).Assembly.GetName().Version`[^golf-build][^golf-csproj] |
| Shared folder | `Log.Folder` is reused by the file bridge (`Logs/bridge/in` and `out`), so every mod-written file lives beside the log |

The README tells players exactly which two lines to look for (`loading (mod id: KSAGolf)` and, once
the game has loaded, `ready - open the 'KSAGolf' panel`) and maps "no log" vs "log but no panel" to
causes.[^golf-readme]

## Lifecycle and timing

- The first `Log.*` call, in `[StarMapImmediateLoad]` (`startup:immediate-load`), resolves the
  folder and rotates the file, so the rotation happens once per process before any game state exists.
- Writes come mostly from the main thread (frame and GUI hooks, main-thread Harmony patches), once
  in a while from a vehicle worker (a `CourseCollider` prefix's one-time complaint), and the flush
  timer runs on a thread-pool thread (`thread:background`); one lock (`Gate`) guards the buffer and
  the file.
- `Shutdown` must be the last call in `[StarMapUnload]`, which StarMap invokes when its loader is
  disposed at process exit, after `Program.Main` has returned; otherwise the final batched lines are
  lost. It is the one piece of the mod's unload that still matters at that point.

## Key code

| Path | Role |
|---|---|
| [src/KSAGolf/Ksa/Log.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Log.cs) | The whole logger |
| [src/KSAGolf/Ksa/Build.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Build.cs) | Version, built-for and running KSA build, developer flag |
| [src/KSAGolf/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Ui/Ui.cs#L309-L318) | Verbose log checkbox |
| [tools/ksa-user-dir.sh](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/ksa-user-dir.sh) | The shell side of the same search, so `tail -F` finds the same file |

## Snippets

```csharp
// src/KSAGolf/Ksa/Log.cs L202-L229
    private static void EnsureResolved()
    {
        if (_resolved) return;
        _resolved = true;

        foreach (string candidate in CandidateDirectories())
        {
            try
            {
                if (!Directory.Exists(candidate)) continue;

                string path = Path.Combine(candidate, "KSAGolf.log");

                // Truncated per session so the file reflects this run, not every run ever -- and the
                // last run kept beside it, because a game relaunched to look into a report is exactly
                // the moment the report's own log would otherwise be lost.
                if (File.Exists(path)) File.Copy(path, Path.Combine(candidate, "KSAGolf.prev.log"), overwrite: true);
                File.WriteAllText(path, $"=== KSAGolf session {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
                _path = path;
                Console.WriteLine($"{Prefix} logging to {path}");
                return;
            }
            catch
            {
                // Try the next candidate.
            }
        }
    }
```

```csharp
// src/KSAGolf/Ksa/Log.cs L116-L144
    private static void Write(Level level, string tag, string message)
    {
        if (level < Threshold) return;

        string line = $"{DateTime.Now:HH:mm:ss.fff} {tag} {message}";

        Console.WriteLine($"{Prefix} {line}");

        lock (Gate)
        {
            EnsureResolved();
            if (_path is null || _fileBroken) return;

            // Here rather than in EnsureResolved, which runs once ever: Shutdown stops the ticker,
            // and a mod loaded again in the same process has to get one back.
            _ticker ??= new Timer(_ => Flush(), null, FlushIntervalMs, FlushIntervalMs);

            Pending.Append(line).Append(Environment.NewLine);

            // Anything that went wrong goes to disk now. A warning is often the last thing written
            // before whatever it was warning about takes the process down.
            if (level >= Level.Warn
                || Pending.Length >= FlushBytes
                || Environment.TickCount64 - _lastFlushMs >= FlushIntervalMs)
            {
                FlushLocked();
            }
        }
    }
```

```csharp
// src/KSAGolf/Ksa/Ui/Ui.cs L309-L318
    private void DrawLogging()
    {
        if (ImGui.Checkbox("Verbose log", ref _config.VerboseLog))
        {
            Log.Threshold = _config.VerboseLog ? Log.Level.Debug : Log.Level.Info;
            Log.Info(_config.VerboseLog ? "verbose logging on" : "verbose logging off");
        }
        Tip("More detail in the log, which is what a bug report wants.");
        ImGui.TextDisabled("  -> Logs/KSAGolf.log");
    }
```

## Game APIs touched

| API | Decomp | Purpose |
|---|---|---|
| `KSA.Vehicle` assembly version | (assembly metadata) | The running KSA build, read without any file or network access |
| `Constants.DocumentsFolderPath` / `LogsFolderPath` | [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs) | Not used by `Log.cs`; KSA's own answer to "where is the user folder" (see pitfalls) |

## Pitfalls and version notes

- KSA's own log folder is `Constants.LogsFolderPath` = `Constants.DocumentsFolderPath` + `logs`
  (lower case), with `DocumentsFolderPath` = `SpecialFolder.Personal` + `My Games/Kitten Space Agency`.[^ksa-constants]
  `Log.cs` guesses paths with a capital `Logs` instead. That is harmless on Windows; on a
  case-sensitive filesystem the guess can miss and fall through to another candidate or the temp
  directory. Reading `KSA.Constants.LogsFolderPath` directly would remove the guesswork (the bridge
  already uses `Constants.DocumentsFolderPath` for screenshots).
- The class comment says KSA's internal logger is unreachable from mods. The decomp shows a public
  `Brutal.Logging.DefaultCategory.Log`; KSAGolf does not reference `Brutal.Logging`, so whether a mod
  writing there lands in KSA's session log is not demonstrated here.[^brutal-defaultcategory]
- Release builds carry no `.pdb`, so stack traces in the log have no line numbers by design.[^golf-csproj]
- The flush timer is static; `Shutdown` disposes it and the next `Write` recreates it, which is what
  keeps a reload in the same process working.

## See also

- [Developer panel and menu-bar entry](developer-panel-and-menu-bar.md)
- [File command bridge and MCP server](file-command-bridge.md)
- [Content-plus-code mod layout](content-and-code-mod-layout.md)
- [Packaging, deploy and release pipeline](packaging-and-release.md)
- [Stamp the targeted KSA build into assembly metadata and compare it at run time](../../../howto/getting-started/lifecycle/check-the-game-version/check-the-game-version-2.md) - how-to article demonstrating this feature
- [Write a batched session log with .prev.log rotation, timer flush and a verbose toggle](../../../howto/getting-started/lifecycle/write-a-per-mod-log-file/write-a-per-mod-log-file-1.md) - how-to article demonstrating this feature

[^golf-log]: src/KSAGolf/Ksa/Log.cs
[^golf-build]: src/KSAGolf/Ksa/Build.cs
[^golf-ui]: src/KSAGolf/Ksa/Ui/Ui.cs
[^golf-mod]: src/KSAGolf/Ksa/KSAGolfMod.cs
[^golf-csproj]: src/KSAGolf/KSAGolf.csproj
[^golf-readme]: README.md
[^ksa-constants]: KSA.Constants (decompiled, build 2026.10.10.5554)
[^brutal-defaultcategory]: Brutal.Logging.DefaultCategory (decompiled, build 2026.10.10.5554)
