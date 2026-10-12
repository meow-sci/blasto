---
type: How-To
title: Cut your own loops below the engine's time-warp threshold
description: Play loops through SoundBehavior.Play, which skips the 10x entry gate, and cut them yourself above a lower warp threshold where input swings would only make noise, then restart them when the world slows again
category: audio
group: channels
topic: keep-mod-sounds-sensible-under-time-warp
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksacars/features/engine-sound-crossfade, mods/ksacars/features/rocket-sound-without-engine]
techniques: [fmod-audio]
hooks: [starmap StarMapAfterGui, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:post-render]
game_types: [KSA.GameAudio, KSA.ChannelWrapper, KSA.SimSpeedRule, KSA.SoundBehavior, KSA.Universe, KSA.IChannel]
tags: [audio, time-warp, loops, engine-sound, threshold]
---

Outcome: continuous sounds whose pitch and volume you drive from game state (an engine from RPM) that go quiet at
high warp instead of warbling, and come back when the world slows. Prerequisites: looping `Sound` assets
([ship custom sounds, article 2](../../sound-assets/ship-custom-sounds-as-xml-assets/ship-custom-sounds-as-xml-assets-2.md))
and held channels ([hold a channel](../../playback/play-a-sound-at-a-world-position-and-hold-its-channel/overview.md)).
Demonstrated by KSACars's buggy engine loops, and contrasted with its rocket roar.

## Background: how the game does it

Three facts decide this design:[^ksa-gameaudio][^ksa-channelwrapper]

1. `SoundBehavior.Play` (what the mod calls) has no speed check; only `GameAudio.PlaySound` does.[^ksa-soundbehavior]
2. Every channel that `Play` creates is registered in `ActiveChannels`, so `StopForSimSpeed` stops it above 10x
   unless the `Sound` says `SimSpeed="Ignore"`, and with the default `All` rule `HandleSimSpeedPitch` raises
   its pitch to as much as 3x on the way there.
3. A mod that re-pitches a loop from RPM every frame is fighting that: under warp RPM swings many times a second.

KSACars's buggy loops declare no `SimSpeed` (see `KSACarsSounds.xml`), so they keep the default `All` rule: below
the mod's 4x cut the engine still multiplies their pitch by `1 + speed/5` on top of the RPM pitch (1.8x just under
the cut). Above 4x the mod cuts them itself, well before the engine's own stop at 10x. This is derived from the
declarations and the decomp, not measured in game.

## Lifecycle and timing

The cut is evaluated in the mod's per-frame sound update, on the main thread, from the frame hook the mod already
uses (`frame:draw-ui`); the engine's own handling runs afterwards in `GameAudio.UpdateAudio` (`frame:post-render`).
Because the cut is by the mod, it takes effect the same frame the speed passes the threshold, not one update later.
See the [game loop](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Name the threshold and why.** The constant carries the reason, which is the part to copy:

   ```csharp
   // src/KSACars/Ksa/BuggySound.cs L21-L23
   // Past this the engine is silent. Under warp its RPM swings many times a second and the loops are
   // re-pitched to match, which is noise rather than an engine.
   private const double LoudestSimSpeed = 4.0;
   ```

2. **Read the speed and cut.** `KsaWorld.SimulationSpeed` is `Universe.SimulationSpeed`. Above the threshold the mod
   cuts both loops **without** playing the key-off sound, because the engine is still "running":

   ```csharp
   // src/KSACars/Ksa/BuggySound.cs L64-L70
   // Cut rather than stopped: the engine is still running, so no key-off going in and no
   // starter coming out.
   if (KsaWorld.SimulationSpeed > LoudestSimSpeed)
   {
       Cut(engine);
       continue;
   }
   ```

3. **Restart for free.** `Cut` clears the held channel fields. The next frame at or below the threshold, `Keep` finds
   no channel and plays a new one, so the loops return without any state to restore:

   ```csharp
   // src/KSACars/Ksa/BuggySound.cs L115-L136
   private IChannel? Keep(IChannel? channel, string id, SpatialAudio spatial, float volume)
   {
       if (channel is not null && SoundChannels.TryMove(channel, spatial)) return channel;
       return Play(id, spatial, volume);
   }

   private void Stop(Vehicle craft, Camera? camera)
   {
       if (!_running.Remove(craft, out Engine? engine)) return;
       Cut(engine);
       if (camera is not null && TrySpatial(camera, craft, out SpatialAudio spatial))
       {
           PlayOnce(engine.Profile.SoundPrefix + "Stop", spatial, 1f);
       }
   }

   private static void Cut(Engine engine)
   {
       if (engine.Idle is not null) SoundChannels.Cut(engine.Idle);
       if (engine.Load is not null) SoundChannels.Cut(engine.Load);
       engine.Idle = engine.Load = null;
   }
   ```

4. **For one-shots, use the engine's gate.** KSACars's rocket roar plays Core's engine sound through the declarative
   overload, which is refused above 10x; the mod does not special-case it and simply retries every frame:

   ```csharp
   // src/KSACars/Ksa/RocketSound.cs L70-L73
   // Started paused, as KSA starts an engine's: the parameters are set before a sample is heard.
   GameAudio.PlaySound(new SoundEvent { SoundId = SoundId }, spatial, out channel, e.Craft, 1f, startPaused: true);
   if (channel is null) return;
   _burning[e.Craft] = channel;
   ```

## Complete example

| File | Role |
|---|---|
| [src/KSACars/Ksa/BuggySound.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/BuggySound.cs) | Threshold, cut, `Keep` and `Play` |
| [src/KSACars/Ksa/RocketSound.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/RocketSound.cs) | The gated alternative: Core's engine sound through `GameAudio.PlaySound(SoundEvent, ...)` |
| [src/KSACars/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.cs#L250-L255) | `SimulationSpeed` and `IsPaused` wrappers over `Universe` |

## Variations and gotchas

- **Your cut must sit below the engine's.** At 4x the engine would pitch a default-rule loop up to 1.8x
  (`1 + 4/5`) and stop it at just over 10x; your cut decides what the player hears in between.
- **Paused is speed 0.** With the default rule the sim-pitch factor is 0, so a loop halts rather than stopping; the
  buggy's `Cut` only fires above the threshold, so a paused world's loops are handled by the engine.
- **A pooled start or stop sound is a separate channel.** The buggy's one-shot start/stop sounds are played with
  `SoundBehavior.Play` too; above 10x they would be stopped by `StopForSimSpeed` as soon as they start.
- **Teardown.** The mod calls `StopAll` when the car leaves the flight scene and at unload; the unload call cannot
  reach a live mixer (see [release sound channels](../release-sound-channels-at-scene-change-and-exit/overview.md)).

## See also

- [Overview of this topic](overview.md)
- [Approach 1: declare a SimSpeed rule and let the engine handle it](keep-mod-sounds-sensible-under-time-warp-1.md)
- [Approach 3: channels the game never registers](keep-mod-sounds-sensible-under-time-warp-3.md)
- [Crossfade engine loops and play spin-up, loop and tail sounds](../../playback/play-a-spin-up-loop-tail-sound-and-crossfade-engine-loops/overview.md)
- Mod features: [engine sound from two crossfaded loops](../../../../mods/ksacars/features/engine-sound-crossfade.md), [rocket roar without an engine](../../../../mods/ksacars/features/rocket-sound-without-engine.md)

[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
[^ksa-channelwrapper]: KSA.ChannelWrapper (decompiled, build 2026.10.10.5554)
[^ksa-soundbehavior]: KSA.SoundBehavior (decompiled, build 2026.10.10.5554)
