---
type: How-To
title: Play Core's engine sound for a mod-simulated rocket by mirroring Vehicle.UpdateAudio
description: Reuse DefaultEngineSoundBehavior for pushes the mod applies itself by starting it paused through GameAudio.PlaySound, feeding Throttle, Distance, Pressure and Iva every frame, and unpausing once
category: audio
group: playback
topic: drive-sound-parameters-from-code
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksacars/features/rocket-sound-without-engine]
techniques: [fmod-audio]
hooks: [starmap StarMapAfterGui, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:after-on-frame, frame:post-render, startup:asset-load]
game_types:
  - KSA.GameAudio
  - KSA.SoundEvent
  - KSA.CompoundSound
  - KSA.CompoundChannelWrapper
  - KSA.ClusterSound
  - KSA.Vehicle
  - KSA.ViewportRegistry
  - KSA.SpatialAudio
  - KSA.KeyHash
tags: [audio, fmod, parameters, engine-sound, rocket, game-audio, reuse]
---

Outcome: a craft whose "rockets" are pushes the mod applies itself (so KSA sees no engine and plays nothing) gets the
stock rocket roar, loud and pitched with the mod's own throttle, quieter with distance, muffled in thin air and in the
cabin. Prerequisites: StarMap. KSACars demonstrates it (`RocketSound`) for its Eldorado's lift, boost and downforce
rockets. Its author records that this mix was built from the decompiled source and has not been listened to in
game, so treat the result as unverified and use the checks at the end.

## Background: how the game does it

A real nozzle's sound is a `<SoundEvent>` in its part data naming a sound id. Core's id is
`DefaultEngineSoundBehavior`, a `<CompoundSound>` of a cluster sound, a start sound, the looping `DefaultEngine`
sound and a stop sound.[current/Content/Core/Sounds.xml](Core/Sounds.xml (build 2026.10.10.5554)#L299-L352)[^ksa-core-sounds] `DefaultEngine` maps `Throttle` to volume
(0.1 at 0, 1 at 1) and pitch (0.5 at 0, 1 at 1), `Distance` to a low-pass (18 kHz at 0 m to 800 Hz at 100 m) and
`Iva` to volume (reversed, 1 outside the cabin down to 0.25 inside):

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

`Vehicle.UpdateAudio` is the code the mod mirrors. For each nozzle with a sound event it builds
`new SpatialAudio(this, offset)`, starts the channel with `GameAudio.PlaySound(soundEvent, spatial, out channel, this, 1f, startPaused)`
when there is none, then sets the four parameters and, on the starting frame only, unpauses the channel and its
cluster channel.[current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6127-L6189)[^ksa-vehicle]

The `PlaySound(SoundEvent ...)` overload does three things the plain `PlaySound(SoundBehavior ...)` does not: it looks the
id up with `ModLibrary.Get<SoundBehavior>`, it attaches the sound's `ClusterSound` (far-field engine layers, shared by all
channels of the same parent) to the `IAudio` parent you pass, and it subscribes the channel to that cluster.[current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L383-L414)[^ksa-gameaudio]
A `Vehicle` is an `IAudio`, so the car itself is the parent. Like every `PlaySound`, it returns no channel above 10x
simulation speed.

## Lifecycle and timing

| Moment | Where | Vocabulary |
|---|---|---|
| Sound known | Core's `Sounds.xml` is loaded with the game | `startup:asset-load` |
| Update | `RocketSound.Update` runs from the mod's `StepOnce`, right after `BuggySound.Update`: from `[StarMapAfterGui]` when the HUD is shown, from `[StarMapAfterOnFrame]` when it is hidden | `frame:draw-ui`, `frame:after-on-frame` |
| Apply | `GameAudio.UpdateAudio` in `PostRender`: first `UpdateAudio()` on every registered `IAudio` (vehicles), then per channel `ApplyParameters`, `UpdateVolume`, `UpdatePitch`, then the cluster channels' spatial update | `frame:post-render` |

Parameters set from `StarMapAfterGui` are therefore consumed in the same frame's apply pass; from the
`StarMapAfterOnFrame` fallback, one frame later.[src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs#L112-L130) The burn level the sound reads
(`Entry.Boosting`, `Rockets`, `Pressed`) is written in the `Vehicle.PrepareWorker` prefix earlier in the frame, so the
sound follows the push handed to the physics step dispatched this frame. Everything is main-thread; see the
[game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and
[inject forces and impulses at the physics safe point](../../../physics/solver/inject-forces-and-impulses-at-the-physics-safe-point/overview.md).

## Steps

1. **Hash the parameter names once.** `KeyHash.Make` takes a span of the id; build them in static fields (the names are
   exactly the `Id` attributes of the `<Param>` elements):

   ```csharp
   // src/KSACars/Ksa/RocketSound.cs L12-L17
       private const string SoundId = "DefaultEngineSoundBehavior";

       private static readonly KeyHash Throttle = KeyHash.Make("Throttle".AsSpan());
       private static readonly KeyHash Distance = KeyHash.Make("Distance".AsSpan());
       private static readonly KeyHash Pressure = KeyHash.Make("Pressure".AsSpan());
       private static readonly KeyHash Iva = KeyHash.Make("Iva".AsSpan());
   ```

2. **Map the mod's state to the engine's input.** In place of `RocketNozzleState.AverageThrustFraction`, the mod's burn
   level: full for the boost, else the larger of the lift throttle and 0.35 while the downforce rockets fire. No burn,
   or a craft that is gone, stops the channel.

3. **Start paused, place, set the parameters, unpause once.** The whole loop, verbatim:

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

   `SoundEvent` has one public field, `SoundId`, and is declared `required`.[current/decomp/KSA/SoundEvent.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SoundEvent.cs#L5-L8) The channel returned is a
   `CompoundChannelWrapper`; its `SetSpatialAudio`, `SetParameter` and `SetPaused` fan out to the start, loop and stop
   channels, and `SetParameter` also reaches the subscribed cluster channel.[current/decomp/KSA/CompoundChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CompoundChannelWrapper.cs#L94-L114)

4. **Unpause the cluster too (the step the mod does not perform).** The engine's own code, after unpausing the channel,
   also unpauses the subscribed cluster channel if it is paused, because `PlaySound(SoundEvent ...)` started the cluster
   paused as well (`startPaused` is passed to `ClusterSound.Play`). KSACars calls `SetPaused(false)` on the channel only. By
   static reading, the cluster layers (`DefaultEngineCluster`: two boost loops, the burner and distant-rocket loops, the interior low-end bed) then stay paused for
   as long as any channel is subscribed. This was not heard in game. Adapted from `Vehicle.UpdateAudio`
   (L6165-L6172), to add after the unpause:

   ```csharp
   // adapted from current/decomp/KSA/Vehicle.cs L6165-L6172
   if (fresh)
   {
       channel.SetPaused(isPaused: false);
       if (channel.SubscribedClusterSound is { } cluster && cluster.IsPaused())
       {
           cluster.SetPaused(isPaused: false);
       }
   }
   ```

5. **Stop with the parameters still set.** The engine sets the four parameters one last time before `Stop()`, so the
   fade-out is mixed with sensible values; the mod's `Stop(craft)` calls `SoundChannels.Cut(channel)` without that. Setting
   them before `Stop()` is a cheap improvement.

## Complete example

| File | Role |
|---|---|
| [src/KSACars/Ksa/RocketSound.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/RocketSound.cs) | The whole feature |
| [src/KSACars/Ksa/SoundChannels.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/SoundChannels.cs) | `Cut`, the guarded stop |
| [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs#L112-L130) | `StepOnce` calls `_rocketSound.Update(_buggies)` |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L550-L566) | Where `Boosting`, `Rockets` and `Pressed` are set |

## Variations and gotchas

- **Unverified by ear.** The mod's notes say the exhaust plume was seen in game but this sound "has not been listened
  to". Check the log is quiet, then listen: Throttle should move volume and pitch, being inside the cabin should mute
  it, and thin air should dull it.
- **Parameters persist, products do not.** `SetParameter` stores a value; the engine maps it again every frame. Setting the
  four each frame is what the engine does. See the [overview](overview.md) for the mapping rule and why not to call
  `ApplyParameters()` by hand.
- **One channel at the car's centre.** KSA plays one sound per nozzle at its exhaust position outside map view. The mod
  plays one per car at the car's position whatever set of rockets burns, so there is no stereo spread between nozzles.
- **Audible range.** The engine gates a vehicle's nozzle sounds on `Vehicle.IsAudible()` (camera within
  `AudibleDistance`, 131072 m). The mod does not, so a car far from the camera still plays and is silenced by
  attenuation.
- **Above 10x** `PlaySound` yields no channel; the mod simply retries next frame. Under
  warp the channel's `SimSpeed` rule also applies (`DefaultEngine` does not set one, so the default `All` raises pitch).
  See [keep mod sounds sensible under time warp](../../channels/keep-mod-sounds-sensible-under-time-warp/overview.md).
- **Why not keep a real engine for effects.** A real nozzle only has sound and a plume while it has propellant flow and
  it also pushes the craft; see [draw exhaust plumes without an engine](../../../rendering/lights-and-particles/draw-exhaust-plumes-without-an-engine/overview.md).
- **A game update can rename `DefaultEngineSoundBehavior`.** `PlaySound(SoundEvent)` resolves it with the throwing
  `ModLibrary.Get`; a rename leaves the car silent, with the mod's one-time `rocket sound:` warning in its log.

## See also

- [Approach 2: explosion parameters set by hand on a MultiSound](drive-sound-parameters-from-code-2.md)
- [Approach 3: stock engine loop for a projectile with Throttle pinned open](drive-sound-parameters-from-code-3.md)
- [Overview of this topic](overview.md)
- Mod feature: [rocket roar from KSA's stock engine sound](../../../../mods/ksacars/features/rocket-sound-without-engine.md)
- [Hold one looping channel per craft](../play-a-sound-at-a-world-position-and-hold-its-channel/play-a-sound-at-a-world-position-and-hold-its-channel-3.md)
- [Understand KSA's sound, channel group and music playlist XML types](../../sound-assets/understand-the-games-sound-and-music-xml-types/overview.md)

[^ksa-core-sounds]: Core/Sounds.xml (build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
