---
type: How-To
title: Compute orbit readouts and event countdowns from state vectors in CCI
description: Derive apsis altitudes, period, time to apsides and nodes, the eccentricity vector and surface velocity components from Orbit and its CCI state vectors
category: flight-and-orbits
group: flight-state
topic: read-orbit-elements-and-timing
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/kitten-engineer-redux/features/orbit-readouts-and-hohmann-estimate]
techniques: [flight-math]
hooks: [harmony-postfix KSA.Vehicle.OnDrawUi]
lifecycle: [frame:draw-ui]
game_types: [KSA.Orbit, KSA.Vehicle, KSA.TrueAnomaly, KSA.UniverseTime, KSA.IParentBody, KSA.Universe]
tags: [orbit, apoapsis, periapsis, period, nodes, eccentricity-vector, countdown]
---

Outcome: one `OrbitSummary` record per frame with apoapsis and periapsis altitude, period, time to apoapsis,
periapsis and both nodes, the eccentricity vector, orbit normal and the vertical and horizontal parts of the surface
velocity. Kitten-Engineer-Redux builds it for its flight HUD.[Orbit readouts and Hohmann estimate](../../../../mods/kitten-engineer-redux/features/orbit-readouts-and-hohmann-estimate.md)
Prerequisites: a mod that runs main-thread code each frame; the math needs no patches.

## Background: how the game does it

`Vehicle.Orbit` is `Patch.Orbit`, the first patch of the flight plan; its `StateVectors` hold CCI position,
velocity and the `StateTime` they belong to. The next apsis times are cached per frame by
`Vehicle.UpdatePerFrameData` from `Orbit.GetNextApoapsisTime`/`GetNextPeriapsisTime`, and default to `UniverseTime.EndOfTime`
when the orbit never reaches them.[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2666-L2677)
`Orbit.GetRemainingTimeTo(TrueAnomaly)` converts an anomaly to mean anomaly and returns the time until the orbit next reaches it,
measured from the orbit's own `StateTime`.[^ksa-orbit] [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L2063-L2089)

## Lifecycle and timing

The mod computes the summary at the start of `FlightHud.Draw`, inside a postfix on `Vehicle.OnDrawUi`
(`frame:draw-ui`, main thread). `Universe.GetElapsedTime()` returns `_lastSimStep.NextTime`; the orbit's
`StateVectors.StateTime` is the time of the sample the orbit holds. The mod does not compare them, so a countdown built as
`NextApoapsisTime - GetElapsedTime()` can differ by up to one simulation step from the displayed state. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Test boundness first**: `orbit.Eccentricity < 1.0` (or `orbit.IsBound()`); apoapsis, period and node
   countdowns exist only for bound orbits.
2. **Altitudes and period.** Subtract `Parent.MeanRadius` from the radii and compute the period from `Orbit.Mu`; the
   game also provides `Orbit.Period`:

```csharp
// OrbitSummary.cs L32-L45
public static OrbitSummary Compute(Vehicle vehicle)
{
    Orbit orbit = vehicle.Orbit;
    bool isBound = orbit.Eccentricity < 1.0;
    double bodyRadius = orbit.Parent.MeanRadius;

    double? apoapsisAltitude = isBound ? orbit.Apoapsis - bodyRadius : (double?)null;
    double periapsisAltitude = orbit.Periapsis - bodyRadius;

    double? period = null;
    if (isBound && orbit.SemiMajorAxis > 0.0)
        period = 2.0 * Math.PI * Math.Sqrt(Math.Pow(orbit.SemiMajorAxis, 3.0) / orbit.Mu);

    UniverseTime now = Universe.GetElapsedTime();
```

3. **Countdowns.** Apsis times are absolute `UniverseTime`s: subtract the current time and call `Seconds()`. Node times
   use `GetRemainingTimeTo` with the true anomaly of the node, wrapped to 0..2pi, and are skipped for equatorial orbits:

```csharp
// OrbitSummary.cs L45-L56
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
```

4. **Eccentricity vector, orbit normal, surface velocity.** From the CCI state vectors, with the body's angular velocity
   removed for the surface part and the result split along the radial direction:

```csharp
// OrbitSummary.cs L58-L73
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

5. **Latitude and longitude** come from `Parent.GetLlaFromCcf(positionCci.Transform(Parent.GetCci2Ccf()))`, see
   [Understand KSA coordinate frames and epochs (ECL, CCE, CCI, CCF, ego, body)](../understand-ksa-coordinate-frames-and-epochs/overview.md).

## Complete example

[OrbitSummary.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/OrbitSummary.cs#L32-L97) (`OrbitSummaryCalculator.Compute`), drawn by [FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L291-L419).

## Variations and gotchas

- **Unbound orbits.** The mod nulls apoapsis and period when `e >= 1`, but its hyperbolic periapsis countdown
  still uses `NextPeriapsisTime`, which for an unbound orbit is `TimeAtPeriapsis` and may be in the past. Clamp to 0 or hide it.
- **`Orbit.Mu` versus `Parent.Mu`.** `Orbit.Mu` equals the parent's `Mu` unless the orbit has its own central mass
  (see [the Mu article](read-orbit-elements-and-timing-3.md)); the planner in the same mod computes `mu` from
  `Constants.GRAVITATIONAL_CONSTANT * Parent.Mass` instead.
- **Equatorial orbits** have an undefined node line; skip nodes when `|sin(i)|` is tiny.
- Elements are radians; `Inclination` is against the parent's CCI +Z.

## See also

- [Overview](overview.md); [sentinel-safe reads](read-orbit-elements-and-timing-2.md); [Mu and cache](read-orbit-elements-and-timing-3.md)
- [Orbit readouts and Hohmann estimate](../../../../mods/kitten-engineer-redux/features/orbit-readouts-and-hohmann-estimate.md)
- [Estimate transfer delta-v in closed form with patched conics](../../transfers/estimate-transfer-delta-v-with-patched-conics/overview.md) for the Hohmann estimate in the same mod

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
