---
type: How-To
title: Always-on single-writer counters shown in a status window
description: Record timing and allocation with an allocation-free accumulator written by one thread and read by the UI, live in any build
category: testing-and-tooling
group: in-game-testing
topic: measure-and-budget-mod-performance
approach: 2
difficulty: intermediate
prerequisites: [starmap, imgui]
demonstrated_by: [mods/gatos/features/logging-and-perfstats]
techniques: [imgui-window, logging]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, startup:all-mods-loaded]
game_types: [KSA.Program]
tags: [performance, counters, allocation, status-window, volatile]
---

Outcome: a "Sample: avg 0.412 ms, max 1.9 ms, last 0.4 ms" and "Sample alloc: avg 3.2 KiB" readout in a status window, plus a Reset button, available in every build with no allocation on the hot path. Demonstrated by gatOS ([logging and perf stats](../../../../mods/gatos/features/logging-and-perfstats.md)).
Choose it over [article 1](measure-and-budget-mod-performance-1.md) when testers run release builds or the measured path runs on another thread than the one that draws the window. Prerequisites: StarMap and an ImGui window.

## Background: how the game does it

Only the hook matters: the status window is drawn in `[StarMapAfterGui]` (`frame:draw-ui`, [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2265-L2269)) while the measured code may run on the game thread (a per-frame sampler, a command drain) or on a task of the mod's own (an MQTT publish pump). The runtime offers `Stopwatch.GetTimestamp` (a QueryPerformanceCounter read) and
`GC.GetAllocatedBytesForCurrentThread()`, which counts only the calling thread.

## Lifecycle and timing

| What | Where |
|---|---|
| Measured paths | The game thread inside the mod's frame work, or one pump task each |
| Writer | Exactly one thread per stat; no interlock |
| Reader | The status window in `StarMapAfterGui` (`frame:draw-ui`), any thread-safe read |
| Logger swap | `[StarMapAllModsLoaded]` (`startup:all-mods-loaded`), before anything else logs |

## Steps

1. **A fixed-size accumulator of count, sum, max and last in raw timestamp ticks.** One writer, many readers; `Volatile.Write` publishes the fields and writes the count last so a reader that sees a new count also sees the new sum. Convert to microseconds only on read:

   ```csharp
   // gatOS.Logging/PerfStat.cs L21-L79
   public sealed class PerfStat
   {
       private static readonly double TicksToMicros = 1_000_000.0 / Stopwatch.Frequency;

       private long _count;
       private long _sumTicks;
       private long _maxTicks;
       private long _lastTicks;

       /// <summary>Starts a scope that records its lifetime into this stat on dispose (alloc-free struct).</summary>
       public Scope Measure() => new(this, Stopwatch.GetTimestamp());

       /// <summary>Records one interval, measured in <see cref="Stopwatch.GetTimestamp"/> ticks.</summary>
       public void Add(long elapsedTicks)
       {
           if (elapsedTicks < 0)
               elapsedTicks = 0; // a clock hiccup must never poison the running sum
           Volatile.Write(ref _sumTicks, _sumTicks + elapsedTicks);
           if (elapsedTicks > _maxTicks)
               Volatile.Write(ref _maxTicks, elapsedTicks);
           Volatile.Write(ref _lastTicks, elapsedTicks);
           // Count is written last (release): a reader that sees the new count also sees the new sum.
           Volatile.Write(ref _count, _count + 1);
       }

       /// <summary>Number of intervals recorded since the last <see cref="Reset"/>.</summary>
       public long Count => Volatile.Read(ref _count);

       /// <summary>Mean interval since the last <see cref="Reset"/>, in microseconds (0 when none).</summary>
       public double AvgMicros
       {
           get
           {
               var count = Volatile.Read(ref _count);
               return count == 0 ? 0 : Volatile.Read(ref _sumTicks) * TicksToMicros / count;
           }
       }

       /// <summary>The most recent interval, in microseconds.</summary>
       public double LastMicros => Volatile.Read(ref _lastTicks) * TicksToMicros;

       /// <summary>The worst interval since the last <see cref="Reset"/>, in microseconds.</summary>
       public double MaxMicros => Volatile.Read(ref _maxTicks) * TicksToMicros;

       /// <summary>Clears all figures (e.g. after the user retunes the cadence and wants fresh numbers).</summary>
       public void Reset()
       {
           Volatile.Write(ref _sumTicks, 0);
           Volatile.Write(ref _maxTicks, 0);
           Volatile.Write(ref _lastTicks, 0);
           Volatile.Write(ref _count, 0);
       }

       /// <summary>A <c>using</c>-scoped timer that records its lifetime into the owning <see cref="PerfStat"/>.</summary>
       public readonly struct Scope(PerfStat stat, long startTimestamp) : IDisposable
       {
           /// <inheritdoc />
           public void Dispose() => stat.Add(Stopwatch.GetTimestamp() - startTimestamp);
       }
   ```

2. **Measure with a scope or a delta.** `using (_sampleStats.Measure())` is two timestamp reads. The alloc tripwire reads the thread-local allocated-bytes counter before and after and records the delta in a value accumulator:

   ```csharp
   // gatOS.GameMod/Game/TelemetrySampler.cs L157-L165
           if (_clock.Tick(dt))
               using (_sampleStats.Measure()) // two timestamp reads; alloc-free
               {
                   // Alloc/tick tripwire (GP3): a thread-local counter read before/after — alloc-free
                   // to record, and the number the status window shows as "sample alloc".
                   var allocBefore = GC.GetAllocatedBytesForCurrentThread();
                   Sample();
                   _allocStats.Add(GC.GetAllocatedBytesForCurrentThread() - allocBefore);
               }
   ```

3. **Show it.** Read the figures in the window, guard the empty case, and put what the number means in a tooltip:

   ```csharp
   // gatOS.GameMod/Game/Mod.Game.cs L1214-L1235
       private void DrawPerfStats()
       {
           ImGui.Spacing();
           var s = _sampleStats;
           if (s.Count == 0)
           {
               ImGui.TextDisabled("Sample timing: no samples yet (start the VM or connect a client)");
           }
           else
           {
               ImGui.Text($"Sample: avg {s.AvgMicros / 1000:F3} ms, max {s.MaxMicros / 1000:F3} ms, "
                          + $"last {s.LastMicros / 1000:F3} ms");
               if (ImGui.IsItemHovered())
                   ImGui.SetTooltip("Game-thread cost of one telemetry sample (all vessels + the enabled "
                                    + "streams). Lower the rate or turn off vessel detail / bodies to reduce it.");
               var a = _sampleAllocStats;
               ImGui.Text($"Sample alloc: avg {a.Avg / 1024:F1} KiB, max {a.Max / 1024.0:F1} KiB, "
                          + $"last {a.Last / 1024.0:F1} KiB");
               if (ImGui.IsItemHovered())
                   ImGui.SetTooltip("Bytes allocated on the game thread by one telemetry sample — the GP3 "
                                    + "regression tripwire (GREENFIELD_PERFORMANCE_IMPROVEMENT_PLANS.md). "
                                    + "Scales with vessels/parts; spikes on vehicle edits are expected.");
   ```

4. **Reset on demand** (the window's Reset button) after the user retunes the cadence and wants fresh numbers.

5. **Swap the logger first.** The mod's libraries log through a swappable static sink so the game-free assemblies never reference the game; `ModLog.SetLogger(new BrutalModLogger())` is the first thing `OnFullyLoaded` does, and the constructor throws if `LogSystem.IsEnabled` is false so the console logger stays in use ([gatOS.GameMod/Game/BrutalModLogger.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/BrutalModLogger.cs#L21-L38)).

6. **Check.** Open the status window with the feature running; expect non-zero counts, an average well under a millisecond for a light path, and an allocation figure that is stable between samples (a creeping figure is a leak or per-call allocation).

## Complete example

| File | Role |
|---|---|
| [gatOS.Logging/PerfStat.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Logging/PerfStat.cs) | Timing accumulator and scope |
| [gatOS.Logging/ValueStat.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Logging/ValueStat.cs) | The same for arbitrary values (allocation bytes) |
| [gatOS.GameMod/Game/TelemetrySampler.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/TelemetrySampler.cs) | Measured sample path |
| [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs) | `DrawPerfStats` window code |

## Variations and gotchas

- **Single writer only.** Two writers need interlocked updates, or the sum loses increments.
- **One-sample staleness.** A reader may see a sum from one sample before the count; irrelevant for a diagnostic average.
- **`GetAllocatedBytesForCurrentThread` is per thread.** Correct for a game-thread hot path, wrong for thread-pool work.
- **Averages hide spikes.** `max` since the last reset is the cheap spike detector; for the worst frame per interval with a per-span breakdown see [article 3](measure-and-budget-mod-performance-3.md).

## See also

- [Overview](overview.md), [article 1](measure-and-budget-mod-performance-1.md), [article 3](measure-and-budget-mod-performance-3.md)
- [Build a settings window](../../../ui/windows/build-a-settings-window/overview.md)
- [gatOS logging and perf stats](../../../../mods/gatos/features/logging-and-perfstats.md)
