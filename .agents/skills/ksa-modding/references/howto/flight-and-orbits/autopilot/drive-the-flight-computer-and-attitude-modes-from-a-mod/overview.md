---
type: Game Feature
title: Drive the flight computer and attitude modes from a mod
description: Write a Custom attitude target so the stock controller flies a mod-computed attitude, command gimbals, and pick the write window that is not erased, raced or seen a step late
category: flight-and-orbits
group: autopilot
topic: drive-the-flight-computer-and-attitude-modes-from-a-mod
demonstrated_by:
  - mods/ohmystars/features/orient-to-star
  - mods/ksarmory/features/fly-another-vehicle
  - mods/ksarmory/features/prepareworker-prefix-command-window
  - mods/gatos/features/solver-phase-command-drain
  - mods/ksa-advancedflightcomputer/features/guidance-attitude-and-gimbal-actuation
  - mods/ksa-advancedflightcomputer/features/engine-performance-and-throttle-inversion
  - mods/ksa-advancedflightcomputer/features/flight-computer-command-sink
  - mods/ksa-advancedflightcomputer/features/post-solver-vehicle-tick
  - mods/compendium/features/flight-computer-root-attitude-tracking
  - mods/compendium/features/root-navigation-targets
  - mods/ohmystars/features/attitude-mode-handoff-patches
lifecycle: [frame:dispatch-solvers, physics:vehicle-solver, frame:apply-solvers, frame:input, frame:draw-ui]
game_types:
  - KSA.Vehicle
  - KSA.FlightComputer
  - KSA.VehicleReferenceFrameEx
  - KSA.FlightComputerAttitudeTrackTarget
  - KSA.KittenEva
  - KSA.VehicleUpdateState
  - KSA.Universe
  - KSA.VehicleUpdateData
  - KSA.PhysicsBubble
  - KSA.Program
  - KSA.InputEvents
  - KSA.BurnPlan
  - KSA.FlightComputerOutput
  - KSA.GimbalController
  - KSA.GimbalControllerState
  - KSA.EngineController
  - KSA.RocketCore
  - KSA.FlightComputerBurnMode
  - KSA.BurnTarget
tags: [flight-computer, attitude, custom-target, gimbal, prepare-worker, copy-from, harmony, worker-thread]
---

The stock flight computer already holds attitude, tracks prograde, points at targets and nulls rates. A mod that wants a different attitude does not need its own controller:
it writes a **Custom** target and lets the stock controller fly it. The hard part is not the math but the **when**: the vehicle solver snapshots the flight computer once per
step and copies the worker's version back over the live object, so a write at the wrong moment is erased, raced or seen a step late.

## Engine background

- **The fields.** `FlightComputer.AttitudeMode` (`Manual`/`Auto`), `AttitudeFrame` (`VehicleReferenceFrame`, default `EclBody`), `AttitudeTrackTarget` (`None` = rate hold, `Custom`, `Prograde`, `Toward`, ...) and
  `CustomAttitudeTarget` (a `double3`). `UpdateAttitudeTarget`, called from `ComputeControl`, turns them into `AttitudeTarget`: with `None`, `CustomAttitudeTarget` is read as a body-rate command; with `Custom` it is converted with
  `AttitudeFrame.EulerAnglesToQuaternion` and composed with the frame orientation (`GetFrame2Cci`), and the frame's own rates become the target rates.[^ksa-flightcomputer] [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1264-L1303)
  `TrackTarget(target)` sets `Auto` plus the track target; `RateHold(frame)` sets `Auto` and `SetNullRot(frame)`; `SetInertialNullRot()` is `SetNullRot(EclBody)`.[current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L331-L343) [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1978-L2000)
- **Euler convention.** For `EclBody` the game extracts angles as `ToRollYawPitchRadians` (`X` roll, `Y` yaw, `Z` pitch), so build `CustomAttitudeTarget` with `frame.QuaternionToEulerAngles(...)`.[^ksa-vehiclereferenceframeex] [current/decomp/KSA/VehicleReferenceFrameEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleReferenceFrameEx.cs#L439-L470)
- **The copy round trip.** `Vehicle.PrepareWorker` ends with `_threadWorkerUpdateState.PrepareFromVehicle`, which runs `NewFlightComputer.CopyFrom(flightComputer)`; at the next `ApplyVehicleSolvers`, `UpdateFromTaskResultsUnsynchronized` runs
  `FlightComputer.CopyFrom(updateData.NewFlightComputer)`. `CopyFrom` copies the modes, targets, `Burn`, and assigns `BurnPlan` by reference.[^ksa-vehicleupdatedata] [current/decomp/KSA/VehicleUpdateData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateData.cs#L84-L100) [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L181-L225)
  The worker runs `ComputeControl` on the copy, on a vehicle solver thread.

## Where can I write? (the five windows)

| Window | Lifecycle | Survives? | Used by |
|---|---|---|---|
| `Vehicle.PrepareWorker` prefix (per vehicle, before the snapshot) | `frame:dispatch-solvers` | yes, it is in the next step's snapshot | OhMyStars, KSArmory, AFC guidance |
| `Universe.ExecuteNextVehicleSolvers` prefix | `frame:dispatch-solvers` | yes, before any vehicle snapshot | gatOS (Priority.First) |
| `Universe.ApplyVehicleSolvers` postfix | `frame:apply-solvers` | yes, copied at the next `PrepareWorker` | AFC state machines (RCS, multi-pass, auto-stage) |
| `InputEvents.ApplyInputEvents` prefix or stock buffers | `frame:input` | yes, the same drain precedes the dispatch | Compendium |
| Any StarMap frame or draw hook | `frame:draw-ui` and later | **no**: erased by the next apply | (wrong) |
| `ComputeControl` or `UpdateAttitudeTarget` pre/postfix | `physics:vehicle-solver` | worker copy only; reaches the vehicle at the next apply | AFC (rates, gimbals), Compendium (navigation target) |

See [Mutate simulation state safely relative to the physics step](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md), the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Reach the physics worker through the references FlightComputer.CopyFrom shares](../../../../mods/ksa-advancedflightcomputer/features/flight-computer-worker-copy-channels.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Orient to a sky direction from a PrepareWorker prefix](drive-the-flight-computer-and-attitude-modes-from-a-mod-1.md) | OhMyStars | flight-computer target | `frame:dispatch-solvers` | The reference recipe; restore the previous mode on exit |
| [Custom target for autopiloted craft, queued writes](drive-the-flight-computer-and-attitude-modes-from-a-mod-2.md) | KSArmory | harmony prefix | `frame:dispatch-solvers` | Many commands in one prefix; double-buffered queues |
| [Attitude mode and quaternion from the solver-phase drain](drive-the-flight-computer-and-attitude-modes-from-a-mod-3.md) | gatOS | command queue | `frame:dispatch-solvers` | Any thread can enqueue; one-frame latency |
| [Inertial Custom target plus turning-rate feedforward](drive-the-flight-computer-and-attitude-modes-from-a-mod-4.md) | KSA-AdvancedFlightComputer | harmony postfix | `frame:dispatch-solvers`, `physics:vehicle-solver` | Tracks a moving target; worker-side postfixes keyed by shared references |
| [Worker-thread postfix on ComputeControl](drive-the-flight-computer-and-attitude-modes-from-a-mod-5.md) | KSA-AdvancedFlightComputer | harmony postfix | `physics:vehicle-solver` | One sink for all actuator writers |
| [Main-thread tick around ApplyVehicleSolvers](drive-the-flight-computer-and-attitude-modes-from-a-mod-6.md) | KSA-AdvancedFlightComputer | harmony postfix | `frame:apply-solvers` | Fresh results, before stock reacts |
| [ComputeControl prefix with a restoring finalizer](drive-the-flight-computer-and-attitude-modes-from-a-mod-7.md) | Compendium | harmony finalizer | `physics:vehicle-solver` | Edit worker inputs for one call only |
| [Own ConcurrentQueue drained in an ApplyInputEvents prefix](drive-the-flight-computer-and-attitude-modes-from-a-mod-8.md) | Compendium | command queue | `frame:input` | Matches stock timing; UI never writes |
| [Patch every attitude-mode writer to release your target](drive-the-flight-computer-and-attitude-modes-from-a-mod-9.md) | OhMyStars | harmony prefix | `frame:input` | Cooperates with the player's own mode changes |

## See also

- [Run a closed-loop autopilot for every vehicle each step and arbitrate control with the player](../run-an-autopilot-for-every-vehicle-each-step/overview.md)
- [Synthesize a NavigationTarget for an object that has no orbit](../synthesize-a-navigation-target-for-the-flight-computer/overview.md)
- [Read a vehicle's position, velocity, attitude, orbit and flight instruments](../../flight-state/read-vehicle-flight-state/overview.md)
- [Set or clear a vehicle's target, including bodies the game cannot target](../set-and-clear-the-vehicle-target/overview.md)
- [Create, chain and read stock maneuver nodes from code](../create-and-read-maneuver-nodes/overview.md)
- Related topics: [Compute a suicide-burn start altitude and the thrust it needs](../../guidance/compute-a-suicide-burn/overview.md), [Plan and fly a powered landing](../../guidance/plan-and-fly-a-powered-landing/overview.md)

[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-vehiclereferenceframeex]: KSA.VehicleReferenceFrameEx (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatedata]: KSA.VehicleUpdateData (decompiled, build 2026.10.10.5554)
