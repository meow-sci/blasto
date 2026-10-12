---
type: Mod Feature
title: Reach the physics worker through the references FlightComputer.CopyFrom shares
description: Publish per-vehicle commands keyed by the BurnPlan and VehicleConfigInfo objects that the worker's flight-computer copy shares with the vehicle, and confine flight-computer writes to the window before the snapshot because the copy is written back at every solver apply
mod: ksa-advancedflightcomputer
category: patching
techniques: [conditional-weak-table, harmony-prefix, harmony-postfix]
hooks:
  - harmony-postfix KSA.Universe.ApplyVehicleSolvers
  - harmony-prefix KSA.Vehicle.PrepareWorker
  - harmony-postfix KSA.FlightComputer.ComputeControl
  - harmony-postfix KSA.FlightComputer.UpdateAttitudeTarget
lifecycle: [frame:apply-solvers, frame:dispatch-solvers, physics:vehicle-solver]
game_types:
  - KSA.FlightComputer
  - KSA.FlightComputer.VehicleConfigInfo
  - KSA.BurnPlan
  - KSA.BurnTarget
  - KSA.Vehicle
  - KSA.VehicleUpdateData
  - KSA.VehicleUpdateState
tags: [threading, flight-computer, worker, copy-back, conditional-weak-table, lifecycle]
---

The vehicle worker never sees a `Vehicle`'s own `FlightComputer`. It flies a copy, and the copy is
written back over the original at the next solver apply. AFC has two features that must talk to the
worker every control step (the RCS executor's pulse commands and guidance's rate feedforward and gimbal
demand) and several that must change flight-computer settings so the next step uses them. Both depend
on knowing exactly what the copy carries in each direction. This concept collects that contract and the
two rules AFC builds on it: key worker-side lookups by an object the copy shares by reference, and
write settings only in the window before the snapshot.

## How it works

### The copy, both ways

- **Out.** `Vehicle.PrepareWorker` ends with `VehicleUpdateState.PrepareFromVehicle`, which calls
  `VehicleUpdateData.Prepare`, which runs `NewFlightComputer.CopyFrom(vehicle.FlightComputer)`
  ([current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L296-L306), [current/decomp/KSA/VehicleUpdateData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateData.cs#L84-L104)).[^ksa-vehicleupdatedata]
  The worker's `ComputeControl`, and every postfix on it, receives `UpdateData.NewFlightComputer`.
- **Back.** At the next `Universe.ApplyVehicleSolvers`, `Vehicle.UpdateFromTaskResultsUnsynchronized`
  runs `FlightComputer.CopyFrom(updateData.NewFlightComputer)` unconditionally, after
  `DeleteInvalidBurns` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2470-L2531)).[^ksa-vehicle] Stock's worker-side writes
  (`BurnMode = Manual`, `AutoBurnCompleted`, `AutoBurnStoppedOutOfPropellant`, the attitude switch at
  alignment time) reach the vehicle this way.
- **What `CopyFrom` copies.** Value fields such as the modes, the attitude target and the burn flags
  are copied; `BurnPlan`, `VehicleConfig` (a `FlightComputer.VehicleConfigInfo` holding the thruster and
  gimbal lists) and `Burn` (the loaded `BurnTarget`) are assigned by reference, so vehicle and worker
  copy point at the same objects; `Ctrl2Asmb`, which `ComputeControl` sets from the navigation frame, is
  not copied at all ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L181-L225)).[^ksa-flightcomputer]

### Rule 1: key worker lookups by a shared reference

A postfix on the worker gets the copy, so a table keyed by the vehicle's `FlightComputer` instance
never matches. AFC keys a `ConditionalWeakTable` by an object `CopyFrom` shares:

| Channel | Key | Published from | Read in |
|---|---|---|---|
| RCS worker command | `BurnPlan` | [post-solver tick](post-solver-vehicle-tick.md) | `ComputeControl` postfix ([command sink](flight-computer-command-sink.md)) |
| Guidance gimbal demand | `VehicleConfigInfo` | `PrepareWorker` prefix | `ComputeControl` postfix |
| Guidance rate feedforward | `VehicleConfigInfo` | `PrepareWorker` prefix | `UpdateAttitudeTarget` postfix |

([AdvancedFlightComputer/Features/RcsTranslation/RcsWorkerCommand.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsWorkerCommand.cs#L40-L59),
[AdvancedFlightComputer/Features/Guidance/Control/GimbalControl.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/GimbalControl.cs#L63-L69),
[AdvancedFlightComputer/Features/Guidance/Control/AttitudeRate.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/AttitudeRate.cs#L21-L33))

The main thread publishes before `ExecuteNextVehicleSolvers` queues the job, so the publish
happens-before the worker read. Commands are immutable records swapped by reference (RCS, gimbal) or a
value republished every step (rate), so a reader never needs a lock; `ConditionalWeakTable` itself is
safe for concurrent reads and writes and drops an entry when the vehicle's plan or config object dies.

The shared `BurnTarget` is the one case where the worker mutates shared state: the RCS writer replaces
`BurnDuration` and `IgnitionTime` on `fc.Burn` every control step, so the loaded target carries RCS
timing instead of the engine timing stock's `UpdateBurnTarget` writes there every tick. Stock does the
same kind of worker-side write to that object (`Throttle`, `StartedAt`, `BurnBody2Cci` in
`ComputeControl` and `UpdateBurnTarget`), and the UI reads it during the draw while the step runs, so
neither side treats it as main-thread-only.

### Rule 2: write settings only before the snapshot

Because the copy-back overwrites every copied field, a main-thread write to the flight computer
survives only if it lands between `ApplyVehicleSolvers` and that vehicle's `PrepareWorker`. AFC uses
exactly two places: the `ApplyVehicleSolvers` postfix (RCS executor, multi-pass, AutoStage) and the
`PrepareWorker` prefix (guidance). Guidance queues every panel action as a request for its next prefix,
and the RCS executor raises the stock end-of-burn flags in the tick, where no copy can drop them
before `RaisePendingAlerts` reads them
([AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs#L1379-L1386),
[AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.cs#L915-L918)).

### What not to read from the vehicle's copy

- `Ctrl2Asmb` on the vehicle's flight computer is stale or identity, because it is set on the worker
  and never copied back; AFC reads `Vehicle.Ctrl2Body` instead
  ([AdvancedFlightComputer/Features/RcsTranslation/RcsCtrlFrame.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsCtrlFrame.cs#L22-L24)).
- `fc.Burn` can outlive its plan entry, because `DeleteInvalidBurns` runs before `CopyFrom` writes
  the worker's target back; AFC identifies the loaded burn by impulse time, not by reference
  ([AdvancedFlightComputer/Core/StockBurnIdentity.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/StockBurnIdentity.cs)).

## Lifecycle and timing

| Moment | Lifecycle | Thread | Flight-computer state |
|---|---|---|---|
| `ApplyVehicleSolvers` (postfix) | `frame:apply-solvers` | Main | Worker copy just written back; writes from here on reach the next step |
| `InputEvents.ApplyInputEvents`, `RaisePendingAlerts` loop | `frame:input`, `frame:prepare` | Main | Stock's own writes (clicks, hotkeys, end of burn) |
| `Vehicle.PrepareWorker` (prefix, then the snapshot) | `frame:dispatch-solvers` | Main | Last write for the next step; the copy is taken at the end of the method |
| `ComputeControl` and its callees | `physics:vehicle-solver` | Vehicle worker | Worker copy; shared `BurnPlan`, `VehicleConfig`, `Burn` |
| Everything after dispatch (UI, StarMap frame hooks, render) | `frame:draw-ui` onwards | Main | Writes are overwritten at the next apply |

The [game loop reference](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md) lists the
same window for all simulated state; the flight computer is the case where the overwrite is total and
unconditional.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/RcsTranslation/RcsWorkerCommand.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsWorkerCommand.cs) | `BurnPlan`-keyed command channel |
| [AdvancedFlightComputer/Features/Guidance/Control/GimbalControl.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/GimbalControl.cs) | `VehicleConfigInfo`-keyed gimbal slots |
| [AdvancedFlightComputer/Features/Guidance/Control/AttitudeRate.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/AttitudeRate.cs) | `VehicleConfigInfo`-keyed rate slots, with the reasoning |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsCtrlFrame.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsCtrlFrame.cs) | Reads the control frame from the vehicle, not the copy |
| [AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs) | Panel requests deferred to the `PrepareWorker` prefix |

## Snippets

```csharp
// current/decomp/KSA/VehicleUpdateData.cs L84-L104
	public void Prepare(ref readonly BubbleOrigin bubbleOrigin, ref readonly KinematicStates kinematicStates, FlightComputer flightComputer)
	{
		NewOrigin = bubbleOrigin;
		NewKinematicStates = kinematicStates;
		// ...
		NewFlightComputer.CopyFrom(flightComputer);
		NewFlightPlan = null;
		NewBurnFlightPlans.Clear();
		Updaters.Prepare();
	}
```

```csharp
// current/decomp/KSA/Vehicle.cs L2476-L2530
		FlightComputer.DeleteInvalidBurns();
		// ...
		_structuralLoad = updateData.NewStructuralLoad;
		FlightComputer.CopyFrom(updateData.NewFlightComputer);
```

```csharp
// AdvancedFlightComputer/Features/Guidance/Control/AttitudeRate.cs L21-L33
// THREADING AND IDENTITY.
// ComputeControl runs on a VehicleSolvers job thread against a COPY of the FlightComputer, so the instance handed to the postfix is never the live one and reference-comparing it would match nothing.
// VehicleConfigInfo is the usable identity - FlightComputer.CopyFrom assigns it by reference rather than cloning - which is the same key KsaGimbalControl is built on, for the same reason.
// The published value is a double3 written on the sim thread and read on the job thread; it is republished every step, so a torn read costs one step of a slightly wrong rate and never a wrong ANGLE.
public static class KsaAttitudeRate
{
    private sealed class Slot
    {
        public double3 RateCci;
        public bool Engaged;
    }

    private static readonly ConditionalWeakTable<FlightComputer.VehicleConfigInfo, Slot> Slots = new();
```

## Game APIs touched

| Member | Use |
|---|---|
| `FlightComputer.CopyFrom` ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L181)) | The copy in both directions; defines what is shared |
| `FlightComputer.BurnPlan`, `VehicleConfig` (`FlightComputer.VehicleConfigInfo`), `Burn`, `Ctrl2Asmb` ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L163-L165)) | Shared keys, shared target, the uncopied frame |
| `FlightComputer.ReadUpdatedVehicleConfiguration` ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L311-L313)) | Replaces `VehicleConfig` with a new object when the part tree changes |
| `VehicleUpdateData.Prepare` ([current/decomp/KSA/VehicleUpdateData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateData.cs#L84)) | Snapshot into the worker copy |
| `Vehicle.UpdateFromTaskResultsUnsynchronized` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2435)) | Write-back at the solver apply |
| `Vehicle.Ctrl2Body` | Control frame read on the main thread |

## Pitfalls and version notes

- Writing a flight-computer field from a UI or StarMap frame hook looks like it works for one frame and
  is then reverted; the bug appears only while the next step's copy is in flight.
- A key that the game replaces orphans the published entry: `ReadUpdatedVehicleConfiguration`
  assigns a new `VehicleConfigInfo` whenever the vehicle configuration is re-read (`Vehicle.Split` on
  decoupling, `MergeFrom` on docking, `UpdateAfterPartTreeModification`, a save load). Publish against the current key every step instead of once.
- Mutating a shared reference object from the worker (as the RCS writer does with the `BurnTarget`)
  is safe only for fields the main thread does not touch while the step is in flight.

## See also

- [Flight computer command sink](flight-computer-command-sink.md)
- [Vehicle control ownership](vehicle-control-ownership.md)
- [Post-solver vehicle tick](post-solver-vehicle-tick.md)
- [Guidance vehicle step loop](guidance-vehicle-step-loop.md)
- [RCS burn execution](rcs-burn-execution.md)
- [Guidance attitude and gimbal actuation](guidance-attitude-and-gimbal-actuation.md)
- [Mutate simulation state safely relative to the physics step](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md) - how-to article demonstrating this feature
- [ConditionalWeakTable keyed by objects the copy shares by reference](../../../howto/patching/harmony/patch-methods-that-run-on-background-threads/patch-methods-that-run-on-background-threads-4.md) - how-to article demonstrating this feature
- [Immutable commands published before the solver job is queued](../../../howto/physics/solver/write-worker-thread-safe-solver-patches/write-worker-thread-safe-solver-patches-6.md) - how-to article demonstrating this feature

[^ksa-vehicleupdatedata]: KSA.VehicleUpdateData (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
