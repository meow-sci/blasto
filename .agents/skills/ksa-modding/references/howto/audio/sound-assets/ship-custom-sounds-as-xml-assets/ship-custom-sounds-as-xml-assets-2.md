---
type: How-To
title: Declare looping layers that share one SpatialSoundData asset
description: Declare a named SpatialSoundData once, reference it from several Sound assets, turn on FMOD loop mode with a Loop element and ship mono wav files so the engine can spatialise them
category: audio
group: sound-assets
topic: ship-custom-sounds-as-xml-assets
approach: 2
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksacars/features/engine-sound-crossfade]
techniques: [xml-asset]
hooks: [xml Assets root listed in mod.toml assets]
lifecycle: [startup:asset-load, startup:all-mods-loaded]
game_types:
  - KSA.SoundReference
  - KSA.SoundFileReference
  - KSA.SpatialSoundData
  - KSA.SoundBehavior
  - KSA.AssetBundle
  - KSA.ModLibrary
  - KSA.LoopData
tags: [audio, xml, sound-assets, loop, spatial-sound-data, mono]
---

Outcome: a set of sounds for one source (a car's start, idle loop, load loop and stop) that share a single declared
spatial range, with the two engine layers set to loop in FMOD. Prerequisites: a StarMap content-and-code mod, and
article 1's basics ([one-shot sounds](ship-custom-sounds-as-xml-assets-1.md)). Demonstrated by KSACars, which ships two
cars' worth of sounds this way.

## Background: how the game does it

`SpatialSoundData` is its own registered asset type. A `<SpatialSoundData Id="X"><VolumeRange .../>...</SpatialSoundData>`
is a definition; a bare `<SpatialSoundData Id="X" />` inside a `Sound` is a reference, because an element with neither
`VolumeRange` nor `TwoDRange` counts as one. References resolve in `SoundReference.Populate` (`SpatialSoundData.Populate`
returns `ModLibrary.Get<SpatialSoundData>(Id)`), so the definition may sit before or after the sounds that use
it.[^ksa-spatialsounddata][^ksa-soundreference]

Looping is a property of the file element: a `<Loop />` child makes `SoundFileReference.Mode` include
`Mode.LoopNormal` (instead of `LoopOff`), and `GameAudio.CreateFmodSound` applies the loop points (`Start`/`End`, or
the whole file when none is given) once the FMOD sound exists.[^ksa-soundfilereference][^ksa-loopdata][^ksa-gameaudio]

## Lifecycle and timing

Same as article 1: the XML is read at `startup:asset-load`, references resolve in `PopulateSounds`, and playback code
looks the ids up later (`startup:game-loaded` onward). The playing side (re-pitching two loops by RPM every frame) is
[crossfade engine loops](../../playback/play-a-spin-up-loop-tail-sound-and-crossfade-engine-loops/overview.md).

## Steps

1. **Cut the audio as mono.** KSACars says why in its file header: the engine spatialises the source, and a stereo
   file arrives already mixed.[^ksacars-ksacarssounds]

2. **List the XML in `mod.toml`.** KSACars lists it among its other asset files; a file missing from the list is
   never loaded, and every id in it then fails to resolve (the mod warns once per id from its play code).

   ```toml
   # src/KSACars/mod.toml L11-L15
   assets = [
       "KSACarsAssets.xml",
       "KSACarsGameData.xml",
       "KSACarsSounds.xml",
   ]
   ```

3. **Declare the shared spatial data once.** The range is the car's, not any one sound's:

   ```xml
   <!-- src/KSACars/KSACarsSounds.xml L15-L18 -->
   <SpatialSoundData Id="KSACarsBuggySpatialData">
       <VolumeRange Min="6" Max="900" />
       <TwoDRange Min="2" Max="8" />
   </SpatialSoundData>
   ```

4. **Declare the sounds that use it.** A one-shot start and stop, and two loops marked with `<Loop />`. `Priority="115"`
   puts them ahead of an undeclared sound (128) when FMOD runs short of voices; `Volume="1.0"` is the per-file gain
   (clamped to 0..2):

   ```xml
   <!-- src/KSACars/KSACarsSounds.xml L20-L30 -->
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

   Declare no `<Pitch>` and no `<Param>` when the code will set pitch and volume itself each frame: the mod's
   `BuggySound` writes `PitchMultiplier` and `VolumeMultiplier` on the channel instead.

5. **Look up and play.** The play side resolves each id with `ModLibrary.Get<SoundBehavior>` and catches the
   `NullReferenceException` it throws for an id that is missing from `mod.toml` or misspelt, then warns once per id so
   the failure is one log line, not one per frame:

   ```csharp
   // src/KSACars/Ksa/BuggySound.cs L138-L155
   private IChannel? Play(string id, SpatialAudio spatial, float volume)
   {
       try
       {
           if (ModLibrary.Get<SoundBehavior>(id) is not { } sound)
           {
               WarnOnce(id, $"buggy sound '{id}' does not resolve; the engine will be silent");
               return null;
           }
           sound.Play(spatial, volume, out IChannel? channel);
           return channel;
       }
       catch (Exception ex)
       {
           WarnOnce(id, $"buggy sound '{id}' failed: {ex.Message}");
           return null;
       }
   }
   ```

## Complete example

| File | Role |
|---|---|
| [src/KSACars/KSACarsSounds.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsSounds.xml) | Two cars' sounds, each with one shared `SpatialSoundData` |
| [src/KSACars/mod.toml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/mod.toml) | Asset list |
| [src/KSACars/Ksa/BuggySound.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/BuggySound.cs) | Lookup with a once-per-id warning |

## Variations and gotchas

- **`<Loop />` loops the whole file.** Add `<Start>` and `<End>` children (each with `Value` and a `Unit` of `Pcm`, `Ms` or `PcmBytes`) for loop points,
  and `RandomStartTime="true"` to start at a random place inside the loop bounds so two engines do not phase together.[^ksa-loopdata][^ksa-loopbounds]
- **A sample that must loop seamlessly has to be cut for it.** KSACars cuts its loops from recordings with scripts so they
  can be re-cut; see article 3 for the provenance habit.
- **Ranges are per car, not global.** The Eldorado has its own `KSACarsEldoSpatialData` with a larger `VolumeRange`.
- **Looping channels outlive their source unless you stop them.** Declaring the loop starts a channel that never ends;
  see [release channels at scene change and exit](../../channels/release-sound-channels-at-scene-change-and-exit/overview.md).
- **Missing ids are quiet.** The XML declaration cannot report that the file was left out of `mod.toml`; the lookup
  throws at play time. Test every id once at startup if silent failure matters.

## See also

- [Overview of this topic](overview.md)
- [Approach 1: one-shot sounds with random variants](ship-custom-sounds-as-xml-assets-1.md)
- [Approach 3: per-weapon-class spatial data, priority and pitch variation](ship-custom-sounds-as-xml-assets-3.md)
- [Crossfade engine loops and play spin-up, loop and tail sounds](../../playback/play-a-spin-up-loop-tail-sound-and-crossfade-engine-loops/overview.md)
- Mod feature: [engine sound from two crossfaded loops](../../../../mods/ksacars/features/engine-sound-crossfade.md)

[^ksa-spatialsounddata]: KSA.SpatialSoundData (decompiled, build 2026.10.10.5554)
[^ksa-soundreference]: KSA.SoundReference (decompiled, build 2026.10.10.5554)
[^ksa-soundfilereference]: KSA.SoundFileReference (decompiled, build 2026.10.10.5554)
[^ksa-loopdata]: KSA.LoopData (decompiled, build 2026.10.10.5554)
[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
[^ksacars-ksacarssounds]: KSACars: src/KSACars/KSACarsSounds.xml
[^ksa-loopbounds]: KSA.LoopBounds (decompiled, build 2026.10.10.5554)
