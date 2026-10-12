---
type: How-To
title: Borrow Core's engine loop for a mod-simulated projectile with Throttle pinned open
description: Play DefaultEngineSoundBehavior from ModLibrary for each burning round and set Throttle to 1 on start, because Core's loop maps Throttle 0 to a tenth of its volume and half its pitch
category: audio
group: playback
topic: drive-sound-parameters-from-code
approach: 3
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/spatial-gun-and-motor-sounds]
techniques: [fmod-audio]
hooks: [starmap StarMapAfterGui]
lifecycle: [startup:asset-load, frame:draw-ui, frame:post-render]
game_types:
  - KSA.ModLibrary
  - KSA.SoundBehavior
  - KSA.CompoundSound
  - KSA.CompoundChannelWrapper
  - KSA.IChannel
  - KSA.KeyHash
  - KSA.SpatialAudio
tags: [audio, fmod, parameters, engine-sound, projectile, game-audio, reuse]
---

Outcome: a rocket-propelled round that the mod simulates (so it is not a KSA vehicle and has no engine module) is
heard burning, using Core's own engine loop until the mod ships a sample of its own. Prerequisites: StarMap. KSArmory's
`MotorSound` demonstrates it. It is the smallest case of
[driving a sound's parameters](overview.md): one input, set once, to a value that is "wide open" because a rocket
motor has one setting.

## Background: how the game does it

`ModLibrary.Get<SoundBehavior>(id)` resolves any registered sound id: a mod's own `<Sound>` and Core's alike, because
both register in the same table.[current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1059-L1112)[^ksa-modlibrary] So code that plays a Core sound by id keeps working
unchanged when the mod later declares its own sound under that id, or when a config names another id.

Core's `DefaultEngineSoundBehavior` is a `CompoundSound`. Its looping `DefaultEngine` sound is shaped by a `Throttle`
parameter: `Throttle` 0 maps to 0.1 times the volume and 0.5 times the pitch, `Throttle` 1 to full volume and pitch.[current/Content/Core/Sounds.xml](Core/Sounds.xml (build 2026.10.10.5554)#L299-L309)[^ksa-core-sounds]
A channel's parameter values start at zero (the `_parameterValues` array is never initialised to anything else), so a
sound played by hand without setting `Throttle` plays at a tenth of its level an octave down.[current/decomp/KSA/ChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChannelWrapper.cs#L212-L246)[^ksa-channelwrapper]

`SoundBehavior.Play` called directly on a `CompoundSound` plays its start sound and loop and holds the stop sound
paused, and returns one `CompoundChannelWrapper`.[current/decomp/KSA/CompoundSound.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CompoundSound.cs#L22-L31)[^ksa-compoundsound] Unlike `GameAudio.PlaySound(SoundEvent ...)` it does not
attach the sound's `ClusterSound`, so the far-field cluster layers are not part of what you hear.

## Lifecycle and timing

| Moment | Where | Vocabulary |
|---|---|---|
| Lookup and start | `MotorSound.Start`, first frame a round is burning, inside the mod's step | `frame:draw-ui` (normally `[StarMapAfterGui]`) |
| Move and stop | `MotorSound.Update` each frame, after the rounds were stepped | `frame:draw-ui` |
| Parameter applied | `GameAudio.UpdateAudio` in `PostRender`; the stored `Throttle` value persists, so one `SetParameter` call at the start holds for the life of the channel | `frame:post-render` |
| Sound known | Core's `Sounds.xml` | `startup:asset-load` |

The per-round channel lifecycle (move, restart, sweep) is
[approach 2 of the world-position topic](../play-a-sound-at-a-world-position-and-hold-its-channel/play-a-sound-at-a-world-position-and-hold-its-channel-2.md);
this article only adds the stock sound and its parameter.

## Steps

1. **Pick the default id and keep it overridable.** The Core loop resolves on every install; a mod-supplied id only
   resolves once that asset ships. The id comes from config, null meaning Core's:

   ```csharp
   // src/KSArmory/Ksa/MotorSound.cs L23-L32
       // Core's engine loop, standing in until the mod ships its own sample: it resolves on every
       // install, which a mod-supplied Id does not until the asset is actually there.
       private const string DefaultMotorId = "DefaultEngineSoundBehavior";

       // Core's engine loop is driven by a Throttle parameter, and everything about how it sounds
       // hangs off it: Sounds.xml maps throttle 0 to 0.1x volume AND 0.5x pitch. Left unset it plays
       // at a tenth of its level, an octave down. A rocket motor has one setting, so this is pinned
       // wide open rather than being anything the round decides.
       private static readonly KeyHash ThrottleHash = KeyHash.Make("Throttle");
       private const float FullThrottle = 1f;
   ```

2. **Start one channel per burning round and pin the throttle.** Look up the id, `Play` with the spatial data you
   built, and set `Throttle` on the returned channel:

   ```csharp
   // src/KSArmory/Ksa/MotorSound.cs L146-L170
       private static IChannel? Start(SpatialAudio spatial, Config config)
       {
           try
           {
               if (ModLibrary.Get<SoundBehavior>(config.MotorSoundId ?? DefaultMotorId) is not { } sound)
               {
                   if (!_warnedMissing)
                   {
                       _warnedMissing = true;
                       Log.Warn($"motor sound '{config.MotorSoundId ?? DefaultMotorId}' does not resolve; "
                                + "rounds will be silent");
                   }
                   return null;
               }

               sound.Play(spatial, config.MotorVolume, out IChannel? channel);

               if (channel is not null)
               {
                   channel.SetParameter(ThrottleHash, FullThrottle);
                   channel.ApplyParameters();
               }

               return channel;
           }
   ```

   (The `catch` that logs `motor sound failed to start` is the remaining lines of the method.) `KeyHash.Make("Throttle")`
   works with a string because the overload takes a `ReadOnlySpan<char>`.

3. **Let the table move it each frame.** `Follow` (see approach 2 of the world-position topic) re-places the channel
   every frame and calls `Start` again if `TryMove` says it died.

4. **Stop at burnout.** `Silence(round)` calls `SoundChannels.Cut(channel)`, i.e. `Stop()`. For a `CompoundChannelWrapper`
   that stops the start and loop channels (honouring their `FadeOut`) and unpauses the stop channel, which for
   `DefaultEngineSoundBehavior` is `DefaultEngineStart` again, so burnout plays Core's start clip.[current/decomp/KSA/CompoundChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CompoundChannelWrapper.cs#L79-L92)[^ksa-compoundchannelwrapper]
   `Stop(stopImmediate: true)` skips the tail entirely.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/MotorSound.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/MotorSound.cs) | The whole player: default id, throttle pin, per-round table, sweep |
| [src/KSArmory/Sim/Config.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Config.cs#L28-L35) and [src/KSArmory/Sim/Config.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Config.cs#L112-L116) | `MotorSound`, `MotorVolume` (default 0.7), and `MotorSoundId` (null means Core's loop) |

## Variations and gotchas

- **Unset parameters read zero.** Any Core sound with `<Param>` curves played by hand needs its inputs set; read the sound's
  XML for the `Id` values. Here only `Throttle` matters: with `Distance` 0 the low-pass is fully open, and with `Iva`
  0 (reversed) the volume is at its maximum, which is the right default for "outside the cabin", while the
  spatial filter in `SetSpatialAudio` still applies the real distance.
- **Calling `ApplyParameters()` by hand.** `Start` calls it right after the set. It is harmless here because a throttle of 1
  maps to a factor of 1.0, but it is the pattern the [overview](overview.md) advises against for curves that map to
  other values.
- **One channel, centre of the round.** There is no cluster, no per-nozzle position and no stereo spread; a shell with a
  tracer is one point source.
- **Silent above 10x, noisy in between.** The channel is stopped by `StopForSimSpeed` above 10x; a table that restarts a
  dead channel every frame will keep restarting it. Gate on simulation speed; see
  [keep mod sounds sensible under time warp](../../channels/keep-mod-sounds-sensible-under-time-warp/overview.md).
- **Swapping to a custom sample.** Because the id comes from config, a later `<CompoundSound>` or `<Sound>` with its own
  `Throttle` curves needs no code change; a sound with no `Throttle` curve simply ignores the parameter. See
  [ship custom sounds as XML assets](../../sound-assets/ship-custom-sounds-as-xml-assets/overview.md).
- **Do not borrow without checking the id.** `ModLibrary.Get` throws on a missing id; the lookup is inside the `try`.

## See also

- [Approach 1: engine parameters mirrored from Vehicle.UpdateAudio](drive-sound-parameters-from-code-1.md)
- [Approach 2: explosion parameters set by hand on a MultiSound](drive-sound-parameters-from-code-2.md)
- [Overview of this topic](overview.md)
- Mod feature: [spatial gun and motor sounds](../../../../mods/ksarmory/features/spatial-gun-and-motor-sounds.md)
- [Hold one channel per mod-simulated source](../play-a-sound-at-a-world-position-and-hold-its-channel/play-a-sound-at-a-world-position-and-hold-its-channel-2.md)
- [Understand KSA's sound, channel group and music playlist XML types](../../sound-assets/understand-the-games-sound-and-music-xml-types/overview.md)

[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-core-sounds]: Core/Sounds.xml (build 2026.10.10.5554)
[^ksa-channelwrapper]: KSA.ChannelWrapper (decompiled, build 2026.10.10.5554)
[^ksa-compoundsound]: KSA.CompoundSound (decompiled, build 2026.10.10.5554)
[^ksa-compoundchannelwrapper]: KSA.CompoundChannelWrapper (decompiled, build 2026.10.10.5554)
