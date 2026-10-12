---
type: How-To
title: Find a writable log folder by probing Documents, XDG and fallback locations
description: Resolve a log directory across Windows, Wine/Proton and Linux by trying existing candidate folders in order, with the temp folder as a last resort, plus immediate flush on warnings
category: getting-started
group: lifecycle
topic: write-a-per-mod-log-file
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksastructures/features/mod-log-file]
techniques: [logging, starmap-lifecycle, background-thread]
hooks: [starmap StarMapImmediateLoad, starmap StarMapUnload]
lifecycle: [startup:immediate-load, thread:background, event:shutdown]
game_types: [KSA.Constants]
tags: [logging, path-probing, xdg, linux, wine, log-file]
---

KSAStructures solves "where do I put my log" without asking the game: it probes the plausible user-data folders on
Windows, Wine/Proton and Linux and uses the first one that already exists, falling back to the temp folder so the
mod can always say where it is logging. The class is the same batched logger as [Rotation to .prev.log, timer flush, runtime debug toggle](./write-a-per-mod-log-file-1.md); this
article is about the directory search and the thread-pool flush. When you can reference `KSA.dll`,
`Constants.LogsFolderPath` is simpler and instance-aware ([Constants.LogsFolderPath and a developer marker file](./write-a-per-mod-log-file-2.md)); probing is for code that
must resolve a folder without game types.

## Background: how the game does it

KSA's logs live in `Constants.LogsFolderPath` = `DocumentsFolderPath/logs`, with `DocumentsFolderPath` =
`<Personal>/My Games/Kitten Space Agency`.[current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281-L287) On Windows and under Wine or Proton, `MyDocuments`
maps to `Documents`, so the first candidate (`.../My Games/Kitten Space Agency/Logs`) matches. On Linux the user data
location varies, so the probe tries XDG paths and others. Because the probe spells the folder `Logs`, it differs
from the game's `logs` only by case, which matters on case-sensitive file systems.

## Lifecycle and timing

| When | Thread | What |
|---|---|---|
| First `Log.*` call (at `StarMapImmediateLoad`) | main | resolve the folder, rotate the previous log |
| Any hook | caller's | append under the lock; flush on warn/error or thresholds |
| Every 100 ms | thread pool (`thread:background`) | timer flush through the same lock |
| `StarMapUnload` | main | stop the timer, final flush |

## Steps

1. **Enumerate candidates best-first**, yielding only locations that make sense, and never create directories (a
   wrong guess then costs a failed `Directory.Exists`):[src/KSAStructures/Ksa/Log.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Log.cs#L231-L269)

   ```csharp
   // src/KSAStructures/Ksa/Log.cs L236-L269
       private static IEnumerable<string> CandidateDirectories()
       {
           const string game = "Kitten Space Agency";

           // Windows, and Wine or Proton, which map Documents into the prefix the same way.
           string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
           if (!string.IsNullOrEmpty(documents))
           {
               yield return Path.Combine(documents, "My Games", game, "Logs");
           }

           // Linux. XDG_DATA_HOME when set, otherwise the default it stands for.
           string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
           string xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? "";
           if (string.IsNullOrEmpty(xdgData) && !string.IsNullOrEmpty(home))
           {
               xdgData = Path.Combine(home, ".local", "share");
           }

           if (!string.IsNullOrEmpty(xdgData))
           {
               yield return Path.Combine(xdgData, game, "Logs");
               yield return Path.Combine(xdgData, game);
           }

           if (!string.IsNullOrEmpty(home))
           {
               yield return Path.Combine(home, ".config", game, "Logs");
               // Some Linux ports keep the Windows-style layout under $HOME regardless.
               yield return Path.Combine(home, "My Games", game, "Logs");
           }

           yield return Path.GetTempPath();
       }
   ```

2. **Use the first existing directory and rotate there.** The loop tries each candidate inside a `try`/`catch`, copies
   an existing `KSAStructures.log` to `KSAStructures.prev.log`, truncates, writes a session header and prints the
   path to the console:[src/KSAStructures/Ksa/Log.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Log.cs#L202-L229)

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

3. **Write with a thread-pool timer and immediate flush on warnings** (identical to approach 1's `Write`: a
   `System.Threading.Timer` flushes every 100 ms even when nothing logs, so the tail reaches disk; `Warn`/`Error`
   lines flush at once). `Debug` takes a `Func<string>` so interpolation is skipped when filtered.
4. **Print the chosen path** to stdout on startup; if the log falls through to temp, the line tells the player where
   to look. The README tells players which line to look for (`office: landmarks placed`).
5. **Log versions first** at `StarMapImmediateLoad`: mod version, KSA build compiled against (stamped from
   `ksa-assemblies.lock`), KSA build running ([Stamp the targeted build into assembly metadata](../check-the-game-version/check-the-game-version-2.md)).
6. **Flush last** in `StarMapUnload` (`Log.Shutdown()`).

## Complete example

[src/KSAStructures/Ksa/Log.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Log.cs) (probing, rotation, batching) and [src/KSAStructures/Ksa/KSAStructuresMod.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KSAStructuresMod.cs) (calls it at `StarMapImmediateLoad`, shuts it down at
`StarMapUnload`).

## Variations and gotchas

- **Never throw from logging.** A broken file sets `_fileBroken` and logging continues on stdout only.
- **Lock around the timer.** The timer runs on the thread pool; every buffer access is under the same lock.
- **Dispose the timer on unload**, or it keeps firing after `StarMapUnload` until the process ends (nothing is
  actually unloaded).
- **Case.** `Logs` versus the game's `logs` matters on Linux; if you can use `Constants.LogsFolderPath`, do.
- **Instance paths.** The probe does not follow `-InstancePath`; `Constants.DocumentsFolderPath` does
  ([Manual Harmony prefix on Constants.DocumentsFolderPath](../../loader/how-starmap-launches-ksa/how-starmap-launches-ksa-3.md)).
- **Missing directories are skipped, not created**, so a fresh install without a logs folder falls to the next
  candidate; the game creates its own logs folder at startup before any mod hook after `BeforeMain` runs.

## See also

- [Rotation to .prev.log, timer flush, runtime debug toggle](./write-a-per-mod-log-file-1.md), [Constants.LogsFolderPath and a developer marker file](./write-a-per-mod-log-file-2.md), [Log from a mod: per-mod log file, game log category and log-once](./overview.md)
- [Per-mod log file beside KSA's logs](../../../../mods/ksastructures/features/mod-log-file.md)
- [How StarMap launches KSA and loads your mod](../../loader/how-starmap-launches-ksa/overview.md), [Clean up on game exit and relaunch the game from a mod](../clean-up-on-game-exit/overview.md)
