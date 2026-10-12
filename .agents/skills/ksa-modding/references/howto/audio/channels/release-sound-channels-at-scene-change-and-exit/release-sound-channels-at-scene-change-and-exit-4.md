---
type: How-To
title: Release FMOD sounds only after no channel plays them
description: Prune stopped channels each frame, release cached FMOD sounds only when their clip version is gone and no live channel plays them, and have teardown free only what the mod created, never the game's FMOD system
category: audio
group: channels
topic: release-sound-channels-at-scene-change-and-exit
approach: 4
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/fmod-audio-playback]
techniques: [fmod-audio]
hooks: [starmap StarMapBeforeGui, starmap StarMapAfterOnFrame, starmap StarMapUnload]
lifecycle: [frame:draw-ui, frame:after-on-frame, event:shutdown]
game_types: [KSA.GameAudio, Brutal.FmodApi.FmodSystem, Brutal.FmodApi.Sound, Brutal.FmodApi.Channel, KSA.Program]
tags: [audio, lifecycle, cleanup, ownership, fmod-sound, release]
---

Outcome: a clear ownership rule for FMOD objects a mod creates: channels are pruned when they stop, a `Sound` is
released only when no live channel uses it, and the mod's teardown frees what it made and nothing the game owns.
Prerequisites: [article 1 of the FMOD-files topic](../play-sound-files-through-the-games-fmod-system/play-sound-files-through-the-games-fmod-system-1.md)
(in-memory sounds and a channel table). Demonstrated by gatOS's `/sim/audio` actuator.

## Background: how the game does it

The game owns the FMOD system (`GameAudio.System`) and the four channel groups; it closes the system in
`Program.Dispose`.[^ksa-gameaudio][^ksa-program] A mod that creates a `Sound` or a `Channel` of its own owns those two things and
nothing else. FMOD copies the bytes at `TryCreateSound` (`Mode.OpenMemory`), so the mod's byte buffer can be dropped
at once; the `Sound` itself must be released with `TryRelease`, and releasing it while a channel plays it audibly cuts
the channel.

## Lifecycle and timing

| Step | Where | Vocabulary |
|---|---|---|
| Prune and release | `Tick`, called by `DriveAudio` each frame | `frame:draw-ui` (or `frame:after-on-frame` with the HUD hidden) |
| FMOD advances channels | `GameAudio.UpdateAudio` in `PostRender` | `frame:post-render` |
| Teardown | `Shutdown`, called from the mod's `[StarMapUnload]` through `TeardownGameCheats` | `event:shutdown` |

## Steps

1. **Treat a finished channel handle as the completion signal.** FMOD recycles handles, so `TryIsPlaying` returns a
   non-Ok result for a channel that has ended; prune it and forget its id (the tick shown in
   [article 1](../play-sound-files-through-the-games-fmod-system/play-sound-files-through-the-games-fmod-system-1.md)
   also enforces end times).

2. **Release sounds deferred.** A cached `Sound` is released only when its clip version is no longer current in the
   store **and** no channel entry still plays that version:

   ```csharp
   // gatOS.GameMod/Game/Ksa/Actuators/AudioActuator.cs L332-L360
   private void ReleaseEvictedSounds()
   {
       if (_sounds.Count == 0)
           return;
       List<(string Name, int Version)>? evict = null;
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
   }
   ```

3. **Tear down only what you made.** `Shutdown` stops every channel it started, releases every `Sound` it created, and
   clears its store; it never calls `System.Update`, `Close` or `Release`:

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

4. **Know where teardown is called.** gatOS calls it from its `[StarMapUnload]` method (`Unload` ->
   `TeardownGameCheats` -> `_audioActuator?.Shutdown()`), each step in its own `try`:

   ```csharp
   // gatOS.GameMod/Game/Mod.Game.cs L681-L688
   try
   {
       _audioActuator?.Shutdown(); // stops every channel, releases every FMOD Sound, clears the store
   }
   catch (Exception ex)
   {
       ModLog.Log.Debug($"gatOS audio teardown error: {ex.Message}");
   }
   ```

   ```csharp
   // gatOS.GameMod/Mod.cs L484-L491
   [StarMapUnload]
   public void Unload()
   {
       try
       {
           _instance = null;
           IsInitialized = false;
           TeardownGameCheats(); // partial: clears welds + restores IVA (unpatches the IVA hooks)
   ```

   That is after the game has closed FMOD, so at process exit the `TryStop` and `TryRelease` calls reach nothing live
   (the `try` keeps a failure from spreading; what native FMOD does with those handles was not tested). The release
   logic earns its keep in the middle of a session, when a clip is replaced or deleted, not at exit.

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Actuators/AudioActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/AudioActuator.cs#L278-L360) | Tick, `Shutdown`, deferred release |
| [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L455-L478) | `DriveAudio` with its fault latch |
| [gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L484-L491) | `[StarMapUnload]` and the start of teardown |

## Variations and gotchas

- **Stop and release in the right order.** Stopping channels before releasing their sound keeps the cut silent; the
  `Shutdown` above stops all channels first.
- **A `CreateStream` sound has one channel.** Only one channel can play a stream at a time, so the "no channel plays
  it" test is trivial; a sample (`CreateSample`) may have many.
- **Do not hold a `Channel` struct forever.** The handle is only valid while the sound plays; check `TryIsPlaying`
  before using an old one.
- **Not the game's job.** Nothing in `GameAudio` tracks your `Sound`s; a leak is yours until process exit.
- **Pending validation.** The repository lists in-game validation of `/sim/audio` as still pending.

## See also

- [Overview of this topic](overview.md)
- [Approach 1: StopAll at StarMapUnload, plus Stop(stopImmediate) on world reload](release-sound-channels-at-scene-change-and-exit-1.md)
- [Approach 2: stop on scene exit and tolerate a dead audio system](release-sound-channels-at-scene-change-and-exit-2.md)
- [Play your own sound files through KSA's FMOD system without blocking](../play-sound-files-through-the-games-fmod-system/overview.md)
- Mod feature: [FMOD audio playback](../../../../mods/gatos/features/fmod-audio-playback.md)

[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
