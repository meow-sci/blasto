---
type: How-To
title: Compute set-apsis, circularize and plane-change impulses and plan them after the last pending burn
description: Closed-form impulses for raising or lowering one apse, circularizing and changing inclination, planned on the orbit left by the last pending burn so burns chain correctly
category: flight-and-orbits
group: autopilot
topic: create-and-read-maneuver-nodes
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/apse-and-inclination-quick-tools]
techniques: [custom-physics]
hooks: []
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Orbit
  - KSA.OrbitalTransfers
  - KSA.BurnPlan
  - KSA.Burn
  - KSA.FlightPlan
  - KSA.PatchedConic
  - KSA.Vehicle
tags: [maneuver-node, apoapsis, periapsis, circularize, plane-change, vis-viva, chaining]
---

Outcome: the delta-v and time for "set periapsis", "set apoapsis", "circularize", "set inclination" and "match plane", computed from the game's own `Orbit`
helpers so the result matches what the flight plan will predict, and planned on the right orbit when a burn is already pending.
KSA-AdvancedFlightComputer's quick tools are the demonstrating code.[Set apse, match and set inclination with an ecliptic or equatorial reference](../../../../mods/ksa-advancedflightcomputer/features/apse-and-inclination-quick-tools.md) Prerequisite: StarMap (any main-thread hook).

## Background: how the game does it

An apse burn is an impulse along the velocity at the opposite apse; a plane change rotates the velocity at a node. The game supplies the time and state helpers:
`Orbit.GetNextApoapsisTime`/`GetNextPeriapsisTime` return `UniverseTime?`; `Orbit.GetStateVectorsAt(time)`; `Orbit.GetTrueAnomaly(double3)` and
`TimeOfTrueAnomaly`; `OrbitalTransfers.DvCciToCircularize(orbit, time)` and `CciToVlf` to produce the VLF delta-v a `Burn` stores.[^ksa-orbit]
[current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L2171-L2188) [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1863-L1869) [^ksa-orbitaltransfers] [current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs#L892-L920) For unbound orbits (`e >= 1`) the apsis helpers
return the query time or `TimeAtPeriapsis`, so refuse them (see [Read orbit elements, apsides and event countdowns safely](../../flight-state/read-orbit-elements-and-timing/overview.md)). The inclination plane choices are explained in
[Understand KSA coordinate frames and epochs (ECL, CCE, CCI, CCF, ego, body)](../../flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md).

## Lifecycle and timing

All computation runs on the main thread inside the plan window's draw (`frame:draw-ui`) every frame the window is open, and only reads orbits and flight
plans. Anything that mutates the vehicle (a new burn, a new target) is queued into an `InputEvents` buffer and applied in the next frame's
`ApplyInputEvents` (`frame:input`); see [approach 1](create-and-read-maneuver-nodes-1.md) and the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Pick the planning basis.** If a nontrivial burn is pending, plan on patch 0 of that burn's own flight plan (the orbit after the burn), starting at its
   `ImpulseTime`; otherwise on the vehicle's current orbit. Keep the plan with its patch so preview and node creation use the same trajectory:

```csharp
// AdvancedFlightComputer/Features/ManeuverTools/PlanningBasis.cs L9-L28
    // Keep the plan with its patch so burn creation and preview use the same trajectory.
    public static PlanningBasis For(Vehicle source)
    {
        UniverseTime now = Universe.GetElapsedTime();

        Burn? finalBurn = source.FlightComputer.BurnPlan.FindFinalNontrivialBurn();
        if (finalBurn == null)
            return new PlanningBasis(source.Orbit, source.FlightPlan.TryFindPatch(now), now, false, null);

        FlightPlan plan = finalBurn.FlightPlan;
        if (plan.Patches.Count == 0)
            return new PlanningBasis(source.Orbit, source.FlightPlan.TryFindPatch(now), now, false, null);

        // Patch zero describes the orbit after the burn, while later patches describe SOI transitions.
        // It starts at the burn's impulse time, which for an interstellar burn lies half the burn after its start.
        PatchedConic patch = plan.Patches[0];
        return new PlanningBasis(patch.Orbit, patch, finalBurn.ImpulseTime, true, plan);
    }
}

```

2. **Set an apse.** Burn at the opposite apse; the apse radius is the burn radius, so vis-viva and the position agree:

```csharp
// AdvancedFlightComputer/Features/ManeuverTools/OrbitManeuvers.cs L28-L42
public static ManeuverResult? ComputeSetPeriapsis(
    Orbit orbit, double targetAltitudeMeters, double parentRadius, UniverseTime now)
{
    if (orbit.Eccentricity >= 1.0)
        return null;

    double currentApRadius = orbit.Apoapsis;
    double newPeRadius = targetAltitudeMeters + parentRadius;

    if (newPeRadius <= 0.0 || newPeRadius >= currentApRadius)
        return null;

    if (orbit.GetNextApoapsisTime(now) is not UniverseTime burnTime)
        return null;
    return ComputeApseBurn(orbit, burnTime, currentApRadius, newPeRadius);
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

3. **Circularize** with the game's own function and convert to VLF; skip when the eccentricity is below 0.001:

```csharp
// AdvancedFlightComputer/Features/ManeuverTools/OrbitManeuvers.cs L62-L82
// The 0.001 eccentricity tolerance matches CircularizeIntent.IsSatisfied and the UI.
public static ManeuverResult? ComputeCircularize(
    Orbit orbit, bool useApoapsis, UniverseTime now)
{
    if (orbit.Eccentricity >= 1.0)
        return null;
    if (orbit.Eccentricity < 0.001)
        return null;

    UniverseTime? apsisTime = useApoapsis
        ? orbit.GetNextApoapsisTime(now)
        : orbit.GetNextPeriapsisTime(now);
    if (apsisTime is not UniverseTime burnTime)
        return null;

    double3 dvCci = OrbitalTransfers.DvCciToCircularize(orbit, burnTime);
    if (dvCci.LengthSquared() < 1e-12)
        return null;

    double3 dvVlf = OrbitalTransfers.CciToVlf(orbit, burnTime, dvCci);
    return new ManeuverResult(dvCci, dvVlf, burnTime);
```

4. **Change the plane** by rotating the velocity at the node about the node line; preserve speed. The node direction is `cross(referenceNormal, orbitNormal)`,
   the target normal is the reference normal tilted by the target inclination about the node line, and a `fraction` scales the angle for partial changes
   (see [AdvancedFlightComputer/Features/ManeuverTools/OrbitManeuvers.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/OrbitManeuvers.cs#L121-L190)).
5. **Match another orbit's plane** with `Orbit.GetAscendingNode(target)` / `GetDescendingNode(target)`, rotation axis `cross(n_vehicle, n_target)` and angle
   `GetRelativeInclination(target)`; store targets by id (indices can move) and reject targets in a different parent frame.
6. **Hand the result on**: `ManeuverResult(DvCci, DvVlf, BurnTime)`; create the node as in [approach 1](create-and-read-maneuver-nodes-1.md).

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/ManeuverTools/OrbitManeuvers.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/OrbitManeuvers.cs) | all maneuver math |
| [AdvancedFlightComputer/Features/ManeuverTools/PlanningBasis.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/PlanningBasis.cs) | trajectory after the last pending burn |
| [AdvancedFlightComputer/Features/ManeuverTools/ManeuverToolsWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/ManeuverToolsWindow.cs) | inputs, reference-plane combo, node selection |
| [AdvancedFlightComputer/Features/ManeuverTools/TargetSelection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/TargetSelection.cs) | target list and id-based selection |
| [tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/SetInclinationTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/SetInclinationTest.cs) | headless test: the burn reaches the requested inclination |

## Variations and gotchas

- **Chaining is the point of the basis.** "Raise apoapsis, then set periapsis" only works if the second computation starts from the orbit after the first.
- **`Burn.Equals` compares time and delta-v**; do not use it to decide which pending burn is final.
- **Unbound orbits** return null: `Orbit.TimeOfTrueAnomaly` does not advance past node times for them.
- **Node choice.** The window defaults to the node with the lower delta-v (the lower orbital speed) and lets the player pick.
- A target change is queued through `InputEvents.ChangeTargetBuffer`, as stock does, because the workers are still running while the UI draws.

## See also

- [Overview](overview.md); [CalculateBurnPatch pass by pass](create-and-read-maneuver-nodes-4.md)
- [Set apse, match and set inclination with an ecliptic or equatorial reference](../../../../mods/ksa-advancedflightcomputer/features/apse-and-inclination-quick-tools.md)
- [Set or clear a vehicle's target, including bodies the game cannot target](../set-and-clear-the-vehicle-target/overview.md), [Understand KSA coordinate frames and epochs (ECL, CCE, CCI, CCF, ego, body)](../../flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md)

[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
[^ksa-orbitaltransfers]: KSA.OrbitalTransfers (decompiled, build 2026.10.10.5554)
