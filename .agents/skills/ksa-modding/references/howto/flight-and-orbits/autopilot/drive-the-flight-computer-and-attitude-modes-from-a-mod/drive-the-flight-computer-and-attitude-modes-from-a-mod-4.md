---
type: How-To
title: Custom attitude in an inertial frame plus turning-rate feedforward, gimbal commands and thrust inversion
description: Command an inertial Custom attitude from the PrepareWorker prefix, add the target's own turning rate with a postfix on the private UpdateAttitudeTarget, write gimbal demands after ComputeControl, and turn a thrust demand into a throttle
category: flight-and-orbits
group: autopilot
topic: drive-the-flight-computer-and-attitude-modes-from-a-mod
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/guidance-attitude-and-gimbal-actuation
  - mods/ksa-advancedflightcomputer/features/engine-performance-and-throttle-inversion
techniques: [harmony-postfix, conditional-weak-table, root-finding]
hooks:
  - harmony-prefix KSA.Vehicle.PrepareWorker
  - harmony-postfix KSA.FlightComputer.UpdateAttitudeTarget
  - harmony-postfix KSA.FlightComputer.ComputeControl
lifecycle: [frame:dispatch-solvers, physics:vehicle-solver]
game_types:
  - KSA.FlightComputer
  - KSA.GimbalController
  - KSA.GimbalControllerState
  - KSA.EngineController
  - KSA.RocketCore
  - KSA.VehicleReferenceFrameEx
  - KSA.Vehicle
tags: [attitude, feedforward, gimbal, tvc, throttle, thrust-inversion, conditional-weak-table]
---

Outcome: guidance drives the stock flight computer instead of flying with its own physics: pointing through the stock attitude controller improved with a feedforward of the target's own turning rate, planned gimbal torque written directly to the gimbals, and throttle computed from the thrust the plan wants.
KSA-AdvancedFlightComputer implements all three, crossing from the main thread to the vehicle worker without locks.[Steer through the stock flight computer with a rate feedforward and direct gimbal commands](../../../../mods/ksa-advancedflightcomputer/features/guidance-attitude-and-gimbal-actuation.md)[Thrust at ambient pressure, the structural throttle cap and thrust-to-throttle inversion](../../../../mods/ksa-advancedflightcomputer/features/engine-performance-and-throttle-inversion.md) Prerequisites: StarMap and Harmony.

## Background: how the game does it

`FlightComputer.AttitudeTarget` carries `Target2Cci` and `RatesCci`; the tracking error is `ctrlRates - RatesCci`. The `Custom` branch fills `RatesCci` with the frame's rate, which is zero for an inertial frame, so the stock controller treats every new target as stationary and chases it.[^ksa-flightcomputer]
[current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1264-L1303) `UpdateAttitudeTarget` is private and called by `ComputeControl` right before the attitude error. `FlightComputer.CopyFrom` assigns `VehicleConfig` (a `FlightComputer.VehicleConfigInfo`) and `BurnPlan` by reference, so those objects are shared between the vehicle's and the worker's copies
and can key a lock-free table (see [Reach the physics worker through the references FlightComputer.CopyFrom shares](../../../../mods/ksa-advancedflightcomputer/features/flight-computer-worker-copy-channels.md)). Gimbal commands are `GimbalControllerState.CommandY` and `CommandZ`.[^ksa-gimbalcontrollerstate]

## Lifecycle and timing

| Write | Where | Thread |
|---|---|---|
| Custom attitude, published rate and gimbal demand, throttle input | `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`) | main |
| `RatesCci` feedforward | `FlightComputer.UpdateAttitudeTarget` postfix (`physics:vehicle-solver`) | vehicle worker |
| Gimbal `CommandY/Z` | `FlightComputer.ComputeControl` postfix via the sink (see [approach 5](drive-the-flight-computer-and-attitude-modes-from-a-mod-5.md)) | vehicle worker |

The main thread publishes before the solver job is queued, so the publish happens-before the worker read. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Command the attitude from the prefix.** Solve `Concatenate(value, frame2Cci) == desired` for `value`, write `CustomAttitudeTarget`, set `AttitudeFrame = EclBody` and `TrackTarget(Custom)`; set `RollMode` only when a roll is deliberately forced (decoupled is the default and discards the target's roll):

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs L1396-L1440
private static void CommandAttitude(Vehicle vehicle, IParentBody parent, double3 dir,
                                    bool fullEngage, double3? rollRef = null)
{
    double3 r = vehicle.Orbit.StateVectors.PositionCci;

    float3 posDir = float3.Pack(double3.Normalize(r));
    float3 steerDir = float3.Pack(double3.Normalize(dir));

    doubleQuat desiredBody2Cci = rollRef.HasValue
        ? SteerBody2Cci(double3.Normalize(dir), rollRef.Value)
        : BurnTarget.ComputeBurnBody2Cci(posDir, steerDir);
    doubleQuat frame2Cci = VehicleReferenceFrameEx.GetEclBody2Cci(parent.GetCce2Cci());

    // Solve Concatenate(value, frame2Cci) == desired  ->  value = Concatenate(desired, inverse(frame2Cci)).
    doubleQuat value = doubleQuat.Concatenate(desiredBody2Cci, doubleQuat.Inverse(frame2Cci));
    double3 euler = value.ToRollYawPitchRadians();

    var fc = vehicle.FlightComputer;
    if (!TryTakeCraft(vehicle, acquire: true))
        return;
    _s.AttitudeOwnership.BeginWrite(fc);
    fc.CustomAttitudeTarget = euler;

    // WHETHER THE FLIGHT COMPUTER LOOKS AT THE ROLL WE JUST COMMANDED. UpdateAttitudeTrackError computes a roll term only when RollMode is not Decoupled; decoupled is the default and discards the target's roll entirely, tracking pointing alone. That is the right behaviour for an ascent - roll is the axis a launch vehicle has least authority about and nothing in the trajectory needs a particular one - so it stays decoupled unless the roll is being forced deliberately.
    //
    // Also decoupled below ForceRollMinAltM even with the box ticked: right off the pad is exactly where the vehicle has the least authority to fight a commanded roll and the least room to recover if it can't, so a forced roll waits for some clearance first.
    //
    // Written every step, not only on engagement: the box can be ticked mid-ascent, and un-ticking it has to give the roll freedom back.
    if (_s.Running)
    {
        double altM = r.Length() - parent.MeanRadius;
        fc.RollMode = _s.ForceRoll && altM >= ForceRollMinAltM
            ? FlightComputerRollMode.Up
            : FlightComputerRollMode.Decoupled;
    }

    if (fullEngage)
    {
        fc.AttitudeFrame = VehicleReferenceFrame.EclBody;
        fc.TrackTarget(FlightComputerAttitudeTrackTarget.Custom);
    }
    _s.AttitudeOwnership.EndWrite(fc, writesRoll: _s.Running);
}

/// <summary>
```

   Bracket every write with an ownership record (`AttitudeOwnership.BeginWrite`/`EndWrite`) that snapshots the original fields once and the last written values every time, for takeover detection and conditional restore (see [Run a closed-loop autopilot for every vehicle each step and arbitrate control with the player](../run-an-autopilot-for-every-vehicle-each-step/overview.md)).
2. **Publish the rate** guidance wants for that vehicle in a `ConditionalWeakTable` keyed by `VehicleConfig`, from the prefix.
3. **Add the rate on the worker** with a postfix on the private `UpdateAttitudeTarget(ref FlightComputerNavigation)`; add, do not replace, so a rotating frame's own rate is kept:

```csharp
// AdvancedFlightComputer/Features/Guidance/Control/AttitudeRate.cs L66-L76
    // Harmony postfix body. Current beside the state it drives.
    internal static void OnUpdateAttitudeTarget(FlightComputer flightComputer)
    {
        FlightComputer.VehicleConfigInfo cfg = flightComputer.VehicleConfig;
        if (cfg == null || !Slots.TryGetValue(cfg, out Slot s) || !s.Engaged)
            return;

        // ADDED to whatever the frame contributes, not substituted for it. For the EclBody frame we steer in that term is nil, but the field is the target's total rate in CCI and a frame that does rotate would still have to be carried - overwriting it outright would silently subtract the frame.
        flightComputer.AttitudeTarget.RatesCci += s.RateCci;
    }
}
```

4. **Write gimbals after stock.** In the sink, after stock `ComputeTvcControl` has written `CommandY`/`CommandZ`, replace them with an allocation that solves `u = B^T (B B^T + lambda I)^-1 tau` (Tikhonov-regularised minimum norm, a closed-form 3 x 3 inverse) from per-gimbal torque columns; stock gives every gimbal the same dimensionless demand, which leaks pitch into roll.
   Commands are normalised to [-1, 1] (`Gimbal.GetCommand` multiplies by `MaxAngle`). The gimbal demand is one immutable record assigned by reference so a torn read cannot pair a new mode with an old torque.
5. **Invert thrust to throttle.** Compute thrust and mass flow at a throttle and the current ambient pressure from the engines' own cores and nozzles (`RocketCore.ComputeConditions`, `RocketNozzle.ComputePerformance`), then solve for the throttle with a bracketed Illinois false position, honouring the minimum throttle and the structural g-load cap:

```csharp
// AdvancedFlightComputer/Features/Guidance/Control/EnginePerf.cs L278-L301
internal static ThrustCommand InvertThrust(double demandN, double minimumThrottle, Func<double, double> thrustAtThrottle,
                                           double maximumThrottle = 1.0)
{
    if (!double.IsFinite(demandN))
        return new(0.0, 0.0, ThrustStatus.InvalidDemand);
    if (demandN <= 0.0)
        return new(0.0, 0.0, ThrustStatus.Off);
    if (!double.IsFinite(minimumThrottle) || minimumThrottle < 0.0 || minimumThrottle > 1.0
        || !double.IsFinite(maximumThrottle))
        return new(0.0, 0.0, ThrustStatus.NoAuthority);

    double lo = Math.Max(minimumThrottle, float.Epsilon), hi = Math.Clamp(maximumThrottle, lo, 1.0);
    double full = thrustAtThrottle(hi), minimum = thrustAtThrottle(lo);
    if (!double.IsFinite(full) || !double.IsFinite(minimum) || full <= 0.0 || minimum < 0.0 || minimum > full)
        return new(0.0, 0.0, ThrustStatus.NoAuthority);
    if (demandN < minimum)
        return new(lo, minimum, ThrustStatus.BelowMinimum);
    if (demandN >= full)
        return new(hi, full, demandN > full ? ThrustStatus.AboveMaximum : ThrustStatus.Available);

    // Illinois false position: keeps the bracket thrust(lo) < demand <= thrust(hi) like bisection, and takes the midpoint when the secant point is degenerate or the bracket stops halving.
    double fLo = minimum - demandN, fHi = full - demandN;
    double tolerance = full * 1e-7;
    int lastSide = 0, stalls = 0;
```

6. **Release must clear the rate**: a stale feedforward is a standing order to keep rotating.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Control/AttitudeRate.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/AttitudeRate.cs) | rate slots and the postfix body |
| [AdvancedFlightComputer/Features/Guidance/Control/GimbalControl.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/GimbalControl.cs) | gimbal override and per-vehicle slots |
| [AdvancedFlightComputer/Features/Guidance/Control/TvcAllocator.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/TvcAllocator.cs) | regularised minimum-norm torque allocation |
| [AdvancedFlightComputer/Features/Guidance/Control/EnginePerf.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/EnginePerf.cs) | engine capability and thrust inversion |
| [AdvancedFlightComputer/Features/Guidance/Control/AttitudeOwnership.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/AttitudeOwnership.cs) | capture, compare, restore |

## Variations and gotchas

- **Thrust is not proportional to throttle in an atmosphere**; commanding newtons and dividing by live capability keeps the flown thrust equal to the planned one.
- **Solid motors and cores that ignore throttle** are excluded from the throttle-following set.
- **Scratch buffers per vehicle**: two guided vehicles allocate on two workers at once.
- **A worker-side exception** in a postfix breaks the step; guard it (`GuidanceFeature` worker-side postfix guard) and never allocate.

## See also

- [Overview](overview.md); [sink](drive-the-flight-computer-and-attitude-modes-from-a-mod-5.md); [PrepareWorker prefix](drive-the-flight-computer-and-attitude-modes-from-a-mod-2.md)
- [Steer through the stock flight computer with a rate feedforward and direct gimbal commands](../../../../mods/ksa-advancedflightcomputer/features/guidance-attitude-and-gimbal-actuation.md), [Thrust at ambient pressure, the structural throttle cap and thrust-to-throttle inversion](../../../../mods/ksa-advancedflightcomputer/features/engine-performance-and-throttle-inversion.md)
- Related topics: [Plan and fly a powered landing](../../guidance/plan-and-fly-a-powered-landing/overview.md)

[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-gimbalcontrollerstate]: KSA.GimbalControllerState (decompiled, build 2026.10.10.5554)
