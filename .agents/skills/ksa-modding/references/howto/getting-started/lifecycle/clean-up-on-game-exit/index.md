# Game Feature

* [Clean up on game exit and relaunch the game from a mod](overview.md) - What runs, and what is already gone, when KSA shuts down under StarMap, which exit paths skip StarMapUnload, and how a mod relaunches the game process

# How-To

* [Clean up with StarMapUnload and ProcessExit, and reset static state](clean-up-on-game-exit-1.md) - Release what your mod owns at shutdown, know what is already gone when StarMapUnload runs, cover the Environment.Exit paths it skips, and reset statics so a second init starts clean
* [Relaunch the game from a mod with a PrepareAll prefix and Process.Start](clean-up-on-game-exit-2.md) - Detect that a startup-only change needs a fresh process, ask the player with a self-pumped popup, then restart StarMap.exe with a marker argument and exit cleanly
