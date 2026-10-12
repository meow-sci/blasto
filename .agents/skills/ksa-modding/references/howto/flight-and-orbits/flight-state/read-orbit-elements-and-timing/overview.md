---
type: Game Feature
title: Read orbit elements, apsides and event countdowns safely
description: Read a vehicle's or celestial's orbit elements, apsides, periods and event countdowns, and choose the right Mu for transfer maths, without tripping on hyperbolic orbits, NaN or UniverseTime sentinels
category: flight-and-orbits
group: flight-state
topic: read-orbit-elements-and-timing
demonstrated_by:
  - mods/kitten-engineer-redux/features/orbit-readouts-and-hohmann-estimate
  - mods/gatos/features/vessel-flight-state-reads
  - mods/ksa-deltavmap/features/patched-conic-transfer-model
lifecycle: [frame:draw-ui, frame:viewports]
game_types:
  - KSA.Orbit
  - KSA.Vehicle
  - KSA.TrueAnomaly
  - KSA.UniverseTime
  - KSA.IParentBody
  - KSA.Celestial
  - KSA.Astronomical
  - KSA.IOrbiter
  - KSA.CelestialSystem
  - KSA.AtmosphereReference
tags: [orbit, elements, apsides, period, mu, true-anomaly, sentinel, hyperbolic, countdown]
---

`Orbit` is the game's one class for every trajectory, from a circular parking orbit to a hyperbolic escape. Its
elements are cheap to read and easy to misuse: some values are radii, some angles are radians, apsides do not
exist for unbound orbits, countdowns can be sentinels, and the gravitational parameter you want depends on the
question. This topic collects the safe patterns. Physical data of bodies (radius, mass, SOI, atmosphere,
rotation) belongs to the sibling topic [Read a body's atmosphere, ocean, ring, rotation, tilt, tidal-lock and orbit-element data](../../../celestials/systems-and-stars/read-body-properties-and-orbit-elements/overview.md).

## Engine background

| Member | Meaning | Gotcha |
|---|---|---|
| `Orbit.SemiMajorAxis`, `Eccentricity`, `Inclination`, `LongitudeOfAscendingNode`, `ArgumentOfPeriapsis` | Elements in the parent's CCI frame | angles in radians; `Inclination` is measured against CCI +Z (see [Understand KSA coordinate frames and epochs (ECL, CCE, CCI, CCF, ego, body)](../understand-ksa-coordinate-frames-and-epochs/overview.md)) |
| `Periapsis`, `Apoapsis` | Radii from the parent's centre | subtract `Parent.MeanRadius` for altitude; for unbound orbits `Apoapsis` is `a(1+e)`, finite and negative |
| `ApoapsisAltitude`, `PeriapsisAltitude` | Radius minus `MeanRadius` | do not special-case unbound orbits |
| `Period` | Orbital period | only meaningful for bound orbits |
| `IsBound()` | `this is Bound` | the right test, not finiteness |
| `Mu` | `_centralMass * G`, else `Parent.Mu` | `Parent.Mu` is `Mass * 6.6743E-11`; `Orbit.Mu` differs for orbits around a barycenter |
| `StateVectors` | Position and velocity (CCI) plus `StateTime` | the sample's own timestamp |
| `GetRemainingTimeTo(TrueAnomaly)` | Time until the orbit next reaches the anomaly | relative to `StateVectors.StateTime` |
| `GetNextPeriapsisTime`, `GetNextApoapsisTime` | `UniverseTime?` absolute times | see below |
| `Vehicle.NextApoapsisTime`, `NextPeriapsisTime`, `NextPatchEventTime` | Cached per frame | `UniverseTime.EndOfTime` when absent |

Sources: [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1181-L1207), [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1858-L1861), [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L2063-L2089),
[current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L2171-L2188), [current/decomp/KSA/UniverseTime.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UniverseTime.cs#L132-L145), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2666-L2678).

Two facts that surprise: `Orbit.GetNextApoapsisTime` returns the passed-in time itself when `e >= 1` (not a sentinel) and
`GetNextPeriapsisTime` returns `TimeAtPeriapsis` (possibly in the past), so `Vehicle.NextApoapsisTime` for an escape
orbit is a finite time that means nothing; and `UniverseTime.EndOfTime` is `MaxValue`, which `Seconds()` turns into
a finite ~1.7e29. Test `IsSaturated()` or gate on `IsBound()` before using either.

**Timing.** Elements are set when `Universe.ApplyVehicleSolvers` applies the step (`frame:apply-solvers`) and the cached
countdown fields in `Vehicle.UpdatePerFrameData`; read them on the main thread afterwards ([lifecycle
reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)). Orbit elements of celestial bodies
do not change within a loaded system.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Readouts from state vectors in CCI](read-orbit-elements-and-timing-1.md) | Kitten-Engineer-Redux | flight math | `frame:draw-ui` | Countdowns, node times, eccentricity vector from first principles; assumes the orbit is bound |
| [Sentinel-safe orbit and encounter reads](read-orbit-elements-and-timing-2.md) | gatOS | immutable snapshot | `frame:viewports` | Never emits a sentinel or NaN; more helper code |
| [Orbit.Mu versus Parent.Mu and a per-system cache](read-orbit-elements-and-timing-3.md) | KSA-DeltaVMap | per-system cache | `frame:draw-ui` | Cheap whole-system pricing; cache must be keyed by the system object |

## See also

- [Read a vehicle's position, velocity, attitude, orbit and flight instruments](../read-vehicle-flight-state/overview.md)
- [Understand KSA coordinate frames and epochs (ECL, CCE, CCI, CCF, ego, body)](../understand-ksa-coordinate-frames-and-epochs/overview.md)
- [Plan and total multi-leg and interstellar delta-v routes](../../transfers/plan-multi-leg-and-interstellar-delta-v-routes/overview.md), [Estimate transfer delta-v in closed form with patched conics](../../transfers/estimate-transfer-delta-v-with-patched-conics/overview.md)
- Related topics: [Read a body's atmosphere, ocean, ring, rotation, tilt, tidal-lock and orbit-element data](../../../celestials/systems-and-stars/read-body-properties-and-orbit-elements/overview.md)
