---
type: Mod Feature
title: Booster boostback with a drag-integrated impact predictor
description: Adopt a separated booster as its own guided vehicle, predict its impact point by integrating gravity and drag in a co-rotating atmosphere, and fly a receding-horizon boostback burn plus an engine-first glide that walks the impact onto the landing site
mod: ksa-advancedflightcomputer
category: flight-and-orbits
techniques: [harmony-prefix, custom-physics]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers]
game_types: [KSA.Vehicle, KSA.SequenceList, KSA.Decoupler, KSA.PhysicsStates, KSA.FlightComputer, KSA.PartTree]
tags: [boostback, impact-prediction, reentry, drag, guidance, booster-recovery, jacobian]
---

When a stage with its own control module separates during a guided ascent, AFC takes over the new
vehicle and flies it back toward its landing site: a settling burn, a flip, the boostback burn, then
an entry attitude and an aerodynamically steered glide, while the player keeps flying the upper stage.
Two pieces are reusable beyond boostback: recognizing a vehicle that did not exist a frame ago as "the
booster we just dropped", and predicting where an unpowered vehicle hits the ground through the
atmosphere.

## How it works

### Which stages can return

A stage can return only if the subtree that separates contains a control module (the same
`Parts.Controls.NumModules > 0` test stock uses for `Vehicle.IsControllable`). One impact prediction and
one velocity Jacobian serve every stage in the list, because only the miss depends on the target, so
listing five stages costs one prediction plus five 3 x 3 solves ([AdvancedFlightComputer/Features/Guidance/Modes/ReturnableStages.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/ReturnableStages.cs#L12-L23)).

### Adopting the separated booster

Before a staging request fires, `ArmBoosterHandover` records what the separation will throw overboard:
the detached root part ids, the landing target and a pending boostback. After the split the parts belong
to a vehicle AFC has no handle on; the per-vehicle step for an unknown, unfocused vehicle calls
`TryAdoptBooster`, which matches it against the recorded roots (first detached root, as the returnable
list does) and gives it state, target and mode. Records expire after a generous window
([AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs#L255-L420)).

### Predicting the impact

`ImpactPredictor` integrates unpowered flight in a body-centred inertial frame with +z on the spin axis:
`vdot = -mu r/|r|^3 - 1/2 rho |v_air| Cd(M, alpha) (A/m) v_air` with `v_air = v - omega x r`. The
atmosphere co-rotates, which the game also models: `PhysicsStates.ComputeAirVelocityBody` subtracts
`cross(parent angular velocity, position)` before drag ([current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L182-L190)),[^ksa-physicsstates]
and at an equatorial pad that term is about 465 m/s. The drag coefficient table comes from the vehicle
itself (see [drag table measurement](drag-table-measurement.md)). It reports why it stopped (hit the
surface, horizon reached, step budget, started below the surface, non-finite input), and gives the impact
point carried back by `omega * time of flight` so it is fixed to the ground
([AdvancedFlightComputer/Features/Guidance/Numerics/Flight/ImpactPredictor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Numerics/Flight/ImpactPredictor.cs#L1-L60)). The dynamics
are written over `Dual`, a two-double forward-mode automatic-differentiation number, so seeding one
velocity component at a time yields the velocity Jacobian of the impact point from the same integrator,
allocation-free ([AdvancedFlightComputer/Features/Guidance/Numerics/Math/Dual.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Numerics/Math/Dual.cs)).

### Two guidance laws, two jobs

- `ImpactSteering.Correction` answers "smallest impulse now that puts the impact on the site", with a
  Gauss-Newton/pseudo-inverse step on the velocity Jacobian (the Jacobian is rank 2, because a crossing
  point of a sphere stays on the sphere). It is cheap (about 3.6 ms per the author) and is used only to
  decide whether a burn is worth lighting and how much is left to correct
  ([docs/guidance/reference-material/impact-steering.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/docs/guidance/reference-material/impact-steering.md)).
- What flies is `BoostbackShooter` in `PoweredBurn`: it integrates the powered arc and optimizes the whole
  burn, re-solved every 2 s from the live state (receding horizon). On the author's reference arc the
  instantaneous correction points 33 degrees below the horizon while the optimized burn points 25 degrees
  above it for 18 % less propellant ([AdvancedFlightComputer/Features/Guidance/Modes/Boostback.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Boostback.cs#L1-L40)).

### Phases

Separation at minimum throttle for 2 s (settle propellant, clear the stack); rotation of the thrust axis to
the boostback direction at a limited rate with the turning rate fed forward to the flight computer; the
boostback burn following the shooter; at `tgo <= 5 s` a 10 Hz impulsive correction, frozen at
`tgo <= 2 s`, cutoff on sensed delta-v; then the glide (`BoostbackGlide`): engine-first into the wind,
with a PID on the coast impact point steering the angle of attack until the impact lies on the site, so
the command returns to alpha 0 and the prediction (which assumes alpha 0) comes true.

## Lifecycle and timing

Everything runs in the `Vehicle.PrepareWorker` prefix for the booster vehicle (`frame:dispatch-solvers`,
main thread), so a booster is flown even when it is not the controlled vehicle. The predictor and the
shooter are synchronous numerical work on the main thread, bounded by step budgets and the 2 s re-solve
cadence; attitude and throttle reach the physics step through the stock flight computer (see
[guidance attitude and gimbal actuation](guidance-attitude-and-gimbal-actuation.md)).

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Modes/Boostback.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Boostback.cs) | Boostback phases and laws |
| [AdvancedFlightComputer/Features/Guidance/Modes/BoostbackGlide.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/BoostbackGlide.cs) | Engine-first glide with impact PID |
| [AdvancedFlightComputer/Features/Guidance/Modes/ReturnableStages.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/ReturnableStages.cs) | Which stages can return, shared Jacobian |
| [AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs) | Booster hand-over at separation |
| [AdvancedFlightComputer/Features/Guidance/Numerics/Flight/ImpactPredictor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Numerics/Flight/ImpactPredictor.cs) | Drag-integrated impact prediction |
| [AdvancedFlightComputer/Features/Guidance/Numerics/Flight/ImpactSteering.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Numerics/Flight/ImpactSteering.cs) | Minimum-norm impact correction |
| [AdvancedFlightComputer/Features/Guidance/Numerics/Flight/PoweredBurn.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Numerics/Flight/PoweredBurn.cs) | Powered-arc shooter |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Guidance/GuidanceBoosterHandoverTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Guidance/GuidanceBoosterHandoverTest.cs) | Headless hand-over test |

## Snippets

```csharp
// AdvancedFlightComputer/Features/Guidance/Numerics/Flight/ImpactPredictor.cs L80-L112
    public readonly void Derivative(Dual t, ReadOnlySpan<Dual> x, Span<Dual> dx)
    {
        Dual rx = x[0], ry = x[1], rz = x[2];
        Dual vx = x[3], vy = x[4], vz = x[5];

        // rdot = v
        dx[0] = vx;
        dx[1] = vy;
        dx[2] = vz;

        // Newtonian gravity.
        // The floor keeps Sqrt's derivative finite at the origin, which the integrator should never visit but a diverged iterate might.
        Dual r2 = rx * rx + ry * ry + rz * rz + 1e-12;
        Dual rlen = Dual.Sqrt(r2);
        Dual gk = -Mu / (r2 * rlen);

        dx[3] = gk * rx;
        dx[4] = gk * ry;
        dx[5] = gk * rz;

        if (!HasDrag)
            return;

        // Air-relative velocity: v - omega x r, with omega = (0, 0, w) so omega x r = (-w*ry, w*rx, 0).
        Dual ax = vx + OmegaZ * ry;
        Dual ay = vy - OmegaZ * rx;
        Dual az = vz;

        Dual speed2 = ax * ax + ay * ay + az * az + 1e-18;
        Dual speed = Dual.Sqrt(speed2);

        Dual rho = Atmosphere.Density(rlen - MeanRadius);
        // Above the atmosphere Density returns a hard zero with a zero derivative, so this short-circuits to no drag AND no drag sensitivity - which is correct, not merely convenient: there is no air there to be sensitive to.
```

## Game APIs touched

| Member | Use |
|---|---|
| `PhysicsStates.ComputeAirVelocityBody` ([current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L182)) | The co-rotating-air convention the predictor matches |
| `SequenceList`, `Decoupler`, `Part` subtree walks ([current/decomp/KSA/SequenceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequenceList.cs)) | What a separation detaches |
| `Vehicle.Parts.Controls.NumModules` | Returnable-stage test |

## Pitfalls and version notes

- Instantaneous impact-point guidance is a poor law to fly for a long burn; use it to decide, and
  optimize the burn as a whole.
- Match the game's atmosphere model (co-rotation, the vehicle's own drag) or the predicted impact is off
  by kilometres.
- Separation creates a new `Vehicle`; record identifying part ids before the split.
- The author states that only single-craft ascent has been flown in the game; boostback is exercised by
  harness tests and the console checks.

## See also

- [Drag table measurement](drag-table-measurement.md)
- [UPFG ascent guidance](upfg-ascent-guidance.md)
- [Deorbit landing planner](deorbit-landing-planner.md)
- [Guidance vehicle step loop](guidance-vehicle-step-loop.md)
- [Adopt a separated booster as its own guided vehicle](../../../howto/flight-and-orbits/autopilot/run-an-autopilot-for-every-vehicle-each-step/run-an-autopilot-for-every-vehicle-each-step-2.md) - how-to article demonstrating this feature
- [Adopt a separated booster and fly a boostback burn from impact predictions](../../../howto/flight-and-orbits/guidance/fly-a-boostback-and-glide-to-a-landing-site/fly-a-boostback-and-glide-to-a-landing-site-1.md) - how-to article demonstrating this feature
- [Predict an impact point with gravity, drag and a measured drag table](../../../howto/flight-and-orbits/guidance/predict-impact-point-and-build-a-ballistic-computer/predict-impact-point-and-build-a-ballistic-computer-1.md) - how-to article demonstrating this feature
- [Impact prediction in a co-rotating atmosphere](../../../howto/physics/mod-simulation/apply-drag-and-gravity-to-a-mod-simulated-body/apply-drag-and-gravity-to-a-mod-simulated-body-3.md) - how-to article demonstrating this feature

[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
