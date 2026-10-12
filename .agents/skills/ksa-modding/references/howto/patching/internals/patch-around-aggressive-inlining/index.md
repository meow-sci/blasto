# Game Feature

* [Changing behaviour of a game method marked AggressiveInlining](overview.md) - Why a Harmony prefix or postfix on a KSA method marked AggressiveInlining can silently never run, what a local test shows about when the JIT bypasses a detour, and four patterns the pinned mods use to change behaviour without depending on it

# How-To

* [Bracket the non-inlined methods that enclose an inlined struct callback](patch-around-aggressive-inlining-4.md) - Suppress a vessel's rigid-body collisions without patching the AggressiveInlining NarrowPhaseCallbacks struct, by removing its Bepu shapes in a prefix on ConstraintSim.DetectCollisions and Simulate and restoring them in a finalizer, and what Sphinx does where it must patch the callback
* [Patch the non-inlined entry points that reach the inlined method too](patch-around-aggressive-inlining-2.md) - Cover the flight-computer methods SetNullRot, RateHold and TrackTarget, which are AggressiveInlining, by also patching the non-inlined Vehicle.SetEnum, ToggleEnum, SetStabilization and KittenEva.HandleKittenAction that reach them, and by listing the paths that stay exposed
* [Postfix the single non-inlined outer method instead of a per-vehicle callee](patch-around-aggressive-inlining-3.md) - Run per-vehicle state machines right after the physics results are applied by patching the static, non-inlined Universe.ApplyVehicleSolvers with a prefix and a postfix, because the per-vehicle callee Vehicle.UpdateFromTaskResultsSynchronized is AggressiveInlining
* [Rewrite the one caller's call site instead of the inlined callee](patch-around-aggressive-inlining-1.md) - Change what PhysicsStates.ComputeDrag does for atmospheric drag by transpiling its caller ComputeDerivatives and swapping one call operand, which is correct whether or not the JIT inlined the AggressiveInlining callee
