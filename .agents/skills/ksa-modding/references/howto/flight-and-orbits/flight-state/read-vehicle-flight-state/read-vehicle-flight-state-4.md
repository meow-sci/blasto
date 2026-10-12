---
type: How-To
title: Derive Mach number from the game's atmosphere pressure and density
description: Compute the speed of sound as sqrt(1.4 p / rho) from PhysicsEnvironment, understand why KSA's isothermal atmosphere makes it constant per body, and use it from a physics patch or the main thread
category: flight-and-orbits
group: flight-state
topic: read-vehicle-flight-state
approach: 4
difficulty: beginner
prerequisites: [harmony]
demonstrated_by: [mods/ksaero/features/mach-from-game-atmosphere]
techniques: [flight-math, harmony-transpiler]
hooks: [harmony-transpiler KSA.PhysicsStates.ComputeDerivatives, starmap StarMapAfterGui]
lifecycle: [physics:vehicle-solver, frame:draw-ui]
game_types: [KSA.PhysicsEnvironment, KSA.PhysicalAtmosphereReference, KSA.PhysicsStates]
tags: [mach, speed-of-sound, atmosphere, pressure, density, aerodynamics]
---

Outcome: a Mach number that matches what the physics step used, with no temperature available from the
game. KSAero needs it for its transonic drag rise and shows it in a readout.[Mach number from the game atmosphere](../../../../mods/ksaero/features/mach-from-game-atmosphere.md)
Prerequisite: Harmony only if you compute it inside the physics patch; the function itself is pure C#.

## Background: how the game does it

KSA's per-vehicle `PhysicsEnvironment` carries `AtmosphericPressure` and `AtmosphericDensity` but no
temperature or speed of sound. `RecomputePositionalValues` sets both from the closest body's
`PhysicalAtmosphereReference` at the vehicle's altitude and zeroes both above the atmosphere's height.[^ksa-physicsenvironment]
[current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L83-L101) The atmosphere is one exponential with one scale height for both:[current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L80-L88)

```text
p(h)   = p0   * exp(-h / H)
rho(h) = rho0 * exp(-h / H)
p / rho = p0 / rho0          independent of altitude
a = sqrt(1.4 * p0 / rho0)    one value per body
```

That is an isothermal atmosphere, so Mach depends only on airspeed and on the body. For Earth in the stock
content (`SeaLevelPressure Atm="1.0"`, `SeaLevelDensity KgPerM3="1.225"`) this gives `a` of about
340.3 m/s.[^ksa-astronomicals] [current/Content/Core/Astronomicals.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Astronomicals.xml#L1336-L1340)

## Lifecycle and timing

- **`physics:vehicle-solver`.** Inside `ComputeAirForces` on vehicle worker threads, pressure and density
  come from the `in PhysicsEnvironment` the integrator just refreshed, so they describe the state being
  evaluated. The function is pure and allocation-free, as worker code must be (see the
  [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)).
- **`frame:draw-ui`.** In a readout the same function runs on the main-thread copy of the environment applied at
  `frame:apply-solvers`.

## Steps

1. **Write the function** with a guard so a zero never reaches a division:

```csharp
// KSAero/AeroModel.cs L152-L153
public static double SpeedOfSound(double pressure, double density)
    => pressure > 0.0 && density > 0.0 ? Math.Sqrt(AeroParameters.Gamma * pressure / density) : 0.0;
```

2. **Use the environment of the state you are evaluating.** On the worker, take `environment.AtmosphericPressure`
   and the density passed to the rewritten call:

```csharp
// KSAero/AirForcePatch.cs L115-L118
double speed = Math.Sqrt(speedSquared);
double3 dirBody = (airVelocityBub / speed).Transform(kinematic.Body2Phys.Inverse());
double mach = speed / Math.Max(AeroModel.SpeedOfSound(environment.AtmosphericPressure, density), 1e-9);
AeroCoefficients c = AeroModel.Evaluate(in body, dirBody.X, dirBody.Y, dirBody.Z, mach);
```

3. **In the readout**, read `vehicle.PhysicsEnvironment` (a `ref readonly` field; copy the values) and show 0
   when the speed of sound is 0 (vacuum).
4. **Alternatively on the main thread** use the body's atmosphere reference directly:
   `GetAtmosphericPressureAtAltitude(h) / GetAtmosphericDensityAtAltitude(h)`; the ratio is the constant `p0/rho0`.

## Complete example

| File | Role |
|---|---|
| [KSAero/AeroModel.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroModel.cs#L75-L76) | `Gamma = 1.4` |
| [KSAero/AeroModel.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroModel.cs#L152-L153) | `SpeedOfSound` |
| [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L115-L118) | Mach in the physics path |
| [KSAero/AeroReadout.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroReadout.cs#L46-L54) | Mach in the readout |

## Variations and gotchas

- **Do not expect altitude dependence.** Real air cools with altitude; KSA's does not, so Mach for a given
  airspeed is the same at every altitude of one body.
- A mod body with a different gas needs its own gamma; 1.4 is a diatomic ideal gas assumption, not a game value.
- Pressure is in pascals (`PressureReference` converts atmospheres with 101325); do not feed atmospheres.
- Pressure and density are `float` in `PhysicsEnvironment`; widen before dividing.

## See also

- [Overview](overview.md); [postfix instrument read](read-vehicle-flight-state-3.md)
- [Mach number from the game atmosphere](../../../../mods/ksaero/features/mach-from-game-atmosphere.md)

[^ksa-physicsenvironment]: KSA.PhysicsEnvironment (decompiled, build 2026.10.10.5554)
[^ksa-astronomicals]: Game content Content/Core/Astronomicals.xml (build 2026.10.10.5554)
