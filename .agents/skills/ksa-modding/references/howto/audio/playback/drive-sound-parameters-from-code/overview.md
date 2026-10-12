---
type: Game Feature
title: Drive a sound's Param curves from code and reuse stock sounds
description: How the named parameters a sound's Param curves listen to are set with SetParameter and a KeyHash, when the engine applies them, and how to reuse Core's engine and explosion sounds for mod-simulated sources
category: audio
group: playback
topic: drive-sound-parameters-from-code
demonstrated_by:
  - mods/ksacars/features/rocket-sound-without-engine
  - mods/ksarmory/features/burst-sound-at-blast-arrival
  - mods/ksarmory/features/spatial-gun-and-motor-sounds
lifecycle:
  - frame:draw-ui
  - frame:post-render
  - frame:after-on-frame
  - startup:asset-load
  - frame:celestials
  - event:load
game_types:
  - KSA.IChannel
  - KSA.ChannelWrapper
  - KSA.CompoundChannelWrapper
  - KSA.MultiChannelWrapper
  - KSA.SoundParameterData
  - KSA.KeyHash
  - KSA.GameAudio
  - KSA.SoundEvent
  - KSA.CompoundSound
  - KSA.ClusterSound
  - KSA.Vehicle
  - KSA.ViewportRegistry
  - KSA.ExplosionSoundSystem
  - KSA.ExplosionSystem
  - KSA.Rendering.Particles.ExplosionReference
  - KSA.MultiSound
  - KSA.SoundBehavior
  - KSA.SpatialAudio
  - KSA.AssetBundle
  - KSA.ModLibrary
tags: [audio, fmod, parameters, game-audio, engine-sound, explosion-sound, reuse]
---

A sound asset in KSA can carry `<Param>` curves: small mappings from a named input (`Throttle`, `Distance`,
`Pressure`, `Iva`) to the sound's volume, pitch, low-pass or high-pass cutoff. Core's engine and explosion sounds are
mixed this way. The engine sets those inputs when it plays the sound itself; a sound that a mod starts by hand has
every input at zero until the mod sets it, which usually sounds wrong (an engine at a tenth of its level, an explosion
whose far report is silent at every range). This topic is how to set them, when the engine applies them, and the
three ways mods use it to borrow Core's sounds for things that are not KSA vehicles.

## Engine background

| Piece | What it does |
|---|---|
| `SoundParameterData` [current/decomp/KSA/SoundParameterData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SoundParameterData.cs#L6-L60) | One `<Param>`: `Type` (`Volume`, `Pitch`, `Lpf`, `Hpf`), `Id` (the input name), `InMin`/`InMax`/`OutMin`/`OutMax`, `Mode` (`Direct` or `Reverse`). `MapValue` clamps the input to `InMin..InMax`, maps it linearly to `OutMin..OutMax`, and with `Reverse` runs the input the other way[^ksa-soundparameterdata] |
| `SoundReference.OnDataLoad` [current/decomp/KSA/SoundReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SoundReference.cs#L127-L162) | Discards a `<Param>` whose four numbers or `Id` are missing, logging `Discarded Invalid SoundParameter for <mod>`; otherwise `IdHash = KeyHash.Make(Id)`[^ksa-soundreference] |
| `ChannelWrapper.SetParameter(KeyHash, float)` [current/decomp/KSA/ChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChannelWrapper.cs#L235-L246) | Stores the value for every `<Param>` whose `IdHash` matches (an unknown id matches nothing and is ignored) and forwards to a subscribed cluster channel[^ksa-channelwrapper] |
| `ChannelWrapper.ApplyParameters` [current/decomp/KSA/ChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChannelWrapper.cs#L212-L233) | Maps each stored value: `Volume` and `Pitch` multiply into the per-frame parameter multiplier, `Lpf` takes the minimum cutoff, `Hpf` the maximum |
| `GameAudio.UpdateAudio` [current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L187-L210) | For every active channel, every frame in `PostRender`: `ApplyParameters`, `UpdateVolume`, `UpdatePitch`, `UpdateLpf`, `UpdateHpf`[^ksa-gameaudio] |
| `UpdateVolume` / `UpdatePitch` [current/decomp/KSA/ChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChannelWrapper.cs#L248-L273) | Write `SoundVolume * VolumeMultiplier * Fade * ParameterVolume` (and the pitch equivalent) to FMOD, then reset the parameter multiplier to 1 while the channel is playing |
| `CompoundChannelWrapper.SetParameter` [current/decomp/KSA/CompoundChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CompoundChannelWrapper.cs#L108-L114) and `MultiChannelWrapper.SetParameter` [current/decomp/KSA/MultiChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MultiChannelWrapper.cs#L79-L86) | Fan one call out to the start, loop and stop channels, or to every layer, and to the cluster channel[^ksa-compoundchannelwrapper][^ksa-multichannelwrapper] |
| `Vehicle.UpdateAudio` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6127-L6189) | The reference for engines: per nozzle, starts the `SoundEvent` paused, sets `Throttle`, `Distance`, `Pressure`, `Iva`, unpauses, and unpauses the cluster channel too[^ksa-vehicle] |
| `ExplosionSoundSystem` [current/decomp/KSA/ExplosionSoundSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ExplosionSoundSystem.cs#L37-L108) | The reference for explosions: plays a preset's `SoundEvent` paused, sets `Distance`, `Pressure`, `Intensity`, unpauses, and re-places it every frame[^ksa-explosionsoundsystem] |
| `ExplosionSystem.SpawnPresetInternal` [current/decomp/KSA/ExplosionSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ExplosionSystem.cs#L253-L257) | Hands a preset's `Sound` (an `ExplosionReference` field, `<SoundEvent>` in XML) to `ExplosionSoundSystem.Spawn` right after the flash[^ksa-explosionsystem] |

How a parameter becomes sound, in order:

1. A value you pass to `SetParameter` is stored per `<Param>` and persists until you set it again; before the first call
   it is 0.
2. In `GameAudio.UpdateAudio` (`frame:post-render`) the stored value is mapped through the curve into a multiplier for
   this frame only; `UpdateVolume` writes the product and puts the multiplier back to 1.
3. So a parameter set in a frame hook (`frame:draw-ui`) is consumed in the same frame's `PostRender`, and one set from
   `StarMapAfterOnFrame` is consumed in the next frame's. Setting the value every frame is the engine's own habit and
   costs nothing; it is required only when the input changes.

Do not call `ApplyParameters()` yourself unless you also call `UpdateVolume()` and `UpdatePitch()`: it multiplies the
pending multiplier and `UpdateAudio` multiplies it again before it is reset, so a curve that maps to 0.5 gets 0.25 for
that first frame. (Both `GunSound` and `MotorSound` in KSArmory call it after a start; it is harmless there because the
pinned throttle maps to 1.0 and the gun sounds have no volume or pitch `<Param>`.)

Core's inputs in this build, from `Sounds.xml` and `ExplosionAssets.xml`:

| Input | Read by | Typical source |
|---|---|---|
| `Throttle` | `DefaultEngine` (volume 0.1..1, pitch 0.5..1), `DefaultEngineStart` | `RocketNozzleState.AverageThrustFraction` |
| `Distance` | `DefaultEngine` low-pass; the cluster layers; the explosion layers | `SpatialAudio.Distance()` |
| `Pressure` | the engine cluster and explosion echo volume | `SpatialAudio.AtmosphericPressure` |
| `Iva` | engine and ambience volumes, the `ExternalChannel` group | `IGameViewport.IvaAudio`, 0 outside the cabin, 1 inside |
| `Intensity` | set by `ExplosionSoundSystem`, but no `<Param>` in Core's XML in this build reads it | explosion energy |
| `Altitude`, `DynamicPressure`, `Grass`, `Desert`, `Beach`, `Poles` | ambience and wind sounds, set by the viewport's biome and environment controllers | engine only |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: Engine parameters mirrored from `Vehicle.UpdateAudio`](drive-sound-parameters-from-code-1.md) | KSACars | `fmod-audio` | `frame:draw-ui`, applied in `frame:post-render` | Full engine mix from `GameAudio.PlaySound(SoundEvent ...)`, including the cluster layers; copies engine logic and must mirror its unpause steps; author has not listened to it |
| [2: Explosion parameters set by hand on a `MultiSound`](drive-sound-parameters-from-code-2.md) | KSArmory | `fmod-audio` | `frame:draw-ui`, `frame:after-on-frame`, applied in `frame:post-render` | Plays Core's bang on the mod's schedule (arrival, pitch by yield); bypasses the engine's 10x and 50 km gates and its voice tracking |
| [3: Stock engine loop for a projectile with `Throttle` pinned open](drive-sound-parameters-from-code-3.md) | KSArmory | `fmod-audio` | `frame:draw-ui` | Smallest reuse: one parameter, one channel per round; direct `Play` means no cluster layers |

## See also

- [Understand KSA's sound, channel group and music playlist XML types](../../sound-assets/understand-the-games-sound-and-music-xml-types/overview.md)
- [Play a sound at a world position and hold its channel](../play-a-sound-at-a-world-position-and-hold-its-channel/overview.md)
- [Crossfade engine loops and play spin-up, loop and tail sounds](../play-a-spin-up-loop-tail-sound-and-crossfade-engine-loops/overview.md)
- [Delay a world sound by the time sound takes to reach the listener](../delay-a-sound-until-the-blast-front-arrives/overview.md)
- [Keep mod-played sounds sensible under time warp](../../channels/keep-mod-sounds-sensible-under-time-warp/overview.md)
- [Draw exhaust plumes without an engine](../../../rendering/lights-and-particles/draw-exhaust-plumes-without-an-engine/overview.md)
- [Spawn particle effects](../../../rendering/lights-and-particles/spawn-particle-effects/overview.md)
- [Inject forces and impulses at the physics safe point](../../../physics/solver/inject-forces-and-impulses-at-the-physics-safe-point/overview.md)

[^ksa-soundparameterdata]: KSA.SoundParameterData (decompiled, build 2026.10.10.5554)
[^ksa-soundreference]: KSA.SoundReference (decompiled, build 2026.10.10.5554)
[^ksa-channelwrapper]: KSA.ChannelWrapper (decompiled, build 2026.10.10.5554)
[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
[^ksa-compoundchannelwrapper]: KSA.CompoundChannelWrapper (decompiled, build 2026.10.10.5554)
[^ksa-multichannelwrapper]: KSA.MultiChannelWrapper (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-explosionsoundsystem]: KSA.ExplosionSoundSystem (decompiled, build 2026.10.10.5554)
[^ksa-explosionsystem]: KSA.ExplosionSystem (decompiled, build 2026.10.10.5554)
