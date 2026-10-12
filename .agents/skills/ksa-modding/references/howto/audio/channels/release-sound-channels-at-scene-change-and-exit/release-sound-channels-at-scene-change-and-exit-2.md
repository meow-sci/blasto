---
type: How-To
title: Stop on scene exit and tolerate a dead audio system
description: Stop held channels when the craft leaves the flight scene or the mod disables itself, treat the StarMapUnload StopAll as moot, and wrap every channel call so a reclaimed or dead channel costs a quiet frame
category: audio
group: channels
topic: release-sound-channels-at-scene-change-and-exit
approach: 2
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksacars/features/engine-sound-crossfade, mods/ksacars/features/rocket-sound-without-engine]
techniques: [starmap-lifecycle, fmod-audio]
hooks: [starmap StarMapAfterGui, starmap StarMapAfterOnFrame, starmap StarMapUnload]
lifecycle: [frame:draw-ui, event:shutdown]
game_types: [KSA.Program, KSA.GameAudio, KSA.IChannel, Brutal.FmodApi.FmodSystem, KSA.Universe]
tags: [audio, lifecycle, cleanup, scene, fault-limiter, guarded-calls]
---

Outcome: engine and rocket sounds that end when the player leaves the flight scene or the mod shuts itself off, and
a mod that never throws into the frame because a channel went away. Prerequisites: a StarMap mod with a per-frame
step. Demonstrated by KSACars.

## Background: how the game does it

Channels you start through `SoundBehavior.Play` are wrappers over FMOD channels in `GameAudio.ActiveChannels`; the
engine drops one when it is neither playing nor virtual, and otherwise leaves it alone.[^ksa-gameaudio] When the
scene goes (the editor opens, the system unloads) your table still holds them. At exit, `Program.Dispose` calls
`GameAudio.Release()` after the window is terminated: `System.Close()` then `System.Release()`. `[StarMapUnload]`
runs after that.[^ksa-program]

## Lifecycle and timing

| Moment | Where | Vocabulary |
|---|---|---|
| Per frame | `StepOnce`, claimed once a frame by `[StarMapAfterGui]`, or the `[StarMapAfterOnFrame]` fallback with the HUD hidden | `frame:draw-ui` |
| Fault limit reached | `Fault`, from whichever hook threw | same |
| Exit | `[StarMapUnload]` | `event:shutdown`, after FMOD is closed |

## Steps

1. **Stop when the scene is not a flight scene.** The step starts by asking whether there is a flight scene at all and
   stops every channel when there is not. The scene test is "no editor and a current system", which is wider than
   "the player has a craft":

   ```csharp
   // src/KSACars/Ksa/KSACarsMod.cs L116-L121
   if (!KsaWorld.InFlightScene)
   {
       _buggySound.StopAll();
   _rocketSound.StopAll();
       return;
   }
   ```

   ```csharp
   // src/KSACars/Ksa/KsaWorld.cs L228-L241
   /// <summary>The flight scene is up, whether or not the player has a craft in it.</summary>
   public static bool InFlightScene
   {
       get
       {
           try
           {
               return Program.Editor is null && Universe.CurrentSystem is not null;
           }
           catch
           {
               return false;
           }
       }
   ```

2. **Stop when the mod gives up.** After too many faults the mod disables itself and its first act is `StopAll` on both
   sound players, so a mod that fails does not leave an engine droning:

   ```csharp
   // src/KSACars/Ksa/KSACarsMod.cs L154-L163
   private void Fault(string where, Exception e)
   {
       _faults++;
       Log.Error($"{where} failed ({_faults}/{FaultLimit})", e);

       if (_faults < FaultLimit) return;

       _disabled = true;
       _buggySound.StopAll();
       _rocketSound.StopAll();
   ```

3. **Stop when the source goes.** Both players walk their table each frame and stop an entry whose craft is no longer in
   the roster (see `Stop(Vehicle, Camera?)` in `BuggySound`); a destroyed car never reaches the update again, so a
   sweep is the only thing that notices.[^ksacars-buggysound]

4. **Keep the unload call, knowing what it is.** `Unload` calls `StopAll` first. The channel calls inside are
   guarded, and the call is moot: StarMap invokes `[StarMapUnload]` only after `Program.Dispose` has closed FMOD, so
   there is no audio system left and the guarded calls simply fail quietly.

   ```csharp
   // src/KSACars/Ksa/KSACarsMod.cs L132-L138
   [StarMapUnload]
   public void Unload()
   {
       // Audio channels belong to the game and nothing else gives them back.
       _buggySound.StopAll();
       _rocketSound.StopAll();
       _buggies.Clear();
   ```

   The comment in the source ("Audio channels belong to the game and nothing else gives them back") is true while the
   game runs and false at exit.

5. **Guard every call on a held channel.** A channel the engine reclaimed answers `IsPlaying() == false`; a wrapper
   method can still throw. Keep the guards tiny:

   ```csharp
   // src/KSACars/Ksa/SoundChannels.cs L26-L50
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
   ```

   `Stop()` on a `ChannelWrapper` ends in `Channel.TryStop()`, which returns a `Result` and does not throw; the guard is
   for the wrapper layers above it. What native FMOD does with a handle of a released system was not tested.

## Complete example

| File | Role |
|---|---|
| [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs#L112-L163) | `StepOnce`, `Unload`, `Fault` |
| [src/KSACars/Ksa/SoundChannels.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/SoundChannels.cs) | `TryMove` and `Cut` |
| [src/KSACars/Ksa/BuggySound.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/BuggySound.cs#L85-L89) | `StopAll` for the engine loops |

## Variations and gotchas

- **Scene exit is the real cleanup.** If you only stop at unload, a long session with channels left over after every
  scene change leaks them until exit.
- **Pair it with a load hook** when sources can outlive a save load; see
  [approach 1](release-sound-channels-at-scene-change-and-exit-1.md).
- **Do not put anything else in `Unload` that touches the game.** The renderer, ImGui and audio are gone; see
  [unload hook and shutdown order](../../../../mods/starmap/features/unload-and-shutdown.md).
- **Environment.Exit paths skip `StarMapUnload`.** Nothing audio-related depends on it.
- **Keep exceptions inside the hook.** See [keep mod exceptions out of KSA's frame loop](../../../getting-started/lifecycle/contain-exceptions-in-starmap-hooks/overview.md).

## See also

- [Overview of this topic](overview.md)
- [Approach 1: StopAll at StarMapUnload, plus Stop(stopImmediate) on world reload](release-sound-channels-at-scene-change-and-exit-1.md)
- [Approach 3: dispose all streams on save reset](release-sound-channels-at-scene-change-and-exit-3.md)
- [Clean up on game exit and relaunch the game from a mod](../../../getting-started/lifecycle/clean-up-on-game-exit/overview.md)
- Mod features: [engine sound from two crossfaded loops](../../../../mods/ksacars/features/engine-sound-crossfade.md), [rocket roar without an engine](../../../../mods/ksacars/features/rocket-sound-without-engine.md)

[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksacars-buggysound]: KSACars: src/KSACars/Ksa/BuggySound.cs
