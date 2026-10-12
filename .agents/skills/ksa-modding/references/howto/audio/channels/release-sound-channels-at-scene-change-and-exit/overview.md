---
type: Game Feature
title: Stop and release sound channels at scene change and game exit
description: When a mod's held sound channels must be stopped (owner gone, save loaded, scene left) and why unload-time cleanup is moot, because Program.Dispose closes FMOD before StarMap invokes StarMapUnload
category: audio
group: channels
topic: release-sound-channels-at-scene-change-and-exit
demonstrated_by:
  - mods/ksarmory/features/spatial-gun-and-motor-sounds
  - mods/ksarmory/features/burst-sound-at-blast-arrival
  - mods/ksacars/features/engine-sound-crossfade
  - mods/ksacars/features/rocket-sound-without-engine
  - mods/unscience/features/byo-music-vessel-3d-audio
  - mods/gatos/features/fmod-audio-playback
lifecycle: [event:shutdown, event:load, frame:draw-ui, frame:after-on-frame, event:save]
game_types:
  - KSA.Program
  - KSA.GameAudio
  - KSA.ExplosionSoundSystem
  - KSA.IChannel
  - Brutal.FmodApi.FmodSystem
  - KSA.Universe
  - Brutal.FmodApi.Sound
  - Brutal.FmodApi.Channel
  - KSA.Vehicle
tags: [audio, lifecycle, cleanup, shutdown, save-load, channels, fmod]
---

A held channel is a promise to stop it. If the thing that owns the sound goes away (a craft is destroyed, the scene is
left, a save is loaded over the world) and the mod keeps the channel, the sound plays on, positioned at a place that no
longer exists, and a loop never ends. KSA helps in exactly one place; everything else is the mod's job. And one
place that looks like the obvious cleanup point, `[StarMapUnload]`, cannot reach audio at all.

## Engine background

| Event | FMOD state | What the engine does to your channels |
|---|---|---|
| A held source disappears mid-session | alive | Nothing. `GameAudio.UpdateAudio` drops a channel only when it is neither playing nor virtual[^ksa-gameaudio] |
| A save is loaded: `Universe.DeserializeSave` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2430-L2446) | alive | Destroys every vehicle, then `ExplosionSoundSystem.StopAll()`, which stops only the explosion sounds the engine itself tracks ([current/decomp/KSA/ExplosionSoundSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ExplosionSoundSystem.cs#L84-L91))[^ksa-universe][^ksa-explosionsoundsystem] |
| A load finishes: `UncompressedSave.Load` [current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L80-L81) | alive | `Universe.DeserializeSave`, then `Program.OnGameLoaded()` |
| Normal exit: `Program.Dispose` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1362-L1488) | **closed** at L1485 | `GameAudio.Release()` calls `System.Close()` then `System.Release()` ([current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L144-L148))[^ksa-program] |
| `[StarMapUnload]` | **already closed** | StarMap invokes it after `Program.Dispose` has returned and after Harmony patches are removed |
| `Environment.Exit` paths (loading screen, MOD FOUND popup, system picker) | n/a | `StarMapUnload` does not run at all |

The consequences:

- **Unload-time `StopAll` calls do nothing.** FMOD is shut down in `Program.Dispose` before `StarMapUnload`. Calls there
  can only reach a system that is gone. Keep them guarded (they may return errors), but do not rely on them or write
  comments claiming they give channels back.
- **A save load does not stop your channels.** The engine's `StopAll` covers its own explosion sounds; yours play on
  into the new world unless you stop them.
- **The reliable cleanup points** are the ones while FMOD is alive: the source going away, leaving the flight scene,
  and a save load (before or right after `DeserializeSave`).

See the lifecycle reference ([game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)),
the mod feature [unload hook and shutdown order](../../../../mods/starmap/features/unload-and-shutdown.md) and
[clean up on game exit](../../../getting-started/lifecycle/clean-up-on-game-exit/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: StopAll at StarMapUnload, plus Stop(stopImmediate) on world reload](release-sound-channels-at-scene-change-and-exit-1.md) | KSArmory | `starmap-lifecycle` | `event:load`, `event:shutdown` | The reload hook is the useful part; the unload call is moot |
| [2: Stop on scene exit and tolerate a dead audio system](release-sound-channels-at-scene-change-and-exit-2.md) | KSACars | `starmap-lifecycle` | `frame:draw-ui`, `event:shutdown` | Cheap and robust: stop when the scene or the mod ends, and guard every call |
| [3: Dispose all streams on save reset before vehicles are replaced](release-sound-channels-at-scene-change-and-exit-3.md) | unscience (BYO Music) | `save-sidecar` | `event:load`, `event:save` | Runs before the world is destroyed and restores sounds paused; needs a save coordinator |
| [4: Release sounds only after no channel plays them](release-sound-channels-at-scene-change-and-exit-4.md) | gatOS | `fmod-audio` | `frame:draw-ui`, `event:shutdown` | Ownership rules for FMOD `Sound`s you create; teardown frees only what you made |

Source files: [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs), [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs),
[byo-music.lib/VesselSound.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music.lib/VesselSound.cs), [gatOS.GameMod/Game/Ksa/Actuators/AudioActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/AudioActuator.cs).

Choosing: do 2 for any mod that holds channels (it is the minimum); add 1's reload hook if sources can outlive a load;
use 3 if sounds should come back after loading; use 4 when you create FMOD sounds yourself.

## See also

- [Play a sound at a world position and hold its spatial channel](../../playback/play-a-sound-at-a-world-position-and-hold-its-channel/overview.md)
- [Play your own sound files through KSA's FMOD system without blocking](../play-sound-files-through-the-games-fmod-system/overview.md)
- [Keep mod-played sounds sensible under time warp and the player's volume](../keep-mod-sounds-sensible-under-time-warp/overview.md)
- [Clean up on game exit and relaunch the game from a mod](../../../getting-started/lifecycle/clean-up-on-game-exit/overview.md)
- [Keep mod exceptions out of KSA's frame loop](../../../getting-started/lifecycle/contain-exceptions-in-starmap-hooks/overview.md)
- [Making a vehicle survive crashes and g-loads, and damaging parts against crash tolerance](../../../vehicles-and-parts/vehicles/make-a-vehicle-indestructible/overview.md) (a destroyed vehicle is a source that has gone away)

[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-explosionsoundsystem]: KSA.ExplosionSoundSystem (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
