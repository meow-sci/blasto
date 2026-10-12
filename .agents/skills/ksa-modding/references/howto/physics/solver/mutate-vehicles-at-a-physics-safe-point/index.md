# Game Feature

* [Mutate vehicles at a physics-safe point](overview.md) - Why vehicle state written from a StarMap hook or ImGui button is lost, and the four windows where a write survives

# How-To

* [Drain a command queue in a Priority.First prefix on Universe.ExecuteNextVehicleSolvers](mutate-vehicles-at-a-physics-safe-point-2.md) - Run flight-computer writes, refills, teleports, orbit swaps and shape-registry reads once per frame in the window after the apply calls and before the vehicle step is queued
* [Join JobSystems.VehicleSolver before mutating from a frame hook](mutate-vehicles-at-a-physics-safe-point-4.md) - Wait on the vehicle solver scheduler before destroying a vehicle, removing it or teleporting it from a StarMap hook, and queue part failures through the engine's own event instead of splitting directly
* [Run a callback at the PrepareFrame handoff with a GetJobSimStep transpiler](mutate-vehicles-at-a-physics-safe-point-3.md) - Rewrite the single Universe.GetJobSimStep call in Program.PrepareFrame so one-shot edits and a per-frame BeforePhysics event run after the apply calls and before cloth, part-tree flushes and the vehicle snapshot
* [Write inside a Vehicle.PrepareWorker prefix](mutate-vehicles-at-a-physics-safe-point-1.md) - Queue a vehicle change from any hook and perform it in a Harmony prefix on Vehicle.PrepareWorker, the last main-thread moment before KSA snapshots that vehicle for the physics worker
