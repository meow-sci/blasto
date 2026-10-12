---
type: Mod Feature
title: Orbit readouts and Hohmann estimate
description: Derives apsis altitudes, period, apsis and node countdowns, eccentricity vector, orbit normal, vertical and horizontal surface speed and latitude/longitude from the vehicle's Orbit, and estimates a coplanar Hohmann transfer to a target circular altitude
mod: kitten-engineer-redux
category: flight-and-orbits
techniques: [flight-math]
hooks: [harmony-postfix KSA.Vehicle.OnDrawUi]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Vehicle
  - KSA.Orbit
  - KSA.StateVectors
  - KSA.IParentBody
  - KSA.TrueAnomaly
  - KSA.UniverseTime
  - KSA.Universe
  - KSA.Constants
tags: [orbit, apoapsis, periapsis, nodes, eccentricity-vector, hohmann, surface-velocity, lat-lon, flight]
---

Four HUD sections (Orbit, Orbit elements, Orbit planner, Surface) and several tiles and alerts share
one `OrbitSummary` record computed per frame from the controlled vehicle's current `Orbit`. A
separate planner turns a typed target altitude into departure, arrival and total delta-v and a
transfer time for a two-burn Hohmann transfer. Everything is textbook two-body math on the game's
state vectors, plus a few game helpers for timing and body-fixed coordinates.

## How it works

`OrbitSummaryCalculator.Compute(vehicle)` ([OrbitSummary.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/OrbitSummary.cs#L32-L97)):[^ker-orbitsummary]

| Output | Computation |
|---|---|
| Apoapsis and periapsis altitude | `Orbit.Apoapsis` / `Periapsis` minus `Parent.MeanRadius`; apoapsis is null when `Eccentricity >= 1` |
| Period | `2 * pi * sqrt(a^3 / Orbit.Mu)` for bound orbits |
| Time to apoapsis and periapsis | `Vehicle.NextApoapsisTime` / `NextPeriapsisTime` minus `Universe.GetElapsedTime()`, as `UniverseTime.Seconds()` |
| Time to ascending and descending node | `Orbit.GetRemainingTimeTo(new TrueAnomaly(-omega))` and `(pi - omega)`, wrapped to 0..2pi, skipped for equatorial orbits |
| Eccentricity vector, orbit normal | `e = (v x h) / mu - r_hat`, `n = h / norm(h)` with `h = r x v` from `Orbit.StateVectors` (CCI) |
| Vertical and horizontal speed | surface velocity `v - (omega_body x r)` with `Parent.GetAngularVelocityCci()`, split along `r_hat` |
| Latitude, longitude | `Parent.GetLlaFromCcf(positionCci.Transform(Parent.GetCci2Ccf()))` (degrees) |
| Terrain and sea-level altitude | `Vehicle.GetRadarAltitude()`, `GetBarometricAltitude()` |
| Orbital speed, elements | `Vehicle.OrbitalSpeed`, `Orbit.Inclination`, `LongitudeOfAscendingNode`, `ArgumentOfPeriapsis`, `SemiMajorAxis` |

`OrbitPlanner.EstimateCircularTransfer(vehicle, targetAltitude)` ([OrbitPlanner.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/OrbitPlanner.cs#L18-L58)):[^ker-orbitplanner]

1. `r1` is the current radius (length of `PositionCci`), `r2 = MeanRadius + target`,
   `mu = Constants.GRAVITATIONAL_CONSTANT * Parent.Mass`.
2. Refuses hyperbolic orbits, negative targets and missing data; returns zero delta-v when
   `|r2 - r1| < 1 cm`.
3. Transfer orbit `a_t = (r1 + r2) / 2`; vis-viva speeds at both ends; departure and arrival delta-v
   are the differences from the circular speeds `sqrt(mu / r)`; transfer time is
   `pi * sqrt(a_t^3 / mu)`.
4. The status string states the assumptions ("Coplanar Hohmann estimate; assumes a circular starting
   orbit"), and the HUD shows it on every frame.

The target altitude is an `ImGui.InputDouble` (step 1 km, fast step 10 km) and is persisted
([FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L391-L408)).[^ker-flighthud]

## Lifecycle and timing

- Computed once per frame at the start of `FlightHud.Draw`, inside the `Vehicle.OnDrawUi` postfix
  (`frame:draw-ui`). The planner runs only when its section is drawn, because it is called from the
  section callback.
- `Vehicle.NextApoapsisTime` and `NextPeriapsisTime` are cached fields refreshed in
  `Vehicle.UpdatePerFrameData` from `Orbit.GetNextApoapsisTime` / `GetNextPeriapsisTime`
  ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2645-L2667)); they default to
  `UniverseTime.EndOfTime` when there is no next apsis.[^ksa-vehicle]
- `Universe.GetElapsedTime()` returns `_lastSimStep.NextTime`
  ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2399-L2402)). The mod does not
  compare it with `Orbit.StateVectors.StateTime`, so the countdowns may be offset by up to one
  simulation step relative to the displayed state (not measured).[^ksa-universe]

## Key code

| Path | Role |
|---|---|
| [OrbitSummary.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/OrbitSummary.cs) | `OrbitSummary` record and calculator |
| [OrbitPlanner.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/OrbitPlanner.cs) | Hohmann estimate |
| [FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L291-L419) | Orbit, elements, planner and surface sections |

## Snippets

```csharp
// OrbitSummary.cs L45-L73
        UniverseTime now = Universe.GetElapsedTime();
        double? timeToApoapsis = isBound ? (vehicle.NextApoapsisTime - now).Seconds() : null;
        double timeToPeriapsis = (vehicle.NextPeriapsisTime - now).Seconds();

        double? timeToAscendingNode = null;
        double? timeToDescendingNode = null;
        if (isBound && Math.Abs(Math.Sin(orbit.Inclination)) > 1e-8)
        {
            double argumentOfPeriapsis = orbit.ArgumentOfPeriapsis;
            timeToAscendingNode = orbit.GetRemainingTimeTo(new TrueAnomaly(WrapRadians(-argumentOfPeriapsis))).Seconds();
            timeToDescendingNode = orbit.GetRemainingTimeTo(new TrueAnomaly(WrapRadians(Math.PI - argumentOfPeriapsis))).Seconds();
        }

        double3 positionCci = orbit.StateVectors.PositionCci;
        double3 velocityCci = orbit.StateVectors.VelocityCci;
        double3 radial = positionCci.NormalizeOrZero();
        double3 angularMomentum = double3.Cross(positionCci, velocityCci);
        double3 eccentricityVector = orbit.Mu > 0.0
            ? double3.Cross(velocityCci, angularMomentum) / orbit.Mu - radial
            : double3.Zero;
        double3 orbitalNormal = angularMomentum.NormalizeOrZero();
        double3 angularVelocityCci = orbit.Parent.GetAngularVelocityCci();
        double3 surfaceVelocity = velocityCci - double3.Cross(angularVelocityCci, positionCci);
        double vertical = double3.Dot(surfaceVelocity, radial);
        double3 horizontalVector = surfaceVelocity - vertical * radial;
        double horizontal = horizontalVector.Length();

        double3 positionCcf = positionCci.Transform(vehicle.Parent.GetCci2Ccf());
        double3 lla = vehicle.Parent.GetLlaFromCcf(positionCcf);
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Orbit.StateVectors`, `Eccentricity`, `SemiMajorAxis`, `Inclination`, `LongitudeOfAscendingNode`, `ArgumentOfPeriapsis`, `Periapsis`, `Apoapsis`, `Mu`, `Parent` | [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1181-L1213) | Elements and CCI state |
| `Orbit.GetRemainingTimeTo(TrueAnomaly)` | [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L2063) | Node countdowns |
| `Vehicle.NextApoapsisTime`, `NextPeriapsisTime`, `OrbitalSpeed` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L418-L420) | Apsis countdowns |
| `Universe.GetElapsedTime()`, `UniverseTime.Seconds()`, `operator -` | [current/decomp/KSA/UniverseTime.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UniverseTime.cs#L95-L187) | Simulation clock arithmetic |
| `IParentBody.GetAngularVelocityCci()`, `GetCci2Ccf()`, `GetLlaFromCcf(double3)` | [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L41-L87) | Surface velocity and lat/lon |
| `Constants.GRAVITATIONAL_CONSTANT` | [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L25) | Planner `mu` |

## Pitfalls and version notes

- The planner computes `mu` from `Parent.Mass` while the summary uses `Orbit.Mu`, which differs when
  the orbit was created with an explicit central mass (`Orbit.Mu` falls back to `Parent.Mu` only when
  `_centralMass` is NaN).[^ksa-orbit]
- The planner's departure delta-v is measured against the circular speed at the current radius, not
  the craft's actual speed, so it is only meaningful from a near-circular orbit; the status string
  says so.
- `GetLlaFromCcf` returns latitude and longitude in degrees and altitude above mean radius in the
  third component; the mod uses only the first two.
- Time to periapsis is not guarded for open orbits: for `Eccentricity >= 1`, `Orbit.GetNextPeriapsisTime`
  returns `TimeAtPeriapsis` even after it has passed, so after a flyby the countdown goes negative
  (shown with a leading "-"), and an enabled "Time to periapsis <" alert stays triggered
  ([current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L2171-L2178)).[^ksa-orbit]

## See also

- [Flight HUD on the controlled vehicle](flight-hud-controlled-vehicle-postfix.md)
- [Suicide-burn calculator](suicide-burn-calculator.md)
- [HUD stat tiles and threshold alerts](hud-widgets-and-threshold-alerts.md)
- [Compute orbit readouts and event countdowns from state vectors in CCI](../../../howto/flight-and-orbits/flight-state/read-orbit-elements-and-timing/read-orbit-elements-and-timing-1.md) - how-to article demonstrating this feature
- [Vessel state in CCI, CCF and ECL: which member is in which frame](../../../howto/flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/understand-ksa-coordinate-frames-and-epochs-1.md) - how-to article demonstrating this feature
- [Read orbit-relative surface velocity as radial and tangential parts](../../../howto/flight-and-orbits/transfers/build-a-site-frame-and-compute-a-launch-window/build-a-site-frame-and-compute-a-launch-window-4.md) - how-to article demonstrating this feature
- [Estimate a coplanar Hohmann transfer to a circular altitude from the current orbit](../../../howto/flight-and-orbits/transfers/estimate-transfer-delta-v-with-patched-conics/estimate-transfer-delta-v-with-patched-conics-1.md) - how-to article demonstrating this feature

[^ker-orbitsummary]: OrbitSummary.cs
[^ker-orbitplanner]: OrbitPlanner.cs
[^ker-flighthud]: FlightHud.cs
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
