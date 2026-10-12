# Game Feature

* [Test a mod against the real game with HeadlessHarness](overview.md) - Ship tests as a second StarMap mod that depends on HeadlessHarness and the mod under test so the real simulation runs without a GPU, swap the clock for simulation time and use the game's own private routines as the oracle

# How-To

* [Build a consumer mod with its own mod.toml, InternalsVisibleTo and a test base class](headless-test-harness-for-game-mods-1.md) - Ship tests as a second StarMap mod that depends on HeadlessHarness and the mod under test, so the real simulation runs without a GPU, with simulation time substituted for the clock and fixtures that clean up after themselves
* [Demand equality with the game's private routines using a small oracle suite](headless-test-harness-for-game-mods-2.md) - Write a few HeadlessHarness tests that call the stock private routines your mod re-implements with the same inputs through reflection, so a game update that changes either side fails a test instead of drifting silently
