---
type: How-To
title: Solver-window drain in a Priority.First prefix on ExecuteNextVehicleSolvers
description: Apply flight computer setpoints, refills and teleports between the physics apply and the next worker snapshot so the asynchronous vehicle solver cannot erase them
category: external-io
group: control
topic: marshal-commands-to-the-game-thread
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/gatos/features/solver-phase-command-drain, mods/gatos/features/game-thread-command-queue]
techniques: [harmony-prefix, command-queue]
hooks: [harmony-prefix KSA.Universe.ExecuteNextVehicleSolvers]
lifecycle: [frame:dispatch-solvers]
game_types: [KSA.Universe, KSA.Vehicle, KSA.FlightComputer, KSA.VehicleUpdateData, KSA.VehicleUpdateTask]
tags: [harmony, prefix, solver, flight-computer, snapshot, teleport]
---

Outcome: commands that write state which KSA's vehicle solver snapshots and restores each frame take effect and stay. Without this, writing `Prograde` into the flight computer from an ordinary per-frame hook makes the navball flash to prograde and snap back to manual. Requires StarMap and Harmony, and approach 1's queue. Demonstrated by [Solver-phase drain for flight computer setpoints](../../../../mods/gatos/features/solver-phase-command-drain.md).

## Background: how the game does it

The solver round trip in build 2026.10.10.5554:

1. `PrepareFrame` waits for `JobSystems.VehicleSolver`, then `Universe.ApplyVehicleSolvers` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1935)) applies results. Per vehicle, `UpdateFromTaskResultsUnsynchronized` runs `FlightComputer.CopyFrom(updateData.NewFlightComputer)`:

   ```csharp
   // current/decomp/KSA/Vehicle.cs L2530-L2530
   		FlightComputer.CopyFrom(updateData.NewFlightComputer);
   ```

2. A few lines later `PrepareFrame` calls `Universe.ExecuteNextVehicleSolvers`, which calls `PrepareVehicleWorkers` and then queues the job:

   ```csharp
   // current/decomp/KSA/Universe.cs L2126-L2139
   	public static void ExecuteNextVehicleSolvers(double dtPlayer, SimStep simStep)
   	{
   		if (CurrentSystem != null)
   		{
   			PrepareVehicleWorkers(simStep);
   			SyncGroundClutter();
   			_vehicleUpdateTask.PrepareFrame(CollectionsMarshal.AsSpan(_orphanVehicles), simStep);
   			JobSystems.VehicleSolver.Queue(_vehicleUpdateTask, 1);
   			_nextPlayerDeltaTime = dtPlayer;
   			_nextSimStep = simStep;
   			JobSystems.VehicleSolverProfiler.Prepare();
   			JobSystems.VehicleSolver.ExecuteJobs();
   		}
   	}
   ```

3. `Vehicle.PrepareWorker` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423)) hands the vehicle state to the worker, and `VehicleUpdateData.Prepare` ends with `NewFlightComputer.CopyFrom(flightComputer)` ([current/decomp/KSA/VehicleUpdateData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateData.cs#L84-L100)).
4. `FlightComputer.CopyFrom` ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L181-L200)) copies `BurnMode`, `AttitudeMode`, `AttitudeFrame`, `AttitudeTrackTarget`, `CustomAttitudeTarget`, `RCSMode`, `Burn` and controller state.

A write after step 3 is not in the worker's snapshot and is overwritten by step 1 of the next frame. A write between steps 1 and 3 is captured. The prefix target sits exactly in that gap.

Teleports have a second reason. `Vehicle.Teleport` detaches the vehicle from its `PhysicsBubble`, and the bubble list belongs to `VehicleUpdateTask`, whose `SyncWindowBubbles` throws while the task runs:

   ```csharp
   // current/decomp/KSA/VehicleUpdateTask.cs L158-L167
   	public ReadOnlySpan<PhysicsBubble> SyncWindowBubbles
   	{
   		get
   		{
   			if (_isRunning)
   			{
   				throw new InvalidOperationException("The bubble list may only be retrieved while the vehicle update task is idle.");
   			}
   			return CollectionsMarshal.AsSpan(_bubbles);
   		}
   ```

## Lifecycle and timing

- Hook point: `frame:dispatch-solvers`, on the main thread, inside `PrepareFrame` after the apply, `Glfw.PollEvents`, `InputEvents.ApplyInputEvents`, the nearest-orbit job queue, `ExecuteNextClothSolvers` and the `PartTree.FlushDirtyDerived()` and `FlushDirtyResourceManagers()` calls ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2196-L2221)). Cloth is already in flight; vehicle state is still owned by the main thread.
- Latency: a command enqueued after frame N's dispatch (that is, while the rest of frame N runs, or at any time before frame N+1 reaches `ExecuteNextVehicleSolvers`) drains in frame N+1's `PrepareFrame` and is in the step dispatched there. One enqueued by a transport thread just before that point still makes it; one just after waits a whole frame. Readers see it after that step is applied.
- A mutation that dirties part-tree derived data here is not flushed again before the snapshot.
- See [the physics step section of the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#the-physics-step) for why main-thread mutation after dispatch is racy.

## Steps

1. **Declare which actions are solver-phase** in the command type, so every transport routes by construction. gatOS's set holds the flight-computer setpoints (`vessel.attitude_mode`, `attitude_frame`, `attitude_target`, `burn`, `rcs_mode`), the refills, and `debug.teleport` and `debug.impulse`.

   ```csharp
   // gatOS.SimFs/Commands/SimCommand.cs L74-L84
       private static readonly HashSet<string> SolverActions =
           new(StringComparer.Ordinal)
           {
               "debug.refill_fuel", "debug.refill_battery",
               // Both call Vehicle.Teleport → RemoveFromCurrentBubble → PhysicsBubble.RemoveVehicle.
               "debug.teleport", "debug.impulse",
               "vessel.attitude_mode", "vessel.attitude_frame", "vessel.attitude_target", "vessel.burn",
               // FlightComputer.RCSMode is copied by CopyFrom alongside the other setpoints, so a
               // frame-phase write would be reverted by the in-flight solve exactly the same way.
               "vessel.rcs_mode",
           };
   ```

2. **Patch `Universe.ExecuteNextVehicleSolvers` with a prefix at `Priority.First`.** It runs before other mods' prefixes, and if the method is not found the mod logs and runs without solver commands instead of failing.

   ```csharp
   // gatOS.GameMod/Game/Mod.Game.cs L859-L881
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
           }
       }

   ```

3. **Drain the Solver queue through the same executor.** The prefix swallows and logs exceptions so a bug cannot break the game's physics dispatch.

   ```csharp
   // gatOS.GameMod/Game/Mod.Game.cs L849-L857, L940-L949
       internal void DrainSolverCommands()
       {
           if (_commandsDead || _commandQueue is not { } queue)
               return;
           EnsureControlObjects();
           using (_drainStats.Measure()) // shares the stat with the Frame-phase drain (both game-thread)
               queue.Drain(CommandPhase.Solver, _catalog!, Config.MaxCommandsPerFrame);
       }

       // ...
       private static void SolverDrainPrefix()
       {
           try
           {
               _instance?.DrainSolverCommands();
           }
           catch (Exception ex)
           {
               ModLog.Log.Debug($"gatOS solver-phase drain error: {ex.Message}");
           }
   ```

4. **Write actuators that survive the round trip.** For example the custom attitude target: KSA's `Custom` track rebuilds its target from Euler angles every step, so the actuator converts the desired quaternion into Euler angles in the inertial `EclBody` frame.

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

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L849-L880) | `DrainSolverCommands`, `InstallSolverHook`, `SolverDrainPrefix` |
| [gatOS.SimFs/Commands/SimCommand.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/SimCommand.cs#L47-L96) | `SolverActions` and the derived `Phase` |
| [gatOS.GameMod/Game/Ksa/Actuators/FlightComputerActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/FlightComputerActuator.cs) | Attitude mode, frame, target, burn, RCS mode |

## Variations and gotchas

- **Do not put every command here.** Manual throttle and thruster flags live in `Vehicle._manualControlInputs`, which apply does not restore, so they work from the Frame phase.
- `Vehicle.PrepareWorker` forces `EngineOn = false` when `FlightComputer.BurnMode` is `Auto`, so a burn in progress overrides manual ignition ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2416)).
- **Unpatching.** gatOS calls `UnpatchAll("gatos.solver")` at unload; with the pinned StarMap, `StarMapCore.DeInit` has already run an id-less `Harmony.UnpatchAll()` ([StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35)), so the id-scoped call is a no-op. Do not depend on it.
- **Teleport timing.** gatOS calls `Vehicle.Teleport` here, after the nearest-orbit job was queued, and does not wait on `JobSystems.NearestOrbitAndPerformanceWorker` first; KSA's own location teleport goes through `InputEvents.TeleportInputBuffer`, drained before that job is queued. Teleporting the controlled vessel from this hook can therefore race the job (inferred from the decomp, not reported by gatOS). See [Change vehicles without racing the solver](../../../vehicles-and-parts/vehicles/change-vehicles-without-racing-the-solver/overview.md).
- **Anchor risk.** The prefix targets the single `(double, SimStep)` overload; a signature change turns the feature off with a warning rather than breaking the game.

## See also

- [Overview](overview.md)
- [Approach 1: the command queue](marshal-commands-to-the-game-thread-1.md)
- [Solver-phase drain for flight computer setpoints](../../../../mods/gatos/features/solver-phase-command-drain.md), [Game-thread command queue](../../../../mods/gatos/features/game-thread-command-queue.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
