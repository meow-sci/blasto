# Game Feature

* [Keep mod exceptions out of KSA's frame loop](overview.md) - Why an exception in a StarMap hook ends the game, and two fault-budget designs that log, disable the mod and undo its side effects

# How-To

* [Count consecutive failures and restore the stock HUD when the mod disables itself](contain-exceptions-in-starmap-hooks-2.md) - Disable a mod only after ten failures in a row, log the first failure of each streak, and undo visible side effects such as hidden gauge canvases
* [Wrap every frame hook in try/catch and disable the mod after ten faults](contain-exceptions-in-starmap-hooks-1.md) - Feed hook exceptions into a shared fault counter, log each one, and past a limit switch the mod off for the session by removing its patches and dropping its state
