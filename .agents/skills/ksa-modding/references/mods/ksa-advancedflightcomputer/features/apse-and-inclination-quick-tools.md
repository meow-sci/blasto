---
type: Mod Feature
title: Set apse, match and set inclination with an ecliptic or equatorial reference
description: Impulsive maneuver math on the game's Orbit API for set periapsis/apoapsis, circularize, match inclination and set inclination, planned on the trajectory after the last pending burn
mod: ksa-advancedflightcomputer
category: flight-and-orbits
techniques: [custom-physics]
hooks: []
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Orbit
  - KSA.OrbitalTransfers
  - KSA.Celestial
  - KSA.BurnPlan
  - KSA.FlightPlan
  - KSA.PatchedConic
  - KSA.InputEvents
tags: [maneuver-planning, orbital-mechanics, inclination, apsis, vis-viva, frames]
---

The quick-tools compute one impulsive burn each: raise or lower one apse to a target altitude, set an
absolute inclination, or match a target's orbital plane. Inclination can be measured against one of two
planes, which the window labels **Ecliptic** and **Equatorial**. The labels are the mod's and read the wrong way
round for a tilted parent: the game's `Orbit.Inclination` is `acos(h.Z / |h|)` of the angular momentum in the parent's
CCI frame, so it is measured against CCI +Z, the parent body's pole (its equatorial plane), not the ecliptic.
The mod's `Ecliptic` option uses CCI +Z (so it reproduces `Orbit.Inclination`), and its `Equatorial` option uses CCE +Z
(CCE shares ECL's axes, so that normal is the ecliptic pole) rotated into CCI; for Earth the two planes differ by about
23.4 degrees. The math is small and
uses the game's own orbit helpers, so the result matches what the flight plan will predict.

## How it works

### Apses (vis-viva at the opposite apse)

`ComputeSetPeriapsis` burns at the next apoapsis and `ComputeSetApoapsis` at the next periapsis
(`Orbit.GetNextApoapsisTime` / `GetNextPeriapsisTime`). `ComputeApseBurn` computes the new semi-major
axis from the burn radius and the requested opposite radius, the new speed from vis-viva
`v = sqrt(mu (2/r - 1/a))`, and the delta-v along the current velocity direction. The burn radius is
the apse radius itself, so vis-viva and the position radius agree. Circularize uses the game's own
`OrbitalTransfers.DvCciToCircularize` ([AdvancedFlightComputer/Features/ManeuverTools/OrbitManeuvers.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/OrbitManeuvers.cs#L28-L84),
[AdvancedFlightComputer/Features/ManeuverTools/OrbitManeuvers.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/OrbitManeuvers.cs#L191-L207)).

### Planes (rotate the velocity about the node line)

- **Reference normal.** AFC uses CCI +Z (the parent's pole, so really the equatorial normal, and the plane
  `Orbit.Inclination` is measured against) for its `Ecliptic` option and, for its `Equatorial` option,
  CCE +Z (the ecliptic pole) transformed to CCI with `orbit.Parent.GetCce2Cci()`
  ([AdvancedFlightComputer/Features/ManeuverTools/OrbitManeuvers.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/OrbitManeuvers.cs#L13-L26), [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L526-L529)).
- **Set inclination.** Node direction `cross(referenceNormal, orbitNormal)` (CCI +X when coplanar),
  true anomaly of that direction with `Orbit.GetTrueAnomaly`, time with `Orbit.TimeOfTrueAnomaly`;
  the target normal is the reference normal tilted by the target inclination about the node line, and
  the velocity at the node is rotated by the angle between the current and target normals. Speed and
  node line are preserved.
- **Match inclination.** Node with `Orbit.GetAscendingNode(target)` / `GetDescendingNode(target)`,
  rotation axis `cross(n_vehicle, n_target)`, angle `GetRelativeInclination(target)`.
- **Partial changes.** A `fraction` scales the rotation angle; multi-pass uses it to split a plane
  change across passes.
- **Node choice.** The window defaults to the node with the lower delta-v (the lower orbital speed)
  and lets the player pick.
- **Output.** `ManeuverResult(DvCci, DvVlf, BurnTime)`, with the VLF (velocity-local frame) components
  from `OrbitalTransfers.CciToVlf`, which is what a stock `Burn` stores.
- Unbound orbits (`e >= 1`) return null, because `Orbit.TimeOfTrueAnomaly` does not advance past node
  times for them.

### Planning after the last burn

`PlanningBasis.For(vehicle)` takes `BurnPlan.FindFinalNontrivialBurn()`; if one exists, it plans on
patch 0 of that burn's `FlightPlan` (the orbit after the burn), starting at the burn's `ImpulseTime`,
and keeps that plan so preview and node creation use the same trajectory. Otherwise it plans on the
vehicle's current orbit ([AdvancedFlightComputer/Features/ManeuverTools/PlanningBasis.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/PlanningBasis.cs)). So
"raise apoapsis, then Set Periapsis" chains correctly. Multi-pass refuses a chained basis.

### Target selection

Targets are stored by id, not by lookup index (`LookupCollection.Deregister` can move indices between
frames); targets in a different CCI frame are rejected because `Orbit.GetRelativeInclination` does not
check parents; a target change is queued through `InputEvents.ChangeTargetBuffer` as stock does,
because the vehicle workers are still running while the UI draws
([AdvancedFlightComputer/Features/ManeuverTools/TargetSelection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/TargetSelection.cs),
[AdvancedFlightComputer/Features/ManeuverTools/ManeuverToolsWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/ManeuverToolsWindow.cs#L413-L422)).

## Lifecycle and timing

All computation runs on the main thread inside the plan-window draw (`frame:draw-ui`) every frame the
window is open; it only reads orbits and flight plans. Anything that mutates the vehicle (new target,
new burn) is queued into an `InputEvents` buffer and applied in the next frame's
`InputEvents.ApplyInputEvents` (`frame:input`), outside the physics step.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/ManeuverTools/OrbitManeuvers.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/OrbitManeuvers.cs) | All maneuver math |
| [AdvancedFlightComputer/Features/ManeuverTools/PlanningBasis.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/PlanningBasis.cs) | Trajectory after the last pending burn |
| [AdvancedFlightComputer/Features/ManeuverTools/ManeuverToolsWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/ManeuverToolsWindow.cs) | Inputs, reference-plane combo, node selection |
| [AdvancedFlightComputer/Features/ManeuverTools/TargetSelection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/TargetSelection.cs) | Target list and id-based selection |
| [tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/SetInclinationTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/SetInclinationTest.cs) | Headless test: the burn reaches the requested inclination |

## Snippets

```csharp
// AdvancedFlightComputer/Features/ManeuverTools/OrbitManeuvers.cs L13-L26
    // CCI +Z is the ecliptic normal. The equatorial normal is CCE +Z transformed to CCI.
    public static double3 GetReferenceNormalCci(Orbit orbit, InclinationReference reference)
    {
        if (reference == InclinationReference.Ecliptic)
            return double3.UnitZ;
        return double3.UnitZ.Transform(orbit.Parent.GetCce2Cci());
    }

    public static double GetInclinationAgainst(Orbit orbit, InclinationReference reference)
    {
        double3 referenceNormal = GetReferenceNormalCci(orbit, reference);
        double3 orbitNormal = orbit.GetOrbitNormalCci();
        return MathEx.SafeAcos(double3.Dot(referenceNormal, orbitNormal));
    }
```

```csharp
// AdvancedFlightComputer/Features/ManeuverTools/OrbitManeuvers.cs L193-L207
    private static ManeuverResult? ComputeApseBurn(
        Orbit orbit, UniverseTime burnTime, double burnRadius, double oppositeRadius)
    {
        double newSma = (burnRadius + oppositeRadius) / 2.0;
        if (newSma <= 0.0)
            return null;

        StateVectors sv = orbit.GetStateVectorsAt(burnTime);
        double vNew = Math.Sqrt(orbit.Mu * (2.0 / burnRadius - 1.0 / newSma));
        double3 vDir = sv.VelocityCci.NormalizeOrZero();
        double3 dvCci = vDir * vNew - sv.VelocityCci;

        double3 dvVlf = OrbitalTransfers.CciToVlf(orbit, burnTime, dvCci);
        return new ManeuverResult(dvCci, dvVlf, burnTime);
    }
```

## Game APIs touched

| Member | Use |
|---|---|
| `Orbit.GetOrbitNormalCci`, `GetTrueAnomaly`, `TimeOfTrueAnomaly`, `GetStateVectorsAt`, `GetAscendingNode`, `GetRelativeInclination`, `GetNextApoapsisTime`, `Mu`, `Inclination` ([current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs)) | Orbit geometry and timing |
| `OrbitalTransfers.CciToVlf`, `DvCciToCircularize` ([current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs#L892-L906)) | Frame conversion and stock circularize |
| `Celestial.GetCce2Cci()` ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L526)) | Equatorial reference |
| `BurnPlan.FindFinalNontrivialBurn()` ([current/decomp/KSA/BurnPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnPlan.cs#L78)) | Chained planning |
| `InputEvents.ChangeTargetBuffer` ([current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs)) | Queued target change |

## Pitfalls and version notes

- The `Ecliptic` and `Equatorial` labels are swapped relative to the game's frames. `Orbit.Inclination` and
  `LongitudeOfAscendingNode` are measured against CCI +Z, the parent body's pole (`Orbit.CreateFromStateCci` uses
  `acos(h.Z / |h|)`), which is what the mod calls `Ecliptic` (the code comment `CCI +Z is the ecliptic normal` is
  wrong). The mod's `Equatorial` option, CCE +Z carried into CCI, is the true ecliptic pole. Both normals are
  applied consistently, so the burns are correct for the plane the player selects; only the names mislead, and the
  two coincide for a parent with no axial tilt.
- Rotating the velocity keeps its magnitude, so the plane change is "pure"; combine with apse changes
  as separate burns, or the plane-change delta-v is underestimated.
- For an interstellar burn, `ImpulseTime` lies half the burn after `Burn.Time`; plan from
  `ImpulseTime`.
- The coplanar fallback node (CCI +X) is arbitrary; the inclination change from 0 is well defined
  but the node line is a convention.

## See also

- [Transfer Planner custom plan types](transfer-planner-custom-plan-types.md)
- [Planner burn node creation](planner-burn-node-creation.md)
- [Multi-pass burn splitting](multi-pass-burn-splitting.md)
- [Orbit context menu shortcuts](orbit-context-menu-shortcuts.md)
- [Compute set-apsis, circularize and plane-change impulses and plan them after the last pending burn](../../../howto/flight-and-orbits/autopilot/create-and-read-maneuver-nodes/create-and-read-maneuver-nodes-3.md) - how-to article demonstrating this feature
- [Equatorial versus ecliptic reference planes for inclination](../../../howto/flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/understand-ksa-coordinate-frames-and-epochs-4.md) - how-to article demonstrating this feature

