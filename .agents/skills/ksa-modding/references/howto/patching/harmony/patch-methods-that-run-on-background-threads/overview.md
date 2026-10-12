---
type: Game Feature
title: Patching game methods that run on worker and pool threads
description: Which KSA methods run on vehicle workers, Bepu threads and the thread pool, what a Harmony patch may touch there, and how results and data cross between those threads and the main thread
category: patching
group: harmony
topic: patch-methods-that-run-on-background-threads
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/hyperbolic-transfer-targets
  - mods/realatmospheres/features/shader-file-redirect
  - mods/ksa-advancedflightcomputer/features/flight-computer-command-sink
  - mods/ksacars/features/keep-thrusting-craft-off-rails
  - mods/ksa-advancedflightcomputer/features/flight-computer-worker-copy-channels
  - mods/compendium/features/flight-computer-root-attitude-tracking
lifecycle:
  - thread:background
  - physics:vehicle-solver
  - startup:starmap-before-main
  - frame:apply-solvers
  - frame:dispatch-solvers
game_types:
  - KSA.TransferPlanner
  - KSA.OrbitalTransfers
  - KSA.RefineBurnTask
  - KSA.PatchedConic
  - KSA.VehicleUpdateTask
  - KSA.PhysicsStates
  - KSA.FlightComputer
  - KSA.Vehicle
  - KSA.BurnPlan
  - KSA.VehicleConfigInfo
  - KSA.VehicleUpdateState
  - KSA.Universe
tags: [harmony, threads, worker-thread, thread-pool, thread-safety, vehicle-solver]
---

A Harmony patch body runs on whatever thread the game called the patched method from. Most of KSA's per-frame code is on the
main thread, but a lot of what mods want to change is not: vehicle workers step physics per bubble, Bepu fans out
its own threads, the Transfer Planner searches on thread-pool threads, and shaders compile in parallel at load. This topic
states which thread each target uses, what a patch may touch there (no ImGui, no Vulkan, no main-thread-only
collections), and the patterns the KSA mods use to get data in and results out.

## Engine background

| Where it runs | Examples (build 2026.10.10.5554) | Phase |
|---|---|---|
| Vehicle workers (`JobSystems.VehicleSolver`) | `FlightComputer.ComputeControl`, `PhysicsBubble.KittenWantsWake`, `PhysicsStates.ComputeDerivatives` | `physics:vehicle-solver` |
| Bepu worker dispatch inside a bubble | `ConstraintSim.Simulate` collision passes | `physics:bepu` |
| Thread pool | `TransferTask.Run` and its porkchop callbacks, `RefineBurnTask.Run` ([current/decomp/KSA/TransferTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferTask.cs#L160-L200)[^ksa-transfertask], [current/decomp/KSA/RefineBurnTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RefineBurnTask.cs#L20-L39)[^ksa-refineburntask]) | `thread:background` |
| Parallel loaders at load | shader compiles via `ModLibrary.LoadAll` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L507-L519)[^ksa-modlibrary]) | `startup:asset-load` |
| Main thread | everything in `Program.OnFrame`, `Universe.Apply*Solvers`, `ExecuteNext*Solvers`, UI | `frame:*` |

The vehicle solver round trip is the one that matters most. `Vehicle.PrepareWorker` copies the vehicle's state, including the
whole `FlightComputer`, into the object the worker will use ([current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L296-L299)[^ksa-vehicleupdatestate]). The worker flies
that copy. At the next `Universe.ApplyVehicleSolvers` the copy is written back over the original
([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2525-L2531)[^ksa-vehicle]). A main-thread write made between those two moments is overwritten. The details of the
window are in [Mutate simulation state safely relative to the physics step](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md); the worker side is in [Run code inside the physics step on worker threads](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md); the whole frame is in [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Rules the five approaches share

1. **Know the thread before you write the patch.** Grep the decompile for every caller and every `ThreadPool`, `Task.Run`,
   `Parallel.` and job queue in the chain. Include callers *you* create.
2. **A patch may only touch what is safe on that thread.** On a worker: the arguments the game passed, immutable objects,
   thread-safe collections, `[ThreadStatic]` state, `Volatile` reads. Not ImGui, not Vulkan, not unsynchronized lists
   (`TimedAlert.Create` mutates one).
3. **Publish before dispatch, read during the step.** The main thread writes into a concurrent store or swaps an
   immutable object by reference *before* `ExecuteNextVehicleSolvers` queues the job; the worker reads. The publish
   happens before the read.
4. **Key by what the copy shares.** `CopyFrom` assigns `BurnPlan`, `VehicleConfig` and `Burn` by reference; the worker's
   `FlightComputer` is not the vehicle's.
5. **Never throw out of a worker patch.** An exception on a worker aborts the bubble's step or is rethrown on a pool thread.
   Log each fault type once.
6. **Return results through the objects the game already passes.** Pooled results travel in the `TransferInfo` or
   `PorkChopEntry`; UI effects go in a finalizer or hook on the draw thread.

## Approaches

| # | Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [Patches on planner tasks that run on pooled threads](patch-methods-that-run-on-background-threads-1.md) | KSA-AdvancedFlightComputer | Prefixes on planner tasks on pool threads; alert from a draw-thread finalizer | `thread:background`, `frame:draw-ui` | Replace work on a pool thread with thread-safe code only |
| 2 | [Preload in StarMapBeforeMain because shaders compile in parallel on pool threads](patch-methods-that-run-on-background-threads-2.md) | Real Atmospheres | Install in `StarMapBeforeMain`, preload the type, thread-safe prefix on parallel shader compile | `startup:starmap-before-main`, `startup:asset-load` | Only hook before shader loading; a throw aborts the launch unless caught |
| 3 | [Worker-side postfix merging commands from the main thread](patch-methods-that-run-on-background-threads-3.md) | KSACars, KSA-AdvancedFlightComputer | Worker-side postfix answering from a concurrent store written before dispatch | `frame:dispatch-solvers`, `physics:vehicle-solver` | Postfix runs many times per step; must be cheap and non-throwing |
| 4 | [ConditionalWeakTable keyed by objects the copy shares by reference](patch-methods-that-run-on-background-threads-4.md) | KSA-AdvancedFlightComputer | `ConditionalWeakTable` keyed by `BurnPlan` or `VehicleConfigInfo` | `frame:apply-solvers`, `physics:vehicle-solver` | Key must be an object the copy shares and the game does not replace |
| 5 | [Look the state up through __instance.BurnPlan in a ComputeControl prefix](patch-methods-that-run-on-background-threads-5.md) | Compendium | Look the state up through `__instance.BurnPlan` in a `ComputeControl` prefix | `physics:vehicle-solver` | Silent fallback if the sharing assumption breaks |

## See also

- [Harmony finalizers, cleanup and scoped overrides](../run-cleanup-or-recover-from-exceptions-with-a-finalizer/overview.md) for the finalizer used by the draw-thread alert and by the worker-side injection
- [Coexisting with other mods that patch the same method](../coexist-with-other-mods-patching-the-same-method/overview.md) for the single `ComputeControl` postfix
- Related tasks and topics: write-worker-thread-safe-solver-patches, run-an-expensive-solve-without-stalling-the-frame, how-starmap-launches-ksa, drive-the-flight-computer-and-attitude-modes-from-a-mod, mutate-vehicles-at-a-physics-safe-point
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Write worker-thread-safe patches inside the vehicle solver](../../../physics/solver/write-worker-thread-safe-solver-patches/overview.md) - related topic
- [Run an expensive solve without stalling the frame](../../../flight-and-orbits/guidance/run-an-expensive-solve-without-stalling-the-frame/overview.md) - related topic
- [How StarMap launches KSA and loads your mod](../../../getting-started/loader/how-starmap-launches-ksa/overview.md) - related topic
- [Drive the flight computer and attitude modes from a mod](../../../flight-and-orbits/autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/overview.md) - related topic
- [Mutate vehicles at a physics-safe point](../../../physics/solver/mutate-vehicles-at-a-physics-safe-point/overview.md) - related topic

[^ksa-transfertask]: KSA.TransferTask (decompiled, build 2026.10.10.5554)
[^ksa-refineburntask]: KSA.RefineBurnTask (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatestate]: KSA.VehicleUpdateState (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
