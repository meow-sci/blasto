---
type: Mod Feature
title: Mach number from the game atmosphere
description: Derives the speed of sound as sqrt(1.4 p / rho) from the pressure and density KSA already stores per vehicle, which is a per-body constant because KSA atmospheres are isothermal exponentials
mod: ksaero
category: physics
techniques: [custom-physics, harmony-transpiler]
hooks: [harmony-transpiler KSA.PhysicsStates.ComputeDerivatives, starmap StarMapAfterGui]
lifecycle: [physics:vehicle-solver, frame:draw-ui]
game_types: [KSA.PhysicsEnvironment, KSA.PhysicalAtmosphereReference, KSA.PhysicsStates]
tags: [physics, aerodynamics, mach, atmosphere, speed-of-sound]
---

KSA's per-vehicle environment carries atmospheric pressure and density but no temperature and no speed
of sound. KSAero needs Mach for the transonic drag rise and the crossflow Mach `M sin a`, so it derives
the speed of sound from the ideal-gas relation `a = sqrt(gamma p / rho)` with `gamma = 1.4`, using the
same pressure and density the game used for this substep.[^ksaero-aeromodel] [^ksaero-readme]

## How it works

- `AeroModel.SpeedOfSound(pressure, density)` returns `sqrt(1.4 p / rho)`, or 0 when either input is
  not positive ([KSAero/AeroModel.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroModel.cs#L152-L153)).
- In the physics path, `ComputeAirForces` passes `environment.AtmosphericPressure` and the `density`
  argument (the rewritten call receives `environment.AtmosphericDensity` widened to `double`), then
  divides the airspeed by `Math.Max(a, 1e-9)` so a zero never reaches a division
  ([KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L115-L117)).[^ksaero-airforcepatch]
- In the readout, the same function runs on the main-thread copy of the environment and Mach is shown
  as 0 when `a` is 0 ([KSAero/AeroReadout.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroReadout.cs#L48-L51)).[^ksaero-aeroreadout]

Where the inputs come from: `PhysicsEnvironment.RecomputePositionalValues` sets both fields from the
closest body's `PhysicalAtmosphereReference` at the vehicle's altitude, and zeroes both above the
atmosphere's height ([current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L83-L101)).[^ksa-physicsenvironment]
The atmosphere is one exponential with one scale height for both quantities
([current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L80-L88)):[^ksa-physicalatmosphere]

```text
p(h)   = p0   * exp(-h / H)
rho(h) = rho0 * exp(-h / H)
p / rho = p0 / rho0          (independent of altitude)
a = sqrt(1.4 * p0 / rho0)    (one value per body)
```

That is an isothermal atmosphere, so Mach depends only on airspeed and on the body. For the Earth entry
in the stock content (`SeaLevelPressure Atm="1.0"`, `SeaLevelDensity KgPerM3="1.225"`,
`ScaleHeight Km="8"`, [current/Content/Core/Astronomicals.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Astronomicals.xml#L1336-L1340)),
`p0 = 101325 Pa` (`PressureReference` converts atmospheres with that factor,
[current/decomp/KSA/PressureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PressureReference.cs#L88-L91)) and
`a = sqrt(1.4 * 101325 / 1.225)`, about 340.3 m/s, matching the README's 340 m/s.[^ksa-astronomicals] [^ksa-pressurereference]

## Lifecycle and timing

- **`physics:vehicle-solver`.** Inside `ComputeAirForces` on vehicle worker threads. Pressure and
  density come from the same `in PhysicsEnvironment` the integrator just refreshed, so they describe the
  state being evaluated; in Velocity Verlet the second evaluation of a substep sees the environment
  recomputed at the new position
  ([current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1173-L1175)).[^ksa-physicsstates]
  The function is pure and allocation-free, as the solver requires (see
  [Body-of-revolution aerodynamics](body-of-revolution-aerodynamics.md#lifecycle-and-timing)).
- **`frame:draw-ui`.** In the readout, on the main thread, from the environment copy applied in
  `frame:apply-solvers` (see [Aero readout window](aero-readout-window.md)).

## Key code

| Path | Role |
|---|---|
| [KSAero/AeroModel.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroModel.cs#L75-L76) | `Gamma = 1.4` |
| [KSAero/AeroModel.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroModel.cs#L152-L153) | `SpeedOfSound` |
| [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L115-L118) | Mach in the physics path |
| [KSAero/AeroReadout.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroReadout.cs#L46-L54) | Mach in the readout |

## Snippets

```csharp
// KSAero/AeroModel.cs L152-L153
    public static double SpeedOfSound(double pressure, double density)
        => pressure > 0.0 && density > 0.0 ? Math.Sqrt(AeroParameters.Gamma * pressure / density) : 0.0;
```

```csharp
// KSAero/AirForcePatch.cs L115-L118
        double speed = Math.Sqrt(speedSquared);
        double3 dirBody = (airVelocityBub / speed).Transform(kinematic.Body2Phys.Inverse());
        double mach = speed / Math.Max(AeroModel.SpeedOfSound(environment.AtmosphericPressure, density), 1e-9);
        AeroCoefficients c = AeroModel.Evaluate(in body, dirBody.X, dirBody.Y, dirBody.Z, mach);
```

```csharp
// current/decomp/KSA/PhysicalAtmosphereReference.cs L80-L88
	public double GetAtmosphericPressureAtAltitude(double altitudeInMeters)
	{
		return (double)SeaLevelPressure * Math.Exp((0.0 - altitudeInMeters) / ScaleHeight.InMeters());
	}

	public double GetAtmosphericDensityAtAltitude(double altitudeInMeters)
	{
		return (double)SeaLevelDensity * Math.Exp((0.0 - altitudeInMeters) / ScaleHeight.InMeters());
	}
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `PhysicsEnvironment.AtmosphericPressure` | [current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L22) | Pressure input (`float`, Pa) |
| `PhysicsEnvironment.AtmosphericDensity` | [current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L24) | Density input (`float`, kg/m^3) |
| `PhysicsEnvironment.RecomputePositionalValues` | [current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L83-L101) | Fills both from the atmosphere at the current altitude |
| `PhysicalAtmosphereReference.GetAtmosphericPressureAtAltitude`, `GetAtmosphericDensityAtAltitude` | [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L80-L88) | Isothermal exponential profiles |
| `PhysicalAtmosphereReference.SeaLevelPressure`, `SeaLevelDensity`, `ScaleHeight` | [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L12-L19) | Per-body XML parameters (defaults 101325 Pa, 1.225 kg/m^3, 1 km) |

## Pitfalls and version notes

- **Constant per body.** With KSA's current atmosphere model there is no temperature lapse, so Mach
  at 30 km equals Mach at sea level for the same airspeed. If the game adds a temperature profile, the
  `p / rho` form keeps working as long as both values stay consistent.
- **`gamma` is fixed at 1.4** for every body, whatever its atmosphere is made of.
- **Outside the atmosphere** both fields are zero and `SpeedOfSound` returns 0. The physics path never
  gets there (the air branch requires `AtmosphericDensity > 0`), and the `1e-9` floor only protects the
  division.
- **Use the substep's own values.** Reading pressure from the main-thread `Vehicle` inside the solver
  would mix two different states; KSAero reads both from the `in` arguments.

## See also

- [Body-of-revolution aerodynamics](body-of-revolution-aerodynamics.md)
- [Aero readout window](aero-readout-window.md)
- [Derive Mach number from the game's atmosphere pressure and density](../../../howto/flight-and-orbits/flight-state/read-vehicle-flight-state/read-vehicle-flight-state-4.md) - how-to article demonstrating this feature
- [Call-site transpiler on ComputeDerivatives writing the Disturbances accumulator](../../../howto/physics/aero-and-ground/replace-the-stock-aerodynamic-force-model/replace-the-stock-aerodynamic-force-model-1.md) - how-to article demonstrating this feature

[^ksaero-aeromodel]: KSAero/AeroModel.cs
[^ksaero-readme]: KSAero/README.md
[^ksaero-airforcepatch]: KSAero/AirForcePatch.cs
[^ksaero-aeroreadout]: KSAero/AeroReadout.cs
[^ksa-physicsenvironment]: KSA.PhysicsEnvironment (decompiled, build 2026.10.10.5554)
[^ksa-physicalatmosphere]: KSA.PhysicalAtmosphereReference (decompiled, build 2026.10.10.5554)
[^ksa-astronomicals]: Content/Core/Astronomicals.xml
[^ksa-pressurereference]: KSA.PressureReference (decompiled, build 2026.10.10.5554)
[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
