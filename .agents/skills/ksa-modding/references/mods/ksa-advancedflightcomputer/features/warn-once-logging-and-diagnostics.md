---
type: Mod Feature
title: Warn-once logging, debug switches and a perf scope
description: Thread-safe once-per-key warnings, throttled diagnostics, compile-time per-feature debug switches and a DEBUG-only stopwatch scope keep a large mod's log readable
mod: ksa-advancedflightcomputer
category: getting-started
techniques: [background-thread]
hooks: []
lifecycle: [frame:draw-ui, frame:apply-solvers, physics:vehicle-solver, thread:background]
game_types: [Brutal.Logging.DefaultCategory]
tags: [logging, diagnostics, brutal-logging, performance, threads]
---

AFC logs from the main thread, the vehicle workers, the Transfer Planner's porkchop worker and its
own solver threads, often from code that runs every frame or every physics substep. A naive
`Log.Warning` in a per-frame catch block floods the log and slows the game. AFC routes all of it
through a small set of helpers in `Core/`.

## How it works

| Helper | Behavior | Typical use |
|---|---|---|
| `LogHelper.WarnOnce(key, message)` | Logs a warning the first time a key is seen per mod load | `catch` blocks on worker threads and per-frame draws, keyed by site plus exception type (`"command-sink-rcs:" + ex.GetType().Name`) |
| `LogHelper.DebugOnce(key, message)` | Same for debug lines, separate key set | Transpiler success lines, because Harmony re-runs a method's transpilers whenever another patch is added to or removed from it |
| `LogHelper.ThrottleAllows(key, seconds)` | True at most once per interval of real time (`Stopwatch`) | Diagnostics whose trigger re-fires every frame |
| `DebugConfig.<Feature>` | Static bools, `true` in DEBUG builds and `false` in Release (`#if DEBUG`) | Gate verbose per-feature logging |
| `PerfTracker.Scope` (DEBUG only) | `using var _ = new PerfTracker.Scope("Name")` records count, total, min and max ticks and reports every 30 s | Measure the plan-window prefix and the guidance step |
| `FormatHelper.FormatDuration` | Seconds to `1h 5m` style with N/A for NaN | UI readouts |

All key sets live behind one lock because `WarnOnce` is reached from the porkchop worker (through
the patched `OrbitalTransfers.AlignmentTime`) as well as from the main thread; the actual log call is
made outside the lock ([AdvancedFlightComputer/Core/LogHelper.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/LogHelper.cs#L6-L66)). Keys include the
vehicle id where a failure is per craft (`guidance-step-{vehicle.Id}:...`) so one bad craft does not
hide another. `LogHelper.Reset()` is the last call in `[StarMapUnload]`, after `UnpatchAll`, so the
transpiler re-runs triggered by unpatching do not repeat their once-only lines. Under StarMap 4dc60f8
that ordering is moot: StarMap's id-less `UnpatchAll()` has already removed every patch before `[StarMapUnload]`, so the
mod's own `UnpatchAll` finds nothing
([feature-gated patch sets](feature-gated-patch-sets.md), [StarMap lifecycle attributes](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-1.md))
([AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L233-L245)).

The logger itself is the game's `Brutal.Logging.DefaultCategory.Log` ([current/decomp/Brutal.Logging/DefaultCategory.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Logging/DefaultCategory.cs)),
with an `[AFC]` prefix on every line so the mod's output can be filtered from the game log.

## Lifecycle and timing

The helpers have no hook of their own; they are called from every lifecycle point AFC uses:
`frame:draw-ui` (planner, settings and guidance windows), `frame:apply-solvers` (the post-solver
tick), `physics:vehicle-solver` (the command sink and the attitude-rate postfix on the vehicle
workers), `thread:background` (the porkchop worker and the guidance solve workers). The lock makes
them safe from any of these threads. `PerfTracker` is not synchronized and is only used on the main
thread.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Core/LogHelper.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/LogHelper.cs) | Once-per-key and throttled logging, thread-safe |
| [AdvancedFlightComputer/Core/DebugConfig.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/DebugConfig.cs) | Per-feature debug switches by build configuration |
| [AdvancedFlightComputer/Core/PerfTracker.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/PerfTracker.cs) | DEBUG-only stopwatch scope and periodic report |
| [AdvancedFlightComputer/Core/FormatHelper.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/FormatHelper.cs) | Duration formatting for readouts |
| [AdvancedFlightComputer/Core/VehicleCommandSink.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/VehicleCommandSink.cs#L42-L77) | Example: worker-thread writers wrapped in try/catch with `WarnOnce` |

## Snippets

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

## Game APIs touched

| Member | Use |
|---|---|
| `Brutal.Logging.DefaultCategory.Log` ([current/decomp/Brutal.Logging/DefaultCategory.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Logging/DefaultCategory.cs)) | The game's logger (`Info`, `Warning`, `Error`, `Debug`) |

## Pitfalls and version notes

- Never let an exception escape a patch that runs on a vehicle worker: it ends the worker's step for
  the whole physics bubble. Catch, `WarnOnce`, continue.
- Key by exception type, not message: messages often contain changing numbers and defeat the dedup.
- Once-per-load sets grow with distinct keys; keep keys bounded (site + type + vehicle id).

## See also

- [Feature-gated patch sets](feature-gated-patch-sets.md)
- [Flight computer command sink](flight-computer-command-sink.md)
- [Guidance vehicle step loop](guidance-vehicle-step-loop.md)
- [Log each problem once with a thread-safe warn-once set reset on unload](../../../howto/getting-started/lifecycle/write-a-per-mod-log-file/write-a-per-mod-log-file-6.md) - how-to article demonstrating this feature
- [Time hot code with a DEBUG-only readonly ref struct scope and a periodic report](../../../howto/testing-and-tooling/in-game-testing/measure-and-budget-mod-performance/measure-and-budget-mod-performance-1.md) - how-to article demonstrating this feature

