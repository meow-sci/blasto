---
type: How-To
title: Set attitude mode and an arbitrary quaternion from a solver-phase command drain
description: Drain a second command queue in a Priority.First prefix on Universe.ExecuteNextVehicleSolvers, convert a requested body-to-CCI quaternion to EclBody Euler angles for the Custom track, and understand why frame-phase writes are erased by CopyFrom
category: flight-and-orbits
group: autopilot
topic: drive-the-flight-computer-and-attitude-modes-from-a-mod
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/gatos/features/solver-phase-command-drain]
techniques: [command-queue, harmony-prefix]
hooks: [harmony-prefix KSA.Universe.ExecuteNextVehicleSolvers]
lifecycle: [frame:dispatch-solvers]
game_types: [KSA.Universe, KSA.FlightComputer, KSA.VehicleReferenceFrameEx, KSA.VehicleUpdateData, KSA.Vehicle]
tags: [attitude, quaternion, command-queue, solver-phase, copy-from, euler]
---

Outcome: scripts and network clients can set the attitude mode, frame, and an arbitrary target quaternion, and the result sticks. gatOS found that writing `Prograde` into `ctl/attitude_mode` from its normal per-frame hook made the navball flash to prograde and snap back to manual.
It now drains a second queue in a prefix on `Universe.ExecuteNextVehicleSolvers`.[Solver-phase drain for flight computer setpoints](../../../../mods/gatos/features/solver-phase-command-drain.md) Prerequisites: StarMap and Harmony.

## Background: how the game does it

The same round trip as in the [overview](overview.md): `PrepareWorker` -> `UpdateData.Prepare(...)` -> `NewFlightComputer.CopyFrom(flightComputer)` (L84-L100), and at the next `ApplyVehicleSolvers` `FlightComputer.CopyFrom(updateData.NewFlightComputer)`. A write made after the snapshot (for example in `StarMapBeforeGui`) is not in the worker's copy and is overwritten by the next frame's copy-back; a write made between the apply and the
snapshot is captured.[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2529-L2531) [^ksa-vehicleupdatedata] [current/decomp/KSA/VehicleUpdateData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateData.cs#L84-L100) [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L181-L200) Which fields are affected: `BurnMode`, `AttitudeMode`, `AttitudeFrame`, `AttitudeTrackTarget`, `CustomAttitudeTarget`, `RCSMode` and `Burn` (see [Create, chain and read stock maneuver nodes from code](../create-and-read-maneuver-nodes/overview.md) approach 2).

## Lifecycle and timing

`frame:dispatch-solvers`: the prefix runs inside `PrepareFrame` after the apply, `Glfw.PollEvents`, `InputEvents.ApplyInputEvents`, the `NearestOrbitPointJob` queue, `Universe.GetJobSimStep`, `ExecuteNextClothSolvers` and the dirty-derived flushes, and before `PrepareVehicleWorkers` snapshots any vehicle.[^ksa-program] [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2196-L2220)
It sees flushed derived data, but anything the cloth jobs read is already in flight, and a mutation that dirties part-tree derived data here is not flushed again before the snapshot. A command enqueued during frame N drains in frame N+1's `PrepareFrame` and is in the step dispatched there; readers see it after that step's apply.
See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Route by phase.** Keep a fixed set of solver-phase actions (`vessel.attitude_mode`, `attitude_frame`, `attitude_target`, `burn`, `rcs_mode`, refills, teleport, impulse) so each transport enqueues in the right queue ([gatOS.SimFs/Commands/SimCommand.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/SimCommand.cs#L74-L96)).
2. **Install the hook** with a prefix at `Priority.First`:

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

3. **Set the mode:** `manual` sets `AttitudeMode = Manual`; a `FlightComputerAttitudeTrackTarget` name (`Prograde`, `Retrograde`, ...) sets `Auto` plus that track target; parse a `VehicleReferenceFrame` for the frame.
4. **Set an arbitrary quaternion.** KSA's Custom track rebuilds `Target2Cci` as `Concatenate(EulerAnglesToQuaternion(CustomAttitudeTarget), frame2Cci)` every step, so express the desired body-to-CCI rotation as Euler angles in the inertial `EclBody` frame; the round trip is exact:

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

5. **Swallow and log exceptions** in the prefix (`SolverDrainPrefix`) and never throw into `PrepareFrame`.
6. **Report latency honestly.** Clients should read the mode back after the next apply, not in the same call.

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L849-L880) | `DrainSolverCommands` and `InstallSolverHook` |
| [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L940-L950) | `SolverDrainPrefix` (swallows and logs) |
| [gatOS.GameMod/Game/Ksa/Actuators/FlightComputerActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/FlightComputerActuator.cs) | attitude mode, frame, target, burn, RCS mode |

## Variations and gotchas

- **`Priority.First`** puts the drain ahead of other mods' prefixes on the same method.
- **Teleport and refills** belong here too: `Vehicle.Teleport` detaches from the physics bubble, whose list the running task owns.
- **Why not `StarMapBeforeGui`?** It runs after the snapshot; the write is erased at the next apply and the navball flickers.
- The `burn` action in the same drain is the direct-burn alternative to a node (approach 2 of [Create, chain and read stock maneuver nodes from code](../create-and-read-maneuver-nodes/overview.md)).

## See also

- [Overview](overview.md); [PrepareWorker prefix](drive-the-flight-computer-and-attitude-modes-from-a-mod-2.md); [post-solver tick](drive-the-flight-computer-and-attitude-modes-from-a-mod-6.md)
- [Solver-phase drain for flight computer setpoints](../../../../mods/gatos/features/solver-phase-command-drain.md)

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatedata]: KSA.VehicleUpdateData (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
