---
type: How-To
title: Read orbit-relative surface velocity as radial and tangential parts
description: Compute surface velocity as v minus omega cross r in the CCI frame, split it along the radial direction, and derive latitude and longitude, for a flight HUD readout
category: flight-and-orbits
group: transfers
topic: build-a-site-frame-and-compute-a-launch-window
approach: 4
difficulty: beginner
prerequisites: [starmap, harmony]
demonstrated_by: [mods/kitten-engineer-redux/features/orbit-readouts-and-hohmann-estimate]
techniques: [flight-math]
hooks: [harmony-postfix KSA.Vehicle.OnDrawUi]
lifecycle: [frame:draw-ui]
game_types: [KSA.Orbit, KSA.IParentBody, KSA.Vehicle, KSA.StateVectors]
tags: [surface-velocity, vertical-speed, horizontal-speed, rotation, cci, latitude-longitude]
---

Outcome: vertical and horizontal speed over a rotating surface, in a few lines. Kitten-Engineer-Redux computes them for its Surface HUD section.[Orbit readouts and Hohmann estimate](../../../../mods/kitten-engineer-redux/features/orbit-readouts-and-hohmann-estimate.md) Prerequisite: a draw hook.

## Background: how the game does it

Surface velocity is `v - omega x r` in CCI, where `omega = Parent.GetAngularVelocityCci()` (the spin axis is CCI +Z). The game computes the same quantity in `Vehicle.GetSurfaceVelocityCci()`/`GetSurfaceSpeed()`; the mod recomputes it from `Orbit.StateVectors` so the radial split uses the same vectors.[^ksa-iparentbody] [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L84-L90) [^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2954-L2963)

## Lifecycle and timing

Computed once per frame at the start of `FlightHud.Draw`, inside the `Vehicle.OnDrawUi` postfix (`frame:draw-ui`, main thread), from the state applied in `frame:apply-solvers`. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Take `positionCci` and `velocityCci`** from `orbit.StateVectors`.
2. **Subtract the rotation:** `surfaceVelocity = velocityCci - cross(angularVelocityCci, positionCci)`.
3. **Split along the radial unit vector:** `vertical = dot(surfaceVelocity, radial)`, `horizontal = |surfaceVelocity - vertical * radial|`:

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

4. **Add the geodetic position:** `lla = Parent.GetLlaFromCcf(positionCci.Transform(Parent.GetCci2Ccf()))`, degrees and metres over the mean radius (see [Understand KSA coordinate frames and epochs (ECL, CCE, CCI, CCF, ego, body)](../../flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md)).
5. **Show orbital and surface speed apart:** `Vehicle.OrbitalSpeed` is the inertial speed, the surface speed is this value's length; they differ by the planet's rotation (about 465 m/s at an equatorial pad on Earth).

## Complete example

[OrbitSummary.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/OrbitSummary.cs#L32-L97) (`OrbitSummaryCalculator.Compute`) and the Surface section in [FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L291-L419).

## Variations and gotchas

- **The radial direction is geocentric,** not the local gravity direction on an oblate body; for a sphere they agree.
- **Horizontal speed is the full tangential magnitude,** including the cross-track part; split with a local frame (see approach 2/3) if you need along- and cross-track.
- At the very centre `positionCci` is zero; guard the normalisation (`NormalizeOrZero`).
- Latitude and longitude are in degrees; altitude from `GetLlaFromCcf` is over the mean radius.

## See also

- [Overview](overview.md); [site-frame bridge](build-a-site-frame-and-compute-a-launch-window-2.md)
- [Orbit readouts and Hohmann estimate](../../../../mods/kitten-engineer-redux/features/orbit-readouts-and-hohmann-estimate.md)
- [Read a vehicle's position, velocity, attitude, orbit and flight instruments](../../flight-state/read-vehicle-flight-state/overview.md)

[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
