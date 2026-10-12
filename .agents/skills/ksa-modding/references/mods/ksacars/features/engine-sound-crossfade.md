---
type: Mod Feature
title: Engine sound from two crossfaded, re-pitched loops
description: Ship mod-owned engine samples as Sound XML assets and play an idle loop and a loaded loop together on KSA's FMOD channels, crossfaded by throttle and re-pitched every frame to the simulated RPM
mod: ksacars
category: audio
techniques: [fmod-audio, xml-asset, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, starmap StarMapAfterOnFrame]
lifecycle: [startup:asset-load, frame:draw-ui, frame:after-on-frame, frame:post-render]
game_types:
  - KSA.GameAudio
  - KSA.SoundBehavior
  - KSA.SoundReference
  - KSA.SoundFileReference
  - KSA.SpatialSoundData
  - KSA.SpatialAudio
  - KSA.IChannel
  - KSA.ChannelWrapper
  - KSA.ModLibrary
  - KSA.PhysicalAtmosphereReference
  - KSA.Camera
  - KSA.AssetBundle
tags: [audio, fmod, sound-xml, spatial-audio, crossfade, pitch, engine-sound, starmap]
---

A KSACars car sounds like an engine: a starter clip when you take the controls, an idle that
rises into a loaded growl as you press the throttle, and a key-off clip when you leave. Each car
ships its own `.wav` files, declared as ordinary KSA `<Sound>` assets, and the mod plays two
looping layers at once, setting their volume and pitch every frame from the simulated engine.[^ksacars-buggysound]

Two layers are used because one sample pitched up three times is still an idle, only faster. A
loaded engine has a different timbre, so the mod crossfades from an idle recording to a
load recording as the throttle and RPM rise, and pitches each against the RPM it was recorded at.

## How it works

**Assets (load time).** `mod.toml` lists `KSACarsSounds.xml` in `assets`, so KSA reads it like
any content file.[^ksacars-modtoml] The XML follows Core's `Sounds.xml` shape:[^ksacars-sounds-xml][^ksa-core-sounds]

| Element | Decompiled type | What the mod uses it for |
|---|---|---|
| `<SpatialSoundData Id>` with `<VolumeRange Min Max>` and `<TwoDRange Min Max>` | `SpatialSoundData` | One per car: 3D min/max distance, and the near range over which the sound blends to 2D |
| `<Sound Id ChannelId="ExternalChannel" Priority="115">` | `SoundReference` (registered under `AssetBundle`'s `[XmlElement("Sound")]`) | Four per car: `<Prefix>Start`, `Idle`, `Load`, `Stop` |
| `<SoundFile Path Volume>` | `SoundFileReference` | Mono `.wav` under `Sounds/`, path relative to the mod folder |
| `<Loop />` inside `<SoundFile>` | `SoundFileReference.LoopData` | Present on the idle and load files, which turns on FMOD `LoopNormal` |
| `<SpatialSoundData Id>` inside `<Sound>` | reference to the one declared above | Shares the car's spatial settings |

`AssetBundle` maps `<Sound>` to `SoundReference` and `<SpatialSoundData>` to
`SpatialSoundData`;[^ksa-assetbundle] a `SoundReference` with files registers itself as a
`SoundBehavior` in `ModLibrary`,[^ksa-soundreference][^ksa-modlibrary] and its `ChannelId` is resolved
to a `ChannelGroupReference` when it is populated (Core declares `ExternalChannel` under `Sfx`).
`<Loop/>` sets the FMOD loop mode and `Volume` is clamped to 0..2.[^ksa-soundfilereference]
The XML declares no `<Pitch>` or `<Param>`: the mod drives both by hand.

**Per frame (`BuggySound.Update`).**

1. Only the car the player is flying makes engine noise, and only when there is a listener:
   `SoundChannels.Listener()` wraps `GameAudio.GetAudioCamera()` (the main viewport's base
   camera).[^ksacars-soundchannels][^ksa-gameaudio]
2. A `SpatialAudio` is built in the Ego frame from that camera: `camera.GetPositionEgo(craft)`,
   `camera.GetVelocityEgo(craft)` (so Doppler is FMOD's job) and the atmospheric pressure at the
   *listener*, `PhysicalAtmosphereReference.GetAtmosphericPressure(camera)`.[^ksa-spatialaudio][^ksa-atmosphere]
3. First frame for a car: play `<Prefix>Start` once and record the time. The loops wait 2000 ms
   for the starter clip to catch.
4. `Keep` holds each loop's `IChannel`: if it is still playing it is moved with
   `SetSpatialAudio`, otherwise a new one is started by
   `ModLibrary.Get<SoundBehavior>(id).Play(spatial, volume, out channel)`.
5. `Mix` computes `rev` (RPM between idle and redline, 0..1) and
   `loaded = clamp(1.4 * rev + 0.35 * drive.Load)`. The idle gets volume `1 - 0.85 * loaded` and
   pitch `Rpm / IdleRecordedRpm`; the load loop gets volume `loaded * (0.55 + 0.45 * Load)` and
   pitch `Rpm / LoadRecordedRpm`. Pitch is clamped to 0.5..2.5 and volume to 0..2.
6. Leaving the car (or it ceasing to be a car) cuts both loops and plays `<Prefix>Stop` once.
   Past 4x simulation speed the loops are cut without a key-off: RPM swings under warp would
   only make noise.

`BuggyProfile` carries the per-car numbers: `SoundPrefix`, `IdleRpm`, `RedlineRpm`,
`LoadRecordedRpm` and `IdleRecordedRpm` (the Eldorado's idle was recorded at a 1400 rpm cold
idle, its load loop at 1900 rpm).[^ksacars-profile] `BuggyDrive.Rpm` and `BuggyDrive.Load`
come from the drive model, which steps inside the physics window.[^ksacars-buggydrive]

## Lifecycle and timing

| Moment | Hook | Thread | What happens |
|---|---|---|---|
| Load | `mod.toml` `assets` -> XML asset bundle (`startup:asset-load`) | game loader | `SoundReference`s registered, `.wav` files loaded into FMOD sounds; `ModLibrary.PopulateSounds` (each `SoundBehavior.Populate`, which resolves `ChannelId`) runs later in the `Program` constructor, after `StarMapAllModsLoaded` and `AttachGameData` (Program.cs L985-L990) |
| Every frame, UI shown | `[StarMapAfterGui]` (Harmony postfix on `Program.OnDrawUiViewports`) -> `StepOnce` -> `BuggySound.Update` | main | Channels started, moved, re-pitched, re-mixed |
| Every frame, UI hidden (F2) | `[StarMapAfterOnFrame]` (postfix on `Program.OnFrame`) -> `StepOnce` | main | Same, via the `FrameLatch` fallback |
| Same frame | `Program.PostRender` -> `GameAudio.UpdateAudio` | main | KSA applies fades, sim-speed pitch, parameters, volume, pitch and filters to every active channel |

`StepOnce` is claimed once per frame through a latch, so it runs from whichever hook fires first
and the frame hook always releases it.[^ksacars-mod] Audio state is plain main-thread data, so
either hook is safe. What matters is that KSA re-applies every active channel in
`GameAudio.UpdateAudio`, which `Program.PostRender` calls after the render (L2362).[^ksa-program]
`ChannelWrapper.UpdateVolume` and `UpdatePitch` multiply `SoundVolumeMultiplier *
VolumeMultiplier * FadeVolumeMultiplier * ParameterVolumeMultiplier` (and the pitch
equivalent with `SimPitchMultiplier`), and only reset the *parameter* multipliers afterwards, so
a `VolumeMultiplier` or `PitchMultiplier` the mod sets stays in force until it sets it
again.[^ksa-channelwrapper] From `StarMapAfterGui` the new values reach FMOD in the same frame's
`PostRender`; from the `StarMapAfterOnFrame` fallback they reach it one frame later.

## Key code

| Path | Role |
|---|---|
| [src/KSACars/KSACarsSounds.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsSounds.xml) | Spatial data and four `<Sound>` assets per car |
| [src/KSACars/mod.toml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/mod.toml#L11-L15) | Lists the sounds XML as an asset file |
| [src/KSACars/Ksa/BuggySound.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/BuggySound.cs) | Start, crossfade, re-pitch, stop; the 4x warp cut |
| [src/KSACars/Ksa/SoundChannels.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/SoundChannels.cs) | Listener, listener pressure, guarded `TryMove` and `Cut` |
| [src/KSACars/Sim/BuggyProfile.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/BuggyProfile.cs#L57-L64) | `SoundPrefix`, `LoadRecordedRpm`, `IdleRecordedRpm` |
| [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs#L112-L130) | `StepOnce`: where `BuggySound.Update` is called each frame |

## Snippets

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

```csharp
// src/KSACars/Ksa/BuggySound.cs L138-L168
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

    private void PlayOnce(string id, SpatialAudio spatial, float volume) => Play(id, spatial, volume);

    private static bool TrySpatial(Camera camera, Vehicle craft, out SpatialAudio spatial)
    {
        spatial = default!;
        double3 posEgo = camera.GetPositionEgo(craft);
        double3 velEgo = camera.GetVelocityEgo(craft);
        if (!Vec.IsFinite(posEgo) || !Vec.IsFinite(velEgo)) return false;

        spatial = new SpatialAudio(posEgo, velEgo, SoundChannels.PressureAt(camera));
        return true;
    }
```

## Game APIs touched

| API | Decompiled source | Used for |
|---|---|---|
| `AssetBundle` `[XmlElement("Sound")]`, `[XmlElement("SpatialSoundData")]` | [current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L55-L61) | Element names a mod's sound XML may use |
| `SoundReference` (`ChannelId`, `Priority`, `SoundFiles`, `SpatialSoundData`, `Play`) | [current/decomp/KSA/SoundReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SoundReference.cs#L10-L89) | A playable sound; `Play` starts an FMOD channel, wraps it in `ChannelWrapper`, registers it |
| `SoundFileReference` (`Path`, `Volume`, `Loop`) | [current/decomp/KSA/SoundFileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SoundFileReference.cs#L8-L81) | The `.wav`, loop mode, FMOD sound creation |
| `SpatialSoundData` (`VolumeRange`, `TwoDRange`) | [current/decomp/KSA/SpatialSoundData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SpatialSoundData.cs#L7-L77) | 3D min/max distance and 2D blend |
| `SoundBehavior.Play(SpatialAudio, float, out IChannel?, bool)` | [current/decomp/KSA/SoundBehavior.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SoundBehavior.cs#L14-L17) | Base type looked up by id |
| `ModLibrary.Get<SoundBehavior>(id)` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L789-L795) | Resolves a sound id from any loaded mod |
| `GameAudio.GetAudioCamera()`, `GameAudio.UpdateAudio` | [current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L65-L215) | Listener camera; the per-frame apply pass |
| `SpatialAudio(double3 posEgo, double3 velEgo, double atmosphericPressure)` | [current/decomp/KSA/SpatialAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SpatialAudio.cs#L29-L34) | Source position, velocity and pressure in Ego |
| `IChannel` (`PitchMultiplier`, `VolumeMultiplier`, `SetSpatialAudio`, `IsPlaying`, `Stop`) | [current/decomp/KSA/IChannel.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IChannel.cs) | Handle the mod keeps per loop |
| `ChannelWrapper.UpdateVolume` / `UpdatePitch` / `SetSpatialAudio` | [current/decomp/KSA/ChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChannelWrapper.cs#L248-L273) | Products applied to FMOD; pressure drives a low-pass from 160 Hz to 18 kHz |
| `PhysicalAtmosphereReference.GetAtmosphericPressure(Camera)` | [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L50-L59) | Pressure in atmospheres at the camera (0 outside an atmosphere) |
| `Core/Sounds.xml` | [current/Content/Core/Sounds.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Sounds.xml#L1-L14) | Reference for the XML shape and the `ExternalChannel` group (L45) |

## Pitfalls and version notes

- **The listener's pressure, not the source's.** `SpatialAudio` carries one pressure and
  `ChannelWrapper.SetSpatialAudio` turns it into a low-pass cutoff (`Lerp(160, 18000,
  clamp(pressure))`), so in vacuum at the camera nothing is heard however loud the car is. KSA's
  own `SpatialAudio(Astronomical)` constructor does the same.[^ksa-spatialaudio][^ksa-channelwrapper]
- **Bypassing `GameAudio.PlaySound`.** The mod calls `SoundBehavior.Play` directly, which skips the
  `Universe.SimulationSpeed > 10` early-out in `GameAudio.PlaySound`.[^ksa-gameaudio] KSA still
  stops channels above 10x in `StopForSimSpeed` unless the `<Sound>` sets `SimSpeed="Ignore"`, and
  scales pitch with simulation speed in `HandleSimSpeedPitch`; the mod's own 4x cut sits below
  both.[^ksa-channelwrapper]
- **Channels are reclaimed by the engine.** `GameAudio.UpdateAudio` stops and drops a channel that
  is neither playing nor virtual, so every call on a held `IChannel` is wrapped in `try` and a dead
  one is replaced next frame.[^ksacars-soundchannels]
- **A missing id is silent.** An asset file left out of `mod.toml` is never loaded; the mod warns
  once per id when `ModLibrary.Get<SoundBehavior>` returns null.[^ksacars-modtoml]
- **Mono files.** The sounds XML notes the files are mono on purpose: the engine spatialises the
  source, and a stereo file arrives already mixed.[^ksacars-sounds-xml]
- **Stopping matters mid-session, not at exit.** `StopAll` runs when the craft leaves a flight
  scene, when the fault limiter disables the mod, and in `[StarMapUnload]`. The last one is moot:
  StarMap invokes `[StarMapUnload]` only after `Program.Run` has returned and `Program.Dispose` has
  called `GameAudio.Release` (FMOD `System.Close` and `Release`)
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1362-L1488)), so there is no audio system left; the
  guarded channel calls simply fail quietly.[^ksacars-mod][^ksa-program]
- The repo's notes on the sound API are in `docs/KSA-MODDING-NOTES.md`, "Sound: reachable, and
  shipped the same way art is".[^ksacars-notes]

## See also

- [Rocket roar from KSA's stock engine sound](./rocket-sound-without-engine.md)
- [Wheel, suspension and tyre model](./wheel-suspension-tyre-model.md) (where `Rpm` and `Load` come from)
- [Art and audio pipeline tooling](./art-and-audio-pipeline-tooling.md) (how the loops are cut from recordings)
- [Mod folder: content mod and code mod in one](./mod-folder-content-and-code-mod.md)
- [KSACars overview](../overview.md)
- [Cut your own loops below the engine's time-warp threshold](../../../howto/audio/channels/keep-mod-sounds-sensible-under-time-warp/keep-mod-sounds-sensible-under-time-warp-2.md) - how-to article demonstrating this feature
- [Stop on scene exit and tolerate a dead audio system](../../../howto/audio/channels/release-sound-channels-at-scene-change-and-exit/release-sound-channels-at-scene-change-and-exit-2.md) - how-to article demonstrating this feature
- [Hold one channel per mod-simulated source and move it every frame](../../../howto/audio/playback/play-a-sound-at-a-world-position-and-hold-its-channel/play-a-sound-at-a-world-position-and-hold-its-channel-2.md) - how-to article demonstrating this feature
- [Keep one looping channel per craft with guarded move and cut](../../../howto/audio/playback/play-a-sound-at-a-world-position-and-hold-its-channel/play-a-sound-at-a-world-position-and-hold-its-channel-3.md) - how-to article demonstrating this feature
- [Crossfade an idle loop and a load loop by RPM, with loops cut from recordings](../../../howto/audio/playback/play-a-spin-up-loop-tail-sound-and-crossfade-engine-loops/play-a-spin-up-loop-tail-sound-and-crossfade-engine-loops-1.md) - how-to article demonstrating this feature
- [Declare looping layers that share one SpatialSoundData asset](../../../howto/audio/sound-assets/ship-custom-sounds-as-xml-assets/ship-custom-sounds-as-xml-assets-2.md) - how-to article demonstrating this feature
- [Read the sound type map through the features that use each type](../../../howto/audio/sound-assets/understand-the-games-sound-and-music-xml-types/understand-the-games-sound-and-music-xml-types.md) - how-to article demonstrating this feature

[^ksacars-sounds-xml]: src/KSACars/KSACarsSounds.xml
[^ksacars-modtoml]: src/KSACars/mod.toml
[^ksacars-buggysound]: src/KSACars/Ksa/BuggySound.cs
[^ksacars-soundchannels]: src/KSACars/Ksa/SoundChannels.cs
[^ksacars-profile]: src/KSACars/Sim/BuggyProfile.cs
[^ksacars-buggydrive]: src/KSACars/Sim/BuggyDrive.cs
[^ksacars-mod]: src/KSACars/Ksa/KSACarsMod.cs
[^ksacars-notes]: docs/KSA-MODDING-NOTES.md
[^ksa-assetbundle]: KSA.AssetBundle (decompiled)
[^ksa-soundreference]: KSA.SoundReference (decompiled)
[^ksa-soundfilereference]: KSA.SoundFileReference (decompiled)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled)
[^ksa-gameaudio]: KSA.GameAudio (decompiled)
[^ksa-channelwrapper]: KSA.ChannelWrapper (decompiled)
[^ksa-spatialaudio]: KSA.SpatialAudio (decompiled)
[^ksa-atmosphere]: KSA.PhysicalAtmosphereReference (decompiled)
[^ksa-program]: KSA.Program (decompiled)
[^ksa-core-sounds]: Core/Sounds.xml
