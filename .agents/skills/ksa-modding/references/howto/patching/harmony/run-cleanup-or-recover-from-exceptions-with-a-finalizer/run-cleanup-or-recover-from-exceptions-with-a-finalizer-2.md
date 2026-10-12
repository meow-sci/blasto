---
type: How-To
title: Swallow an exception and finish the method's remaining work
description: Replay the tail of TransferPlanner.SetTransferInfo from a Harmony finalizer after a NaN Period makes the stock method throw, and swallow the exception by returning null
category: patching
group: harmony
topic: run-cleanup-or-recover-from-exceptions-with-a-finalizer
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/hyperbolic-transfer-targets]
techniques: [harmony-finalizer]
hooks: [harmony-finalizer KSA.TransferPlanner.SetTransferInfo]
lifecycle: [frame:draw-ui]
game_types: [KSA.TransferPlanner, KSA.UniverseTime, KSA.Orbit]
tags: [harmony, finalizer, swallow-exception, transfer-planner, nan]
---

When a stock method throws on data it was never designed for, a postfix never runs, because the
original did not complete. KSA-AdvancedFlightComputer lets the Transfer Planner target interstellar
comets, whose orbits are hyperbolic and whose `Orbit.Period` is `NaN`. `TransferPlanner.SetTransferInfo`
then throws halfway through. The mod attaches a finalizer that swallows the exception and executes only the
lines the method never reached.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by KSA-AdvancedFlightComputer in
[Transfer Planner targets on hyperbolic orbits](../../../../mods/ksa-advancedflightcomputer/features/hyperbolic-transfer-targets.md).

## Background: how the game does it

`SetTransferInfo` is a private static method of `TransferPlanner`. It creates the `TransferInfo`, stores it in
the `_transferInfo` field, computes the Hohmann time of flight, and then derives the minimum and maximum
transfer times by subtracting the target's `Period` from a `UniverseTime`. Everything after that line
(the selected min and max times, the display time unit) depends on it:

```csharp
// current/decomp/KSA/TransferPlanner.cs L1573-L1603
private static void SetTransferInfo()
{
	_transferInfo = null;
	if (Destination == null || Source == null || _destinationBody.GetKey() == "N/A")
	{
		return;
	}
	IOrbiter orbiter = Source.Parent as IOrbiter;
	_transferInfo = new OrbitalTransfers.TransferInfo(orbiter ?? Source, Destination, Source, usePorkChopData: true);
	Orbit origin = ((orbiter == null) ? _transferInfo.Starting : orbiter.Orbit);
	if (OrbitalTransfers.SameSoiTransfer(_transferInfo))
	{
		origin = _transferInfo.Starting;
	}
	_transferInfo.HohmannTimeOfFlight = OrbitalTransfers.HohmannFlight(origin, _transferInfo.Target.Orbit);
	_transferInfo.MinTransferTimeOfFlight = UniverseTime.Max(_transferInfo.HohmannTimeOfFlight - _transferInfo.Target.Orbit.Period, _transferInfo.HohmannTimeOfFlight / 2L);
	_transferInfo.MaxTransferTimeOfFlight = _transferInfo.MinTransferTimeOfFlight + UniverseTime.Min(new UniverseTime(2.0 * _transferInfo.Target.Orbit.Period), _transferInfo.HohmannTimeOfFlight);
	_selectedMinTime = new UniverseTime(_transferInfo.MinTransferTimeOfFlight.Seconds());
	_selectedMaxTime = new UniverseTime(_transferInfo.MaxTransferTimeOfFlight.Seconds());
	foreach (TimeObject timeUnit in _timeUnits)
	{
		string text = TimeSpanReference.AllocateNearestString(timeUnit.Unit, _transferInfo.HohmannTimeOfFlight.Seconds());
		string[] array = text.Split(' ');
		string[] array2 = array[0].Split('.');
		if (array2[0].Length <= 3)
		{
			_selectedTimeUnit = timeUnit;
			break;
		}
	}
}
```

Source: [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L1573-L1603)[^ksa-transferplanner]

For a hyperbolic orbit `Orbit.Period` is `double.NaN` ([current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1658-L1670)[^ksa-orbit]), and a `UniverseTime`
refuses NaN:

```csharp
// current/decomp/KSA/UniverseTime.cs L71-L76
private static Int128 NanosecondsFromSeconds(double seconds)
{
	if (double.IsNaN(seconds))
	{
		throw new ArgumentException("Cannot construct a UniverseTime from NaN");
	}
```

Source: [current/decomp/KSA/UniverseTime.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UniverseTime.cs#L71-L76)[^ksa-universetime]

`UniverseTime - double` goes through `NanosecondsFromSeconds`, so line 1588 throws `ArgumentException`
after `_transferInfo` and `HohmannTimeOfFlight` are already stored ([current/decomp/KSA/UniverseTime.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UniverseTime.cs#L167-L170)).

## Lifecycle and timing

`SetTransferInfo` is called only from inside `TransferPlanner.DrawPlanWindow`
([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L349-L426)), which `Program.OnDrawUiThreadSafe` runs
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3236)[^ksa-program]). The finalizer therefore runs on the main thread in `frame:draw-ui`, inside
the ImGui frame. That is why it may raise the player alert; the mod's other patches on the same feature run
on pool threads and must not ([Patches on planner tasks that run on pooled threads](../patch-methods-that-run-on-background-threads/patch-methods-that-run-on-background-threads-1.md)). See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Recognise a postfix cannot work.** The throw happens at line 1588, so the lines at 1590-1602 never
   run. A postfix on the same method would also never run. The state that the missing lines would have
   produced (window and time unit) has to be produced somewhere else, and a finalizer is the only patch
   type that still runs.

2. **Fix the data that can be fixed earlier.** The mod separately patches `OrbitalTransfers.HohmannFlight`
   so that it does not build a `UniverseTime` from NaN; by the time `SetTransferInfo` throws, stock has
   stored a valid `HohmannTimeOfFlight` ([AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs#L105-L114)[^ksa-advancedflightcomputer-transferplannerpatches]).

3. **Attach a finalizer with the exception as its parameter.** A private target is named by string in the
   attribute; the empty `Type[]` selects the no-argument overload. The finalizer takes
   `Exception? __exception` and returns `Exception?`:

```csharp
// AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs L115-L116, L122-L133
[HarmonyPatch(typeof(TransferPlanner), "SetTransferInfo", new Type[0])]
internal static class Patch_SetTransferInfo
// ...
    static Exception? Finalizer(Exception? __exception)
    {
        try
        {
            OrbitalTransfers.TransferInfo? info = StockPlanner.TransferInfo;
            if (info?.Target is not IOrbiter target || target.Orbit == null || target.Orbit.IsBound())
                return __exception;
            if (__exception != null && __exception is not ArgumentException)
                return __exception;

            double hohmannSec = info.HohmannTimeOfFlight.Seconds();
            if (!(hohmannSec > 0.0))
```

Source: [AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs#L115-L133)

4. **Decide whether the exception is yours.** Return `__exception` unchanged for every case you do not
   own: the target is bound, or the exception is anything other than `ArgumentException`. Returning `null`
   for an unrelated failure would hide real bugs.

5. **Replay the tail and return `null`.** The finalizer fills the window from the stored Hohmann time
   (0.3 times to 4 times), writes the two selected times through cached `AccessTools` field refs, picks
   the time unit the way stock does, raises the one-time alert and returns `null`, which makes Harmony
   swallow the `ArgumentException`:

```csharp
// AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs L146-L162
        info.MinTransferTimeOfFlight = new UniverseTime(hohmannSec * HyperbolicTargets.MinTofRatio);
        info.MaxTransferTimeOfFlight = new UniverseTime(hohmannSec * HyperbolicTargets.MaxTofRatio);
        GameReflection.TransferPlanner_selectedMinTimeRef!() = info.MinTransferTimeOfFlight;
        GameReflection.TransferPlanner_selectedMaxTimeRef!() = info.MaxTransferTimeOfFlight;
        SelectTimeUnit(hohmannSec);

        MaybeAlertDepartureWindowPast(target, info.HohmannTimeOfFlight);
        return null;
    }
    catch (Exception ex)
    {
        LogHelper.WarnOnce("set-transfer-info:" + ex.GetType().Name,
            $"[AFC] SetTransferInfo finalizer for target " +
            $"'{(StockPlanner.TransferInfo?.Target as Astronomical)?.Id ?? "?"}': {ex}");
        return __exception;
    }
}
```

Source: [AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs#L146-L162)

6. **Replay the unit pick faithfully.** The stock loop at lines 1592-1602 takes the first unit whose
   formatted estimate has at most three integer digits; the mod copies that rule:

```csharp
// AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs L167-L182
private static void SelectTimeUnit(double hohmannSec)
{
    if (StockPlanner.TimeUnits is not { } units)
        return;
    foreach (TimeObject unit in units)
    {
        ReadOnlySpan<char> text = TimeSpanReference.AllocateNearestString(unit.Unit, hohmannSec);
        int end = text.IndexOfAny(' ', '.');
        int integerDigits = end < 0 ? text.Length : end;
        if (integerDigits <= 3)
        {
            StockPlanner.SelectedTimeUnit = unit;
            return;
        }
    }
}
```

Source: [AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs#L167-L182)

7. **Verify.** Select an interstellar comet in the Transfer Planner: the window and time unit are filled
   and no exception reaches the draw loop. Choose a bound target and the finalizer returns `__exception`
   (`null` in the success case), so stock behaviour is untouched.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs) | The finalizer, the Hohmann prefix and alignment patches |
| [AdvancedFlightComputer/Features/HyperbolicTargets/HyperbolicTargets.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/HyperbolicTargets/HyperbolicTargets.cs)[^ksa-advancedflightcomputer-hyperbolictargets] | Window ratios (`MinTofRatio` 0.3, `MaxTofRatio` 4.0) |

## Variations and gotchas

- **`null` swallows; `__exception` rethrows; `void` neither.** A finalizer that returns `void` leaves the
  exception in flight. Checked against Lib.Harmony 2.4.2.
- **When the original does not return `void`.** A swallowed exception makes a non-void method return `default`;
  this one returns `void`, so there is nothing to supply.
- **Replay only what you can prove.** The finalizer copies lines that sit *after* the throw. If the stock method
  changes order in a later build, the replay is wrong silently. The mod documents the stock lines in
  comments and drives the finalizer from a headless test
  ([tests/AdvancedFlightComputer.HarnessTests/Features/HyperbolicTargets/HyperbolicTargetsTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/HyperbolicTargets/HyperbolicTargetsTest.cs#L185-L190)[^ksa-advancedflightcomputer-hyperbolictargetstest]).
- **Never construct a `UniverseTime` from a value derived from `Period`, `Apoapsis` or a synodic period
  without a finiteness check.**
- **The finalizer must itself be safe.** It is wrapped in `try/catch` and returns the original exception if the
  replay fails, so a bug in the replay cannot hide a different failure.

## See also

- [Restore what a prefix changed around one game call](run-cleanup-or-recover-from-exceptions-with-a-finalizer-1.md), [Prefix with __state and a finalizer that restores an edited input](run-cleanup-or-recover-from-exceptions-with-a-finalizer-3.md), [Prefix, postfix and finalizer as a transaction around loads](run-cleanup-or-recover-from-exceptions-with-a-finalizer-4.md), [ThreadStatic override set in try/finally around the mod's own submission](run-cleanup-or-recover-from-exceptions-with-a-finalizer-5.md), [Thread-static construction context scoping a transpiler and a prefix](run-cleanup-or-recover-from-exceptions-with-a-finalizer-6.md), [Mark the asker in a Priority.First prefix, clear it in a Priority.Last finalizer](run-cleanup-or-recover-from-exceptions-with-a-finalizer-7.md)
- [Harmony finalizers, cleanup and scoped overrides](overview.md)
- [Transfer Planner targets on hyperbolic orbits](../../../../mods/ksa-advancedflightcomputer/features/hyperbolic-transfer-targets.md)
- [Patching game methods that run on worker and pool threads](../patch-methods-that-run-on-background-threads/overview.md)

[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
[^ksa-universetime]: KSA.UniverseTime (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-advancedflightcomputer-transferplannerpatches]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs
[^ksa-advancedflightcomputer-hyperbolictargets]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Features/HyperbolicTargets/HyperbolicTargets.cs
[^ksa-advancedflightcomputer-hyperbolictargetstest]: KSA-AdvancedFlightComputer/tests/AdvancedFlightComputer.HarnessTests/Features/HyperbolicTargets/HyperbolicTargetsTest.cs
