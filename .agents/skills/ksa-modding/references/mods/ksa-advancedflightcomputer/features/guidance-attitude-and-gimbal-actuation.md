---
type: Mod Feature
title: Steer through the stock flight computer with a rate feedforward and direct gimbal commands
description: Command a Custom attitude in an inertial frame, add the commanded turning rate in a postfix on the private FlightComputer.UpdateAttitudeTarget on the physics worker, write least-squares gimbal deflections after ComputeControl, and invert engine performance to deliver a thrust rather than a throttle
mod: ksa-advancedflightcomputer
category: flight-and-orbits
techniques: [harmony-postfix, reflection, conditional-weak-table]
hooks:
  - harmony-postfix KSA.FlightComputer.UpdateAttitudeTarget
  - harmony-postfix KSA.FlightComputer.ComputeControl
  - harmony-prefix KSA.Vehicle.PrepareWorker
lifecycle: [frame:dispatch-solvers, physics:vehicle-solver]
game_types:
  - KSA.FlightComputer
  - KSA.FlightComputer.VehicleConfigInfo
  - KSA.FlightComputerOutput
  - KSA.GimbalController
  - KSA.GimbalControllerState
  - KSA.EngineController
  - KSA.RocketCore
tags: [attitude-control, thrust-vector-control, gimbal, feedforward, worker-thread, engine-performance]
---

Guidance does not fly the vehicle with its own physics; it drives the stock flight computer. Pointing
goes through the stock attitude controller, which AFC improves with a feedforward of the target's own
turning rate; for 6-DOF landing the planned gimbal torque is written directly to the gimbals; throttle
is computed from the thrust the plan wants, using the game's engine performance model. All three
cross from the main thread to the vehicle worker without locks.

## How it works

### Attitude command (main thread)

`CommandAttitude` converts the desired direction (and a roll reference derived from the target orbit
plane) into Euler angles for a `Custom` target in the inertial `EclBody` frame: it solves
`Concatenate(value, frame2Cci) == desired` for `value`, writes `CustomAttitudeTarget`, sets
`AttitudeFrame = EclBody` and `TrackTarget(Custom)`, and sets `RollMode` only when a roll is deliberately
forced (and only above a minimum altitude). Every write is bracketed by `AttitudeOwnership.BeginWrite`
/ `EndWrite`, which snapshots the original fields once and the last written values every time, for
takeover detection and conditional restore ([AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs#L1396-L1440),
[AdvancedFlightComputer/Features/Guidance/Control/AttitudeOwnership.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/AttitudeOwnership.cs)).

### Rate feedforward (worker)

`FlightComputer.AttitudeTarget` carries `Target2Cci` and `RatesCci`, and the tracking error is
`ctrlRates - RatesCci` ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1264-L1300)).[^ksa-flightcomputer] The Custom branch
fills `RatesCci` with the frame's rate, which is zero for the inertial frame, so the stock controller
treats every new target as stationary and chases it. A postfix on the private
`FlightComputer.UpdateAttitudeTarget(ref FlightComputerNavigation)` (called by `ComputeControl`
right before `UpdateAttitudeError`) adds the rate guidance published for that vehicle:
`AttitudeTarget.RatesCci += s.RateCci` (added, not replaced, so a rotating frame's own rate is kept)
([AdvancedFlightComputer/Features/Guidance/Control/AttitudeRate.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/AttitudeRate.cs)).

### Gimbal override (worker)

`KsaGimbalControl.OnComputeControl` is dispatched by the shared
[command sink](flight-computer-command-sink.md) after stock `ComputeTvcControl` has written its own
`CommandY`/`CommandZ`, so it has the last word. In `Lsq` mode `TvcAllocator` builds the 3 x 2N matrix
from per-gimbal commands to body torque (central differences around the current command, from each
gimbal's thrust and moment arm), and solves `u = B^T (B B^T + lambda I)^-1 tau` with a Tikhonov term
relative to `trace(B B^T)`, a closed-form 3 x 3 inverse cheap enough for every control step. Stock's
own allocation gives every gimbal the same dimensionless demand, which leaks pitch into roll and cannot
express a torque in N m ([AdvancedFlightComputer/Features/Guidance/Control/TvcAllocator.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/TvcAllocator.cs),
[AdvancedFlightComputer/Features/Guidance/Control/GimbalControl.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/GimbalControl.cs#L111-L160)). Commands are
normalized to [-1, 1] (`Gimbal.GetCommand` multiplies by `MaxAngle`).

### Thrust instead of throttle

`EnginePerf` computes thrust and mass flow at a commanded throttle and the current ambient pressure
from the engines' own cores and nozzles, mirrors the structural g-load throttle cap
(`FlightComputer.SolveGLoadThrottleCap`, applied to Manual throttle), and inverts thrust to throttle.
Solid motors and cores that ignore throttle are excluded from the throttle-following set
([AdvancedFlightComputer/Features/Guidance/Control/EnginePerf.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/EnginePerf.cs)). In an atmosphere thrust is not
proportional to throttle, so commanding newtons and dividing by live capability is what keeps the
flown thrust equal to the planned one.

### Crossing threads without locks

- The worker runs `ComputeControl` on a copy of the `FlightComputer`, so the copy cannot be matched by
  reference. `FlightComputer.CopyFrom` assigns `VehicleConfig` (a `VehicleConfigInfo`) by reference
  ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L181-L201)), so that object is the key of a `ConditionalWeakTable` slot
  per vehicle for both the rate and the gimbal demand.
- The main thread publishes in the `PrepareWorker` prefix, before the solver job is queued, so the
  publish happens-before the worker read. The gimbal demand is one immutable record assigned by
  reference (a torn read cannot pair a new mode with an old torque); scratch buffers are per vehicle
  because two guided vehicles allocate on two workers at once.
- Release must clear the rate: a stale feedforward is a standing order to keep rotating.

## Lifecycle and timing

| Write | Where | Thread |
|---|---|---|
| Custom attitude, published rate and gimbal demand, `_manualControlInputs` throttle | `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`) | Main |
| `RatesCci` feedforward | `FlightComputer.UpdateAttitudeTarget` postfix (`physics:vehicle-solver`) | Vehicle worker |
| Gimbal `CommandY/Z` | `FlightComputer.ComputeControl` postfix via the sink (`physics:vehicle-solver`) | Vehicle worker |

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Control/AttitudeRate.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/AttitudeRate.cs) | Rate slots and the postfix body |
| [AdvancedFlightComputer/Features/Guidance/Control/GimbalControl.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/GimbalControl.cs) | Gimbal override and per-vehicle slots |
| [AdvancedFlightComputer/Features/Guidance/Control/TvcAllocator.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/TvcAllocator.cs) | Regularized minimum-norm torque allocation |
| [AdvancedFlightComputer/Features/Guidance/Control/EnginePerf.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/EnginePerf.cs) | Engine capability and thrust inversion |
| [AdvancedFlightComputer/Features/Guidance/Control/AttitudeOwnership.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/AttitudeOwnership.cs) | Capture, compare, restore |
| [AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs#L98-L113) | Worker-side postfix guard |
| [docs/guidance/scvx-bridge-devnotes.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/docs/guidance/scvx-bridge-devnotes.md) | Author's notes on the actuator boundary |

## Snippets

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

## Game APIs touched

| Member | Use |
|---|---|
| `FlightComputer.UpdateAttitudeTarget` (private, [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1264)) | Postfix point for the rate feedforward |
| `FlightComputer.AttitudeTarget.RatesCci`, `CustomAttitudeTarget`, `AttitudeFrame`, `RollMode`, `TrackTarget` | Attitude command |
| `FlightComputer.VehicleConfig` / `VehicleConfigInfo` ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L165)) | Identity shared with the worker copy |
| `FlightComputer.ComputeTvcControl` (private, [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L821)) | Stock gimbal writer that runs first |
| `FlightComputerOutput.Gimbals`, `GimbalControllerState.CommandY/CommandZ` ([current/decomp/KSA/FlightComputerOutput.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputerOutput.cs)) | Gimbal outputs |
| `FlightComputer.MaxGLoad`, `SolveGLoadThrottleCap` (private) | Structural throttle cap mirrored by `EnginePerf` |

## Pitfalls and version notes

- `UpdateAttitudeTarget` is private with one call site; if a game update inlines it, the handle still
  resolves but the postfix goes silent. AFC's `afc-guidance-driver` test checks that the rate arrives.
- Writing a rate into `CustomAttitudeTarget` under tracking `None` is a rotation-rate command, not an
  angle; clear coordinates on release.
- Never write gimbal commands through an empty module slot (null module and state ref); it is an access
  violation, not an exception.

## See also

- [Guidance vehicle step loop](guidance-vehicle-step-loop.md)
- [Flight computer command sink](flight-computer-command-sink.md)
- [Vehicle control ownership](vehicle-control-ownership.md)
- [6-DOF SCvx landing MPC](scvx-6dof-landing-mpc.md)
- [Flight-computer worker copy channels](flight-computer-worker-copy-channels.md)
- [Custom attitude in an inertial frame plus turning-rate feedforward, gimbal commands and thrust inversion](../../../howto/flight-and-orbits/autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/drive-the-flight-computer-and-attitude-modes-from-a-mod-4.md) - how-to article demonstrating this feature

[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
