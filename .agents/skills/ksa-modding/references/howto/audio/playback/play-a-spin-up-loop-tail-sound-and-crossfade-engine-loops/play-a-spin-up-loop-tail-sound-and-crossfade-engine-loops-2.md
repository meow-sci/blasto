---
type: How-To
title: Declare a CompoundSound with spin-up, loop and tail cut from one recording
description: Cut one recording into spin, loop and tail at envelope-measured points, declare them as a CompoundSound, and let the engine sequence them while the mod only starts, holds and stops one channel
category: audio
group: playback
topic: play-a-spin-up-loop-tail-sound-and-crossfade-engine-loops
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/spatial-gun-and-motor-sounds]
techniques: [compound-sound, xml-asset, fmod-audio]
hooks: [starmap StarMapAfterGui]
lifecycle: [startup:asset-load, frame:draw-ui, frame:post-render]
game_types:
  - KSA.CompoundSound
  - KSA.CompoundChannelWrapper
  - KSA.SoundReference
  - KSA.SoundFileReference
  - KSA.SoundBehavior
  - KSA.IChannel
  - KSA.GameAudio
tags: [audio, fmod, compound-sound, loops, weapon-sound, sound-cutting, python]
---

Outcome: a burst of gunfire that spins up, holds a steady loop while the trigger is held, and ends with the reports
arriving back off the terrain, without the mod scheduling any of the three. The mod starts one channel when the gun starts
firing, moves it every frame and stops it when the gun stops. Prerequisites: StarMap, a recording of the real sound.
Demonstrated by KSArmory (`KSArmory_Cannon_*`, `GunSound`, `tools/cut-cannon.py`).

## Background: how the game does it

A `<CompoundSound>` has three optional parts: `<StartSound>`, `<Sound>` and `<StopSound>`. `CompoundSound.Play` plays the
start and loop sounds immediately and plays the stop sound **paused**, returning a `CompoundChannelWrapper`.[current/decomp/KSA/CompoundSound.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CompoundSound.cs#L22-L31)[^ksa-compoundsound]
`CompoundChannelWrapper.Stop()` stops the start and loop channels (running their `FadeOut` if declared) and un-pauses
the stop channel, so the tail plays; `Stop(stopImmediate: true)` also kills the tail.[current/decomp/KSA/CompoundChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CompoundChannelWrapper.cs#L79-L92)[^ksa-compoundchannelwrapper]
Its `SetSpatialAudio` moves all three channels, and `PitchMultiplier` and `VolumeMultiplier` forward to all three.[current/decomp/KSA/CompoundChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CompoundChannelWrapper.cs#L21-L106)
Each part is a normal `<Sound>`, referenced by `Id`, so each can carry its own spatial data, priority and loop flag.

Core builds its engines the same way: `DefaultEngineSoundBehavior` is a `<CompoundSound>` of a start sound, the
looping `DefaultEngine` and a stop sound.[current/Content/Core/Sounds.xml](Core/Sounds.xml (build 2026.10.10.5554)#L347-L352)[^ksa-core-sounds] The mod's own comment: "Same shape as Core's DefaultEngineSoundBehavior, which is a
CompoundSound for the same reason an engine is: it starts, it runs, it stops."

## Lifecycle and timing

| Moment | Where | Vocabulary |
|---|---|---|
| Assets | `KSArmorySounds.xml` listed in `mod.toml` `assets` | `startup:asset-load` |
| Start, move, stop | `GunSound.Update` for each firing system, in the mod's per-frame step after the rounds were stepped | `frame:draw-ui` |
| Apply | `GameAudio.UpdateAudio` in `PostRender` | `frame:post-render` |

The channel lifecycle (start, `TryMove`, sweep, `StopAll`) is
[approach 2 of the world-position topic](../play-a-sound-at-a-world-position-and-hold-its-channel/play-a-sound-at-a-world-position-and-hold-its-channel-2.md).
See the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Cut the recording into three parts, measured, not typed.** `tools/cut-cannon.py` builds a 5 ms RMS envelope and finds
   the boundaries from it: the spin-up ends where the level first reaches 75% of the peak, firing ends where it last does,
   and the tail ends where the level falls below 3% of the peak (an encoded file never reaches silence):

   ```python
   # tools/cut-cannon.py L52-L68
   def segments(x):
       """(spin_end, fire_end, tail_end) in samples, from the envelope."""
       env, h = envelope(x)
       peak = env.max()

       # The plateau: everything within 25% of the loudest. Its first sample ends the spin-up and its
       # last ends the firing.
       plateau = np.where(env >= 0.75 * peak)[0]
       spin_end = int(plateau[0] * h)
       fire_end = int((plateau[-1] + 1) * h)

       # The tail runs until the level falls into the noise floor. 3% of peak rather than zero: an
       # encoded file never reaches silence, and chasing it to zero appends a second of nothing.
       above = np.where(env >= 0.03 * peak)[0]
       tail_end = int(min((above[-1] + 1) * h, len(x)))

       return spin_end, fire_end, tail_end
   ```

   A new recording needs no new numbers. `--report` measures and prints without writing.

2. **Make the loop seamless.** A slice taken straight from the middle clicks once per pass. The script centres a 0.9 s
   loop in the steady part and blends the 60 ms that would have followed onto its head, then writes mono WAVs (a
   stereo file "cannot be" spatialised, per the script's comment); the spin-up gets no fade-in, because its first sample
   is the first round:

   ```python
   # tools/cut-cannon.py L71-L87
   def loop_from(x, start, end):
       """A seamless loop out of [start, end), crossfaded against what follows it."""
       length = int(LOOP_SECONDS * RATE)
       xf = int(CROSSFADE_SECONDS * RATE)

       if end - start < length + xf:
           length = max(int(0.25 * RATE), (end - start) - xf)

       # Centred in the steady part, so neither edge borrows from the spin-up or the run-down.
       at = start + max(0, ((end - start) - (length + xf)) // 2)

       core = x[at:at + length].copy()
       follows = x[at + length:at + length + xf]

       w = np.linspace(0.0, 1.0, len(follows))
       core[:len(follows)] = core[:len(follows)] * w + follows * (1.0 - w)
       return core
   ```

   Keep the source recording and its licence beside the script: `tools/audio/README.md` records freesound id 163119, the
   CC0 dedication and the measured cycle rate (4700 rpm for a recording used at 4500).[tools/audio/README.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/audio/README.md#L1-L36)
   Prefer a lossless source: the README notes a 128 kbit copy of the same recording measured 6600 rpm because the codec
   smeared the envelope enough to put the autocorrelation on a harmonic.

3. **Declare the three sounds and the compound.** The loop carries `<Loop />`; all three share one `SpatialSoundData` by
   `Id`; `Priority="110"` keeps gun channels ahead of Core's explosions under FMOD's voice limit (see
   [ship custom sounds as XML assets](../../sound-assets/ship-custom-sounds-as-xml-assets/overview.md)):

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

   A `<StartSound Id="..."/>` with no files is a reference: `SoundReference.IsReference()` is true for an empty file list
   and `Populate` swaps in the registered sound, which happens after `AttachGameData`.[current/decomp/KSA/SoundReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SoundReference.cs#L122-L126)[^ksa-soundreference]

4. **A recording with no spin-up: loop plus tail.** Leave `<StartSound>` out. `CompoundChannelWrapper` tolerates a null
   channel for any part, so the M197 compound is a loop and a tail:

   ```xml
   <!-- src/KSArmory/KSArmorySounds.xml L96-L99 -->
       <CompoundSound Id="KSArmoryM197">
           <Sound Id="KSArmoryM197Loop" />
           <StopSound Id="KSArmoryM197Tail" />
       </CompoundSound>
   ```

5. **Hold it while firing, stop it to get the tail.** Start with `ModLibrary.Get<SoundBehavior>("KSArmoryCannon").Play(...)`
   when the guns begin firing, move it each frame, and call `Stop()` (the mod's `SoundChannels.Cut`) when they stop:
   the loop ends and the tail plays on its own. The held channel then goes away, and the tail's channel finishes by
   itself; `GameAudio.UpdateAudio` drops it once FMOD reports it done. Do not restart the loop from code; `<Loop />`
   loops it.

6. **Retune toward the gun's rate, within limits.** The recording is one real gun, so a gun with a different rate is
   pitched toward it, clamped, because pitch moves cycle and timbre together:

   ```csharp
   // src/KSArmory/Ksa/GunSound.cs L149-L161
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
   ```

   A gun that names its own recording (`GunSoundId`) is played as recorded.

## Complete example

| File | Role |
|---|---|
| [tools/cut-cannon.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/cut-cannon.py) | Envelope-measured spin, loop and tail from one recording |
| [tools/audio/README.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/audio/README.md) | Provenance, licence, measured cycle rate |
| [src/KSArmory/KSArmorySounds.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmorySounds.xml) | Sounds, spatial data, `CompoundSound` |
| [src/KSArmory/Ksa/GunSound.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/GunSound.cs) | One channel per firing system, retune |

## Variations and gotchas

- **The tail is a separate channel.** After `Stop()` the loop is gone and the tail is a separate paused-then-unpaused
  channel at the position of its last `SetSpatialAudio`; it does not follow the source afterwards. For a tail that must
  follow, keep the compound wrapper and keep calling `SetSpatialAudio` until `IsPlaying()` is false.
- **`Stop()` cuts the loop abruptly** unless the loop `<Sound>` declares `<FadeOut>`; the tail begins at once.
- **Default `SimSpeed` rule.** None of these sounds declares `SimSpeed`, so they carry `All`: pitch is multiplied by
  `min(1 + speed / 5, 3)` at 1x and above, and the channels are stopped above 10x.[current/decomp/KSA/ChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChannelWrapper.cs#L456-L503) The 0.8..1.25
  retune is on top of that. This comes from reading `HandleSimSpeedPitch`, not from listening. See
  [keep mod sounds sensible under time warp](../../channels/keep-mod-sounds-sensible-under-time-warp/overview.md).
- **One shot per round for slow guns.** A 5-inch gun at 40 rounds a minute has `KSArmoryMk42Gunshot`, a single `<Sound>`
  with `<Pitch Min="0.96" Max="1.04" />`, played per round; a 75 rounds-per-second Phalanx would need 75 voices a second
  as one-shots, which is why the loop is the right unit there.
- **Licences.** Keep the freesound id in the filename and the README; CC0 sources need no credit but a recording among
  synthesised samples should be shown to be safe.

## See also

- [Approach 1: two mono loops with per-frame volume and pitch](play-a-spin-up-loop-tail-sound-and-crossfade-engine-loops-1.md)
- [Overview of this topic](overview.md)
- Mod feature: [spatial gun and motor sounds](../../../../mods/ksarmory/features/spatial-gun-and-motor-sounds.md)
- [Hold one channel per mod-simulated source](../play-a-sound-at-a-world-position-and-hold-its-channel/play-a-sound-at-a-world-position-and-hold-its-channel-2.md)
- [Understand KSA's sound, channel group and music playlist XML types](../../sound-assets/understand-the-games-sound-and-music-xml-types/overview.md)

[^ksa-compoundsound]: KSA.CompoundSound (decompiled, build 2026.10.10.5554)
[^ksa-compoundchannelwrapper]: KSA.CompoundChannelWrapper (decompiled, build 2026.10.10.5554)
[^ksa-core-sounds]: Core/Sounds.xml (build 2026.10.10.5554)
[^ksa-soundreference]: KSA.SoundReference (decompiled, build 2026.10.10.5554)
