---
type: How-To
title: Teleport to CCI state vectors in the solver-dispatch window
description: Build an orbit from a Cci position and velocity with Orbit.CreateFromStateCci and call Vehicle.Teleport from a prefix on ExecuteNextVehicleSolvers, with refill and impulsive-kick variants and the nearest-orbit-job race noted
category: physics
group: kinematics
topic: teleport-a-vessel-by-building-a-new-orbit
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/gatos/features/debug-cheats-teleport-refuel-warp]
techniques: [harmony-prefix, kinematic-teleport]
hooks: [harmony-prefix KSA.Universe.ExecuteNextVehicleSolvers]
lifecycle: [frame:dispatch-solvers]
game_types: [KSA.Vehicle, KSA.Orbit, KSA.Universe, KSA.Battery, KSA.ModuleStateful, KSA.PartTree]
tags: [physics, kinematics, teleport, orbit, cci, impulse, refill, gatos, debug]
---

Outcome: a scripted test, tutorial or agent can put a craft into a known orbit or give it an instantaneous velocity
change. gatOS exposes these under `/sim/debug/` (and the same actions over HTTP, MQTT and MCP), gated by `debug_namespace` in
`gatos.toml`. Prerequisites: StarMap and Harmony for the solver-window prefix (the window itself is
[mutate approach 2](../../solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-2.md)).

## Background: how the game does it

Teleport is the "physics-bypass" pattern: rather than moving a body, build a new conic from a CCI state vector about the
current parent and hand it to `Vehicle.Teleport`, which recomputes the flight plan, removes the vessel from its physics bubble
and lets the next step re-bubble it. The impulse cheat reuses it with `v + dv`, which is the same math as a separation
impulse (`Vehicle.Split`):

```csharp
// current/decomp/KSA/Orbit.cs L1611-L1617
	public static Orbit CreateFromStateCci(IParentBody parent, UniverseTime stateTime, double3 positionCci, double3 velocityCci, byte4 orbitLineColor)
	{
		double mu = parent.Mu;
		double3 b = double3.Cross(positionCci, velocityCci);
		double3 b2 = ((double3.Dot(velocityCci, velocityCci) - mu / positionCci.Length()) * positionCci - double3.Dot(positionCci, velocityCci) * velocityCci) / mu;
		double num = positionCci.Length();
		double num2 = velocityCci.LengthSquared();
```

`Teleport` computes `FlightPlan.ComputeCompleteTrajectory`, and on a failure only logs "Failed to compute trajectory for new
orbit during teleport, aborting teleport." and does not throw, so the caller cannot rely on an exception.[^ksa-vehicle]

## Lifecycle and timing

- **Solver phase** (`frame:dispatch-solvers`, a prefix on `Universe.ExecuteNextVehicleSolvers`): teleport and impulse call
  `Vehicle.Teleport`, which calls `RemoveFromCurrentBubble()`; bubble ownership lives in `VehicleUpdateTask`, whose
  `SyncWindowBubbles` throws `InvalidOperationException` unless the task is idle. The task runs from the end of one
  `PrepareFrame` until the next frame's `VehicleSolver.Wait()`, so the only safe window is after that wait and before the
  job is re-queued. Refills mutate resource state the step reads.
- **Frame phase** (warp, vessel switch): touches only main-thread globals.
- **Race to know about:** the prefix runs after the frame's `NearestOrbitPointJob` was queued (`Program.cs` L2198) and
  gatOS does not wait on `JobSystems.NearestOrbitAndPerformanceWorker`. KSA's own location teleport instead adds to
  `InputEvents.TeleportInputBuffer`, which `ApplyInputEvents` drains before that job is queued. The job reads the
  controlled vehicle's flight plan, so teleporting the controlled vessel in this prefix can race it. This is inferred
  from the decomp, not reported by gatOS; `Teleport` disposes cached orbit points via `FlightPlan.ReleaseCachedPoints`.
  Join the worker first (as [approach 3](teleport-a-vessel-by-building-a-new-orbit-3.md) does) when the target is the controlled vessel.
- Since 5348 bubble management lives in `VehicleUpdateTask` on the solver thread; a frame-phase teleport that worked before
  now races it, which is why gatOS moved teleport and impulse to the solver set.

See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Validate input before touching the game: six finite numbers `px py pz vx vy vz` in Cci, and a vessel with a parent.
2. Build the orbit at the elapsed time, call `Teleport` with null attitude and rates (so the vessel keeps its own orientation and
   spin), then refresh the caches:

```csharp
// gatOS.GameMod/Game/Ksa/Actuators/DebugActuator.cs L50-L66
    internal static CommandResult Teleport(Vehicle vehicle, IReadOnlyList<double> state)
    {
        if (state.Count != 6)
            return new CommandResult(CommandOutcome.Invalid, "teleport expects 'px py pz vx vy vz' (CCI)");
        foreach (var c in state)
            if (!double.IsFinite(c))
                return new CommandResult(CommandOutcome.Invalid, "teleport components must be finite");
        if (vehicle.Parent is not { } parent)
            return new CommandResult(CommandOutcome.Busy, "vessel has no parent body to teleport about");

        var position = new double3(state[0], state[1], state[2]);
        var velocity = new double3(state[3], state[4], state[5]);
        var orbit = Orbit.CreateFromStateCci(parent, Universe.GetElapsedTime(), position, velocity, default);
        vehicle.Teleport(orbit, null, null);
        vehicle.UpdatePerFrameData();
        return CommandResult.Ok;
    }
```

3. The impulsive-kick variant rotates the input to Cci if it was given in the body frame, divides N*s by `TotalMass` (or takes it as
   delta-v), adds to `GetVelocityCci()` and rebuilds the orbit from the current position; it works on rails and in the physics bubble alike:

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

4. Refill batteries through a mutable state reference: module state lists hand out `ref readonly` states normally, so use
   `GetModuleAndAllMutableStatesForInitialization`. `Vehicle.RefillConsumables()` refills tanks, recomputes mass and updates
   flight-computer configuration (since 5478 it also re-lights engines that flamed out):

```csharp
// gatOS.GameMod/Game/Ksa/Actuators/DebugActuator.cs L131-L141
    internal static CommandResult RefillBattery(Vehicle vehicle)
    {
        var batteries = vehicle.Parts.Batteries;
        foreach (var battery in batteries.Modules)
        {
            var mutable = batteries.GetModuleAndAllMutableStatesForInitialization(battery);
            mutable.Module.Refill(ref mutable.State);
        }

        return CommandResult.Ok;
    }
```

5. Check the outcome through telemetry: a rejected trajectory is only logged.

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Actuators/DebugActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/DebugActuator.cs#L50-L141) | Teleport, impulse, refills |
| [gatOS.GameMod/Game/Ksa/Actuators/DebugActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/DebugActuator.cs) | All debug actuators |

## Variations and gotchas

- `Universe.SetSimulationSpeed` has no auto-warp guard; while auto-warp runs, `ProcessAutoWarp` overwrites the speed each
  `PrepareFrame`, so a warp cheat is overridden.
- Velocity changes by rebuilding the orbit also work for a vessel in the bubble because `Teleport` resets the physics state from the new orbit
  (`OverwriteFromLeaderAnalytic`).
- The new orbit's colour defaults here; pass the vessel's own `Orbit.OrbitLineColor` to keep its line colour.

## See also

- [Overview and comparison](overview.md)
- [Teleport at the handoff and fire at a clicked point](teleport-a-vessel-by-building-a-new-orbit-3.md)
- Mod feature: [gatOS debug cheats: teleport, refuel, warp](../../../../mods/gatos/features/debug-cheats-teleport-refuel-warp.md)
- [Inject forces and impulses](../../solver/inject-forces-and-impulses-at-the-physics-safe-point/inject-forces-and-impulses-at-the-physics-safe-point-3.md)

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
