---
type: Mod Feature
title: Vehicle control ownership, takeover detection and hand-back
description: Main-thread claims per vehicle, a burn-mode hold that mirrors Vehicle.SetEnum, and attitude takeover detection by comparing the exact fields the flight computer reads, so automation yields to the player and restores only what it changed
mod: ksa-advancedflightcomputer
category: flight-and-orbits
techniques: [conditional-weak-table]
hooks: []
lifecycle: [frame:apply-solvers, frame:dispatch-solvers]
game_types:
  - KSA.Vehicle
  - KSA.FlightComputer
  - KSA.FlightComputerBurnMode
  - KSA.FlightComputerAttitudeTrackTarget
  - KSA.BurnTarget
tags: [flight-computer, autopilot, burn-mode, attitude, ownership, takeover]
---

Two AFC features fly a vehicle on their own: the RCS burn executor and powered guidance. Both write
the stock flight computer's burn mode and attitude settings, and both must stop the moment the player
(or another mod) takes over, then put back exactly what they changed and nothing else. AFC writes
the rules down in `docs/control-ownership.md` and implements them with three small pieces: a claim
table, a burn-mode hold, and command snapshots compared every step.

## How it works

### Claims

`VehicleControlOwnership` keeps one claimant (`RcsTranslation` or `Guidance`) per `Vehicle` object in
a `ConditionalWeakTable`, so a renamed vehicle keeps its claim and a disposed one does not leak.
`TryClaim` lets the holder claim again and refuses a different claimant; `Release` only removes the
caller's own claim; claims are main-thread only and never saved
([AdvancedFlightComputer/Core/VehicleControlOwnership.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/VehicleControlOwnership.cs#L13-L79)). A controller acquires before
its first write and releases after its cleanup succeeded.

### Burn mode: hold Manual, give Auto back

A controller that flies the craft holds `FlightComputer.BurnMode` in Manual, because stock Auto takes
the attitude near ignition and `Vehicle.PrepareWorker` clears the engine master switch every frame
while Auto is set ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2412-L2415)).[^ksa-vehicle] `StockBurnMode.HoldManual`
selects Manual and reports whether it replaced an Auto; `GiveBackAuto` restores it only when the mode
still reads Manual, the loaded `BurnTarget` is still the one the hold saw, the vehicle is
controllable, and the burn would not stop at once for lack of propellant
([AdvancedFlightComputer/Core/StockBurnMode.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/StockBurnMode.cs#L11-L64)). The writes mirror what
`Vehicle.SetEnum(FlightComputerBurnMode)` does (navball frame BurnBody for Auto, the region's frame
otherwise, refusal without control) without calling it, because AFC intercepts `SetEnum` to start and
cancel RCS burns ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6375-L6386)).

### Attitude: snapshot, compare, yield

- The RCS executor takes the attitude once, at acquisition (`FlightComputer.RateHold` for Hold, a
  Custom target for Align), and stores an `RcsAttitudeCommand` snapshot of mode, frame, track target
  and custom coordinates.
- Every later step compares the snapshot with the live fields. Mode and track target always count;
  frame and coordinates count only under `None` and `Custom`, because
  `FlightComputer.UpdateAttitudeTarget` reads `CustomAttitudeTarget` as a rotation rate under `None`
  and as Euler angles in `AttitudeFrame` under `Custom`, while built-in targets derive their own frame
  ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1264-L1300), [AdvancedFlightComputer/Features/RcsTranslation/RcsExecution.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsExecution.cs#L329-L351)).[^ksa-flightcomputer]
- A mismatch is a takeover: the executor yields (Align drops to Hold, no attitude gate, nothing to
  hand back) and records `attitude_yielded`, which is saved so a load cannot take the attitude back
  ([AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.cs#L1109-L1180)).
- Guidance uses the same idea in `AttitudeOwnership`: capture the original fields before its first
  write, record the last written snapshot, treat any difference as a takeover, and on release restore
  frame, tracking and coordinates as one group only if they still match its last command
  ([AdvancedFlightComputer/Features/Guidance/Control/AttitudeOwnership.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/AttitudeOwnership.cs)). See
  [guidance attitude and gimbal actuation](guidance-attitude-and-gimbal-actuation.md).
- Release also clears custom coordinates left under `None`, because they would otherwise remain a
  standing rotation-rate command.

### Release rules worth copying

| Rule | Reason |
|---|---|
| Restore a field only if it still holds the value you wrote | Anything else is the player's choice now |
| Give Auto back only for the same loaded burn target | Stock also writes Manual when a burn is loaded, unloaded or completed |
| Never give Auto back to a burn that would stop for lack of propellant | It would stop on the next worker step |
| A takeover moves the attitude only; an engine cut already decided still happens | Avoid leaving an engine burning or cutting one mid-air by accident |

## Lifecycle and timing

All ownership state is main-thread only. The RCS executor evaluates it in the
[post-solver vehicle tick](post-solver-vehicle-tick.md) (`frame:apply-solvers`), guidance in its
`Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`, after the input drain). Both points come
before the worker snapshot of the flight computer, so a player click drained from input this frame
is visible to the comparison (guidance) or on the next frame (RCS), and a restored value is what the
next physics step uses.

The windows are not a style choice. The worker flies a copy of the flight computer taken in
`PrepareWorker`, and `Vehicle.UpdateFromTaskResultsUnsynchronized` copies it back with
`FlightComputer.CopyFrom` at the next `ApplyVehicleSolvers`, overwriting every copied field
([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2530)).[^ksa-vehicle] A claim, hold or restore written from a draw hook
would be undone one frame later, and a comparison made there would see values the worker is about to
replace. Stock's own buttons and burn-mode hotkeys reach `Vehicle.SetEnum` through
`InputEvents.FlightComputerInputBuffer`, drained in the same window
([current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L592-L605)). See
[flight-computer worker copy channels](flight-computer-worker-copy-channels.md).

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Core/VehicleControlOwnership.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/VehicleControlOwnership.cs) | Claim table |
| [AdvancedFlightComputer/Core/StockBurnMode.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/StockBurnMode.cs) | Burn-mode hold and give-back |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsExecution.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsExecution.cs) | `RcsAttitudeCommand` snapshot and `Matches` |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.cs) | Acquisition, yield and release in the RCS executor |
| [AdvancedFlightComputer/Features/Guidance/Control/AttitudeOwnership.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/AttitudeOwnership.cs) | Guidance's capture, compare and conditional restore |
| [docs/control-ownership.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/docs/control-ownership.md) | Field-by-field write and release table |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Core/VehicleControlOwnershipTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Core/VehicleControlOwnershipTest.cs) | Headless claim tests |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Core/ControlWriteSurfaceTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Core/ControlWriteSurfaceTest.cs) | Snapshot of public flight-computer fields around an RCS activation and cancel |

## Snippets

```csharp
// AdvancedFlightComputer/Core/StockBurnMode.cs L26-L41
    internal static bool GiveBackAuto(Vehicle vehicle, BurnTarget? heldTarget, bool takesStopBack = false)
    {
        FlightComputer fc = vehicle.FlightComputer;
        if (fc.BurnMode != FlightComputerBurnMode.Manual || heldTarget == null || !ReferenceEquals(fc.Burn, heldTarget)
            || !vehicle.IsControllable)
            return false;
        if (!takesStopBack && WouldStopAtOnce(vehicle, heldTarget))
        {
            TimedAlert.CreateWarning(ReferenceEquals(vehicle, Program.ControlledVehicle)
                ? LStrings.AlertAutoBurnStopped.Format()
                : LStrings.AlertAutoBurnStoppedVehicle.Format<string>(vehicle.Id));
            return false;
        }
        Write(vehicle, FlightComputerBurnMode.Auto);
        return true;
    }
```

```csharp
// AdvancedFlightComputer/Features/RcsTranslation/RcsExecution.cs L338-L350
    /// <summary>
    /// Compare frame and coordinates for None and Custom, where FlightComputer.UpdateAttitudeTarget uses both.
    /// Built-in targets derive their frame, so only mode and tracker are compared for them.
    /// </summary>
    internal readonly bool Matches(FlightComputer fc)
    {
        if (fc.AttitudeMode != Mode || fc.AttitudeTrackTarget != TrackTarget)
            return false;
        if (TrackTarget is not (FlightComputerAttitudeTrackTarget.None
            or FlightComputerAttitudeTrackTarget.Custom))
            return true;
        return fc.AttitudeFrame == Frame && fc.CustomAttitudeTarget.Equals(CustomTarget);
    }
```

## Game APIs touched

| Member | Use |
|---|---|
| `FlightComputer.BurnMode`, `Burn` ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L32)) | Burn-mode hold and loaded target identity |
| `FlightComputer.AttitudeMode`, `AttitudeFrame`, `AttitudeTrackTarget`, `CustomAttitudeTarget`, `RollMode` | Snapshot and restore |
| `FlightComputer.RateHold`, `SetNullRot` | Stock helpers AFC uses to take and release attitude |
| `Vehicle.SetEnum(Enum)` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6321)) | Stock path whose burn-mode behavior is mirrored |
| `Vehicle.SetNavBallFrame`, `VehicleRegion.GetVehicleReferenceFrame`, `Vehicle.IsControllable` | Frame pairing and refusal |
| `Vehicle.PrepareWorker` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396)) | Clears `EngineOn` while Auto; the reason for the Manual hold |

## Pitfalls and version notes

- Checking only `AttitudeMode` misses a takeover: selecting another tracked target leaves the mode at
  Auto.
- A save does not carry recorded commands. AFC reconciles after a load (re-commands Align, adopts
  the loaded Hold values only when they are the Auto its rate hold leaves) instead of taking control
  again.
- The ownership protocol is internal to AFC; another mod writing the same fields is detected as a
  takeover but not arbitrated.
- `afc-control-write-surface` compares only public `FlightComputer` fields shallowly; it is a
  regression check, not a proof that every write is restored.

## See also

- [RCS burn execution](rcs-burn-execution.md)
- [Guidance vehicle step loop](guidance-vehicle-step-loop.md)
- [Guidance attitude and gimbal actuation](guidance-attitude-and-gimbal-actuation.md)
- [Flight computer command sink](flight-computer-command-sink.md)
- [Flight-computer worker copy channels](flight-computer-worker-copy-channels.md)
- [Claim vehicles per mod, hold burn mode and compare exact fields to detect a takeover](../../../howto/flight-and-orbits/autopilot/run-an-autopilot-for-every-vehicle-each-step/run-an-autopilot-for-every-vehicle-each-step-4.md) - how-to article demonstrating this feature

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
