---
type: How-To
title: Schedule an impulsive burn directly on the flight computer from a solver-phase prefix
description: Set FlightComputer.Burn to a BurnTarget in a Priority.First prefix on Universe.ExecuteNextVehicleSolvers, the one window where a main-thread write survives the CopyFrom copy-back
category: flight-and-orbits
group: autopilot
topic: create-and-read-maneuver-nodes
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/gatos/features/solver-phase-command-drain]
techniques: [command-queue, harmony-prefix]
hooks: [harmony-prefix KSA.Universe.ExecuteNextVehicleSolvers]
lifecycle: [frame:dispatch-solvers]
game_types: [KSA.FlightComputer, KSA.BurnTarget, KSA.Universe, KSA.Vehicle, KSA.UniverseTime]
tags: [burn, burn-target, flight-computer, copy-from, worker-copy, solver-phase]
---

Outcome: an impulsive burn ("at this universe time apply this CCI delta-v") that the onboard autopilot executes under time warp, without
adding a node to the burn plan. gatOS exposes it as the `burn` control file.[Solver-phase drain for flight computer setpoints](../../../../mods/gatos/features/solver-phase-command-drain.md) Prerequisites: StarMap and Harmony.

## Background: how the game does it

The vehicle solver round trip, as documented in the lifecycle reference: `Vehicle.PrepareWorker` ends with `VehicleUpdateData.Prepare`, which runs
`NewFlightComputer.CopyFrom(flightComputer)`; at the next `ApplyVehicleSolvers`, `UpdateFromTaskResultsUnsynchronized` runs
`FlightComputer.CopyFrom(updateData.NewFlightComputer)` unconditionally.[^ksa-vehicleupdatedata] [current/decomp/KSA/VehicleUpdateData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateData.cs#L84-L100)
[current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L181-L225) `CopyFrom` copies `BurnMode`, the attitude fields, `RCSMode` and `Burn` (the loaded `BurnTarget`, by reference), so a main-thread write to any of
them survives only if it lands between `ApplyVehicleSolvers` and that vehicle's `PrepareWorker`. The details are in
[Mutate simulation state safely](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md) and
[Reach the physics worker through the references FlightComputer.CopyFrom shares](../../../../mods/ksa-advancedflightcomputer/features/flight-computer-worker-copy-channels.md).

## Lifecycle and timing

`frame:dispatch-solvers`: the prefix on `Universe.ExecuteNextVehicleSolvers` runs inside `PrepareFrame` after the apply, `Glfw.PollEvents`,
`InputEvents.ApplyInputEvents`, `Universe.GetJobSimStep` and the dirty-derived-data flushes, and before `PrepareVehicleWorkers` snapshots any vehicle.[^ksa-program]
[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2196-L2220) It runs on the main thread with the previous step applied and the next not started, so the flight computer is owned by the main thread. A command
enqueued in frame N drains in frame N+1's `PrepareFrame` and is in the step dispatched there.

## Steps

1. **Queue commands from wherever they arrive** (HTTP, UI, hotkeys) into a thread-safe queue; the prefix is the only consumer.
2. **Patch `ExecuteNextVehicleSolvers` with a prefix at `Priority.First`**, so it runs before any other mod's prefix:

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

3. **Drain the queue in the prefix and write the target:**

```csharp
// gatOS.GameMod/Game/Ksa/Actuators/FlightComputerActuator.cs L121-L133
internal static CommandResult SetBurn(Vehicle vehicle, IReadOnlyList<double> burn)
{
    if (burn.Count != 4)
        return new CommandResult(CommandOutcome.Invalid, "burn expects 'ut dvx dvy dvz'");
    if (!double.IsFinite(burn[0]))
        return new CommandResult(CommandOutcome.Invalid, "burn ut must be finite");
    vehicle.FlightComputer.Burn = new BurnTarget
    {
        ImpulsiveInstant = new UniverseTime(burn[0]),
        DeltaVTargetCci = new float3((float)burn[1], (float)burn[2], (float)burn[3]),
    };
    return CommandResult.Ok;
}
```

   `ImpulsiveInstant` is a `UniverseTime` (its constructor throws on NaN, so validate first); `DeltaVTargetCci` is a `float3` in the
   parent's CCI frame. The autopilot flies the impulse, including through time warp.
4. **Swallow and log exceptions** in the prefix (`SolverDrainPrefix`): an exception here would abort `PrepareFrame`.
5. **Route by phase.** gatOS keeps a fixed `SolverActions` set (attitude mode, frame, target, burn, RCS mode, refills, teleport) so each transport
   places a command in the right queue automatically ([gatOS.SimFs/Commands/SimCommand.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/SimCommand.cs#L74-L96)).

## Complete example

[gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L849-L880) (`DrainSolverCommands`, `InstallSolverHook`) and
[gatOS.GameMod/Game/Ksa/Actuators/FlightComputerActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/FlightComputerActuator.cs) (attitude mode, frame, target, burn, RCS mode).

## Variations and gotchas

- **This is not a node.** `FlightComputer.Burn` is the loaded burn target; nothing is added to `BurnPlan`, so the burn does not show in the planner. For a
  stock node use [approach 1](create-and-read-maneuver-nodes-1.md).
- **A write in `StarMapBeforeGui` flickers.** gatOS found that writing the attitude from the normal per-frame hook made the navball flash to prograde and snap
  back: the write landed after the snapshot and was erased by the next copy-back.
- **Other mods' prefixes** on the same method run in priority order; `Priority.First` puts yours ahead of the snapshot logic of mods that write later.
- Teleports and refills also belong to this phase: `Vehicle.Teleport` detaches the vehicle from its physics bubble, whose list the running task owns.

## See also

- [Overview](overview.md); [Burn.Create queued](create-and-read-maneuver-nodes-1.md)
- [Solver-phase drain for flight computer setpoints](../../../../mods/gatos/features/solver-phase-command-drain.md)
- [Drive the flight computer and attitude modes from a mod](../drive-the-flight-computer-and-attitude-modes-from-a-mod/overview.md)

[^ksa-vehicleupdatedata]: KSA.VehicleUpdateData (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
