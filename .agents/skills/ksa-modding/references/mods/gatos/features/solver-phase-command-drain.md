---
type: Mod Feature
title: Solver-phase drain for flight computer setpoints
description: A Priority.First Harmony prefix on Universe.ExecuteNextVehicleSolvers is the one window where flight computer modes, burns, refills and teleports written from the main thread survive KSA's async vehicle solver
mod: gatos
category: patching
techniques: [harmony-prefix, command-queue]
hooks: [harmony-prefix KSA.Universe.ExecuteNextVehicleSolvers]
lifecycle: [frame:dispatch-solvers]
game_types:
  - KSA.Universe
  - KSA.Vehicle
  - KSA.FlightComputer
  - KSA.VehicleUpdateState
  - KSA.VehicleUpdateData
  - KSA.VehicleUpdateTask
  - KSA.BurnTarget
  - KSA.VehicleReferenceFrameEx
tags: [harmony, flight-computer, solver, threading, attitude, burn, physics-step]
---

Writing `Prograde` into `ctl/attitude_mode` from the normal per-frame hook made the navball
flash to prograde and snap back to manual. The cause is KSA's asynchronous vehicle solver: it
snapshots the whole `FlightComputer` when it dispatches a step and copies the worker's copy back
when it applies the result, so any main-thread write made in between is erased. gatOS drains a
second command queue inside a Harmony prefix on `Universe.ExecuteNextVehicleSolvers`, which runs
after the apply and before the next snapshot. This page documents that window and the flight
computer actuators that need it.

## How it works

**The solver round trip in build 2026.10.10.5554:**

1. `Program.PrepareFrame` waits for `JobSystems.VehicleSolver`, then `Universe.ApplyVehicleSolvers`
   calls `_vehicleUpdateTask.ApplyResultsToMainThread()`; per vehicle,
   `UpdateFromTaskResultsUnsynchronized` runs `FlightComputer.CopyFrom(updateData.NewFlightComputer)`
   ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2529-L2531)).
2. A few lines later `PrepareFrame` calls `Universe.ExecuteNextVehicleSolvers`, which calls
   `PrepareVehicleWorkers` then queues the job ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2155)).
3. `Vehicle.PrepareWorker` calls `_threadWorkerUpdateState.PrepareFromVehicle(...)`, which runs
   `UpdateData.Prepare(..., ReadOnlyVehicle.FlightComputer)`, and that does
   `NewFlightComputer.CopyFrom(flightComputer)` ([current/decomp/KSA/VehicleUpdateData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateData.cs#L84-L100)).
4. `FlightComputer.CopyFrom` copies `BurnMode`, `AttitudeMode`, `AttitudeFrame`,
   `AttitudeTrackTarget`, `CustomAttitudeTarget`, `RCSMode`, `Burn` and the controller state
   ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L181-L200)).

A write made after step 3 (for example in `StarMapBeforeGui`) is not in the worker's snapshot and
is overwritten by step 1 of the next frame. A write made between steps 1 and 3 is captured.

**The hook.** `InstallSolverHook` patches `Universe.ExecuteNextVehicleSolvers` with a prefix at
`Priority.First`, so it runs before any other mod's prefix, and the prefix drains the `Solver`
queue through the same `KsaCatalog` executor ([gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L849-L880)).
Which actions go there is a fixed set in `SimCommand.SolverActions`, so every transport routes
correctly by construction ([gatOS.SimFs/Commands/SimCommand.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/SimCommand.cs#L74-L96)):

| Action | Why Solver phase |
|---|---|
| `vessel.attitude_mode`, `attitude_frame`, `attitude_target`, `burn`, `rcs_mode` | `FlightComputer.CopyFrom` snapshot/restore |
| `debug.refill_fuel`, `debug.refill_battery` | resource state the solver reads that step |
| `debug.teleport`, `debug.impulse` | `Vehicle.Teleport` detaches from its `PhysicsBubble`; the bubble list belongs to `VehicleUpdateTask`, whose `SyncWindowBubbles` throws while the task runs ([current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L158-L167)) |

**Flight computer actuators** ([gatOS.GameMod/Game/Ksa/Actuators/FlightComputerActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/FlightComputerActuator.cs)):

- `SetAttitudeMode`: `manual` sets `AttitudeMode = Manual`; any `FlightComputerAttitudeTrackTarget`
  name (`Prograde`, `Retrograde`, ...) sets `Auto` plus that track target.
- `SetAttitudeFrame`: parses a `VehicleReferenceFrame`.
- `SetAttitudeTarget(x y z w)`: KSA's `Custom` track rebuilds its target from Euler angles every
  step, so gatOS converts the desired body-to-CCI quaternion into Euler angles in the inertial
  `EclBody` frame using `VehicleReferenceFrameEx.GetEclBody2Cci` and `QuaternionToEulerAngles`, which
  round-trips exactly.
- `SetBurn(ut dvx dvy dvz)`: assigns `FlightComputer.Burn = new BurnTarget { ImpulsiveInstant, DeltaVTargetCci }`
  and lets the onboard autopilot execute it, which keeps working under time warp.
- `SetRcsMode`: the FC's RCS master (in-game R); `Disabled` makes KSA zero manual thruster flags.

## Lifecycle and timing

- `frame:dispatch-solvers`: the prefix runs inside `PrepareFrame` after the apply, `Glfw.PollEvents`,
  `InputEvents.ApplyInputEvents`, the `NearestOrbitPointJob` queue, `Universe.GetJobSimStep`,
  `ExecuteNextClothSolvers` and the `PartTree.FlushDirtyDerived()` / `FlushDirtyResourceManagers()`
  calls, and before `PrepareVehicleWorkers` snapshots any vehicle
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2196-L2220)). It therefore sees flushed derived data, but anything the cloth
  jobs read is already in flight, and a mutation that dirties part-tree derived data here is not
  flushed again before the snapshot.
- Main thread; the previous step has been waited for and applied, the next has not started, so
  bubble lists and flight computer state are owned by the main thread.
- Latency: a command enqueued during frame N drains in frame N+1's `PrepareFrame` and is in the
  step dispatched there. Readers see it after that step's apply.

## Key code

| Path | Role |
|---|---|
| [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L849-L880) | `DrainSolverCommands` and `InstallSolverHook` |
| [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L940-L950) | `SolverDrainPrefix` (swallows and logs) |
| [gatOS.SimFs/Commands/SimCommand.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/SimCommand.cs#L47-L96) | `SolverActions` and the derived `Phase` |
| [gatOS.GameMod/Game/Ksa/Actuators/FlightComputerActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/FlightComputerActuator.cs) | attitude mode/frame/target, burn, RCS mode |

## Snippets

```csharp
// gatOS.GameMod/Game/Mod.Game.cs L858-L878
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    partial void InstallSolverHook()
    {
        try
        {
            _solverHarmony = new Harmony("gatos.solver");
            var original = AccessTools.Method(typeof(Universe), nameof(Universe.ExecuteNextVehicleSolvers));
            if (original is null)
            {
                ModLog.Log.Warn("Solver hook: Universe.ExecuteNextVehicleSolvers not found; "
                                + "solver-phase commands (refills) are disabled.");
                return;
            }

            var prefix = new HarmonyMethod(typeof(Mod), nameof(SolverDrainPrefix)) { priority = Priority.First };
            _solverHarmony.Patch(original, prefix: prefix);
            ModLog.Log.Info("gatOS solver-phase command hook installed.");
        }
        catch (Exception ex)
        {
            ModLog.Log.Error($"gatOS solver hook failed to install (solver commands disabled): {ex.Message}");
```

```csharp
// gatOS.GameMod/Game/Ksa/Actuators/FlightComputerActuator.cs L85-L105
    internal static CommandResult SetAttitudeTarget(Vehicle vehicle, IReadOnlyList<double> quat)
    {
        if (quat.Count != 4)
            return new CommandResult(CommandOutcome.Invalid, "attitude_target expects 'x y z w'");
        var fc = vehicle.FlightComputer;
        var target2Cci = new doubleQuat(quat[0], quat[1], quat[2], quat[3]);

        // KSA's Custom track recomputes Target2Cci as Concatenate(EulerAnglesToQuaternion(CustomAttitudeTarget),
        // frame2Cci) every solver step, so we express the desired Body→CCI rotation as euler angles in the
        // (inertial) EclBody frame. The round-trip is exact: with euler = QuaternionToEulerAngles(target2Cci ∘
        // frame2Cci⁻¹), KSA rebuilds Concatenate(target2Cci ∘ frame2Cci⁻¹, frame2Cci) == target2Cci.
        const VehicleReferenceFrame frame = VehicleReferenceFrame.EclBody;
        var frame2Cci = VehicleReferenceFrameEx.GetEclBody2Cci(vehicle.Orbit.Parent.GetCce2Cci());
        var frame2Desired = doubleQuat.Concatenate(target2Cci, frame2Cci.Inverse());

        fc.AttitudeMode = FlightComputerAttitudeMode.Auto;
        fc.AttitudeTrackTarget = FlightComputerAttitudeTrackTarget.Custom;
        fc.AttitudeFrame = frame;
        fc.CustomAttitudeTarget = frame.QuaternionToEulerAngles(frame2Desired);
        return CommandResult.Ok;
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Universe.ExecuteNextVehicleSolvers(double, SimStep)` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2139) | Harmony prefix target |
| `FlightComputer.CopyFrom` | [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L181-L206) | the snapshot/restore that forces this phase |
| `FlightComputer.AttitudeMode`, `AttitudeFrame`, `CustomAttitudeTarget`, `RCSMode`, `Burn` | [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L34-L67) | setpoints written |
| `BurnTarget.ImpulsiveInstant`, `DeltaVTargetCci` | [current/decomp/KSA/BurnTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnTarget.cs#L8-L20) | burn target |
| `VehicleReferenceFrameEx.GetEclBody2Cci`, `QuaternionToEulerAngles` | [current/decomp/KSA/VehicleReferenceFrameEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleReferenceFrameEx.cs#L76-L76) | custom attitude conversion |
| `VehicleUpdateData.Prepare` | [current/decomp/KSA/VehicleUpdateData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateData.cs#L84-L100) | worker snapshot of the FC |

## Pitfalls and version notes

- Do not put every command here. Manual throttle and thruster flags live in
  `Vehicle._manualControlInputs`, which apply does not restore, so they work from the Frame phase
  (see [Manual throttle and RCS inputs](manual-throttle-and-rcs-inputs.md)).
- `Vehicle.PrepareWorker` forces `EngineOn = false` when `FlightComputer.BurnMode` is `Auto`, so a
  burn in progress overrides manual ignition ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2416)).
- `BurnTarget.ImpulsiveInstant` became a `UniverseTime` when KSA moved to nanosecond time (gatOS
  anchor notes); gatOS builds it with `new UniverseTime(double seconds)`.
- The prefix is un-anchored in gatOS's own attribute scheme (a Harmony target, not a reader); gatOS
  re-checked it at 5482. In 5554 the single `(double, SimStep)` overload is unchanged.
- gatOS calls `UnpatchAll("gatos.solver")` from `[StarMapUnload]`. With StarMap 4dc60f8 that is a
  no-op: `StarMapCore.DeInit` has already run `Harmony.UnpatchAll()` without an id, removing every
  mod's patches, before the unload methods are invoked
  ([StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35)).
- `debug.teleport` and `debug.impulse` call `Vehicle.Teleport` here, after the frame's
  `NearestOrbitPointJob` was queued, and gatOS does not wait on
  `JobSystems.NearestOrbitAndPerformanceWorker` first. KSA's own location teleport instead adds to
  `InputEvents.TeleportInputBuffer`, which `ApplyInputEvents` drains before that job is queued
  ([current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1114-L1140)). The job reads the controlled vehicle's flight plan, so
  teleporting the controlled vessel here can race it (inferred from the decomp, not reported by gatOS).

## See also

- [Game-thread command queue](game-thread-command-queue.md)
- [Manual throttle and RCS inputs](manual-throttle-and-rcs-inputs.md)
- [Debug cheats for teleport, refuel, warp and impulse](debug-cheats-teleport-refuel-warp.md)
- [Frame hooks with an F2-proof fallback](frame-hooks-and-f2-fallback.md)
- [gatOS overview](../overview.md)
- [Solver-window drain in a Priority.First prefix on ExecuteNextVehicleSolvers](../../../howto/external-io/control/marshal-commands-to-the-game-thread/marshal-commands-to-the-game-thread-2.md) - how-to article demonstrating this feature
- [Schedule an impulsive burn directly on the flight computer from a solver-phase prefix](../../../howto/flight-and-orbits/autopilot/create-and-read-maneuver-nodes/create-and-read-maneuver-nodes-2.md) - how-to article demonstrating this feature
- [Set attitude mode and an arbitrary quaternion from a solver-phase command drain](../../../howto/flight-and-orbits/autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/drive-the-flight-computer-and-attitude-modes-from-a-mod-3.md) - how-to article demonstrating this feature
- [Mutate simulation state safely relative to the physics step](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md) - how-to article demonstrating this feature
- [Priority.First prefixes and Priority.Last finalizers around shared seams](../../../howto/patching/harmony/coexist-with-other-mods-patching-the-same-method/coexist-with-other-mods-patching-the-same-method-1.md) - how-to article demonstrating this feature
- [Drain a command queue in a Priority.First prefix on Universe.ExecuteNextVehicleSolvers](../../../howto/physics/solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-2.md) - how-to article demonstrating this feature

