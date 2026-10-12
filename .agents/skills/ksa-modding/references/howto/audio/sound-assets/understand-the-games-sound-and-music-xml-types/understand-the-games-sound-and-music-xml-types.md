---
type: How-To
title: Read the sound type map through the features that use each type
description: Walk the Sound, CompoundSound, MultiSound, ClusterSound, SpatialSoundData, ChannelGroup and MusicPlaylist types through real mod and Core asset files, with what each Play returns and what each attribute does
category: audio
group: sound-assets
topic: understand-the-games-sound-and-music-xml-types
difficulty: beginner
prerequisites: [starmap]
demonstrated_by:
  - mods/ksacars/features/engine-sound-crossfade
  - mods/ksacars/features/rocket-sound-without-engine
  - mods/ksarmory/features/spatial-gun-and-motor-sounds
  - mods/ksarmory/features/burst-sound-at-blast-arrival
  - mods/unscience/features/byo-music-vessel-3d-audio
techniques: [xml-asset]
hooks: [xml Assets root listed in mod.toml assets]
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

Outcome: you can read any `<Sound>`, `<CompoundSound>`, `<MultiSound>`, `<ClusterSound>`, `<SpatialSoundData>`,
`<ChannelGroup>` or `<MusicPlaylist>` in Core's `Sounds.xml` or a mod, say which type it becomes, what `Play`
returns for it, and which attributes the engine reads. Prerequisite: a mod whose `mod.toml` lists an asset XML (see
[the mod.toml reference](../../../getting-started/loader/mod-toml-reference/overview.md)). The examples come from
KSArmory and KSACars asset files, BYO Music's development asset, and Core.

## Background: how the game does it

All the types derive from `SerializedId`, so each has an `Id` and can be a definition (it carries content) or a
reference (it names an existing definition). The rule differs per type:[^ksa-soundreference][^ksa-compoundsound][^ksa-spatialsounddata]

| Type | It is a reference when | `Get()` / `Populate()` resolves with |
|---|---|---|
| `SoundReference` | it has no `SoundFile` or `MusicFile` children | `ModLibrary.Get<SoundReference>(Id)` |
| `CompoundSound` | it has neither a `Sound` nor a `StartSound` child | `ModLibrary.Get<CompoundSound>(Id)` |
| `MultiSound` / `ClusterSound` | `Sounds.Count == 0` | `ModLibrary.Get<MultiSound>` / `<ClusterSound>` |
| `SpatialSoundData` | it has neither `VolumeRange` nor `TwoDRange` | `ModLibrary.Get<SpatialSoundData>(Id)` |
| `ChannelGroupReference` | never: `OnDataLoad` throws `NotImplementedException` for a reference | not applicable; refer to a group by `ChannelId` |

That is why `<StartSound Id="DefaultEngineStart" />` inside Core's `DefaultEngineSoundBehavior` is cheap to write: it
is a reference to a `Sound` declared elsewhere. A definition registers itself in `OnDataLoad` (`ModLibrary.Register`),
and a lookup of an id that was never registered throws `NullReferenceException` rather than returning null.[^ksa-modlibrary]

What `Play` returns differs by type, which matters for the `IChannel` your code holds:[^ksa-compoundsound][^ksa-multisound][^ksa-soundreference]

| Type | `Play` creates | You hold |
|---|---|---|
| `SoundReference` | One FMOD channel, started paused, volume and pitch applied, then unpaused unless `startPaused`; registered in `GameAudio.ActiveChannels` | `ChannelWrapper` |
| `CompoundSound` | `StartSound` and `Sound` play now; `StopSound` is started **paused** | `CompoundChannelWrapper`: `Stop()` stops start and loop and un-pauses the stop sound; `Stop(stopImmediate: true)` cuts all three |
| `MultiSound` | Every child plays | `MultiChannelWrapper` over the children |
| `ClusterSound` | Every child plays | `ClusterChannelWrapper`, whose position is the average of the channels subscribed to it |
| `MusicPlayList.PlayMusic` | One music channel, delayed on the DSP clock | `ChannelWrapper` (not through `Play`) |

## Lifecycle and timing

Declarations are read at `startup:asset-load` and populated once the scene's game data is attached; there is no code
of yours in between unless you patch (see
[mod XML load order](../../../assets/asset-xml/mod-xml-load-order/overview.md)).[^ksa-modlibrary] Looking a sound
up from mod code is safe from `startup:game-loaded` on. Playing is a main-thread, per-frame concern; the other audio
topics cover where. The engine applies fades, parameters, pitch and volume once a frame in
`GameAudio.UpdateAudio`, called from `PostRender` (`frame:post-render`).[^ksa-gameaudio] Frame order:
[game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Start from the file, not the class.** Core's `Sounds.xml` is the only documentation of the format; both
   KSArmory's and KSACars's asset files say so in their header comments.[^ksarmory-ksarmorysounds][^ksacars-ksacarssounds]
   An `Assets` root holds the elements the game maps in `AssetBundle` (see the
   [element reference](../../../assets/asset-xml/asset-xml-element-reference/overview.md)).

2. **Read a `Sound`.** KSACars declares a `SpatialSoundData` once and several `Sound`s that reference it:

   ```xml
   <!-- src/KSACars/KSACarsSounds.xml L15-L30 -->
   <SpatialSoundData Id="KSACarsBuggySpatialData">
       <VolumeRange Min="6" Max="900" />
       <TwoDRange Min="2" Max="8" />
   </SpatialSoundData>

   <Sound Id="KSACarsBuggyStart" ChannelId="ExternalChannel" Priority="115">
       <SoundFile Path="Sounds/KSACars_Buggy_Start.wav" Volume="1.0"/>
       <SpatialSoundData Id="KSACarsBuggySpatialData" />
   </Sound>

   <Sound Id="KSACarsBuggyIdle" ChannelId="ExternalChannel" Priority="115">
       <SoundFile Path="Sounds/KSACars_Buggy_Idle.wav" Volume="1.0">
           <Loop />
       </SoundFile>
       <SpatialSoundData Id="KSACarsBuggySpatialData" />
   </Sound>
   ```

3. **Know the `Sound` attributes and children.** From `SoundReference` and `SoundFileReference`:[^ksa-soundreference][^ksa-soundfilereference][^ksa-simspeedrule]

   | Attribute or child | Meaning | Default |
   |---|---|---|
   | `ChannelId` | Channel group the sound plays on (`ChannelGroupReference` id) | `Master` |
   | `Priority` | FMOD channel priority; lower wins when voices run out | 128 |
   | `SimSpeed` | `SimSpeedRule`: `Ignore`, `All`, `OnlyLower`, `OnlyHigher` (`None` is also an enum value) | `All` |
   | `SelectionRule` | With more than one file: `Random`, `Sequential`, `Shuffle`; one file is always that file | `Random` |
   | `SoundFile` / `MusicFile` | `Path` relative to the mod; `TwoD`, `StreamFromDisk`, `Volume` (clamped to 0..2 at load), `Loop` | 3D, in memory, volume 1, no loop |
   | `Loop` | Child of the file: loop mode on, optional `Start` and `End` points, `RandomStartTime` | absent means one shot |
   | `SpatialSoundData` | A definition or an `Id` reference | `DefaultSpatialSoundData` |
   | `SoundGroup` | FMOD sound group with a voice limit | none |
   | `FadeIn` / `FadeOut` | `Time` in seconds | `0.2` when the element is empty |
   | `Pitch` / `Volume` | `Value`, or `Min` and `Max` for a random draw per play | 1 |
   | `Param` | `Type` (`Volume`, `Pitch`, `Lpf`, `Hpf`) or a channel-group DSP `Index`, plus `Id`, `InMin`, `InMax`, `OutMin`, `OutMax`, `Mode`, `Rule`; discarded with an error log when a bound or the `Id` is missing[^ksa-soundparameterdata] | none |

   `SimSpeed` and the 10x cutoff are covered in
   [keep mod sounds sensible under time warp](../../channels/keep-mod-sounds-sensible-under-time-warp/overview.md);
   `Param` in [drive a sound's Param curves](../../playback/drive-sound-parameters-from-code/overview.md).

4. **Read `SpatialSoundData`.** `VolumeRange` feeds FMOD's 3D min/max distance (metres); `TwoDRange` blends from 2D
   to 3D between its bounds, with `TwoDFloor` as a lower limit; with no `TwoDRange` the sound is fully 3D;
   `DistanceFilter` (default true) keeps the engine's distance roll-off filters on. Core's presets include
   `DefaultSpatialSoundData` (1 to 10000 m), `EngineSpatialData` and the two `EngineClusterSpatialData*` sets (those
   with `DistanceFilter="false"`):[^ksa-spatialsounddata]

   ```xml
   <!-- current/Content/Core/Sounds.xml L3-L22 -->
   <SpatialSoundData Id="DefaultSpatialSoundData">
       <VolumeRange Min="1" Max="10000" />
       <TwoDRange Min="0" Max="0" />
   </SpatialSoundData>
   <SpatialSoundData Id="WindSpatialData">
       <VolumeRange Min="5" Max="10000" />
       <TwoDRange Min="5" Max="150" />
   </SpatialSoundData>
   <SpatialSoundData Id="EngineSpatialData">
       <VolumeRange Min="3" Max="20000" />
       <TwoDRange Min="1" Max="5" />
   </SpatialSoundData>
   <SpatialSoundData Id="EngineClusterSpatialDataFar" DistanceFilter="false">
       <VolumeRange Min="250" Max="20000" />
       <TwoDRange Min="20" Max="50" />
   </SpatialSoundData>
   <SpatialSoundData Id="EngineClusterSpatialDataMid" DistanceFilter="false">
       <VolumeRange Min="30" Max="20000" />
       <TwoDRange Min="20" Max="50" />
   </SpatialSoundData>
   ```

5. **Read a `CompoundSound`.** KSArmory's cannon is the same shape as Core's engines: a start, a loop and a tail, each
   an ordinary `Sound` referenced by id:

   ```xml
   <!-- src/KSArmory/KSArmorySounds.xml L61-L65 -->
   <CompoundSound Id="KSArmoryCannon">
       <StartSound Id="KSArmoryCannonSpin" />
       <Sound Id="KSArmoryCannonLoop" />
       <StopSound Id="KSArmoryCannonTail" />
   </CompoundSound>
   ```

   `Play` returns a `CompoundChannelWrapper`; `Stop()` lets the tail play. See
   [spin-up, loop and tail](../../playback/play-a-spin-up-loop-tail-sound-and-crossfade-engine-loops/overview.md).

6. **Read `MultiSound` and `ClusterSound`.** A `MultiSound` is a list of `Sound` and `CompoundSound` children that
   all start together; Core's `ExplosionBig` is one (KSArmory plays it and gets a `MultiChannelWrapper`, see
   [the burst feature](../../../../mods/ksarmory/features/burst-sound-at-blast-arrival.md)). A `ClusterSound` has the
   same children but is meant to be shared: Core's `DefaultEngineSoundBehavior` carries a `<ClusterSound
   Id="DefaultEngineCluster"/>` child, and `GameAudio.PlaySound(SoundEvent, ...)` subscribes each engine's channel to
   one shared cluster channel.[^ksa-gameaudio]

7. **Read the channel group tree.** `GameAudio.OnApplicationStart` creates four groups (`Master`, `Sfx`, `Ui`,
   `Music`) and registers each as a `ChannelGroupReference`; `PopulateSounds` parents every group without an explicit
   `ParentId` under `Master`. Core adds `ExternalChannel` and `InternalChannel` under `Sfx`, then
   `EnginesChannel`, `AmbienceChannel` and `ExtReverb` under `ExternalChannel`, each with a DSP chain (`Reverb`,
   `Compressor`, `MultibandEq`, `Delay`, `ConvolutionReverb`, `Send`, `Return`):[^ksa-gameaudio][^ksa-channelgroupreference]

   ```xml
   <!-- current/Content/Core/Sounds.xml L67-L74 -->
   <ChannelGroup Id="InternalChannel" ParentId="Sfx"/>
   <ChannelGroup Id="EnginesChannel" ParentId="ExternalChannel">
       <Compressor Threshold="-12" Ratio="10" Attack="0.3" Release="10" Linked="true"/>
       <Send TargetId="ExtReverbReturn" Level="0.1"/>
   </ChannelGroup>
   <ChannelGroup Id="AmbienceChannel" ParentId="ExternalChannel" Volume="0.9">
       <Send TargetId="ExtReverbReturn" Level="0.01"/>
   </ChannelGroup>
   ```

   A mod's `Sound` almost always names `ExternalChannel` (outside sounds: its DSP chain reads the `Iva` and `Pressure` parameters) or `InternalChannel`; the player's Sfx slider reaches both through `Sfx`.

8. **Read a `MusicPlaylist`.** It is a `SoundReference` with `LocationId` and `MusicFile` children, picked by
   `LocationMusicPlayer`. The shape, the lookup and the one development-only mod example are in
   [add background music for a body](../add-a-music-playlist-for-a-location/overview.md).[^ksa-musicplaylist]

## Complete example

| File | Role |
|---|---|
| [src/KSACars/KSACarsSounds.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsSounds.xml) | `SpatialSoundData` declared once, four `Sound`s per car referencing it |
| [src/KSArmory/KSArmorySounds.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmorySounds.xml) | `Sound`, shared spatial data, `CompoundSound`, a `Pitch` range |
| [byo-music/Assets.xml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music/Assets.xml) | `MultiSound` and `MusicPlaylist` (development host only) |
| [current/Content/Core/Sounds.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Sounds.xml) | Every type, including the channel group tree and Core's playlists |

## Variations and gotchas

- **`ChannelId`, not `Channel`.** The attribute is `ChannelId` (`SoundReference.ChannelGroupId`). BYO Music's
  development `Assets.xml` writes `Channel="Sfx"` and `Channel="Music"`; `SoundReference` has no such attribute, so
  an XML serializer that ignores unknown attributes leaves the group unset and the sound plays on `Master` (inferred
  from the type, not run in game).[^unscience-assets]
- **A `<SoundBehavior>` element does not exist.** `SoundBehavior` is the C# base class; XML says `Sound`,
  `CompoundSound`, `MultiSound`, `ClusterSound`.
- **Population order matters for groups, not for sounds.** A `Sound` resolves its group in `Populate`
  (`ModLibrary.Get<ChannelGroupReference>(id).ChannelGroup`), after every group has been created in `OnDataLoad`, so
  declaration order inside one XML does not matter. A group parents into the tree later in the same
  `PopulateSounds` pass.[^ksa-modlibrary][^ksa-soundreference]
- **Unknown `ChannelId` throws.** `ModLibrary.Get<ChannelGroupReference>` throws for an id nobody declared, which
  fails during `PopulateSounds`, not at play time.
- **A `MusicPlaylist` is also a `SoundReference`.** Its `OnDataLoad` runs the base registration and then registers it
  as a playlist, so it is in both `AllSoundBehaviours` and `AllMusicPlaylists`, and `ModLibrary.Get<SoundBehavior>`
  finds it.[^ksa-musicplaylist][^ksa-modlibrary]
- **`SoundFileReference.Mode`** is built from `TwoD`, `Loop` and `StreamFromDisk`: `_2d` or `_3d`, `LoopNormal` or
  `LoopOff`, and `NonBlocking` for a stream. A `TwoD` file skips `SetSpatialAudio` entirely.[^ksa-soundfilereference]

## See also

- [Overview of this topic](overview.md)
- [Ship custom sounds with a mod as XML sound assets](../ship-custom-sounds-as-xml-assets/overview.md)
- [Add background music for a body with a MusicPlaylist asset](../add-a-music-playlist-for-a-location/overview.md)
- [Play your own sound files through KSA's FMOD system without blocking](../../channels/play-sound-files-through-the-games-fmod-system/overview.md)
- Mod features: [engine sound from two crossfaded loops](../../../../mods/ksacars/features/engine-sound-crossfade.md), [rocket roar without an engine](../../../../mods/ksacars/features/rocket-sound-without-engine.md), [spatial gun and motor sounds](../../../../mods/ksarmory/features/spatial-gun-and-motor-sounds.md), [BYO Music](../../../../mods/unscience/features/byo-music-vessel-3d-audio.md)

[^ksa-soundreference]: KSA.SoundReference (decompiled, build 2026.10.10.5554)
[^ksa-compoundsound]: KSA.CompoundSound (decompiled, build 2026.10.10.5554)
[^ksa-spatialsounddata]: KSA.SpatialSoundData (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-multisound]: KSA.MultiSound (decompiled, build 2026.10.10.5554)
[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
[^ksarmory-ksarmorysounds]: KSArmory: src/KSArmory/KSArmorySounds.xml
[^ksacars-ksacarssounds]: KSACars: src/KSACars/KSACarsSounds.xml
[^ksa-soundfilereference]: KSA.SoundFileReference (decompiled, build 2026.10.10.5554)
[^ksa-simspeedrule]: KSA.SimSpeedRule (decompiled, build 2026.10.10.5554)
[^ksa-soundparameterdata]: KSA.SoundParameterData (decompiled, build 2026.10.10.5554)
[^ksa-channelgroupreference]: KSA.ChannelGroupReference (decompiled, build 2026.10.10.5554)
[^ksa-musicplaylist]: KSA.MusicPlayList (decompiled, build 2026.10.10.5554)
[^unscience-assets]: unscience: byo-music/Assets.xml
