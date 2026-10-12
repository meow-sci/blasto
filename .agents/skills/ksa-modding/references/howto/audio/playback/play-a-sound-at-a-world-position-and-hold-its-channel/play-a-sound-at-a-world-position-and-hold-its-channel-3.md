---
type: How-To
title: Keep one looping channel per craft with guarded move and cut
description: Attach a looping sound to a vehicle for as long as it exists, with a per-craft channel table, TryMove and Cut wrappers around every call, and a dead channel replaced on the next frame
category: audio
group: playback
topic: play-a-sound-at-a-world-position-and-hold-its-channel
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksacars/features/engine-sound-crossfade, mods/ksacars/features/rocket-sound-without-engine]
techniques: [fmod-audio, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, starmap StarMapAfterOnFrame]
lifecycle: [startup:asset-load, frame:draw-ui, frame:after-on-frame, frame:post-render]
game_types:
  - KSA.GameAudio
  - KSA.SpatialAudio
  - KSA.SoundBehavior
  - KSA.IChannel
  - KSA.Vehicle
  - KSA.Camera
  - KSA.ModLibrary
tags: [audio, fmod, held-channel, loop, vehicle, lifetime, game-audio]
---

Outcome: a car (or any craft you know about) makes a continuous sound that follows it, starts when it becomes the
active craft, is re-placed every frame, is replaced by a fresh channel if the engine drops the old one, and is cut
when the craft goes away, the scene ends or the mod faults. Prerequisites: StarMap, a sounds XML with `<Loop />` sounds
([XML assets](../../sound-assets/ship-custom-sounds-as-xml-assets/overview.md)). Demonstrated by KSACars
(`BuggySound`, `RocketSound`, `SoundChannels`).

It is [approach 2](play-a-sound-at-a-world-position-and-hold-its-channel-2.md) reduced to its smallest form: one
key (the `Vehicle`), one or two channels per key, and the same guarded `TryMove`/`Cut` helpers.

## Background: how the game does it

A looping `<Sound>` (a `<SoundFile>` with a `<Loop />` child) sets FMOD loop mode `LoopNormal` on the sound, so a
channel started once plays until it is stopped.[current/decomp/KSA/SoundFileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SoundFileReference.cs#L29-L40)[^ksa-soundfilereference]
The channel is a `ChannelWrapper` registered in `GameAudio.ActiveChannels`, and every frame `GameAudio.UpdateAudio`
stops and drops a channel that is neither playing nor virtual.[current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L187-L210)[^ksa-gameaudio]
Anything that stops a channel (a `Stop()` from the mod, a fade-out completing, `StopForSimSpeed` above 10x) leaves
`IsPlaying()` false, so "is it still playing" is the only liveness test a mod needs. One exception: a `<Sound>` that
declares `<FadeOut>` keeps `IsPlaying()` true while `Stop()` fades it (`ChannelWrapper.Stop` only sets the fade state;
`Stop(stopImmediate: true)` ends it at once), so a held channel with a fade is not replaced until the fade finishes.
The KSACars loops declare no fade.

`ChannelWrapper.VolumeMultiplier` and `PitchMultiplier` are persistent: `UpdateVolume`/`UpdatePitch` multiply them with
the sound's own, fade and parameter factors every frame and reset only the parameter factors afterwards.[current/decomp/KSA/ChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChannelWrapper.cs#L248-L273)[^ksa-channelwrapper]
A value set from the mod therefore keeps applying until the mod sets another.

## Lifecycle and timing

| Moment | Hook | Vocabulary |
|---|---|---|
| Assets | `mod.toml` `assets` includes `KSACarsSounds.xml` | `startup:asset-load` |
| Every frame, HUD shown | `[StarMapAfterGui]` postfix on `Program.OnDrawUiViewports` calls `StepOnce`, which calls `BuggySound.Update` and `RocketSound.Update` | `frame:draw-ui` |
| Every frame, HUD hidden | `[StarMapAfterOnFrame]` postfix on `Program.OnFrame` calls the same `StepOnce`; a `FrameLatch` makes it a no-op on frames the GUI hook already ran | `frame:after-on-frame` |
| Apply | `GameAudio.UpdateAudio` in `Program.PostRender` applies the multipliers set this frame | `frame:post-render` |

`StepOnce` is claimed once per frame, so whichever hook fires first does the work.[src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs#L60-L130)
It runs on the main thread, before the frame's `PostRender`, so from `StarMapAfterGui` the values set here reach FMOD
in the same frame; from the `StarMapAfterOnFrame` fallback they are applied one frame later.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2336-L2370)[^ksa-program]
The same gating pattern is described in the
[game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and
[draw UI while the HUD is hidden](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md).

## Steps

1. **Copy the guarded helpers.** KSACars carries the same `SoundChannels` as KSArmory (listener from
   `GameAudio.GetAudioCamera()`, pressure at the listener, `TryMove`, `Cut`); see
   [approach 2 step 2](play-a-sound-at-a-world-position-and-hold-its-channel-2.md) for the listing, which is the same file
   ([src/KSACars/Ksa/SoundChannels.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/SoundChannels.cs#L26-L50)).
   `TryMove` returning false is the signal "this channel is dead".

2. **Keep a table of channels per craft.** One `Engine` record per active car holds its two loop channels:

   ```csharp
   // src/KSACars/Ksa/BuggySound.cs L28-L38
       private sealed class Engine
       {
           public required BuggyProfile Profile;
           public long StartedMs;
           public IChannel? Idle;
           public IChannel? Load;
       }

       private readonly Dictionary<Vehicle, Engine> _running = [];
       private readonly List<Vehicle> _stopped = [];
       private readonly HashSet<string> _warned = [];
   ```

3. **Each frame, for every car, decide whether it should be heard.** KSACars makes noise only for the craft the
   player is flying (`KsaWorld.ControlledVehicle`) and only with a listener; anything else is stopped:

   ```csharp
   // src/KSACars/Ksa/BuggySound.cs L40-L83
       public void Update(Buggies buggies, float volume)
       {
           Vehicle? flown = KsaWorld.ControlledVehicle;
           Camera? camera = SoundChannels.Listener();

           foreach (Buggies.Entry e in buggies.All)
           {
               bool on = ReferenceEquals(e.Craft, flown) && camera is not null && volume > 0f;
               if (!on)
               {
                   Stop(e.Craft, camera);
                   continue;
               }

               if (!TrySpatial(camera!, e.Craft, out SpatialAudio spatial)) continue;

               if (!_running.TryGetValue(e.Craft, out Engine? engine))
               {
                   engine = new Engine { Profile = e.Drive.Profile, StartedMs = Environment.TickCount64 };
                   _running[e.Craft] = engine;
                   PlayOnce(engine.Profile.SoundPrefix + "Start", spatial, volume);
               }
               if (Environment.TickCount64 - engine.StartedMs < CatchesAfterMs) continue;

               // Cut rather than stopped: the engine is still running, so no key-off going in and no
               // starter coming out.
               if (KsaWorld.SimulationSpeed > LoudestSimSpeed)
               {
                   Cut(engine);
                   continue;
               }

               engine.Idle = Keep(engine.Idle, engine.Profile.SoundPrefix + "Idle", spatial, volume);
               engine.Load = Keep(engine.Load, engine.Profile.SoundPrefix + "Load", spatial, volume);
               Mix(engine, e.Drive, volume);
           }

           foreach (Vehicle v in _running.Keys)
           {
               if (!buggies.All.Any(b => ReferenceEquals(b.Craft, v))) _stopped.Add(v);
           }
           foreach (Vehicle v in _stopped) Stop(v, camera);
           _stopped.Clear();
       }
   ```

   The last loop is the sweep: a car that vanished from the registry is stopped even though it no longer reaches the
   first loop.

4. **Build the spatial data from the craft.** The position and velocity are Ego values from the audio camera and the
   pressure is the listener's:

   ```csharp
   // src/KSACars/Ksa/BuggySound.cs L159-L168
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

   For a source that is a real `Vehicle` this is the same result as `new SpatialAudio(craft, double3.Zero)`, which
   `RocketSound` in the same mod uses; the hand-built form adds the finite check and a single shared camera.

5. **Move the channel, or replace it.** `Keep` is the entire dead-channel policy: a live channel is moved, anything
   else (null, stopped, reclaimed by the engine) is replaced by a fresh `Play` this frame:

   ```csharp
   // src/KSACars/Ksa/BuggySound.cs L115-L119
       private IChannel? Keep(IChannel? channel, string id, SpatialAudio spatial, float volume)
       {
           if (channel is not null && SoundChannels.TryMove(channel, spatial)) return channel;
           return Play(id, spatial, volume);
       }
   ```

   `RocketSound` uses the same policy for its single channel: `fresh = !_burning.TryGetValue(...) || !channel.IsPlaying()`
   cuts the old channel, drops it and starts a new one.[src/KSACars/Ksa/RocketSound.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/RocketSound.cs#L53-L82)

6. **Set the per-frame mix after placing.** The idle and load layers get their volume and pitch every frame with
   guarded property writes (`Set`), see [crossfade engine loops](../play-a-spin-up-loop-tail-sound-and-crossfade-engine-loops/overview.md).

7. **Cut when the craft, the scene or the mod goes away.** `Stop` cuts both loops and plays a one-shot key-off;
   `StopAll` is called when the player leaves a flight scene, when the fault limiter disables the mod, and at
   `[StarMapUnload]`:

   ```csharp
   // src/KSACars/Ksa/KSACarsMod.cs L112-L121
       private void StepOnce()
       {
           if (!_frame.Claim()) return;

           if (!KsaWorld.InFlightScene)
           {
               _buggySound.StopAll();
           _rocketSound.StopAll();
               return;
           }
   ```

   (The odd indentation of `_rocketSound.StopAll()` is in the original.) Stopping at unload is the part that does
   nothing: see [release sound channels at scene change and exit](../../channels/release-sound-channels-at-scene-change-and-exit/overview.md).

## Complete example

| File | Role |
|---|---|
| [src/KSACars/Ksa/BuggySound.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/BuggySound.cs) | Per-craft table, start, keep, mix, stop, 4x cut |
| [src/KSACars/Ksa/RocketSound.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/RocketSound.cs) | One channel per car with dead-channel replacement |
| [src/KSACars/Ksa/SoundChannels.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/SoundChannels.cs) | Guarded helpers |
| [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs#L60-L176) | `StepOnce`, both hooks, `StopAll` on scene exit, fault and unload |
| [src/KSACars/KSACarsSounds.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsSounds.xml) | The `Start`, `Idle` (loop), `Load` (loop) and `Stop` sounds |

## Variations and gotchas

- **The engine reclaims channels.** `GameAudio.UpdateAudio` drops a channel that is neither playing nor virtual, so
  every held channel can disappear between frames. The guard-and-replace pattern is the only safe use.
- **Real time for the starter.** `BuggySound` waits `CatchesAfterMs = 2000` of wall-clock time
  (`Environment.TickCount64`) before bringing the loops in, so the wait does not stretch with warp or freeze when the
  game is paused.
- **Warp cut.** Above 4x simulation speed the loops are cut without a key-off because RPM swings would only make
  noise. That sits below the engine's own 10x `StopForSimSpeed` and below `HandleSimSpeedPitch`'s pitch scaling; see
  [keep mod sounds sensible under time warp](../../channels/keep-mod-sounds-sensible-under-time-warp/overview.md).
- **A missing id is silent.** A sounds XML left out of `mod.toml` `assets` is never loaded, and `ModLibrary.Get<SoundBehavior>`
  then throws; `Play` catches it and warns once per id.
- **Only the flown craft is heard.** Two cars playing engine loops from the same listener would stack voices; the
  mod's `on` test is the simplest voice budget.
- **Unverified rocket mix.** `RocketSound` borrows Core's engine sound; the mod's author records that it has not been
  listened to. See [drive a sound's Param curves from code](../drive-sound-parameters-from-code/overview.md), approach 1,
  for the parameters and for a step the mod does not perform.

## See also

- [Approach 1: GameAudio.PlaySound with SpatialAudio(Astronomical, offset)](play-a-sound-at-a-world-position-and-hold-its-channel-1.md)
- [Approach 2: held channel per source, swept against the live roster](play-a-sound-at-a-world-position-and-hold-its-channel-2.md)
- [Overview of this topic](overview.md)
- Mod features: [engine sound from two crossfaded loops](../../../../mods/ksacars/features/engine-sound-crossfade.md), [rocket roar from KSA's stock engine sound](../../../../mods/ksacars/features/rocket-sound-without-engine.md)
- [Stop and release sound channels at scene change and game exit](../../channels/release-sound-channels-at-scene-change-and-exit/overview.md)
- [Contain exceptions in StarMap hooks](../../../getting-started/lifecycle/contain-exceptions-in-starmap-hooks/overview.md)

[^ksa-soundfilereference]: KSA.SoundFileReference (decompiled, build 2026.10.10.5554)
[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
[^ksa-channelwrapper]: KSA.ChannelWrapper (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
