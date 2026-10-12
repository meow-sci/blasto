---
type: How-To
title: Play in-memory sounds with a channel table and reference-counted release
description: Create FMOD sounds from bytes on GameAudio.System, play them paused on the game's Sfx, Music or Ui group, track the channels, and release a sound only when no live channel plays it
category: audio
group: channels
topic: play-sound-files-through-the-games-fmod-system
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/fmod-audio-playback]
techniques: [fmod-audio]
hooks: [harmony-prefix KSA.GameViewport.OnFrame, starmap StarMapBeforeGui, starmap StarMapAfterOnFrame]
lifecycle: [frame:viewports, frame:draw-ui, frame:after-on-frame, frame:post-render, thread:background]
game_types:
  - KSA.GameAudio
  - Brutal.FmodApi.FmodSystem
  - Brutal.FmodApi.Sound
  - Brutal.FmodApi.Channel
  - Brutal.FmodApi.Mode
  - KSA.ChannelGroupType
tags: [audio, fmod, in-memory, channels, release, main-thread]
---

Outcome: play clips that exist only as bytes in memory (uploaded over HTTP, read from a data folder, embedded in the
mod) on the game's own mixer, with volume, pitch, loop and a start/end window, many at once, and free each FMOD sound
only when nothing is still playing it. Prerequisite: a StarMap mod with a per-frame hook. Demonstrated by gatOS's
`/sim/audio` actuator, which lets a guest program or HTTP client upload a clip and play it on the SFX, music or UI group.

## Background: how the game does it

The game creates its FMOD system once and exposes it as `GameAudio.System`; it creates the `Master`, `Sfx`, `Ui` and
`Music` groups and sets their volumes from the player's sliders.[^ksa-gameaudio] `GameAudio.CreateFmodSound` shows the
game's own in-memory recipe (`TryCreateSound` with the file bytes and `Mode.OpenMemory`), and `GameAudio.UpdateAudio`
is what pumps FMOD every frame ([current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L150-L152)). A mod copies the recipe, plays on one of the groups,
and leaves the pump alone.

## Lifecycle and timing

| Step | Where | Vocabulary |
|---|---|---|
| Upload bytes into a store | Transport threads; touches only a game-free byte store | `thread:background` |
| `audio.play`, `audio.set`, `audio.stop` | Frame-phase command drain; with the camera hook installed this is the `GameViewport.OnFrame` prefix | `frame:viewports` |
| Per-frame tick | `DriveAudio` from `StarMapBeforeGui`, or `StarMapAfterOnFrame` when the HUD is hidden | `frame:draw-ui`, `frame:after-on-frame` |
| FMOD applies everything | `GameAudio.UpdateAudio` -> `System.Update()` in `PostRender` | `frame:post-render` |

Every FMOD call is on the main thread. On hidden-HUD frames the tick runs after `PostRender`, so pruning and `end=`
enforcement reach FMOD one update later.[^gatos-mod-game] Frame structure:
[game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md); hidden-HUD hooks:
[draw UI while the HUD is hidden](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md).

## Steps

1. **Create the FMOD sound once per clip version.** FMOD copies the bytes at create, so the store's array can be
   freed or replaced immediately and a re-upload never disturbs a playing channel. Clips up to 1 MiB are decoded
   fully (`CreateSample`); larger ones stay compressed (`CreateCompressedSample`) so creation stays cheap and one sound
   can still play on several channels (a stream could not).

   ```csharp
   // gatOS.GameMod/Game/Ksa/Actuators/AudioActuator.cs L156-L170
   private Sound? CreateOrGetSound(AudioClip clip)
   {
       var key = (clip.Name, clip.Version);
       if (_sounds.TryGetValue(key, out var cached))
           return cached;

       var mode = Mode.OpenMemory | Mode._2d
                  | (clip.Bytes.Length <= CreateSampleMaxBytes ? Mode.CreateSample : Mode.CreateCompressedSample);
       var exInfo = new CreateSoundExInfo { Length = (uint)clip.Bytes.Length };
       if (GameAudio.System.TryCreateSound(clip.Bytes, mode, in exInfo, out var sound) != Result.Ok)
           return null;

       _sounds[key] = sound;
       return sound;
   }
   ```

2. **Play paused on a game channel group, configure, then unpause.** This is the game's own anti-pop idiom: the first
   audible sample already has the right position, loop, volume and pitch. `GameAudio.GetChannelGroup` puts the channel
   under the matching player slider:

   ```csharp
   // gatOS.GameMod/Game/Ksa/Actuators/AudioActuator.cs L103-L129
   // The game's own idiom: start paused, configure everything, then unpause (no pops).
   if (GameAudio.System.TryPlaySound(sound, GameAudio.GetChannelGroup(groupType), paused: true,
           out var channel) != Result.Ok)
       return new CommandResult(CommandOutcome.Fault, $"FMOD could not start a channel for '{name}'");

   if (startMs > 0 && channel.TrySetPosition(startMs, TimeUnit.Ms) != Result.Ok)
   {
       channel.TryStop();
       return new CommandResult(CommandOutcome.Invalid, $"start={startMs} is beyond the clip");
   }

   if (loop)
   {
       channel.TrySetMode(Mode.LoopNormal);
       channel.TrySetLoopCount(-1);
       if (endMs > 0)
           channel.TrySetLoopPoints(startMs, TimeUnit.Ms, endMs, TimeUnit.Ms);
   }

   var volume = v[AudioCommands.PlayVol];
   if (volume < 1)
       channel.TrySetVolume((float)volume);
   if (v[AudioCommands.PlayPan] is not 0.0)
       channel.TrySetPan((float)v[AudioCommands.PlayPan]);
   if (v[AudioCommands.PlayPitch] is not 1.0)
       channel.TrySetPitch((float)v[AudioCommands.PlayPitch]);
   channel.TrySetPaused(false);
   ```

3. **Keep a channel table.** The mod stores each `Channel` with its clip name and version, loop flag, end time and
   group under an id; reusing a live id stops the old channel (restart semantics), and a cap rejects plays when all
   slots are used (the cap is a config value, `Config.AudioMaxChannels`).

4. **Tick every frame.** A finished channel handle answers a non-Ok `Result` (FMOD recycles handles), and that **is**
   the completion signal. The tick also enforces the end time by position, releases evicted sounds, and publishes
   status:

   ```csharp
   // gatOS.GameMod/Game/Ksa/Actuators/AudioActuator.cs L278-L311
   internal void Tick()
   {
       if (_channels.Count > 0)
       {
           List<string>? finished = null;
           foreach (var (id, entry) in _channels)
           {
               if (entry.Channel.TryIsPlaying(out var playing) != Result.Ok || !playing)
               {
                   EmitFinished(id, entry.ClipName, "ended");
                   (finished ??= []).Add(id);
                   continue;
               }

               var posKnown = entry.Channel.TryGetPosition(out var positionMs, TimeUnit.Ms) == Result.Ok;
               if (posKnown && entry.EndMs > 0 && positionMs >= entry.EndMs)
               {
                   entry.Channel.TryStop();
                   EmitFinished(id, entry.ClipName, "ended");
                   (finished ??= []).Add(id);
                   continue;
               }

               entry.PositionMs = posKnown ? positionMs : entry.PositionMs;
           }

           if (finished is not null)
               foreach (var id in finished)
                   _channels.Remove(id);
       }

       ReleaseEvictedSounds();
       PublishStatus();
   }
   ```

5. **Release sounds only when nothing plays them.** Releasing a `Sound` while a channel plays it audibly cuts the
   channel, so a sound whose clip version was evicted is released only when no channel entry references that
   version:

   ```csharp
   // gatOS.GameMod/Game/Ksa/Actuators/AudioActuator.cs L337-L359
   foreach (var key in _sounds.Keys)
   {
       if (store.CurrentVersion(key.Name) == key.Version)
           continue;
       var inUse = false;
       foreach (var entry in _channels.Values)
           if (entry.ClipName == key.Name && entry.Version == key.Version)
           {
               inUse = true;
               break;
           }

       if (!inUse)
           (evict ??= []).Add(key);
   }

   if (evict is null)
       return;
   foreach (var key in evict)
   {
       if (_sounds.Remove(key, out var sound) && sound.TryRelease() != Result.Ok)
           ModLog.Log.Debug($"audio: releasing the sound for '{key.Name}' v{key.Version} failed");
   }
   ```

6. **Never touch the system.** The actuator's remarks state it: `System.Update`, `Close` and `Release` belong to the
   game; the mod frees only the `Sound`s it created and stops only the `Channel`s it started.

   ```csharp
   // gatOS.GameMod/Game/Ksa/Actuators/AudioActuator.cs L313-L325
   /// <summary>Stops everything and frees every FMOD sound + the clip store (mod unload).</summary>
   internal void Shutdown()
   {
       foreach (var entry in _channels.Values)
           entry.Channel.TryStop();
       _channels.Clear();
       foreach (var sound in _sounds.Values)
           sound.TryRelease();
       _sounds.Clear();
       store.Clear();
       store.PublishChannels([]);
       _publishedEmpty = true;
   }
   ```

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Actuators/AudioActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/AudioActuator.cs) | Sound cache, channel table, tick, teardown |
| [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L455-L478) | `DriveAudio` with a fault latch (one error log, then audio is off for the session) |

The byte store, caps and command grammar are the game-free `gatOS.SimFs/Audio` files; see the
[feature page](../../../../mods/gatos/features/fmod-audio-playback.md).

## Variations and gotchas

- **A stream cannot play on several channels.** Use `CreateStream` only for a clip that plays once at a time; see
  [article 2](play-sound-files-through-the-games-fmod-system-2.md).
- **Mode `_2d`.** gatOS creates its sounds `Mode._2d`: no 3D placement, so a clip is equally loud wherever the camera
  is. For a sound at a world point use [article 3](play-sound-files-through-the-games-fmod-system-3.md) or a declared
  sound.
- **Teardown.** The actuator's `Shutdown` stops every channel and releases every sound, but it is called from the mod's
  `[StarMapUnload]`, which runs after the game has closed FMOD; see
  [release sound channels at scene change and exit](../release-sound-channels-at-scene-change-and-exit/overview.md).
- **Drift.** The mod's `[KsaAnchor]` notes say these calls were last verified against build `2026.6.9.4750`; the calls
  named here exist in the decompiled build `2026.10.10.5554` (`Fmod.cs`: `TryCreateSound`, `TryPlaySound`,
  `TryGetPosition`), but the mod's own in-game validation of `/sim/audio` is listed as still pending in its
  repository.[^brutal-fmod]
- **Failure is a `Result`.** FMOD rejecting the bytes (corrupt or unsupported container) comes back as a non-Ok
  `Result` from `TryCreateSound`; map it to a user-visible error rather than throwing into the frame hook.

## See also

- [Overview of this topic](overview.md)
- [Approach 2: file-path streams opened non-blocking](play-sound-files-through-the-games-fmod-system-2.md)
- [Approach 3: a vessel-attached sound with per-frame 3D attributes](play-sound-files-through-the-games-fmod-system-3.md)
- [Keep mod-played sounds sensible under time warp and the player's volume](../keep-mod-sounds-sensible-under-time-warp/overview.md)
- [Stop and release sound channels at scene change and game exit](../release-sound-channels-at-scene-change-and-exit/overview.md)
- Mod feature: [FMOD audio playback](../../../../mods/gatos/features/fmod-audio-playback.md)

[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
[^gatos-mod-game]: gatOS: gatOS.GameMod/Game/Mod.Game.cs
[^brutal-fmod]: Brutal.FmodApi.Fmod (decompiled, build 2026.10.10.5554)
