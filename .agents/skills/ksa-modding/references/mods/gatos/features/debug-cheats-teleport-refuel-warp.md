---
type: Mod Feature
title: Debug cheats for teleport, refuel, warp and impulse
description: A gated /sim/debug namespace that teleports vessels to a CCI state vector, applies impulsive kicks, refills propellant and batteries, sets time warp and switches the controlled vessel, each in the frame phase that keeps it race-free
mod: gatos
category: flight-and-orbits
techniques: [harmony-prefix, command-queue]
hooks: [harmony-prefix KSA.Universe.ExecuteNextVehicleSolvers, harmony-prefix KSA.GameViewport.OnFrame]
lifecycle: [frame:dispatch-solvers, frame:viewports, frame:draw-ui]
game_types:
  - KSA.Vehicle
  - KSA.Orbit
  - KSA.Universe
  - KSA.PartTree
  - KSA.Battery
  - KSA.ModuleStateful
  - KSA.Camera
  - KSA.Program
  - KSA.VehicleUpdateTask
tags: [cheats, debug, teleport, refuel, warp, impulse, orbit]
---

Scripted tests, tutorials and agents need to set up scenarios: put a craft into a known orbit,
top up its tanks, jump time, take control of another vessel. gatOS exposes these under
`/sim/debug/` (and the same actions over HTTP, MQTT and MCP), gated by `debug_namespace` in
`gatos.toml`. The actuators are short and show the public KSA calls for each cheat, plus which ones
must run in the solver window.

## How it works

| Action | KSA calls | Phase |
|---|---|---|
| `debug.teleport px py pz vx vy vz` | `Orbit.CreateFromStateCci(parent, Universe.GetElapsedTime(), pos, vel, default)`, `vehicle.Teleport(orbit, null, null)`, `vehicle.UpdatePerFrameData()` | Solver |
| `debug.impulse x y z [frame] [unit]` | rotate body-frame input by `GetBody2Cci()`, divide N·s by `TotalMass`, add to `GetVelocityCci()`, then the same orbit rebuild and `Teleport` | Solver |
| `debug.refill_fuel` | `Vehicle.RefillConsumables()` (refills tanks, recomputes mass, updates FC configuration) | Solver |
| `debug.refill_battery` | `Parts.Batteries.GetModuleAndAllMutableStatesForInitialization(b)` then `Battery.Refill(ref state)` | Solver |
| `debug.warp f` | `Universe.SetSimulationSpeed(f, alert: false)` | Frame |
| `debug.control_vessel` | `Program.GetMainCamera().SetFollow(target, tidalLocking: true, changeControl: false)`; `Program.ControlledVehicle = target` | Frame |

Teleport is the "physics-bypass" pattern: rather than moving a body, build a new conic from a CCI
state vector about the current parent and hand it to `Vehicle.Teleport`, which recomputes the
flight plan, removes the vessel from its physics bubble and lets the next step re-bubble it
([gatOS.GameMod/Game/Ksa/Actuators/DebugActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/DebugActuator.cs#L50-L66)). The impulse cheat reuses it with
`v + dv`, which is the same math as a separation impulse ([gatOS.GameMod/Game/Ksa/Actuators/DebugActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/DebugActuator.cs#L79-L111)).

Refilling batteries needs a **mutable** state reference: module state lists hand out
`ref readonly` states normally, so gatOS uses
`GetModuleAndAllMutableStatesForInitialization`, the pattern from the unscience "eternal-flame"
sub-mod ([gatOS.GameMod/Game/Ksa/Actuators/DebugActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/DebugActuator.cs#L131-L141)).

## Lifecycle and timing

- **Solver phase** (`frame:dispatch-solvers`, prefix on `Universe.ExecuteNextVehicleSolvers`): teleport and
  impulse call `Vehicle.Teleport`, which calls `RemoveFromCurrentBubble()`; bubble ownership lives
  in `VehicleUpdateTask`, whose `SyncWindowBubbles` throws `InvalidOperationException` unless the
  task is idle ([current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L158-L167)). The task runs from the
  end of one `PrepareFrame` until the next frame's `VehicleSolver.Wait()`, so the only safe window
  is after that wait and before the job is re-queued. Refills mutate resource state the step reads.
  See [Solver-phase drain for flight computer setpoints](solver-phase-command-drain.md).
- **Frame phase**: warp and vessel switch only touch main-thread globals.
- Gating: with `debug_namespace=false` `SimFsTree` does not build `/sim/debug`, and the HTTP and
  MQTT command paths refuse `debug.*` actions when `ICommandSink.DebugEnabled` is false
  ([gatOS.Http/SimHttpServer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Http/SimHttpServer.cs#L733-L735)).

## Key code

| Path | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Actuators/DebugActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/DebugActuator.cs) | all cheat actuators |
| [gatOS.SimFs/Commands/ImpulseRules.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/ImpulseRules.cs) | game-free validation of impulse vector, frame and unit keywords |
| [gatOS.SimFs/Commands/SimCommand.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/SimCommand.cs#L74-L84) | which debug actions are Solver phase |

## Snippets

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

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Orbit.CreateFromStateCci` | [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1611-L1611) | conic from a CCI state vector |
| `Vehicle.Teleport(Orbit?, doubleQuat?, double3?)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2241-L2270) | apply new orbit, leave bubble |
| `Vehicle.UpdatePerFrameData()` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2645-L2652) | refresh CCE/ECL caches immediately |
| `Vehicle.RefillConsumables()` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3201-L3206) | refuel |
| `Battery.Refill(ref BatteryState)` | [current/decomp/KSA/Battery.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Battery.cs#L63-L63) | battery refill |
| `ModuleStateful<...>.StateList.GetModuleAndAllMutableStatesForInitialization` | [current/decomp/KSA/ModuleStateful.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleStateful.cs#L479-L482) | mutable state access |
| `Universe.SetSimulationSpeed(double, bool)`, `GetElapsedTime()` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2288-L2399) | warp, current `UniverseTime` |
| `Camera.SetFollow`, `Program.ControlledVehicle` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L602-L602) | switch vessel |
| `VehicleUpdateTask.SyncWindowBubbles` | [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L158-L167) | evidence for the solver window |

## Pitfalls and version notes

- `Vehicle.Teleport` aborts (logs an error) when the new trajectory cannot be computed; it does not
  throw, so check the result through telemetry.
- Since 5348 (revs 5331/5339) bubble management moved into `VehicleUpdateTask` on the solver
  thread; a Frame-phase teleport that worked before then now races it (gatOS moved teleport and
  impulse to the Solver set for that reason).
- Since 5478 refilling fuel also re-lights engines that flamed out (gatOS notes); SRB grain is
  refilled for free.
- `Universe.SetSimulationSpeed(double, bool)`, which gatOS calls, has no auto-warp guard; the
  "cannot set simulation speed while auto warp is active" refusal lives only in the console command
  `SetSimulationSpeedDirect`. While auto-warp runs, `Universe.ProcessAutoWarp` sets the speed again in
  every `PrepareFrame` before `GetJobSimStep`, so a `debug.warp` write is overridden before a step
  uses it ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2208-L2236)).
- Teleport and impulse run after the frame's `NearestOrbitPointJob` was queued and do not wait on
  `JobSystems.NearestOrbitAndPerformanceWorker`; see the pitfall in
  [Solver-phase drain for flight computer setpoints](solver-phase-command-drain.md).
- Taking control hands `ClearHeldPlayerInput()` to the previous vehicle through the
  `Program.ControlledVehicle` setter.

## See also

- [Solver-phase drain for flight computer setpoints](solver-phase-command-drain.md)
- [Game-thread command queue](game-thread-command-queue.md)
- [Welds that pin one vessel to another](welds-vessel-anchoring.md)
- [Per-vessel scale and force-render cheats](scale-and-force-render-cheats.md)
- [gatOS overview](../overview.md)
- [Set the speed with Universe.SetSimulationSpeed and suppress the on-screen alert](../../../howto/flight-and-orbits/flight-state/set-simulation-speed-from-a-mod/set-simulation-speed-from-a-mod-1.md) - how-to article demonstrating this feature
- [Teleport to CCI state vectors in the solver-dispatch window](../../../howto/physics/kinematics/teleport-a-vessel-by-building-a-new-orbit/teleport-a-vessel-by-building-a-new-orbit-1.md) - how-to article demonstrating this feature
- [Impulsive kick from the solver-dispatch window](../../../howto/physics/solver/inject-forces-and-impulses-at-the-physics-safe-point/inject-forces-and-impulses-at-the-physics-safe-point-3.md) - how-to article demonstrating this feature
- [Refuel on demand from a command queue drained in the solver window](../../../howto/vehicles-and-parts/modules-and-resources/refill-or-drain-resources/refill-or-drain-resources-2.md) - how-to article demonstrating this feature

