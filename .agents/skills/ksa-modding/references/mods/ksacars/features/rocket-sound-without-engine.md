---
type: Mod Feature
title: Rocket roar from KSA's stock engine sound, with no engine
description: Play Core's DefaultEngineSoundBehavior on a channel the mod owns and feed it the Throttle, Distance, Pressure and Iva parameters each frame, exactly as Vehicle.UpdateAudio does for a real nozzle
mod: ksacars
category: audio
techniques: [fmod-audio, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:after-on-frame, frame:post-render]
game_types:
  - KSA.GameAudio
  - KSA.SoundEvent
  - KSA.SpatialAudio
  - KSA.IChannel
  - KSA.CompoundSound
  - KSA.CompoundChannelWrapper
  - KSA.ChannelWrapper
  - KSA.KeyHash
  - KSA.Vehicle
  - KSA.ViewportRegistry
  - KSA.IGameViewport
tags: [audio, fmod, engine-sound, sound-parameters, compound-sound, rockets]
---

The Eldorado's lift, boost and downforce rockets are the mod's own pushes, not KSA engines, so
the game would play nothing while they fire. `Ksa/RocketSound.cs` borrows KSA's stock rocket
engine sound instead: one channel per car, started paused, positioned at the car, and fed the same
four parameters KSA feeds a real nozzle's sound every frame.[^ksacars-rocketsound]

The mechanism is a re-implementation of the per-nozzle branch of `Vehicle.UpdateAudio` with the
mod's own throttle in place of `RocketNozzleState.AverageThrustFraction`.[^ksa-vehicle] The
author records that this sound was built from the decompiled source and **has not been listened
to** in game.[^ksacars-notes]

## How it works

1. `RocketSound.Update` walks every car each frame. The burn level is `1.0` while the boost fires,
   else `max(Rockets, Pressed ? 0.35 : 0)`, where `Rockets` is the lift throttle once the irises
   are open and `Pressed` means the downforce rockets are firing.[^ksacars-rocketsound][^ksacars-buggies]
2. No burn, or the craft is gone: the channel is stopped and forgotten.
3. A new channel is started when there is none or the old one stopped playing:
   `GameAudio.PlaySound(new SoundEvent { SoundId = "DefaultEngineSoundBehavior" }, spatial, out channel, e.Craft, 1f, startPaused: true)`.
   The car is passed as the `IAudio` parent, as `Vehicle.UpdateAudio` passes `this`.[^ksa-gameaudio]
4. Every frame: `SetSpatialAudio(new SpatialAudio(e.Craft, double3.Zero))`, then
   `SetParameter` for `Throttle` (the burn), `Distance` (`spatial.Distance()`), `Pressure`
   (`spatial.AtmosphericPressure`) and `Iva` (`ViewportRegistry.MainViewport.IvaAudio`), with
   `KeyHash.Make` hashes built once in static fields.
5. Only on the frame it was started: `SetPaused(false)`, so the first sample is heard with the
   parameters already applied.

What the parameters do comes from Core's data. `DefaultEngineSoundBehavior` is a
`<CompoundSound>` of a start sound, the looping `DefaultEngine` sound, a stop sound and the
`DefaultEngineCluster` cluster sound.[^ksa-core-sounds] `DefaultEngine` maps `Throttle` to volume
(0.1..1) and pitch (0.5..1), `Distance` to a low-pass filter and `Iva` to volume; the cluster's
layers read `Pressure`. `CompoundChannelWrapper.SetParameter` forwards each value to the start,
loop and stop channels and to the subscribed cluster channel.[^ksa-compoundchannelwrapper]

| Parameter | KSA's value in `Vehicle.UpdateAudio` | KSACars' value |
|---|---|---|
| `Throttle` | `RocketNozzleState.AverageThrustFraction` | boost ? 1 : max(lift throttle, downforce ? 0.35 : 0) |
| `Distance` | `spatialAudio.Distance()` | same |
| `Pressure` | `spatialAudio.AtmosphericPressure` | same |
| `Iva` | `ViewportRegistry.MainViewport.IvaAudio` | same |
| Position | `SpatialAudio(this, offset)` with the nozzle's exhaust location (outside map view) | `SpatialAudio(craft, double3.Zero)`: the car's centre |

## Lifecycle and timing

`RocketSound.Update` runs from the mod's per-frame `StepOnce`, right after `BuggySound.Update`:
from `[StarMapAfterGui]` (Harmony postfix on `Program.OnDrawUiViewports`) when the UI is drawn, or
from `[StarMapAfterOnFrame]` (postfix on `Program.OnFrame`) when it is hidden.[^ksacars-mod] Both
are on the main thread, where all of `GameAudio` lives.

KSA's own call happens later in the same frame: `Program.PostRender` calls
`GameAudio.UpdateAudio`, which calls `UpdateAudio()` on every registered `IAudio` (vehicles
included) and then, per active channel, `ApplyParameters`, `UpdateVolume` and
`UpdatePitch`.[^ksa-program][^ksa-gameaudio] Parameters the mod sets in `StarMapAfterGui` are
therefore consumed in the same frame's apply pass. `ChannelWrapper.SetParameter` only stores the
value; `ApplyParameters` maps it through the sound's `<Param>` curves.[^ksa-channelwrapper]

The burn level the sound reads (`Entry.Rockets`, `Boosting`, `Pressed`) is written in the
`Vehicle.PrepareWorker` prefix earlier in the frame, so the sound always follows the push that was
handed to the physics step dispatched this frame.[^ksacars-buggies]

## Key code

| Path | Role |
|---|---|
| [src/KSACars/Ksa/RocketSound.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/RocketSound.cs) | The whole feature: start paused, position, four parameters, stop |
| [src/KSACars/Ksa/SoundChannels.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/SoundChannels.cs#L45-L50) | `Cut`: stop that tolerates a reclaimed channel |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L550-L566) | Where `Boosting`, `Rockets` and `Pressed` are set each physics window |
| [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs#L112-L130) | `StepOnce` calls `_rocketSound.Update(_buggies)` |

## Snippets

```csharp
// src/KSACars/Ksa/RocketSound.cs L53-L82
    private void Update(Buggies.Entry e)
    {
        // The boost burns flat out, so it sounds as the lift rockets do at full throttle.
        double burn = e.Boosting ? 1.0 : Math.Max(e.Rockets, e.Pressed ? 0.35 : 0.0);
        if (burn <= 0.0 || !KsaWorld.IsAlive(e.Craft))
        {
            Stop(e.Craft);
            return;
        }

        SpatialAudio spatial = new(e.Craft, double3.Zero);
        bool fresh = !_burning.TryGetValue(e.Craft, out IChannel? channel) || !channel.IsPlaying();
        if (fresh)
        {
            if (channel is not null) SoundChannels.Cut(channel);
            _burning.Remove(e.Craft);

            // Started paused, as KSA starts an engine's: the parameters are set before a sample is heard.
            GameAudio.PlaySound(new SoundEvent { SoundId = SoundId }, spatial, out channel, e.Craft, 1f, startPaused: true);
            if (channel is null) return;
            _burning[e.Craft] = channel;
        }

        channel!.SetSpatialAudio(spatial);
        channel.SetParameter(Throttle, (float)burn);
        channel.SetParameter(Distance, (float)spatial.Distance());
        channel.SetParameter(Pressure, (float)spatial.AtmosphericPressure);
        channel.SetParameter(Iva, ViewportRegistry.MainViewport.IvaAudio);
        if (fresh) channel.SetPaused(isPaused: false);
    }
```

```xml
<!-- current/Content/Core/Sounds.xml L299-L309 and L347-L352 -->
    <Sound Id="DefaultEngine" ChannelId="EnginesChannel">
        <SoundFile Id="DefaultEngineSoundFile" />
        <SpatialSoundData Id="EngineSpatialData" />
        <!--<SoundGroup Id="EngineLoopSoundGroup"/> -->
        <FadeIn Time="0.2"/>
        <FadeOut Time="0.1" />
        <Param Type="Volume" Id="Throttle" InMin="0" InMax="1" OutMin="0.1" OutMax="1" />
        <Param Type="Pitch" Id="Throttle" InMin="0" InMax="1" OutMin="0.5" OutMax="1" />
        <Param Type="Lpf" Id="Distance" InMin="0" InMax="100" OutMin="18000" OutMax="800" />
        <Param Type="Volume" Id="Iva" InMin="0" InMax="1" OutMin="0.25" OutMax="1" Mode="Reverse"/>
    </Sound>
    <!-- ... -->
    <CompoundSound Id="DefaultEngineSoundBehavior">
        <ClusterSound Id="DefaultEngineCluster"/>
        <StartSound Id="DefaultEngineStart" />
        <Sound Id="DefaultEngine" />
        <StopSound Id="DefaultEngineStart" />
    </CompoundSound>
```

## Game APIs touched

| API | Decompiled source | Used for |
|---|---|---|
| `GameAudio.PlaySound(SoundEvent, SpatialAudio, out IChannel?, IAudio?, float, bool)` | [current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L383-L414) | Resolves the id with `ModLibrary.Get<SoundBehavior>`, plays it, attaches the cluster sound to the parent; returns nothing above 10x |
| `Vehicle.UpdateAudio` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6127-L6189) | The reference: per-nozzle start paused, four `SetParameter` calls, unpause |
| `Vehicle` sound hashes (`ThrottleHash`, `DistanceHash`, `PressureHash`, `IvaHash`) | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6961-L6966) | The parameter names the mod hashes itself |
| `CompoundSound.Play` | [current/decomp/KSA/CompoundSound.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CompoundSound.cs#L22-L31) | Starts start/loop/stop channels, returns a `CompoundChannelWrapper` |
| `CompoundChannelWrapper.SetParameter` / `SetSpatialAudio` / `SetPaused` | [current/decomp/KSA/CompoundChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CompoundChannelWrapper.cs#L94-L114) | Fans calls out to the child and cluster channels |
| `ChannelWrapper.ApplyParameters` / `SetParameter` | [current/decomp/KSA/ChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChannelWrapper.cs#L212-L246) | Maps stored parameter values through `<Param>` curves |
| `SpatialAudio(Astronomical, double3)` | [current/decomp/KSA/SpatialAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SpatialAudio.cs#L45-L53) | Position and velocity from the audio camera, listener pressure |
| `ViewportRegistry.MainViewport.IvaAudio` | [current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L50) | How far the listener is inside the cabin |
| `Program.PostRender` -> `GameAudio.UpdateAudio` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2336-L2370) | When the parameters are applied |

## Pitfalls and version notes

- **Not yet heard.** The repo says the plume was seen in game but this sound "has not been
  listened to".[^ksacars-notes] Treat the mix as unverified.
- **Silent above 10x.** `GameAudio.PlaySound` returns no channel when
  `Universe.SimulationSpeed > 10`, and the mod then simply retries next frame.[^ksa-gameaudio]
- **One channel at the centre.** KSA plays one sound per nozzle at its exhaust location; the mod
  plays one per car at the car's position, whatever set of rockets is burning.
- **Why not a real engine.** A real nozzle has no plume or sound without propellant and pushes the
  craft whenever it has flow; the mod's notes walk through why a stock engine cannot be kept for its
  effects alone.[^ksacars-notes] See [exhaust plumes without an engine](./exhaust-plumes-without-engine.md).
- **Reclaimed channels.** `IsPlaying()` false means KSA has dropped the channel; the mod `Cut`s it
  (inside a `try`) and starts a new one.[^ksacars-soundchannels]

## See also

- [Exhaust plumes without an engine](./exhaust-plumes-without-engine.md)
- [Rocket lift, hover, boost and downforce](./rocket-lift-hover-boost-downforce.md)
- [Engine sound from two crossfaded loops](./engine-sound-crossfade.md)
- [KSACars overview](../overview.md)
- [Cut your own loops below the engine's time-warp threshold](../../../howto/audio/channels/keep-mod-sounds-sensible-under-time-warp/keep-mod-sounds-sensible-under-time-warp-2.md) - how-to article demonstrating this feature
- [Stop on scene exit and tolerate a dead audio system](../../../howto/audio/channels/release-sound-channels-at-scene-change-and-exit/release-sound-channels-at-scene-change-and-exit-2.md) - how-to article demonstrating this feature
- [Play Core's engine sound for a mod-simulated rocket by mirroring Vehicle.UpdateAudio](../../../howto/audio/playback/drive-sound-parameters-from-code/drive-sound-parameters-from-code-1.md) - how-to article demonstrating this feature
- [Keep one looping channel per craft with guarded move and cut](../../../howto/audio/playback/play-a-sound-at-a-world-position-and-hold-its-channel/play-a-sound-at-a-world-position-and-hold-its-channel-3.md) - how-to article demonstrating this feature
- [Read the sound type map through the features that use each type](../../../howto/audio/sound-assets/understand-the-games-sound-and-music-xml-types/understand-the-games-sound-and-music-xml-types.md) - how-to article demonstrating this feature

[^ksacars-rocketsound]: src/KSACars/Ksa/RocketSound.cs
[^ksacars-soundchannels]: src/KSACars/Ksa/SoundChannels.cs
[^ksacars-buggies]: src/KSACars/Ksa/Buggies.cs
[^ksacars-mod]: src/KSACars/Ksa/KSACarsMod.cs
[^ksacars-notes]: docs/KSA-MODDING-NOTES.md, "An exhaust plume without an engine"
[^ksa-vehicle]: KSA.Vehicle (decompiled)
[^ksa-gameaudio]: KSA.GameAudio (decompiled)
[^ksa-compoundchannelwrapper]: KSA.CompoundChannelWrapper (decompiled)
[^ksa-channelwrapper]: KSA.ChannelWrapper (decompiled)
[^ksa-program]: KSA.Program (decompiled)
[^ksa-core-sounds]: Core/Sounds.xml
