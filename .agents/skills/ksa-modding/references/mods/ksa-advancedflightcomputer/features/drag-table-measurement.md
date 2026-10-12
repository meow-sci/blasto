---
type: Mod Feature
title: Measure a vehicle's drag table by calling the game's force function on a state copy
description: Copy the vehicle's physics state structs, set the airspeed per sample, call the static PhysicsStates.ComputeDerivatives with dt 0 and no thrusters, and fit Cd and CL over Mach and angle of attack, so planners use whatever drag model is active, including aero mods
mod: ksa-advancedflightcomputer
category: physics
techniques: [custom-physics]
hooks: []
lifecycle: [frame:dispatch-solvers, frame:draw-ui]
game_types:
  - KSA.PhysicsStates
  - KSA.Vehicle
  - KSA.BubbleOrigin
  - KSA.KinematicStates
  - KSA.VehicleProperties
  - KSA.PhysicsEnvironment
  - KSA.Disturbances
tags: [aerodynamics, drag, measurement, physics-states, surrogate-model, guidance]
---

Landing and ascent planners need the vehicle's drag coefficient as a function of Mach and angle of
attack. Instead of re-implementing KSA's drag model (which a mod such as KSAero may replace), AFC
measures it: it runs the game's own force function on a copy of the vehicle's state at many airspeeds and
directions and fits a table to the forces that come back. Whatever drag model is active is what gets
measured.

## How it works

1. **Copy the state.** `ForceProbe.TryCreate` copies the four structs the force function reads:
   `Vehicle.BubbleOrigin`, `Vehicle.KinematicStates`, `Vehicle.Props` and `Vehicle.PhysicsEnvironment`
   (all exposed as `ref readonly`, so a local copy is a value copy). On the copy it sets free air at the
   chosen density and pressure, no ocean, zero spin ([AdvancedFlightComputer/Features/Guidance/Adapters/KsaAeroSweep.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/KsaAeroSweep.cs#L276-L324)).
2. **Set the airspeed the game's way.** The probe asks the game's own
   `PhysicsStates.ComputeAirVelocityBody` what the airspeed is at zero physics velocity (the frame's share
   from the bubble's motion and the co-rotating air) and sets every probe velocity relative to it, then
   checks a test velocity round-trips to within 1 cm/s before trusting the probe.
3. **Read the force.** `PhysicsStates.ComputeDerivatives(in origin, in kinematic, in props, in environment, 0.0, 0.0, positionBub, velocityBub, default, default)`:
   the static overload the integrator uses per substep, with `dt = 0` (which disables stock's
   semi-implicit drag limiter, a property of the integrator, not of the air) and no thrusters or chutes
   ([current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L951-L965)).[^ksa-physicsstates] A near-zero-speed baseline probe removes
   buoyancy and gravity from the samples.
4. **Sweep.** Over a Mach grid (Mach 0 sampled at 0.05) and an angle-of-attack grid measured from
   tail-first, averaging 24 roll azimuths per alpha (15 degree steps hit both extremes of a box model).
   The force along the airspeed becomes the Cd table (referenced to the bounding-box frontal area); the
   signed cross-flow force becomes a CL table used by the boostback glide; the lift-to-drag ratio,
   pointing sensitivity near tail-first and roll spread are reported so the approximations are visible.
5. **Mirror and verify the atmosphere.** The body's atmosphere is mirrored as an exponential model and
   checked against the game's density at altitude on every resample, recording the worst disagreement.
6. **Hand off immutably.** The `Result` is built entirely from copies and shares nothing with the game, so
   it can be handed to solver threads ([AdvancedFlightComputer/Features/Guidance/Adapters/KsaAeroSweep.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/KsaAeroSweep.cs#L8-L60)).

## Lifecycle and timing

`KsaAeroSweep.TryBuild` is called on the main thread: from the boostback mode and the boostback gauge's
resample (in the guidance step, `frame:dispatch-solvers`, or the panel draw, `frame:draw-ui`) and when the
convex ascent builds its problem. Calling the static force function on copies is safe on the main thread
even while the vehicle worker runs, because the probe never reads or writes the live vehicle's structs
after copying them and `ComputeDerivatives` writes nothing: it returns a `Disturbances` value computed
from its arguments, plus the parent body's `Mu` and angular velocity read through `origin.Parent`,
which the vehicle step does not change. Main-thread vehicle structs change only inside
`ApplyVehicleSolvers`, so copying them during the draw is safe as well. The sweep costs many force evaluations, so it runs on demand (on engagement or a
resample), not every frame; the result records the vehicle mass and bounding box so a stale sweep is
recognizable.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Adapters/KsaAeroSweep.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/KsaAeroSweep.cs) | Probe, sweep, fit, atmosphere mirror |
| [AdvancedFlightComputer/Features/Guidance/Numerics/Flight/Aero.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Numerics/Flight/Aero.cs) | `AeroTable` surrogate used by the solvers |
| [AdvancedFlightComputer/Features/Guidance/Numerics/Flight/Atmosphere.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Numerics/Flight/Atmosphere.cs) | Exponential atmosphere |
| [AdvancedFlightComputer/Features/Guidance/Ui/Gauges/BoostbackGauge.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/Gauges/BoostbackGauge.cs#L294-L320) | Resample from the panel |
| [tests/AdvancedFlightComputer.Guidance.Tests/Scvx/AeroCheck.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.Guidance.Tests/Scvx/AeroCheck.cs) | Console check of the aero model |

## Snippets

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/KsaAeroSweep.cs L328-L337
        /// <summary>The body-axis force the game applies at this air velocity (body axes), buoyancy included.</summary>
        public double3 ForceBody(double3 airVelocityBody)
        {
            _kinematic.VelocityPhys = airVelocityBody.Transform(_kinematic.Body2Phys) - _frameAirPhys;

            // dt = 0 turns off the semi-implicit limiter stock applies to its drag, which is a property of the integrator rather than of the air. No thrusters, no chutes.
            Disturbances d = PhysicsStates.ComputeDerivatives(in _origin, in _kinematic, in _props, in _environment,
                0.0, 0.0, _origin.PositionBub, _origin.VelocityBub, default, default);
            return d.ForceBody;
        }
```

## Game APIs touched

| Member | Use |
|---|---|
| `PhysicsStates.ComputeDerivatives(in BubbleOrigin, in KinematicStates, in VehicleProperties, in PhysicsEnvironment, ...)` ([current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L956)) | The game's force function, called on copies |
| `PhysicsStates.ComputeAirVelocityBody` ([current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L182)) | The game's definition of airspeed |
| `Vehicle.BubbleOrigin`, `KinematicStates`, `Props`, `PhysicsEnvironment` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L537-L543)) | State to copy |
| `PhysicsEnvironment.AtmosphericDensity`, `AtmosphericPressure` ([current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs)) | Probe conditions |

## Pitfalls and version notes

- Pass `dt = 0`; otherwise stock's semi-implicit drag limiter, which depends on the step, distorts the
  measured force.
- The table has no roll input; for stock's box model a square-section stack rolled 45 degrees presents
  about sqrt(2) times the flank, roughly 25 % for a slender stack, which the sweep reports as roll spread.
- Measuring through the game's function is what makes the planners agree with aero mods that patch
  `ComputeDerivatives` or its callees; a transpiled call site inside the integrator only (not inside
  `ComputeDerivatives`) would not be seen.

## See also

- [Boostback and impact prediction](boostback-impact-prediction.md)
- [Convex ascent with SCvx](convex-ascent-scvx.md)
- [6-DOF SCvx landing MPC](scvx-6dof-landing-mpc.md)
- [Predict an impact point with gravity, drag and a measured drag table](../../../howto/flight-and-orbits/guidance/predict-impact-point-and-build-a-ballistic-computer/predict-impact-point-and-build-a-ballistic-computer-1.md) - how-to article demonstrating this feature
- [Speed of sound from the atmosphere reference for a measurement tool](../../../howto/physics/aero-and-ground/replace-the-stock-aerodynamic-force-model/replace-the-stock-aerodynamic-force-model-2.md) - how-to article demonstrating this feature

[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
