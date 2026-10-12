# Game Feature

* [Build a KSA mod that references only StarMap.API and Harmony](overview.md) - Four ways to limit how much of the game a mod's build and code depend on - pure reflection, a compile-gated Game half, a provider library and a per-frame census

# How-To

* [Build a reflection-only mod that references just StarMap.API and Harmony](build-a-mod-without-game-references-1.md) - Compile with no KSA reference and reach every game type by name with AccessTools, checking parameter names and field types before each patch and logging one consequence sentence per failed lookup
* [Centralize game-state access behind a static provider library](build-a-mod-without-game-references-3.md) - Put every read of controlled vehicle, vehicle list, celestials and sim time in one small library of static providers so a game update breaks one file, and let features call the providers instead of game types
* [Rebuild the vehicle set each frame from Universe.CurrentSystem.All and sync a registry](build-a-mod-without-game-references-4.md) - Read the set of vehicles from the system collection instead of Program.VehiclesInFrame, which is empty after PostRender, and keep a Vehicle-keyed registry that drops disposed craft
* [Split a mod into a game-free half and a compile-gated Game half](build-a-mod-without-game-references-2.md) - Keep typed game access in a Game/ folder that is compiled only when KSA.dll is present, bridged by partial methods, so the rest of the mod, including tests, builds anywhere
