# Game Feature

* [Play your own sound files through KSA's FMOD system without blocking](overview.md) - How a mod creates FMOD sounds from bytes or a file path on GameAudio.System, opens long files non-blocking, and plays them on the game's Sfx, Music or Ui channel group so they mix with game audio and obey the volume sliders

# How-To

* [Attach a stream to a vessel with per-frame 3D attributes and a completion-based repeat scheduler](play-sound-files-through-the-games-fmod-system-3.md) - Own one VesselSound per entry that places its FMOD channel at a vessel every frame in camera view space, loops natively or repeats after a real-time gap counted from observed completion, and survives scene saves
* [Play in-memory sounds with a channel table and reference-counted release](play-sound-files-through-the-games-fmod-system-1.md) - Create FMOD sounds from bytes on GameAudio.System, play them paused on the game's Sfx, Music or Ui group, track the channels, and release a sound only when no live channel plays it
* [Stream a file path on the Sfx group, opened non-blocking and polled each frame](play-sound-files-through-the-games-fmod-system-2.md) - Open a long audio file with TryCreateStream and Mode.NonBlocking, poll TryGetOpenState once per frame instead of blocking, play it paused on the game's Sfx group and release the stream when done
