---
type: Game Feature
title: Understand KSA's sound, channel group and music playlist XML types
description: A map of the sound types a mod can declare or look up (Sound, CompoundSound, MultiSound, ClusterSound, SpatialSoundData, SoundGroup, ChannelGroup, MusicPlaylist), how each registers and populates, and what playing each one returns
category: audio
group: sound-assets
topic: understand-the-games-sound-and-music-xml-types
demonstrated_by:
  - mods/ksacars/features/engine-sound-crossfade
  - mods/ksacars/features/rocket-sound-without-engine
  - mods/ksarmory/features/spatial-gun-and-motor-sounds
  - mods/ksarmory/features/burst-sound-at-blast-arrival
  - mods/unscience/features/byo-music-vessel-3d-audio
lifecycle: [startup:asset-load, startup:all-mods-loaded, startup:game-loaded]
game_types:
  - KSA.SoundBehavior
  - KSA.SoundReference
  - KSA.CompoundSound
  - KSA.MultiSound
  - KSA.ClusterSound
  - KSA.SpatialSoundData
  - KSA.ChannelGroupReference
  - KSA.MusicPlayList
  - KSA.LocationMusicPlayer
  - KSA.ModLibrary
tags: [audio, reference, xml, sound-types, channel-groups, music, fmod]
---

Every sound a mod plays comes from a declaration in an `<Assets>` XML file that `mod.toml` lists under `assets`. The
game does not have one "sound" class: it has a small family of types that nest inside each other, plus a separate
tree of FMOD channel groups that decides where a sound is mixed. A modder who knows the family can read Core's
`Sounds.xml`, pick what to reuse, and declare the rest.

This topic is a reference. It has one article that walks the type map through the five mod features that use it
(KSACars, KSArmory and BYO Music), and it is the place the other audio topics link to for "what is this element".

## Engine background

| Type (XML element) | Role | Where |
|---|---|---|
| `SoundBehavior` (no element) | Abstract base: `Play(SpatialAudio, float, out IChannel?, bool)`, `Get()`, an optional `ClusterSound` child[^ksa-soundbehavior] | [current/decomp/KSA/SoundBehavior.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SoundBehavior.cs#L6-L38) |
| `SoundReference` (`<Sound>`) | One sound: files, channel group, spatial data, priority, simulation-speed rule, fades, pitch and volume ranges, `Param` curves[^ksa-soundreference] | [current/decomp/KSA/SoundReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SoundReference.cs#L10-L70) |
| `CompoundSound` (`<CompoundSound>`) | `StartSound`, `Sound`, `StopSound` played as one: start and loop begin together, the stop sound waits paused[^ksa-compoundsound] | [current/decomp/KSA/CompoundSound.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CompoundSound.cs#L6-L31) |
| `MultiSound` (`<MultiSound>`) | A list of `Sound` and `CompoundSound` children, all started together[^ksa-multisound] | [current/decomp/KSA/MultiSound.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MultiSound.cs#L7-L63) |
| `ClusterSound` (`<ClusterSound>`) | A `MultiSound` whose channels many sources share, with an averaged position[^ksa-clustersound] | [current/decomp/KSA/ClusterSound.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ClusterSound.cs#L5-L39) |
| `SpatialSoundData` (`<SpatialSoundData>`) | 3D distance range, the 2D/3D blend and the distance filter flag[^ksa-spatialsounddata] | [current/decomp/KSA/SpatialSoundData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SpatialSoundData.cs#L7-L77) |
| `ChannelGroupReference` (`<ChannelGroup>`) | One FMOD channel group with a parent, a volume and a DSP chain[^ksa-channelgroupreference] | [current/decomp/KSA/ChannelGroupReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChannelGroupReference.cs#L8-L101) |
| `MusicPlayList` (`<MusicPlaylist>`) | A `SoundReference` with a `LocationId`, played by `LocationMusicPlayer`[^ksa-musicplaylist] | [current/decomp/KSA/MusicPlayList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MusicPlayList.cs#L6-L37) |

The XML-to-type table is `AssetBundle`: `SoundFile`, `Sound`, `MultiSound`, `CompoundSound`, `ClusterSound`,
`SpatialSoundData`, `SoundGroup`, `MusicPlaylist` and `ChannelGroup` are the element names the game accepts
([current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L55-L73)).[^ksa-assetbundle] There is no `<SoundBehavior>` element in that table or in
Core's `Sounds.xml`; notes in some mod repositories that say otherwise are wrong for this build.

The channel group tree has four roots made in code (`Master`, `Sfx`, `Ui`, `Music`, from `GameAudio.OnApplicationStart`)
and Core's own groups and presets in `Sounds.xml`.[^ksa-gameaudio][^ksa-core-sounds] Music is not played by a mod call:
`LocationMusicPlayer.Update` runs from `GameAudio.UpdateAudio` every frame and starts the playlist registered for the
body the audio camera follows.[^ksa-locationmusicplayer]

Registration and population run in a fixed order:[^ksa-modlibrary][^ksa-program]

| Phase | What happens | Lifecycle |
|---|---|---|
| Asset XML read | Each type's `OnDataLoad` registers it in `ModLibrary` (sound behaviours, spatial data, music playlists, channel groups); `ChannelGroupReference.OnDataLoad` creates the FMOD group; sound files queue as loaders | `startup:asset-load` |
| Loaders | `SoundFileReference.DoLoad` calls `GameAudio.CreateFmodSound` to build each FMOD `Sound` | `startup:asset-load` |
| `ModLibrary.AttachGameData` | Game data merged into templates | between `startup:all-mods-loaded` and `startup:game-loaded` |
| `ModLibrary.PopulateSounds` | `Populate()` on every sound behaviour, then every playlist, then every channel group: references resolve, groups parent into the tree and take their DSPs | before the scene exists |
| `LocationMusicPlayer.Initialise` | Playlists indexed by `LocationId` hash | right after, same call site |

`PopulateSounds` and `Initialise` are called back to back after `AttachGameData` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L985-L991));
`PopulateSounds` itself is [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1360-L1373).

## Approaches

| Article | Mod | Technique | Trade-off |
|---|---|---|---|
| [Read the type map through the features that use each type](understand-the-games-sound-and-music-xml-types.md) | KSACars, KSArmory, unscience | `xml-asset` | A reference, not a recipe: each type is shown in a real asset file with what its `Play` returns and what its attributes do |

## See also

- [Ship custom sounds with a mod as XML sound assets](../ship-custom-sounds-as-xml-assets/overview.md)
- [Add background music for a body with a MusicPlaylist asset](../add-a-music-playlist-for-a-location/overview.md)
- [Crossfade engine loops and play spin-up, loop and tail sounds](../../playback/play-a-spin-up-loop-tail-sound-and-crossfade-engine-loops/overview.md)
- [Drive a sound's Param curves from code and reuse stock sounds](../../playback/drive-sound-parameters-from-code/overview.md)
- [Play your own sound files through KSA's FMOD system without blocking](../../channels/play-sound-files-through-the-games-fmod-system/overview.md)
- [Know which asset XML elements the game accepts and what they load](../../../assets/asset-xml/asset-xml-element-reference/overview.md)
- [Understand when and in which order KSA loads mod XML](../../../assets/asset-xml/mod-xml-load-order/overview.md)

[^ksa-soundbehavior]: KSA.SoundBehavior (decompiled, build 2026.10.10.5554)
[^ksa-soundreference]: KSA.SoundReference (decompiled, build 2026.10.10.5554)
[^ksa-compoundsound]: KSA.CompoundSound (decompiled, build 2026.10.10.5554)
[^ksa-multisound]: KSA.MultiSound (decompiled, build 2026.10.10.5554)
[^ksa-clustersound]: KSA.ClusterSound (decompiled, build 2026.10.10.5554)
[^ksa-spatialsounddata]: KSA.SpatialSoundData (decompiled, build 2026.10.10.5554)
[^ksa-channelgroupreference]: KSA.ChannelGroupReference (decompiled, build 2026.10.10.5554)
[^ksa-musicplaylist]: KSA.MusicPlayList (decompiled, build 2026.10.10.5554)
[^ksa-assetbundle]: KSA.AssetBundle (decompiled, build 2026.10.10.5554)
[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
[^ksa-core-sounds]: Core/Sounds.xml (build 2026.10.10.5554)
[^ksa-locationmusicplayer]: KSA.LocationMusicPlayer (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
