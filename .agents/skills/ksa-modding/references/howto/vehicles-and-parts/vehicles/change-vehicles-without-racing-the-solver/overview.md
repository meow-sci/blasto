---
type: Game Feature
title: Changing vehicles without racing the vehicle solver
description: Why building, destroying or editing a vehicle from a mod hook can race KSA's physics worker, and seven recipes (lock, wait, stage, or write in the one window the worker does not overwrite)
category: vehicles-and-parts
group: vehicles
topic: change-vehicles-without-racing-the-solver
demonstrated_by:
  - mods/ksacars/features/spawn-parked-craft
  - mods/ksarmory/features/spawn-and-teleport-test-craft
  - mods/unscience/features/doh-kitten-spawning
  - mods/gatos/features/welds-vessel-anchoring
  - mods/ksarmory/features/safe-vehicle-destruction-and-part-failure
  - mods/unscience/features/eternal-flame-resource-refill
  - mods/ksarmory/features/per-part-store-mass
  - mods/ksarmory/features/prepareworker-prefix-command-window
  - mods/ksarmory/features/holed-tank-leaks
lifecycle:
  - frame:apply-solvers
  - frame:after-on-frame
  - frame:dispatch-solvers
  - physics:vehicle-solver
  - frame:draw-ui
game_types:
  - KSA.ConstraintSim
  - KSA.JobSystems
  - KSA.Vehicle
  - KSA.PhysicsBubble
  - KSA.InputEvents
  - KSA.PartFailureEvent
  - KSA.PartFailure
  - KSA.Universe
  - KSA.PartTree
  - KSA.MoleState
  - KSA.FlightComputer
  - KSA.Part
  - KSA.VehicleUpdateTask
  - KSA.VehicleUpdateState
tags: [threading, physics, shapes-lock, job-system, prepareworker, destruction, mass, tanks, spawn]
---
KSA's vehicle physics runs on worker threads. `Program.PrepareFrame` collects the last step, applies
it, and dispatches the next one before any StarMap hook runs; from then until the next frame's wait,
the main thread shares vehicle state with a running worker. A mod that builds a vehicle, disposes one,
or writes a field the worker also writes will either throw inside the engine or have its write erased
a frame later. This topic is the cookbook for each failure mode. The frame order that makes it
necessary is in [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Engine background

- **Frame order.** `PrepareFrame` calls `JobSystems.VehicleSolver.Wait()` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2158)[^ksa-program], then
  `Universe.ApplyVehicleSolvers()` (L2167), `InputEvents.ApplyInputEvents()` (L2181), and finally
  `Universe.ExecuteNextVehicleSolvers` (L2220); StarMap's frame hooks fire after that, so they always
  run with a worker in flight.
- **Snapshot.** `ExecuteNextVehicleSolvers` calls `PrepareVehicleWorkers`, which calls
  `Vehicle.PrepareWorker(SimStep)` on every vehicle and copies manual inputs, kinematics and the
  flight computer into the worker's `VehicleUpdateState` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2128-L2160)[^ksa-universe]
  [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2422)[^ksa-vehicle].
- **Copy-on-write results.** Properties and module states are copied lazily: the first worker write calls
  `GetNewProps()` or `GetNewStates()` [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L183-L193)[^ksa-vehicleupdatestate], and the next
  `ApplyVehicleSolvers` writes them back over the live objects
  [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2435-L2450) [current/decomp/KSA/ModuleStateful.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleStateful.cs#L383-L413)[^ksa-modulestateful].
- **The shapes lock.** `VehicleUpdateTask.Run` holds the Bepu shape registry from `BeginVehicleUpdate` to
  `EndVehicleUpdate` [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L261-L298)[^ksa-vehicleupdatetask]; `UnlockShapes` throws while it is held,
  `UnlockShapesBlocking` waits [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L111-L153)[^ksa-constraintsim].
- **Stock staging.** KSA's own UI queues work in `InputEvents` buffers (`CreateVehicleBuffer`,
  `EvaSpawnBuffer`, `VehicleDestroyBuffer`, `TeleportInputBuffer`, ...) that `ApplyInputEvents` drains in
  the settled window [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1113-L1140)[^ksa-inputevents].

## Approaches

| Article | Mod | Technique | Use when |
|---|---|---|---|
| [1: hold the shapes lock around the build](change-vehicles-without-racing-the-solver-1.md) | KSACars, KSArmory | `shapes-registry-lock` | You build or finish a vehicle (shapes change) from a frame hook |
| [2: wait on `JobSystems.VehicleSolver`](change-vehicles-without-racing-the-solver-2.md) | unscience, gatOS | `job-barrier` | You mutate or teleport something the worker enumerates, or construct a kitten, once or per frame |
| [3: safe destruction](change-vehicles-without-racing-the-solver-3.md) | KSArmory | `job-barrier` | You kill, remove or break a craft (queue `PartFailureEvent`, never split inline) |
| [4: prefix `Universe.ExecuteNextVehicleSolvers`](change-vehicles-without-racing-the-solver-4.md) | unscience | `harmony-prefix` | A periodic write that must land before the snapshot, for every vehicle at once |
| [5: prefix `Vehicle.PrepareWorker`](change-vehicles-without-racing-the-solver-5.md) | KSArmory | `prepareworker-window` | A per-vehicle write to the flight computer, kinematics or manual inputs |
| [6: mole states](change-vehicles-without-racing-the-solver-6.md) | KSArmory | `module-state-write` | Changing tank contents so `UpdateBulkStates` does not overwrite them |
| [7: part mass](change-vehicles-without-racing-the-solver-7.md) | KSArmory | `unsafe-ref-write` | Changing a part's inert mass and the craft's mass properties |

Rule of thumb: structure changes (create, dispose, split) need the lock or a wait; field writes need the
window between apply and snapshot; results that the worker computed for the old shape must never land on
the new one.

## See also

- [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md), [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md), [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md)
- [Spawn a vehicle from code (from the vehicle library or a hand-built part)](../spawn-a-vehicle-at-runtime/overview.md), [Make a vehicle survive crashes and g-loads, or damage parts against crash tolerance](../make-a-vehicle-indestructible/overview.md), [Refill, drain or top up a vehicle's propellant and batteries](../../modules-and-resources/refill-or-drain-resources/overview.md), [Take control of another vehicle: switch the controlled craft or autopilot it from a mod](../take-control-of-another-vehicle/overview.md)
- Mod features: [ksacars/spawn-parked-craft](../../../../mods/ksacars/features/spawn-parked-craft.md), [ksarmory/safe-vehicle-destruction-and-part-failure](../../../../mods/ksarmory/features/safe-vehicle-destruction-and-part-failure.md), [unscience/eternal-flame-resource-refill](../../../../mods/unscience/features/eternal-flame-resource-refill.md), [ksarmory/prepareworker-prefix-command-window](../../../../mods/ksarmory/features/prepareworker-prefix-command-window.md), [ksarmory/per-part-store-mass](../../../../mods/ksarmory/features/per-part-store-mass.md), [ksarmory/holed-tank-leaks](../../../../mods/ksarmory/features/holed-tank-leaks.md)

[^ksa-vehicleupdatetask]: current/decomp/KSA/VehicleUpdateTask.cs (decompiled, build 2026.10.10.5554)
[^ksa-universe]: current/decomp/KSA/Universe.cs (decompiled, build 2026.10.10.5554)
[^ksa-inputevents]: current/decomp/KSA/InputEvents.cs (decompiled, build 2026.10.10.5554)
[^ksa-program]: current/decomp/KSA/Program.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatestate]: current/decomp/KSA/VehicleUpdateState.cs (decompiled, build 2026.10.10.5554)
[^ksa-constraintsim]: current/decomp/KSA/ConstraintSim.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^ksa-modulestateful]: current/decomp/KSA/ModuleStateful.cs (decompiled, build 2026.10.10.5554)
