# Game Feature

* [Patching game methods that run on worker and pool threads](overview.md) - Which KSA methods run on vehicle workers, Bepu threads and the thread pool, what a Harmony patch may touch there, and how results and data cross between those threads and the main thread

# How-To

* [ConditionalWeakTable keyed by objects the copy shares by reference](patch-methods-that-run-on-background-threads-4.md) - The vehicle worker flies a copy of the flight computer, so a worker-side lookup keyed by the vehicle's own FlightComputer never matches; key it by the BurnPlan or VehicleConfigInfo that CopyFrom shares, publish before dispatch and write settings only before the snapshot
* [Look the state up through __instance.BurnPlan in a ComputeControl prefix](patch-methods-that-run-on-background-threads-5.md) - Find a vehicle's side state from a prefix on FlightComputer.ComputeControl by the BurnPlan object that the solver's copy shares with the live flight computer, and use it for exactly one call
* [Patches on planner tasks that run on pooled threads](patch-methods-that-run-on-background-threads-1.md) - Four prefixes replace parts of the Transfer Planner's pooled searches for unbound targets; they run on thread-pool threads, share only a locked once-only logger, and leave the player alert to a finalizer on the draw thread
* [Preload in StarMapBeforeMain because shaders compile in parallel on pool threads](patch-methods-that-run-on-background-threads-2.md) - A prefix on ShaderModuleUtils.FromFile redirects shader paths while the game compiles them on thread-pool threads at load; install it in StarMapBeforeMain, preload the owning assembly into the game's load context, and keep the prefix thread-safe
* [Worker-side postfix merging commands from the main thread](patch-methods-that-run-on-background-threads-3.md) - Answer a question on a vehicle worker thread from a concurrent dictionary that the main thread fills before dispatch: a postfix on PhysicsBubble.KittenWantsWake keeps burning cars off rails, with a prefix on Vehicle.PrepareWorker doing the main-thread writes
