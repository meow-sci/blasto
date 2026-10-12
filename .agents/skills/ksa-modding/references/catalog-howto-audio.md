# Audio

Playing sound in KSA from a mod through the game's FMOD system - shipping sound XML assets, placing and moving spatial channels, reusing Core sounds and their parameters, timing sounds against the world, and bringing your own files or music.

## Playback

Playing sounds against the world from code: spatial channels, parameter curves, engine loops and propagation delay.

### [Delay a world sound by the time sound takes to reach the listener](howto/audio/playback/delay-a-sound-until-the-blast-front-arrives/overview.md)

How a mod makes a distant event heard after it is seen, by following its expanding front against the live audio camera so that flying toward it brings the sound sooner

- [Follow a burst's front against the live camera and play its bang on arrival](howto/audio/playback/delay-a-sound-until-the-blast-front-arrives/delay-a-sound-until-the-blast-front-arrives.md) — advanced

### [Drive a sound's Param curves from code and reuse stock sounds](howto/audio/playback/drive-sound-parameters-from-code/overview.md)

How the named parameters a sound's Param curves listen to are set with SetParameter and a KeyHash, when the engine applies them, and how to reuse Core's engine and explosion sounds for mod-simulated sources

- [Play Core's engine sound for a mod-simulated rocket by mirroring Vehicle.UpdateAudio](howto/audio/playback/drive-sound-parameters-from-code/drive-sound-parameters-from-code-1.md) — intermediate
- [Play Core's explosion sound on your own schedule with hand-set parameters](howto/audio/playback/drive-sound-parameters-from-code/drive-sound-parameters-from-code-2.md) — advanced
- [Borrow Core's engine loop for a mod-simulated projectile with Throttle pinned open](howto/audio/playback/drive-sound-parameters-from-code/drive-sound-parameters-from-code-3.md) — beginner

### [Play a sound at a world position and hold its spatial channel](howto/audio/playback/play-a-sound-at-a-world-position-and-hold-its-channel/overview.md)

How a mod starts a declared sound so it pans, attenuates, Doppler-shifts and is muffled by air as if it came from a point in the world, and how it keeps and moves the channel afterwards

- [Fire a positional sound with GameAudio.PlaySound and SpatialAudio(Astronomical, offset)](howto/audio/playback/play-a-sound-at-a-world-position-and-hold-its-channel/play-a-sound-at-a-world-position-and-hold-its-channel-1.md) — beginner
- [Hold one channel per mod-simulated source and move it every frame](howto/audio/playback/play-a-sound-at-a-world-position-and-hold-its-channel/play-a-sound-at-a-world-position-and-hold-its-channel-2.md) — intermediate
- [Keep one looping channel per craft with guarded move and cut](howto/audio/playback/play-a-sound-at-a-world-position-and-hold-its-channel/play-a-sound-at-a-world-position-and-hold-its-channel-3.md) — intermediate

### [Crossfade engine loops and play spin-up, loop and tail sounds](howto/audio/playback/play-a-spin-up-loop-tail-sound-and-crossfade-engine-loops/overview.md)

How a mod makes a simulated engine or weapon sound alive, either by crossfading two looping layers against RPM and load or by declaring a CompoundSound whose start, loop and stop sounds the engine sequences

- [Crossfade an idle loop and a load loop by RPM, with loops cut from recordings](howto/audio/playback/play-a-spin-up-loop-tail-sound-and-crossfade-engine-loops/play-a-spin-up-loop-tail-sound-and-crossfade-engine-loops-1.md) — advanced
- [Declare a CompoundSound with spin-up, loop and tail cut from one recording](howto/audio/playback/play-a-spin-up-loop-tail-sound-and-crossfade-engine-loops/play-a-spin-up-loop-tail-sound-and-crossfade-engine-loops-2.md) — intermediate

## Sound assets

Declaring sounds and music as XML assets, and the sound type map they register into.

### [Add background music for a body with a MusicPlaylist asset](howto/audio/sound-assets/add-a-music-playlist-for-a-location/overview.md)

How the game picks and crossfades music for the body the camera follows from MusicPlaylist assets with a LocationId, and what a mod declares to add or replace a body's soundtrack

- [Declare a MusicPlaylist in a mod asset XML](howto/audio/sound-assets/add-a-music-playlist-for-a-location/add-a-music-playlist-for-a-location.md) — intermediate

### [Ship custom sounds with a mod as XML sound assets](howto/audio/sound-assets/ship-custom-sounds-as-xml-assets/overview.md)

How a mod declares its own wav files in an asset XML listed in mod.toml so KSA registers them as sounds you can look up by id, with channel group, 3D range, loop flag, priority and simulation-speed rule

- [Declare one-shot sounds with random variants and a simulation-speed rule](howto/audio/sound-assets/ship-custom-sounds-as-xml-assets/ship-custom-sounds-as-xml-assets-1.md) — beginner
- [Declare looping layers that share one SpatialSoundData asset](howto/audio/sound-assets/ship-custom-sounds-as-xml-assets/ship-custom-sounds-as-xml-assets-2.md) — beginner
- [Tune spatial data, priority and pitch variation per weapon class](howto/audio/sound-assets/ship-custom-sounds-as-xml-assets/ship-custom-sounds-as-xml-assets-3.md) — intermediate

### [Understand KSA's sound, channel group and music playlist XML types](howto/audio/sound-assets/understand-the-games-sound-and-music-xml-types/overview.md)

A map of the sound types a mod can declare or look up (Sound, CompoundSound, MultiSound, ClusterSound, SpatialSoundData, SoundGroup, ChannelGroup, MusicPlaylist), how each registers and populates, and what playing each one returns

- [Read the sound type map through the features that use each type](howto/audio/sound-assets/understand-the-games-sound-and-music-xml-types/understand-the-games-sound-and-music-xml-types.md) — beginner

## Channels

Owning FMOD channels: raw sound files, time-warp and volume behaviour, and releasing channels at scene change and exit.

### [Keep mod-played sounds sensible under time warp and the player's volume](howto/audio/channels/keep-mod-sounds-sensible-under-time-warp/overview.md)

What the engine does to a sound at high simulation speed (pitch rule, stop above 10x, PlaySound refusing) and to the player's Master, Sfx, Ui and Music sliders, and how a mod follows, overrides or bypasses each

- [Declare a SimSpeed rule and let the engine handle time warp](howto/audio/channels/keep-mod-sounds-sensible-under-time-warp/keep-mod-sounds-sensible-under-time-warp-1.md) — beginner
- [Cut your own loops below the engine's time-warp threshold](howto/audio/channels/keep-mod-sounds-sensible-under-time-warp/keep-mod-sounds-sensible-under-time-warp-2.md) — intermediate
- [Play channels the game never registers so warp rules do not touch them](howto/audio/channels/keep-mod-sounds-sensible-under-time-warp/keep-mod-sounds-sensible-under-time-warp-3.md) — intermediate

### [Play your own sound files through KSA's FMOD system without blocking](howto/audio/channels/play-sound-files-through-the-games-fmod-system/overview.md)

How a mod creates FMOD sounds from bytes or a file path on GameAudio.System, opens long files non-blocking, and plays them on the game's Sfx, Music or Ui channel group so they mix with game audio and obey the volume sliders

- [Play in-memory sounds with a channel table and reference-counted release](howto/audio/channels/play-sound-files-through-the-games-fmod-system/play-sound-files-through-the-games-fmod-system-1.md) — advanced
- [Stream a file path on the Sfx group, opened non-blocking and polled each frame](howto/audio/channels/play-sound-files-through-the-games-fmod-system/play-sound-files-through-the-games-fmod-system-2.md) — intermediate
- [Attach a stream to a vessel with per-frame 3D attributes and a completion-based repeat scheduler](howto/audio/channels/play-sound-files-through-the-games-fmod-system/play-sound-files-through-the-games-fmod-system-3.md) — advanced

### [Stop and release sound channels at scene change and game exit](howto/audio/channels/release-sound-channels-at-scene-change-and-exit/overview.md)

When a mod's held sound channels must be stopped (owner gone, save loaded, scene left) and why unload-time cleanup is moot, because Program.Dispose closes FMOD before StarMap invokes StarMapUnload

- [Stop channels on world reload with stopImmediate and treat unload-time StopAll as moot](howto/audio/channels/release-sound-channels-at-scene-change-and-exit/release-sound-channels-at-scene-change-and-exit-1.md) — intermediate
- [Stop on scene exit and tolerate a dead audio system](howto/audio/channels/release-sound-channels-at-scene-change-and-exit/release-sound-channels-at-scene-change-and-exit-2.md) — beginner
- [Dispose all streams on save reset before the vehicles are replaced](howto/audio/channels/release-sound-channels-at-scene-change-and-exit/release-sound-channels-at-scene-change-and-exit-3.md) — advanced
- [Release FMOD sounds only after no channel plays them](howto/audio/channels/release-sound-channels-at-scene-change-and-exit/release-sound-channels-at-scene-change-and-exit-4.md) — advanced
