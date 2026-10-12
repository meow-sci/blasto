---
type: How-To
title: Build a per-body state ladder and classify the vessel onto it with a snapped eccentricity rule
description: Derive surface, low orbit, stationary and SOI-edge states from live celestial data, then classify the controlled vessel's orbit onto its parent's ladder using HasMeaningfulOrbit and eccentricity snapping
category: flight-and-orbits
group: transfers
topic: plan-multi-leg-and-interstellar-delta-v-routes
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-deltavmap/features/body-state-ladder-and-you-are-here]
techniques: [vehicle-state-classification]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Vehicle
  - KSA.Orbit
  - KSA.IParentBody
  - KSA.AtmosphericBody
  - KSA.Astronomical
  - KSA.AstronomicalTemplate
  - KSA.PhysicalAtmosphereReference
tags: [classification, ladder, low-orbit, parking-orbit, stationary-orbit, you-are-here, eccentricity]
---

Outcome: every body gets a short ladder of states you can be in (surface, low parking orbit, stationary orbit, ellipse to the SOI edge, and an Intercept node for a destination), computed from live celestial data so modded systems need no per-body config, and the vessel's orbit is mapped onto its parent's ladder as the "you are here" node every route starts from.
KSA-DeltaVMap implements it.[Per-body state ladder and "you are here"](../../../../mods/ksa-deltavmap/features/body-state-ladder-and-you-are-here.md) Prerequisite: StarMap for a draw hook.

## Background: how the game does it

Inputs are game members: `IParentBody.MeanRadius`, `Mu`, `SphereOfInfluence`, `GetAtmosphereReference().Physical.Height`, `GetNearSurfaceRadius()` (for an airless body it is `MaxTerrainRadius + 1`; `AtmosphericBody` returns `MeanRadius + atmosphere height + 1` with an atmosphere), the rotation rate for the stationary radius,
and for the vessel `HasMeaningfulOrbit` (the game's definition is `!Situation.HasAnyContact()`), `Orbit.Eccentricity`, `SemiMajorAxis` and `Apoapsis`.[^ksa-astronomical] [^ksa-atmosphericbody] [^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L551) The game has no gas-giant flag, so the mod drops the surface rung for any body with an atmosphere and a radius of at least 15,000 km.

## Lifecycle and timing

Ladders are built once per loaded system (from `MapWindow.EnsureBuilt` on the first draw after the `CelestialSystem` instance changes). The classifier is **not** run every frame; it runs in `RebuildAt` on the first build, a root change, a vehicle switch, a crossing into another star system, and layout/detail/visibility toggles. It reads `Vehicle.HasMeaningfulOrbit`, `Vehicle.Orbit` (`Patch.Orbit`) and `GetPositionEcl()`: main-thread fields
swapped in by `ApplyVehicleSolvers` at `frame:apply-solvers`, so a draw sees the state applied at the start of this frame. The classification and the parent lookup are wrapped in `try` because a freshly spawned vessel can throw while its patch chain is not materialized. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Low orbit heuristic** ("about 10 km above the atmosphere or the highest terrain", never below `R + 5 km`):

```csharp
// DeltaVMap/Dv/LowOrbitHeuristic.cs L14-L36

    public static double Compute(double meanRadius, bool hasAtmosphere, double atmosphereHeight, double nearSurfaceRadius)
    {
        double rLo;
        if (hasAtmosphere)
        {
            // About 10 km above the atmosphere boundary (or 10% of its height,
            // whichever is larger, for very tall atmospheres).
            rLo = meanRadius + atmosphereHeight + Math.Max(ClearanceMeters, 0.1 * atmosphereHeight);
        }
        else
        {
            // About 10 km above the highest terrain, but never below 5% of the
            // body radius. The 5% term is a sane minimum parking altitude that
            // wins for larger airless bodies (e.g. Luna lands at ~87 km, not the
            // ~20 km the bare 10 km clearance would give), leaving margin over
            // unmodelled terrain.
            rLo = Math.Max(nearSurfaceRadius + ClearanceMeters, meanRadius * 1.05);
        }

        return Math.Max(rLo, meanRadius + AbsoluteFloorMeters);
    }
}
```

2. **Rungs, bottom to top:** `Surface` (`MeanRadius`; present when airless, or with an atmosphere and `MeanRadius < 15,000 km`), `LowOrbit` (present when the SOI is above the mean radius), `Stationary` (`cbrt(mu T^2 / (4 pi^2))` with `T = 2 pi / abs(omega)`, when inside the SOI and above `r_lo`), `SoiEdge` (a finite SOI). Two guards keep tiny moons ordered: SOI at or below the mean radius means a surface-only flyby
   destination, and a heuristic low orbit above the SOI is clamped to `(MeanRadius + SOI) / 2`.
3. **Hub ladders** for a star or barycenter: no surface, one "Parking Orbit" at `max(min(1 AU, 0.5 SOI), 2 R)`; the same function sizes the interstellar capture so the map and the interstellar estimate agree.
4. **Classify the vessel** with `HasMeaningfulOrbit` first, then eccentricity snapping before the SOI-edge test (so a genuine parking orbit on a tight-SOI body still reads as low orbit):

```csharp
// DeltaVMap/Dv/StateClassifier.cs L41-L75
public static ClassifiedState Classify(Vehicle vehicle, BodyLadder ladder)
{
    if (!vehicle.HasMeaningfulOrbit)
        return new ClassifiedState(StateKind.Surface, ladder.MeanRadius);

    Orbit orbit = vehicle.Orbit;
    double eccentricity = orbit.Eccentricity;
    double semiMajorAxis = orbit.SemiMajorAxis;
    double apoapsis = orbit.Apoapsis;

    // An escape trajectory is leaving through the SOI boundary.
    if (eccentricity >= 1.0)
        return new ClassifiedState(StateKind.SoiEdge, ladder.SoiRadius ?? apoapsis);

    // Snap a near-circular orbit onto a specific rung first. This has to come
    // before the fractional SOI-edge test below, because on a tight-SOI body
    // the low orbit can sit above half the SOI, and a genuine parking orbit
    // there must still read as LowOrbit, not SoiEdge.
    if (eccentricity < NearCircularEccentricity)
    {
        if (IsNear(semiMajorAxis, ladder.LowOrbitRadius))
            return new ClassifiedState(StateKind.LowOrbit, ladder.LowOrbitRadius);

        if (ladder.StationaryRadius.HasValue && IsNear(semiMajorAxis, ladder.StationaryRadius.Value))
            return new ClassifiedState(StateKind.Stationary, ladder.StationaryRadius.Value);
    }

    // Otherwise, an orbit reaching well out toward the SOI is on the
    // elliptical-to-SOI-edge rung.
    if (ladder.SoiRadius.HasValue && apoapsis > SoiEdgeFraction * ladder.SoiRadius.Value)
        return new ClassifiedState(StateKind.SoiEdge, ladder.SoiRadius.Value);

    // Between rungs: the "medium orbit" case, placed at its actual SMA.
    return new ClassifiedState(StateKind.YouAreHere, semiMajorAxis);
}
```

5. **Classify a vessel orbiting a star** as always its own "you are here" node, at the SMA on a bound orbit, else at its current distance with `IsOpenCruise` set so the router does not treat that distance as an orbit to transfer from:

```csharp
// DeltaVMap/Dv/StateClassifier.cs L82-L89
public static ClassifiedState ClassifyCruise(Vehicle vehicle)
{
    Orbit orbit = vehicle.Orbit;
    if (orbit.Eccentricity < 1.0 && orbit.SemiMajorAxis > 0.0)
        return new ClassifiedState(StateKind.YouAreHere, orbit.SemiMajorAxis);
    double distance = (vehicle.GetPositionEcl() - vehicle.Parent.GetPositionEcl()).Length();
    return new ClassifiedState(StateKind.YouAreHere, distance, isOpenCruise: true);
}
```

6. **Insert the node.** Either flag the snapped rung or insert a new node raised off the low orbit with a circular-to-circular cost; keep node ids stable (`"<body>.<state>"`) so selection survives rebuilds.
7. **Pick detail per tree:** Full detail for the root body, its moons, spine hubs and the nearest siblings; Core (surface and low orbit) elsewhere.

## Complete example

| File | Role |
|---|---|
| [DeltaVMap/Dv/OrbitalStates.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/OrbitalStates.cs#L85-L279) | `StateKind`, `BodyLadder`, gas-giant test, stationary radius, `BuildLadder`, `BuildHubLadder` |
| [DeltaVMap/Dv/LowOrbitHeuristic.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/LowOrbitHeuristic.cs#L10-L36) | `r_lo` rule |
| [DeltaVMap/Dv/StateClassifier.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/StateClassifier.cs#L28-L95) | `Classify` and `ClassifyCruise` |
| [DeltaVMap/Model/DetailLevel.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/DetailLevel.cs#L20-L65) | Full vs Core bodies and rung filter |
| [DeltaVMap/Model/VisualTree.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/VisualTree.cs#L383-L454) | rung nodes, arrival anchor, `InsertYouAreHere` |

## Variations and gotchas

- **Order of tests matters.** Snap near-circular orbits (eccentricity under 0.05, SMA within 5% of a rung) before the half-SOI test, or tight-SOI bodies misclassify.
- **Unbound orbits are `SoiEdge`**; do not read `Apoapsis` for them.
- **Re-rooted maps have no "you are here"**: the vessel is classified only when its parent is the map root.
- The atmosphere test uses `SeaLevelDensity > 0.01 kg/m^3` for "usable".

## See also

- [Overview](overview.md); [route finding](plan-multi-leg-and-interstellar-delta-v-routes-1.md); [interstellar legs](plan-multi-leg-and-interstellar-delta-v-routes-3.md)
- [Per-body state ladder and "you are here"](../../../../mods/ksa-deltavmap/features/body-state-ladder-and-you-are-here.md)
- [Read orbit elements, apsides and event countdowns safely](../../flight-state/read-orbit-elements-and-timing/overview.md)

[^ksa-astronomical]: KSA.Astronomical (decompiled, build 2026.10.10.5554)
[^ksa-atmosphericbody]: KSA.AtmosphericBody (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
