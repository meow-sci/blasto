---
type: Mod Feature
title: Closed-form patched-conic transfer model
description: Hohmann transfers between sibling bodies with Oberth-combined ejection and capture, cached per unordered body pair for the life of a loaded system, plus comet, multiple-star, atmospheric ascent, landing, plane-change and transfer-time terms
mod: ksa-deltavmap
category: flight-and-orbits
techniques: [patched-conic-math, pure-math-kernel, per-system-cache]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Orbit
  - KSA.IOrbiter
  - KSA.IParentBody
  - KSA.Astronomical
  - KSA.AtmosphereReference
  - KSA.PhysicalAtmosphereReference
  - KSA.OrbitalTransfers
  - KSA.Universe
  - KSA.CelestialSystem
tags: [delta-v, hohmann, oberth, patched-conic, cache, orbital-mechanics, aerobraking, plane-change]
---

Every in-system number on the Delta-V Map comes from one small closed-form kernel. A transfer between two
bodies that share a hub (two planets of a star, two moons of a planet) is a Hohmann ellipse around
the hub; the burn at each end is then wrapped in an Oberth ejection or capture from that body's low
orbit. There is no iterative solver, so pricing a whole star system is cheap, and each pair is
computed once per loaded system.

The mechanism is a three-layer split: `DeltaVCalculator` is pure SI math with no game types,
`DvCache` turns two `IOrbiter`s into a direction-bearing pair of hub-frame legs (`EdgeDv`), and
`TransferBurns.ComputeLegs` turns those legs plus each end body's ladder into the two real burns.
Both the on-map badge and the route breakdown call that one function, so they never disagree.[^dvm-transferburns]

## How it works

### Formulas, as coded

All radii are from the body center, `mu = Mass * 6.6743e-11` (the same constant as the game's
`IParentBody.Mu`).[^dvm-deltavcalculator][^ksa-iparentbody]

| Term | Formula in the code | Method |
|---|---|---|
| Rendezvous radius | `e < 0.01 ? a : (Ap + Pe) / 2`; for an open orbit (`e >= 1`) the periapsis | `DeltaVCalculator.EffectiveRadius`, `OrbitalStates.TransferRadius` |
| Hohmann legs around the hub | `dvDepart = abs(sqrt(mu/r1) * (sqrt(2 r2/(r1+r2)) - 1))`, `dvArrive = abs(sqrt(mu/r2) * (1 - sqrt(2 r1/(r1+r2))))` | `Hohmann` |
| Oberth ejection or capture | `sqrt(vInf^2 + 2 mu/r_lo) - sqrt(mu/r_lo)` with `vInf` = the hub leg | `OberthBurn` |
| Comet (open) endpoint | `v_body = sqrt(mu (1 + e) / r_peri)` instead of the circular speed; `dv = abs(v_body - visviva(r, (rA + rB)/2))` | `ConicTransfer`, `PeriapsisSpeed` |
| Multiple-star pair | `v1 = sqrt(Orbit.Mu / r1)`, `v2 = sqrt(Orbit.Mu / r2)`, `dv = abs(v - visviva(muHub, r, a_t))` | `TransferBetweenSpeeds` |
| Transfer time | `pi * sqrt(a_t^3 / muHub)`, `a_t = (r1 + r2) / 2` | `TransferTimeSeconds` |
| Plane change | `2 v sin(di / 2)`, zero below 0.00873 rad (half a degree); `v` is the smaller `vInf` of the two ends | `PlaneChange` |
| Vacuum ascent | two impulses from rest at `R` to a circle at `r_lo` via the ellipse `a = (R + r_lo)/2` | `AscentVacuum` |
| Atmospheric ascent factor | `clamp(1 + 0.12 sqrt(rho/1.225) (1 + 2 H/R) + 0.05 sqrt(g/9.81), 1, 2)` with `g = mu/R^2` | `AtmosphericAscentFactor` |
| Landing fraction | `clamp(0.06 + 0.004 / (rho/1.225 + 0.01), 0.06, 0.35)` of the vacuum ascent, only when `rho > 0.01 kg/m^3` | `AtmosphericLandingFraction`, `OrbitalStates.ComputeDescent` |
| Raise to SOI edge / circularize from capture ellipse | `visviva(r_lo, (r_lo + r_soi)/2) - sqrt(mu/r_lo)` | `EscapeToSoi` |
| Low orbit to stationary | full circular-to-circular Hohmann | `CircularToCircular` |

The `0.01` circular threshold is copied from the game: `OrbitalTransfers.HohmannFlight` uses the
same `Eccentricity < 0.01 ? SemiMajorAxis : (Apoapsis + Periapsis) / 2` rule.[^ksa-orbitaltransfers]
The atmosphere numbers are absolute ISA references (1.225 kg/m^3, 9.81 m/s^2), not stock-body data,
so a custom system with a 1.225 kg/m^3 atmosphere behaves like Earth; the landing fraction comment
records the offline check against stock data (Earth about 510 m/s, Venus about 460, Mars about 775).[^dvm-deltavcalculator]

### Cache: one value per unordered body pair

`DvCache.GetTransfer(from, to)` keys a `Dictionary<(string, string), EdgeDv>` by the two body Ids in
ordinal order and returns `edge.Reversed()` (legs swapped) for the other direction. Keplerian
orbits of celestial bodies are fixed, so nothing is ever invalidated; body Ids are unique across all
star systems of one `CelestialSystem`, so one cache serves every system in it.[^dvm-dvcache]
`MapWindow.EnsureBuilt` replaces the cache with a new instance only when `Universe.CurrentSystem` is
a different object than the one the graph was built from (load or scene swap).[^dvm-mapwindow]

`Compute` picks one of three formulas by the endpoints' orbits:

1. Either orbit open (`Eccentricity >= 1`, a comet): `ConicTransfer`, matched at the true
   perihelion speed; flagged approximate.
2. Either orbit has its own central mass (`abs(Orbit.Mu - Parent.Mu) > 1e-9 * Parent.Mu`, the stars of
   a multiple system orbiting a barycenter): `TransferBetweenSpeeds`; flagged approximate.[^dvm-orbitalstates]
3. Otherwise the exact circular `Hohmann`, so stock planet-to-planet numbers are unchanged.

`Orbit.Mu` returns the orbit's own `_centralMass * G` when one is authored and `Parent.Mu`
otherwise, which is what makes the second test work.[^ksa-orbit] `DvCache` only serves sibling pairs and
throws `ArgumentException` when the two `Orbit.Parent`s differ. Parent-to-child legs (a planet to
its moon, a spine child down to the hub's own low orbit) are computed inline by
`VisualTree.DirectTransfer` with the same Hohmann or `ConicTransfer` choice.[^dvm-visualtree]

### From hub legs to real burns

An `EdgeDv` holds hub-frame legs only. `TransferBurns.ComputeLegs` derives the burns at
accumulation time from each end body's cached `BodyLadder` (mu, `r_lo`, `r_soi`):

- The end that is the hub itself (a parking orbit around the hub) pays the plain Hohmann leg.
- A child or sibling end pays `OberthBurn(ladder.Mu, ladder.LowOrbitRadius, vInf)`; an unknown body
  falls back to the raw leg, which is the correct `mu -> 0` limit.
- When the transfer lands on an `Intercept` anchor (a destination at full detail, entered from
  outside its SOI) the arrive burn is `max(0, OberthCapture - EscapeToSoi(mu, r_lo, r_soi))`: only
  the capture into a loose ellipse at the SOI edge. The circularize down to low orbit lives on the
  separate `Capture` ladder edge, so a route never charges it twice and the two still sum to one
  Oberth capture.[^dvm-transferburns][^dvm-visualtree]

The raw `vInf` values travel with the result (`DepartVinf`, `ArriveVinf`) because a plane change
is priced against the hyperbolic excess, not the hub orbital speed. The route code comments that
using the orbital speed was an earlier bug that overstated the cost about tenfold for Earth to
Mars.[^dvm-routeaccumulator]

### Where each term is attached

| Edge kind | Cost stored | Built by |
|---|---|---|
| `Transfer` | `EdgeDv` (two legs), `TransferTimeSeconds`, `IsApproximate`, optional `PlaneChangeDv` | `VisualTree.ConnectTransfer` |
| `Ascent` (low orbit to surface) | `LadderDv` = effective ascent, `DescentDv` = landing | `VisualTree.WireLadderEdges` |
| `Raise` | `CircularToCircular` or `EscapeToSoi` | same |
| `Capture` (Intercept to low orbit) | `EscapeToSoi` | same |
| `Land` (surface-only body) | `sqrt(mu / R)`, a rough "kill circular speed" | same |
| `HubLink`, `GroupLink`, `Interstellar`, `Approach` | nothing | structural |

The `Edge` comment spells out the rule: a ladder edge uses `LadderDv`, a transfer uses `Transfer`,
and the burns are derived per route rather than baked in.[^dvm-edge]

## Lifecycle and timing

Everything here runs on the main thread inside `MapWindow.DrawContent`, called from the mod's
`[StarMapAfterGui]` handler, which StarMap runs as a postfix on `Program.OnDrawUiViewports`
(`frame:draw-ui`), and only while the Delta-V Map window is shown and the HUD is visible.

| Step | When |
|---|---|
| New `DvCache` | `MapWindow.EnsureBuilt`, first draw after the `CelestialSystem` instance changes[^dvm-mapwindow] |
| Pair values filled | Lazily during `VisualTree.Build` in `MapWindow.RebuildAt` (a re-root, toggle or system change) and by the transfer-window list (see [transfer windows](./transfer-windows-and-phase-clock.md)) |
| `TransferBurns.ComputeLegs` | Per edge when the badge layout is built (`VisualTreeAdapter.BadgeDv`) and per step when a route is accumulated (`RouteAccumulator`)[^dvm-visualtreeadapter] |

State read: only celestial `Orbit` element getters (`Eccentricity`, `SemiMajorAxis`, `Apoapsis`,
`Periapsis`, `Mu`, `Parent`), `IParentBody.Mu`, `MeanRadius`, `SphereOfInfluence` and the
atmosphere template. These are the fixed Keplerian elements of on-rails bodies, so reading them
after `frame:dispatch-solvers` has started the next vehicle step is safe; no vessel state is
touched by this kernel. Nothing is written to the game.

## Key code

| Path | Role |
|---|---|
| [DeltaVMap/Dv/DeltaVCalculator.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/DeltaVCalculator.cs#L10-L207) | Pure closed-form math: Hohmann, Oberth, conic and own-mass transfers, ascent, landing, plane change, transfer time |
| [DeltaVMap/Dv/DvCache.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/DvCache.cs#L40-L119) | `EdgeDv` and the unordered-pair cache with the three-way formula choice |
| [DeltaVMap/Model/TransferBurns.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/TransferBurns.cs#L75-L137) | Hub legs to real burns: Oberth wrap, hub-end plain leg, Intercept split |
| [DeltaVMap/Dv/OrbitalStates.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/OrbitalStates.cs#L157-L169) | `TransferRadius` (open orbit uses periapsis), `HasOwnCentralMass`, `ComputeAscent`, `ComputeDescent` |
| [DeltaVMap/Model/VisualTree.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/VisualTree.cs#L459-L504) | Ladder edge costs; `SiblingPlaneChange` (L585-L594) and `DirectTransfer` (L684-L697) |
| [DeltaVMap/Model/Edge.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/Edge.cs#L26-L94) | `SegmentKind` and where each edge stores its cost |
| [DeltaVMap/Layout/VisualTreeAdapter.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/VisualTreeAdapter.cs#L189-L210) | Badge value from the same `TransferBurns.ComputeLegs` |
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L1742-L1757) | Per-system latch that creates the `DvCache` |

## Snippets

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

```csharp
// DeltaVMap/Dv/DvCache.cs L54-L68, L86-L110
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
    // ...
        double muHub = fromOrbit.Parent.Mu;
        double r1 = OrbitalStates.TransferRadius(fromOrbit);
        double r2 = OrbitalStates.TransferRadius(toOrbit);
        // ...
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
```

```csharp
// DeltaVMap/Model/TransferBurns.cs L101-L121
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
```

```csharp
// DeltaVMap/Dv/DeltaVCalculator.cs L71-L80, L98-L101
    public static double AtmosphericAscentFactor(double seaLevelDensity, double surfaceGravity, double atmosphereHeight, double rSurface)
    {
        double densityRatio = seaLevelDensity / StandardSeaLevelDensity;
        double gravityRatio = surfaceGravity / StandardSurfaceGravity;
        double atmoDepth = atmosphereHeight / rSurface;
        double factor = 1.0
            + 0.12 * Math.Sqrt(densityRatio) * (1.0 + 2.0 * atmoDepth)
            + 0.05 * Math.Sqrt(gravityRatio);
        return Math.Clamp(factor, 1.0, 2.0);
    }
    // ...
    public static double AtmosphericLandingFraction(double densityRatio)
    {
        return Math.Clamp(0.06 + 0.004 / (densityRatio + 0.01), 0.06, 0.35);
    }
```

## Game APIs touched

| API | Decompiled source | Used for |
|---|---|---|
| `Orbit.Eccentricity`, `Orbit.SemiMajorAxis`, `Orbit.Apoapsis`, `Orbit.Periapsis`, `Orbit.Parent` | [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs) | Rendezvous radius and the open-orbit test[^ksa-orbit] |
| `Orbit.Mu` | same | Own central mass of a multiple-star orbit (`_centralMass * G`, else `Parent.Mu`) |
| `Orbit.GetRelativeInclination` | same | Angle between two orbit normals for the plane change |
| `IParentBody.Mu`, `IParentBody.GetAtmosphereReference` | [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs) | Hub and body gravitational parameter, atmosphere lookup[^ksa-iparentbody] |
| `IOrbiter.Orbit`, `IObjectId.Id` | [current/decomp/KSA/IOrbiter.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IOrbiter.cs) | Cache key and orbit access[^ksa-iorbiter] |
| `PhysicalAtmosphereReference.SeaLevelDensity`, `.Height` | [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs) | Ascent factor, landing fraction, usable-atmosphere floor[^ksa-physicalatmosphere] |
| `OrbitalTransfers.HohmannFlight` (not called) | [current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs) | Source of the `e < 0.01` effective-radius rule the mod mirrors[^ksa-orbitaltransfers] |
| `Universe.CurrentSystem` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs) | Per-system cache latch by reference[^ksa-universe] |

## Pitfalls and version notes

- The cache is never invalidated within a loaded system. A mod that edits celestial orbits at
  runtime would leave stale numbers until the `CelestialSystem` instance changes.
- `DvCache.GetTransfer` throws for bodies with different `Orbit.Parent`; only pass siblings.
  `MapWindow` catches build exceptions per root, so a throw shows "unavailable" rather than
  corrupting the ImGui stack.[^dvm-dvcache][^dvm-mapwindow]
- The vacuum ascent starts from rest, so the surface rotation of the body is not credited; the
  atmospheric factor and landing fraction are empirical fits and every such edge is flagged
  approximate.
- Comet transfers use the perihelion as the rendezvous radius and are only estimates; the offline
  tests assert that a comet arrival costs more than the circular one and that the bound case equals
  plain Hohmann.[^dvm-tests]
- A multiple-star transfer is a two-impulse estimate between eccentric, mutually bound orbits and
  is flagged approximate.
- `Orbit.GetRelativeInclination` compares orbit normals in each orbit's own parent frame, so the
  mod only prices a plane change when both orbits share one `Parent` object.
- Checked against build 2026.10.10.5554; `IParentBody.Mu`, `Orbit.Mu` and the `HohmannFlight`
  threshold match the constants and rules the mod copies.

## See also

- [Per-body state ladder and "you are here"](./body-state-ladder-and-you-are-here.md)
- [Route finding and accumulation](./route-finding-and-accumulation.md)
- [Interstellar routes and fuel](./interstellar-routes-and-fuel.md)
- [Live celestial system graph](./live-celestial-system-graph.md)
- [Transfer windows and phase clock](./transfer-windows-and-phase-clock.md)
- [Subway-map canvas rendering](./subway-map-canvas-rendering.md)
- [Offline layout and math tests](./offline-layout-and-math-tests.md)
- [KSA-DeltaVMap overview](../overview.md)
- [Choose Orbit.Mu or Parent.Mu for transfer maths and cache per loaded system](../../../howto/flight-and-orbits/flight-state/read-orbit-elements-and-timing/read-orbit-elements-and-timing-3.md) - how-to article demonstrating this feature
- [Price transfers with hub Hohmann legs wrapped in Oberth burns, cached per body pair](../../../howto/flight-and-orbits/transfers/estimate-transfer-delta-v-with-patched-conics/estimate-transfer-delta-v-with-patched-conics-2.md) - how-to article demonstrating this feature

[^dvm-deltavcalculator]: DeltaVMap/Dv/DeltaVCalculator.cs
[^dvm-dvcache]: DeltaVMap/Dv/DvCache.cs
[^dvm-transferburns]: DeltaVMap/Model/TransferBurns.cs
[^dvm-orbitalstates]: DeltaVMap/Dv/OrbitalStates.cs
[^dvm-edge]: DeltaVMap/Model/Edge.cs
[^dvm-visualtree]: DeltaVMap/Model/VisualTree.cs
[^dvm-visualtreeadapter]: DeltaVMap/Layout/VisualTreeAdapter.cs
[^dvm-routeaccumulator]: DeltaVMap/Route/RouteAccumulator.cs
[^dvm-mapwindow]: DeltaVMap/Render/MapWindow.cs
[^dvm-tests]: DeltaVMap.Tests/Program.cs
[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-iorbiter]: KSA.IOrbiter (decompiled, build 2026.10.10.5554)
[^ksa-orbitaltransfers]: KSA.OrbitalTransfers (decompiled, build 2026.10.10.5554)
[^ksa-physicalatmosphere]: KSA.PhysicalAtmosphereReference (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
