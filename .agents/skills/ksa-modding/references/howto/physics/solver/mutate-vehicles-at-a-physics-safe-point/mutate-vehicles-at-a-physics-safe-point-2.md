---
type: How-To
title: Drain a command queue in a Priority.First prefix on Universe.ExecuteNextVehicleSolvers
description: Run flight-computer writes, refills, teleports, orbit swaps and shape-registry reads once per frame in the window after the apply calls and before the vehicle step is queued
category: physics
group: solver
topic: mutate-vehicles-at-a-physics-safe-point
approach: 2
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/gatos/features/solver-phase-command-drain
  - mods/ksagolf/features/launch-site-surface-raycast
  - mods/unscience/features/kiwis-marbles-celestial-welding
techniques: [harmony-prefix]
hooks: [harmony-prefix KSA.Universe.ExecuteNextVehicleSolvers, starmap StarMapAllModsLoaded]
lifecycle: [frame:dispatch-solvers, frame:apply-solvers]
game_types: [KSA.Universe, KSA.FlightComputer, KSA.Vehicle, KSA.ConstraintSim, KSA.Orbit]
tags: [physics, vehicle, solver, harmony, prefix, command-queue, orbit, shapes-registry]
---

Outcome: a command your UI or network thread decided during the frame is applied at one main-thread moment
where every vehicle is settled, and is part of the step that is dispatched immediately afterwards. Prerequisites:
StarMap, Harmony. gatOS drains flight-computer and teleport commands here, KSAGolf copies Bepu shapes here, and
Kiwi's Marbles (unscience) swaps celestial orbits here.

## Background: how the game does it

`Universe.ExecuteNextVehicleSolvers` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2139) is the dispatcher:

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

A prefix on it runs after `ApplyOrbitSolvers`, `ApplyVehicleSolvers` and `ApplyClothSolvers`, after
`InputEvents.ApplyInputEvents`, after `ExecuteNextClothSolvers` and the two `PartTree` flushes, and before the
first `PrepareWorker` snapshot (see [per-frame row 6](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence)).
`FlightComputer.CopyFrom` runs at apply and `NewFlightComputer.CopyFrom(flightComputer)` at the snapshot, so
a flight-computer write between the two survives (`FlightComputer.CopyFrom` [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L181-L206),
`VehicleUpdateData.Prepare` [current/decomp/KSA/VehicleUpdateData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateData.cs#L84-L104)).[^ksa-vehicleupdatedata]

## Lifecycle and timing

| Property | Value |
|---|---|
| Thread | main |
| Frequency | once per frame, also while paused (the step is dispatched with `DeltaTime == 0`) and with the HUD hidden |
| Vehicle state | the step applied this frame; no vehicle worker running |
| Orbit state | `ApplyOrbitSolvers` has run and `ExecuteNextOrbitSolvers` has not: the orbit worker snapshots `Celestial.Orbit` after your prefix |
| Shapes registry | idle: `ConstraintSim.UnlockShapes()` succeeds here, as it does anywhere on the main thread before the job is started (it throws once `VehicleUpdateTask.Run` has begun); dispose the unlock before the method returns |
| Still running | the cloth step and the nearest-orbit job; do not edit burn lists or flight-plan points the job reads |
| Latency | a command queued in frame N is drained in frame N+1 and is in the step dispatched there |

Compared with a `Vehicle.PrepareWorker` prefix ([approach 1](mutate-vehicles-at-a-physics-safe-point-1.md)):
this window is earlier and once per frame instead of once per vehicle, and it is the simplest place a mod can reach every
frame to create or free Bepu shapes (the registry is equally idle in a `PrepareWorker` prefix, but that runs once per vehicle). The `PrepareWorker` window is better when the edit is a per-vehicle velocity
or pose write that you want interleaved with other per-vehicle logic.

## Steps

1. Patch `Universe.ExecuteNextVehicleSolvers` at `Priority.First` so your prefix runs before other mods'
   prefixes that may rely on the state you change. gatOS:

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

2. Keep the prefix tiny and exception-proof; it is on the frame's critical path. It forwards to a drain method:

```csharp
// gatOS.GameMod/Game/Mod.Game.cs L849-L855
    internal void DrainSolverCommands()
    {
        if (_commandsDead || _commandQueue is not { } queue)
            return;
        EnsureControlObjects();
        using (_drainStats.Measure()) // shares the stat with the Frame-phase drain (both game-thread)
            queue.Drain(CommandPhase.Solver, _catalog!, Config.MaxCommandsPerFrame);
    }
```

3. Route each command to the right phase at definition time, so callers never choose. gatOS keeps a fixed
   `SolverActions` set (attitude mode, frame, target, burn and RCS mode; refills; teleport and impulse)
   [gatOS.SimFs/Commands/SimCommand.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/SimCommand.cs#L74-L96). Flight-computer fields are in the set because
   `CopyFrom` restores them at apply; manual throttle and thruster flags are not, because
   `Vehicle._manualControlInputs` is not restored by apply and works from an ordinary frame hook.

4. Write flight-computer fields directly in the drain. gatOS' custom attitude shows the one subtlety: KSA
   rebuilds the target from Euler angles each step, so convert the quaternion first:

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

5. Swap an orbit here when something outside the vehicle system must move. Kiwi's Marbles builds a new
   `Orbit` for a celestial from its target's fresh state vectors, once per step, at `Priority.First`, in the
   window where the orbit worker has not yet read `Celestial.Orbit`:

```csharp
// kiwis-marbles.lib/KiwisMarblesPatches.cs L22-L33
    public static void Apply(Harmony harmony)
    {
        var original = AccessTools.Method(typeof(Universe), nameof(Universe.ExecuteNextVehicleSolvers));
        var prefixMethod = AccessTools.Method(typeof(KiwisMarblesPatches), nameof(BeforeVehicleSolvers));

        if (original == null)
            throw new MissingMethodException(typeof(Universe).FullName, nameof(Universe.ExecuteNextVehicleSolvers));
        if (prefixMethod == null)
            throw new MissingMethodException(typeof(KiwisMarblesPatches).FullName, nameof(BeforeVehicleSolvers));

        harmony.Patch(original, prefix: new HarmonyMethod(prefixMethod) { priority = Priority.First });
    }
```

   An orbit swapped from a UI hook after dispatch loses: the orbit worker reads `Celestial.Orbit` when it
   runs and its staged result is written over whatever orbit the body has next frame, so the weld has no
   visible effect (the symptom the unscience authors recorded).[^ksa-celestialupdatetask]

6. Read or create Bepu shapes here. KSAGolf opens `ConstraintSim.UnlockShapes()` inside the prefix and
   copies what it needs; the dedicated how-to for this is
   [create and free shapes in the shared registry](../../colliders/mutate-the-shared-bepu-shape-registry/overview.md):

```csharp
// src/KSAGolf/Ksa/PadSurface.cs L222-L243
    // Nothing is stepping here and nothing can start: the last step has been applied and the next is
    // queued after this returns. A refusal all the same leaves the copy for the next frame.
    private static void BetweenVehicleSteps()
    {
        if (_wanted is not { } body) return;

        try
        {
            using ShapesUnlock unlocked = ConstraintSim.UnlockShapes();
            _pads = Copy(body, unlocked.Shapes);
            _body = body;
            _wanted = null;
            Log.Info($"copied {_pads.Length} launch site(s) on {body.Id} for the ball to rest on, "
                     + $"{_pads.Sum(p => p.Pieces.Length)} collider pieces and {_pads.Sum(p => p.Drawn.Triangles)} drawn ground triangles");
        }
        catch (Exception e)
        {
            if (_complained) return;
            _complained = true;
            Log.Warn($"could not copy the launch sites yet, will try again: {e.GetBaseException().Message}");
        }
    }
```

7. Expect one quiet install line in the log and a single logged warning on failure; the prefix swallows every
   exception (`SolverDrainPrefix` logs at debug level only).

## Complete example

| Mod | Files |
|---|---|
| gatOS | [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L849-L880), [gatOS.GameMod/Game/Ksa/Actuators/FlightComputerActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/FlightComputerActuator.cs), [gatOS.SimFs/Commands/SimCommand.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/SimCommand.cs#L47-L96) |
| KSAGolf | [src/KSAGolf/Ksa/PadSurface.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/PadSurface.cs#L128-L150), [src/KSAGolf/Ksa/PadSurface.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/PadSurface.cs#L222-L275) |
| unscience | [kiwis-marbles.lib/KiwisMarblesPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/KiwisMarblesPatches.cs), [kiwis-marbles.lib/CelestialWeldEngine.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/CelestialWeldEngine.cs#L27-L52) |

## Variations and gotchas

- The window is not settled for every job: `NearestOrbitPointJob` is queued earlier in `PrepareFrame` and
  reads flight plans on its own worker. gatOS' teleport and impulse commands call `Vehicle.Teleport` here
  without waiting on `JobSystems.NearestOrbitAndPerformanceWorker`; teleporting the controlled vessel can
  race it (inferred from the decomp, not observed by gatOS). Unscience's handoff seam
  ([approach 3](mutate-vehicles-at-a-physics-safe-point-3.md)) joins that job explicitly.
- Edits that dirty part-tree derived data are not flushed again before the snapshot, because the
  `PartTree.FlushDirty*` calls already ran. Use the earlier handoff window for those.
- `Vehicle.PrepareWorker` clears `EngineOn` while `BurnMode` is Auto: a burn in progress overrides a
  manual ignition written here.
- `StarMapCore.DeInit` removes every Harmony patch before `[StarMapUnload]` runs, so an
  `UnpatchAll(id)` in the unload method is a no-op; do not rely on it to run last-chance restores
  (Kiwi's Marbles restores in `Dispose` for the same reason).
- The `ExecuteNextVehicleSolvers` signature is `(double dtPlayer, SimStep simStep)`; both KSAGolf and
  Kiwi's Marbles look the method up with `AccessTools.Method(typeof(Universe), nameof(...))` and degrade
  to "feature off" if it is missing.
- If you only need to read applied results rather than change them, a postfix on `ApplyVehicleSolvers`
  is simpler.

## See also

- [Overview and comparison](overview.md)
- [Approach 1: PrepareWorker prefix](mutate-vehicles-at-a-physics-safe-point-1.md)
- [Approach 3: PrepareFrame handoff](mutate-vehicles-at-a-physics-safe-point-3.md)
- Mod features: [gatOS solver-phase command drain](../../../../mods/gatos/features/solver-phase-command-drain.md), [KSAGolf launch-site surface ray cast](../../../../mods/ksagolf/features/launch-site-surface-raycast.md), [Kiwi's Marbles celestial welding](../../../../mods/unscience/features/kiwis-marbles-celestial-welding.md)
- [Game loop reference, settled-window write paths](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md)

[^ksa-vehicleupdatedata]: KSA.VehicleUpdateData (decompiled, build 2026.10.10.5554)
[^ksa-celestialupdatetask]: KSA.CelestialUpdateTask (decompiled, build 2026.10.10.5554)
