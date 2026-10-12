---
type: Mod Feature
title: Vessel flight-state reads
description: How gatOS reads position, velocity, attitude, altitude, mass, orbit, navball, environment and encounters from KSA.Vehicle each sample, in which coordinate frames, and how it scrubs KSA's sentinels
mod: gatos
category: flight-and-orbits
techniques: [starmap-lifecycle, harmony-prefix, immutable-snapshot]
hooks: [harmony-prefix KSA.GameViewport.OnFrame, starmap StarMapBeforeGui]
lifecycle: [frame:viewports, frame:draw-ui]
game_types:
  - KSA.Vehicle
  - KSA.Orbit
  - KSA.IParentBody
  - KSA.NavBallData
  - KSA.PhysicsEnvironment
  - KSA.PhysicalAtmosphereReference
  - KSA.PatchedConic
  - KSA.Encounter
  - KSA.UniverseTime
tags: [telemetry, vehicle, orbit, navball, coordinate-frames, cci, ccf, cce]
---

`VesselReader` is the only place gatOS reads flight state. It produces the per-vessel record
behind `/sim/vessels/<id>/{position,velocity,attitude,altitude,mass,orbit,navball,environment,encounters}`
and the same fields over HTTP, MQTT and MCP. It is a compact map of which public `Vehicle`
members carry which quantity, in which frame and unit, and which values need guarding.

## How it works

The reader has two builders sharing one `ReadBasics` pass ([gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs#L41-L61)):

- **Core** (always): id, situation, CCI position, lat/lon, speeds, attitude quaternion, body rates,
  barometric and radar altitude, total/dry/propellant mass, orbit elements, engines, tanks,
  battery, animations.
- **Full** (when `telemetry_vessel_detail` is on): core plus ECL position, CCI velocity, centre of
  mass, navball, environment, power, RCS, solar, lights, docking, decouplers, SRB, encounters and
  the control read-backs (`GetManualThrottle`, flight computer modes). If any detail read throws,
  the reader logs once and falls back to the core record rather than dropping the vessel.

| Quantity | KSA source | Frame / unit |
|---|---|---|
| position | `Vehicle.GetPositionCci()` (= `Orbit.GetPositionCci()`) | parent-centred inertial, m |
| lat / lon | `double3.Transform(posCci, parent.GetCci2Ccf())` then `IParentBody.GetLlaFromCcf` | body-fixed, deg |
| attitude | `Vehicle.GetBody2Cci()` | body to CCI quaternion |
| rates | `Vehicle.BodyRates` | body frame, rad/s |
| speeds | `OrbitalSpeed`, `GetSurfaceSpeed()`, `GetInertialSpeed()` | m/s |
| altitude | `GetBarometricAltitude()`, `GetRadarAltitude()` | m |
| mass | `TotalMass`, `InertMass`, `PropellantMass` | kg |
| orbit | `Vehicle.Orbit` (`Apoapsis`, `Periapsis`, `Eccentricity`, `Inclination`, `SemiMajorAxis`, `Period`, `LongitudeOfAscendingNode`, `ArgumentOfPeriapsis`, `StateVectors.TrueAnomaly`) | radii from body centre, angles in rad |
| apsis times | `NextApoapsisTime`, `NextPeriapsisTime`, `NextPatchEventTime` | `UniverseTime` |
| navball | `ref readonly Vehicle.NavBallData` (`AttitudeAngles`, `ThrustWeightRatio`, `DeltaV`, `Frame`, `Speed`) | int degrees |
| environment | `ref readonly Vehicle.PhysicsEnvironment`, `PhysicalAtmosphereReference.GetDynamicPressure(vehicle)`, `AccelerationBody`, `AngularAccelerationBody` | Pa, kg/m3, m/s2 |
| encounters | `Vehicle.Patch.Encounters` (`Body`, `GameTime`, `ClosestDistance`) | SOI encounters only |

**Frames.** KSA's frames (from the repo's copy of the developer article on celestial frames,
[docs/KSA_CELESTIAL_COORDINATE_FRAMES.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/docs/KSA_CELESTIAL_COORDINATE_FRAMES.md)): ECL is the star-centred inertial root; CCE is ECL
re-centred on a body (shown as STAR in the UI); CCF is body-fixed and rotates with the body; CCI
keeps CCF's polar Z axis but fixes X at the vernal point, so it is inertial. All frames are
right-handed. The decomp confirms attitude is stored as `Body2Cce` and converted on demand:
`GetBody2Cci()` concatenates `Body2Cce` with `Orbit.Parent.GetCce2Cci()`
([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3127-L3130)).

**Sanitizing.** Every double goes through `Sanitize.Finite` (NaN/Inf become 0). Apsis radii become
altitudes by subtracting `MeanRadius`, and apoapsis is forced to 0 when `Orbit.IsBound()` is false,
because KSA stores `a(1+e)` (finite, large and negative) for hyperbolic orbits
([gatOS.SimFs/Telemetry/Sanitize.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Telemetry/Sanitize.cs#L20-L31)). Missing apsis/patch times use
`UniverseTime.EndOfTime`, which converts to a finite ~1.7e29 s, so the reader tests
`UniverseTime.IsSaturated()` before converting ([gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs#L369-L378)).

## Lifecycle and timing

The reader runs inside the sampler's main-thread slot (viewport prefix, `StarMapBeforeGui` or
the F2 fallback, see [Telemetry sampler and snapshot store](telemetry-sampler-snapshot.md)).
At that point `Universe.ApplyVehicleSolvers` has already copied the finished step into each
`Vehicle` (kinematics, `Body2Cce`, `BodyRates`, kinematic measurements, flight computer, part
states) in `Vehicle.UpdateFromTaskResultsUnsynchronized`.[^ksa-vehicle] The
in-flight step works on `VehicleUpdateState` copies, so these reads are consistent for the frame.
`NavBallData` and `PhysicsEnvironment` are exposed as `ref readonly` fields; the reader copies the
values out immediately and never holds the reference.

## Key code

| Path | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs#L82-L135) | `ReadBasics`: core reads, CCI to CCF to lat/lon |
| [gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs#L179-L259) | `BuildFull`: detail pass |
| [gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs#L309-L378) | orbit elements, apsis times, saturation guards |
| [gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs#L397-L432) | navball and environment |
| [gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs#L908-L922) | SOI encounters |
| [gatOS.SimFs/Telemetry/Sanitize.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Telemetry/Sanitize.cs) | finite and altitude helpers |

## Snippets

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

```csharp
// gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs L341-L378
    private static OrbitSnapshot? FullOrbit(Vehicle vehicle, IParentBody? parent, double utSeconds)
    {
        if (vehicle.Orbit is not { } o || parent is null)
            return null;
        return new OrbitSnapshot(
            Sanitize.ApoapsisToAltitude(o.Apoapsis, parent.MeanRadius, o.IsBound()),
            Sanitize.RadiusToAltitude(o.Periapsis, parent.MeanRadius),
            Sanitize.Finite(o.Eccentricity),
            Sanitize.Finite(o.Inclination * RadToDeg),
            Sanitize.Finite(o.SemiMajorAxis),
            Sanitize.Finite(o.Period))
        {
            LanDeg = Sanitize.Finite(o.LongitudeOfAscendingNode * RadToDeg),
            ArgPeDeg = Sanitize.Finite(o.ArgumentOfPeriapsis * RadToDeg),
            TrueAnomalyDeg = Sanitize.Finite(o.StateVectors.TrueAnomaly.Degrees),
            TimeToApoapsis = TimeUntil(vehicle.NextApoapsisTime, utSeconds),
            TimeToPeriapsis = TimeUntil(vehicle.NextPeriapsisTime, utSeconds),
            NextPatchEventUt = UtOrZero(vehicle.NextPatchEventTime),
        };
    }

    /// <summary>
    ///     A <see cref="UniverseTime"/> as absolute UT seconds, or 0 when it carries the
    ///     "no such event" sentinel. KSA saturates missing times to
    ///     <c>UniverseTime.EndOfTime</c>/<c>BeginningOfTime</c> (Int128 Max/Min nanoseconds); unlike
    ///     the old <c>SimTime.PositiveInfinity</c> those convert to a finite ~1.7e29 seconds, which
    ///     <see cref="Sanitize.Finite"/> would happily pass through as a real timestamp.
    /// </summary>
    private static double UtOrZero(UniverseTime target)
        => target.IsSaturated() ? 0 : Sanitize.Finite(target.Seconds());

    private static double TimeUntil(UniverseTime target, double utSeconds)
    {
        if (target.IsSaturated())
            return 0; // no such event on this orbit (KSA's EndOfTime sentinel)
        var dt = Sanitize.Finite(target.Seconds()) - utSeconds;
        return dt > 0 ? dt : 0;
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Vehicle.GetPositionCci`, `GetPositionEcl`, `GetVelocityCci`, `GetBody2Cci`, `BodyRates` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs) | state vectors and attitude |
| `Vehicle.NavBallData`, `PhysicsEnvironment`, `AccelerationBody` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L543-L591) | navball, environment, accelerometer |
| `IParentBody.GetCci2Ccf`, `GetLlaFromCcf` | [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L41-L67) | geodetic lat/lon |
| `Orbit.Apoapsis`, `Periapsis`, `Inclination`, `IsBound()` | [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1195-L1207) | elements (radii, radians) |
| `NavBallData.AttitudeAngles`, `DeltaV`, `ThrustWeightRatio` | [current/decomp/KSA/NavBallData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/NavBallData.cs) | navball |
| `PhysicsEnvironment.AtmosphericPressure`, `AtmosphericDensity`, `OceanDensity`, `TerrainRadius` | [current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L22-L28) | environment |
| `PhysicalAtmosphereReference.GetDynamicPressure(Vehicle)` | [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L61-L70) | dynamic pressure |
| `UniverseTime.IsSaturated()` | [current/decomp/KSA/UniverseTime.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UniverseTime.cs#L142-L145) | "no such event" test |
| `Vehicle.Patch` / `PatchedConic.Encounters` / `Encounter` | [current/decomp/KSA/Encounter.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Encounter.cs) | SOI encounters |

## Pitfalls and version notes

- `NavBallData.DeltaV` was `DeltaVInVacuum` before rev 5114 and now means the active staging
  sequence's propellant-aware delta-v; `ThrustWeightRatio` became atmosphere-corrected in the same
  revision (gatOS anchor notes). Both members exist unchanged in 5554.
- `Orbit` exposes `ApoapsisAltitude`/`PeriapsisAltitude` since rev 5439, but they are plain
  radius-minus-`MeanRadius` and would leak the hyperbolic `a(1+e)`; gatOS keeps its own `IsBound` gate.
- `Vehicle.Patch.Encounters` are SOI encounters; they never include planned burns and are not the
  target gauge's closest-approach list.
- KSA has no vessel display name: `Vehicle.Id` is used as both id and name.
- EVA kittens (`KittenEva : Vehicle`) and debris fragments appear in the same enumeration.

## See also

- [Part and module state reads](part-module-state-reads.md)
- [Celestial body catalog reads](celestial-body-catalog-reads.md)
- [Telemetry sampler and snapshot store](telemetry-sampler-snapshot.md)
- [Solver-phase drain for flight computer setpoints](solver-phase-command-drain.md)
- [gatOS overview](../overview.md)
- [Read orbit and encounter data without leaking NaN or UniverseTime sentinels](../../../howto/flight-and-orbits/flight-state/read-orbit-elements-and-timing/read-orbit-elements-and-timing-2.md) - how-to article demonstrating this feature
- [Build an immutable flight-state snapshot in a main-viewport OnFrame prefix](../../../howto/flight-and-orbits/flight-state/read-vehicle-flight-state/read-vehicle-flight-state-1.md) - how-to article demonstrating this feature
- [Vessel state in CCI, CCF and ECL: which member is in which frame](../../../howto/flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/understand-ksa-coordinate-frames-and-epochs-1.md) - how-to article demonstrating this feature

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
