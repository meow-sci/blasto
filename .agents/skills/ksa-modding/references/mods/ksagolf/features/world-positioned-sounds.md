---
type: Mod Feature
title: World-positioned sounds
description: Shipping wav files as Sound assets in a mod's XML and playing them at an arbitrary world point through GameAudio.PlaySound, anchored on a nearby vehicle for Doppler and air pressure
mod: ksagolf
category: audio
techniques: [fmod-audio, xml-asset]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [startup:asset-load, frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types:
  - KSA.SoundReference
  - KSA.SoundBehavior
  - KSA.SpatialSoundData
  - KSA.SpatialAudio
  - KSA.GameAudio
  - KSA.ChannelWrapper
  - KSA.ModLibrary
tags: [audio, fmod, sound-xml, spatial-audio, doppler, wav]
---

The putter makes a tap, a soft click or a hard whack depending on how fast the ball leaves the
face, and the ball makes a drop sound when it lands. The six wav files ship inside the mod, are
declared as four `<Sound>` assets, and are played from C# at the ball's position in the world, so
they pan, attenuate with distance and Doppler-shift like any engine sound.

The technique is the general recipe for a mod-simulated object that is not a part: declare the
sound like Core does, look it up by id, and hand `GameAudio.PlaySound` a `SpatialAudio` built from a
real `Astronomical` plus an offset.

## How it works

1. **Declare.** `KSAGolfSounds.xml` (listed in `mod.toml` `assets`) has one `<Sound>` per strike
   class.[^golf-sounds-xml] `<Sound>` maps to `SoundReference`, a `SoundBehavior`.[^ksa-soundreference]

   | Attribute or child | Engine meaning |
   |---|---|
   | `ChannelId="ExternalChannel"` | `SoundReference.ChannelGroupId`; `ExternalChannel` is a Core channel group under `Sfx`[^ksa-core-sounds] |
   | `SimSpeed="OnlyLower"` | `SimSpeedRule.OnlyLower`: pitch follows simulation speed below 1x (floored at 0.25) and is not raised above 1x[^ksa-channelwrapper][^ksa-simspeedrule] |
   | Two `<SoundFile>` children | `SelectionRule` defaults to `Random`, so each play picks one file[^ksa-soundreference] |
   | `<SpatialSoundData><VolumeRange Min Max>` | FMOD 3D min/max distance in metres (`TrySet3dMinMaxDistance`)[^ksa-spatialsounddata] |
   | `<TwoDRange Min="0" Max="0"/>` | Feeds `Apply3dLevel`; with both bounds zero the clamped 3D level is 1 (fully positional) at any distance above zero[^ksa-spatialsounddata] |

2. **Choose.** `Sim/StrikeSound.cs` maps launch speed to an id and a volume: below 0.6 m/s a tap,
   below 2.2 m/s a soft putt, above that a hard putt, each band ramping volume from a floor to 1. A
   landing uses the drop sound scaled by impact speed. It is KSA-free and unit-tested.[^golf-strikesound][^golf-strikesound-tests]

3. **Play.** `Ksa/GolfSounds.Play` resolves the id with `ModLibrary.Get<SoundBehavior>`, builds
   `new SpatialAudio(near, atEcl - PositionEcl(near))` from a live vehicle near the ball (the flown
   kitten), and calls `GameAudio.PlaySound(behavior, spatial, out channel, volume)`.[^golf-golfsounds]
   The `SpatialAudio(Astronomical, double3 offset)` constructor takes the vehicle's position relative
   to the audio camera, adds the offset, copies the vehicle's velocity relative to the camera, and
   samples atmospheric pressure at the camera, so Doppler and thin-air muffling are the engine's
   job.[^ksa-spatialaudio] Because Ego is a pure translation of Ecl, an Ecl difference is a valid Ego
   offset.

## Lifecycle and timing

| Stage | Where |
|---|---|
| Load | `startup:asset-load`; the `<Sound>` assets register in `ModLibrary`'s sound-behaviour table |
| Play | Inside `Golf.Step`, which the mod runs once per frame from whichever of its step hooks claims the frame first: the `StarMapAfterGui` postfix on `Program.OnDrawUiViewports`, the Harmony prefix on `Program.OnFrameCelestials` (UI hidden), or `StarMapAfterOnFrame` (see [Dual-hook frame stepping](dual-hook-frame-stepping.md))[^golf-golf] |

All three are on the main thread, after `PrepareFrame` has applied the frame's physics step and
also after it has dispatched the next one. The workers write their own staging copies until the next
`ApplyVehicleSolvers`, so the main-thread vehicle position used for the anchor holds still for the
rest of the frame. The strike sound fires in
`Golf.Swinging` the frame the club face crosses the ball; the landing sound fires in `Golf.StepBall`
when the ball's impact counter changes.

## Key code

| Path | Role |
|---|---|
| [src/KSAGolf/KSAGolfSounds.xml](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/KSAGolfSounds.xml) | Four `<Sound>` assets, six wav files |
| [src/KSAGolf/Sim/StrikeSound.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/StrikeSound.cs) | Speed bands to sound id and volume |
| [src/KSAGolf/Ksa/GolfSounds.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/GolfSounds.cs) | Lookup and positional play |
| [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs#L638-L643) | Strike and landing call sites |

## Snippets

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

```csharp
// src/KSAGolf/Sim/StrikeSound.cs L15-L27
    public static (string Id, float Volume) ForStrike(double launchSpeed)
    {
        if (launchSpeed < SoftFrom) return (Tap, Loudness(launchSpeed, 0.0, SoftFrom, 0.5f));
        if (launchSpeed < HardFrom) return (Soft, Loudness(launchSpeed, SoftFrom, HardFrom, 0.7f));
        return (Hard, Loudness(launchSpeed, HardFrom, 2.0 * HardFrom, 0.8f));
    }

    public static (string Id, float Volume) ForLanding(double impactSpeed)
        => (Drop, Loudness(impactSpeed, GolfBall.AudibleImpact, 3.0, 0.3f));

    // Rises from the floor at the bottom of a band to full at its top.
    private static float Loudness(double speed, double low, double high, float floor)
        => floor + ((1f - floor) * (float)Math.Clamp((speed - low) / (high - low), 0.0, 1.0));
```

## Game APIs touched

| API | Decomp | Purpose |
|---|---|---|
| `SoundReference` (`<Sound>`): `ChannelId`, `SimSpeed`, `SoundFile`, `SpatialSoundData`, `SelectionRule` | [current/decomp/KSA/SoundReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SoundReference.cs) | The declared sound and how a file is picked |
| `SpatialSoundData.Apply` / `Apply3dLevel` | [current/decomp/KSA/SpatialSoundData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SpatialSoundData.cs) | 3D distance range and 2D/3D blend |
| `SpatialAudio(Astronomical, double3)` | [current/decomp/KSA/SpatialAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SpatialAudio.cs) | Ego position, relative velocity and pressure from a live object plus an offset |
| `GameAudio.PlaySound(SoundBehavior, SpatialAudio, out IChannel?, float, bool, bool)` | [current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs) | Starts the channel; skipped above 10x simulation speed unless `alwaysPlay` |
| `ChannelWrapper.HandleSimSpeedPitch` | [current/decomp/KSA/ChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChannelWrapper.cs) | What `SimSpeed="OnlyLower"` does to pitch |
| `ModLibrary.Get<SoundBehavior>` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs) | Id lookup in the sound-behaviour table |

## Pitfalls and version notes

- `ModLibrary.Get<SoundBehavior>` throws `NullReferenceException` for an unknown id rather than
  returning null, so the `is not { } behavior` test in `GolfSounds` never fires; the `catch` is what
  handles a bad id, and it silences golf audio for the rest of the session after one warning.[^ksa-modlibrary]
- `GameAudio.PlaySound` silently does nothing above 10x simulation speed unless `alwaysPlay` is
  passed; `channel` is then null, which `LastPlayed` reports as "no channel".[^ksa-gameaudio]
- The anchor must be a live vehicle near the sound. An offset from something far away still works
  numerically but the Doppler and pressure then belong to that object.
- The mod's notes say Core's `Sounds.xml` uses a `<SoundBehavior>` element; in build 2026.10.10.5554
  there is no such top-level element in the `AssetBundle` table or in `Core/Sounds.xml`, and
  `SoundBehavior` is only the C# base type of `<Sound>`.[^golf-modding-notes]
- Declarative sound (a `<SoundEvent>` in `<PartGameData>`) is for parts with modules; a
  mod-simulated ball has no part, so the imperative call is the only route.[^golf-modding-notes]

## See also

- [Prop and course asset XML](prop-and-course-asset-xml.md)
- [Putting session with a mouse-swung club](putting-session.md)
- [Golf ball physics in the body-fixed frame](golf-ball-physics.md)
- [Dual-hook frame stepping on simulated time](dual-hook-frame-stepping.md)
- [Declare a SimSpeed rule and let the engine handle time warp](../../../howto/audio/channels/keep-mod-sounds-sensible-under-time-warp/keep-mod-sounds-sensible-under-time-warp-1.md) - how-to article demonstrating this feature
- [Fire a positional sound with GameAudio.PlaySound and SpatialAudio(Astronomical, offset)](../../../howto/audio/playback/play-a-sound-at-a-world-position-and-hold-its-channel/play-a-sound-at-a-world-position-and-hold-its-channel-1.md) - how-to article demonstrating this feature
- [Declare one-shot sounds with random variants and a simulation-speed rule](../../../howto/audio/sound-assets/ship-custom-sounds-as-xml-assets/ship-custom-sounds-as-xml-assets-1.md) - how-to article demonstrating this feature

[^golf-sounds-xml]: src/KSAGolf/KSAGolfSounds.xml
[^golf-golfsounds]: src/KSAGolf/Ksa/GolfSounds.cs
[^golf-strikesound]: src/KSAGolf/Sim/StrikeSound.cs
[^golf-strikesound-tests]: tests/KSAGolf.Tests/StrikeSoundTests.cs
[^golf-golf]: src/KSAGolf/Ksa/Golf.cs
[^golf-modding-notes]: docs/KSA-MODDING-NOTES.md
[^ksa-soundreference]: KSA.SoundReference (decompiled, build 2026.10.10.5554)
[^ksa-spatialsounddata]: KSA.SpatialSoundData (decompiled, build 2026.10.10.5554)
[^ksa-spatialaudio]: KSA.SpatialAudio (decompiled, build 2026.10.10.5554)
[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
[^ksa-channelwrapper]: KSA.ChannelWrapper (decompiled, build 2026.10.10.5554)
[^ksa-simspeedrule]: KSA.SimSpeedRule (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-core-sounds]: Core/Sounds.xml (build 2026.10.10.5554)
