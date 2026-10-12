---
type: How-To
title: Impulsive kick from the solver-dispatch window
description: Apply an instantaneous velocity change to a vessel by rebuilding its orbit from the bumped CCI state vector in a prefix on Universe.ExecuteNextVehicleSolvers
category: physics
group: solver
topic: inject-forces-and-impulses-at-the-physics-safe-point
approach: 3
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/gatos/features/debug-cheats-teleport-refuel-warp]
techniques: [harmony-prefix, kinematic-teleport]
hooks: [harmony-prefix KSA.Universe.ExecuteNextVehicleSolvers]
lifecycle: [frame:dispatch-solvers]
game_types: [KSA.Vehicle, KSA.Orbit, KSA.Universe, KSA.PhysicsBubble, KSA.VehicleUpdateTask]
tags: [physics, vehicle, impulse, delta-v, teleport, orbit, cheat, solver-window]
---

Outcome: a one-shot kick (a scripted delta-v, a debug cheat) applied to a vessel in any state, on rails or in
the physics bubble, with no per-part model and no per-frame state. gatOS' `debug.impulse` demonstrates it.
Prerequisites: StarMap and Harmony. Compared with [approach 1](inject-forces-and-impulses-at-the-physics-safe-point-1.md)
this does not write `VelocityPhys`; it rebuilds the vessel's orbit from the bumped state vector and
teleports it, which makes it the simplest variant and also the heaviest (the vessel leaves its physics bubble).

## Background: how the game does it

`Vehicle.Teleport(Orbit?, doubleQuat?, double3?)` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2241-L2290) computes the new trajectory
and, if that succeeds, calls `RemoveFromCurrentBubble()`, sets the flight plan and applies the optional
attitude and rates. If the trajectory cannot be computed it logs
`Failed to compute trajectory for new orbit during teleport, aborting teleport.` and returns without throwing.
`RemoveFromCurrentBubble` drops the vehicle's update state from the bubble's list, which the vehicle worker
enumerates; `VehicleUpdateTask.SyncWindowBubbles` throws unless the task is idle
[current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L158-L167). The task runs from the end of one `PrepareFrame` until the next frame's
`JobSystems.VehicleSolver.Wait()`, so the only safe window is after that wait and before the job is re-queued.

`Orbit.CreateFromStateCci` [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1611-L1640) builds a conic from a body-centred inertial state
vector at a given time. The impulse math is the same as the engine's separation impulse: delta-v is impulse over
`TotalMass`.

## Lifecycle and timing

| Property | Value |
|---|---|
| Hook | `Priority.First` prefix on `Universe.ExecuteNextVehicleSolvers` (see [approach 2 of the safe-point topic](../../solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-2.md)) |
| Thread and frame | main, once per frame; a command queued in frame N is applied in frame N+1 and is in the step dispatched there |
| State at the hook | The step applied this frame; no vehicle worker running |
| Epoch used | `Universe.GetElapsedTime()`, the applied step's `NextTime`, the instant the state vectors describe |
| Not covered | The nearest-orbit job queued earlier in `PrepareFrame` is still running and reads the controlled vessel's flight plan; a teleport here can race it (inferred from the decomp; gatOS does not wait on `JobSystems.NearestOrbitAndPerformanceWorker`) |

gatOS puts `debug.teleport` and `debug.impulse` in a fixed "Solver" command set so every transport (file
system, HTTP, MQTT) routes them to this window by construction. A frame-phase teleport that worked before build
5348 now races the solver, because bubble management moved onto the solver thread.

## Steps

1. Install the dispatch-window prefix as in [approach 2](../../solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-2.md)
   and queue the command from any thread; the drain runs it here.

2. Validate on the main thread: finite components, a parent body, a positive finite mass for an N s impulse.
   Return a result instead of throwing.

3. Convert the vector to CCI (rotate a body-frame input by `GetBody2Cci()`), divide an impulse by `TotalMass`
   to get delta-v, add it to `GetVelocityCci()`, check the result is finite, rebuild the orbit and teleport:

```csharp
// gatOS.GameMod/Game/Ksa/Actuators/DebugActuator.cs L79-L111
    internal static CommandResult Impulse(Vehicle vehicle, IReadOnlyList<double> vector, string? frame, string? unit)
    {
        // Re-validate through the shared game-free rules: the HTTP/MQTT command paths reach here
        // without the 9p control-file parse, so arity/keywords are not pre-checked on that route.
        if (ImpulseRules.Validate(vector, frame, unit) is { } error)
            return new CommandResult(CommandOutcome.Invalid, error);
        if (vehicle.Parent is not { } parent)
            return new CommandResult(CommandOutcome.Busy, "vessel has no parent body to impulse about");

        var input = new double3(vector[0], vector[1], vector[2]);
        var cci = ImpulseRules.IsBodyFrame(frame) ? double3.Transform(input, vehicle.GetBody2Cci()) : input;
        var deltaV = cci;
        if (!ImpulseRules.IsDeltaV(unit))
        {
            double mass = vehicle.TotalMass;
            if (!double.IsFinite(mass) || mass <= 0)
                return new CommandResult(CommandOutcome.Busy, "vessel mass unavailable for an N·s impulse");
            deltaV = cci / mass;
        }

        if (deltaV.X == 0 && deltaV.Y == 0 && deltaV.Z == 0)
            return CommandResult.Ok; // zero kick — nothing to apply

        var velocity = vehicle.GetVelocityCci() + deltaV;
        if (!double.IsFinite(velocity.X) || !double.IsFinite(velocity.Y) || !double.IsFinite(velocity.Z))
            return new CommandResult(CommandOutcome.Invalid, "resulting velocity is not finite");

        var orbit = Orbit.CreateFromStateCci(parent, Universe.GetElapsedTime(),
            vehicle.GetPositionCci(), velocity, default);
        vehicle.Teleport(orbit, null, null);
        vehicle.UpdatePerFrameData();
        return CommandResult.Ok;
    }
```

4. `UpdatePerFrameData()` after the teleport refreshes the cached CCE and ecliptic state immediately, so readers in
   the same frame see the new velocity. Expect the vessel's orbit line to change on the next frame; there is no
   log line on success, and a failed trajectory logs the `Failed to compute trajectory` error from the engine.

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Actuators/DebugActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/DebugActuator.cs#L50-L111) | Teleport and impulse actuators |
| [gatOS.SimFs/Commands/ImpulseRules.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/ImpulseRules.cs) | Game-free validation of vector, frame and unit keywords |
| [gatOS.SimFs/Commands/SimCommand.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/SimCommand.cs#L74-L96) | Which actions are Solver phase |
| [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L849-L880) | The `ExecuteNextVehicleSolvers` drain |

## Variations and gotchas

- This is a teleport, not an integrated push: the vessel is removed from its bubble and re-bubbled at the next
  dispatch (`Universe.PrepareVehicleWorkers`), and its flight plan is replaced. A craft in contact with the
  ground or other craft loses that bubble state; for a force that should act inside the bubble use
  [approach 1](inject-forces-and-impulses-at-the-physics-safe-point-1.md).
- The kick is instantaneous: it does not scale with the step, so it needs no warp handling, and a repeated kick
  must be rate-limited by the caller.
- Neither `Vehicle.Teleport` nor `Orbit.CreateFromStateCci` throws on a bad vector; non-finite input produces a
  failed trajectory or a corrupt orbit, so validate first as above.
- `StarMapCore.DeInit` removes every Harmony patch before `[StarMapUnload]` runs, so do not rely on unpatching
  from the unload hook.
- To teleport to a given state vector rather than add to it, see the topic
  [teleport a vessel by building a new orbit](../../kinematics/teleport-a-vessel-by-building-a-new-orbit/overview.md).

## See also

- [Overview and comparison](overview.md)
- [Approach 1: velocity deltas in the PrepareWorker prefix](inject-forces-and-impulses-at-the-physics-safe-point-1.md)
- Mod feature: [gatOS debug cheats: teleport, refuel, warp](../../../../mods/gatos/features/debug-cheats-teleport-refuel-warp.md)
- [Mutate vehicles at a physics-safe point](../../solver/mutate-vehicles-at-a-physics-safe-point/overview.md)
