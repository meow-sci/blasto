# Game Feature

* [Making a vehicle survive crashes and g-loads, and damaging parts against crash tolerance](overview.md) - KSA destroys vehicles through two paths, a per-part crash-tolerance comparison and a whole-craft structural check, and four recipes to veto, neutralise, filter or imitate them

# How-To

* [Damage parts against their crash tolerance with a weak-keyed health ledger](make-a-vehicle-indestructible-4.md) - Judge each part of nearby craft against Part.CrashTolerancePascals with a cube-root blast law, keep cumulative damage in a ConditionalWeakTable, break through the engine's failure path and dent through FxDeformation.ReportContact
* [Filter only the g-load read with a guarded transpiler and persist it by vessel identity](make-a-vehicle-indestructible-3.md) - Insert a static filter after the single StructuralLoad.GLoadFraction getter call in DetectStructuralFailure, and rebind the per-vessel setting after a save load without ambiguity
* [Make one kind of craft indestructible with CrashTolerance and a destruction veto](make-a-vehicle-indestructible-1.md) - Set a huge CrashTolerance on the part XML to stop per-part failure and add a Harmony prefix on Universe.DestroyVehicleFromEvent to veto whole-craft destruction for your craft
* [Protect chosen vessels with a tolerance getter prefix and a detector pair](make-a-vehicle-indestructible-2.md) - Keep a worker-safe registry of vessels and patch Part.CrashTolerancePascals and PhysicsBubble.DetectStructuralFailure so only those vessels cannot break, with no template mutation
