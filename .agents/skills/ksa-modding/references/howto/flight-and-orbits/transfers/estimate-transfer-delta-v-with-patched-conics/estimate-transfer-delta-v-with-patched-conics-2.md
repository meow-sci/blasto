---
type: How-To
title: Price transfers with hub Hohmann legs wrapped in Oberth burns, cached per body pair
description: Compute the departure and arrival burns between bodies that share a hub as Hohmann legs around the hub, convert each leg to an Oberth ejection or capture from the end body's low orbit, and add comet, multiple-star, ascent, landing and plane-change terms
category: flight-and-orbits
group: transfers
topic: estimate-transfer-delta-v-with-patched-conics
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksa-deltavmap/features/patched-conic-transfer-model]
techniques: [patched-conic-math, per-system-cache]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Orbit
  - KSA.IParentBody
  - KSA.IOrbiter
  - KSA.OrbitalTransfers
  - KSA.PhysicalAtmosphereReference
  - KSA.CelestialSystem
tags: [patched-conic, oberth, hohmann, delta-v-map, comet, atmosphere, plane-change, cache]
---

Outcome: a delta-v for any pair of bodies in a system, computed once per pair and cached, that mirrors the game's own transfer model: no iterative solver, so pricing a whole star system is cheap. KSA-DeltaVMap uses it for its subway map.[Closed-form patched-conic transfer model](../../../../mods/ksa-deltavmap/features/patched-conic-transfer-model.md)
Prerequisite: StarMap for a draw hook.

## Background: how the game does it

A transfer between two bodies that share a hub (two planets of a star, two moons of a planet) is a Hohmann ellipse around the hub; at each end the burn is wrapped in an Oberth ejection or capture from that body's low orbit. The code is a three-layer split: `DeltaVCalculator` is pure SI math with no game types, `DvCache` turns two `IOrbiter`s into a direction-bearing pair of hub-frame legs, and `TransferBurns.ComputeLegs` turns those plus each end body's ladder into the two real burns.
The `0.01` circular threshold and effective radius rule are copied from `OrbitalTransfers.HohmannFlight`.[^ksa-orbitaltransfers] [current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs#L756-L763)

## Lifecycle and timing

Everything runs on the main thread inside the map window's draw (`[StarMapAfterGui]`, `frame:draw-ui`), only while the window is shown and the HUD is visible. State read: celestial `Orbit` element getters, `IParentBody.Mu`, `MeanRadius`, `SphereOfInfluence` and the atmosphere template, all fixed Keplerian values of on-rails bodies, so reading after `frame:dispatch-solvers` is safe. Nothing is written. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Hub legs.** Departure and arrival speeds are the Hohmann leg magnitudes; the arrival magnitude doubles as the hyperbolic excess speed `vInf` for an Oberth capture:

```csharp
// DeltaVMap/Dv/DeltaVCalculator.cs L119-L127, L172-L181
// Hohmann transfer around a hub body between two orbital radii. Returns the
// departure and arrival burn magnitudes separately; the arrival magnitude
// doubles as the hyperbolic excess speed (v_inf) for an Oberth capture.
public static void Hohmann(double muHub, double r1, double r2, out double dvDepart, out double dvArrive)
{
    double sum = r1 + r2;
    dvDepart = Math.Abs(CircularSpeed(muHub, r1) * (Math.Sqrt(2.0 * r2 / sum) - 1.0));
    dvArrive = Math.Abs(CircularSpeed(muHub, r2) * (1.0 - Math.Sqrt(2.0 * r1 / sum)));
}
// ...
// Analytical patched-conic ejection (or capture, reversed) from a circular low
// orbit deep in the well, given the hyperbolic excess speed v_inf from the hub
// Hohmann. As mu -> 0 the Oberth benefit vanishes and the burn tends to v_inf,
// which is physically correct and needs no special case.
public static double OberthBurn(double muBody, double rLowOrbit, double vInf)
{
    double vPark = CircularSpeed(muBody, rLowOrbit);
    double vBurn = Math.Sqrt(vInf * vInf + 2.0 * muBody / rLowOrbit);
    return vBurn - vPark;
}
```

2. **Choose the formula by the endpoints** (see [Read orbit elements, apsides and event countdowns safely](../../flight-state/read-orbit-elements-and-timing/overview.md), approach 3): open orbit (`e >= 1`) uses the periapsis as rendezvous radius and a conic formula; own-central-mass pairs use their own speeds; otherwise exact circular Hohmann so stock planet-to-planet numbers are unchanged. Cache per unordered pair keyed by body ids:

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

3. **Wrap the legs in Oberth burns.** The hub end pays the plain leg; a child or sibling end pays `OberthBurn(ladder.Mu, ladder.LowOrbitRadius, vInf)`; an unknown body falls back to the raw leg (the `mu -> 0` limit). When the transfer lands on an Intercept anchor the arrival burn is the capture minus the circularize carried by a separate ladder edge, so a route never charges it twice:

```csharp
// DeltaVMap/Model/TransferBurns.cs L98-L121, L131-L136
    BodyLadder? departLadder = ladderFor(departBody.Id);
    BodyLadder? arriveLadder = ladderFor(arriveBody.Id);

    double departBurn = departIsHub
        ? dv.DepartDv
        : OberthOrRaw(departLadder, dv.DepartDv);

    double arriveFull = arriveIsHub
        ? dv.ArriveDv
        : OberthOrRaw(arriveLadder, dv.ArriveDv);

    // The capture into a destination's Intercept ellipse is the full capture minus
    // the circularize the Capture ladder edge carries, so the route never charges
    // the circularize twice.
    double circularize = 0.0;
    bool split = false;
    double arriveBurn = arriveFull;
    if (!arriveIsHub && edge.To.Kind == StateKind.Intercept
        && arriveLadder is { CanHoldOrbit: true } al && al.SoiRadius.HasValue)
    {
        circularize = DeltaVCalculator.EscapeToSoi(al.Mu, al.LowOrbitRadius, al.SoiRadius.Value);
        arriveBurn = Math.Max(0.0, arriveFull - circularize);
        split = true;
    }
// ...
private static double OberthOrRaw(BodyLadder? ladder, double vInf)
{
    if (ladder == null || !(ladder.LowOrbitRadius > 0.0))
        return vInf;
    return DeltaVCalculator.OberthBurn(ladder.Mu, ladder.LowOrbitRadius, vInf);
}
```

4. **Add the other terms as the model needs them**: transfer time `pi sqrt(a_t^3 / mu_hub)`; plane change `2 v sin(di/2)` (zero below half a degree, `v` the smaller `vInf`; price against `vInf`, not the hub orbital speed, which overstated Earth to Mars about tenfold); vacuum ascent from rest to a low circle; an empirical atmospheric ascent factor
   `clamp(1 + 0.12 sqrt(rho/1.225)(1 + 2H/R) + 0.05 sqrt(g/9.81), 1, 2)` and landing fraction; raise to the SOI edge `EscapeToSoi`. Flag comets, multiple-star pairs and every ascent or landing edge as approximate.
5. **Keep the cache honest** by latching on the `CelestialSystem` object (see [Read orbit elements, apsides and event countdowns safely](../../flight-state/read-orbit-elements-and-timing/overview.md)).

## Complete example

| File | Role |
|---|---|
| [DeltaVMap/Dv/DeltaVCalculator.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/DeltaVCalculator.cs#L10-L207) | pure closed-form math |
| [DeltaVMap/Dv/DvCache.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/DvCache.cs#L40-L119) | `EdgeDv` and the unordered-pair cache |
| [DeltaVMap/Model/TransferBurns.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/TransferBurns.cs#L75-L137) | hub legs to real burns |
| [DeltaVMap/Dv/OrbitalStates.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/OrbitalStates.cs#L157-L169) | `TransferRadius`, `HasOwnCentralMass`, ascent and descent |
| [DeltaVMap/Model/VisualTree.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/VisualTree.cs#L459-L504) | ladder edge costs |

## Variations and gotchas

- **The atmosphere numbers are absolute ISA references** (1.225 kg/m^3, 9.81 m/s^2), not stock-body data; a custom system with a 1.225 kg/m^3 atmosphere behaves like Earth. The landing fraction comment records the offline check against stock data (Earth about 510 m/s, Venus about 460, Mars about 775).
- **The vacuum ascent starts from rest**, so the surface rotation is not credited.
- **Comet arrivals** cost more than the circular one; the offline tests assert that and that the bound case equals plain Hohmann.
- `DvCache.GetTransfer` throws for bodies with different parents; only pass siblings.

## See also

- [Overview](overview.md); [Hohmann to an altitude](estimate-transfer-delta-v-with-patched-conics-1.md)
- [Closed-form patched-conic transfer model](../../../../mods/ksa-deltavmap/features/patched-conic-transfer-model.md)
- [Plan and total multi-leg and interstellar delta-v routes](../plan-multi-leg-and-interstellar-delta-v-routes/overview.md), [Compute phase angles, synodic periods and the time to the next transfer window](../compute-transfer-windows/overview.md)

[^ksa-orbitaltransfers]: KSA.OrbitalTransfers (decompiled, build 2026.10.10.5554)
