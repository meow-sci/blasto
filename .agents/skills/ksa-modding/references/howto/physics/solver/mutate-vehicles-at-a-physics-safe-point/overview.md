---
type: Game Feature
title: Mutate vehicles at a physics-safe point
description: Why vehicle state written from a StarMap hook or ImGui button is lost, and the four windows where a write survives
category: physics
group: solver
topic: mutate-vehicles-at-a-physics-safe-point
demonstrated_by:
  - mods/ksacars/features/prepareworker-impulse-injection
  - mods/ksarmory/features/prepareworker-prefix-command-window
  - mods/ksastructures/features/lift-moving-landmark
  - mods/gatos/features/solver-phase-command-drain
  - mods/ksagolf/features/launch-site-surface-raycast
  - mods/unscience/features/kiwis-marbles-celestial-welding
  - mods/unscience/features/lib-physics-frame-hook
  - mods/unscience/features/garrys-torch-vehicle-welding
  - mods/unscience/features/dent-wizard-vessel-launcher
  - mods/unscience/features/godzilla-vessel-scaling
  - mods/gatos/features/welds-vessel-anchoring
  - mods/ksarmory/features/safe-vehicle-destruction-and-part-failure
lifecycle: [frame:prepare, frame:apply-solvers, frame:dispatch-solvers, frame:draw-ui]
game_types: [KSA.Vehicle, KSA.Universe, KSA.Program, KSA.JobSystems, KSA.VehicleUpdateTask, KSA.SimStep]
tags: [physics, vehicle, physics-step, prepareworker, solver, harmony, safe-point, threading]
---

A mod button or a StarMap hook runs while the next physics step is already in flight on a worker thread.
Anything it writes to a vehicle's velocity, attitude, flight computer or part tree is either never seen by
that step or is overwritten when the step is applied. This topic compares the four places a mod can write
and still have the change integrated: a `Vehicle.PrepareWorker` prefix, a prefix on
`Universe.ExecuteNextVehicleSolvers`, a transpiler seam on the `GetJobSimStep` call in `PrepareFrame`, and
an explicit `JobSystems.VehicleSolver.Wait()` from a frame hook.

The general write-path table, the timing proofs and the full frame order live in the
[game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and its
[settled-window approach](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md); this topic applies them to physics-state
writes and does not repeat them.

## Engine background

Each frame, `Program.PrepareFrame` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141-L2236) joins the four schedulers
(`JobSystems.VehicleSolver.Wait()` among them), applies the finished step (`Universe.ApplyVehicleSolvers`
overwrites each vehicle's kinematic state and copies its flight computer back), polls input and runs
`InputEvents.ApplyInputEvents`, then computes the next `SimStep` and dispatches it. The ordering that matters
for a mod:

| Moment | Call | State |
|---|---|---|
| Wait and apply | `VehicleSolver.Wait()`, `Universe.ApplyVehicleSolvers` | Main-thread vehicle state holds the finished step; vehicle, orbit and cloth workers idle (the nearest-orbit job is queued a few lines later and is still running at the handoff and at dispatch) |
| Handoff | `Universe.GetJobSimStep` | Last point before cloth is dispatched and part trees are flushed |
| Dispatch | `Universe.ExecuteNextVehicleSolvers` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2139) | `PrepareVehicleWorkers` calls `Vehicle.PrepareWorker(SimStep)` per vehicle, then queues one `VehicleUpdateTask` |
| Snapshot | end of `Vehicle.PrepareWorker` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423) | `PrepareFromVehicle` copies origin, kinematics, flight computer and manual inputs for the worker |
| In flight | `StarMapBeforeGui`, `StarMapAfterGui`, `StarMapAfterOnFrame` | The worker owns the simulation; the next apply overwrites kinematics |

`Vehicle.GetPhysicsStatesMutable` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L922-L931) returns a `PhysicsStates` whose fields alias
the vehicle's own `_bubbleOrigin`, `_kinematicStates`, `_props` and `_environment`, so assigning through it
writes the live vehicle. That is only useful in a window where the next snapshot has not been taken yet.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: Write inside a `Vehicle.PrepareWorker` prefix](mutate-vehicles-at-a-physics-safe-point-1.md) | KSACars, KSArmory, KSAStructures | harmony-prefix | `frame:dispatch-solvers` | Per vehicle, right before that vehicle's snapshot; runs once per frame for every vehicle so the filter must be cheap |
| [2: Drain a command queue in a `Priority.First` prefix on `ExecuteNextVehicleSolvers`](mutate-vehicles-at-a-physics-safe-point-2.md) | gatOS (and KSAGolf, unscience) | harmony-prefix | `frame:dispatch-solvers` | Once per frame, all vehicles; after cloth dispatch and part-tree flushes; the shapes registry is idle |
| [3: Run a callback at the `PrepareFrame` handoff](mutate-vehicles-at-a-physics-safe-point-3.md) | unscience | harmony-transpiler | `frame:prepare` | Earliest window (before the part-tree flushes) and survives inlining; needs a strict IL shape check |
| [4: Join `JobSystems.VehicleSolver` before mutating](mutate-vehicles-at-a-physics-safe-point-4.md) | KSArmory | reflection | `frame:draw-ui` | Works from a UI hook for structural edits, but stalls the main thread and does not make ordinary state writes stick |

Pick by what you change:

| Change | Window |
|---|---|
| Velocity, spin, position, attitude of one craft; flight computer fields; tank contents; mass | 1 (or 2/3 when you can do it for all vehicles at once) |
| Orbit swaps for celestials, refills, reading the Bepu shape registry | 2 |
| Teleports of the controlled vessel (window 2 runs after the nearest-orbit job was queued and can race it, inferred from the decomp; join `JobSystems.NearestOrbitAndPerformanceWorker` first) | 3, or 2 plus the join |
| Rescales, welds, part-tree edits that dirty derived data (and teleports) | 3 |
| Destroy or remove a vehicle, a teardown that cannot wait | 4 |

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [mutating simulation state safely](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md)
- [Run code inside the physics step on worker threads](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md)
- [Write worker-thread-safe patches inside the vehicle solver](../../solver/write-worker-thread-safe-solver-patches/overview.md): code that runs inside the step instead of before it
- [Inject forces, impulses and velocity changes at the physics safe point](../../solver/inject-forces-and-impulses-at-the-physics-safe-point/overview.md): what to write in these windows
- [Weld or anchor one vessel to another](../../kinematics/weld-one-vessel-to-another/overview.md) and [teleport a vessel by building a new orbit](../../kinematics/teleport-a-vessel-by-building-a-new-orbit/overview.md): the two kinematic edits that most often need a window
- [Write worker-thread-safe patches inside the vehicle solver](../write-worker-thread-safe-solver-patches/overview.md) - related topic
- [Inject forces, impulses and velocity changes at the physics safe point](../inject-forces-and-impulses-at-the-physics-safe-point/overview.md) - related topic

