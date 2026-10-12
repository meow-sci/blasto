# Subdirectories

* [check-the-game-version](check-the-game-version/index.md) - Comparing the running KSA build with the tested one and logging both
* [choose-a-starmap-startup-hook](choose-a-starmap-startup-hook/index.md) - What exists at StarMapBeforeMain, ImmediateLoad and AllModsLoaded, how to choose between them and how to diagnose a silent hook
* [clean-up-on-game-exit](clean-up-on-game-exit/index.md) - What runs at shutdown under StarMap, and relaunching the game from a mod
* [contain-exceptions-in-starmap-hooks](contain-exceptions-in-starmap-hooks/index.md) - Fault budgets that keep hook exceptions out of KSA's frame loop
* [game-loop-and-lifecycle](game-loop-and-lifecycle/index.md) - How KSA runs a frame and steps physics, and where mod code can safely hook in
* [write-a-per-mod-log-file](write-a-per-mod-log-file/index.md) - Per-mod log files, the game log category and log-once helpers
