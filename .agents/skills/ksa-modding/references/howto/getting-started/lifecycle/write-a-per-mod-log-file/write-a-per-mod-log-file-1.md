---
type: How-To
title: Write a batched session log with .prev.log rotation, timer flush and a verbose toggle
description: Copy a small static Log class that rotates the last session to .prev.log, batches writes with a 100 ms timer, flushes warnings immediately and lets a UI checkbox switch to debug level
category: getting-started
group: lifecycle
topic: write-a-per-mod-log-file
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/mod-log-file-with-rotation, mods/ksacars/features/mod-log-file]
techniques: [logging, starmap-lifecycle, background-thread]
hooks: [starmap StarMapImmediateLoad, starmap StarMapUnload]
lifecycle: [startup:immediate-load, thread:background, event:shutdown, frame:draw-ui]
game_types: [KSA.Constants]
tags: [logging, log-file, rotation, timer, verbose, starmap]
---

A StarMap mod's `Console.WriteLine` only reaches the terminal that launched the game. KSAGolf and KSACars (same
author, an identical static `Log` class) keep their own file beside KSA's logs: truncated each session, the
previous session kept as `.prev.log`, batched writes so a hot loop does not become a syscall per line, a timer so
the tail reaches disk, and a runtime switch to debug level for bug reports. This article is the walk-through
of that class. Variants: [Constants.LogsFolderPath and a developer marker file](./write-a-per-mod-log-file-2.md) (developer marker and build identity),
[Path probing across Documents and XDG locations](./write-a-per-mod-log-file-3.md) (where the folder is found), [One LogCategory in a tiny shared assembly](./write-a-per-mod-log-file-4.md) to
[HashSet-keyed WarnOnce/ErrorOnce with the exception type](./write-a-per-mod-log-file-7.md) (game log category and log-once).

## Background: how the game does it

KSA's own logging is separate: the `Program` constructor creates `Constants.LogsFolderPath`
(`<Documents>/My Games/Kitten Space Agency/logs`) and registers the in-game console, the process console and a
log file as sinks for the `Brutal.Logging` system.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L689-L725)[current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281-L287) A mod can write
through that system (see [One LogCategory in a tiny shared assembly](./write-a-per-mod-log-file-4.md)); the classes here deliberately keep an independent file
with a stable name that is easy to tail and attach to a bug report. Note one thing both mods' comments get wrong:
KSA's logger is reachable (`Brutal.Logging.DefaultCategory.Log` is a public static), a separate file is merely a
choice.

## Lifecycle and timing

- First `Log.*` call, in `[StarMapImmediateLoad]` (`startup:immediate-load`): resolves the folder and rotates the
  file, once per process, before any game state exists.
- Writes come from the main thread (hooks, main-thread patches), occasionally from a vehicle worker, and the flush
  timer runs on a thread-pool thread (`thread:background`); one lock (`Gate`) guards buffer and file.
- `Log.Shutdown()` must be the **last** call in `[StarMapUnload]`, otherwise the final batched lines are lost. It is
  the one part of Unload that still matters at that point ([StarMapUnload plus ProcessExit for exit paths that skip it](../clean-up-on-game-exit/clean-up-on-game-exit-1.md)).

## Steps

1. **Create a static `Log` class** with levels `Debug`, `Info`, `Warn`, `Error`, `Off`; `Threshold` defaults to
   `Debug` under `#if DEBUG`, `Info` otherwise; `Debug(Func<string>)` skips string building below threshold.
2. **Rotate on first use.** Pick the log directory (approach 3 covers discovery), copy any existing `<Mod>.log`
   to `<Mod>.prev.log`, then truncate with a session header:[src/KSAGolf/Ksa/Log.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Log.cs#L202-L229)

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

3. **Batch under a lock; flush on warnings, size or time; keep a timer.** Every line also goes to
   `Console.WriteLine` with a prefix:[src/KSAGolf/Ksa/Log.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Log.cs#L116-L144)

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

   Constants: flush every 100 ms, or at 64 KB pending. Any write exception sets `_fileBroken` and abandons the file
   while stdout keeps working: logging must never throw.
4. **Add a runtime verbose switch.** A release build logs at Info; a checkbox drops the threshold to Debug so a
   player can produce a detailed report without a special build:[src/KSAGolf/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Ui/Ui.cs#L309-L318)

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

5. **Log identity first.** At immediate load write the mod id, mod version, the KSA build it was compiled for and
   the build running, so every report states its context (see [Stamp the targeted build into assembly metadata](../check-the-game-version/check-the-game-version-2.md)):[src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs#L30-L35)

   ```csharp
   // src/KSACars/Ksa/KSACarsMod.cs L30-L35
       [StarMapImmediateLoad]
       public void OnImmediateLoad(Mod mod)
       {
           Log.Info($"loading (mod id: {mod.Id})");
           Log.Info($"KSACars {Build.Version} built for KSA {Build.KsaBuild ?? "?"}, running {Build.KsaRunning ?? "?"}");
       }
   ```

6. **Shut down last.** End `[StarMapUnload]` with `Log.Shutdown()`, which disposes the timer and flushes.
7. **Add a fault counter** that logs each exception with its count and disables the mod after ten; that is
   [Per-hook try/catch feeding a shared fault counter, unpatch at ten](../contain-exceptions-in-starmap-hooks/contain-exceptions-in-starmap-hooks-1.md) and uses this log.

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Ksa/Log.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Log.cs) | The `Log` class (KSAGolf; KSACars and KSArmory carry the same code under their names: [src/KSACars/Ksa/Log.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Log.cs)) |
| [src/KSAGolf/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Ui/Ui.cs) | Verbose toggle |
| [src/KSAGolf/Ksa/KSAGolfMod.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KSAGolfMod.cs) | Calls `Log.Shutdown()` last in `Unload` (L191-L214) |
| [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs) | Identity lines and the fault counter |

## Variations and gotchas

- **The folder is guessed** (approach 3 shows the candidate list); the example uses a capital `Logs` while KSA's own
  folder is lower-case `logs`, so on a case-sensitive file system the first candidate can miss. Reading
  `Constants.LogsFolderPath` removes the guesswork and follows `-InstancePath` ([Constants.LogsFolderPath and a developer marker file](./write-a-per-mod-log-file-2.md)).
- **Static timers outlive unload** unless `Shutdown` runs; `Write` recreates the ticker on the next call so a
  reloaded mod gets it back.
- **Release builds carry no `.pdb`**, so stack traces have no line numbers by design.
- **Do not make the first `Log` call from `StarMapBeforeMain`.** The folder is resolved once, on the first write,
  and only existing folders are accepted; KSA creates its logs folder in the `Program` constructor, so on a first
  launch a `BeforeMain` write skips it and the whole session's file lands in the last candidate,
  `Path.GetTempPath()`.[src/KSAGolf/Ksa/Log.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Log.cs#L236-L268) Use the console there.

## See also

- [Constants.LogsFolderPath and a developer marker file](./write-a-per-mod-log-file-2.md), [Path probing across Documents and XDG locations](./write-a-per-mod-log-file-3.md), [Log from a mod: per-mod log file, game log category and log-once](./overview.md)
- [Mod log file with rotation and a verbose toggle](../../../../mods/ksagolf/features/mod-log-file-with-rotation.md), [Per-mod log file, build stamp and fault limiter](../../../../mods/ksacars/features/mod-log-file.md)
- [Warn when the running game build differs from the one a mod was tested on](../check-the-game-version/overview.md), [Keep mod exceptions out of KSA's frame loop](../contain-exceptions-in-starmap-hooks/overview.md), [How StarMap launches KSA and loads your mod](../../loader/how-starmap-launches-ksa/overview.md)
