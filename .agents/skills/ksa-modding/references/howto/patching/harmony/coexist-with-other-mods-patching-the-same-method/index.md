# Game Feature

* [Coexisting with other mods that patch the same method](overview.md) - How Harmony orders and re-runs patches when several mods target one game method, and how KSA mods order, share and merge patches so that features and neighbouring mods do not break each other

# How-To

* [Anchors that one mod can consume (and what the next transpiler then sees)](coexist-with-other-mods-patching-the-same-method-2.md) - Harmony re-runs every registered transpiler on the original IL whenever any mod patches a method, so a later transpiler sees the earlier one's output; choose anchors that survive earlier rewriting and fail by name when they do not
* [Idempotent shared installs with a registration API](coexist-with-other-mods-patching-the-same-method-3.md) - Install a seam once (a part render filter, a PrepareFrame handoff hook) and let any number of features register against it, so applying the same seam twice is harmless and the last owner removes it
* [One shared tick with per-feature enable flags set from TryApply results](coexist-with-other-mods-patching-the-same-method-4.md) - Drive several per-vehicle state machines from one prefix and postfix pair on Universe.ApplyVehicleSolvers, with a static enable flag per feature that is set only from that feature's own patch result
* [One worker-side postfix merging every writer](coexist-with-other-mods-patching-the-same-method-5.md) - Own exactly one postfix on FlightComputer.ComputeControl, run every actuator writer from it in a fixed order, and merge their wake-up and actuator requests into the stock output without clearing or raising what stock set
* [Priority.First prefixes and Priority.Last finalizers around shared seams](coexist-with-other-mods-patching-the-same-method-1.md) - Use Harmony priorities so a prefix on Universe.ExecuteNextVehicleSolvers runs before every other mod's prefix and a clearing finalizer on Vehicle.PrepareWorker runs after every other mod's finalizer
