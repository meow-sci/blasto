---
type: How-To
title: Hold one channel per mod-simulated source and move it every frame
description: Build an Ego SpatialAudio by hand for sources the mod simulates, keep the returned IChannel, re-place it each frame after the source is stepped, and sweep the table against the live roster
category: audio
group: playback
topic: play-a-sound-at-a-world-position-and-hold-its-channel
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/spatial-gun-and-motor-sounds, mods/ksacars/features/engine-sound-crossfade]
techniques: [fmod-audio, held-channel]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame, frame:post-render]
game_types:
  - KSA.GameAudio
  - KSA.SpatialAudio
  - KSA.SoundBehavior
  - KSA.IChannel
  - KSA.CompoundChannelWrapper
  - KSA.Camera
  - KSA.PhysicalAtmosphereReference
  - KSA.ModLibrary
tags: [audio, fmod, spatial-audio, held-channel, doppler, game-audio, lifetime]
---

Outcome: a sound that stays attached to something the mod simulates itself (a cannon that fires 75 rounds a second, a
rocket motor on a shell that is not a KSA vehicle). The mod starts the channel once, moves it every frame, and
removes it when the source stops burning, firing, or ceases to exist. Prerequisites: StarMap and the sounds
already declared in XML ([XML assets](../../sound-assets/ship-custom-sounds-as-xml-assets/overview.md)). Demonstrated by
KSArmory (`GunSound`, `MotorSound`, `SoundChannels`), with KSACars using the same helpers for a car engine.

Use this when [approach 1](play-a-sound-at-a-world-position-and-hold-its-channel-1.md) does not fit: there is no live
vehicle to anchor on at the sound's position, or the sound lasts and must follow its source. The
[single looping channel per craft](play-a-sound-at-a-world-position-and-hold-its-channel-3.md) is the same idea reduced to
one source kind.

## Background: how the game does it

`GameAudio.PlaySound` is only a thin wrapper (a 10x gate, then `SoundBehavior.Play`).[current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L416-L423)[^ksa-gameaudio]
The part a mod needs is below it. `SoundBehavior.Play(SpatialAudio spatial, float volume, out IChannel? channel, bool startPaused)`
returns an `IChannel`, the handle that stays valid while the sound plays:[current/decomp/KSA/IChannel.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IChannel.cs#L3-L45)[^ksa-ichannel]

| `IChannel` member | Use while holding a channel |
|---|---|
| `SetSpatialAudio(SpatialAudio)` | Re-place the source: new Ego position, velocity and pressure |
| `IsPlaying()` / `IsVirtual()` | Is the channel still alive |
| `Stop(bool stopImmediate = false)` | End it; the sound's `FadeOut` (if any) runs unless `stopImmediate` |
| `PitchMultiplier`, `VolumeMultiplier` | Persistent multipliers, applied by the engine each frame |
| `SetParameter(KeyHash, float)` | Feed the sound's `<Param>` curves ([drive parameters](../drive-sound-parameters-from-code/overview.md)) |

The engine keeps every started `ChannelWrapper` in `GameAudio.ActiveChannels` and, once per frame in
`GameAudio.UpdateAudio`, stops and drops any whose FMOD channel is neither playing nor virtual.[current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L187-L210)
So a finished one-shot disappears by itself, a held channel that has been stopped (by you, by a fade, or by the
engine) reports `IsPlaying() == false`, and a mod that holds channels must treat that as "gone" and not touch it
again. FMOD calls inside `ChannelWrapper` are the non-throwing `Try*` forms, so a stale handle yields an error
`Result`, not an exception, but the mod's helpers still guard every call for cheap insurance.[current/decomp/KSA/ChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChannelWrapper.cs#L314-L318)[^ksa-channelwrapper]

The spatial data is a `SpatialAudio(double3 posEgo, double3 velEgo, double atmosphericPressure)`, built by hand
instead of from an `Astronomical`. Ego is the camera-relative frame, so the mod asks the audio camera for its
platform's Ego position and velocity and adds the mod-held offset of the source from that platform.[current/decomp/KSA/SpatialAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SpatialAudio.cs#L29-L34)

## Lifecycle and timing

| Moment | Where | Vocabulary |
|---|---|---|
| Assets | `mod.toml` `assets` | `startup:asset-load` |
| Per frame | After the mod has stepped its rounds, still inside the mod's one-per-frame step: `_motors.Update`, `_gunSound.Update` | `frame:draw-ui` (the `[StarMapAfterGui]` postfix), else `frame:celestials` (prefix on `Program.OnFrameCelestials`), else `frame:after-on-frame` |
| Sweep | Later in the same step, once all systems have updated | same |
| Apply | `GameAudio.UpdateAudio` in `PostRender` | `frame:post-render` |

The order inside the step matters: KSArmory runs the sound updates after the rounds have been stepped, "so a motor is
heard where its round now is rather than where it was at the start of the frame".[src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L871-L881)
Because the positions come from the same pass that integrated the rounds, they cannot lag a frame, and from
`StarMapAfterGui` the new values reach FMOD in the same frame's `PostRender`; from the `StarMapAfterOnFrame` fallback
they reach it one frame later. See the
[game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for the hooks and
[simulate bodies outside the vehicle system](../../../physics/mod-simulation/simulate-bodies-outside-the-vehicle-system/overview.md)
for how the rounds are stepped.

## Steps

1. **Pick the channel unit by what the ear hears.** KSArmory's Phalanx fires 75 rounds a second, which the ear reads as
   pitch, not rhythm, so one looping channel per firing system is held while it fires, not 75 one-shots. A 40 round
   a minute gun is the opposite: its gunshot is a one-shot per round. A rocket motor is a loop per burning round.
   The reasoning is the mod's own, stated in the class comment on `GunSound`.[src/KSArmory/Ksa/GunSound.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/GunSound.cs#L6-L25)

2. **Write the guarded helpers once.** The listener is `GameAudio.GetAudioCamera()`, the pressure is the one at the
   listener, and moving or cutting a channel never throws into the frame hook:

   ```csharp
   // src/KSArmory/Ksa/SoundChannels.cs L9-L51
   internal static class SoundChannels
   {
       /// <summary>The camera the engine hears from, or null when it cannot say.</summary>
       public static Camera? Listener()
       {
           try { return GameAudio.GetAudioCamera(); }
           catch { return null; }
       }

       /// <summary>The pressure at the listener, which is what decides how much of a sound survives the trip.</summary>
       public static double PressureAt(Camera listener)
       {
           // The listener's, not the source's: in vacuum at the ear nothing is heard however loud it is.
           try { return PhysicalAtmosphereReference.GetAtmosphericPressure(listener); }
           catch { return 1.0; }
       }

       /// <summary>
       /// Moves a held channel if it is still playing. False means it has stopped or broken, and the
       /// caller should drop it and start another.
       /// </summary>
       public static bool TryMove(IChannel channel, SpatialAudio spatial)
       {
           try
           {
               if (!channel.IsPlaying()) return false;

               channel.SetSpatialAudio(spatial);
               return true;
           }
           catch
           {
               return false;
           }
       }

       /// <summary>Stops a channel. Safe on one the engine has already reclaimed.</summary>
       public static void Cut(IChannel channel)
       {
           try { channel.Stop(); }
           catch { /* A channel the engine has already reclaimed is already stopped. */ }
       }
   }
   ```

   `TryMove` returns false when the channel has stopped, which tells the caller to drop it and start another.

3. **Build the spatial data from the platform plus the source's own offset.** KSArmory does not convert an absolute
   ecliptic position of each round to Ego. It asks the camera where the launching platform is now and adds the round's
   offset from that platform, "the one quantity the mod already keeps epoch-clean, and at 29.8 km/s the alternative is
   audibly in the wrong place":

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

   The caller computes `platformEgo = camera.GetPositionEgo(platform)`, `platformVelEgo = camera.GetVelocityEgo(platform)`
   and `pressure = SoundChannels.PressureAt(camera)` once per system per frame ([src/KSArmory/Ksa/MotorSound.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/MotorSound.cs#L44-L75)).
   For the frames and epochs behind "epoch-clean" see
   [understand KSA coordinate frames and epochs](../../../flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md).

4. **Hold, move, restart.** The table is keyed on the source; `Follow` above is the whole life cycle: look up the held
   channel, try to move it, if that fails forget it and start a fresh one, record the new one. The gun does the same
   keyed on the firing system, and stops by removing and cutting:

   ```csharp
   // src/KSArmory/Ksa/GunSound.cs L38-L60
       public void Update(IEffectSource system)
       {
           // Ahead of the firing gate: the last round of a burst is fired on the frame the cannon stop.
           PlayGunshot(system);

           if (!_config.CannonSound || !system.GunsFiring || system.Platform is not { } platform
               || (system.Profile.GunSoundId is null && system.Profile.GunshotSoundId is not null))
           {
               Silence(system);
               return;
           }

           if (!TrySpatial(platform, out SpatialAudio spatial)) return;

           if (_firing.TryGetValue(system, out IChannel? channel))
           {
               if (SoundChannels.TryMove(channel, spatial)) return;

               _firing.Remove(system);
           }

           if (Start(spatial, system) is { } started) _firing[system] = started;
       }
   ```

5. **Do not restart a loop from code.** Declare the loop with `<Loop />` in the XML and let FMOD loop it. The mod's
   comment: a once-a-frame check at 60 fps "is a whole round missing from the cycle". A `CompoundSound` with a tail
   (see [spin-up, loop and tail](../play-a-spin-up-loop-tail-sound-and-crossfade-engine-loops/overview.md)) ends by
   `Stop()`: the loop stops and the tail plays on its own.

6. **Sweep against the live roster.** A craft destroyed mid-burst never reaches `Update` again, so its channel would
   play for the session. Each frame, after every system has updated, compare the table with the live roster and cut
   what the roster no longer knows:

   ```csharp
   // src/KSArmory/Ksa/GunSound.cs L66-L83
       public void Sweep(WeaponSystems roster)
       {
           foreach (IEffectSource system in _firing.Keys)
           {
               if (!roster.Knows(system)) _stopped.Add(system);
           }
           foreach (IEffectSource system in _shotsHeard.Keys)
           {
               if (!roster.Knows(system) && !_stopped.Contains(system)) _stopped.Add(system);
           }

           foreach (IEffectSource system in _stopped)
           {
               Silence(system);
               _shotsHeard.Remove(system);
           }
           _stopped.Clear();
       }
   ```

   The mod calls the sweeps at the end of the step, once for each effect that holds a pooled emitter or channel
   (`_motors.Sweep(_roster)`, `_gunSound.Sweep(_roster)`).[src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L966-L972)
   `MotorSound.Update` also removes rounds that left the system's list without burning out, and every owner is
   stored beside the channel (`(IRoundsInFlight Owner, IChannel Channel)`) so one system's sweep never cuts another
   system's rounds.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/SoundChannels.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/SoundChannels.cs) | Listener, pressure, guarded move and cut |
| [src/KSArmory/Ksa/MotorSound.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/MotorSound.cs) | One loop per burning round; sweep of orphans |
| [src/KSArmory/Ksa/GunSound.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/GunSound.cs) | One loop per firing system, gunshots per round, retune |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L866-L972) | Update after the step, then the sweeps |
| [src/KSArmory/KSArmorySounds.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmorySounds.xml) | The declared loop, tail and compound sounds |

The same `SoundChannels` file is copied verbatim into KSACars, where it holds a car's engine loops
([approach 3](play-a-sound-at-a-world-position-and-hold-its-channel-3.md)).

## Variations and gotchas

- **Bypasses the 10x gate, not `StopForSimSpeed`.** Calling `SoundBehavior.Play` directly skips
  `GameAudio.PlaySound`'s early-out, but every `ChannelWrapper` is still visited by `StopForSimSpeed` each frame and
  is stopped above 10x unless its `<Sound>` sets `SimSpeed="Ignore"`.[current/decomp/KSA/ChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChannelWrapper.cs#L497-L503) A mod that
  restarts a channel whenever `TryMove` fails will therefore start (and have the engine stop) a channel every frame
  at high warp. Gate on `Universe.SimulationSpeed` yourself; see
  [keep mod sounds sensible under time warp](../../channels/keep-mod-sounds-sensible-under-time-warp/overview.md).
- **Sound ids throw on a typo.** `ModLibrary.Get<SoundBehavior>` throws; both KSArmory players catch it and warn once
  per id (`WarnOnce`, `_warnedMissing`) so a missing file costs one log line, not one per frame.
- **Borrowed ids resolve identically.** The same lookup finds a mod's sound and Core's. The motor plays Core's
  `DefaultEngineSoundBehavior` until the mod ships its own sample; that needs a `Throttle` parameter, see
  [drive a sound's Param curves](../drive-sound-parameters-from-code/overview.md) (approach 3).
- **Priority.** A gun's `<Sound>` carries `Priority="110"` so FMOD keeps it ahead of Core's explosions under its voice
  limit; see [ship custom sounds as XML assets](../../sound-assets/ship-custom-sounds-as-xml-assets/overview.md).
- **Pitch retune is a persistent multiplier.** `channel.PitchMultiplier = clamp(rpm / reference, 0.8, 1.25)` set once
  at start stays until set again; `CompoundChannelWrapper` forwards it to the start, loop and stop channels.[current/decomp/KSA/CompoundChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CompoundChannelWrapper.cs#L45-L67)[^ksa-compoundchannelwrapper]
- **Teardown.** Stopping the table at `[StarMapUnload]` looks sensible but FMOD is already closed by then; see
  [release sound channels at scene change and exit](../../channels/release-sound-channels-at-scene-change-and-exit/overview.md).
  Stop on world reload and when the source goes away instead.

## See also

- [Approach 1: GameAudio.PlaySound with SpatialAudio(Astronomical, offset)](play-a-sound-at-a-world-position-and-hold-its-channel-1.md)
- [Approach 3: one looping channel per craft](play-a-sound-at-a-world-position-and-hold-its-channel-3.md)
- [Overview of this topic](overview.md)
- Mod features: [spatial gun and motor sounds](../../../../mods/ksarmory/features/spatial-gun-and-motor-sounds.md), [engine sound from two crossfaded loops](../../../../mods/ksacars/features/engine-sound-crossfade.md)
- [Crossfade engine loops and play spin-up, loop and tail sounds](../play-a-spin-up-loop-tail-sound-and-crossfade-engine-loops/overview.md)
- [Contain exceptions in StarMap hooks](../../../getting-started/lifecycle/contain-exceptions-in-starmap-hooks/overview.md)

[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
[^ksa-ichannel]: KSA.IChannel (decompiled, build 2026.10.10.5554)
[^ksa-channelwrapper]: KSA.ChannelWrapper (decompiled, build 2026.10.10.5554)
[^ksa-compoundchannelwrapper]: KSA.CompoundChannelWrapper (decompiled, build 2026.10.10.5554)
