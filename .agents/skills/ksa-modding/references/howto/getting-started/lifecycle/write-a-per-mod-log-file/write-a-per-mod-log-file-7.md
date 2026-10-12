---
type: How-To
title: Log with HashSet-keyed WarnOnce and ErrorOnce that include the exception type
description: Deduplicate per-frame failures with separate warning and error key spaces keyed by site and exception type, and log only the first failure of a streak
category: getting-started
group: lifecycle
topic: write-a-per-mod-log-file
approach: 7
difficulty: beginner
prerequisites: [starmap]
demonstrated_by:
  - mods/ksa-measuretools/features/resilient-patching-and-diagnostics
  - mods/ksa-public-telemetry/features/error-budget-self-disable
techniques: [logging, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, starmap StarMapUnload]
lifecycle: [startup:all-mods-loaded, frame:draw-ui, frame:after-on-frame, event:shutdown]
game_types: [Brutal.Logging.LogCategory]
tags: [logging, warn-once, error-once, deduplication, exception-type, first-of-streak]
---

The smallest useful dedup helper is two `HashSet<string>` fields. KSA-MeasureTools has exactly that: `WarnOnce` and
`ErrorOnce` with separate key spaces, keyed by site plus the exception **type**, so a new kind of failure in the same
hook still logs once while the thousand repeats of the old one do not. KSA-Public-Telemetry adds the
"first failure of a streak" variant. Use this when all calls are on the main thread; for worker-thread callers use
the locked helper of [Thread-safe warn-once set reset on unload](./write-a-per-mod-log-file-6.md).

## Background: how the game does it

The sink is `DefaultCategory.Log`.[current/decomp/Brutal.Logging/DefaultCategory.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Logging/DefaultCategory.cs) The game's frame loop has no handler, so a per-frame
`catch` in a mod hook runs every frame once something is broken (see [Keep mod exceptions out of KSA's frame loop](../contain-exceptions-in-starmap-hooks/overview.md)). Without
dedup, a broken draw call writes a stack trace sixty times a second.

## Lifecycle and timing

The helper is main-thread only ("Not thread-safe, call from main thread only", in its own comment). MeasureTools
calls it from `[StarMapAllModsLoaded]` (patch application failures, reflection drift) and from the
`[StarMapAfterGui]` hook (`frame:draw-ui`), and clears it in `[StarMapUnload]` (`event:shutdown`).[MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs#L19-L100)
Telemetry's counter sits in `StarMapAfterOnFrame` and `StarMapAfterGui`.

## Steps

1. **Add the helper** with two key spaces so a `WarnOnce(k)` never silences a later `ErrorOnce(k)`:[MeasureTools/Core/LogHelper.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Core/LogHelper.cs)

   ```csharp
   // MeasureTools/Core/LogHelper.cs L5-L30
   // Log deduplication. Separate keyspaces so a WarnOnce(k) never silences
   // a later ErrorOnce(k). Not thread-safe, call from main thread only.
   internal static class LogHelper
   {
       private static readonly HashSet<string> _loggedWarnings = new();
       private static readonly HashSet<string> _loggedErrors = new();

       public static void WarnOnce(string key, string message)
       {
           if (_loggedWarnings.Add(key))
               DefaultCategory.Log.Warning(message);
       }

       public static void ErrorOnce(string key, string message)
       {
           if (_loggedErrors.Add(key))
               DefaultCategory.Log.Error(message);
       }

       public static void Reset()
       {
           _loggedWarnings.Clear();
           _loggedErrors.Clear();
       }
   }
   ```

2. **Key per-frame catches by exception type**, so each distinct exception type logs one stack per session:[MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs#L73-L77)

   ```csharp
   // MeasureTools/Mod.cs L73-L77
           catch (Exception ex)
           {
               // Per-frame path: first throw of each type logs a stack, then quiet.
               LogHelper.ErrorOnce("aftergui-" + ex.GetType().Name, $"[MeasureTools] Per-frame draw failed: {ex}");
           }
   ```

3. **Use stable keys for one-off diagnostics** (patch application, reflection drift): `"patch-" + patchClass.Name`,
   `"reflect-mouse-latch"`. The message states the player-visible consequence ("a cancelled right-click can leave
   the stock burn menu armed").
4. **Clear the sets on unload** (`LogHelper.Reset()`), so a reload in the same process logs again.
5. **For failures that recur on every frame until fixed, log the first of each streak.** Telemetry counts
   consecutive failures; only the first prints the full exception and success resets the streak:[TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L204-L236)

   ```csharp
   // TelemetryOverlayMod.cs L212-L217
           _consecutiveFailures++;

           if (_consecutiveFailures == 1)
           {
               Console.WriteLine(LogPrefix + $"error while {stage}: {ex}");
           }
   ```

   (the full budget is in [Consecutive-failure budget that restores the stock HUD](../contain-exceptions-in-starmap-hooks/contain-exceptions-in-starmap-hooks-2.md)).

## Complete example

[MeasureTools/Core/LogHelper.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Core/LogHelper.cs) and the call sites in [MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs); the streak variant in [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs). MeasureTools also has
debug-only `PerfTracker` scopes (`using var _ = new PerfTracker.Scope("Name")` under `#if DEBUG`) that log average,
min, max and call count every five seconds.

## Variations and gotchas

- **Key by type, not message.** Messages often contain numbers and defeat the set.
- **A new exception type still logs.** That is intended: the key `"aftergui-" + type name` separates failure modes.
- **Unbounded key growth** is possible if keys include ids; bound them.
- **Not thread-safe**: from a worker, use [Thread-safe warn-once set reset on unload](./write-a-per-mod-log-file-6.md).

## See also

- [Thread-safe warn-once set reset on unload](./write-a-per-mod-log-file-6.md), [Rotation to .prev.log, timer flush, runtime debug toggle](./write-a-per-mod-log-file-1.md), [Log from a mod: per-mod log file, game log category and log-once](./overview.md)
- [Per-patch Harmony application, drift checks, once-only logging and debug perf scopes](../../../../mods/ksa-measuretools/features/resilient-patching-and-diagnostics.md), [Error budget and self-disable](../../../../mods/ksa-public-telemetry/features/error-budget-self-disable.md)
- [Keep mod exceptions out of KSA's frame loop](../contain-exceptions-in-starmap-hooks/overview.md), [Clean up on game exit and relaunch the game from a mod](../clean-up-on-game-exit/overview.md)
