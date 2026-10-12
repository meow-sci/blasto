---
type: Mod Feature
title: Suicide-burn calculator
description: Estimates the altitude, duration and countdown for a last-moment full-thrust landing burn from radar altitude, surface-relative velocity, active-engine thrust at the live ambient pressure and an averaged gravity term
mod: kitten-engineer-redux
category: flight-and-orbits
techniques: [flight-math]
hooks: [harmony-postfix KSA.Vehicle.OnDrawUi]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Vehicle
  - KSA.Orbit
  - KSA.IParentBody
  - KSA.Astronomical
  - KSA.AtmosphericBody
  - KSA.PhysicsEnvironment
  - KSA.EngineController
  - KSA.RocketControllerData
tags: [landing, suicide-burn, hoverslam, radar-altitude, thrust, gravity, flight]
---

The "Suicide burn" section of the flight HUD tells a descending pilot at which radar altitude a
full-thrust burn must start to reach zero speed at the ground, how long that burn lasts, and how
many seconds remain before it, switching to "BURN NOW" once the craft is below that altitude. It is a
closed-form estimate recomputed every frame, not a simulation.

## How it works

`SuicideBurnCalculator.Compute(vehicle, orbit)` ([SuicideBurnCalculator.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SuicideBurnCalculator.cs#L16-L79)):[^ker-suicideburn]

| Quantity | Source |
|---|---|
| Height above ground `h` | `Vehicle.GetRadarAltitude()`: radius minus mean radius minus terrain (or ocean) height under the craft |
| Descent rate `vd` | `-VerticalVelocity` from the orbit summary (surface-relative velocity projected on the radial direction)[^ker-orbitsummary] |
| Speed `v` | `Vehicle.GetSurfaceSpeed()`, the length of the surface-relative velocity |
| Thrust `F`, flow `mdot` | `ActiveEngineThrust.Compute(parts, PhysicsEnvironment.AtmosphericPressure)`: active engines at full throttle, through `RocketControllerData.ComputeFromCores` or `VacuumData` (see [Live and rated engine thrust with TWR](live-engine-thrust-and-twr.md))[^ker-activeenginethrust] |
| Mass `m0` | `Vehicle.TotalMass` |
| Gravity `g` | mean of `mu / r^2` at the current radius and at `IParentBody.GetNearSurfaceRadius()` |

Then, with flight-path angle `gamma = asin(vd / v)`:

1. Abort with "insufficient thrust" if `F <= 0`, `mdot <= 0`, `m0 <= 0` or `F / m0 <= g`; the same
   status is returned when the burn duration below comes out negative or NaN, or the average
   deceleration in step 3 is not positive.
2. Burn duration `t = v / (F/m0 + 0.5 * mdot * v / m0 - g)`, a first-order correction for the craft
   getting lighter during the burn.
3. End mass `m1 = max(0.1, m0 - mdot * t)`; average deceleration `a = F / ((m0 + m1) / 2) - g * sin(gamma)`.
4. Stopping distance along the path `d = v^2 / (2a)`; burn altitude `hb = d * sin(gamma)`.
5. `BurnNow = h <= hb`; otherwise, if descending, time to burn `= (h - hb) / vd`.

## Lifecycle and timing

- Called once per frame from `FlightHud.Draw` inside the `Vehicle.OnDrawUi` postfix
  (`frame:draw-ui`), right after the orbit summary it depends on
  ([FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L105-L106)).[^ker-flighthud]
- It reads only main-thread vehicle state already applied for this frame (orbit state vectors,
  `PhysicsEnvironment`, module states) and writes nothing, so it is safe from the UI pass even though
  the next vehicle-solver step is running on workers.
- It runs whether or not the section is open, because the HUD computes all frame data before
  drawing.

## Key code

| Path | Role |
|---|---|
| [SuicideBurnCalculator.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SuicideBurnCalculator.cs) | The estimate and its result record |
| [ActiveEngineThrust.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/ActiveEngineThrust.cs#L37-L63) | Active-engine thrust and flow at an ambient pressure |
| [FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L305-L323) | Section rendering and status strings |

## Snippets

```csharp
// SuicideBurnCalculator.cs L32-L63
        IParentBody parent = vehicle.Parent;
        double currentRadius = vehicle.Orbit.StateVectors.PositionCci.Length();
        double surfaceRadius = parent.GetNearSurfaceRadius();
        double gravityCurrent = currentRadius > 0.0 ? parent.Mu / (currentRadius * currentRadius) : 0.0;
        double gravitySurface = surfaceRadius > 0.0 ? parent.Mu / (surfaceRadius * surfaceRadius) : gravityCurrent;
        double localGravity = (gravityCurrent + gravitySurface) * 0.5;
        double massFlowRate = engines.TotalMassFlowRate;
        double initialThrustAccel = engines.TotalThrust / massInitial;

        if ((initialThrustAccel - localGravity) <= 0.0)
            return new SuicideBurnInfo(false, isDescending, false, 0f, null, 0f);

        double thrustToMassRatio = engines.TotalThrust / massInitial;
        double burnDuration = surfaceSpeed / (thrustToMassRatio + (0.5 * massFlowRate * surfaceSpeed / massInitial) - localGravity);

        if (burnDuration < 0.0 || double.IsNaN(burnDuration))
            return new SuicideBurnInfo(false, isDescending, false, 0f, null, 0f);

        double pitchAngle = surfaceSpeed > 0.0 ? System.Math.Asin(descentRate / surfaceSpeed) : System.Math.PI * 0.5;
        
        double massFinal = massInitial - (massFlowRate * burnDuration);
        if (massFinal < 0.1) massFinal = 0.1; 

        double averageThrustAccel = engines.TotalThrust / ((massInitial + massFinal) * 0.5);
        double netDecelDynamic = averageThrustAccel - (localGravity * System.Math.Sin(pitchAngle));

        if (netDecelDynamic <= 0.0)
            return new SuicideBurnInfo(false, isDescending, false, 0f, null, 0f);

        double burnDistance = (surfaceSpeed * surfaceSpeed) / (2.0 * netDecelDynamic);
        double burnAltitude = burnDistance * System.Math.Sin(pitchAngle);
        bool burnNow = radarAltitude <= burnAltitude;
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Vehicle.GetRadarAltitude()` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3065-L3090) | Height above terrain or ocean |
| `Vehicle.GetSurfaceSpeed()` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2960-L2963) | Surface-relative speed |
| `Vehicle.TotalMass`, `Orbit`, `Parent`, `Orbit.StateVectors.PositionCci` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L567), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L371-L373), [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1185) | Mass and radius |
| `IParentBody.Mu` | [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L15) | Gravitational parameter (`Mass * 6.6743E-11`) |
| `IParentBody.GetNearSurfaceRadius()` | [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L382-L385), [current/decomp/KSA/AtmosphericBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphericBody.cs#L20-L28) | Second gravity sample radius |
| `EngineController.VacuumData` / `RocketControllerData.ComputeFromCores` | [current/decomp/KSA/EngineController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineController.cs#L40-L74) | Thrust and flow at pressure |

## Pitfalls and version notes

- `GetNearSurfaceRadius()` is not the ground radius. On an `AtmosphericBody` with an atmosphere it
  returns `MeanRadius + atmosphere height + 1`, and on other bodies `MaxTerrainRadius + 1`
  ([current/decomp/KSA/AtmosphericBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphericBody.cs#L20-L28)).[^ksa-atmosphericbody] [^ksa-astronomical]
  On a body with an atmosphere the "surface" gravity sample is taken at the top of the atmosphere,
  so the averaged `g` is lower than the gravity near the ground and the burn starts late; on an
  airless body it is taken at the highest terrain point, which is close to the ground. The game uses
  this radius as an "above the terrain and atmosphere" threshold (for example `PhysicsEnvironment`'s
  `PhysicsRadius`, [current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L42)), not as a ground radius.[^ksa-physicsenvironment] Use `MeanRadius` (or `MeanRadius + terrain height`) if you
  copy this.
- Thrust is the rated full-throttle thrust of engines that are already active, at the current ambient
  pressure; with engines off the section shows "INSUFFICIENT THRUST" rather than a plan for the
  next stage. `ActiveEngineThrust.Compute` checks only `EngineController.IsActive`, not
  `IsPropellantAvailable`, so an active engine with empty tanks still counts.
- Time to burn divides the remaining height by the current descent rate, ignoring the gravity that
  will speed the craft up while it coasts, so it is optimistic for long coasts.
- The model assumes thrust opposes velocity along a straight path; it does not model gravity turns or
  horizontal-only braking.

## See also

- [Flight HUD on the controlled vehicle](flight-hud-controlled-vehicle-postfix.md)
- [Orbit readouts and Hohmann estimate](orbit-readouts-and-hohmann-estimate.md)
- [Custom stage delta-v estimator](custom-stage-delta-v-estimator.md)
- [Live and rated engine thrust with TWR](live-engine-thrust-and-twr.md)
- [Closed-form suicide-burn calculator on radar altitude](../../../howto/flight-and-orbits/guidance/compute-a-suicide-burn/compute-a-suicide-burn-1.md) - how-to article demonstrating this feature

[^ker-suicideburn]: SuicideBurnCalculator.cs
[^ker-orbitsummary]: OrbitSummary.cs
[^ker-activeenginethrust]: ActiveEngineThrust.cs
[^ker-flighthud]: FlightHud.cs
[^ksa-atmosphericbody]: KSA.AtmosphericBody (decompiled, build 2026.10.10.5554)
[^ksa-astronomical]: KSA.Astronomical (decompiled, build 2026.10.10.5554)
[^ksa-physicsenvironment]: KSA.PhysicsEnvironment (decompiled, build 2026.10.10.5554)
