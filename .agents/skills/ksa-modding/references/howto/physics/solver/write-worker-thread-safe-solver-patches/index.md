# Game Feature

* [Write worker-thread-safe patches inside the vehicle solver](overview.md) - How to share data with Harmony patches that run on vehicle worker threads per substep, without locks, allocation or exceptions

# How-To

* [Concurrent registries keyed by reference](write-worker-thread-safe-solver-patches-3.md) - Let a UI edit a set of vehicles while worker-thread patches read it, using a ConcurrentDictionary with reference equality, a disposed check on read and a per-frame prune on the main thread
* [Identify the vehicle a callback runs for with a ThreadStatic set in a prefix and cleared in a finalizer](write-worker-thread-safe-solver-patches-5.md) - Bracket Vehicle.PrepareWorker with a Priority.First prefix that records the vehicle in a ThreadStatic and a Priority.Last finalizer that clears it, so a patch on a function that does not receive the vehicle can tell vehicle-driven calls from camera and UI calls
* [Immutable commands published before the solver job is queued](write-worker-thread-safe-solver-patches-6.md) - Run every mod actuator writer from one postfix on FlightComputer.ComputeControl, feed each from an immutable command published on the main thread before dispatch, and merge their wake and off-rails requests into the stock output
* [Pure, allocation-free, never-throwing code over the in arguments](write-worker-thread-safe-solver-patches-1.md) - Replace a call inside PhysicsStates.ComputeDerivatives with a function that touches only its arguments, never allocates and never throws, and falls back to the stock method when it cannot answer
* [Read-only tables filled on the main thread before physics starts](write-worker-thread-safe-solver-patches-2.md) - Give worker-thread prefixes and postfixes lock-free data by filling a ConditionalWeakTable at load time, or by swapping a whole array through a volatile field when the data changes at runtime
* [Volatile snapshot handoff from worker patches to the main thread](write-worker-thread-safe-solver-patches-4.md) - Publish an immutable snapshot from the main thread through a volatile field, read it once per call in prefixes on ConstraintSim passes and KittenLocomotion.StepGrounded, and keep all mutable state per simulation
