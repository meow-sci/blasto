# Game Feature

* [Create and load native KSA saves from mod code](overview.md) - The game methods that write and read a save, and three ways mods call them - a timed autosave, a command-file load that waits for a craft, and a scenario runner that loads after startup

# How-To

* [Load a save a few seconds after start from an unattended scenario runner](save-and-load-the-game-from-code-3.md) - Read a one-shot request file at load, wait three seconds of player time, call GameSaves.LoadSaveGame, then run the scripted scenario once a craft is flown
* [Load a save from a command file and wait until a craft is flown again](save-and-load-the-game-from-code-2.md) - Call GameSaves.LoadSaveGame from a polled command bridge and finish the command only when Program.ControlledVehicle is a live vehicle, because the load itself gives no completion signal
* [Write timed native saves with SaveName.TryAccept and UncompressedSave.Make](save-and-load-the-game-from-code-1.md) - Save the game every N real seconds through the same two calls as the console save command, refusing in the editor and keeping the timer independent of warp and pause
