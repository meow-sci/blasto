---
type: Game Feature
title: Add background music for a body with a MusicPlaylist asset
description: How the game picks and crossfades music for the body the camera follows from MusicPlaylist assets with a LocationId, and what a mod declares to add or replace a body's soundtrack
category: audio
group: sound-assets
topic: add-a-music-playlist-for-a-location
demonstrated_by: [mods/unscience/features/byo-music-vessel-3d-audio]
lifecycle: [startup:asset-load, startup:game-loaded, frame:post-render]
game_types:
  - KSA.MusicPlayList
  - KSA.MusicFileReference
  - KSA.LocationMusicPlayer
  - KSA.MusicPlayer
  - KSA.GameAudio
  - KSA.ChannelGroupType
tags: [audio, music, xml, playlist, location, crossfade]
---

KSA already has location music: as the camera follows Earth, the Moon, the Sun, Mars or Jupiter, a different track
plays on the `Music` channel group and fades into the next at a musical sync point. A mod adds to this by declaring
a `<MusicPlaylist>` whose `LocationId` names a body; there is no code to write for the playback itself.

**Status of the evidence.** No shipped mod feature runs this path in game. The only mod file that declares a
`MusicPlaylist` is BYO Music's development-host asset [byo-music/Assets.xml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music/Assets.xml): it is listed only by the
standalone host's `mod.toml`, the shipped unscience `mod.toml` lists no assets, and the file it names,
`Music/Sabotage.mp3`, is git-ignored.[^unscience-assets] This topic therefore takes the real shape and behaviour from
Core's own playlists and the decompiled `LocationMusicPlayer`, and treats the mod file as a hint at the element names
only. The single article marks every claim that was read from code and not run.

## Engine background

| Piece | What it does |
|---|---|
| `MusicPlayList` (`<MusicPlaylist>`) [current/decomp/KSA/MusicPlayList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MusicPlayList.cs#L6-L37) | A `SoundReference` with a `LocationId` attribute (field `AstronomicalParentId`); `PlayMusic` starts one music channel on the playlist's group, delayed on the DSP clock[^ksa-musicplaylist] |
| `MusicFileReference` (`<MusicFile>`) [current/decomp/KSA/MusicFileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MusicFileReference.cs#L6-L9) | A `SoundFileReference` with `<SyncPoint Value Unit>` children: the moments a crossfade may start[^ksa-musicfilereference] |
| `LocationMusicPlayer.Initialise` [current/decomp/KSA/LocationMusicPlayer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LocationMusicPlayer.cs#L17-L28) | Indexes every registered playlist by the hash of its `LocationId` (a later playlist with the same id replaces an earlier one)[^ksa-locationmusicplayer] |
| `LocationMusicPlayer.GetLocationPlaylist` [current/decomp/KSA/LocationMusicPlayer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LocationMusicPlayer.cs#L30-L45) | Looks up the followed object's hash, then walks `Parent` until a playlist is found |
| `LocationMusicPlayer.Update` and `ScheduleNext` [current/decomp/KSA/LocationMusicPlayer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LocationMusicPlayer.cs#L47-L108) | Every frame: pick the playlist for the audio camera's followed object, stop in some camera states, and when it changes fade the old track out at a sync point and start the new one |
| `GameAudio.UpdateAudio` [current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L150-L153) | Calls `System.Update()` and then `LocationMusicPlayer.Update` once per frame from `PostRender`[^ksa-gameaudio] |
| Startup order [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L985-L991) | `PopulateSounds` then `LocationMusicPlayer.Initialise`[^ksa-program] |
| Core playlists [current/Content/Core/Sounds.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Sounds.xml#L522-L611) | `EarthSOIMusic`, `LunaSOIMusic`, `SolSOIMusic`, `MarsSOIMusic`, `JupiterSOIMusic`, all `ChannelId="Music"`, `SimSpeed="Ignore"`, `SelectionRule="Shuffle"`[^ksa-core-sounds] |

Timing: the XML is read at `startup:asset-load`; the playlist index is built once at the end of loading; the player
runs every frame at `frame:post-render`. See the
[game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Declare a MusicPlaylist in a mod asset XML](add-a-music-playlist-for-a-location.md) | unscience (development asset), Core as the model | `xml-asset` | `startup:asset-load`, `frame:post-render` | Zero code and the game's own crossfade; limited to "one playlist per body", no 3D, no per-vehicle music |

For music that comes from the player's own files, or that sounds from a vessel in 3D, use the FMOD route in
[play your own sound files through KSA's FMOD system](../../channels/play-sound-files-through-the-games-fmod-system/overview.md).

## See also

- [Read the sound type map through the features that use each type](../understand-the-games-sound-and-music-xml-types/overview.md)
- [Ship custom sounds with a mod as XML sound assets](../ship-custom-sounds-as-xml-assets/overview.md)
- [Play your own sound files through KSA's FMOD system without blocking](../../channels/play-sound-files-through-the-games-fmod-system/overview.md)
- Mod feature: [BYO Music](../../../../mods/unscience/features/byo-music-vessel-3d-audio.md)

[^unscience-assets]: unscience: byo-music/Assets.xml
[^ksa-musicplaylist]: KSA.MusicPlayList (decompiled, build 2026.10.10.5554)
[^ksa-musicfilereference]: KSA.MusicFileReference (decompiled, build 2026.10.10.5554)
[^ksa-locationmusicplayer]: KSA.LocationMusicPlayer (decompiled, build 2026.10.10.5554)
[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-core-sounds]: Core/Sounds.xml (build 2026.10.10.5554)
