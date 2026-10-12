# Game Feature

* [Test Harmony patches without running the game](overview.md) - Apply the real production patch code to stand-in KSA types in a console test, run a patch-apply check over a new game build offline, or install a test-scoped Harmony owner inside a headless run

# How-To

* [Apply the real patches to stand-in KSA types in a console test](test-harmony-patches-without-the-game-1.md) - Link the production patch files into a framework-free console project, declare minimal managed stand-ins that carry the game's namespace and member names, and let Harmony patch them exactly as it patches the real game
* [Check that every patch class still applies to a new game build with a throwaway console app](test-harmony-patches-without-the-game-2.md) - Load the built mod DLL and a new set of game DLLs offline and run CreateClassProcessor on every patch class the way the mod's Patcher does, to catch target and parameter-name drift the compiler cannot
* [Run transpilers over the game's real IL and scope test patches to a disposable Harmony owner](test-harmony-patches-without-the-game-3.md) - In a HeadlessHarness test, feed PatchProcessor.GetOriginalInstructions of the current game method to the mod's transpiler, assert on the resulting instructions, and apply hook sets under an IDisposable scope with their own Harmony id
