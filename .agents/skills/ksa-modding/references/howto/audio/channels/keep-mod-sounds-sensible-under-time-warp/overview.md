---
type: Game Feature
title: Keep mod-played sounds sensible under time warp and the player's volume
description: What the engine does to a sound at high simulation speed (pitch rule, stop above 10x, PlaySound refusing) and to the player's Master, Sfx, Ui and Music sliders, and how a mod follows, overrides or bypasses each
category: audio
group: channels
topic: keep-mod-sounds-sensible-under-time-warp
demonstrated_by:
  - mods/ksagolf/features/world-positioned-sounds
  - mods/ksacars/features/engine-sound-crossfade
  - mods/unscience/features/byo-music-vessel-3d-audio
  - mods/ksacars/features/rocket-sound-without-engine
lifecycle: [startup:asset-load, frame:draw-ui, frame:post-render]
game_types:
  - KSA.GameAudio
  - KSA.ChannelWrapper
  - KSA.SimSpeedRule
  - KSA.SoundReference
  - KSA.ChannelGroupType
  - KSA.Universe
  - KSA.ChannelGroupReference
  - Brutal.FmodApi.Channel
  - KSA.SoundBehavior
  - KSA.IChannel
tags: [audio, time-warp, simulation-speed, volume, sliders, simspeedrule]
---

A sound that is perfect at 1x can be wrong at 100x: a loop shifted up three times in pitch, a gunshot per frame, an
engine that should be silent. KSA has built-in rules for this, and they apply only to channels the engine knows
about. A mod picks, per sound, one of three stances: **follow** the rules (declare them), **override** them (play
through a lower call and cut yourself), or **bypass** them (play channels the engine never registers).

## Engine background

Everything below is per frame, in `GameAudio.UpdateAudio` at `frame:post-render`, for every channel in
`GameAudio.ActiveChannels`:[^ksa-gameaudio]

| Piece | Behaviour |
|---|---|
| Entry gate: `GameAudio.PlaySound(SoundBehavior, SpatialAudio, out IChannel?, float, bool, bool alwaysPlay)` [current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L416-L423) | Returns no channel when `Universe.SimulationSpeed > 10` unless `alwaysPlay` |
| Entry gate: `GameAudio.PlaySound(SoundEvent, SpatialAudio, ...)` [current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L383-L414) | Same 10x refusal, with no `alwaysPlay` parameter |
| No gate: `GameAudio.Play2DSound`, `GameAudio.PlaySound(SoundBehavior, out IChannel?)`, `SoundBehavior.Play` [current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L359-L368) | Never check the simulation speed |
| `ChannelWrapper.HandleSimSpeedPitch` [current/decomp/KSA/ChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChannelWrapper.cs#L456-L495) | Sets the sim-pitch factor from the sound's `SimSpeedRule` (table below)[^ksa-channelwrapper] |
| `ChannelWrapper.StopForSimSpeed` [current/decomp/KSA/ChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChannelWrapper.cs#L497-L503) | Calls `Stop()` when speed is above 10 and the rule is not `Ignore` |
| Final pitch [current/decomp/KSA/ChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChannelWrapper.cs#L262-L273) | `SoundPitch * PitchMultiplier * SimPitch * ParameterPitch` |
| Player sliders [current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L425-L444) | `ApplySettings` sets the `Master`, `Sfx`, `Ui` and `Music` group volumes from the audio settings through `MapVolumeSetting`; `GameSettings.Apply` calls it[^ksa-gamesettings] |

`SimSpeedRule` has five values (`None`, `Ignore`, `All`, `OnlyLower`, `OnlyHigher`); `SoundReference.SimSpeedRule`
defaults to `All`.[^ksa-simspeedrule][^ksa-soundreference] The sim-pitch factor by rule and speed (derived from
`HandleSimSpeedPitch`, constants 5, 3 and 0.25 in the class):

| Rule | speed >= 1 | 0 < speed < 1 | speed == 0 (paused) |
|---|---|---|---|
| `Ignore` | not computed (factor stays 1) | not computed | not computed |
| `All` (default) | `min(1 + speed / 5, 3)` | `max(speed, 0.25)` | 0 |
| `OnlyHigher` | `min(1 + speed / 5, 3)` | 1 | 0 |
| `OnlyLower` | 1 | `max(speed, 0.25)` | 0 |
| `None` | 1 | 1 | 0 |

Above 10x every rule except `Ignore` stops the channel at the next update. Paused is speed exactly 0:
`Universe.IsPaused()` is `_simulationSpeed == 0.0` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1799-L1802)).[^ksa-universe]

Volume: `MapVolumeSetting(v)` is 0 for 0, else `10^(lerp(-80, 0, v/100) / 40)`: 100 gives 1, 75 about 0.32, 50 about 0.1,
25 about 0.032. A channel plays in a group; the group's volume multiplies it. A `Sound` with no `ChannelId` plays
on `Master` and follows only the Master slider; `ExternalChannel` and `InternalChannel` sit under `Sfx` and follow
Sfx and Master.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: Declare a SimSpeed rule and let the engine handle it](keep-mod-sounds-sensible-under-time-warp-1.md) | KSAGolf | `fmod-audio` | `startup:asset-load`, `frame:draw-ui` | One attribute per sound; a missing channel above 10x must be handled by the caller |
| [2: Cut your own loops below the engine's threshold](keep-mod-sounds-sensible-under-time-warp-2.md) | KSACars | `fmod-audio` | `frame:draw-ui`, `frame:post-render` | Full control and a cleaner sound (no pitch swings) at the cost of a threshold you own |
| [3: Channels the game never registers](keep-mod-sounds-sensible-under-time-warp-3.md) | unscience (BYO Music) | `fmod-audio` | `frame:draw-ui`, `frame:post-render` | Stable pitch and sound in warp for free; no fades, no `Param` curves, no debug listing, and you stop it yourself |

Source files: [src/KSAGolf/Ksa/GolfSounds.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/GolfSounds.cs) (article 1), [src/KSACars/Ksa/BuggySound.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/BuggySound.cs) (article 2),
[byo-music.lib/VesselSound.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music.lib/VesselSound.cs) (article 3).

Choosing: use article 1 for impacts and one-shots that should slow down with the world; article 2 for continuous
engine-like loops whose input swings under warp; article 3 for music and narration that must ignore warp. The
sliders apply to all three as long as the channel is in one of the game's groups.

## See also

- [Ship custom sounds with a mod as XML sound assets](../../sound-assets/ship-custom-sounds-as-xml-assets/overview.md)
- [Play your own sound files through KSA's FMOD system without blocking](../play-sound-files-through-the-games-fmod-system/overview.md)
- [Play a sound at a world position and hold its spatial channel](../../playback/play-a-sound-at-a-world-position-and-hold-its-channel/overview.md)
- [Set the simulation speed from a mod](../../../flight-and-orbits/flight-state/set-simulation-speed-from-a-mod/overview.md)
- [Compute mission elapsed time and detect liftoff](../../../flight-and-orbits/flight-state/compute-mission-elapsed-time-and-detect-liftoff/overview.md)
- [Inject forces and impulses at the physics safe point](../../../physics/solver/inject-forces-and-impulses-at-the-physics-safe-point/overview.md)
- [Simulate mod-owned bodies outside the vehicle system](../../../physics/mod-simulation/simulate-bodies-outside-the-vehicle-system/overview.md)

[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
[^ksa-channelwrapper]: KSA.ChannelWrapper (decompiled, build 2026.10.10.5554)
[^ksa-gamesettings]: KSA.GameSettings (decompiled, build 2026.10.10.5554)
[^ksa-simspeedrule]: KSA.SimSpeedRule (decompiled, build 2026.10.10.5554)
[^ksa-soundreference]: KSA.SoundReference (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
