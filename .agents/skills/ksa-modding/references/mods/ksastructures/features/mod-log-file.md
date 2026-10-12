---
type: Mod Feature
title: Per-mod log file beside KSA's logs
description: Writes the mod's own batched log to Logs/KSAStructures.log under the KSA user folder, keeps the previous session's log, flushes on a timer thread and immediately on warnings, and switches to debug detail at runtime
mod: ksastructures
category: getting-started
techniques: [background-thread, starmap-lifecycle]
hooks: [starmap StarMapImmediateLoad, starmap StarMapUnload]
lifecycle: [startup:immediate-load, thread:background]
game_types: [KSA.Constants]
tags: [logging, diagnostics, file-io, timer, bug-report]
---

StarMap mods only reach stdout, and KSA's own session log is written by an internal logger mods
cannot use, so KSAStructures keeps its own: `Logs/KSAStructures.log` in the KSA user folder, with the
previous session kept as `KSAStructures.prev.log`. The README tells players to check it for
`office: landmarks placed` after loading.[^mod-log][^mod-readme]

## How it works

1. **Find the folder once.** `EnsureResolved` tries, in order, `Documents/My Games/Kitten Space
   Agency/Logs` (Windows, and Wine/Proton), `$XDG_DATA_HOME` or `~/.local/share` variants, `~/.config`,
   `~/My Games`, then the temp folder, and uses the first that already exists. That matches the folder
   KSA itself uses for logs on Windows (`Constants.LogsFolderPath` is `DocumentsFolderPath/logs`).[^mod-log][^ksa-constants]
2. **One file per session.** On first use it copies any existing log to `KSAStructures.prev.log`
   and truncates the current one with a session header, so a game relaunched to investigate a report
   does not lose the report's log.[^mod-log]
3. **Batched writes.** Lines are appended to a `StringBuilder` under a lock and flushed when 100 ms
   have passed, 64 KB are pending, or the line is a warning or error. A `System.Threading.Timer` on
   the thread pool flushes every 100 ms even when nothing logs, so the tail reaches disk.[^mod-log]
4. **Levels.** `Debug` takes a `Func<string>` overload so interpolation is skipped when the line is
   filtered. Debug builds start at `Debug`, release builds at `Info`; the settings window's
   **Verbose log** box changes `Log.Threshold` at runtime.[^mod-log]
5. **Versions in the first lines.** `[StarMapImmediateLoad]` logs the mod version, the KSA build it
   was compiled against (stamped from `ksa-assemblies.lock` into an `AssemblyMetadata` attribute by
   the csproj) and the KSA build actually running (the loaded `KSA` assembly's version).[^mod-entry][^mod-build][^mod-csproj]
6. **Shutdown.** `[StarMapUnload]` calls `Log.Shutdown()` last, which disposes the timer and flushes.[^mod-entry]
7. **Stdout too.** Every line is also written to the console with a `[KSAStructures]` prefix.

## Lifecycle and timing

| When | Thread | What |
|---|---|---|
| first `Log.*` call (at `[StarMapImmediateLoad]`) | main | Resolve the folder, rotate the previous log |
| any hook | caller's | Append under the lock; flush on warn/error or thresholds |
| every 100 ms | thread pool (`thread:background`) | Timer flush; synchronizes through the same lock |
| `[StarMapUnload]` | main | Stop the timer, final flush |

## Key code

| Path | Role |
|---|---|
| [src/KSAStructures/Ksa/Log.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Log.cs#L19-L144) | Levels, buffer and `Write` |
| [src/KSAStructures/Ksa/Log.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Log.cs#L146-L185) | `Shutdown` and flushing |
| [src/KSAStructures/Ksa/Log.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Log.cs#L200-L269) | Folder search and rotation |
| [src/KSAStructures/Ksa/Build.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Build.cs#L12-L104) | Version, compiled-against and running KSA build |
| [src/KSAStructures/KSAStructures.csproj](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructures.csproj) | `KsaBuild` metadata stamped from the lock file |

## Snippets

Write, with a timer that keeps the tail on disk:

```csharp
// src/KSAStructures/Ksa/Log.cs L116-L144
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

Rotating the previous session's log:

```csharp
// src/KSAStructures/Ksa/Log.cs L202-L229
    private static void EnsureResolved()
    {
        if (_resolved) return;
        _resolved = true;

        foreach (string candidate in CandidateDirectories())
        {
            try
            {
                if (!Directory.Exists(candidate)) continue;

                string path = Path.Combine(candidate, "KSAStructures.log");

                // Truncated per session so the file reflects this run, not every run ever -- and the
                // last run kept beside it, because a game relaunched to look into a report is exactly
                // the moment the report's own log would otherwise be lost.
                if (File.Exists(path)) File.Copy(path, Path.Combine(candidate, "KSAStructures.prev.log"), overwrite: true);
                File.WriteAllText(path, $"=== KSAStructures session {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
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

## Game APIs touched

| API | Decomp | Use |
|---|---|---|
| `Constants.DocumentsFolderPath`, `LogsFolderPath` | [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281-L283) | KSA's own user folder (the mod probes paths itself) |

## Pitfalls and version notes

- **Never throw from logging.** A broken file sets `_fileBroken` and logging continues on stdout only.
- **Lock around the timer.** The flush timer runs on a thread-pool thread; every buffer access is
  under the same lock.
- **Dispose the timer on unload**, or it keeps firing into an unloaded mod.

## See also

- [Frame step plumbing](./frame-step-plumbing.md) - where `Unload` and the fault counter live
- [Settings menu](./settings-menu.md) - the verbose log switch
- [Developer file bridge and MCP server](./dev-file-bridge-and-mcp.md) - files written beside the log
- [Find a writable log folder by probing Documents, XDG and fallback locations](../../../howto/getting-started/lifecycle/write-a-per-mod-log-file/write-a-per-mod-log-file-3.md) - how-to article demonstrating this feature
- [Derive the data folder from the mod's log folder, with a temp-directory fallback](../../../howto/persistence/user-data/choose-where-a-mod-stores-its-files/choose-where-a-mod-stores-its-files-4.md) - how-to article demonstrating this feature

[^mod-log]: src/KSAStructures/Ksa/Log.cs
[^mod-entry]: src/KSAStructures/Ksa/KSAStructuresMod.cs
[^mod-build]: src/KSAStructures/Ksa/Build.cs
[^mod-csproj]: src/KSAStructures/KSAStructures.csproj
[^mod-readme]: KSAStructures README
[^ksa-constants]: KSA.Constants (decompiled, build 2026.10.10.5554)
