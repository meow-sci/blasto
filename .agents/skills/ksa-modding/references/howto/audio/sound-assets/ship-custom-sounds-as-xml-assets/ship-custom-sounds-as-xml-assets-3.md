---
type: How-To
title: Tune spatial data, priority and pitch variation per weapon class
description: Declare one SpatialSoundData per class of sound so a big gun carries further than a small one, set Priority below Core's explosions so gun channels survive FMOD's voice limit, and add a Pitch range so a repeated shot is not one sample
category: audio
group: sound-assets
topic: ship-custom-sounds-as-xml-assets
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/spatial-gun-and-motor-sounds]
techniques: [xml-asset]
hooks: [xml Assets root listed in mod.toml assets]
lifecycle: [startup:asset-load, startup:all-mods-loaded]
game_types:
  - KSA.SoundReference
  - KSA.SoundFileReference
  - KSA.SpatialSoundData
  - KSA.CompoundSound
  - KSA.SoundBehavior
  - KSA.AssetBundle
  - KSA.ModLibrary
tags: [audio, xml, sound-assets, priority, pitch, spatial-sound-data, provenance]
---

Outcome: sound assets whose reach, voice priority and variation are chosen per kind of source, using KSArmory's guns
as the model: a 5-inch gun that carries further than a 20 mm cannon, gun channels that stay audible next to Core's
explosions, and a gunshot that is pitched a little differently each time. Prerequisites: articles
[1](ship-custom-sounds-as-xml-assets-1.md) and [2](ship-custom-sounds-as-xml-assets-2.md). Demonstrated by KSArmory.

## Background: how the game does it

- **Spatial data per class.** `SpatialSoundData.Apply` sets FMOD's 3D min and max distance from `VolumeRange` when the
  channel is created, and `Apply3dLevel` blends 2D to 3D by distance from `TwoDRange`. A `SoundReference` points at one
  `SpatialSoundData`, so a class of sound with its own reach needs its own asset.[^ksa-spatialsounddata]
- **Priority.** `ChannelWrapper`'s constructor passes `SoundReference.Priority` to `Channel.TrySetPriority`; the default
  is 128, and in FMOD a lower number is more important.[^ksa-channelwrapper] Core's explosion layers are 123 to 125 and
  its engine cluster layers 125, while the main `DefaultEngine` loop and `DefaultEngineStart` declare no `Priority` and
  sit at the default 128.[^ksa-core-explosionassets][^ksa-core-sounds]
- **Variation.** `SoundReference` keeps `Pitch` and `Volume` as `FloatRangeData` (`Value`, `Min`, `Max`);
  `ChannelWrapper` draws `GenerateValue` for each at channel creation. With `Min >= Max` the draw returns `Value`
  (default 1).[^ksa-floatrangedata][^ksa-soundreference]

## Lifecycle and timing

All declarative: `startup:asset-load` for the XML and the files, `PopulateSounds` for the references. The values are
applied each time a channel is created, on whichever frame your code plays the sound.

## Steps

1. **Give each class its own `SpatialSoundData`.** KSArmory's cannon is "quieter and shorter-ranged than a warhead";
   the 5-inch gun carries further:

   ```xml
   <!-- src/KSArmory/KSArmorySounds.xml L26-L29 -->
   <SpatialSoundData Id="KSArmoryCannonSpatialData">
       <VolumeRange Min="25" Max="6000" />
       <TwoDRange Min="3" Max="14" />
   </SpatialSoundData>
   ```

   ```xml
   <!-- src/KSArmory/KSArmorySounds.xml L71-L74 -->
   <SpatialSoundData Id="KSArmoryMk42SpatialData">
       <VolumeRange Min="60" Max="12000" />
       <TwoDRange Min="4" Max="18" />
   </SpatialSoundData>
   ```

   Ranges are in metres: the Phalanx's `VolumeRange` is 25 to 6000, the Mk 42's is 60 to 12000.

2. **Choose a priority and write the reason down.** The comment at the head of KSArmory's file is the best part of the
   pattern: it states the number, what it competes with, and why.

   ```xml
   <!-- src/KSArmory/KSArmorySounds.xml L14-L20 -->
   <!--
     Every gun sound here is Priority 110, above everything Core plays: its explosions are 123 and
     124, its engines 125, and anything undeclared 128, where lower wins. FMOD mixes only 64 voices
     for real and silences the least important of the rest, so at 128 a gun firing through a string
     of kills went quiet beside the explosions it was causing. There are only a few gun channels at
     once, so the explosions keep every other voice.
   -->
   ```

   The "64 real voices" figure is the mod's statement about FMOD, not something the decomp shows; the decomp shows the
   game initialising FMOD with 1024 channels and passing `Priority` to each channel.[^ksa-gameaudio]

3. **Set the priority on every `Sound` of the class** (the XML has no inheritance), and add a `Pitch` range to
   anything that repeats:

   ```xml
   <!-- src/KSArmory/KSArmorySounds.xml L76-L81 -->
   <!-- One per round, pitched a little each time so forty a minute is not one sample forty times. -->
   <Sound Id="KSArmoryMk42Gunshot" ChannelId="ExternalChannel" Priority="110">
       <SoundFile Path="Sounds/KSArmory_Mk42_Gunshot.wav" Volume="1.0"/>
       <SpatialSoundData Id="KSArmoryMk42SpatialData" />
       <Pitch Min="0.96" Max="1.04" />
   </Sound>
   ```

   `Min="0.96" Max="1.04"` is drawn per channel, so forty shots a minute are not one sample forty times.

4. **Compose with `CompoundSound`.** The start, loop and tail are three ordinary `Sound`s joined by id (see the
   [type map](../understand-the-games-sound-and-music-xml-types/overview.md)); the compound adds no attributes of its
   own, so each `Sound` still needs the `Priority`.

   ```xml
   <!-- src/KSArmory/KSArmorySounds.xml L56-L65 -->
   <Sound Id="KSArmoryCannonTail" ChannelId="ExternalChannel" Priority="110">
       <SoundFile Path="Sounds/KSArmory_Cannon_Tail.wav" Volume="1.0"/>
       <SpatialSoundData Id="KSArmoryCannonSpatialData" />
   </Sound>

   <CompoundSound Id="KSArmoryCannon">
       <StartSound Id="KSArmoryCannonSpin" />
       <Sound Id="KSArmoryCannonLoop" />
       <StopSound Id="KSArmoryCannonTail" />
   </CompoundSound>
   ```

5. **Record where each recording came from.** KSArmory cuts every wav from a source recording with a script, so it can
   be re-cut, and keeps the source's name, link and licence in a table beside the tools:

   ```markdown
   <!-- tools/audio/README.md L1-L12 -->
   # Source recordings

   Recordings the shipped cannon sound is cut from. **Unlike everything else under
   `src/KSArmory/Sounds/`, these are not synthesised**, which is why this folder exists rather than the
   cuts simply appearing in `Sounds/`: a recording has a provenance, and the provenance has to live
   where the next person will find it.

   | File | Source | Licence |
   | --- | --- | --- |
   | `163119__qubodup__navy-mk-15-phalanx-ciws-anti-air-fast-burst.flac` | freesound.org/s/163119/, by qubodup | **CC0** (public domain dedication) |

   CC0 places no condition on redistribution, so this ships inside the MIT archive with nothing to
   ```

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/KSArmorySounds.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmorySounds.xml) | Spatial data per class, `Priority="110"`, `Pitch` range, two `CompoundSound`s |
| [tools/audio/README.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/audio/README.md) | Provenance and licence of the recordings |

## Variations and gotchas

- **Priority is a shared negotiation.** 110 was chosen against Core's 123 to 125; a mod that sets 0 for everything
  starves Core's own sounds. Keep the number as high as it can be and give only the few channels that need it.
- **`Pitch` on a held loop.** The range is drawn once at start; a mod that retunes a loop each frame sets the channel's
  `PitchMultiplier` instead (see [crossfade engine loops](../../playback/play-a-spin-up-loop-tail-sound-and-crossfade-engine-loops/overview.md)).
- **No per-class hearing range beyond `Max`.** The engine's explosion system drops a sound beyond 50 km
  (`MaxAudibleDistanceMeters`); a mod's own sounds have no such cap, so large ranges are your decision.
- **Licensing is part of the asset.** A recording needs a licence that allows redistribution; KSArmory's table records
  the source, the author and the licence for each recording.[^ksarmory-readme]

## See also

- [Overview of this topic](overview.md)
- [Approach 1: one-shot sounds with random variants](ship-custom-sounds-as-xml-assets-1.md)
- [Approach 2: looping layers sharing one SpatialSoundData](ship-custom-sounds-as-xml-assets-2.md)
- [Play a sound at a world position and hold its spatial channel](../../playback/play-a-sound-at-a-world-position-and-hold-its-channel/overview.md)
- Mod feature: [spatial gun and motor sounds](../../../../mods/ksarmory/features/spatial-gun-and-motor-sounds.md)

[^ksa-spatialsounddata]: KSA.SpatialSoundData (decompiled, build 2026.10.10.5554)
[^ksa-channelwrapper]: KSA.ChannelWrapper (decompiled, build 2026.10.10.5554)
[^ksa-core-explosionassets]: Core/ExplosionAssets.xml (build 2026.10.10.5554)
[^ksa-core-sounds]: Core/Sounds.xml (build 2026.10.10.5554)
[^ksa-floatrangedata]: KSA.FloatRangeData (decompiled, build 2026.10.10.5554)
[^ksa-soundreference]: KSA.SoundReference (decompiled, build 2026.10.10.5554)
[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
[^ksarmory-readme]: KSArmory: tools/audio/README.md
