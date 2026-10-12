---
type: How-To
title: Crossfade an idle loop and a load loop by RPM, with loops cut from recordings
description: Play two looping channels per car and set their volume and pitch every frame from the simulated RPM and load, using loops cut from CC0 recordings with the recorded RPM kept as a profile number
category: audio
group: playback
topic: play-a-spin-up-loop-tail-sound-and-crossfade-engine-loops
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksacars/features/engine-sound-crossfade, mods/ksacars/features/art-and-audio-pipeline-tooling]
techniques: [fmod-audio, xml-asset]
hooks: [starmap StarMapAfterGui, starmap StarMapAfterOnFrame]
lifecycle: [startup:asset-load, frame:draw-ui, frame:after-on-frame, frame:post-render]
game_types:
  - KSA.IChannel
  - KSA.SoundBehavior
  - KSA.SpatialAudio
  - KSA.GameAudio
  - KSA.SoundReference
  - KSA.SoundFileReference
tags: [audio, fmod, loops, engine-sound, rpm, crossfade, pitch, sound-cutting, python]
---

Outcome: a car engine that idles, rises into a loaded growl as the throttle and RPM rise, starts with a starter clip
and ends with a key-off. Two looping recordings play at once and the mod changes their volume and pitch every frame.
Prerequisites: StarMap, a sounds XML with `<Loop />` sounds ([XML assets](../../sound-assets/ship-custom-sounds-as-xml-assets/overview.md))
and a simulation that produces an RPM and a load value. Demonstrated by KSACars (`BuggySound`, `tools/buggy-sounds.py`,
`tools/eldorado-sounds.py`).

Two layers are used because one sample pitched up three times is still an idle, only faster: a loaded engine has a
different timbre. Each loop is pitched against the RPM it was recorded at, which is why the recorded RPM is data
kept with the profile.

## Background: how the game does it

`ChannelWrapper.UpdateVolume` writes `SoundVolume * VolumeMultiplier * Fade * Parameter` and `UpdatePitch` writes
`SoundPitch * PitchMultiplier * SimPitch * Parameter` to FMOD every frame in `GameAudio.UpdateAudio`; the
parameter factors are reset afterwards but `VolumeMultiplier` and `PitchMultiplier` are not, so a value the mod writes
stays in force until it writes another.[current/decomp/KSA/ChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChannelWrapper.cs#L248-L273)[^ksa-channelwrapper] FMOD pitch scales playback speed, so a pitch of `rpm / recordedRpm`
moves the recording's engine line to the frequency the simulated engine should have.

The loop is a property of the sound file: any `<Loop />` child on `<SoundFile>` makes the FMOD sound `LoopNormal`.[current/decomp/KSA/SoundFileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SoundFileReference.cs#L29-L40)[^ksa-soundfilereference]
The `SimSpeed` rule (default `All`) adds `SimPitch`; see the gotchas.

## Lifecycle and timing

| Moment | Hook | Vocabulary |
|---|---|---|
| Assets | `mod.toml` `assets` lists `KSACarsSounds.xml`; `SoundReference`s register and the wavs load into FMOD | `startup:asset-load` |
| Each frame, HUD shown | `[StarMapAfterGui]` -> `StepOnce` -> `BuggySound.Update` | `frame:draw-ui` |
| Each frame, HUD hidden | `[StarMapAfterOnFrame]` -> `StepOnce` (latch makes it a no-op when the GUI hook ran) | `frame:after-on-frame` |
| Apply | `GameAudio.UpdateAudio` in `PostRender` | `frame:post-render` |

`BuggyDrive.Rpm` and `BuggyDrive.Load` are produced by the drive model, which steps inside the engine's physics
window; the sound only reads them on the main thread.[src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs#L112-L130) The sound has no physics-thread
code.

## Steps

1. **Cut seamless mono loops from recordings.** `tools/buggy-sounds.py` cuts four files per car (starter, idle loop,
   load loop, key-off) from CC0 recordings: the load stretch is the steadiest part of the recording (the script's docstring says it was found by tracking
   the strongest engine line and taking the window where it moves least; the script itself cuts a fixed window of the
   pre-trimmed excerpt), then each loop is cut where the tail best repeats the head and what would have followed is
   blended onto the head with a 20 ms crossfade. Peaks are normalised and the output is mono, "because the engine
   spatialises the source" (the mod's comment). The loop maker:

   ```python
   # tools/buggy-sounds.py L83-L98
   def make_loop(x, rate):
       """Cut where the tail best repeats the head, and blend what would have followed onto the head."""
       head = x[:int(HEAD_SECONDS * rate)]
       head = (head - head.mean()) / (head.std() + 1e-9)
       n = len(head)
       best, best_score = len(x) - n, -2.0
       for end in range(len(x) - int(SEARCH_SECONDS * rate), len(x) - n):
           seg = x[end:end + n]
           score = float(np.dot(head, (seg - seg.mean()) / (seg.std() + 1e-9))) / n
           if score > best_score:
               best, best_score = end, score
       f = int(CROSSFADE_SECONDS * rate)
       loop = x[:best].copy()
       ramp = np.linspace(0.0, 1.0, f)
       loop[:f] = x[:f] * ramp + x[best:best + f] * (1.0 - ramp)
       return loop, best_score
   ```

   It prints how well each seam matches (`seam matches its head at r = ...`), and `--report` writes nothing. The script
   needs NumPy and SciPy (`resample_poly` halves 96 kHz sources); the Eldorado script reuses the buggy script's
   functions. Source recordings stay beside the scripts in `tools/audio/` with their freesound id, licence and the
   RPM they were recorded at.[tools/audio/README.md](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/audio/README.md#L1-L26)

2. **Record the RPM each loop was recorded at.** This is the number the pitch ratio depends on, and it comes from the
   recording, not the game. KSACars derives it from the firing frequency in the take: the buggy's idle fires at 45 Hz,
   which is 1350 rpm on a flat-four; the Cadillac idle is 95 Hz, "a cold start's fast idle of about 1400 rpm on a
   V8"; the Impala loaded loop holds 125 Hz, about 1900 rpm on a V8 (a four-stroke fires half its cylinders per
   revolution, so rpm = Hz * 60 / (cylinders / 2)). The profile carries them:

   ```csharp
   // src/KSACars/Sim/BuggyProfile.cs L57-L64
       /// <summary>The engine's sounds: <c>{SoundPrefix}Start</c>, <c>Idle</c>, <c>Load</c> and <c>Stop</c>.</summary>
       public required string SoundPrefix { get; init; }

       /// <summary>The RPM the loop under load was recorded at.</summary>
       public double LoadRecordedRpm { get; init; } = 2500.0;

       /// <summary>The RPM the idle loop was recorded at, or zero where that is <see cref="IdleRpm"/>.</summary>
       public double IdleRecordedRpm { get; init; }
   ```

   and the Eldorado profile sets `IdleRpm = 650.0`, `LoadRecordedRpm = 1900.0` and `IdleRecordedRpm = 1400.0`.[src/KSACars/Sim/BuggyProfile.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/BuggyProfile.cs#L302-L306)

3. **Declare the sounds.** One `SpatialSoundData` per car, four `<Sound>` elements, `<Loop />` on the two loops. No
   `<Pitch>` or `<Param>`: the mod drives both by hand. See
   [ship custom sounds as XML assets](../../sound-assets/ship-custom-sounds-as-xml-assets/overview.md) for each attribute:

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

   List the file in `mod.toml` `assets`; a file left out is never loaded and `ModLibrary.Get` then throws for the id.

4. **Hold the two channels** (`Keep`, `TryMove`, per-craft table). That is
   [one looping channel per craft](../play-a-sound-at-a-world-position-and-hold-its-channel/play-a-sound-at-a-world-position-and-hold-its-channel-3.md).

5. **Mix every frame.** `rev` is where the RPM sits between idle and redline; `loaded` rises with `rev` and with the
   load; the idle fades out as `loaded` rises, the load loop fades in. Each loop is pitched against its own recorded
   RPM, and the product is clamped:

   ```csharp
   // src/KSACars/Ksa/BuggySound.cs L91-L113
       private static void Mix(Engine engine, BuggyDrive drive, float volume)
       {
           BuggyProfile p = drive.Profile;
           double rev = Math.Clamp((drive.Rpm - p.IdleRpm) / (p.RedlineRpm - p.IdleRpm), 0.0, 1.0);
           double loaded = Math.Clamp((rev * 1.4) + (0.35 * drive.Load), 0.0, 1.0);

           Set(engine.Idle, drive.Rpm / (p.IdleRecordedRpm > 0.0 ? p.IdleRecordedRpm : p.IdleRpm), (1.0 - (0.85 * loaded)) * volume);
           Set(engine.Load, drive.Rpm / p.LoadRecordedRpm, loaded * (0.55 + (0.45 * drive.Load)) * volume);
       }

       private static void Set(IChannel? channel, double pitch, double volume)
       {
           if (channel is null) return;
           try
           {
               channel.PitchMultiplier = (float)Math.Clamp(pitch, 0.5, 2.5);
               channel.VolumeMultiplier = (float)Math.Clamp(volume, 0.0, 2.0);
           }
           catch
           {
               // A channel the engine has reclaimed is picked up again next frame.
           }
       }
   ```

   Worked number: the Eldorado idles at 650 rpm against a loop recorded at 1400, a ratio of 0.46; that is below the
   0.5 floor, so at idle it plays at 0.5.

6. **Starter, key-off and warp.** On the first frame a car is the flown craft, play `<Prefix>Start` once and wait
   2000 ms of wall-clock time before bringing the loops in (the starter clip's length less its fade); leaving the car
   cuts both loops and plays `<Prefix>Stop` once. Above 4x simulation speed the loops are cut with no key-off, because
   RPM swings under warp would only be noise. Only the car the player flies is heard.[src/KSACars/Ksa/BuggySound.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/BuggySound.cs#L40-L83)

## Complete example

| File | Role |
|---|---|
| [src/KSACars/Ksa/BuggySound.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/BuggySound.cs) | Start, crossfade, re-pitch, stop; the 4x warp cut |
| [src/KSACars/KSACarsSounds.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsSounds.xml) | Four sounds and a spatial data asset per car |
| [src/KSACars/Sim/BuggyProfile.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/BuggyProfile.cs#L57-L64) | `SoundPrefix`, `IdleRecordedRpm`, `LoadRecordedRpm` |
| [tools/buggy-sounds.py](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/buggy-sounds.py) and [tools/eldorado-sounds.py](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/eldorado-sounds.py) | Cut the loops |
| [tools/audio/README.md](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/audio/README.md) | Provenance, licence, recorded RPMs |

## Variations and gotchas

- **The engine adds its own pitch.** `HandleSimSpeedPitch` multiplies a sound's pitch by `min(1 + speed / 5, 3)` for every
  speed of 1x or more when the `<Sound>` has the default `SimSpeed` rule (`All`), which these loops do, and by the
  speed itself (floored at 0.25) below 1x.[current/decomp/KSA/ChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChannelWrapper.cs#L456-L495) Read literally, the loops are pitched 1.2 times higher
  than `rpm / recordedRpm` at 1x and 1.4 times at 2x. The mod does not compensate and its author does not mention
  it; this is from reading the code, not from listening. Declaring `SimSpeed="OnlyLower"` on the loops would keep the
  mod's ratio exact at 1x and above (it only slows pitch below 1x), and `Ignore` would also disable the engine's
  stop above 10x.
- **Paused world.** With the default rule, simulation speed 0 sets the sim pitch to 0, so the loops fall silent while
  paused; that is usually what you want from an engine.
- **Persisting multipliers.** Because `PitchMultiplier` and `VolumeMultiplier` persist, a branch that skips `Mix` (such as
  the 2 s starter wait) leaves the last values in force.
- **Mono files.** The mod's notes insist on mono: the engine spatialises the source and a stereo file "arrives already
  mixed". Treat that as the mod author's observed advice.
- **Clamps.** Pitch 0.5..2.5 and volume 0..2 are the mod's; `SoundFileReference.Volume` is separately clamped to 0..2
  at load.
- **Loops have a cost.** Each loop is a voice; two per flown car is cheap, but see the voice limit note in
  [ship custom sounds as XML assets](../../sound-assets/ship-custom-sounds-as-xml-assets/overview.md).
- **Provenance.** Keep the filename as freesound produced it; it encodes the id, the only thing that leads back to the
  licence. KSACars ships no credit by choice, relying on CC0.

## See also

- [Approach 2: CompoundSound spin, loop and tail cut from one recording](play-a-spin-up-loop-tail-sound-and-crossfade-engine-loops-2.md)
- [Overview of this topic](overview.md)
- Mod features: [engine sound from two crossfaded loops](../../../../mods/ksacars/features/engine-sound-crossfade.md), [art and audio pipeline tooling](../../../../mods/ksacars/features/art-and-audio-pipeline-tooling.md)
- [Hold one looping channel per craft](../play-a-sound-at-a-world-position-and-hold-its-channel/play-a-sound-at-a-world-position-and-hold-its-channel-3.md)
- [Simulate wheels and suspension without a wheel module](../../../physics/aero-and-ground/simulate-wheels-and-suspension-without-a-wheel-module/overview.md)
- [Keep mod-played sounds sensible under time warp](../../channels/keep-mod-sounds-sensible-under-time-warp/overview.md)

[^ksa-channelwrapper]: KSA.ChannelWrapper (decompiled, build 2026.10.10.5554)
[^ksa-soundfilereference]: KSA.SoundFileReference (decompiled, build 2026.10.10.5554)
