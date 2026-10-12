---
type: How-To
title: Read altitude, speed, vertical speed, g-load and Mach in a Harmony postfix for an instrument panel
description: Read a settled vehicle inside a postfix on Program.OnFrameEditor to feed a HUD, and derive barometric versus radar altitude, surface speed, vertical speed, g-load and Mach correctly
category: flight-and-orbits
group: flight-state
topic: read-vehicle-flight-state
approach: 3
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-aircrafthud/features/flight-data-from-vehicle]
techniques: [harmony-postfix, flight-math]
hooks: [harmony-postfix KSA.Program.OnFrameEditor]
lifecycle: [frame:viewports]
game_types: [KSA.Vehicle, KSA.Program, KSA.Orbit, KSA.PhysicalAtmosphereReference, KSA.Celestial]
tags: [altitude, radar-altitude, speed, vertical-speed, g-load, mach, hud, harmony]
---

Outcome: the instrument values an aircraft-style HUD needs, computed each frame from the controlled
vehicle: altitude (barometric, switching to radar near the ground), surface speed, vertical speed, g-load,
Mach and the orbit readouts. KSA_AircraftHUD does this inside one Harmony postfix;[Surface attitude and air data from the controlled vehicle](../../../../mods/ksa-aircrafthud/features/flight-data-from-vehicle.md)
this article gives the formulas and, importantly, the places its code and a straightforward reading of the
decomp disagree. Prerequisites: StarMap and Harmony.

## Background: how the game does it

- `GetBarometricAltitude()` is `|PositionCci| - Parent.MeanRadius`. `GetRadarAltitude()` subtracts the terrain
  height under the vehicle (and the larger of terrain and ocean height on a body with an ocean), and falls
  back to the barometric value when the parent is not a `Celestial`.[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3060-L3088)
- `GetSurfaceSpeed()` is the length of `GetSurfaceVelocityCci()` = `VelocityCci - (parent angular velocity x
  PositionCci)`, the velocity relative to the rotating surface.[current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2949-L2963)
- `Vehicle.AccelerationBody` is the *proper* acceleration in body axes (thrust, drag and so on; gravity is
  not in it), taken from `KinematicMeasurements`. The integrator stores `ForceBody / mass` in it while gravity
  is added separately.[current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L573) [^ksa-physicsstates] [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1130-L1141)
- The atmosphere is one exponential per body for pressure and density
  (`PhysicalAtmosphereReference.GetAtmosphericPressureAtAltitude`, `GetAtmosphericDensityAtAltitude`), so the speed of
  sound is constant per body (see [the Mach article](read-vehicle-flight-state-4.md)).[^ksa-physicalatmospherereference] [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L80-L88)

## Lifecycle and timing

The mod patches `Program.OnFrameEditor` with a postfix (`frame:viewports`, main thread). Before that point
`PrepareFrame` has applied the finished vehicle step and dispatched the next one, so the main-thread `Vehicle`
holds the step applied this frame while the workers use copies; reads are consistent, writes would race and
be overwritten. The values lag the simulation by the step in flight, exactly like every other UI in the
frame. `OnFrameEditor` also runs with the HUD hidden, but only after `PrepareFrame` returned a continue
value. The practical difference from the [after-frame snapshot](read-vehicle-flight-state-2.md) is
not staleness (both read the same step) but that a postfix here can draw this frame. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Patch the method.**

```csharp
// AircraftHUDMain.cs L1332-L1341
[HarmonyPatch(typeof(Program))]
internal static class ProgramPatches
{
    [HarmonyPatch("OnFrameEditor")]
    [HarmonyPostfix]
    private static void AfterOnFrameViewports(double dtPlayer)
    {
        HUD.Instance?.DrawHud(dtPlayer);
    }
}
```

2. **Fetch the vehicle and guard its parent.** The mod casts `Orbit.Parent` to `Celestial`; stars implement
   `IParentBody` without deriving from `Celestial`, so around a star that cast throws every frame. Use
   `Orbit.Parent as Celestial` and return on null.[^ksa-stellarbody] [current/decomp/KSA/StellarBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StellarBody.cs#L11)
3. **Altitude.** Barometric for the main box; below 50 km also read radar altitude:

```csharp
// AircraftHUDMain.cs L1042-L1049

// ALTITUDE (MSL / RADAR)
bool useRadarAlt = false;
float altitudeRadar = 0f;
if (altitude < 50000f)
{
    useRadarAlt = true;
    altitudeRadar = ConvertToDistanceUnits((float)controlledVehicle.GetRadarAltitude());
```

4. **Vertical speed** is the CCI velocity projected on the radial direction, and **surface speed** is
   `GetSurfaceSpeed()`:

```csharp
// AircraftHUDMain.cs L220-L225
public static double GetVerticalVelocity(Vehicle vehicle)
{
    double3 positionCci = vehicle.GetPositionCci();
    double3 vector = positionCci.Normalized();
    return double3.Dot(vehicle.GetVelocityCci(), vector);
}
```

5. **G-load.** Use the proper acceleration directly. The game's own debug readout shows
   `AccelerationBody.Length() / 9.80665`. The mod subtracts a computed local gravity first, which reads about
   0 G standing on the ground (true value 1 G) and about minus one local g coasting in orbit (true value 0 G):

```csharp
// AircraftHUDMain.cs L1071-L1073
float verticalVelocity = (float)GetVerticalVelocity(controlledVehicle);

double gravity = GetGravity(controlledVehicle);
```

6. **Mach** from the body's atmosphere reference at barometric altitude:

```csharp
// AircraftHUDMain.cs L1169-L1173
if (AtmosphereDensity > 0)
{
    // MACH
    double speedOfSound = Math.Sqrt(1.4 * AtmospherePressure / AtmosphereDensity);
    double mach = velocity / speedOfSound;
```

   `BodyTemplate.AtmosphereReference` is null on an airless body; the exponential density never reaches zero,
   so hide Mach by the null check, not by density.
7. **Orbit readouts.** `Orbit.Eccentricity` is dimensionless; `Orbit.Inclination` is in radians (the game
   compares it with `Math.PI`); apsis radii need `MeanRadius` subtracted, in metres, before any unit
   conversion. `Orbit.ApoapsisAltitude` / `PeriapsisAltitude` do the subtraction.

## Complete example

[AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L956-L1085) (`DrawHud` gathering) and the helpers in [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L220-L263);
attitude, alpha and beta from the same file are covered in [Read roll, pitch, heading, angle of attack and sideslip in the local horizon frame](../read-flight-state-in-a-local-horizon-frame/overview.md).

## Variations and gotchas

- **Unit factors.** The mod divides speed by 100 and multiplies by 25.2 / 15.6586 / 13.6, which is 7 times
  the usual km/h, mph and knot factors; check unit conversions against 3.6 for km/h before reusing them.
- **Distances in feet before subtracting `MeanRadius`** make Ap/Pe wrong with imperial units; subtract in
  metres first.
- **Console spam on airless bodies.** `AtmosphereReference` is nullable; warn once, not per frame.
- **Alpha and beta use inertial velocity** (`Orbit.StateVectors.VelocityCci`); use `GetSurfaceVelocityCci()` for
  air-relative angles.
- Check `Situation.HasAnyContact()` / `Vehicle.HasMeaningfulOrbit` before presenting orbital numbers for a craft
  on the ground.

## See also

- [Overview](overview.md); [immutable snapshot](read-vehicle-flight-state-1.md); [after-frame snapshot](read-vehicle-flight-state-2.md)
- [Surface attitude and air data from the controlled vehicle](../../../../mods/ksa-aircrafthud/features/flight-data-from-vehicle.md)
- [Read roll, pitch, heading, angle of attack and sideslip in the local horizon frame](../read-flight-state-in-a-local-horizon-frame/overview.md)

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^ksa-physicalatmospherereference]: KSA.PhysicalAtmosphereReference (decompiled, build 2026.10.10.5554)
[^ksa-stellarbody]: KSA.StellarBody (decompiled, build 2026.10.10.5554)
