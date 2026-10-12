---
type: Mod Feature
title: Surface attitude and air data from the controlled vehicle
description: Computes HUD flight data on the main thread each frame from the controlled Vehicle, including roll/pitch/heading in the local ENU frame, angle of attack and sideslip, vertical speed, g-load, Mach from the body atmosphere, and radar or barometric altitude
mod: ksa-aircrafthud
category: flight-and-orbits
techniques: [custom-physics, harmony-postfix]
hooks: [harmony-postfix KSA.Program.OnFrameEditor]
lifecycle: [frame:viewports]
game_types:
  - KSA.Vehicle
  - KSA.VehicleReferenceFrameEx
  - KSA.QuaternionEx
  - KSA.Orbit
  - KSA.Celestial
  - KSA.AtmosphereReference
  - KSA.PhysicalAtmosphereReference
  - KSA.KinematicMeasurements
  - KSA.StellarBody
tags: [flight-data, attitude, enu, angle-of-attack, sideslip, mach, altitude, g-force, quaternion]
---

Every number on the HUD is computed in C# from the controlled vehicle once per frame, just before
drawing. Nothing is sent to the shaders. The interesting part is the attitude: KSA stores vehicle
orientation as a body-to-CCI quaternion, and the mod turns it into aircraft-style roll, pitch and
heading by removing the local East-North-Up frame with the same helper the game uses for its
`EnuBody` reference frame.[^ah-main] [^ksa-vrfex]

## How it works

| Readout | Computation | Code |
|---|---|---|
| Roll, pitch, heading | `Concatenate(vehicle.GetBody2Cci(), GetEnuBody2Cci(positionCci).Inverse()).ToRollPitchYawRadians()`, then shifted by 90 degrees and wrapped | [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L238-L245), [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L302-L334) |
| Alpha, beta | Inertial velocity rotated into body axes: `alpha = atan2(-v.X, -v.Z)`, `beta = atan2(-v.X, v.Y)`, then the same 90-degree shift (`AngleRadToDegHalf`) and wrapped to plus or minus 180 | [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L227-L236) |
| Vertical speed | `dot(GetVelocityCci(), normalize(GetPositionCci()))` | [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L220-L225) |
| G-load | `(AccelerationBody.Length() - g) / 9.80665`, with `g = G * Celestial.Mass / r^2` (9.81 if the parent is not a `Celestial`); wrong by one local g, see pitfalls | [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L247-L263), [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1073-L1074) |
| Altitude | `GetBarometricAltitude()`; below 50 km the box shows `GetRadarAltitude()` with an `R` tag | [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1043-L1051) |
| Mach | `GetSurfaceSpeed() / sqrt(1.4 p / rho)`, with `p` and `rho` from `BodyTemplate.AtmosphereReference.Physical` at barometric altitude; hidden only near bodies without an atmosphere, because the exponential density never reaches 0 | [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L978-L995), [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1169-L1173) |
| Ap, Pe, e, i | `Vehicle.Apoapsis/Periapsis`, `Orbit.Eccentricity/Inclination`; "Orbit: Surface" mode subtracts `MeanRadius` | [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1053-L1064) |
| Speed | `GetSurfaceSpeed()` through `ConvertToSpeedUnits` | [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1066-L1069), [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L186-L196) |

Engine facts behind these:

- `GetBarometricAltitude` is `|PositionCci| - Parent.MeanRadius`; `GetRadarAltitude` subtracts the
  terrain height under the vehicle and, on bodies with an ocean, the larger of terrain and ocean
  height ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3060-L3088)).[^ksa-vehicle]
- `GetSurfaceSpeed` is the length of `VelocityCci - (parent angular velocity x PositionCci)`, the
  velocity relative to the rotating surface
  ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2954-L2963)).
- `GetEnuBody2Cci` returns `null` when the ENU frame is undefined and otherwise concatenates
  `Vehicle.BODY2UPFRAME` with the ENU-to-CCI rotation
  ([current/decomp/KSA/VehicleReferenceFrameEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleReferenceFrameEx.cs#L102-L110));
  the game's own `QuaternionToEulerAngles` uses `ToRollPitchYawRadians` (XYZ order) for
  `EnuBody` and `ToRollYawPitchRadians` for the other frames
  ([current/decomp/KSA/VehicleReferenceFrameEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleReferenceFrameEx.cs#L439-L450),
  [current/decomp/KSA/QuaternionEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/QuaternionEx.cs#L151-L160)).[^ksa-quaternionex]
- `Vehicle.AccelerationBody` is the non-gravitational (proper) acceleration in body axes. The
  integrator stores `ForceBody / mass` in it while gravity is added separately as `AccelPhys`, and
  the constrained (Bepu) path stores the velocity change minus `dt * AccelPhys`
  ([current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1130-L1141),
  [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L976),
  [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L923-L935)).
  The game's own debug readout shows `G-FORCE` as `AccelerationBody.Length() / 9.80665` with no
  gravity term ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L5591)).[^ksa-physicsstates] [^ksa-constraintsim]
- The atmosphere is one exponential per body for both pressure and density
  ([current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L80-L88)),
  so the speed of sound is constant per body.[^ksa-physatmo]

## Lifecycle and timing

`frame:viewports`, main thread, inside the Harmony postfix on `Program.OnFrameEditor` (see
[Fullscreen draw-list HUD](fullscreen-drawlist-hud.md#lifecycle-and-timing)). `PrepareFrame` has
already run `Universe.ApplyVehicleSolvers()` and then dispatched the next step with
`Universe.ExecuteNextVehicleSolvers` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2167-L2220)),
so the main-thread `Vehicle` holds the step applied in this frame, and the next step is already
running on the worker threads. The workers write into per-vehicle staging, not the live `Vehicle`,
so these reads are consistent until the next frame's `ApplyVehicleSolvers`; writes from here would
race the step and be overwritten. The mod only reads.[^ksa-program] The displayed values therefore
lag the simulation by the step that is in flight, which is the same state every other UI in the
frame shows.

## Key code

| Path | Role |
|---|---|
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L220-L263) | Vertical speed, alpha/beta, surface attitude, gravity |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L167-L218) | Unit conversion and labels |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L270-L300) | Zero-padded readouts with a `K` suffix above the pad width |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1043-L1085) | Per-frame data gathering in `DrawHud` |

## Snippets

```csharp
// AircraftHUDMain.cs L220-L245
        public static double GetVerticalVelocity(Vehicle vehicle)
        {
            double3 positionCci = vehicle.GetPositionCci();
            double3 vector = positionCci.Normalized();
            return double3.Dot(vehicle.GetVelocityCci(), vector);
        }

        public static double2 GetAlphaBeta(Vehicle vehicle)
        {
            var state = vehicle.Orbit.StateVectors;
            var cci2body = vehicle.GetBody2Cci().Inverse();
            var velBody = (cci2body * state.VelocityCci).Normalized();

            var alpha = Math.Atan2(-velBody.X, -velBody.Z);
            var beta = Math.Atan2(-velBody.X, velBody.Y);
            return new double2(alpha, beta); // radians
        }

        public static double3 GetSurfaceAttitude(Vehicle vehicle)
        {
            double3 Body2Cci = vehicle.GetBody2Cci().ToRollYawPitchRadians();

            doubleQuat enuBody2Cci = VehicleReferenceFrameEx.GetEnuBody2Cci(vehicle.GetPositionCci()) ?? doubleQuat.Identity;
            doubleQuat attitudeQuat = doubleQuat.Concatenate(vehicle.GetBody2Cci(), enuBody2Cci.Inverse());
            return attitudeQuat.ToRollPitchYawRadians();
        }
```

```csharp
// AircraftHUDMain.cs L1169-L1173
            if (AtmosphereDensity > 0)
            {
                // MACH
                double speedOfSound = Math.Sqrt(1.4 * AtmospherePressure / AtmosphereDensity);
                double mach = velocity / speedOfSound;
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Vehicle.GetBody2Cci()` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3127-L3130) | Body orientation |
| `Vehicle.GetPositionCci()`, `GetVelocityCci()` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2570-L2625) | Radial direction, vertical speed |
| `Vehicle.GetSurfaceSpeed()` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2960-L2963) | Airspeed and Mach |
| `Vehicle.GetBarometricAltitude()`, `GetRadarAltitude()` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3060-L3088) | Altitude box and tape |
| `Vehicle.Orbit`, `Apoapsis`, `Periapsis` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L371-L401) | Orbit readouts |
| `Vehicle.AccelerationBody` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L573) | G-load (from `KinematicMeasurements`) |
| `Orbit.StateVectors`, `Eccentricity`, `Inclination` | [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1185-L1207) | Inertial velocity, orbit readouts[^ksa-orbit] |
| `VehicleReferenceFrameEx.GetEnuBody2Cci(double3)` | [current/decomp/KSA/VehicleReferenceFrameEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleReferenceFrameEx.cs#L102-L110) | Local horizon frame |
| `QuaternionEx.ToRollPitchYawRadians` | [current/decomp/KSA/QuaternionEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/QuaternionEx.cs#L151-L154) | Euler angles |
| `Celestial.Mass` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L89) | Local gravity[^ksa-celestial] |
| `PhysicalAtmosphereReference.GetAtmosphericDensityAtAltitude`, `GetAtmosphericPressureAtAltitude` | [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L80-L88) | Speed of sound |

## Pitfalls and version notes

- **G-load counts gravity twice.** `AccelerationBody` already excludes gravity (see Engine facts),
  so subtracting the local `g` again makes the readout about 0 G in level flight or at rest on the
  ground (true value 1 G) and about minus the local g in units of 9.80665 m/s^2 when coasting in
  orbit (true value 0 G). Use `AccelerationBody.Length() / 9.80665`, as the game's debug readout does.
- Alpha and beta use the inertial `Orbit.StateVectors.VelocityCci`, not the surface-relative
  velocity, so on the runway the flight path marker is displaced by the planet's rotation. Using
  `GetSurfaceVelocityCci()` would give air-relative angles.
- `Orbit.Inclination` is in radians (the game compares it to `Math.PI`,
  [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1911)) and the HUD prints it raw as
  `I 0.xx`.
- Distances are converted to feet before `MeanRadius` (meters) is subtracted in "Orbit: Surface"
  mode, so Ap/Pe are wrong with imperial units
  ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1057-L1064)). `Orbit.ApoapsisAltitude` and
  `PeriapsisAltitude` exist and do the subtraction in meters.
- Speed is `GetSurfaceSpeed() / 100` times 25.2 (Metric), 15.6586 (Imperial) or 13.6 (Knots). Those
  factors are 7 times the usual m/s to km/h, mph and knot factors, so the tape shows 0.07 times the
  conventional value; the intent is not documented in the repo.
- The mod's `GetGravity` uses its own `G` constant and `Vehicle.DistanceTo(celestial)`, and the
  unused local `Body2Cci` in `GetSurfaceAttitude` is dead code.
- **Crash in solar orbit.** The parent body is cast with `(Celestial)controlledVehicle.Orbit.Parent`
  ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L969-L970)).
  `Orbit.Parent` is an `IParentBody`, and stars are `StellarBody` subclasses (`FixedStar`,
  `OrbitingStar`) that implement `IParentBody` without deriving from `Celestial`
  ([current/decomp/KSA/StellarBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StellarBody.cs#L11),
  [current/decomp/KSA/FixedStar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedStar.cs#L7)).
  With a vehicle orbiting the sun in the `Orbit`, `IVA` or `Fixed` camera, the cast throws
  `InvalidCastException` on every frame. It runs inside the `Program.OnFrameEditor` postfix and
  nothing up to `App.Run` catches it
  ([current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L38-L72)), so the game ends (derived
  from the code, not observed). Use `Orbit.Parent as Celestial` and return on null.[^ksa-stellarbody]
  [^ksa-fixedstar] [^ksa-app]
- **Console spam on airless bodies.** `CelestialTemplate.AtmosphereReference` is nullable
  ([current/decomp/KSA/AstronomicalTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AstronomicalTemplate.cs#L57)),
  and `DrawHud` writes `AircraftHUD: AtmosphereReference is invalid!` with `Console.WriteLine` on
  every frame the HUD draws near a body without one
  ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L984-L995)).[^ksa-astronomicaltemplate]
- With imperial distance units the attitude block disappears above 999,999 ft, because the
  `altitude < 999999.0` check runs after the unit conversion (see
  [Fullscreen draw-list HUD](fullscreen-drawlist-hud.md#pitfalls-and-version-notes)).

## See also

- [Fullscreen draw-list HUD](fullscreen-drawlist-hud.md)
- [INI settings file and settings window](ini-settings-and-settings-window.md) (unit selection)
- [KSAero: Mach number from the game atmosphere](../../ksaero/features/mach-from-game-atmosphere.md)
- [Concatenate GetBody2Cci with the inverse ENU frame and call ToRollPitchYawRadians](../../../howto/flight-and-orbits/flight-state/read-flight-state-in-a-local-horizon-frame/read-flight-state-in-a-local-horizon-frame-1.md) - how-to article demonstrating this feature
- [Read altitude, speed, vertical speed, g-load and Mach in a Harmony postfix for an instrument panel](../../../howto/flight-and-orbits/flight-state/read-vehicle-flight-state/read-vehicle-flight-state-3.md) - how-to article demonstrating this feature

[^ah-main]: KSA_AircraftHUD/AircraftHUDMain.cs
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-vrfex]: KSA.VehicleReferenceFrameEx (decompiled, build 2026.10.10.5554)
[^ksa-quaternionex]: KSA.QuaternionEx (decompiled, build 2026.10.10.5554)
[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
[^ksa-physatmo]: KSA.PhysicalAtmosphereReference (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^ksa-constraintsim]: KSA.ConstraintSim (decompiled, build 2026.10.10.5554)
[^ksa-stellarbody]: KSA.StellarBody (decompiled, build 2026.10.10.5554)
[^ksa-fixedstar]: KSA.FixedStar (decompiled, build 2026.10.10.5554)
[^ksa-astronomicaltemplate]: KSA.AstronomicalTemplate (decompiled, build 2026.10.10.5554)
[^ksa-app]: KSA.App (decompiled, build 2026.10.10.5554)
