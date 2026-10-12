---
type: How-To
title: Fire a positional sound with GameAudio.PlaySound and SpatialAudio(Astronomical, offset)
description: Play a declared sound at an arbitrary world point by anchoring a SpatialAudio on a live vehicle near it, so pan, attenuation, Doppler and air muffling are the engine's job
category: audio
group: playback
topic: play-a-sound-at-a-world-position-and-hold-its-channel
approach: 1
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/world-positioned-sounds]
techniques: [fmod-audio, xml-asset]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [startup:asset-load, frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.GameAudio, KSA.SpatialAudio, KSA.SoundBehavior, KSA.IChannel, KSA.ModLibrary, KSA.Vehicle]
tags: [audio, fmod, spatial-audio, doppler, one-shot, game-audio]
---

Outcome: a one-shot sound (a putter striking a ball, a ball landing) that is heard where the event happened. It pans
with the camera, fades with distance, picks up Doppler from the relative motion and goes dull in thin air, without
the mod computing any of that. Prerequisites: a StarMap mod whose `mod.toml` lists a sounds XML in `assets` (see
[ship custom sounds as XML assets](../../sound-assets/ship-custom-sounds-as-xml-assets/overview.md)), and a
live `Vehicle` near the sound to anchor on. The [KSAGolf](https://github.com/LaurensDeV/KSAGolf) mod demonstrates it
for the putter strike and the ball landing.

This article is fire-and-forget: the returned channel is not kept. For a sound that must follow a moving source see
[approach 2](play-a-sound-at-a-world-position-and-hold-its-channel-2.md) and
[approach 3](play-a-sound-at-a-world-position-and-hold-its-channel-3.md).

## Background: how the game does it

`GameAudio.PlaySound(SoundBehavior, SpatialAudio, out IChannel?, float volume = 1, bool startPaused = false, bool alwaysPlay = false)`
is the engine's own entry point for a placed sound. It returns no channel when `Universe.SimulationSpeed > 10` and
`alwaysPlay` is false; otherwise it calls the virtual `SoundBehavior.Play`.[current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L416-L423)[^ksa-gameaudio]
For a `<Sound>` that ends in `SoundReference.Play`, which starts the FMOD channel, wraps it in a `ChannelWrapper`,
applies the volume and the first `SetSpatialAudio` and registers the wrapper so `GameAudio.UpdateAudio` can
service it every frame.[current/decomp/KSA/SoundReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SoundReference.cs#L72-L89)[^ksa-soundreference]

The `SpatialAudio` you pass is a struct of three things in the audio camera's Ego frame (camera-relative
position and velocity, plus pressure in atmospheres). Its `SpatialAudio(Astronomical astronomical, double3 offset)`
constructor does the whole job for you: it reads `GameAudio.GetAudioCamera()`, takes
`camera.GetPositionEgo(astronomical) + offset`, takes `camera.GetVelocityEgo(astronomical)` and
`PhysicalAtmosphereReference.GetAtmosphericPressure(camera)`.[current/decomp/KSA/SpatialAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SpatialAudio.cs#L45-L53)[^ksa-spatialaudio]
So the velocity (and with it the Doppler shift) and the pressure belong to the anchor object, and only the position
is moved by the offset. A `Vehicle` is an `Astronomical`, which makes any live vehicle a valid anchor.[current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L29)[^ksa-vehicle]

Ego is the camera-relative frame whose axes are the ecliptic axes (`Camera.EclToEgo` subtracts the camera's
ecliptic position), so a difference of two ecliptic positions is also a valid Ego offset.[current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L229-L233)[^ksa-camera]
See [understand KSA coordinate frames and epochs](../../../flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md)
for the frames; the practical rule here is to subtract two positions read in the same frame.

## Lifecycle and timing

| Stage | Where | Vocabulary |
|---|---|---|
| Load | `mod.toml` `assets` lists the sounds XML; each `<Sound>` registers itself as a `SoundBehavior` | `startup:asset-load` |
| Play | Inside the mod's per-frame step, which runs once per frame from whichever of three hooks reaches it first | `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame` |
| Apply | `GameAudio.UpdateAudio` in `Program.PostRender` re-applies volume, pitch and filters to the new channel | `frame:post-render` |

KSAGolf steps its simulation from the `[StarMapAfterGui]` postfix (HUD visible), a Harmony prefix on the private
`Program.OnFrameCelestials` (HUD hidden, still before the render) and `[StarMapAfterOnFrame]` as the last resort, so the
strike sound is issued on the same frame the club crosses the ball.[src/KSAGolf/Ksa/KSAGolfMod.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KSAGolfMod.cs#L110-L172)
All three are main-thread, after `PrepareFrame` has applied the physics step, so the anchor's position is settled
for the frame. Why those three hooks and not one: the
[game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and
[draw UI while the HUD is hidden](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md) explain the F2
behaviour. If your sound is triggered from a UI click, `StarMapAfterGui` alone is enough.

Call `GameAudio` only from the main thread: `GameAudio.ActiveChannels` is a plain `List` that `PostRender` walks on the main thread every frame.

## Steps

1. **Declare the sounds.** Put a `<Sound>` per variant in a mod XML and list the file in `mod.toml` `assets`. KSAGolf
   gives each sound the `ExternalChannel` group, two files (the engine picks one at random per play) and a
   positional range:

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

   Verified meanings: `ChannelId` is `SoundReference.ChannelGroupId`; `SimSpeed` is a `SimSpeedRule`; several
   `<SoundFile>` children are chosen by `SelectionRule`, default `Random`; `VolumeRange` becomes the FMOD 3D
   min/max distance; with `TwoDRange` min and max both 0 the 3D level clamps to 1, fully positional.[current/decomp/KSA/SoundReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SoundReference.cs#L16-L54)[current/decomp/KSA/SpatialSoundData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SpatialSoundData.cs#L53-L77)[^ksa-spatialsounddata]
   The [XML assets article](../../sound-assets/ship-custom-sounds-as-xml-assets/overview.md) covers every attribute.

2. **Choose the sound id and volume from the event.** Keep this logic free of game types so it can be unit tested.
   KSAGolf maps launch speed to a tap, a soft or a hard putt and ramps volume inside each band:

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

3. **Look the sound up and play it at the point.** Resolve the id with `ModLibrary.Get<SoundBehavior>`, build the
   `SpatialAudio` from a live vehicle plus the Ecl difference to the point, and call `GameAudio.PlaySound`:

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

   `KsaWorld.IsAlive(v)` is the mod's `v is { IsDisposed: false }` and `KsaWorld.PositionEcl(near)` is
   `near.GetPositionEcl()`.[src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L196-L196)[src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L312-L312)
   The `channel` is allowed to be null: `PlaySound` yields none above 10x simulation speed, and
   `SoundReference.Play` yields none if FMOD refuses the play call.

4. **Call it at the event.** The mod passes the kitten as the anchor and the ball's ecliptic position as the
   point, on the frame the club crosses the ball and on the frame the ball's impact counter changes:

   ```csharp
   // src/KSAGolf/Ksa/Golf.cs L638-L642
           if (_stroke.Update(dx, dtPlayer, _towards, _rig, out double speed))
           {
               _struckFromCcf = ball.PositionCcf;
               ball.Strike(_targetCcf * speed);
               GolfSounds.Play(StrikeSound.ForStrike(speed), kitten, BallEcl());
   ```

5. **Check the log.** Nothing is logged on success; `GolfSounds.LastPlayed` records the last id, volume and whether a
   channel was returned and is shown by the mod's debug panel. On a bad id the catch logs one warning
   (`could not play ..., so golf is silent this session`) and the mod stays silent from then on.

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/KSAGolfSounds.xml](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/KSAGolfSounds.xml) | Four `<Sound>` assets, six wav files under `Sounds/` |
| [src/KSAGolf/mod.toml](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/mod.toml) | `assets = ["KSAGolfProps.xml", "KSAGolfSounds.xml", "KSAGolfCourse.xml"]` |
| [src/KSAGolf/Sim/StrikeSound.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/StrikeSound.cs) | Speed bands to sound id and volume |
| [src/KSAGolf/Ksa/GolfSounds.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/GolfSounds.cs) | Lookup and positional play |
| [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs#L638-L643) | Call sites for strike and landing |

## Variations and gotchas

- **Unknown id throws.** `ModLibrary.Get<T>` throws `NullReferenceException` ("... is null for 'id'") instead of
  returning null, so the `is not { } behavior` test never takes its false branch and the surrounding `catch` is what
  handles a typo.[current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1059-L1112)[^ksa-modlibrary] Prefer `ModLibrary.TryGet<SoundBehavior>(id, out var behavior)` (same
  file, L776), which returns false instead of throwing.
- **Silent above 10x.** `GameAudio.PlaySound` returns no channel above 10x unless `alwaysPlay` is true. A one-shot at
  that speed is usually not worth hearing; see
  [keep mod sounds sensible under time warp](../../channels/keep-mod-sounds-sensible-under-time-warp/overview.md).
- **The anchor owns Doppler and pressure.** Anchor on a vehicle near the sound. A vehicle far away still gives a
  numerically valid position, but the velocity (Doppler) and the camera-relative geometry then belong to it.
- **Pressure is the listener's.** `PhysicalAtmosphereReference.GetAtmosphericPressure(camera)` is evaluated at the
  camera, not the source, and drives a low-pass from 160 Hz (vacuum) to 18 kHz (one atmosphere or more) in
  `ChannelWrapper.SetSpatialAudio`, so a camera in space hears almost nothing of a loud event.[current/decomp/KSA/ChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChannelWrapper.cs#L434-L454)[^ksa-channelwrapper]
- **Wrong top-level element in the mod's notes.** KSAGolf's notes say Core uses a `<SoundBehavior>` element. In build
  2026.10.10.5554 there is none in the `AssetBundle` element table or in Core's `Sounds.xml`; `SoundBehavior` is only
  the C# base type of `<Sound>`, `<CompoundSound>` and the others.[current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L55-L62)[^ksa-assetbundle]
- **No parameters set.** A sound declared with `<Param>` curves reads zero for every parameter when played by hand;
  see [drive a sound's Param curves from code](../drive-sound-parameters-from-code/overview.md).
- **Declarative alternative.** Parts with modules can name a `<SoundEvent>` in their game data and let the engine
  play it; a mod-simulated ball has no part, so the imperative call is the only route.

## See also

- [Approach 2: held channel per source, swept against the live roster](play-a-sound-at-a-world-position-and-hold-its-channel-2.md)
- [Approach 3: one looping channel per craft](play-a-sound-at-a-world-position-and-hold-its-channel-3.md)
- [Overview of this topic](overview.md)
- Mod feature: [world-positioned sounds](../../../../mods/ksagolf/features/world-positioned-sounds.md)
- [Ship custom sounds with a mod as XML sound assets](../../sound-assets/ship-custom-sounds-as-xml-assets/overview.md)
- [Keep mod-played sounds sensible under time warp](../../channels/keep-mod-sounds-sensible-under-time-warp/overview.md)
- [Understand KSA coordinate frames and epochs](../../../flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md)

[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
[^ksa-soundreference]: KSA.SoundReference (decompiled, build 2026.10.10.5554)
[^ksa-spatialaudio]: KSA.SpatialAudio (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-spatialsounddata]: KSA.SpatialSoundData (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-channelwrapper]: KSA.ChannelWrapper (decompiled, build 2026.10.10.5554)
[^ksa-assetbundle]: KSA.AssetBundle (decompiled, build 2026.10.10.5554)
