---
type: How-To
title: Time hot code with a DEBUG-only readonly ref struct scope and a periodic report
description: Add a using-scope that stamps a timestamp only when a debug flag is on, accumulates average, minimum and maximum per key and logs them every few seconds, compiled out of release builds
category: testing-and-tooling
group: in-game-testing
topic: measure-and-budget-mod-performance
approach: 1
difficulty: beginner
prerequisites: [starmap]
demonstrated_by:
  - mods/ksa-deltavmap/features/in-game-validation-dumps-and-perf
  - mods/ksa-measuretools/features/resilient-patching-and-diagnostics
  - mods/ksa-advancedflightcomputer/features/warn-once-logging-and-diagnostics
techniques: [logging, msbuild]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Program]
tags: [performance, stopwatch, debug-build, ref-struct, periodic-report]
---

Outcome: `using var perf = new PerfTracker.Scope("Mod.Draw.Canvas");` around any block gives, every five seconds, a log line `[DvMap] Perf (5.0s): DvMap.Draw.Canvas avg=0.412ms min=0.380ms max=1.903ms (297 calls)`, with no cost in a release build. The same three-part pattern (a scope type, a keyed accumulator, a time-based report) is used by KSA-DeltaVMap, KSA-MeasureTools and KSA-AdvancedFlightComputer.
Prerequisites: a StarMap mod with a main-thread hook.

## Background: how the game does it

Nothing from the game is needed except a place to put the scopes and a log. The mod's main-thread frame hook is `StarMapAfterGui` (`frame:draw-ui`, inside `if (DrawUI)`, [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2265-L2269)); the log is `Brutal.Logging.DefaultCategory.Log` ([current/decomp/Brutal.Logging/DefaultCategory.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Logging/DefaultCategory.cs)).
The game's own profiler already wraps its phases with `Profiler.MainThread.Begin(...)` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2243)); a mod scope is a cheaper way to see only its own code.

## Lifecycle and timing

Scopes run wherever the measured code runs. The tracker is not thread-safe: KSA-DeltaVMap and KSA-MeasureTools call it only from the main thread, and the report is emitted from `Record`, so it appears on the first scope that completes after the interval elapses. See the [frame order](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).
The report is logged at debug level, so check the log threshold you run with.

## Steps

1. **A runtime flag that is constant false in release**, so release builds pay one boolean (see [bridge article 5](../drive-a-running-game-through-a-file-bridge/drive-a-running-game-through-a-file-bridge-5.md) for the flag):

   ```csharp
   // DeltaVMap/Core/DebugConfig.cs L1-L13
   namespace DeltaVMap.Core;

   internal static class DebugConfig
   {
   #if DEBUG
       public static bool Performance = true;
       public static bool ValidationDump = true;
   #else
       public static bool Performance = false;
       public static bool ValidationDump = false;
   #endif

       public static bool Any => Performance || ValidationDump;
   ```

2. **The scope: a `readonly ref struct`.** It cannot be boxed or captured, allocates nothing, and stamps the timestamp only when the flag is on (else -1, which `Dispose` treats as "off"):

   ```csharp
   // DeltaVMap/Core/PerfTracker.cs L24-L40
       internal readonly ref struct Scope
       {
           private readonly string _name;
           private readonly long _start;

           public Scope(string name)
           {
               _name = name;
               _start = DebugConfig.Performance ? Stopwatch.GetTimestamp() : -1;
           }

           public void Dispose()
           {
               if (_start < 0) return;
               Record(_name, Stopwatch.GetTimestamp() - _start);
           }
       }
   ```

3. **Accumulate per key, report on an interval, then clear.** `MaybeReport` runs from `Record`, logs average, minimum, maximum and count per key in first-seen order, and resets, so the report is a window:

   ```csharp
   // DeltaVMap/Core/PerfTracker.cs L42-L93
       public static void Record(string name, long elapsedTicks)
       {
           if (_entries.TryGetValue(name, out var data))
           {
               data.Count++;
               data.TotalTicks += elapsedTicks;
               if (elapsedTicks < data.MinTicks) data.MinTicks = elapsedTicks;
               if (elapsedTicks > data.MaxTicks) data.MaxTicks = elapsedTicks;
               _entries[name] = data;
           }
           else
           {
               _entries[name] = new PerfData
               {
                   Count = 1,
                   TotalTicks = elapsedTicks,
                   MinTicks = elapsedTicks,
                   MaxTicks = elapsedTicks
               };
               _orderedKeys.Add(name);
           }

           MaybeReport();
       }

       private static void MaybeReport()
       {
           long now = Stopwatch.GetTimestamp();
           double elapsed = (now - _lastReportTimestamp) / (double)Stopwatch.Frequency;
           if (elapsed < ReportIntervalSeconds)
               return;

           _lastReportTimestamp = now;

           foreach (string key in _orderedKeys)
           {
               if (!_entries.TryGetValue(key, out var data) || data.Count == 0)
                   continue;

               double avgMs = TicksToMs(data.TotalTicks / data.Count);
               double minMs = TicksToMs(data.MinTicks);
               double maxMs = TicksToMs(data.MaxTicks);

               DefaultCategory.Log.Debug(string.Format(
                   CultureInfo.InvariantCulture,
                   "[DvMap] Perf ({0:F1}s): {1} avg={2:F3}ms min={3:F3}ms max={4:F3}ms ({5} calls)",
                   elapsed, key, avgMs, minMs, maxMs, data.Count));
           }

           _entries.Clear();
           _orderedKeys.Clear();
       }
   ```

4. **Wrap the hot paths, inside `#if DEBUG` so the type need not exist in release:**

   ```csharp
   // DeltaVMap/Render/MapWindow.cs L1100-L1102
   #if DEBUG
           using var perf = new PerfTracker.Scope("DvMap.Draw.Canvas");
   #endif
   ```

   The whole `PerfTracker` file is inside `#if DEBUG` too ([DeltaVMap/Core/PerfTracker.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Core/PerfTracker.cs#L1)), so a stray call without the guard is a compile error in release.

5. **Reset on unload** so a reload starts a fresh window (`PerfTracker.Reset()`, [DeltaVMap/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Mod.cs#L85-L89)).

6. **Variants.** KSA-MeasureTools' tracker is the same shape with a 5 s report ([MeasureTools/Core/PerfTracker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Core/PerfTracker.cs#L8-L47)); KSA-AdvancedFlightComputer reports every 30 s and adds `LogHelper.ThrottleAllows(key, seconds)` for diagnostics that re-fire every frame, and `WarnOnce`/`DebugOnce` keyed by site plus exception type for catch blocks in per-frame code ([AdvancedFlightComputer/Core/LogHelper.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/LogHelper.cs#L13-L33)).

7. **Run and read.** Build Debug, run the scenario, and look for `Perf (5.0s)` lines in the game log. Sort by avg times calls to find the dear scope.

## Complete example

| File | Role |
|---|---|
| [DeltaVMap/Core/PerfTracker.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Core/PerfTracker.cs) | Scope, accumulator, report |
| [DeltaVMap/Core/DebugConfig.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Core/DebugConfig.cs) | Flags |
| [MeasureTools/Core/PerfTracker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Core/PerfTracker.cs) | The same, in another mod |
| [AdvancedFlightComputer/Core/LogHelper.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/LogHelper.cs) | Once-per-key and throttled logging, thread-safe |

## Variations and gotchas

- **Debug builds only.** A tester on a release build cannot get these numbers; use [article 2](measure-and-budget-mod-performance-2.md) for always-on counters.
- **Not thread-safe.** Do not put scopes on worker threads without a lock; AFC keeps its log helpers behind one lock for that reason but uses `PerfTracker` on the main thread only.
- **Scopes measure CPU time on the calling thread.** A GPU dispatch is asynchronous; see [article 4](measure-and-budget-mod-performance-4.md).
- **Allocation.** The scope allocates nothing; the report builds strings, but only every few seconds.

## See also

- [Overview](overview.md), [article 2](measure-and-budget-mod-performance-2.md), [article 3](measure-and-budget-mod-performance-3.md)
- [Write a per-mod log file](../../../getting-started/lifecycle/write-a-per-mod-log-file/overview.md)
- [KSA-DeltaVMap validation dumps and perf](../../../../mods/ksa-deltavmap/features/in-game-validation-dumps-and-perf.md), [KSA-AdvancedFlightComputer warn-once logging](../../../../mods/ksa-advancedflightcomputer/features/warn-once-logging-and-diagnostics.md), [KSA-MeasureTools resilient patching and diagnostics](../../../../mods/ksa-measuretools/features/resilient-patching-and-diagnostics.md)
