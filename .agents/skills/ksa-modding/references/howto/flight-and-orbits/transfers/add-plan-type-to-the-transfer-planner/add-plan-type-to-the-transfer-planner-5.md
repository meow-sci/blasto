---
type: How-To
title: Let the planner target hyperbolic bodies with an XML patch, NaN-period patches and a Harmony finalizer
description: Add Mass and SphereOfInfluence to comets in the system XML, then patch each stock assumption (target list, Hohmann time, window sizing, alignment, intercept search, closest approaches) that breaks for an eccentricity of one or more
category: flight-and-orbits
group: transfers
topic: add-plan-type-to-the-transfer-planner
approach: 5
difficulty: advanced
prerequisites: [starmap, harmony, kittenextensions]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/hyperbolic-transfer-targets]
techniques: [harmony-finalizer, harmony-prefix, xml-patch]
hooks:
  - harmony-postfix KSA.TransferPlanner.PopulateWithPlanets
  - harmony-finalizer KSA.TransferPlanner.SetTransferInfo
  - harmony-prefix KSA.OrbitalTransfers.HohmannFlight
  - harmony-prefix KSA.OrbitalTransfers.AlignmentTime
lifecycle: [startup:asset-load, frame:draw-ui, thread:background]
game_types:
  - KSA.TransferPlanner
  - KSA.OrbitalTransfers
  - KSA.RefineBurnTask
  - KSA.TransferTask
  - KSA.PatchedConic
  - KSA.Orbit
  - KSA.UniverseTime
tags: [hyperbolic, comet, finalizer, xml-patch, nan, thread-pool, transfer-planner, kittenextensions]
---

Outcome: the interstellar comets (Oumuamua, 2I/Borisov, 3I/ATLAS) become targetable in the stock Transfer Planner. The Lambert solver itself handles any geometry; what breaks is the code around it, which assumes a finite `Period`, a positive semi-major axis and a usable sphere of influence.
KSA-AdvancedFlightComputer fixes the data with an XML patch and each assumption with a targeted patch.[Transfer Planner targets on hyperbolic orbits](../../../../mods/ksa-advancedflightcomputer/features/hyperbolic-transfer-targets.md) Prerequisites: StarMap, Harmony and KittenExtensions (for the XML patch).

## Background: how the game does it

`PopulateWithPlanets(list, ref count, getAll)` skips bodies with `Eccentricity >= 1.0` or a NaN or non-positive SOI. `HohmannFlight` uses `(Apoapsis + Periapsis) / 2`, which is NaN for an unbound orbit. `SetTransferInfo` subtracts the NaN `Period` from a `UniverseTime`, which throws.[^ksa-transferplanner] [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L1573-L1589) [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L1776-L1828)
[^ksa-orbitaltransfers] [current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs#L756-L763) `AlignmentTime` uses the synodic period, infinite against an unbound target; `RefineBurnTask.TryFindIntercept` sweeps delta-v for an SOI encounter it cannot resolve; the private `PatchedConic.FindClosestApproaches` builds a `UniverseTime` from NaN for a finite patch against an unbound body.

## Lifecycle and timing

| Patch | Thread |
|---|---|
| XML patch | `startup:asset-load`, applied by KittenExtensions before the system XML is deserialized |
| `PopulateWithPlanets`, `SetTransferInfo` | main thread, from `TransferPlanner.DrawPlanWindow` (`frame:draw-ui`) |
| `AlignmentTime`, `HohmannFlight` | both: the plan window's alignment block on the main thread and `TransferTask.Run` on the `ThreadPool` |
| `TryFindIntercept` | the `ThreadPool` thread `RefineBurnTask` queues itself on |
| `FindClosestApproaches` | wherever patched conics look for encounters: the vehicle worker, `TransferTask` on the pool, and main-thread target changes; not the orbit solver jobs |

Three of the patches run off the main thread, so they touch no single-threaded state: the player alert is raised only from the finalizer on the draw thread (`TimedAlert.Create` mutates an unsynchronized list), and logging goes through a locked warn-once helper. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Run code inside the physics step on worker threads](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md).

## Steps

1. **Fix the data.** Append `Mass` and `SphereOfInfluence` to each `InterstellarComet` with an XML patch (3I_ATLAS already has a mass):

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

2. **Offer the bodies.** A postfix on `PopulateWithPlanets` appends unbound bodies that orbit the transfer's own frame and have a valid SOI. Mirror how `SetTransferInfo` picks the body the transfer is solved around (the source's parent's parent when the parent orbits something), so from a moon orbit a heliocentric comet is not offered.
3. **Make Hohmann time finite.** A prefix on `HohmannFlight` uses the periapsis for an unbound end and returns `UniverseTime.Zero` instead of building a `UniverseTime` from NaN.
4. **Use a finalizer, not a postfix, for `SetTransferInfo`.** The original never completes, so a postfix would never run. By the time it throws, stock already stored the new `TransferInfo`; the finalizer replays only the lines after the throw (window as 0.3x to 4x the Hohmann time, selected min and max, time unit) and swallows the `ArgumentException`:

```csharp
// AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs L115-L128
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
```

5. **Replace alignment and intercept search.** A prefix on `AlignmentTime` places departure one Hohmann time before the comet's periapsis; a prefix on `RefineBurnTask.TryFindIntercept` builds the flight plan from the Lambert delta-v and measures closest approach by sampling the heliocentric patch (1 day coarse, 1 minute fine).
6. **Guard the private scan.** A prefix on `FindClosestApproaches` skips only the NaN branch and reports "searched until end of time", the same answer stock gives for a non-finite window; it is a soft anchor and its absence is logged as an error because the XML patch alone would make the stock scan throw.
7. **Raise alerts only on the draw thread.** The "ideal departure is already past" alert comes from the finalizer.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Patches/HyperbolicBodies.xml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Patches/HyperbolicBodies.xml) | mass and SOI for the comets |
| [AdvancedFlightComputer/Features/HyperbolicTargets/HyperbolicTargets.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/HyperbolicTargets/HyperbolicTargets.cs) | patch block, transfer frame, window ratios |
| [AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs) | target listing, Hohmann time, window finalizer, alignment |
| [AdvancedFlightComputer/Features/HyperbolicTargets/EncounterPatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/HyperbolicTargets/EncounterPatches.cs#L11-L129) | intercept replacement and closest-approach guard |

## Variations and gotchas

- **A finalizer returning `null`** swallows the exception; returning `__exception` rethrows. Return the original for any exception that is not the expected `ArgumentException`.
- **Methods on pool threads** must not touch ImGui or unsynchronized state; keep patched bodies pure and allocation-light.
- **Soft anchors** (private methods that may be renamed) should log loudly but not abort the whole feature.
- Without the XML patch the comets have no SOI, and `PopulateWithPlanets` skips them regardless of the other patches.

## See also

- [Overview](overview.md); [skip-original prefix](add-plan-type-to-the-transfer-planner-1.md)
- [Transfer Planner targets on hyperbolic orbits](../../../../mods/ksa-advancedflightcomputer/features/hyperbolic-transfer-targets.md), [Optional KittenExtensions XML patches from mod.toml](../../../../mods/ksa-advancedflightcomputer/features/optional-kittenextensions-patches.md)
- [Read orbit elements, apsides and event countdowns safely](../../flight-state/read-orbit-elements-and-timing/overview.md)

[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
[^ksa-orbitaltransfers]: KSA.OrbitalTransfers (decompiled, build 2026.10.10.5554)
