---
type: Mod Feature
title: Transfer Planner targets on hyperbolic orbits
description: Give interstellar comets a mass and SOI with an XML patch, list them as planner targets, and patch the time-of-flight, transfer-window, alignment, intercept and closest-approach code that assumes a finite period, including a finalizer that finishes a method that throws
mod: ksa-advancedflightcomputer
category: flight-and-orbits
techniques: [harmony-prefix, harmony-postfix, harmony-finalizer, xml-patch, reflection, background-thread]
hooks:
  - harmony-postfix KSA.TransferPlanner.PopulateWithPlanets
  - harmony-prefix KSA.OrbitalTransfers.HohmannFlight
  - harmony-finalizer KSA.TransferPlanner.SetTransferInfo
  - harmony-prefix KSA.OrbitalTransfers.AlignmentTime
  - harmony-prefix KSA.RefineBurnTask.TryFindIntercept
  - harmony-prefix KSA.PatchedConic.FindClosestApproaches
lifecycle: [startup:asset-load, frame:draw-ui, thread:background, physics:vehicle-solver]
game_types:
  - KSA.TransferPlanner
  - KSA.OrbitalTransfers
  - KSA.RefineBurnTask
  - KSA.TransferTask
  - KSA.PatchedConic
  - KSA.Orbit
  - KSA.UniverseTime
tags: [transfer-planner, hyperbolic, comets, porkchop, finalizer, nan, thread-pool]
---

The stock Transfer Planner drops every body with eccentricity >= 1 from its target list, so the
interstellar comets (Oumuamua, 2I/Borisov, 3I/ATLAS) cannot be targeted. The Lambert solver itself
handles any geometry; what breaks is the code around it, which assumes a finite `Period`, a positive
semi-major axis and a usable sphere of influence. AFC fixes the data with an XML patch and each
assumption with a targeted patch.

## How it works

| Problem in stock | AFC fix |
|---|---|
| Comets have no `Mass`/`SphereOfInfluence` in the system XML (3I_ATLAS has a mass) | `Patches/HyperbolicBodies.xml` appends `<Mass Gg=...>` and `<SphereOfInfluence Km="50000">` to each `InterstellarComet` (applied by KittenExtensions) ([AdvancedFlightComputer/Patches/HyperbolicBodies.xml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Patches/HyperbolicBodies.xml)) |
| `PopulateWithPlanets` skips `Eccentricity >= 1.0` ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L1824)) | Postfix appends unbound bodies that orbit the transfer's own frame and have a valid SOI ([AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs#L8-L58)) |
| `HohmannFlight` uses `(Apoapsis + Periapsis) / 2`; apoapsis is NaN | Prefix uses periapsis for an unbound end and returns `UniverseTime.Zero` instead of building a `UniverseTime` from NaN |
| `SetTransferInfo` subtracts the NaN `Period` from a `UniverseTime`, which throws ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L1573-L1589)) | **Finalizer**: the original never completes, so a postfix would never run. By the time it throws, stock already stored the new `TransferInfo`; the finalizer replays only the lines after the throw (window as 0.3x to 4x the Hohmann time, selected min/max, time unit) and swallows the `ArgumentException` |
| `AlignmentTime` uses the synodic period, infinite against an unbound target | Prefix places departure one Hohmann time before the comet's periapsis |
| `RefineBurnTask.TryFindIntercept` sweeps dv for an SOI encounter it cannot resolve | Prefix builds the flight plan from the Lambert dv and measures closest approach by sampling the heliocentric patch (1 day coarse, 1 minute fine) ([AdvancedFlightComputer/Features/HyperbolicTargets/EncounterPatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/HyperbolicTargets/EncounterPatches.cs#L11-L129)) |
| `PatchedConic.FindClosestApproaches` (private) builds a `UniverseTime` from NaN for a finite patch against an unbound body | Prefix skips only that branch and reports "searched until end of time", the same answer stock gives for a non-finite window; it is a soft anchor and its absence is logged as an error because the XML patch alone would make the stock scan throw |

`HyperbolicTargets.TransferFrame` mirrors how `SetTransferInfo` picks the body the transfer is
solved around (the source's parent's parent when the parent orbits something), so from a moon orbit
a heliocentric comet is not offered ([AdvancedFlightComputer/Features/HyperbolicTargets/HyperbolicTargets.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/HyperbolicTargets/HyperbolicTargets.cs)).

## Lifecycle and timing

| Patch | Thread |
|---|---|
| XML patch | `startup:asset-load`, applied by KittenExtensions before system XML is deserialized |
| `PopulateWithPlanets`, `SetTransferInfo` | Main thread, from `TransferPlanner.DrawPlanWindow` (`frame:draw-ui`) |
| `AlignmentTime`, `HohmannFlight` | Both: the plan window's alignment block on the main thread, and `TransferTask.Run` on the `ThreadPool` ([current/decomp/KSA/TransferTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferTask.cs#L160-L200))[^ksa-transfertask] |
| `TryFindIntercept` | The `ThreadPool` thread `RefineBurnTask` queues itself on ([current/decomp/KSA/RefineBurnTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RefineBurnTask.cs#L20-L41)) |
| `FindClosestApproaches` | Wherever patched conics look for encounters and target nodes: the vehicle worker (`VehicleUpdateState` flight-plan and burn-plan updates, `PhysicsBubble` encounter scans), `TransferTask` on the `ThreadPool`, and main-thread target changes (`Vehicle` and `BurnPlan.CalculateTargetNodes`); not the orbit solver jobs, which propagate celestials only |

Because three of the patches run off the main thread (two on pool threads, the
`FindClosestApproaches` prefix also on the vehicle workers, per patch per step), they touch no
single-threaded state: the player
alert ("ideal departure is already past") is raised only from the finalizer on the draw thread,
since `TimedAlert.Create` mutates an unsynchronized list, and logging goes through the locked
[warn-once helper](warn-once-logging-and-diagnostics.md).

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Patches/HyperbolicBodies.xml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Patches/HyperbolicBodies.xml) | Mass and SOI for the comets |
| [AdvancedFlightComputer/Features/HyperbolicTargets/HyperbolicTargets.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/HyperbolicTargets/HyperbolicTargets.cs) | Patch block, transfer frame, window ratios |
| [AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs) | Target listing, Hohmann time, window finalizer, alignment |
| [AdvancedFlightComputer/Features/HyperbolicTargets/EncounterPatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/HyperbolicTargets/EncounterPatches.cs) | Intercept replacement and closest-approach guard |
| [tests/AdvancedFlightComputer.HarnessTests/Features/HyperbolicTargets/HyperbolicTargetsTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/HyperbolicTargets/HyperbolicTargetsTest.cs) | Headless test |

## Snippets

```csharp
// AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs L115-L143
[HarmonyPatch(typeof(TransferPlanner), "SetTransferInfo", new Type[0])]
internal static class Patch_SetTransferInfo
{
    private static readonly HashSet<string> _alertedTargets = new();

    public static void Reset() => _alertedTargets.Clear();

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
            {
                // No estimate to size a window from.
                // A null TransferInfo is stock's own "nothing selected" state, so the window degrades instead of throwing out of the draw every frame.
                if (GameReflection.TransferPlanner_transferInfoRef is { } infoRef)
                    infoRef() = null;
                LogHelper.WarnOnce(
                    $"transfer-window-degenerate-{target.Id}",
                    "[AFC] SetTransferInfo: no Hohmann estimate for the unbound target; " +
                    "transfer cleared.");
                return null;
```

```xml
<!-- AdvancedFlightComputer/Patches/HyperbolicBodies.xml L7-L19 -->
  <Copy Path="//InterstellarComet[@Id='Oumuamua']" Pos="Append">
    <Mass Gg="4.19" />
    <SphereOfInfluence Km="50000" />
  </Copy>

  <Copy Path="//InterstellarComet[@Id='Borisov']" Pos="Append">
    <Mass Gg="524" />
    <SphereOfInfluence Km="50000" />
  </Copy>

  <Copy Path="//InterstellarComet[@Id='3I_ATLAS']" Pos="Append">
    <SphereOfInfluence Km="50000" />
  </Copy>
```

## Game APIs touched

| Member | Use |
|---|---|
| `TransferPlanner.PopulateWithPlanets(Span<TransferObject>, ref int, bool)` ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L1776)) | Target list |
| `TransferPlanner.SetTransferInfo()` (private, [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L1573)) | Window sizing from `Period` |
| `OrbitalTransfers.HohmannFlight`, `AlignmentTime` ([current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs#L708-L756)) | Time-of-flight and departure alignment |
| `RefineBurnTask.TryFindIntercept` ([current/decomp/KSA/RefineBurnTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RefineBurnTask.cs#L41)) | Intercept refinement on the pool |
| `PatchedConic.FindClosestApproaches` (private, [current/decomp/KSA/PatchedConic.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PatchedConic.cs#L440)) | Encounter search |
| `Orbit.IsBound`, `Periapsis`, `SemiMajorAxis`, `Period` ([current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs)) | Bound/unbound branches |

## Pitfalls and version notes

- `UniverseTime` throws on NaN; never build one from a value derived from `Period`, `Apoapsis` or a
  synodic period without a finiteness check.
- A finalizer that returns `null` swallows the exception; return `__exception` for every case you do
  not own, or you hide real bugs.
- The comet SOI (50,000 km) is a modeling choice for the planner, not an observed value.
- Without KittenExtensions the comets have no SOI and are skipped with a one-time log line.

## See also

- [Optional KittenExtensions patches](optional-kittenextensions-patches.md)
- [Transfer Planner custom plan types](transfer-planner-custom-plan-types.md)
- [Validated reflection handles](validated-reflection-handles.md)
- [Add missing mass and sphere-of-influence elements to a kind of body](../../../howto/assets/xml-patching/xpath-xml-patch-recipes/xpath-xml-patch-recipes-2.md) - how-to article demonstrating this feature
- [Let the planner target hyperbolic bodies with an XML patch, NaN-period patches and a Harmony finalizer](../../../howto/flight-and-orbits/transfers/add-plan-type-to-the-transfer-planner/add-plan-type-to-the-transfer-planner-5.md) - how-to article demonstrating this feature
- [Patches on planner tasks that run on pooled threads](../../../howto/patching/harmony/patch-methods-that-run-on-background-threads/patch-methods-that-run-on-background-threads-1.md) - how-to article demonstrating this feature
- [Swallow an exception and finish the method's remaining work](../../../howto/patching/harmony/run-cleanup-or-recover-from-exceptions-with-a-finalizer/run-cleanup-or-recover-from-exceptions-with-a-finalizer-2.md) - how-to article demonstrating this feature

[^ksa-transfertask]: KSA.TransferTask (decompiled, build 2026.10.10.5554)
