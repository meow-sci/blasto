---
type: How-To
title: Steer on a predicted impact point with a zero-effort-miss law
description: Close the loop on the miss between a predicted impact point and a site with a gain over time-to-go squared, a measured impact Jacobian, a time-to-go floor, the integral term off for long glides and a wall-clock re-solve cadence
category: flight-and-orbits
group: guidance
topic: steer-by-zero-effort-miss
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/aerodynamic-glide-impact-steering]
techniques: [closed-loop-guidance, zero-effort-miss, automatic-differentiation, harmony-prefix]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers]
game_types: [KSA.Vehicle, KSA.Orbit, KSA.IParentBody, KSA.PhysicsEnvironment, KSA.FlightComputer]
tags: [zero-effort-miss, zem, impact-point, time-to-go, pid, anti-windup, jacobian, glide]
---
Outcome: a falling vehicle is steered so its predicted impact point walks onto a site and the command returns to zero as the miss does. The quantity steered is the zero-effort miss (ZEM): where the vehicle lands if nothing more is commanded. KSA-AdvancedFlightComputer (AFC) uses it to glide a returning booster; this article isolates the law and its tuning so you can reuse it for any predictor and any actuator. The full booster glide, including the lift-curve inversion and attitude trim, is [approach 2 of the boostback topic](../fly-a-boostback-and-glide-to-a-landing-site/fly-a-boostback-and-glide-to-a-landing-site-2.md).[Aerodynamic glide impact steering](../../../../mods/ksa-advancedflightcomputer/features/aerodynamic-glide-impact-steering.md)

Prerequisites: an impact predictor that returns the impact point and the time of flight (see [Predict impact points with drag](../predict-impact-point-and-build-a-ballistic-computer/predict-impact-point-and-build-a-ballistic-computer-1.md)), and a per-vehicle guidance step (AFC uses a Harmony prefix on `Vehicle.PrepareWorker`; see [Run an autopilot for every vehicle each step](../../autopilot/run-an-autopilot-for-every-vehicle-each-step/overview.md)).

## Background: how the game does it

- **State to predict from.** `Vehicle.Orbit.StateVectors` gives `PositionCci` and `VelocityCci` in the parent's inertial frame; `IParentBody.Mu`, `MeanRadius` and `GetAngularVelocity()` give gravity, radius and the spin rate about CCI +Z.[^ksa-orbit] [^ksa-iparentbody] [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1185)
- **Ground frame.** `IParentBody.GetCci2Ccf()` rotates the predicted hit into the body-fixed frame, where a site does not move.[^ksa-iparentbody]
- **Dynamic pressure.** `Vehicle.PhysicsEnvironment.AtmosphericDensity` is the density at the vehicle.[^ksa-physicsenvironment] [current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L24)
- **Actuation.** The law produces a thrust-axis direction in CCI that the guidance step hands to the stock flight computer; see [Drive the flight computer](../../autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/overview.md).

Why ZEM rather than [proportional navigation](steer-by-zero-effort-miss-1.md): the target is a place, the vehicle is falling under drag, and the predicted miss already folds gravity, drag and the planet's rotation in. Commanding acceleration `Kp * ZEM / tgo^2` is the classic ZEM law; `Kp = 3` is the textbook gain.

## Lifecycle and timing

- **`frame:dispatch-solvers`, main thread.** The glide is a phase of the boostback mode and runs inside AFC's `Vehicle.PrepareWorker` prefix, the last settled point before that vehicle's next step is snapshotted. It reads the state the previous step committed and returns a direction; the next physics step tracks it.
- **Cadence on the wall clock.** The PID and the Jacobian re-solve every 100 ms of real time (`Environment.TickCount64`), and the command is held in between. Simulated-time cadences re-solve every step under warp. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Pick the gains as fields on the per-vehicle state.** Defaults from the author's closed-loop runs: `Kp = 5` (3 is textbook; 5 tolerates attitude lag better), `Ki = Kd = 0`:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/VehicleAutopilotState.cs L1130-L1144
    // Boostback glide: a PID on the coast impact point, steering the angle of attack. See Guidance/BoostbackGlide.cs.
    //
    // The PID asks for the impact to move back towards the site at (Kp miss + Ki integral + Kd rate) / tgo; the impact Jacobian turns that into lift, and the measured lift curve turns the lift into an angle. The defaults are the ones flat-earth closed-loop runs through KSAero's aero settled on.

    /// <summary>Proportional gain, dimensionless: Kp * miss / tgo^2 is the lateral acceleration asked for. 3 is the textbook zero-effort-miss law; 5 holds up better when the attitude lags the command.</summary>
    public double GlideKp = 5.0;

    /// <summary>Integral gain, 1/s. Off by default - over a long glide the integral of a shrinking miss becomes a stale bias.</summary>
    public double GlideKi = 0.0;

    /// <summary>Derivative gain, s, on the low-passed rate the miss is changing at.</summary>
    public double GlideKd = 0.0;

    /// <summary>Largest angle of attack the glide commands, degrees off engine-first into the wind.</summary>
    public double GlideMaxAoaDeg = 15.0;
```

2. **Floor time-to-go and fix the cadence.** Dividing by `tgo` squared asks for infinity in the last seconds; floor it at 5 s so the end of the glide asks for the airframe's limit. Re-solve every 100 ms of wall clock:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/BoostbackGlide.cs L34-L53
    /// <summary>Floor on the time to go the gains are divided by, s, so the last seconds ask for the airframe's limit rather than for infinity.</summary>
    private const double GlideMinTgo = 5.0;

    /// <summary>Low-pass on the miss rate the derivative term uses, s. The prediction steps at its own recompute rate, so the raw difference is a spike train.</summary>
    private const double GlideRateTau = 1.0;
    // ...
    /// <summary>How often the glide re-solves, ms - one velocity Jacobian of the coast prediction each time, about 3.6 ms, the same cadence the burn steers at.</summary>
    private const long GlideIntervalMs = 100;
```

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/BoostbackGlide.cs L116-L122
        // Re-solve on its own cadence. Between solves the command is held, and the slew smooths the steps.
        long tick = Environment.TickCount64;
        if (tick - _s.GlideTick >= GlideIntervalMs)
        {
            _s.GlideTick = tick;
            UpdateGlidePid(vehicle, orbit, parent, heading, up, left, speed, now);
        }
```

3. **Predict once with derivatives.** One forward-mode run of the coast predictor from the current CCI state returns the impact point, its time of flight, and a 3x3 velocity Jacobian (`dG`): how far the impact moves per m/s of push. That Jacobian is what makes the law work whatever the geometry; AFC's first version assumed "lift up stretches the impact" and pushed it a kilometre the wrong way while the booster was still climbing:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/BoostbackGlide.cs L216-L242
        double siteRadius = parent.MeanRadius + SiteTerrainHeight(parent);
        var sys = new DragCoastSystem
        {
            Mu = parent.Mu,
            OmegaZ = parent.GetAngularVelocity(),
            MeanRadius = parent.MeanRadius,
            AreaOverMass = aero.ReferenceArea / mass,
            Alpha = 0.0,
            Table = aero.Table,
            Atmosphere = aero.Atmosphere,
        };
        var opt = ImpactOptions.Default(parent.MeanRadius);
        opt.MaxTime = ImpactHorizonMinutes * 60.0;
        opt.PathStride = 0;
        opt.TargetRadius = siteRadius;

        double3 r0 = orbit.StateVectors.PositionCci;
        double3 v0 = orbit.StateVectors.VelocityCci;
        Span<double> x0 = stackalloc double[ImpactPredictor.N] { r0.X, r0.Y, r0.Z, v0.X, v0.Y, v0.Z };
        _s.ImpactScratch ??= new Dual[ImpactPredictor.ScratchLength];
        Span<double> dG = stackalloc double[9];
        ImpactPrediction nom = ImpactPredictor.VelocityJacobian(sys, x0, opt, _s.ImpactScratch, dG, default, default);
        if (!nom.Hit)
        {
            HoldGlide("No impact within the prediction horizon - holding alpha 0.");
            return;
        }
```

4. **Measure the miss on the ground at the site**, in the body-fixed frame, and project the Jacobian's effect of each actuator axis onto the same plane (`perUp`, `perLeft`); the code is step 3 of the [boostback glide article](../fly-a-boostback-and-glide-to-a-landing-site/fly-a-boostback-and-glide-to-a-landing-site-2.md). Low-pass the miss rate for the derivative term, because the prediction updates at its own cadence and the raw difference is a spike train:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/BoostbackGlide.cs L259-L262
        if (_s.GlidePrevValid && dt > 1e-6)
            _s.GlideRateCcf += ((miss - _s.GlidePrevMissCcf) / dt - _s.GlideRateCcf) * Blend(GlideRateTau, dt);
        _s.GlidePrevMissCcf = miss;
        _s.GlidePrevValid = true;
```

5. **Ask for an impact rate, not an acceleration.** The PID output divided by `tgo` is the rate the impact should move back toward the site; a damped least-squares solve over the two measured actuator columns turns it into accelerations. Because the Jacobian itself grows like `tgo`, the acceleration requested is `Kp * miss / tgo^2`, the ZEM law, and one gain set holds from cutoff to the ground. Integrate only while the command is not saturated, or while the integral is unwinding:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/BoostbackGlide.cs L264-L277
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
```

6. **Map the acceleration to your actuator.** AFC turns it into a lift coefficient `a m / (q A)` and inverts the measured lift curve to an angle of attack (dynamic pressure `q = 0.5 rho v^2` from `PhysicsEnvironment.AtmosphericDensity`). A tail-kit store would clamp it to a fin-authority g limit instead ([approach 3](steer-by-zero-effort-miss-3.md)).
7. **Hand the direction to the guidance phase switch.** The glide returns a CCI thrust-axis direction from `GlideDirection`, which the boostback step uses as its wanted attitude:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/Boostback.cs L497-L499
            case BoostbackPhase.Glide:
                want = GlideDirection(vehicle, orbit, parent, now, dt);
                break;
```

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Modes/BoostbackGlide.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/BoostbackGlide.cs#L179-L290) | `UpdateGlidePid`: prediction, miss, rate, ZEM PID, anti-windup |
| [AdvancedFlightComputer/Features/Guidance/Modes/BoostbackGlide.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/BoostbackGlide.cs#L331-L349) | `SolveGlideLift`: damped two-axis least squares |
| [AdvancedFlightComputer/Features/Guidance/Modes/VehicleAutopilotState.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/VehicleAutopilotState.cs#L1130-L1171) | Gains, limits and glide state |
| [AdvancedFlightComputer/Features/Guidance/Numerics/Flight/ImpactPredictor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Numerics/Flight/ImpactPredictor.cs) | Drag-integrated coast and its velocity Jacobian |
| [AdvancedFlightComputer/Features/Guidance/Modes/Boostback.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Boostback.cs#L490-L504) | Phase switch that calls the glide |

## Variations and gotchas

- **Integral off for long horizons.** Over a minutes-long glide the integral of an ordinary shrinking miss becomes a large stale bias that drives the vehicle past the site; AFC leaves `Ki = 0` and exposes it on the panel.
- **Derivative bought nothing.** The overshoot it was meant to damp turned out to be attitude lag; AFC removes that with a separate trim on the measured flown tilt.
- **Measure the sensitivity.** A geometric assumption of which push moves the impact which way is wrong while climbing and when the planet turns under a long coast.
- **Hold rather than wag.** When the actuator has no authority (no lift under the active aero model, above the atmosphere, no impact in the horizon) the glide holds its neutral command and reports why.
- **Validation.** The author's header records flat-earth closed-loop runs landing within 2 m; the AFC README says only single-craft ascent had been flown in game from this tree.

## See also

- [Overview](overview.md); [Proportional navigation](steer-by-zero-effort-miss-1.md); [Predicted-impact guidance with ground designation](steer-by-zero-effort-miss-3.md)
- [Fly a boostback burn and glide a booster to its landing site](../fly-a-boostback-and-glide-to-a-landing-site/overview.md)
- [Predict impact points with drag and build a ballistic computer](../predict-impact-point-and-build-a-ballistic-computer/overview.md)
- [Aerodynamic glide impact steering (mod feature)](../../../../mods/ksa-advancedflightcomputer/features/aerodynamic-glide-impact-steering.md)

[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-physicsenvironment]: KSA.PhysicsEnvironment (decompiled, build 2026.10.10.5554)
