---
type: How-To
title: Log each problem once with a thread-safe warn-once set reset on unload
description: Keep per-frame and per-substep failures from flooding the log with keyed WarnOnce/DebugOnce sets behind one lock, a time throttle, and compile-time debug switches
category: getting-started
group: lifecycle
topic: write-a-per-mod-log-file
approach: 6
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/warn-once-logging-and-diagnostics]
techniques: [logging, starmap-lifecycle, background-thread]
hooks: [starmap StarMapUnload]
lifecycle: [frame:draw-ui, frame:apply-solvers, physics:vehicle-solver, thread:background, event:shutdown]
game_types: [Brutal.Logging.LogCategory]
tags: [logging, warn-once, throttle, deduplication, threads, debug-switches]
---

A `Log.Warning` in a per-frame `catch`, or in a patch that runs on every physics substep, floods the log and slows
the game. KSA-AdvancedFlightComputer routes all such messages through a tiny `LogHelper`: a keyed once-set, a
throttle by real time, and compile-time debug switches. The helper is safe from the main thread, the vehicle
workers, the porkchop worker and its own solver threads. A single-threaded variant that also keys on the
exception type is [HashSet-keyed WarnOnce/ErrorOnce with the exception type](./write-a-per-mod-log-file-7.md).

## Background: how the game does it

The logger is the game's `Brutal.Logging.DefaultCategory.Log` (a public static `LogCategory`), with an `[AFC]`
prefix on each line so the mod's output can be filtered from the game log.[current/decomp/Brutal.Logging/DefaultCategory.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Logging/DefaultCategory.cs) Nothing in the
game de-duplicates: each `Log.Warning` call writes a line. The problem is the *callers*: code on `frame:draw-ui`,
`frame:apply-solvers` (the post-solver tick), `physics:vehicle-solver` (a patch on the vehicle worker) and
`thread:background` (the porkchop and guidance workers) can each fail once per frame or once per substep.

## Lifecycle and timing

The helpers have no hook of their own; they are called from every lifecycle point AFC uses. One lock makes them
safe from any of those threads. `PerfTracker` (DEBUG-only) is not synchronized and is only used on the main thread.
`LogHelper.Reset()` is called in `[StarMapUnload]` ([StarMapUnload plus ProcessExit for exit paths that skip it](../clean-up-on-game-exit/clean-up-on-game-exit-1.md)). Because Harmony re-runs every
transpiler on a method whenever another patch is applied to or removed from it, patch-time success lines must be
once-only too (hence `DebugOnce`).

## Steps

1. **Create the helper** with separate key sets for warnings and debug lines and a lock, so the log call itself
   happens outside the lock:[AdvancedFlightComputer/Core/LogHelper.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/LogHelper.cs#L13-L33)

   ```csharp
   // AdvancedFlightComputer/Core/LogHelper.cs L13-L33
   internal static class LogHelper
   {
       private static readonly HashSet<string> _warned = new();
       private static readonly HashSet<string> _debugged = new();
       private static readonly Dictionary<string, long> _throttleLastTimestamp = new();
       private static readonly object _gate = new();

       public static void WarnOnce(string key, string message)
       {
           if (FirstTime(_warned, key))
               DefaultCategory.Log.Warning(message);
       }

       /// <summary>For patch-time diagnostics. Harmony re-runs every transpiler on a method whenever
       /// another patch is applied to or removed from it, so an ungated success line would repeat
       /// per re-run.</summary>
       public static void DebugOnce(string key, string message)
       {
           if (FirstTime(_debugged, key))
               DefaultCategory.Log.Debug(message);
       }
   ```

2. **Add the throttle and the set primitive:**[AdvancedFlightComputer/Core/LogHelper.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/LogHelper.cs#L38-L66)

   ```csharp
   // AdvancedFlightComputer/Core/LogHelper.cs L38-L49 (ThrottleAllows)
       public static bool ThrottleAllows(string key, double minIntervalSec)
       {
           long now = Stopwatch.GetTimestamp();
           lock (_gate)
           {
               if (_throttleLastTimestamp.TryGetValue(key, out long last)
                   && now - last < minIntervalSec * Stopwatch.Frequency)
                   return false;
               _throttleLastTimestamp[key] = now;
               return true;
           }
       }
   ```
   plus `FirstTime(set, key)` = `lock (_gate) return seen.Add(key);` and `Reset()` clearing all three collections.
3. **Key by site plus exception type**, and by vehicle id where a failure is per craft, so one bad craft does not
   hide another: for example `"command-sink-rcs:" + ex.GetType().Name`, `"guidance-step-{vehicle.Id}:..."`.
   Key by type, not message: messages often contain changing numbers and defeat the dedup.
4. **Use the throttle for diagnostics whose trigger re-fires every frame** (`ThrottleAllows(key, seconds)`).
5. **Gate verbose per-feature logging with compile-time switches.** `DebugConfig.<Feature>` are static bools, `true`
   under `#if DEBUG`, `false` in Release, so a release build pays nothing. `PerfTracker.Scope` (DEBUG only)
   records count, total, min and max ticks and reports every 30 s.
6. **Reset on unload, after unpatching.** `LogHelper.Reset()` is the last helper cleared in `Unload`:[AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L242-L250)

   ```csharp
   // AdvancedFlightComputer/Mod.cs L242-L250
           ModSettingsPage.Reset();
           SaveLoadObserver.Reset();
           Patch_SetTransferInfo.Reset();
           LogHelper.Reset();
   #if DEBUG
           PerfTracker.Reset();
   #endif

           DefaultCategory.Log.Info("[AFC] Unloaded.");
   ```

   The source comment: "LogHelper stays after UnpatchAll, or the transpiler re-runs that unpatching triggers print
   their once-only lines again." Under StarMap 4dc60f8 the ordering is moot (the id-less `UnpatchAll()` already ran),
   but it is correct for an in-process reload.

## Complete example

[AdvancedFlightComputer/Core/LogHelper.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/LogHelper.cs) (the helper), [AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs) (the unload ordering). Never let an exception escape a patch that runs on
a vehicle worker: no hook wrapper sees it there and the decompiled `PhysicsBubble` job code has no handler of its own (what the
job system then does with it is not verified here). Catch, `WarnOnce`, continue.

## Variations and gotchas

- **Once-per-load sets grow with distinct keys.** Keep keys bounded (site + type + vehicle id); a free-form message
  as key is a leak.
- **Thread-safety costs a lock**; the single-threaded variant ([HashSet-keyed WarnOnce/ErrorOnce with the exception type](./write-a-per-mod-log-file-7.md)) skips it but must stay
  on the main thread.
- **Separate key spaces** for warnings and debug so a debug line never silences a warning with the same key.
- **A reload resets the sets**, so a problem logs once per mod load, not once per session.
- **Combine with fault budgets.** Dedup hides repetition; a budget stops it ([Keep mod exceptions out of KSA's frame loop](../contain-exceptions-in-starmap-hooks/overview.md)).

## See also

- [HashSet-keyed WarnOnce/ErrorOnce with the exception type](./write-a-per-mod-log-file-7.md), [One LogCategory in a tiny shared assembly](./write-a-per-mod-log-file-4.md), [Log from a mod: per-mod log file, game log category and log-once](./overview.md)
- [Warn-once logging, debug switches and a perf scope](../../../../mods/ksa-advancedflightcomputer/features/warn-once-logging-and-diagnostics.md)
- [Clean up on game exit and relaunch the game from a mod](../clean-up-on-game-exit/overview.md), [Game loop and mod lifecycle](../game-loop-and-lifecycle/overview.md)
