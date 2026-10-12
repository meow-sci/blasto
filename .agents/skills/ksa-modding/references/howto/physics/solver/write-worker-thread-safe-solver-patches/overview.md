---
type: Game Feature
title: Write worker-thread-safe patches inside the vehicle solver
description: How to share data with Harmony patches that run on vehicle worker threads per substep, without locks, allocation or exceptions
category: physics
group: solver
topic: write-worker-thread-safe-solver-patches
demonstrated_by:
  - mods/ksaero/features/body-of-revolution-aerodynamics
  - mods/ksaero/features/stock-drag-delegate-fallback
  - mods/realatmospheres/features/measured-flight-atmospheres
  - mods/ksastructures/features/dry-volume-below-sea-level
  - mods/unscience/features/kitchen-sink-g-load-protection
  - mods/unscience/features/the-tick-vessel-indestructibility
  - mods/ksagolf/features/course-colliders-as-clutter
  - mods/ksastructures/features/first-person-kitten-view
  - mods/ksastructures/features/terrain-height-roof-override
  - mods/ksa-advancedflightcomputer/features/flight-computer-command-sink
  - mods/ksa-advancedflightcomputer/features/flight-computer-worker-copy-channels
lifecycle:
  - physics:vehicle-solver
  - physics:bepu
  - frame:dispatch-solvers
  - startup:asset-load
  - frame:draw-ui
  - frame:viewports
  - frame:celestials
  - frame:apply-solvers
game_types:
  - KSA.PhysicsStates
  - KSA.PhysicsBubble
  - KSA.VehicleUpdateTask
  - KSA.PhysicsEnvironment
  - KSA.ConstraintSim
  - KSA.KittenLocomotion
  - KSA.FlightComputer
tags: [physics, worker-thread, threading, harmony, solver, substep, concurrency, allocation-free]
---

Methods such as `PhysicsStates.ComputeDerivatives`, `PhysicsEnvironment.RecomputePositionalValues`,
`FlightComputer.ComputeControl`, `ConstraintSim.DetectCollisions`/`Simulate` and `KittenLocomotion.StepGrounded`
run on the vehicle solver's worker threads, per substep or per collision pass, for several vehicles at once.
A Harmony patch on any of them inherits those constraints: no ImGui or Vulkan, no main-thread-only state, no
blocking, no allocation on the hot path, and no uncaught exception. This topic collects six ways to feed such a
patch mod data that the main thread owns.

The call chain and the thread table are in the lifecycle reference's
[worker-thread approach](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md) and the
[physics step section](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#the-physics-step); this topic applies them and does not repeat them.

## Engine background

| Type | Role for a patch |
|---|---|
| `Universe.ExecuteNextVehicleSolvers` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2139) | Main thread: calls `Vehicle.PrepareWorker` per vehicle, then queues one `VehicleUpdateTask` on `JobSystems.VehicleSolver` |
| `VehicleUpdateTask.Run` [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L261-L298) | `VehicleSolver` runner: trims and merges bubbles, runs contact islands as a `ParallelBatch` on `JobSystems.VehicleWorkerPool` |
| `PhysicsBubble` [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L688-L714) | Steps one bubble: `StepFrameTo`, substeps, `FullPhysicsConstrainedStep` or `FullPhysicsUnconstrainedStep` |
| `PhysicsStates.ComputeDerivatives` [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L956-L1087) | The force model; once or twice per substep per vehicle, 12 times per RK8 step |
| `ConstraintSim` [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L870-L904) | One per bubble; `DetectCollisions` and `Simulate` run Bepu on a dispatcher over the worker pool |
| `IBubbleJob` [current/decomp/KSA/IBubbleJob.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IBubbleJob.cs) | Wraps each bubble job in a try/catch that calls `PhysicsBubble.MarkFailed` |

What a throwing patch costs: `MarkFailed` logs `Update task failed for vehicle(s)` as an error and marks the
bubble failed, `PublishResults` is skipped, and the bubble's vehicles do not advance that frame. The error
repeats every frame the throw does, and the player sees frozen vehicles instead of a crash.

## Approaches

| Article | Mod | Technique | Trade-off |
|---|---|---|---|
| [1: Pure, allocation-free, never-throwing code over the `in` arguments](write-worker-thread-safe-solver-patches-1.md) | KSAero | harmony-transpiler | No shared state at all; the model must fit in the call's arguments |
| [2: Read-only tables filled before physics starts](write-worker-thread-safe-solver-patches-2.md) | RealAtmospheres, KSAStructures | reflection | Lock-free reads; data fixed at load, or swapped whole through a `volatile` field |
| [3: Concurrent registries keyed by reference](write-worker-thread-safe-solver-patches-3.md) | unscience | reflection | UI can edit while workers read; needs identity keys, a disposed check and a main-thread prune |
| [4: Volatile snapshot handoff from worker patches to the main thread](write-worker-thread-safe-solver-patches-4.md) | KSAGolf, KSAStructures | worker-thread-patch | Immutable snapshot in, volatile fields out; one simulation per call, no shared mutable state |
| [5: ThreadStatic "who is asking" set in a prefix, cleared in a finalizer](write-worker-thread-safe-solver-patches-5.md) | KSAStructures | harmony-finalizer | Tells vehicle-driven calls from camera and UI calls; relies on the window being bracketed correctly |
| [6: Immutable commands published before the solver job is queued](write-worker-thread-safe-solver-patches-6.md) | KSA-AdvancedFlightComputer | harmony-postfix | One postfix runs every actuator writer; commands are keyed by an object the worker's flight-computer copy shares |

Pick by the direction of data flow:

| You need | Use |
|---|---|
| Nothing from the mod (replace a formula) | 1 |
| Static or rarely changing data (tables, volumes) | 2 |
| A set of vessels the player edits live | 3 |
| Per-frame values in (a heading, a course) or results out (a hit, a handle list) | 4 |
| Per-vehicle context for a function that does not receive the vehicle | 5 |
| A different command for each vehicle every control step | 6 |

## See also

- [Run code inside the physics step on worker threads](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md)
- [Mutate vehicles at a physics-safe point](../../solver/mutate-vehicles-at-a-physics-safe-point/overview.md): the windows in which main-thread state is settled
- [Mutate the shared Bepu shape registry](../../colliders/mutate-the-shared-bepu-shape-registry/overview.md) and [add static colliders to physics bubbles](../../colliders/add-static-colliders-to-physics-bubbles/overview.md)
- The topic replace-the-stock-aerodynamic-force-model (another batch) covers what a force replacement should compute; this topic covers how to run it safely
- [Mutate vehicles at a physics-safe point](../mutate-vehicles-at-a-physics-safe-point/overview.md) - related topic
- [Replace the stock aerodynamic force model and measure the active one](../../aero-and-ground/replace-the-stock-aerodynamic-force-model/overview.md) - related topic

