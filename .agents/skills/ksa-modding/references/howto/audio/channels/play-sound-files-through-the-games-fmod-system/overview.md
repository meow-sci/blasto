---
type: Game Feature
title: Play your own sound files through KSA's FMOD system without blocking
description: How a mod creates FMOD sounds from bytes or a file path on GameAudio.System, opens long files non-blocking, and plays them on the game's Sfx, Music or Ui channel group so they mix with game audio and obey the volume sliders
category: audio
group: channels
topic: play-sound-files-through-the-games-fmod-system
demonstrated_by: [mods/gatos/features/fmod-audio-playback, mods/unscience/features/byo-music-vessel-3d-audio]
lifecycle:
  - frame:viewports
  - frame:draw-ui
  - frame:after-on-frame
  - frame:post-render
  - thread:background
  - event:save
  - event:load
  - frame:prepare
game_types:
  - KSA.GameAudio
  - Brutal.FmodApi.FmodSystem
  - Brutal.FmodApi.Sound
  - Brutal.FmodApi.Channel
  - Brutal.FmodApi.Mode
  - KSA.ChannelGroupType
  - KSA.ChannelGroupReference
  - KSA.ModLibrary
  - Brutal.FmodApi.OpenState
  - Brutal.FmodApi.Result
  - KSA.SpatialAudio
  - KSA.Vehicle
  - KSA.Program
tags: [audio, fmod, streaming, nonblocking, channel-groups, 3d-audio, game-audio]
---

Not every sound a mod plays is a declared asset. A mod that plays a file the player supplies (an uploaded clip, an
imported song) has no XML to put it in. KSA exposes its FMOD system as `GameAudio.System`, so the mod can create an
FMOD `Sound` itself and play it on one of the game's own channel groups. The sound then mixes with game audio and
follows the player's volume sliders, and the mod never owns or pumps the FMOD system.

Two things go wrong when this is done naively: long files decoded on the main thread stall a frame, and a second FMOD
system (or a call to `Update`, `Close` or `Release`) fights the game for the output device. The articles show three
shapes that avoid both.

## Engine background

| Piece | What it does for a mod |
|---|---|
| `GameAudio.System` [current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L55-L55) | The one `FmodSystem`, created in `OnApplicationStart` with 1024 channels, right-handed 3D and a 3D scale of (1, 1, 0.1)[^ksa-gameaudio] |
| File callbacks [current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L309-L357) | KSA installs open/close/read/seek callbacks that `File.OpenRead` the name FMOD is given, so an absolute path works for `TryCreateStream`[^ksa-gameaudio] |
| `GameAudio.OnApplicationStart` groups [current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L104-L142) | Creates `Master`, `Sfx`, `Ui` and `Music` groups and registers each as a `ChannelGroupReference`; `ApplySettings` sets their volumes from the player's sliders |
| `GameAudio.GetChannelGroup(ChannelGroupType)` [current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L660-L677) | Returns the FMOD `ChannelGroup` for `Master`, `Sfx`, `Ui` or `Music`; `ModLibrary.Get<ChannelGroupReference>("Sfx").ChannelGroup` is the same group by id[^ksa-channelgroupreference] |
| `GameAudio.CreateFmodSound` [current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L270-L307) | The game's own recipe: bytes with `Mode.OpenMemory`, or `TryCreateStream` followed by a `Thread.Sleep(1)` loop on `TryGetOpenState` until `Ready` |
| `GameAudio.UpdateAudio` [current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L150-L153) | Calls `System.Update()` once per frame from `Program.PostRender` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2362)); this is the only pump[^ksa-program] |
| `FmodSystem.TryCreateSound`, `TryCreateStream`, `TryPlaySound` [current/decomp/Brutal.FmodApi/Fmod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.FmodApi/Fmod.cs#L4775-L4900) | The non-throwing `Try*` forms return a `Result`; `TryGetOpenState` is at L6042[^brutal-fmod] |
| `Mode` flags [current/decomp/Brutal.FmodApi/Mode.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.FmodApi/Mode.cs#L1-L37) | `_2d`, `_3d`, `LoopOff`, `LoopNormal`, `OpenMemory`, `CreateSample`, `CreateCompressedSample`, `NonBlocking`, `_3dLinearRolloff`[^brutal-fmod-mode] |
| `OpenState` [current/decomp/Brutal.FmodApi/OpenState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.FmodApi/OpenState.cs#L1-L14) | `Ready`, `Loading`, `Error`, `Connecting`, `Buffering`, `Seeking`, `Playing`, `SetPosition`, `Max`[^brutal-fmod-openstate] |
| `SpatialAudio.PositionView` and `VelocityView` [current/decomp/KSA/SpatialAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SpatialAudio.cs#L55-L63) | Camera view-space vectors that FMOD 3D channel attributes expect here: KSA never calls the FMOD listener-attribute functions (searched in the decompiled game), so sources are placed in view space and the listener stays at the origin[^ksa-spatialaudio] |

Timing: every FMOD call here is main-thread work. Channels are configured wherever the mod's per-frame code runs
(`frame:viewports` for gatOS's command drain, `frame:draw-ui` for both mods' ticks, `frame:after-on-frame` on frames
with the HUD hidden), and FMOD applies the result in `System.Update()` at `frame:post-render`. See the
[game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: In-memory sounds, a channel table and reference-counted release](play-sound-files-through-the-games-fmod-system-1.md) | gatOS | `fmod-audio` | `frame:viewports`, `frame:draw-ui`, `thread:background` (uploads) | Short clips from bytes; many channels per sound, concurrent playback; you cache, count and release sounds yourself |
| [2: File-path streams on the Sfx group, opened non-blocking and polled each frame](play-sound-files-through-the-games-fmod-system-2.md) | unscience (BYO Music) | `fmod-audio` | `frame:draw-ui`, `frame:post-render` | Long files without loading them into memory or stalling the frame; one channel per stream; an error state to handle |
| [3: One VesselSound per entry, per-frame 3D attributes and a completion-based repeat scheduler](play-sound-files-through-the-games-fmod-system-3.md) | unscience (BYO Music) | `fmod-audio` | `frame:draw-ui`, `event:save`, `event:load`, `frame:prepare` | Sound that follows a vessel, loops with a real-time gap and survives a save; the most state to own |

Source files: [gatOS.GameMod/Game/Ksa/Actuators/AudioActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/AudioActuator.cs) (article 1) and [byo-music.lib/VesselSound.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music.lib/VesselSound.cs) (articles 2 and 3).

Choosing: take article 1 for short clips and many overlapping plays; article 2 for one long file at a time; article 3
when the sound must sit at a vessel in 3D. Declared sounds (XML) are better when the file ships with the mod; see
[ship custom sounds as XML assets](../../sound-assets/ship-custom-sounds-as-xml-assets/overview.md). These channels are
not registered with `GameAudio.ActiveChannels`, so the engine's time-warp pitch and stop handling never touches them
([keep mod sounds sensible under time warp](../keep-mod-sounds-sensible-under-time-warp/overview.md)).

## See also

- [Read the sound type map through the features that use each type](../../sound-assets/understand-the-games-sound-and-music-xml-types/overview.md)
- [Keep mod-played sounds sensible under time warp and the player's volume](../keep-mod-sounds-sensible-under-time-warp/overview.md)
- [Stop and release sound channels at scene change and game exit](../release-sound-channels-at-scene-change-and-exit/overview.md)
- [Add background music for a body with a MusicPlaylist asset](../../sound-assets/add-a-music-playlist-for-a-location/overview.md)
- [Draw UI while the HUD is hidden](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md)
- [Run an expensive solve without stalling the frame](../../../flight-and-orbits/guidance/run-an-expensive-solve-without-stalling-the-frame/overview.md)
- [Making a vehicle survive crashes and g-loads, and damaging parts against crash tolerance](../../../vehicles-and-parts/vehicles/make-a-vehicle-indestructible/overview.md) - related topic
- [Build searchable lists, two-pane browsers and file pickers in ImGui](../../../ui/windows/build-searchable-lists-and-browsers/overview.md) - related topic

[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
[^ksa-channelgroupreference]: KSA.ChannelGroupReference (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^brutal-fmod]: Brutal.FmodApi.Fmod (decompiled, build 2026.10.10.5554)
[^brutal-fmod-mode]: Brutal.FmodApi.Mode (decompiled, build 2026.10.10.5554)
[^brutal-fmod-openstate]: Brutal.FmodApi.OpenState (decompiled, build 2026.10.10.5554)
[^ksa-spatialaudio]: KSA.SpatialAudio (decompiled, build 2026.10.10.5554)
