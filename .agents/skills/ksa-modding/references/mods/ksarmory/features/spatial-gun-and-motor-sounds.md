---
type: Mod Feature
title: Spatialised gun and rocket-motor sounds driven by held FMOD channels
description: Gun fire is an XML CompoundSound (spin-up, loop, tail) played once per firing system and moved every frame, a slow gun gets one pitched gunshot per round, and each burning mod-simulated rocket gets Core's engine loop with its Throttle parameter pinned open
mod: ksarmory
category: audio
techniques: [fmod-audio, xml-asset, held-channel, compound-sound, sound-priority, recording-cut-scripts]
hooks: [starmap StarMapAfterGui (via StepOnce), starmap StarMapUnload]
lifecycle: [startup:asset-load, frame:draw-ui, event:starmap-unload]
game_types:
  - KSA.SoundBehavior
  - KSA.CompoundSound
  - KSA.CompoundChannelWrapper
  - KSA.SoundReference
  - KSA.SpatialSoundData
  - KSA.SpatialAudio
  - KSA.IChannel
  - KSA.GameAudio
  - KSA.PhysicalAtmosphereReference
  - KSA.Camera
  - KSA.ModLibrary
tags: [fmod, sound, spatial-audio, compound-sound, priority, xml]
---

KSArmory's guns and rockets are mod-simulated (no KSA `Vehicle`, no engine module), so they get no
audio from the engine. The mod plays KSA sound assets itself: it looks a `SoundBehavior` up in
`ModLibrary` by Id, calls `Play(SpatialAudio, volume, out IChannel)`, keeps the returned channel,
and moves it every frame with `IChannel.SetSpatialAudio` until it stops the channel.[^ksa-modlibrary][^ksa-ichannel][^gun-sound][^motor-sound][^sound-channels]
The same lookup resolves a mod-declared sound and a Core one, so the motor borrows Core's
`DefaultEngineSoundBehavior` until the mod ships its own sample.[^motor-sound]

## How it works

### Declaring sounds (`KSArmorySounds.xml`, listed in `mod.toml` `assets`)

| Asset | Purpose |
|---|---|
| `SpatialSoundData` `KSArmoryCannonSpatialData` / `KSArmoryMk42SpatialData` | `VolumeRange` and `TwoDRange` per weapon class (a 5-inch gun carries further than a 20 mm)[^sounds-xml] |
| `Sound` `KSArmoryCannonSpin`, `...Loop` (with `<Loop />`), `...Tail` | The three cuts of one recording, mono so FMOD can spatialise them, `ChannelId="ExternalChannel"`, `Priority="110"`[^sounds-xml] |
| `CompoundSound` `KSArmoryCannon` | `StartSound` spin-up, `Sound` loop, `StopSound` tail; same shape as Core's `DefaultEngineSoundBehavior`[^sounds-xml][^ksa-core-sounds] |
| `Sound` `KSArmoryMk42Gunshot` with `<Pitch Min="0.96" Max="1.04" />` | One shot per round, varied so 40 rounds a minute are not one sample repeated[^sounds-xml] |
| `CompoundSound` `KSArmoryM197` | Loop plus tail, no spin-up (the recording has none)[^sounds-xml] |

`CompoundSound.Play` starts the start and loop sounds and starts the stop sound **paused**;
`CompoundChannelWrapper.Stop()` stops the first two and unpauses the stop sound, which is how a
held trigger ends in a tail without the mod scheduling anything.[^ksa-compoundsound][^ksa-compoundchannel]

**Priority 110** (lower wins) puts gun channels above everything Core plays: explosion sounds are
123 and 124, engines 125, and an undeclared `SoundReference.Priority` defaults to 128.[^sounds-xml][^ksa-explosion-assets][^ksa-core-sounds][^ksa-soundreference]
KSA initialises FMOD with `System.Init(1024, ...)` and never sets the software-voice count, so the
real mix is FMOD's default voice limit (64 according to the mod's comment); at 128 a gun went silent
beside the explosions it was causing.[^ksa-gameaudio][^sounds-xml]

### Playing them

| Class | Unit | Behaviour |
|---|---|---|
| `GunSound` | One channel **per firing system** | A Phalanx fires 75 rounds a second, a rate the ear hears as pitch, so a per-round one-shot would model the wrong thing and cost 75 voices a second. The loop is held while `GunsFiring`, moved each frame from `Camera.GetPositionEgo/GetVelocityEgo(platform)`[^ksa-camera], and re-pitched toward each gun's rate (`PitchMultiplier = clamp(rpm / CannonReferenceRpm, 0.8, 1.25)`, then `ApplyParameters()`) when the gun shares the recorded Phalanx sample[^gun-sound][^config] |
| `GunSound.PlayGunshot` | One shot per round fired | Only for a profile naming `GunshotSoundId` (the Mk 42); a counter of `GunShotsFired` already heard avoids replaying rounds fired before anyone listened[^gun-sound][^launcher-profile] |
| `MotorSound` | One channel **per burning round** | Position is the platform's ego position plus the round's own offset (the epoch-consistent quantity), velocity likewise; on start it sets the `Throttle` parameter to 1, because Core's engine loop maps throttle 0 to 0.1x volume and 0.5x pitch[^motor-sound][^ksa-core-sounds] |
| `SoundChannels` | Shared helpers | Listener = `GameAudio.GetAudioCamera()` (the main viewport's base camera); pressure = `PhysicalAtmosphereReference.GetAtmosphericPressure(listener)`, the listener's air rather than the source's; `TryMove` returns false once `IsPlaying()` is false so the caller restarts; `Cut` swallows a channel the engine already reclaimed[^sound-channels][^ksa-spatialaudio][^ksa-gameaudio][^ksa-atmosphere] |

Every channel is owned by a dictionary keyed on the system or round, and swept each frame against
the live roster, so a craft destroyed mid-burst does not leave a loop playing for the session.[^gun-sound][^motor-sound]

## Lifecycle and timing

- **Assets: `startup:asset-load`**, from `mod.toml`; nothing is registered from code.
- **Per frame: `frame:draw-ui`.** `_motors.Update` and `_gunSound.Update` run at the tail of
  `StepSimulation` (KSArmoryMod.cs L871-L881), **after** the rounds were stepped, so a motor is heard
  where its round is now; `Sweep` follows at L968-L972. `StepSimulation` runs inside `StepOnce`,
  normally from the `[StarMapAfterGui]` postfix, or the pre-render / after-frame fallbacks when the
  HUD is hidden.[^entry] FMOD calls are main-thread safe here; the positions come from the same
  pass that integrated the rounds, so they cannot lag a frame.[^motor-sound]
- **Unload: `[StarMapUnload]`** calls `StopAll()` on both, because pooled channels belong to the
  game and nothing else returns them (L1009-L1010).[^entry]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/KSArmorySounds.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmorySounds.xml) | Sound, spatial data and compound sound declarations |
| [src/KSArmory/Ksa/GunSound.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/GunSound.cs#L37-L188) | Held loop per system, gunshots, pitch retune |
| [src/KSArmory/Ksa/MotorSound.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/MotorSound.cs#L52-L181) | Channel per burning round, `Throttle` parameter |
| [src/KSArmory/Ksa/SoundChannels.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/SoundChannels.cs) | Listener, pressure, guarded move and stop |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L866-L972) | Update after the step, sweep, stop on unload |
| [tools/cut-cannon.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/cut-cannon.py) | Cuts spin, loop and tail from one recording at envelope-measured points |
| [tools/audio/README.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/audio/README.md) | Provenance and licence of each recording |

## Snippets

```xml
<!-- src/KSArmory/KSArmorySounds.xml L44-L65 -->
    <Sound Id="KSArmoryCannonSpin" ChannelId="ExternalChannel" Priority="110">
        <SoundFile Path="Sounds/KSArmory_Cannon_Spin.wav" Volume="1.0"/>
        <SpatialSoundData Id="KSArmoryCannonSpatialData" />
    </Sound>

    <Sound Id="KSArmoryCannonLoop" ChannelId="ExternalChannel" Priority="110">
        <SoundFile Path="Sounds/KSArmory_Cannon_Loop.wav" Volume="1.0">
            <Loop />
        </SoundFile>
        <SpatialSoundData Id="KSArmoryCannonSpatialData" />
    </Sound>

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

```csharp
// src/KSArmory/Ksa/GunSound.cs L138-L163
    private IChannel? Start(SpatialAudio spatial, IEffectSource system)
    {
        string id = system.Profile.GunSoundId ?? CannonId;
        try
        {
            if (ModLibrary.Get<SoundBehavior>(id) is not { } sound)
            {
                WarnOnce(id, $"cannon sound '{id}' does not resolve; the gun will be silent");
                return null;
            }

            sound.Play(spatial, _config.CannonVolume, out IChannel? channel);

            if (channel is not null && system.Profile.GunSoundId is null
                && _config.CannonReferenceRpm > 0f && system.Profile.GunRoundsPerMinute > 0f)
            {
                // Clamped, because the sample is a recording of one real gun rather than a
                // synthesised pulse train. Pitch moves the cycle and the timbre together, so a
                // large shift does not give a slower gun, it gives the same gun played wrong.
                // Within a quarter either way it still reads as a cannon of a different rate.
                channel.PitchMultiplier = Math.Clamp(
                    system.Profile.GunRoundsPerMinute / _config.CannonReferenceRpm, 0.8f, 1.25f);
                channel.ApplyParameters();
            }

            return channel;
```

```csharp
// src/KSArmory/Ksa/MotorSound.cs L116-L137
    private void Follow(IProjectile round, IRoundsInFlight system,
                        double3 platformEgo, double3 platformVelEgo, double pressure)
    {
        // Built from the round's offset from its platform, never from an absolute Ecl position
        // converted on its own. The offset is the one quantity the mod already keeps epoch-clean,
        // and at 29.8 km/s the alternative is audibly in the wrong place.
        double3 posEgo = platformEgo + round.OffsetFromPlatform;
        double3 velEgo = platformVelEgo + round.VelocityLocal;

        if (!Vec.IsFinite(posEgo) || !Vec.IsFinite(velEgo)) return;

        var spatial = new SpatialAudio(posEgo, velEgo, pressure);

        if (_burning.TryGetValue(round, out (IRoundsInFlight Owner, IChannel Channel) held))
        {
            if (SoundChannels.TryMove(held.Channel, spatial)) return;

            _burning.Remove(round);
        }

        if (Start(spatial, _config) is { } started) _burning[round] = (system, started);
    }
```

## Game APIs touched

| Member | Use |
|---|---|
| `ModLibrary.Get<SoundBehavior>(id)` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1059)) | Resolve a mod or Core sound by Id |
| `SoundBehavior.Play(SpatialAudio, float, out IChannel?, bool)`, `CompoundSound.Play` ([current/decomp/KSA/CompoundSound.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CompoundSound.cs#L22)) | Start; the stop sound starts paused |
| `CompoundChannelWrapper.Stop` ([current/decomp/KSA/CompoundChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CompoundChannelWrapper.cs#L79)) | Stops loop, unpauses the tail |
| `IChannel.SetSpatialAudio`, `IsPlaying`, `Stop`, `PitchMultiplier`, `SetParameter(KeyHash, float)`, `ApplyParameters` ([current/decomp/KSA/IChannel.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IChannel.cs)) | Move, test, stop, retune |
| `SpatialAudio(double3 posEgo, double3 velEgo, double pressure)` ([current/decomp/KSA/SpatialAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SpatialAudio.cs)) | Ego-frame source state |
| `GameAudio.GetAudioCamera()` ([current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L65)) | `Program.MainViewport.BaseCamera` |
| `PhysicalAtmosphereReference.GetAtmosphericPressure(Camera)` ([current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L50)) | Pressure at the listener |
| `Camera.GetPositionEgo(IPosition)`, `GetVelocityEgo(IVelocity)` ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L236)) | Source position relative to the listener |
| `SoundReference.Priority` (default 128) ([current/decomp/KSA/SoundReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SoundReference.cs#L19)) | Voice stealing order |

## Pitfalls and version notes

- **Do not restart a loop from code**: checking once a frame at 60 fps loses a whole round of the
  cycle; let the asset's `<Loop />` loop it.[^gun-sound]
- **A sound Id that does not resolve throws from `ModLibrary.Get`**; both players catch it and warn
  once per Id rather than every frame.[^gun-sound][^motor-sound]
- **Core's engine loop needs its `Throttle` parameter**, or it plays at a tenth of its level an
  octave down.[^motor-sound][^ksa-core-sounds]
- **Ship mono files**: a stereo file arrives pre-mixed and cannot be spatialised.[^sounds-xml]
- Pitch shifting a recording past about 25% sounds like the same gun played wrong, hence the clamp
  and per-gun recordings (`GunSoundId`) for the M197.[^gun-sound][^launcher-profile]
- Recording licences are tracked beside the cut scripts; the cut points are measured off the
  envelope so a new recording does not need new numbers.[^audio-readme][^cut-cannon]

## See also

- [Burst sound at blast arrival](./burst-sound-at-blast-arrival.md)
- [Mod-simulated rounds](./mod-simulated-rounds.md)
- [Weapon part definitions in XML](./weapon-part-definitions-xml.md)
- [Trail renderer smoke](./trail-renderer-smoke.md)
- [StarMap entry and fault containment](./starmap-entry-and-fault-containment.md)
- [Stop channels on world reload with stopImmediate and treat unload-time StopAll as moot](../../../howto/audio/channels/release-sound-channels-at-scene-change-and-exit/release-sound-channels-at-scene-change-and-exit-1.md) - how-to article demonstrating this feature
- [Borrow Core's engine loop for a mod-simulated projectile with Throttle pinned open](../../../howto/audio/playback/drive-sound-parameters-from-code/drive-sound-parameters-from-code-3.md) - how-to article demonstrating this feature
- [Hold one channel per mod-simulated source and move it every frame](../../../howto/audio/playback/play-a-sound-at-a-world-position-and-hold-its-channel/play-a-sound-at-a-world-position-and-hold-its-channel-2.md) - how-to article demonstrating this feature
- [Declare a CompoundSound with spin-up, loop and tail cut from one recording](../../../howto/audio/playback/play-a-spin-up-loop-tail-sound-and-crossfade-engine-loops/play-a-spin-up-loop-tail-sound-and-crossfade-engine-loops-2.md) - how-to article demonstrating this feature
- [Tune spatial data, priority and pitch variation per weapon class](../../../howto/audio/sound-assets/ship-custom-sounds-as-xml-assets/ship-custom-sounds-as-xml-assets-3.md) - how-to article demonstrating this feature
- [Read the sound type map through the features that use each type](../../../howto/audio/sound-assets/understand-the-games-sound-and-music-xml-types/understand-the-games-sound-and-music-xml-types.md) - how-to article demonstrating this feature

[^sounds-xml]: src/KSArmory/KSArmorySounds.xml
[^gun-sound]: src/KSArmory/Ksa/GunSound.cs
[^motor-sound]: src/KSArmory/Ksa/MotorSound.cs
[^sound-channels]: src/KSArmory/Ksa/SoundChannels.cs
[^entry]: src/KSArmory/Ksa/KSArmoryMod.cs
[^launcher-profile]: src/KSArmory/Sim/LauncherProfile.cs
[^config]: src/KSArmory/Sim/Config.cs
[^audio-readme]: tools/audio/README.md
[^cut-cannon]: tools/cut-cannon.py
[^ksa-compoundsound]: KSA.CompoundSound (decompiled, build 2026.10.10.5554)
[^ksa-compoundchannel]: KSA.CompoundChannelWrapper (decompiled, build 2026.10.10.5554)
[^ksa-soundreference]: KSA.SoundReference (decompiled, build 2026.10.10.5554)
[^ksa-ichannel]: KSA.IChannel (decompiled, build 2026.10.10.5554)
[^ksa-spatialaudio]: KSA.SpatialAudio (decompiled, build 2026.10.10.5554)
[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
[^ksa-atmosphere]: KSA.PhysicalAtmosphereReference (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-core-sounds]: Content/Core/Sounds.xml (build 2026.10.10.5554)
[^ksa-explosion-assets]: Content/Core/ExplosionAssets.xml (build 2026.10.10.5554)
