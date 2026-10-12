# Game Feature

* [Writing a Harmony transpiler for a KSA method](overview.md) - How to read the real IL of a KSA method, build the instruction rewrite, prove the match count and survive Harmony re-running transpilers, with four worked shapes: retarget a call, insert at an anchor, replace an instruction run and filter a read

# How-To

* [Filter one value read through a registry check](write-a-harmony-transpiler-4.md) - Insert a filter call after the single StructuralLoad.GLoadFraction getter in PhysicsBubble.DetectStructuralFailure so registered vessels read 0.0 for the destruction comparison only, with a concurrent registry because the detector runs on vehicle workers
* [Insert a call before a validated anchor](write-a-harmony-transpiler-2.md) - Add a draw call into the stock Transfer Planning window by inserting it before a validated anchor call in a pipeline of small passes, each returning the list unchanged and warning once when its anchor is missing, and unit test the rewrite over the original IL
* [Replace an instruction run with a helper that takes a local by reference](write-a-harmony-transpiler-3.md) - Rewrite the compare-and-branch that picks the nearby body in CelestialSystem.GetNearbyAstronomical: verify the exact IL sequence, read local slots from it, pass the best-so-far local by address to a helper, nop the redundant store, and return the original IL when the shape check fails
* [Retarget the operand of one call (same signature)](write-a-harmony-transpiler-1.md) - The smallest useful Harmony transpiler: read the real IL from KSA.dll, resolve the target and replacement exactly, change only the operand of the one matching call, count the matches and throw unless there is exactly one
