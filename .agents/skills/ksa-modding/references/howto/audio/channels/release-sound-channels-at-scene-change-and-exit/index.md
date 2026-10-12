# Game Feature

* [Stop and release sound channels at scene change and game exit](overview.md) - When a mod's held sound channels must be stopped (owner gone, save loaded, scene left) and why unload-time cleanup is moot, because Program.Dispose closes FMOD before StarMap invokes StarMapUnload

# How-To

* [Dispose all streams on save reset before the vehicles are replaced](release-sound-channels-at-scene-change-and-exit-3.md) - Stop and release every FMOD stream in a save participant's reset step, which runs in a Harmony prefix on Universe.DeserializeSave before the world is destroyed, stop an entry when its target vehicle is lost, and restore sounds paused
* [Release FMOD sounds only after no channel plays them](release-sound-channels-at-scene-change-and-exit-4.md) - Prune stopped channels each frame, release cached FMOD sounds only when their clip version is gone and no live channel plays them, and have teardown free only what the mod created, never the game's FMOD system
* [Stop channels on world reload with stopImmediate and treat unload-time StopAll as moot](release-sound-channels-at-scene-change-and-exit-1.md) - Latch a save load with a postfix on Program.OnGameLoaded, stop held channels with stopImmediate on the next frame, and know that the StopAll in StarMapUnload runs after FMOD has been closed
* [Stop on scene exit and tolerate a dead audio system](release-sound-channels-at-scene-change-and-exit-2.md) - Stop held channels when the craft leaves the flight scene or the mod disables itself, treat the StarMapUnload StopAll as moot, and wrap every channel call so a reclaimed or dead channel costs a quiet frame
