# Game Feature

* [Create and free shapes in KSA's global Bepu shape registry](overview.md) - Why ConstraintSim.UnlockShapes throws while the vehicle step runs, and the three ways mods create, cache and free Bepu shapes: at the dispatch window, after waiting on the solver, or by blocking until the step ends

# How-To

* [Add and free statics' shapes only while the solver is idle](mutate-the-shared-bepu-shape-registry-2.md) - Build and dispose Bepu Box and two-sided Mesh shapes from UI edits by waiting on the vehicle and cloth solver pools first, with a per-collider triangle budget and a retire-before-free order
* [Create shapes in an ExecuteNextVehicleSolvers prefix and cache by size](mutate-the-shared-bepu-shape-registry-1.md) - Make Bepu shapes on the main thread in the window between applying the last step and queueing the next, hand them to worker prefixes through a volatile snapshot, and copy convex pieces out so they can be queried at any time
* [UnlockShapesBlocking around vehicle construction](mutate-the-shared-bepu-shape-registry-3.md) - Build a vehicle from a frame hook by taking the shape registry with UnlockShapesBlocking, which spins until the in-flight step releases it, and attach the new craft to its parent so it is visited each frame
