---
type: Mod Feature
title: Logging shim and perf stats
description: A game-free logging interface that every library uses, swapped at load for a sink on KSA's Brutal LogCategory, plus allocation-free single-writer timing and allocation counters shown live in the mod's status window
mod: gatos
category: getting-started
techniques: [starmap-lifecycle, imgui-window]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapAfterGui]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [Brutal.Logging.LogCategory, Brutal.Logging.LogSystem]
tags: [logging, brutal, performance, profiling, diagnostics, zero-alloc]
---

Ten of gatOS's eleven projects must build and test without the game, yet their log lines belong in
the game's log when running in KSA. And a mod that runs code every frame needs to measure what that
costs without perturbing it. gatOS solves both with two tiny game-free types.

## How it works

**`ModLog`** ([gatOS.Logging/ModLog.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Logging/ModLog.cs#L12-L45)): an `IModLogger` interface (`Debug`, `Info`,
`Warn`, `Error(message, ex)`) and a static `ModLog.Log` that defaults to a console logger. Libraries
call `ModLog.Log.Info(...)` and never reference a game assembly.

**`BrutalModLogger`** ([gatOS.GameMod/Game/BrutalModLogger.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/BrutalModLogger.cs#L12-L51)): the game-side sink,
installed by `ModLog.SetLogger(new BrutalModLogger())` as the first step of `OnFullyLoaded`.
Its constructor throws if `Brutal.Logging.LogSystem.IsEnabled` is false (calls would silently no-op),
so the caller stays on the console logger; otherwise it creates `LogCategory.Make("gatOS")` and maps
levels to `Debug`/`Info`/`Warning`/`Error`, falling back to `Console.WriteLine` if a call throws.
The partial method that installs it is `NoInlining` and wrapped in try/catch, so a missing game
assembly fails at that call site rather than when the caller is JIT-compiled.

**`PerfStat`** ([gatOS.Logging/PerfStat.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Logging/PerfStat.cs#L21-L44)): single-writer, many-reader accumulator of
count, sum, max and last in `Stopwatch` ticks. `using (stat.Measure())` returns a struct scope
(two timestamp reads, no allocation); `Volatile.Write` publishes with count written last so a reader
never sees a count without its sum; conversion to microseconds happens only when the UI reads it.
`ValueStat` records arbitrary values the same way.

**What is measured**: telemetry sample time, telemetry bytes allocated per sample (a
`GC.GetAllocatedBytesForCurrentThread()` delta, the "Sample alloc" regression tripwire), Frame and
Solver command drains, MQTT publish, IVA physics, display capture and encode, schedule ticks. The
status window shows avg/max/last with a Reset button.

## Lifecycle and timing

- Logger swap: `[StarMapAllModsLoaded]`, before anything else logs. `LogSystem` is initialized by
  then in practice; the constructor guard covers the case where it is not.
- Stats are written by whichever single thread owns the measured path (game thread for the sampler
  and drains, pump tasks for MQTT) and read by the ImGui status window in `[StarMapAfterGui]`.

## Key code

| Path | Role |
|---|---|
| [gatOS.Logging/ModLog.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Logging/ModLog.cs) | logging interface and swappable static sink |
| [gatOS.GameMod/Game/BrutalModLogger.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/BrutalModLogger.cs) | Brutal `LogCategory` sink with console fallback |
| [gatOS.Logging/PerfStat.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Logging/PerfStat.cs) | allocation-free timing accumulator |
| [gatOS.Logging/ValueStat.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Logging/ValueStat.cs) | allocation-free value accumulator |
| [gatOS.GameMod/Game/TelemetrySampler.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/TelemetrySampler.cs#L157-L165) | sample time and allocation tripwire |
| [gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L961-L971) | guarded logger install |

## Snippets

```csharp
// gatOS.GameMod/Game/BrutalModLogger.cs L21-L38
    public BrutalModLogger()
    {
        if (!LogSystem.IsEnabled)
            throw new InvalidOperationException("the game log system is not initialized");
        _log = LogCategory.Make("gatOS");
    }

    public void Debug(string message) => Try(() => _log.Debug(message), "DBG", message);

    public void Info(string message) => Try(() => _log.Info(message), "INF", message);

    public void Warn(string message) => Try(() => _log.Warning(message), "WRN", message);

    public void Error(string message, Exception? ex = null)
    {
        var full = ex is null ? message : $"{message}{Environment.NewLine}{ex}";
        Try(() => _log.Error(full), "ERR", full);
    }
```

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

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `LogCategory.Make(string)` | [current/decomp/Brutal.Logging/LogCategory.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Logging/LogCategory.cs#L44-L44) | create the mod's log category |
| `LogCategory.Debug/Info/Warning/Error` | [current/decomp/Brutal.Logging/LogCategory.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Logging/LogCategory.cs#L110-L135) | log levels |
| `LogSystem.IsEnabled` | [current/decomp/Brutal.Logging/LogSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Logging/LogSystem.cs#L11-L11) | detect an uninitialized log system |

## Pitfalls and version notes

- Brutal's log methods take interpolated-string handlers with caller-info parameters; passing a
  prebuilt string works but formats eagerly.
- `GC.GetAllocatedBytesForCurrentThread()` measures only the calling thread; it is the right tool
  for a main-thread hot path, not for thread-pool work.
- Keep stats single-writer; two writers would need interlocked updates.

## See also

- [Compile-gated game seam](compile-gated-game-seam.md)
- [ModMenu entry with a fallback menu](modmenu-entry-with-fallback-menu.md)
- [Telemetry sampler and snapshot store](telemetry-sampler-snapshot.md)
- [gatOS overview](../overview.md)
- [Keep library code game-free with a logging interface swapped for a Brutal sink at load](../../../howto/getting-started/lifecycle/write-a-per-mod-log-file/write-a-per-mod-log-file-5.md) - how-to article demonstrating this feature
- [Always-on single-writer counters shown in a status window](../../../howto/testing-and-tooling/in-game-testing/measure-and-budget-mod-performance/measure-and-budget-mod-performance-2.md) - how-to article demonstrating this feature

