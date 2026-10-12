---
type: How-To
title: Attach a stream to a vessel with per-frame 3D attributes and a completion-based repeat scheduler
description: Own one VesselSound per entry that places its FMOD channel at a vessel every frame in camera view space, loops natively or repeats after a real-time gap counted from observed completion, and survives scene saves
category: audio
group: channels
topic: play-sound-files-through-the-games-fmod-system
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/byo-music-vessel-3d-audio]
techniques: [fmod-audio, save-sidecar]
hooks: [starmap StarMapBeforeGui, unscience save participant byo-music (order 220)]
lifecycle: [frame:draw-ui, frame:post-render, event:save, event:load, frame:prepare]
game_types:
  - KSA.GameAudio
  - KSA.SpatialAudio
  - KSA.Vehicle
  - KSA.Program
  - Brutal.FmodApi.Channel
  - Brutal.FmodApi.Mode
  - Brutal.FmodApi.Result
tags: [audio, fmod, 3d-audio, spatial-audio, repeat, scene-saves, vessel]
---

Outcome: a sound that comes from a vessel (pans, falls off with distance from the camera, keeps a stable pitch), can
loop with no gap or repeat after a gap of real seconds, is stopped when its vessel goes away, and is restored (paused)
after a save load. Prerequisite: [article 2](play-sound-files-through-the-games-fmod-system-2.md)'s stream, open-state
polling and Sfx-group play. Demonstrated by BYO Music in the unscience mod.

## Background: how the game does it

KSA places a sound by giving FMOD a position and velocity in **camera view space** and leaving the listener at the
origin: `ChannelWrapper.SetSpatialAudio` calls `Channel.TrySet3dAttributes(spatial.PositionView(), spatial.VelocityView())`,
and `SpatialAudio` builds those from the audio camera (`GameAudio.GetAudioCamera()` is `Program.MainViewport.BaseCamera`).
KSA never sets FMOD listener attributes, searched across the decompiled game.[^ksa-channelwrapper][^ksa-spatialaudio][^ksa-gameaudio]
A hand-made FMOD channel follows the same convention by building `new SpatialAudio(vessel)` and passing its two view
vectors to `TrySet3dAttributes`.

## Lifecycle and timing

| Step | Where | Vocabulary |
|---|---|---|
| Decode poll, repeat scheduler, 3D attributes, target checks | `ByoMusicSubmod.Update` from `[StarMapBeforeGui]`; the mod's host also replays it with the HUD hidden | `frame:draw-ui` |
| FMOD commits the attributes | `GameAudio.UpdateAudio` -> `System.Update()` in `PostRender` | `frame:post-render` |
| Capture / reset / restore | The mod's save participant, around the game's save and load; load is finished at the `PrepareFrame` hand-off | `event:save`, `event:load`, `frame:prepare` |

Why `frame:draw-ui` works: `Program.OnFrameViewports` has already updated the main camera that frame
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2263-L2263)) before `OnDrawUiFrame` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2267-L2267)), so
`SpatialAudio` measures the vessel against current camera state, and FMOD applies it in the same frame's
`PostRender`.[^ksa-program] The `dt` the hook receives is the real-time frame delta, so gaps count wall-clock seconds,
also while paused or warping. See the [game loop](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Configure the channel every frame while it plays, starting before it is audible.** Start the channel paused, call
   `ApplySettings`, then unpause, so the first sample is already at the vessel and not at the listener origin:

   ```csharp
   // byo-music.lib/VesselSound.cs L104-L125
   private void Start()
   {
       var group = ModLibrary.Get<ChannelGroupReference>("Sfx").ChannelGroup;
       Check(GameAudio.System.TryPlaySound(_sound, group, true, out _channel), "play sound");
       _hasChannel = true;
       ApplySettings(); // Configure position and gain while paused: no one-frame sound at the origin.
       Check(_channel.TrySetPaused(false), "start sound");
   }

   private void ApplySettings()
   {
       bool continuous = Repeat && GapSeconds == 0;
       Check(_channel.TrySetMode(Mode._3d | Mode._3dLinearRolloff | (continuous ? Mode.LoopNormal : Mode.LoopOff)), "set repeat");
       Check(_channel.TrySetLoopCount(continuous ? -1 : 0), "set repeat count");
       Check(_channel.TrySetVolume(Volume), "set volume");
       Check(_channel.TrySet3dMinMaxDistance(Math.Min(10, RangeMetres * .1f), RangeMetres), "set audible range");
       // Match stock SpatialAudio's camera-relative positions. Keep music pitch stable in warp.
       var spatial = new SpatialAudio(Target);
       Check(_channel.TrySet3dAttributes(spatial.PositionView(), spatial.VelocityView()), "follow vessel");
       Check(_channel.TrySet3dSpread(0), "position stereo files as a point source");
       Check(_channel.TrySet3dDopplerLevel(0), "keep playback pitch stable");
   }
   ```

   - `Mode._3d | Mode._3dLinearRolloff`: positional, linear roll-off to the audible range.
   - `LoopNormal` with loop count -1 only for a repeat with no gap; otherwise `LoopOff`.
   - `TrySet3dMinMaxDistance(min(10, range * 0.1), range)`: the audible range is the user's setting.
   - Spread 0 makes a stereo file act as a point source; Doppler 0 keeps pitch stable in warp.

   All of these are `Channel.TrySet*` calls in Brutal's FMOD wrapper.[^brutal-fmod]

2. **Count gaps from observed completion.** FMOD cannot loop with a pause, so a tiny state machine counts real seconds
   from the moment it first sees the sound finished. Editing the gap while waiting keeps the silence already elapsed,
   and a zero gap with repeat never reaches it because FMOD loops natively:

   ```csharp
   // byo-music.lib/RepeatPlayback.cs L7-L29
   internal sealed class RepeatPlayback
   {
       private bool _started;
       private bool _waiting;
       private double _lastGap;
       public double Remaining { get; private set; }
       public PlaybackAction Advance(double dt, bool playing, bool repeat, double gap)
       {
           if (!_started) { _started = true; return PlaybackAction.Play; }
           if (playing) { _waiting = false; return PlaybackAction.None; }
           if (!repeat) return PlaybackAction.Finish;
           if (!_waiting) { _waiting = true; Remaining = Math.Max(0, gap); }
           else
           {
               // Editing the gap while waiting keeps elapsed silence rather than restarting it.
               Remaining += gap - _lastGap;
               if (double.IsFinite(dt) && dt > 0) Remaining -= dt;
           }
           _lastGap = gap;
           if (Remaining > 0) return PlaybackAction.None;
           _waiting = false;
           return PlaybackAction.Play;
       }
   ```

3. **Drive it from the entry's `Update`.** Detect completion with `TryIsPlaying`, tolerating a stale handle, then act on
   the scheduler's answer (`Play`, `Finish` or nothing):

   ```csharp
   // byo-music.lib/VesselSound.cs L72-L95
   bool playing = false;
   if (_hasChannel)
   {
       Result result = _channel.TryIsPlaying(out var isPlaying);
       playing = result == Result.Ok && isPlaying;
       if (result != Result.Ok && result != Result.ErrInvalidHandle && result != Result.ErrChannelStolen)
           Check(result, "check playback");
   }
   switch (_repeat.Advance(dt, playing, Repeat, GapSeconds))
   {
       case PlaybackAction.Play:
           Start();
           playing = true;
           break;
       case PlaybackAction.Finish:
           Stop("Finished");
           return;
   }
   if (playing)
   {
       ApplySettings();
       Status = Repeat && GapSeconds == 0 ? "Looping" : "Playing";
   }
   else Status = $"Next play in {_repeat.Remaining:0.0}s";
   ```

4. **Stop on target loss.** At the top of `Update`: a disposed vehicle, or one that `VehicleProvider.FindVehicle(id)`
   no longer returns as the same object, stops the entry (`Stop("Target no longer available")`):

   ```csharp
   // byo-music.lib/VesselSound.cs L54-L55
   if (Target.IsDisposed || VehicleProvider.FindVehicle(Target.Id) != Target)
   { Stop("Target no longer available"); return; }
   ```

5. **Make it survive a save.** The mod registers a save participant: capture stores (vehicle id, file, repeat, gap,
   volume, range) for every unfinished sound whose vessel is alive; reset is `Dispose` (every stream is stopped and
   released before KSA replaces the vehicles); restore re-creates each sound **paused**, so Resume plays it from the
   beginning. The cursor and the remaining gap are not saved:

   ```csharp
   // byo-music.lib/ByoMusicSubmod.Saves.cs L11-L16, L24-L24, L29-L29
   public sealed record SavedSound(string VehicleId, string File, bool Repeat, float Gap, float Volume, float Range);
   public IEnumerable<ISaveParticipant> SaveParticipants => new[]
   {
       new SaveParticipant<SavedSound[]>("byo-music", () => _sounds.Where(s => !s.Finished && !s.Target.IsDisposed)
           .Select(s => new SavedSound(s.Target.Id, s.FileName, s.Repeat, s.GapSeconds, s.Volume, s.RangeMetres)).ToArray(),
           Dispose, (state, context) =>
   // ...
                       _sounds.Add(new(target, item.File, item.Repeat, item.Gap, item.Volume, item.Range, paused: true));
   // ...
           }, order: 220, validate: state =>
   ```

   The coordinator behind it is described in
   [scene save coordinator](../../../../mods/unscience/features/lib-scene-save-coordinator.md); the teardown side is
   [article 3 of releasing channels](../release-sound-channels-at-scene-change-and-exit/release-sound-channels-at-scene-change-and-exit-3.md).

## Complete example

| File | Role |
|---|---|
| [byo-music.lib/VesselSound.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music.lib/VesselSound.cs) | The per-entry stream, channel and 3D settings |
| [byo-music.lib/RepeatPlayback.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music.lib/RepeatPlayback.cs) | The completion-based gap scheduler |
| [byo-music.lib/ByoMusicSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music.lib/ByoMusicSubmod.Saves.cs) | Capture, reset and restore |
| [byo-music.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music.lib/README.md) | Behaviour contract and the remaining in-game checks |

## Variations and gotchas

- **Possible finish-path issue (static reading, unverified in game).** `Update` calls `Check(_channel.TrySetPaused(Paused))`
  (the strict form that throws on any non-Ok result) at the top of every tick, before the tolerant
  `TryIsPlaying` branch (L56 versus L75-L78). If FMOD has already invalidated a finished one-shot's channel handle,
  the strict call would turn a normal finish, or the start of a positive gap, into an error stop. The authors list
  native repeat and stop behaviour as still needing an in-game audio pass.[^unscience-readme]
- **No atmospheric muffling.** `SpatialAudio` carries pressure and `ChannelWrapper` turns it into a low-pass; this
  channel never goes through `ChannelWrapper`, so it sounds in vacuum, by design.
- **Time warp.** The channel is not in `GameAudio.ActiveChannels`, so engine pitch and stop rules never touch it; see
  [keep mod sounds sensible under time warp](../keep-mod-sounds-sensible-under-time-warp/overview.md).
- **Same file, same vessel replaces.** Play removes an older entry for the same pair first so gain does not double.
- **Doppler is switched off.** Level 0 keeps pitch steady; set a level if you want the shift and accept that KSA
  uses a Doppler scale of 1 in `Set3dSettings`.

## See also

- [Overview of this topic](overview.md)
- [Approach 1: in-memory sounds, a channel table and reference-counted release](play-sound-files-through-the-games-fmod-system-1.md)
- [Approach 2: file-path streams opened non-blocking](play-sound-files-through-the-games-fmod-system-2.md)
- [Play a sound at a world position and hold its spatial channel](../../playback/play-a-sound-at-a-world-position-and-hold-its-channel/overview.md)
- [Draw UI while the HUD is hidden](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md)
- Mod feature: [BYO Music](../../../../mods/unscience/features/byo-music-vessel-3d-audio.md)

[^ksa-channelwrapper]: KSA.ChannelWrapper (decompiled, build 2026.10.10.5554)
[^ksa-spatialaudio]: KSA.SpatialAudio (decompiled, build 2026.10.10.5554)
[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^brutal-fmod]: Brutal.FmodApi.Fmod (decompiled, build 2026.10.10.5554)
[^unscience-readme]: unscience: byo-music.lib/README.md
