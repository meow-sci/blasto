# Game Feature

* [Use ground clutter as colliders: custom shapes and vehicle interaction](overview.md) - How a mod swaps a planet's ground clutter for its own collidable objects, builds the colliders from KSA's ColliderTemplate types, switches clutter collisions on and tunes how easily rocks come loose

# How-To

* [GroundClutterCollisions setting and DisplaceEnergyPerKg](give-ground-clutter-custom-colliders-3.md) - Turn clutter collisions on at runtime, lower the energy at which rocks come loose and make them lighter while a pusher is about, restoring the stock values afterwards, with a 400 ms off-on cycle so new masses are used
* [Register course statics as clutter on every pass](give-ground-clutter-custom-colliders-4.md) - Register your own Bepu statics in a bubble's private clutter dictionary at the start of each constraint-sim pass, with infinite mass so they never displace, because clutter's own Clear can wipe the registration at any frame
* [Subclass ColliderTemplate with an owned convex hull](give-ground-clutter-custom-colliders-1.md) - Convert a collider recipe into KSA's own ColliderTemplate subclasses, or into a private subclass for convex hulls built from any CPU mesh, so the native clutter physical data creates, scales and frees the shapes
* [Swap clutter bundles in an ExecuteNextClothSolvers prefix](give-ground-clutter-custom-colliders-2.md) - Commit a replacement clutter graph at the solver handoff, in a Priority.First prefix on Universe.ExecuteNextClothSolvers, after quiescing every solver and the GPU, and keep the originals for exact restore
