---
type: How-To
title: "Vessel state in CCI, CCF and ECL: which member is in which frame"
description: Read a vehicle's position, velocity and attitude in the frame each Vehicle member actually returns, convert a position to latitude, longitude and altitude, and go back from latitude and longitude to a position
category: flight-and-orbits
group: flight-state
topic: understand-ksa-coordinate-frames-and-epochs
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/gatos/features/vessel-flight-state-reads
  - mods/kitten-engineer-redux/features/orbit-readouts-and-hohmann-estimate
techniques: [immutable-snapshot, flight-math]
hooks: [harmony-prefix KSA.GameViewport.OnFrame, harmony-postfix KSA.Vehicle.OnDrawUi]
lifecycle: [frame:viewports, frame:draw-ui]
game_types: [KSA.Vehicle, KSA.IParentBody, KSA.Orbit, KSA.Celestial, KSA.Universe]
tags: [frames, cci, ccf, ecl, latitude-longitude, attitude, snapshot]
---

Outcome: you know, for every kinematic member of `Vehicle`, which frame it returns, and you can turn
a position into latitude, longitude and altitude (and back) with the game's own calls. gatOS reads
this set once per vessel into an immutable record; [Vessel flight-state reads](../../../../mods/gatos/features/vessel-flight-state-reads.md) Kitten-Engineer-Redux
derives surface velocity and lat/lon from the same members for its HUD. [Orbit readouts and Hohmann estimate](../../../../mods/kitten-engineer-redux/features/orbit-readouts-and-hohmann-estimate.md)
Prerequisite: a StarMap mod that can run code on the main thread (see the lifecycle reference).

## Background: how the game does it

A `Vehicle` stores one authoritative state: `Orbit.StateVectors` (position and velocity in the parent
body's **CCI**) and `Body2Cce` (attitude, body to **CCE**). Everything else is derived and cached.
The members you will use:

| Quantity | Member | Frame / unit |
|---|---|---|
| position | `Vehicle.GetPositionCci()` (returns `Orbit.GetPositionCci()` = `StateVectors.PositionCci`) | parent-centred inertial, m |
| velocity | `Vehicle.GetVelocityCci()` | parent-centred inertial, m/s |
| position, ecliptic | `Vehicle.GetPositionEcl()` (cached `_positionEcl`) | star-centred, m |
| velocity, ecliptic | `Vehicle.GetVelocityEcl()` | star-centred, m/s |
| attitude | `Vehicle.Body2Cce`; `Vehicle.GetBody2Cci()` | body to CCE; body to CCI |
| surface coordinates | `IParentBody.GetCci2Ccf()` then `IParentBody.GetLlaFromCcf(positionCcf)` | body-fixed; returns degrees, degrees, metres above `MeanRadius` |

`GetBody2Cci()` is just `Body2Cce` concatenated with `Orbit.Parent.GetCce2Cci()`, so the attitude
you read is CCE unless you convert it.[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3127-L3130)
`GetPositionCci()` and `GetVelocityCci()` read the orbit's state vectors without evaluating the
orbit again, so they are consistent with `GetPositionEcl()` only at the apply epoch (see
[Lifecycle and timing](#lifecycle-and-timing)).[current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2570-L2573)
[current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2622-L2625)
`GetLlaFromCcf` returns `(latitude deg, longitude deg, radius - MeanRadius)` and `double3.Zero` for a
near-zero vector.[^ksa-iparentbody] [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L67-L76) The third component is the height above the *mean*
radius, not above terrain: use `Vehicle.GetRadarAltitude()` or `GetBarometricAltitude()` for those
(see [Read a vehicle's position, velocity, attitude, orbit and flight instruments](../read-vehicle-flight-state/overview.md)).

## Lifecycle and timing

Read these members only after the solver results are applied: `Universe.ApplyVehicleSolvers` writes the
orbit state vectors and `Body2Cce` of the step that just finished, then `UpdatePerFrameData` refreshes the ECL
caches (see the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md),
`frame:apply-solvers`). Any main-thread hook after `PrepareFrame` therefore sees one consistent
instant, `Universe.GetElapsedTime()`. gatOS reads in a prefix on the main `GameViewport.OnFrame`
(`frame:viewports`) or from `StarMapBeforeGui`; Kitten-Engineer-Redux reads in a postfix on
`Vehicle.OnDrawUi` (`frame:draw-ui`). `Vehicle.NextApoapsisTime` and `NextPeriapsisTime` are cached in
`UpdatePerFrameData` from `Orbit.StateVectors.StateTime`, not from `Universe.GetElapsedTime()`, so a countdown
built from them can be off by up to one step.

## Steps

1. **Get the vehicle and its parent.** `Vehicle.Parent` is the `IParentBody` whose CCI frame all the
   vectors are in.
2. **Read position and velocity in CCI** with `GetPositionCci()` and `GetVelocityCci()`; read the
   attitude with `GetBody2Cci()` when you want it in the same inertial frame.
3. **Convert to the body-fixed frame and extract lat/lon.** gatOS does this in its core read:

```csharp
// gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs L82-L99
private static Basics ReadBasics(Vehicle vehicle, string? activeVesselId)
{
    var parent = vehicle.Parent; // IParentBody (=> Orbit.Parent)
    var positionCci = vehicle.GetPositionCci();

    // Lat/lon via the body's own frame math: CCI → CCF, then IParentBody.GetLlaFromCcf.
    double latitudeDeg = 0, longitudeDeg = 0;
    if (parent is not null)
    {
        // The static Transform avoids the extension-method overload set (which would drag
        // BepuUtilities into overload resolution — CS0012 without that reference).
        var positionCcf = double3.Transform(positionCci, parent.GetCci2Ccf());
        var lla = parent.GetLlaFromCcf(positionCcf);
        latitudeDeg = Sanitize.Finite(lla.X);
        longitudeDeg = Sanitize.Finite(lla.Y);
    }

    var attitude = vehicle.GetBody2Cci();
```

   The comment in the mod explains the choice of the static `double3.Transform`: it avoids dragging
   the BepuUtilities overload set into overload resolution (error CS0012 without that reference).
4. **Derive surface velocity** in CCI by subtracting the body's rotation, and split it along the local
   vertical. Kitten-Engineer-Redux does it in the same pass:

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

5. **Go the other way, from latitude and longitude to a position.** `Celestial` already has it:
   `GetDirCcfFromLatLon(lat, lon)` returns the unit direction in CCF and `GetPositionEclFromLatLon`
   scales it by `MeanRadius`, rotates it to CCE with `GetCcf2Cce()` and adds the body's ECL position,
   so the result rotates with the planet (it is body-fixed). Site and landing code (such as pins in
   [Live anchors in body, orbit, surface and part frames](../../../../mods/ksa-measuretools/features/live-body-relative-anchors.md)) uses this pair: [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L667-L683)
6. **Guard the edges** before publishing numbers: gatOS scrubs NaN and infinity, turns apsis radii
   into altitudes, and forces apoapsis to 0 when `Orbit.IsBound()` is false; see
   [Read orbit elements, apsides and event countdowns safely](../read-orbit-elements-and-timing/overview.md).

## Complete example

- gatOS: [Vessel flight-state reads](../../../../mods/gatos/features/vessel-flight-state-reads.md) points at
  [gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs#L82-L135) (`ReadBasics`) and
  [gatOS.SimFs/Telemetry/Sanitize.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Telemetry/Sanitize.cs#L20-L31).
- Kitten-Engineer-Redux: [OrbitSummary.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/OrbitSummary.cs#L32-L97) (`OrbitSummaryCalculator.Compute`).

## Variations and gotchas

- **Attitude is not CCI.** `Body2Cce` is the stored form; code that treats it as CCI is wrong by the
  parent's tilt. Use `GetBody2Cci()`.
- **CCF rotates.** `GetCci2Ccf()` is the cached rotation at the last apply epoch; use the
  `GetCci2Ccf(UniverseTime)` overload for another instant.
- **The height from `GetLlaFromCcf` is over the mean radius**, which includes neither terrain nor an
  ocean.
- **Order of Transform arguments.** `positionCci.Transform(parent.GetCci2Ccf())` rotates CCI into CCF;
  swapping the quaternion for its inverse silently gives plausible-looking but wrong longitudes.
- Version note: all members above exist in build 2026.10.10.5554. `Orbit` also exposes
  `ApoapsisAltitude` and `PeriapsisAltitude`, plain radius minus `MeanRadius`.

## See also

- [Overview of frames and epochs](overview.md)
- Sibling approaches: [ecl and ego](understand-ksa-coordinate-frames-and-epochs-2.md), [owner-frame anchors](understand-ksa-coordinate-frames-and-epochs-3.md), [reference planes](understand-ksa-coordinate-frames-and-epochs-4.md), [draw anchor](understand-ksa-coordinate-frames-and-epochs-5.md)
- [Read a vehicle's position, velocity, attitude, orbit and flight instruments](../read-vehicle-flight-state/overview.md), [Read orbit elements, apsides and event countdowns safely](../read-orbit-elements-and-timing/overview.md), [Read roll, pitch, heading, angle of attack and sideslip in the local horizon frame](../read-flight-state-in-a-local-horizon-frame/overview.md)

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
