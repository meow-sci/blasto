---
type: How-To
title: Play channels the game never registers so warp rules do not touch them
description: Create FMOD channels directly on GameAudio.System and the game's Sfx group so HandleSimSpeedPitch and StopForSimSpeed never see them, keeping stable pitch and sound in warp while still following the player's Sfx and Master sliders
category: audio
group: channels
topic: keep-mod-sounds-sensible-under-time-warp
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/byo-music-vessel-3d-audio]
techniques: [fmod-audio]
hooks: [starmap StarMapBeforeGui]
lifecycle: [frame:draw-ui, frame:post-render]
game_types:
  - KSA.GameAudio
  - KSA.ChannelWrapper
  - KSA.ChannelGroupType
  - KSA.ChannelGroupReference
  - Brutal.FmodApi.Channel
  - KSA.Universe
tags: [audio, time-warp, sliders, unregistered-channels, doppler]
---

Outcome: music or narration that plays at constant pitch in space and at any time-warp, yet still obeys the player's
Sfx and Master volume. Prerequisites: [article 2 of the FMOD-files topic](../play-sound-files-through-the-games-fmod-system/play-sound-files-through-the-games-fmod-system-2.md)
(stream, open state, play on the Sfx group). Demonstrated by BYO Music in the unscience mod.

## Background: how the game does it

`GameAudio.UpdateAudio` walks `ActiveChannels` only. A `ChannelWrapper` enters that list in `SoundReference.Play`
(`GameAudio.Register`); a channel made by `GameAudio.System.TryPlaySound` is a bare FMOD `Channel` and is never in it.
So `HandleSimSpeedPitch`, `StopForSimSpeed`, `HandleFade`, `ApplyParameters` and the volume, pitch and filter updates
never touch it.[^ksa-gameaudio][^ksa-channelwrapper] The player's sliders still apply, because they set volume on the
FMOD channel **groups** (`Master`, `Sfx`, `Ui`, `Music`), and the bare channel plays in one of them.[^ksa-channelgroupreference]

## Lifecycle and timing

Same loop as the FMOD-files articles: the tick runs in the mod's per-frame hook (`frame:draw-ui`), FMOD commits at
`frame:post-render`. What changes is what does **not** run: nothing in `GameAudio.UpdateAudio` will fix, fade or stop
your channel. See the [game loop](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Create the channel yourself, on a game group.** Start paused so you can configure it:

   ```csharp
   // byo-music.lib/VesselSound.cs L104-L111
   private void Start()
   {
       var group = ModLibrary.Get<ChannelGroupReference>("Sfx").ChannelGroup;
       Check(GameAudio.System.TryPlaySound(_sound, group, true, out _channel), "play sound");
       _hasChannel = true;
       ApplySettings(); // Configure position and gain while paused: no one-frame sound at the origin.
       Check(_channel.TrySetPaused(false), "start sound");
   }
   ```

   `ModLibrary.Get<ChannelGroupReference>("Sfx").ChannelGroup` is the same group as
   `GameAudio.GetChannelGroup(ChannelGroupType.Sfx)`. Use `Music` or `Ui` to follow those sliders instead.

2. **Keep the pitch stable.** BYO Music turns FMOD's Doppler off for the channel and never sets a pitch, so warp and
   relative velocity do not change what you hear:

   ```csharp
   // byo-music.lib/VesselSound.cs L120-L124
   // Match stock SpatialAudio's camera-relative positions. Keep music pitch stable in warp.
   var spatial = new SpatialAudio(Target);
   Check(_channel.TrySet3dAttributes(spatial.PositionView(), spatial.VelocityView()), "follow vessel");
   Check(_channel.TrySet3dSpread(0), "position stereo files as a point source");
   Check(_channel.TrySet3dDopplerLevel(0), "keep playback pitch stable");
   ```

3. **Own the stop.** Nothing stops the channel for you at high warp or when the source goes away; BYO Music stops it
   when the vessel disappears, on the user's command and at `Dispose` (see
   [release sound channels](../release-sound-channels-at-scene-change-and-exit/release-sound-channels-at-scene-change-and-exit-3.md)).

4. **To follow part of the engine's rule yourself, compute it.** Adapted from `ChannelWrapper.HandleSimSpeedPitch`
   for an `OnlyLower`-like behaviour on a bare channel (set it each frame; not part of BYO Music):

   ```csharp
   // adapted from KSA.ChannelWrapper.HandleSimSpeedPitch (current/decomp/KSA/ChannelWrapper.cs L456-L495)
   static float SimPitch(double simulationSpeed)
       => simulationSpeed == 0.0 ? 0f : simulationSpeed < 1.0 ? (float)Math.Max(simulationSpeed, 0.25) : 1f;
   // channel.TrySetPitch(SimPitch(Universe.SimulationSpeed));
   ```

## Complete example

| File | Role |
|---|---|
| [byo-music.lib/VesselSound.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music.lib/VesselSound.cs) | Direct `TryPlaySound` on the Sfx group, Doppler 0 |
| [byo-music.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music.lib/README.md) | States the behaviour: real-time playback, in space and in warp, with stable pitch |

## Variations and gotchas

- **What you give up.** No fades (`FadeIn`/`FadeOut`), no `<Param>` curves, no pitch or volume smoothing from the
  wrapper, no entry in the Audio Debug window or its channel count, and no automatic cleanup of your table of
  channels.
- **No atmospheric muffling.** The low-pass that follows air pressure lives in `ChannelWrapper.SetSpatialAudio`; a bare
  channel has none, so it sounds in vacuum.
- **It plays during pause.** Pause is speed 0; the engine would set sim-pitch to 0 for a registered channel.
  A bare channel keeps playing unless your code pauses it.
- **Still your job at exit.** Stop what you started; see the [release topic](../release-sound-channels-at-scene-change-and-exit/overview.md).

## See also

- [Overview of this topic](overview.md)
- [Approach 1: declare a SimSpeed rule and let the engine handle it](keep-mod-sounds-sensible-under-time-warp-1.md)
- [Approach 2: cut your own loops below the engine's threshold](keep-mod-sounds-sensible-under-time-warp-2.md)
- [Play your own sound files through KSA's FMOD system without blocking](../play-sound-files-through-the-games-fmod-system/overview.md)
- Mod feature: [BYO Music](../../../../mods/unscience/features/byo-music-vessel-3d-audio.md)

[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
[^ksa-channelwrapper]: KSA.ChannelWrapper (decompiled, build 2026.10.10.5554)
[^ksa-channelgroupreference]: KSA.ChannelGroupReference (decompiled, build 2026.10.10.5554)
