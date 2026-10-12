---
type: How-To
title: Declare one-shot sounds with random variants and a simulation-speed rule
description: Ship wav files as Sound assets with two variants each, a spatial range and SimSpeed OnlyLower, list the XML in mod.toml and look the sounds up by id
category: audio
group: sound-assets
topic: ship-custom-sounds-as-xml-assets
approach: 1
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/world-positioned-sounds]
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
  - KSA.ChannelGroupReference
tags: [audio, xml, sound-assets, one-shot, random-variants, simspeed]
---

Outcome: four sounds, each with one or two wav files, that you can fetch by id and play at a point in the world, with
a random variant per play and pitch that follows slow motion. Prerequisites: a StarMap mod (code mod, see
[create a minimal StarMap mod](../../../getting-started/loader/create-a-minimal-starmap-mod/overview.md)) that is also a
content mod, as in
[ship content and code in one mod](../../../getting-started/loader/ship-content-and-code-in-one-mod/overview.md).
Demonstrated by KSAGolf, which declares a putter strike in three strengths and a ball drop.

## Background: how the game does it

Each `<Sound>` becomes a `SoundReference`. When the file loader runs, `SoundFileReference.DoLoad` fails loudly if
the `Path` is empty or the file does not exist (`NullReferenceException` with "Sound file does not exist in ..."),
and otherwise `GameAudio.CreateFmodSound` reads the file into memory and creates the FMOD `Sound`; a `StreamFromDisk`
file would be opened as a stream instead.[^ksa-soundfilereference][^ksa-gameaudio] Population then resolves the channel
group and the spatial data.[^ksa-soundreference] Nothing here is code you write: the XML is the whole declaration.

## Lifecycle and timing

| Moment | What happens | Vocabulary |
|---|---|---|
| Game start | `mod.toml` `assets` XML is read; each `Sound` registers in `ModLibrary`; wav files load | `startup:asset-load` |
| After game data attaches | `PopulateSounds` resolves `ChannelId` and `SpatialSoundData` | `startup:all-mods-loaded` through `startup:game-loaded` |
| Any later frame | `ModLibrary.Get<SoundBehavior>(id)` succeeds | main thread |

Do not look sounds up before the scene exists; do it lazily when you first play, as KSAGolf does. See
[the load order reference](../../../assets/asset-xml/mod-xml-load-order/overview.md).

## Steps

1. **Put the wav files inside the mod folder.** Paths in the XML are relative to the mod (KSAGolf keeps them in
   `Sounds/`). Files must be a format FMOD decodes; mono is the right choice for a sound the engine will place in
   3D (see the gotchas).

2. **List the XML in `mod.toml`.** KSAGolf lists its three asset files one by one, and says why in the file: a file
   left out of the list is never loaded and nothing reports it.

   ```toml
   # src/KSAGolf/mod.toml L5-L8
   # Asset definitions, listed one by one rather than globbed: a file left out of this list is never
   # loaded, and nothing reports it. They sit at the mod root beside Meshes/ and Textures/, so their
   # relative paths resolve the same whether read against the mod root or the XML's own directory.
   assets = ["KSAGolfProps.xml", "KSAGolfSounds.xml", "KSAGolfCourse.xml"]
   ```

3. **Declare each sound.** One `<Sound>` per strike class, with two `<SoundFile>` children for the louder ones so a
   repeat is not the same click:

   ```xml
   <!-- src/KSAGolf/KSAGolfSounds.xml L4-L12 -->
   <Assets>
       <Sound Id="KSAGolf_PuttHard" ChannelId="ExternalChannel" SimSpeed="OnlyLower">
           <SoundFile Path="Sounds/KSAGolf_PuttHard1.wav"/>
           <SoundFile Path="Sounds/KSAGolf_PuttHard2.wav"/>
           <SpatialSoundData>
               <VolumeRange Min="1" Max="120"/>
               <TwoDRange Min="0" Max="0"/>
           </SpatialSoundData>
       </Sound>
   ```

   What the engine does with each attribute:[^ksa-soundreference][^ksa-spatialsounddata][^ksa-simspeedrule]

   | XML | Meaning |
   |---|---|
   | `Id="KSAGolf_PuttHard"` | The key for `ModLibrary.Get<SoundBehavior>`; keep it unique across mods (prefix it with the mod name) |
   | `ChannelId="ExternalChannel"` | A Core channel group under `Sfx`, so the player's Sfx slider applies and the external reverb/low-pass chain is used[^ksa-core-sounds] |
   | `SimSpeed="OnlyLower"` | Pitch follows simulation speed below 1x (floored at 0.25) and is never raised above 1x |
   | two `SoundFile` | `SelectionRule` defaults to `Random`, so each play picks one; with one file it is always that file |
   | `VolumeRange Min Max` | FMOD 3D min and max distance in metres (`Channel.TrySet3dMinMaxDistance`): attenuation starts at `Min` and stops growing at `Max`; the game sets FMOD's roll-off scale to 0.1 |
   | `TwoDRange Min="0" Max="0"` | The 2D/3D blend; with both bounds zero the 3D level clamps to 1 at any distance above zero, so the sound is fully positional |

4. **Look the sound up and play it.** The lookup throws for an unknown id, so keep it in a `try`:

   ```csharp
   // src/KSAGolf/Ksa/GolfSounds.cs L18-L33
   public static void Play((string Id, float Volume) sound, Vehicle near, double3 atEcl)
   {
       try
       {
           if (!KsaWorld.IsAlive(near) || ModLibrary.Get<SoundBehavior>(sound.Id) is not { } behavior) return;

           GameAudio.PlaySound(behavior, new SpatialAudio(near, atEcl - KsaWorld.PositionEcl(near)), out IChannel? channel, sound.Volume);
           LastPlayed = $"{sound.Id} at {sound.Volume:F2}{(channel is null ? ", no channel" : "")}";
       }
       catch (Exception e)
       {
           if (_warned) return;
           _warned = true;
           Log.Warn($"could not play {sound.Id}, so golf is silent this session: {e.Message}");
       }
   }
   ```

   The play call and the `SpatialAudio` it takes are explained in
   [play a sound at a world position](../../playback/play-a-sound-at-a-world-position-and-hold-its-channel/play-a-sound-at-a-world-position-and-hold-its-channel-1.md).

5. **Check the result in the log.** A bad `Path` stops that file's load with the "Sound file does not exist in
   '<path>'" message from `SoundFileReference.DoLoad`; a bad id shows up as the mod's own "could not play ..."
   warning, once.

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/KSAGolfSounds.xml](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/KSAGolfSounds.xml) | Four `Sound` assets, six wav files |
| [src/KSAGolf/mod.toml](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/mod.toml) | Lists the asset XML files |
| [src/KSAGolf/Ksa/GolfSounds.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/GolfSounds.cs) | Lookup and positional play with a once-only warning |

## Variations and gotchas

- **`Get` throws, it does not return null.** `ModLibrary.Get<SoundBehavior>` raises `NullReferenceException` for
  an unregistered id, so KSAGolf's `is not { } behavior` test never fires and the `catch` is what handles a typo (it
  silences golf audio for the session after one warning).[^ksa-modlibrary] Use `ModLibrary.TryGet<SoundBehavior>`
  when you want a boolean.
- **High simulation speed.** `GameAudio.PlaySound` returns no channel above 10x unless `alwaysPlay`, and the engine
  stops channels above 10x unless `SimSpeed="Ignore"`; see
  [keep mod sounds sensible under time warp](../../channels/keep-mod-sounds-sensible-under-time-warp/overview.md).[^ksa-channelwrapper]
- **Variants only help with more than one file.** `SelectionRule` has `Random` (default), `Sequential` and
  `Shuffle`; `Shuffle` draws from a `ShuffleBag`.
- **Volume and pitch.** `<SoundFile Volume>` is clamped to 0..2 at load; `<Volume>` and `<Pitch>` elements on the
  `Sound` take `Value` or a `Min`/`Max` range drawn per play (see
  [article 3](ship-custom-sounds-as-xml-assets-3.md)).
- **A declaration is not a hook.** Declarative sound for a part uses `<SoundEvent SoundId=...>` in game data; a
  mod-simulated object has no part, so the imperative call is the only route.
- **The mod's notes mention a `<SoundBehavior>` element.** There is none in this build; the XML element is
  `<Sound>`.

## See also

- [Overview of this topic](overview.md)
- [Approach 2: looping layers sharing one SpatialSoundData](ship-custom-sounds-as-xml-assets-2.md)
- [Approach 3: per-weapon-class spatial data, priority and pitch variation](ship-custom-sounds-as-xml-assets-3.md)
- [Read the sound type map through the features that use each type](../understand-the-games-sound-and-music-xml-types/overview.md)
- Mod feature: [world-positioned sounds](../../../../mods/ksagolf/features/world-positioned-sounds.md)

[^ksa-soundfilereference]: KSA.SoundFileReference (decompiled, build 2026.10.10.5554)
[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
[^ksa-soundreference]: KSA.SoundReference (decompiled, build 2026.10.10.5554)
[^ksa-spatialsounddata]: KSA.SpatialSoundData (decompiled, build 2026.10.10.5554)
[^ksa-simspeedrule]: KSA.SimSpeedRule (decompiled, build 2026.10.10.5554)
[^ksa-core-sounds]: Core/Sounds.xml (build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-channelwrapper]: KSA.ChannelWrapper (decompiled, build 2026.10.10.5554)
