---
type: How-To
title: Choose Orbit.Mu or Parent.Mu for transfer maths and cache per loaded system
description: Pick the gravitational parameter that matches the question (the hub's Parent.Mu for a transfer ellipse, the orbit's own Orbit.Mu for a body's speed) and cache results keyed by the CelestialSystem instance, not its Id
category: flight-and-orbits
group: flight-state
topic: read-orbit-elements-and-timing
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-deltavmap/features/patched-conic-transfer-model]
techniques: [per-system-cache, patched-conic-math]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Orbit, KSA.IParentBody, KSA.IOrbiter, KSA.CelestialSystem, KSA.Universe]
tags: [orbit, mu, parent-mu, cache, celestial-system, hohmann, barycenter]
---

Outcome: orbit-based transfer maths that stays correct for stars orbiting a barycenter and for comets, and a cache that
is dropped when a save or scene swap replaces the bodies. KSA-DeltaVMap does both for its whole-system pricing.[Closed-form patched-conic transfer model](../../../../mods/ksa-deltavmap/features/patched-conic-transfer-model.md)
Prerequisite: StarMap for the draw hook.

## Background: how the game does it

`Orbit.Mu` is the orbit's own central mass times G when one is authored, otherwise the parent's `Mu`:
`double.IsNaN(_centralMass) ? Parent.Mu : (_centralMass * 6.6743E-11)`. `IParentBody.Mu` is `Mass * 6.6743E-11`.[^ksa-orbit]
[current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1181) [^ksa-iparentbody] [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L15) The two differ for the stars of a multiple system
orbiting a barycenter. A vessel's transfer ellipse feels only the hub's point mass (`Parent.Mu`); a body that sits on an orbit with
its own central mass moves at the speed that mass gives it (`Orbit.Mu`).

Elements of celestial bodies are fixed Keplerian values on rails, so results are valid for as long as the same bodies exist.
`Universe.CurrentSystem` is the `CelestialSystem`; loading a save rebuilds its bodies.[^ksa-universe] [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L114)

## Lifecycle and timing

Everything runs on the main thread in the Delta-V Map window's draw, called from `[StarMapAfterGui]` (`frame:draw-ui`). It
reads only celestial `Orbit` element getters, `IParentBody.Mu`, `MeanRadius` and `SphereOfInfluence`, so reading after
`frame:dispatch-solvers` has started the next vehicle step is safe; nothing is written to the game. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Use the hub's Mu for the transfer ellipse** and the orbit's own `Mu` for a body's circular speed.
2. **Detect an own central mass** by comparing the two with a relative tolerance:

```csharp
// DeltaVMap/Dv/OrbitalStates.cs L248-L251
public static bool HasOwnCentralMass(Orbit orbit, double muHub)
{
    return Math.Abs(orbit.Mu - muHub) > CentralMassTolerance * muHub;
}
```

3. **Pick the formula by the endpoints.** Open orbits (`e >= 1`) use the periapsis as rendezvous radius and a conic
   formula; own-central-mass pairs use the speeds from their own `Mu`; everything else is plain Hohmann. Siblings only: the
   precondition is the same `Orbit.Parent`:

```csharp
// DeltaVMap/Dv/DvCache.cs L70-L116
private static EdgeDv Compute(IOrbiter from, IOrbiter to)
{
    Orbit fromOrbit = from.Orbit;
    Orbit toOrbit = to.Orbit;

    // A Hohmann transfer only makes sense between bodies sharing one hub. This
    // is a documented precondition; surface a violation loudly rather than
    // returning a number computed in mismatched frames. The dump wraps the
    // call in a catch, so this cannot reach the render path.
    if (fromOrbit.Parent != toOrbit.Parent)
        throw new ArgumentException(
            $"Transfer requires two bodies sharing one hub, got '{from.Id}' (parent '{fromOrbit.Parent?.Id}') and '{to.Id}' (parent '{toOrbit.Parent?.Id}').");

    // The vessel's transfer ellipse feels the hub's point mass, but a body on an orbit with
    // its own central mass (Orbit.Mu differs from Parent.Mu, as for the stars of a multiple
    // system) moves at the speed that central mass gives it.
    double muHub = fromOrbit.Parent.Mu;
    double r1 = OrbitalStates.TransferRadius(fromOrbit);
    double r2 = OrbitalStates.TransferRadius(toOrbit);

    // An open endpoint (a comet) is matched at its true perihelion speed; a bound pair
    // keeps the exact circular Hohmann, so stock planet-to-planet numbers are unchanged.
    bool fromOpen = fromOrbit.Eccentricity >= 1.0;
    bool toOpen = toOrbit.Eccentricity >= 1.0;
    bool ownCentralMass = OrbitalStates.HasOwnCentralMass(fromOrbit, muHub) || OrbitalStates.HasOwnCentralMass(toOrbit, muHub);
    double departDv;
    double arriveDv;
    if (fromOpen || toOpen)
    {
        DeltaVCalculator.ConicTransfer(muHub, r1, fromOpen, fromOrbit.Eccentricity, r2, toOpen, toOrbit.Eccentricity, out departDv, out arriveDv);
    }
    else if (ownCentralMass)
    {
        double v1 = DeltaVCalculator.CircularSpeed(fromOrbit.Mu, r1);
        double v2 = DeltaVCalculator.CircularSpeed(toOrbit.Mu, r2);
        DeltaVCalculator.TransferBetweenSpeeds(muHub, r1, v1, r2, v2, out departDv, out arriveDv);
    }
    else
    {
        DeltaVCalculator.Hohmann(muHub, r1, r2, out departDv, out arriveDv);
    }

    double transferTime = DeltaVCalculator.TransferTimeSeconds(muHub, r1, r2);
    // A pair with its own central mass is a multiple-star pair: eccentric, mutually bound
    // orbits that a two-impulse estimate only roughly covers.
    bool isApproximate = fromOpen || toOpen || ownCentralMass;

```

4. **Cache one value per unordered pair**, keyed by the two Ids in ordinal order, returning the reversed edge for the other direction:

```csharp
// DeltaVMap/Dv/DvCache.cs L54-L68
public EdgeDv GetTransfer(IOrbiter from, IOrbiter to)
{
    bool swapped = string.CompareOrdinal(from.Id, to.Id) > 0;
    var key = swapped ? (to.Id, from.Id) : (from.Id, to.Id);

    if (!_cache.TryGetValue(key, out EdgeDv edge))
    {
        IOrbiter first = swapped ? to : from;
        IOrbiter second = swapped ? from : to;
        edge = Compute(first, second);
        _cache[key] = edge;
    }

    return swapped ? edge.Reversed() : edge;
}
```

5. **Key the cache lifetime to the system object.** Latch on `ReferenceEquals(_graph.System, system)`, not the system Id:
   loading the same system again builds new bodies, and nodes holding old ones would read orbits that no longer update:

```csharp
// DeltaVMap/Render/MapWindow.cs L1742-L1757
private bool EnsureBuilt()
{
    CelestialSystem? system = Universe.CurrentSystem;
    if (system == null)
        return false;

    // Latched on the system instance, not its Id: loading the same system again builds new
    // bodies, and nodes holding the old ones would read orbits that no longer update.
    if (_graph == null || !ReferenceEquals(_graph.System, system))
    {
        SystemGraph? graph = SystemGraph.Build(system);
        if (graph == null)
            return false;
        _graph = graph;
        _cache = new DvCache();
        _palette = ColorPalette.Build(graph);
```

## Complete example

| File | Role |
|---|---|
| [DeltaVMap/Dv/DvCache.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/DvCache.cs#L40-L119) | `EdgeDv` and the unordered-pair cache |
| [DeltaVMap/Dv/OrbitalStates.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/OrbitalStates.cs#L157-L169) | `TransferRadius`, `HasOwnCentralMass` |
| [DeltaVMap/Dv/DeltaVCalculator.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/DeltaVCalculator.cs#L10-L207) | pure closed-form math, no game types |

## Variations and gotchas

- The cache is never invalidated within a loaded system; a mod that edits orbits at runtime leaves stale numbers until
  the `CelestialSystem` instance changes.
- `DvCache.GetTransfer` throws for bodies with different parents; only pass siblings.
- `Orbit.GetRelativeInclination` compares normals in each orbit's own parent frame; price a plane change only when both
  orbits share one `Parent` object.
- Comet and multiple-star results are estimates; flag them as approximate in the UI.

## See also

- [Overview](overview.md); [CCI readouts](read-orbit-elements-and-timing-1.md); [sentinel-safe reads](read-orbit-elements-and-timing-2.md)
- [Closed-form patched-conic transfer model](../../../../mods/ksa-deltavmap/features/patched-conic-transfer-model.md)
- [Estimate transfer delta-v in closed form with patched conics](../../transfers/estimate-transfer-delta-v-with-patched-conics/overview.md), [Plan and total multi-leg and interstellar delta-v routes](../../transfers/plan-multi-leg-and-interstellar-delta-v-routes/overview.md)

[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
