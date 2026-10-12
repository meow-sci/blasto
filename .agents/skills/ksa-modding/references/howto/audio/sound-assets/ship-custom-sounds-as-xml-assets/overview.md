---
type: Game Feature
title: Ship custom sounds with a mod as XML sound assets
description: How a mod declares its own wav files in an asset XML listed in mod.toml so KSA registers them as sounds you can look up by id, with channel group, 3D range, loop flag, priority and simulation-speed rule
category: audio
group: sound-assets
topic: ship-custom-sounds-as-xml-assets
demonstrated_by:
  - mods/ksagolf/features/world-positioned-sounds
  - mods/ksacars/features/engine-sound-crossfade
  - mods/ksarmory/features/spatial-gun-and-motor-sounds
lifecycle: [startup:asset-load, startup:all-mods-loaded]
game_types:
  - KSA.SoundReference
  - KSA.SoundFileReference
  - KSA.SpatialSoundData
  - KSA.SoundBehavior
  - KSA.AssetBundle
  - KSA.ModLibrary
  - KSA.ChannelGroupReference
  - KSA.CompoundSound
  - KSA.LoopData
tags: [audio, xml, sound-assets, mod-toml, wav, spatial-sound-data]
---

A mod adds sounds the same way it adds parts: an asset XML, listed in `mod.toml`, that names files shipped in the mod
folder. The game turns each `<Sound>` into a `SoundReference`, loads its wav into an FMOD `Sound`, and registers it
under its `Id`. Code then looks it up with `ModLibrary.Get<SoundBehavior>(id)` and plays it (see
[play a sound at a world position](../../playback/play-a-sound-at-a-world-position-and-hold-its-channel/overview.md)).
There is no code hook to write for the declaration itself.

The three articles are three shapes of the same file: one-shot sounds with random variants, looping layers that share
one spatial asset, and a per-weapon-class set with tuned priority and pitch. Use the
[type map](../understand-the-games-sound-and-music-xml-types/overview.md) for the meaning of the other elements.

## Engine background

| Piece | What it does for a mod |
|---|---|
| `AssetBundle` element table (`Sound`, `SpatialSoundData`, `CompoundSound`, `MultiSound`, `MusicPlaylist`, `ChannelGroup`, `SoundFile`) [current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L55-L73) | The element names an `Assets` file may contain; `<Sound>` maps to `SoundReference`[^ksa-assetbundle] |
| `SoundReference` [current/decomp/KSA/SoundReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SoundReference.cs#L10-L54) | `ChannelId`, `Priority` (default 128), `SimSpeed` (default `All`), `SelectionRule` (default `Random`), `SoundFile` children, `SpatialSoundData`, `Pitch`, `Volume`, `Param`[^ksa-soundreference] |
| `SoundFileReference` [current/decomp/KSA/SoundFileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SoundFileReference.cs#L8-L81) | `Path`, `TwoD`, `StreamFromDisk`, `Volume` (clamped to 0..2), `Loop`; `DoLoad` throws when `Path` is empty or the file is missing[^ksa-soundfilereference] |
| `GameAudio.CreateFmodSound` [current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L270-L307) | Reads the file into memory and creates the FMOD `Sound` with `Mode.OpenMemory`, or opens a stream when `StreamFromDisk` is set; applies loop points[^ksa-gameaudio] |
| `SpatialSoundData` [current/decomp/KSA/SpatialSoundData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SpatialSoundData.cs#L7-L77) | `VolumeRange` (FMOD min/max distance), `TwoDRange`, `TwoDFloor`, `DistanceFilter`; an element with neither range is a reference to an `Id`[^ksa-spatialsounddata] |
| `ModLibrary.Get<SoundBehavior>` [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1102-L1108) and `ModLibrary.TryGet<T>` [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L776-L798) | `Get` looks an id up in the sound table and throws `NullReferenceException` ("SoundBehavior is null for '<id>'") for an unknown one; `TryGet` returns false instead[^ksa-modlibrary] |
| Core `Sounds.xml` [current/Content/Core/Sounds.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Sounds.xml#L45-L76) | `ExternalChannel` under `Sfx` and the other channel groups a mod's `ChannelId` can name[^ksa-core-sounds] |

Timing: the XML is read and the wav files are loaded at `startup:asset-load`; sounds, spatial data and channel groups
resolve their references in `ModLibrary.PopulateSounds` after `AttachGameData`. From `startup:game-loaded` on, every id
can be looked up. See
[game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and
[the XML load order](../../../assets/asset-xml/mod-xml-load-order/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: One-shot sounds with random variants and a simulation-speed rule](ship-custom-sounds-as-xml-assets-1.md) | KSAGolf | `xml-asset` | `startup:asset-load` | The smallest useful file; each `Sound` carries its own `SpatialSoundData`, so ranges repeat |
| [2: Looping layers sharing one SpatialSoundData asset](ship-custom-sounds-as-xml-assets-2.md) | KSACars | `xml-asset` | `startup:asset-load` | One named spatial asset for a whole car; looping flag in the file; a file missing from `mod.toml` is silent |
| [3: Per-weapon-class spatial data, priority and pitch variation](ship-custom-sounds-as-xml-assets-3.md) | KSArmory | `xml-asset` | `startup:asset-load` | Tunes how far each class carries and who wins under FMOD's voice limit; the cost is that priorities are a global negotiation with Core's numbers |

The asset files: [src/KSAGolf/KSAGolfSounds.xml](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/KSAGolfSounds.xml) (article 1), [src/KSACars/KSACarsSounds.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsSounds.xml) (article 2),
[src/KSArmory/KSArmorySounds.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmorySounds.xml) (article 3).

Choosing: start from article 1; use article 2 when several sounds describe one source; add article 3's priority and
pitch settings when your sound competes with Core's explosions and engines or repeats very often.

## See also

- [Read the sound type map through the features that use each type](../understand-the-games-sound-and-music-xml-types/overview.md)
- [Add background music for a body with a MusicPlaylist asset](../add-a-music-playlist-for-a-location/overview.md)
- [Play a sound at a world position and hold its spatial channel](../../playback/play-a-sound-at-a-world-position-and-hold-its-channel/overview.md)
- [Crossfade engine loops and play spin-up, loop and tail sounds](../../playback/play-a-spin-up-loop-tail-sound-and-crossfade-engine-loops/overview.md)
- [Keep mod-played sounds sensible under time warp and the player's volume](../../channels/keep-mod-sounds-sensible-under-time-warp/overview.md)
- [Ship content and code in one mod](../../../getting-started/loader/ship-content-and-code-in-one-mod/overview.md)
- [Know which asset XML elements the game accepts and what they load](../../../assets/asset-xml/asset-xml-element-reference/overview.md)

[^ksa-assetbundle]: KSA.AssetBundle (decompiled, build 2026.10.10.5554)
[^ksa-soundreference]: KSA.SoundReference (decompiled, build 2026.10.10.5554)
[^ksa-soundfilereference]: KSA.SoundFileReference (decompiled, build 2026.10.10.5554)
[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
[^ksa-spatialsounddata]: KSA.SpatialSoundData (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-core-sounds]: Core/Sounds.xml (build 2026.10.10.5554)
