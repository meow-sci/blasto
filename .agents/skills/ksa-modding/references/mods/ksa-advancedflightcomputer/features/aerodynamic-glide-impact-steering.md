---
type: Mod Feature
title: Steer an unpowered descent onto a site with angle of attack, using a measured lift curve and an impact Jacobian
description: After a boostback, fly engine-first into the wind and turn the predicted impact miss into a lift request through the velocity Jacobian of a drag-integrated coast prediction, invert the measured lift curve for the angle of attack, and trim out the attitude loop's lag
mod: ksa-advancedflightcomputer
category: flight-and-orbits
techniques: [closed-loop-guidance, automatic-differentiation]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers]
game_types: [KSA.Vehicle, KSA.IParentBody, KSA.PhysicsEnvironment, KSA.Orbit]
tags: [boostback, glide, angle-of-attack, lift, impact-prediction, pid, zero-effort-miss, aerodynamics]
---

Once the boostback burn ends, a returning booster still has minutes of falling through the atmosphere in
which dispersions grow. AFC's glide phase steers that fall with the airframe's own lift: it points the
engine end into the airflow, tilted by a commanded angle of attack, and closes the loop on the predicted
impact point until the miss is zero and the command is back at alpha 0. Nothing about the airframe is
assumed; the lift direction, the lift curve and the impact sensitivity are all measured from the active aero
model ([AdvancedFlightComputer/Features/Guidance/Modes/BoostbackGlide.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/BoostbackGlide.cs#L12-L31)).

## How it works

| Step | Detail |
|---|---|
| Steered quantity | The alpha-0 coast impact point (the one the overlay draws) and its miss from the site, projected onto the ground plane at the site in the body-fixed frame (`IParentBody.GetCci2Ccf()`) |
| Sensitivity | One forward-mode velocity Jacobian of the drag-integrated coast prediction (`ImpactPredictor.VelocityJacobian` over `Dual` numbers, about 3.6 ms per the author), applied to the two lift axes (vertical plane and cross-track) to get "impact metres per m/s of push" |
| Control law | Wanted impact rate `-(Kp miss + Ki integral + Kd rate) / tgo`; because the Jacobian grows with time to go, the lift asked for is `Kp miss / tgo^2`, the zero-effort-miss law; defaults `Kp = 5` (3 is the textbook value, 5 tolerates attitude lag better), `Ki = Kd = 0`, maximum AoA 15 degrees, `tgo` floored at 5 s ([AdvancedFlightComputer/Features/Guidance/Modes/VehicleAutopilotState.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/VehicleAutopilotState.cs#L1130-L1145)) |
| Lift solve | Damped 2 x 2 least squares for accelerations along the two lift axes (Tikhonov damping `1e-3` of the mean authority) |
| Lift to angle | `C_L = a m / (q A)` with `q = 0.5 rho v^2` from `Vehicle.PhysicsEnvironment.AtmosphericDensity`; the smallest AoA reaching it is found by walking the measured `C_L(Mach, alpha)` in 0.25 degree steps and interpolating, with a linear ramp near zero so the command stays continuous |
| Which way to tilt | Sign of `C_L / C_D` at the maximum AoA read from the same table every update: a slender booster lifts toward its tilted engine end, a capsule the other way, a model with no lift holds alpha 0 |
| Attitude trim | Every step, the tilt actually flown (measured off the thrust axis) is compared with the tilt asked for and the difference integrated into the command (gain 0.5/s, anti-windup) to cancel the attitude loop's lag on a turning airflow |
| Cadence | PID and Jacobian every 100 ms of wall-clock time; trim and command every step |

The aero table comes from [drag table measurement](drag-table-measurement.md): `KsaAeroSweep` samples the
game's force function on a copied vehicle state, so the glide adapts to whatever aerodynamics model is active.
The author's header records closed-loop flat-earth runs through KSAero's aerodynamics (boosters of fineness
6 to 13 and a capsule, up to 5 s of attitude lag) landing within 2 m of the site.

## Lifecycle and timing

The glide is one phase of the boostback mode and runs in the guidance step, the `Vehicle.PrepareWorker`
prefix for the booster (`frame:dispatch-solvers`, main thread), so an unfocused booster is still flown
([AdvancedFlightComputer/Features/Guidance/Modes/Boostback.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Boostback.cs#L490-L504)). It reads the committed
state (`Orbit.StateVectors`, `PhysicsEnvironment.AtmosphericDensity`, mass) of the step just applied and
returns a thrust-axis direction in CCI; the attitude goes to the stock flight computer, which the next
physics step tracks (see [guidance attitude and gimbal actuation](guidance-attitude-and-gimbal-actuation.md)).
The impact prediction is synchronous numerical work bounded by a step budget; the 100 ms wall-clock cadence
keeps it from running every step under warp.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Modes/BoostbackGlide.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/BoostbackGlide.cs) | Glide direction, PID, lift solve, AoA inversion, trim, flight log line |
| [AdvancedFlightComputer/Features/Guidance/Modes/Boostback.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Boostback.cs) | Phase switch that calls the glide |
| [AdvancedFlightComputer/Features/Guidance/Numerics/Flight/ImpactPredictor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Numerics/Flight/ImpactPredictor.cs) | Drag-integrated coast and its velocity Jacobian |
| [AdvancedFlightComputer/Features/Guidance/Adapters/KsaAeroSweep.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/KsaAeroSweep.cs) | Measured `C_D` and `C_L` tables (`LiftAt`) |
| [AdvancedFlightComputer/Features/Guidance/Ui/Overlays/BoostbackOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/Overlays/BoostbackOverlay.cs) | Draws the predicted impact the glide steers on |

## Snippets

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/BoostbackGlide.cs L264-L285
        // THE PID, as the rate the impact should move at: (Kp miss + Ki integral + Kd rate) / tgo, back towards the site. With the Jacobian scaling like the time to go, the lift this asks for is the zero-effort-miss law's Kp miss / tgo^2.
        double tgo = Math.Max(nom.TimeOfFlight.V, GlideMinTgo);
        _s.GlideTgo = tgo;
        double3 pid = _s.GlideKp * miss + _s.GlideKi * _s.GlideIntCcf + _s.GlideKd * _s.GlideRateCcf;
        SolveGlideLift(perUp, perLeft, -pid / tgo, out double aUp, out double aLeft);
        double accel = Math.Sqrt(aUp * aUp + aLeft * aLeft);
        _s.GlideAccelCmd = accel;

        double clWanted = accel * mass / (q * aero.ReferenceArea);
        double alphaDeg = GlideAoaForLift(aero, mach, _s.GlideLiftSign, clWanted, maxAoa, out bool saturated);

        // Anti-windup: integrate only while the command has room, or where the integral is unwinding.
        if (!saturated || double3.Dot(miss, _s.GlideIntCcf) < 0.0)
            _s.GlideIntCcf += miss * dt;

        // The engine end tilts towards the lift for a body that lifts that way, away from it for one that lifts the other.
        double nUp = accel > 0.0 ? aUp / accel : 0.0;
        double nLeft = accel > 0.0 ? aLeft / accel : 0.0;
        _s.GlideTiltUpDeg = _s.GlideLiftSign * nUp * alphaDeg;
        _s.GlideTiltLeftDeg = _s.GlideLiftSign * nLeft * alphaDeg;
        _s.GlideAoaDeg = alphaDeg;
        _s.GlideStatus = saturated ? $"AoA limited to {maxAoa:F0} deg - asking for more lift than it has." : "";
```

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/BoostbackGlide.cs L244-L252
        // Everything in the body-fixed frame, on the ground plane at the site. The Jacobian's rows are in the co-rotating frame - the current body-fixed frame turned back into CCI axes - so the current CCI-to-CCF rotation carries them, and the predicted hit, into it.
        doubleQuat toCcf = parent.GetCci2Ccf();
        double3 siteUp = SiteDirCcf();
        double3 hitCcf = new double3(nom.Fx.V, nom.Fy.V, nom.Fz.V).Transform(toCcf);
        double3 miss = OnGround(hitCcf - siteUp * siteRadius, siteUp);

        // HOW FAR THE IMPACT MOVES PER m/s OF PUSH ALONG EACH LIFT AXIS. Measured, not assumed: lift "up" stretches the impact while descending, but climbing steeply after a boostback it points mostly backwards and can pull the impact in, and the planet turning under a long coast couples the two axes. Steering on the geometric assumption pushed the impact the wrong way for as long as the vehicle was still climbing.
        double3 perUp = OnGround(ApplyJacobian(dG, liftUpCci).Transform(toCcf), siteUp);
        double3 perLeft = OnGround(ApplyJacobian(dG, liftLeftCci).Transform(toCcf), siteUp);
```

## Game APIs touched

| Member | Use |
|---|---|
| `IParentBody.GetCci2Ccf()`, `GetAngularVelocity()`, `GetAngularVelocityCci()`, `Mu`, `MeanRadius` ([current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L15-L90)) | Body-fixed miss, co-rotating air, gravity and radius for the coast model |
| `Vehicle.PhysicsEnvironment` -> `PhysicsEnvironment.AtmosphericDensity` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L543), [current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L24)) | Dynamic pressure at the vehicle |
| `Vehicle.TotalMass`, `Orbit.StateVectors` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L567)) | State for the prediction and the lift-to-acceleration conversion |

## Pitfalls and version notes

- Do not assume lift "up" moves the impact downrange: right after a boostback the vehicle may still be
  climbing, where that axis points mostly backwards; the author's first version pushed the impact a kilometre
  the wrong way. Measure the sensitivity.
- Commanding degrees straight from a PID stalls in thick air because the lift curve is not linear; invert the
  measured curve instead.
- The integral term defaults off: over a minutes-long glide it accumulates a stale bias that drives the
  vehicle past the site.
- Under stock aerodynamics a body may produce no usable lift (`|C_L/C_D| < 0.02` at the maximum AoA); the glide
  then holds alpha 0 rather than wagging the vehicle.
- The README notes that, of the guidance modes, only single-craft ascent had been flown in the game from this
  tree; the glide's validation is the author's flat-earth closed-loop runs.

## See also

- [Booster boostback with a drag-integrated impact predictor](boostback-impact-prediction.md)
- [Measure a vehicle's drag table](drag-table-measurement.md)
- [Guidance attitude and gimbal actuation](guidance-attitude-and-gimbal-actuation.md)
- [Convert vehicle state to a site frame](vehicle-state-to-site-frame.md)
- KSAero: [Body-of-revolution aerodynamics](../../ksaero/features/body-of-revolution-aerodynamics.md)
- [Glide a booster onto its site with a zero-effort-miss law and an inverted lift curve](../../../howto/flight-and-orbits/guidance/fly-a-boostback-and-glide-to-a-landing-site/fly-a-boostback-and-glide-to-a-landing-site-2.md) - how-to article demonstrating this feature
- [Steer on a predicted impact point with a zero-effort-miss law](../../../howto/flight-and-orbits/guidance/steer-by-zero-effort-miss/steer-by-zero-effort-miss-2.md) - how-to article demonstrating this feature
- [Speed of sound from the atmosphere reference for a measurement tool](../../../howto/physics/aero-and-ground/replace-the-stock-aerodynamic-force-model/replace-the-stock-aerodynamic-force-model-2.md) - how-to article demonstrating this feature

