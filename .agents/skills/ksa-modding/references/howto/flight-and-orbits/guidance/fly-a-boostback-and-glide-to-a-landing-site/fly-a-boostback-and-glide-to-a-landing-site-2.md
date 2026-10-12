---
type: How-To
title: Glide a booster onto its site with a zero-effort-miss law and an inverted lift curve
description: Steer the unpowered fall by tilting the engine end off the airflow, using the impact Jacobian to size the wanted lift and the measured lift curve to turn it into an angle of attack
category: flight-and-orbits
group: guidance
topic: fly-a-boostback-and-glide-to-a-landing-site
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/aerodynamic-glide-impact-steering]
techniques: [closed-loop-guidance, automatic-differentiation, harmony-prefix]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers]
game_types: [KSA.Vehicle, KSA.IParentBody, KSA.PhysicsEnvironment, KSA.Orbit, KSA.FlightComputer]
tags: [glide, angle-of-attack, zero-effort-miss, impact-point, lift-curve, jacobian, booster-recovery]
---
Outcome: after the boostback burn ends, a returning booster still has minutes of falling through the atmosphere in which dispersions grow. This article steers that fall with the airframe's own lift: the engine end is pointed into the airflow, tilted by a commanded angle of attack, and the loop closes on the predicted impact point until the miss is zero and the command is back at alpha 0. Nothing about the airframe is assumed: the lift direction, the lift curve and the impact sensitivity are all measured. KSA-AdvancedFlightComputer is the demonstration.[Aerodynamic glide impact steering](../../../../mods/ksa-advancedflightcomputer/features/aerodynamic-glide-impact-steering.md)

Prerequisites: the drag and lift tables and the coast predictor from [Predict an impact point with gravity, drag and a measured drag table](../predict-impact-point-and-build-a-ballistic-computer/predict-impact-point-and-build-a-ballistic-computer-1.md), and a way to command an attitude ([Drive the flight computer](../../autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/overview.md)). The glide is the last phase of the state machine in [approach 1](fly-a-boostback-and-glide-to-a-landing-site-1.md). The law itself is the subject of [Steer by predicted miss](../steer-by-zero-effort-miss/overview.md).

## Background: how the game does it

- **Lift exists only through the active aero model.** Stock's box model produces none, so the glide checks the sign of `C_L / C_D` at the maximum angle of attack from the measured table every update and holds alpha 0 when it is under 0.02.
- **Air-relative velocity** is `orbit.StateVectors.VelocityCci - cross(parent.GetAngularVelocityCci(), r)`, the same term `PhysicsStates.ComputeAirVelocityBody` subtracts.[^ksa-physicsstates] [^ksa-iparentbody]
- **Dynamic pressure** is `0.5 * rho * v^2` with `rho` from `Vehicle.PhysicsEnvironment.AtmosphericDensity`.[^ksa-physicsenvironment]
- **Body-fixed frame.** `IParentBody.GetCci2Ccf()` carries the miss into the frame fixed to the ground.[^ksa-iparentbody]

## Lifecycle and timing

- **`frame:dispatch-solvers`, main thread.** The glide runs inside the boostback step in the `Vehicle.PrepareWorker` prefix, so an unfocused booster is flown. It reads the state the previous step committed and returns a thrust-axis direction in CCI; the flight computer tracks it during the next physics step.
- **Cadence.** The PID and the Jacobian run every 100 ms of wall-clock time (`GlideIntervalMs`; one Jacobian is about 3.6 ms per the author); the tilt trim and the command run every step. Wall clock keeps it from running every step under time warp. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Start from surface retrograde, engine first.** Alpha is retrograde-first: 0 means the engine end points into the wind. Build the airflow direction and the ground track (carried over from the last step that had one when falling straight down):

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/BoostbackGlide.cs L89-L96
        double3 retro = SurfaceRetrogradeCci(orbit, parent);
        double3 r = orbit.StateVectors.PositionCci;
        double3 vAir = orbit.StateVectors.VelocityCci - double3.Cross(parent.GetAngularVelocityCci(), r);
        double speed = vAir.Length();
        if (!(speed > 1.0))
            return retro;
        double3 vHat = vAir / speed;
        double3 rHat = r.NormalizeOrZero();
```

2. **Decide whether there is lift to steer with, and which way to tilt.** The sign of `C_L` at the maximum angle of attack says whether the engine end tilts toward the lift (a slender booster) or away (a blunt capsule); a model with no lift holds alpha 0:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/BoostbackGlide.cs L193-L204
        double maxAoa = Math.Clamp(_s.GlideMaxAoaDeg, 0.0, 60.0);
        double maxAoaRad = maxAoa * Math.PI / 180.0;
        double mach = aero.Atmosphere?.Mach(speed) ?? 0.0;
        double clMaxAoa = aero.LiftAt(mach, maxAoaRad);
        double cdMaxAoa = aero.Table.Cd(mach, maxAoaRad);
        _s.GlideLiftSign = cdMaxAoa > 0.0 && Math.Abs(clMaxAoa / cdMaxAoa) >= GlideMinLiftToDrag ? Math.Sign(clMaxAoa) : 0;
        if (_s.GlideLiftSign == 0 || !(maxAoa > 0.0))
        {
            _s.GlideIntCcf = double3.Zero;
            HoldGlide("The active aero model makes no lift to steer with - holding alpha 0.");
            return;
        }
```

3. **Measure the miss and what lift does to it.** The miss is the alpha-0 coast impact minus the site's radius point, projected onto the ground plane in the body-fixed frame. One forward-mode velocity Jacobian of the coast prediction turns a unit of lift along each axis into impact metres; the sensitivity is measured, not assumed, because right after a boostback "up" lift points mostly backwards:

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

4. **Apply the zero-effort-miss law.** The wanted impact rate is `-(Kp miss + Ki integral + Kd rate) / tgo`; because the Jacobian grows with time to go, the lift requested is `Kp miss / tgo^2`. Defaults are `Kp = 5`, `Ki = Kd = 0`, maximum angle of attack 15 degrees, and `tgo` floored at 5 s:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/BoostbackGlide.cs L264-L273
        // THE PID, as the rate the impact should move at: (Kp miss + Ki integral + Kd rate) / tgo, back towards the site. With the Jacobian scaling like the time to go, the lift this asks for is the zero-effort-miss law's Kp miss / tgo^2.
        double tgo = Math.Max(nom.TimeOfFlight.V, GlideMinTgo);
        _s.GlideTgo = tgo;
        double3 pid = _s.GlideKp * miss + _s.GlideKi * _s.GlideIntCcf + _s.GlideKd * _s.GlideRateCcf;
        SolveGlideLift(perUp, perLeft, -pid / tgo, out double aUp, out double aLeft);
        double accel = Math.Sqrt(aUp * aUp + aLeft * aLeft);
        _s.GlideAccelCmd = accel;

        double clWanted = accel * mass / (q * aero.ReferenceArea);
        double alphaDeg = GlideAoaForLift(aero, mach, _s.GlideLiftSign, clWanted, maxAoa, out bool saturated);
```

5. **Solve the two-axis lift in damped least squares.** Tikhonov damping at `1e-3` of the mean authority keeps a direction the lift can barely move from asking for unbounded lift:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/BoostbackGlide.cs L331-L349
    private static void SolveGlideLift(double3 perUp, double3 perLeft, double3 want, out double aUp, out double aLeft)
    {
        double m11 = double3.Dot(perUp, perUp);
        double m12 = double3.Dot(perUp, perLeft);
        double m22 = double3.Dot(perLeft, perLeft);
        double damp = GlideSolveDamping * (m11 + m22);
        m11 += damp;
        m22 += damp;
        double r1 = double3.Dot(perUp, want);
        double r2 = double3.Dot(perLeft, want);
        double det = m11 * m22 - m12 * m12;
        if (!(det > 0.0))
        {
            aUp = aLeft = 0.0;
            return;
        }
        aUp = (m22 * r1 - m12 * r2) / det;
        aLeft = (m11 * r2 - m12 * r1) / det;
    }
```

6. **Invert the measured lift curve.** `C_L = a m / (q A)` is turned into the smallest angle of attack reaching it by walking `C_L(Mach, alpha)` in 0.25 degree steps and interpolating, with a linear ramp near zero so the command stays continuous; a PID that commanded degrees directly would stall in thick air:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/BoostbackGlide.cs L291-L317
    private static double GlideAoaForLift(KsaAeroSweep.Result aero, double mach, int sign, double clWanted,
                                          double maxAoaDeg, out bool saturated)
    {
        const double DegToRad = Math.PI / 180.0;
        double clMax = sign * aero.LiftAt(mach, maxAoaDeg * DegToRad);
        double floor = GlideLiftRamp * clMax;
        double target = Math.Max(clWanted, floor);
        saturated = true;
        if (target >= clMax)
            return maxAoaDeg;

        double prevAoa = 0.0, prevCl = 0.0;
        for (double aoa = GlideAoaStepDeg; aoa <= maxAoaDeg + 1e-9; aoa += GlideAoaStepDeg)
        {
            double cl = sign * aero.LiftAt(mach, aoa * DegToRad);
            if (cl >= target)
            {
                saturated = false;
                double t = cl > prevCl ? Math.Clamp((target - prevCl) / (cl - prevCl), 0.0, 1.0) : 1.0;
                double found = prevAoa + t * (aoa - prevAoa);
                return clWanted < floor ? found * Math.Max(clWanted, 0.0) / floor : found;
            }
            prevAoa = aoa;
            prevCl = cl;
        }
        return maxAoaDeg;
    }
```

7. **Trim for attitude lag, then command.** The airflow turns as the trajectory bends, so an attitude loop lagging a turning target flies a steady degree or two nobody asked for. Every step, the tilt actually flown is compared with the tilt asked for and the difference is integrated into the command (gain 0.5 per second, with anti-windup):

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/BoostbackGlide.cs L144-L150
        // Anti-windup as in the PID: a trim that is already being clipped only ever unwinds.
        double errUp = _s.GlideTiltUpDeg - flownUp;
        double errLeft = _s.GlideTiltLeftDeg - flownLeft;
        if (!sendLimited || errUp * _s.GlideTrimUpDeg < 0.0)
            _s.GlideTrimUpDeg = Math.Clamp(_s.GlideTrimUpDeg + GlideTrackGain * errUp * dt, -maxAoa, maxAoa);
        if (!sendLimited || errLeft * _s.GlideTrimLeftDeg < 0.0)
            _s.GlideTrimLeftDeg = Math.Clamp(_s.GlideTrimLeftDeg + GlideTrackGain * errLeft * dt, -maxAoa, maxAoa);
```

   The thrust axis points opposite the airflow direction tilted by the angle of attack:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/BoostbackGlide.cs L170-L176
        double aoa = sendSize / RadToDeg;
        double3 tilt = (sendUp * up + sendLeft * left).NormalizeOrZero();
        if (!(aoa > 0.0) || tilt.Length() < 0.5)
            return retro;

        // The engine end leads into the airflow, tilted off it; the thrust axis points the other way down the body.
        return -(Math.Cos(aoa) * vHat + Math.Sin(aoa) * tilt);
```

8. **Log what was asked against what was flown** (the mod writes a `glide:` line every 2 sim seconds), so a misbehaving glide can be read back from the game log.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Modes/BoostbackGlide.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/BoostbackGlide.cs) | direction, PID, lift solve, AoA inversion, trim, log line |
| [AdvancedFlightComputer/Features/Guidance/Modes/Boostback.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Boostback.cs) | phase switch that calls the glide |
| [AdvancedFlightComputer/Features/Guidance/Modes/VehicleAutopilotState.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/VehicleAutopilotState.cs) | gains and limits (`GlideKp`, `GlideMaxAoaDeg`) |
| [AdvancedFlightComputer/Features/Guidance/Numerics/Flight/ImpactPredictor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Numerics/Flight/ImpactPredictor.cs) | coast and velocity Jacobian |
| [AdvancedFlightComputer/Features/Guidance/Adapters/KsaAeroSweep.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/KsaAeroSweep.cs) | measured `C_D` and `C_L` tables |

## Variations and gotchas

- **Do not assume lift "up" moves the impact downrange.** Right after a boostback the vehicle may still be climbing, where that axis points backwards; the author's first version pushed the impact a kilometre the wrong way.
- **The integral term defaults off.** Over a minutes-long glide it accumulates a stale bias that drives the vehicle past the site; the integral winds only while the command has room.
- **Steer in any air at all.** A dispersion is cheapest to fix early; high up the lift asked for simply exceeds what thin air gives, and the loop saturates at the angle limit and reports it in the status line.
- **Under stock aerodynamics there may be no usable lift,** and the glide then holds alpha 0.
- **The prediction assumes alpha 0.** That is why the command returns to 0 as the miss closes: it is what makes the prediction come true.
- **Closed-loop validation is the author's flat-earth runs** through KSAero's aerodynamics (boosters of fineness 6 to 13 and a capsule, up to 5 s of attitude lag); in-game flight has covered only single-craft ascent.

## See also

- [Overview](overview.md); [Impact-prediction-driven boostback and glide](fly-a-boostback-and-glide-to-a-landing-site-1.md)
- [Aerodynamic glide impact steering (mod feature)](../../../../mods/ksa-advancedflightcomputer/features/aerodynamic-glide-impact-steering.md)
- [Steer by predicted miss](../steer-by-zero-effort-miss/overview.md)
- [Predict impact points with drag](../predict-impact-point-and-build-a-ballistic-computer/overview.md)

[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-physicsenvironment]: KSA.PhysicsEnvironment (decompiled, build 2026.10.10.5554)
