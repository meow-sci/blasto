---
type: How-To
title: Equatorial versus ecliptic reference planes for inclination
description: Which plane the stock Orbit.Inclination is measured against, how to get the other plane's normal in CCI, and how a plane-change tool picks its reference
category: flight-and-orbits
group: flight-state
topic: understand-ksa-coordinate-frames-and-epochs
approach: 4
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/apse-and-inclination-quick-tools]
techniques: [custom-physics]
hooks: []
lifecycle: [frame:draw-ui]
game_types: [KSA.Orbit, KSA.IParentBody, KSA.Celestial, KSA.CelestialFrameMath]
tags: [frames, inclination, ecliptic, equatorial, cci, cce, plane-change]
---

Outcome: you can compute an orbit's inclination against either of the two planes that matter, the
parent's equator and the ecliptic, and you know which one `Orbit.Inclination` reports. KSA-AdvancedFlightComputer
builds its set-inclination tool on this distinction. [Set apse, match and set inclination with an ecliptic or equatorial reference](../../../../mods/ksa-advancedflightcomputer/features/apse-and-inclination-quick-tools.md)
No hooks are needed: it is pure math on `Orbit` and `IParentBody`.

## Background: how the game does it

Two normals are available as constants in the right frame:

- **CCI +Z** is the body's rotation axis, the pole of its equator. The game defines CCI that way
  (Z preserved from CCF, X at the vernal point).[^gatos-ksa-celestial-coordinate-frames]
- **CCE +Z** is the ecliptic normal: CCE shares ECL's axes. `IParentBody.GetCce2Cci()` rotates it into
  CCI.[current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L526-L529)

`Orbit.CreateFromStateCci` computes the stored inclination from the CCI angular momentum,
`acos(h.Z / |h|)`, so `Orbit.Inclination` is measured against CCI +Z, the **parent's equatorial
plane**.[^ksa-orbit] [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1611-L1623) The orbit normal in CCI is `Orbit.GetOrbitNormalCci()`
(`UnitZ` rotated by `Orb2ParentCci`), and `GetRelativeInclination(other)` is the angle between two
such normals; it does not check that both orbits share a parent frame.[current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1872-L1881)
The tilt is real: in `Content/Core/Astronomicals.xml` Earth's `<Rotation DefinitionFrame="Ecliptic">` carries
`<Tilt Degrees="23.441522" />`, which places the pole 23.4 degrees from the ecliptic normal (while Earth's own
orbit is inclined only 0.0045 degrees to the ecliptic), so the two planes differ by about 23.4 degrees for an Earth
orbit.[^ksa-astronomicals] [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L215-L230)

A caution about naming. AFC calls the CCI +Z reference `Ecliptic` and the CCE +Z reference
`Equatorial` (and its source comment says "CCI +Z is the ecliptic normal"). By the decompiled frame
definitions above the reverse is true for a vehicle orbiting a tilted body: the CCI +Z value is
the equatorial one and equals `Orbit.Inclination`, and the CCE +Z value is the ecliptic one. The math in
the mod is consistent with itself (the two enum values select the two normals); only the labels read
the wrong way round for a tilted parent, and the two coincide for a parent with zero tilt. Check the labels before you
show them to players.

## Lifecycle and timing

All of it runs on the main thread in the plan window's draw (`frame:draw-ui`) and only reads orbits;
nothing mutates the vehicle. Anything that does mutate (a new burn, a new target) is queued through an
`InputEvents` buffer and applied at `frame:input` the next frame; see
the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Choose the reference normal in CCI.** AFC's helper is the whole technique:

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

2. **Measure inclination against either plane** with `GetInclinationAgainst`, and compare with
   `Orbit.Inclination` to see which plane the stock value corresponds to.
3. **Find the node line** as `cross(referenceNormal, orbitNormal)` (CCI +X when the orbits are coplanar), take its
   true anomaly with `Orbit.GetTrueAnomaly(double3 vectorCci)` and its time with `Orbit.TimeOfTrueAnomaly(ta, now)`.
4. **Rotate the velocity at the node** about the node line by the difference between the current and the
   target normal. Apse burns use vis-viva at the opposite apse:

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

5. **Match another orbit's plane** with `Orbit.GetAscendingNode(target)` / `GetDescendingNode(target)` and
   `GetRelativeInclination(target)`; reject targets whose parent differs, because the relative
   inclination does not check parents.
6. **Return null for unbound orbits** (`e >= 1`): `Orbit.TimeOfTrueAnomaly` does not advance past
   node times for them.

## Complete example

- [AdvancedFlightComputer/Features/ManeuverTools/OrbitManeuvers.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/OrbitManeuvers.cs) holds all of the math;
  [AdvancedFlightComputer/Features/ManeuverTools/PlanningBasis.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/PlanningBasis.cs) picks the orbit after the last pending burn;
  the headless test [tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/SetInclinationTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/SetInclinationTest.cs) checks the result.
- The burn that results is created as a stock node; see [Create, chain and read stock maneuver nodes from code](../../autopilot/create-and-read-maneuver-nodes/overview.md).

## Variations and gotchas

- The two reference planes coincide for a parent with zero tilt (and for a star whose pole is the ecliptic
  normal), so a test on such a body hides a wrong reference.
- Do not confuse CCI of the *parent* with CCI of the *vehicle's* previous parent after an SOI change:
  recompute the normals from `orbit.Parent` of the orbit you are measuring.
- A fraction scales the rotation angle, which is how multi-pass splits a plane change across passes
  (see [Execute planned burns hands-free with the stock Auto burn](../../autopilot/execute-planned-burns-with-the-stock-autopilot/overview.md)).

## See also

- [Overview of frames and epochs](overview.md)
- [Vessel state in CCI/CCF/ECL](understand-ksa-coordinate-frames-and-epochs-1.md)
- [Set apse, match and set inclination with an ecliptic or equatorial reference](../../../../mods/ksa-advancedflightcomputer/features/apse-and-inclination-quick-tools.md)
- [Read orbit elements, apsides and event countdowns safely](../read-orbit-elements-and-timing/overview.md)

[^gatos-ksa-celestial-coordinate-frames]: gatOS: docs/KSA_CELESTIAL_COORDINATE_FRAMES.md
[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
[^ksa-astronomicals]: Game content Content/Core/Astronomicals.xml (build 2026.10.10.5554)
