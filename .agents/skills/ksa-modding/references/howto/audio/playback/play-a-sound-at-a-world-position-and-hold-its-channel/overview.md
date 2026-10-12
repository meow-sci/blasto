---
type: Game Feature
title: Play a sound at a world position and hold its spatial channel
description: How a mod starts a declared sound so it pans, attenuates, Doppler-shifts and is muffled by air as if it came from a point in the world, and how it keeps and moves the channel afterwards
category: audio
group: playback
topic: play-a-sound-at-a-world-position-and-hold-its-channel
demonstrated_by:
  - mods/ksagolf/features/world-positioned-sounds
  - mods/ksarmory/features/spatial-gun-and-motor-sounds
  - mods/ksacars/features/engine-sound-crossfade
  - mods/ksacars/features/rocket-sound-without-engine
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame, frame:post-render, startup:asset-load]
game_types:
  - KSA.GameAudio
  - KSA.SpatialAudio
  - KSA.SoundBehavior
  - KSA.IChannel
  - KSA.Camera
  - KSA.PhysicalAtmosphereReference
  - KSA.ModLibrary
  - KSA.ChannelWrapper
  - KSA.CompoundChannelWrapper
  - KSA.Vehicle
tags: [audio, fmod, spatial-audio, doppler, channels, game-audio]
---

KSA plays a sound "at a place" by handing FMOD a position, a velocity and an air pressure, all expressed relative to
the audio camera. The engine does this for engines and explosions, but a mod whose objects are not vehicles (a golf
ball, a shell, a rocket motor simulated by the mod, a car's engine) gets nothing for free: it has to look a declared
sound up, build the position data itself and call the sound. If the sound lasts (a loop, a long tail) the mod also
has to keep the returned `IChannel` and place it again every frame, because the source moves and so does the
listener.

The three articles below are three levels of the same job: fire a sound once, hold a channel per source and sweep it,
and hold a looping channel per craft with guarded move and cut calls. All of them assume the sound is already
declared as an XML asset ([ship custom sounds as XML assets](../../sound-assets/ship-custom-sounds-as-xml-assets/overview.md)).

## Engine background

| Piece | What it does for a mod |
|---|---|
| `GameAudio.PlaySound(SoundBehavior, SpatialAudio, out IChannel?, float, bool, bool)` [current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L416-L423) | Calls `soundBehavior.Play(...)` unless `Universe.SimulationSpeed > 10` and `alwaysPlay` is false, in which case it returns no channel[^ksa-gameaudio] |
| `SoundBehavior.Play(SpatialAudio, float, out IChannel?, bool)` [current/decomp/KSA/SoundBehavior.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SoundBehavior.cs#L14-L17) | The virtual entry point that `Sound`, `CompoundSound` and `MultiSound` override; calling it directly skips the 10x gate[^ksa-soundbehavior] |
| `SoundReference.Play` [current/decomp/KSA/SoundReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SoundReference.cs#L72-L89) | Starts the FMOD channel paused, wraps it in a `ChannelWrapper`, applies volume and the first `SetSpatialAudio`, un-pauses unless `startPaused`, and registers the wrapper in `GameAudio.ActiveChannels`[^ksa-soundreference] |
| `SpatialAudio` [current/decomp/KSA/SpatialAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SpatialAudio.cs#L5-L74) | A readonly struct of `PositionEgo`, `VelocityEgo` and `AtmosphericPressure`. Constructors: `(double3 posEgo, double3 velEgo, double pressure)`, `(Astronomical)` and `(Astronomical, double3 offset)`; the last two read the audio camera[^ksa-spatialaudio] |
| `GameAudio.GetAudioCamera()` [current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L65-L68) | `Program.MainViewport.BaseCamera`, the listener |
| `Camera.GetPositionEgo` / `GetVelocityEgo` [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L236-L262) | Source position relative to the camera, and velocity relative to the object the camera follows[^ksa-camera] |
| `PhysicalAtmosphereReference.GetAtmosphericPressure(Camera)` [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L50-L59) | Pressure in atmospheres at the camera's nearby celestial, 0 where there is no atmosphere[^ksa-physicalatmospherereference] |
| `ChannelWrapper.SetSpatialAudio` [current/decomp/KSA/ChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChannelWrapper.cs#L434-L454) | Sends the view-space position and velocity to FMOD, turns pressure into a low-pass cutoff (`Lerp(160, 18000, clamp(pressure))`), applies the distance filter and the 2D/3D blend. Does nothing for a file declared `TwoD`[^ksa-channelwrapper] |
| `GameAudio.UpdateAudio` [current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L150-L215) | Once per frame in `PostRender`: runs every active channel through fade, simulation-speed pitch, parameters, volume, pitch and filters, and stops and drops any channel that is neither playing nor virtual |

Two facts shape every approach. First, the "listener" is the main viewport's base camera, and KSA never sets FMOD
listener attributes; it hands FMOD view-space positions via `SpatialAudio.PositionView()`, so the Ego vectors you
build must be relative to that camera in the same frame it will render this frame. Second, a `SpatialAudio` carries
one pressure, taken at the listener: in vacuum at the camera nothing is heard however loud the source is.

Timing: all of this is main-thread work. The lifecycle values are `frame:draw-ui` (the preferred point, the
`StarMapAfterGui` postfix), `frame:celestials` and `frame:after-on-frame` as the fallbacks when the HUD is hidden, and
`frame:post-render` where the engine applies what the mod set. See the
[game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and
[draw UI while the HUD is hidden](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: `GameAudio.PlaySound` with `SpatialAudio(Astronomical, offset)`](play-a-sound-at-a-world-position-and-hold-its-channel-1.md) | KSAGolf | `fmod-audio` | `frame:draw-ui` and fallbacks | Shortest code and engine-correct Doppler and pressure from a live anchor vehicle; fire-and-forget, so no control after the call; refused above 10x |
| [2: Hand-built Ego `SpatialAudio`, one held channel per source, swept against the live roster](play-a-sound-at-a-world-position-and-hold-its-channel-2.md) | KSArmory | `held-channel` | `frame:draw-ui` after the objects are stepped | Works for mod-simulated sources with no vehicle to anchor on; you own channel lifetime, sweeping and epoch-clean positions |
| [3: One looping channel per craft, guarded move and cut](play-a-sound-at-a-world-position-and-hold-its-channel-3.md) | KSACars | `fmod-audio` | `frame:draw-ui` before `PostRender` applies | Keeps a continuous sound attached to a craft you do know; every call guarded, dead channels replaced next frame |

Choosing: use article 1 for impacts, shots and other events; use article 2 when the source is an object the mod
simulates itself and the channel must follow it; use article 3 for a sound attached to a vehicle for as long as the
vehicle exists. Articles 2 and 3 both bypass `GameAudio.PlaySound`; read
[keep mod sounds sensible under time warp](../../channels/keep-mod-sounds-sensible-under-time-warp/overview.md) for
what that means above 10x simulation speed.

## See also

- [Ship custom sounds with a mod as XML sound assets](../../sound-assets/ship-custom-sounds-as-xml-assets/overview.md)
- [Drive a sound's Param curves from code and reuse stock sounds](../drive-sound-parameters-from-code/overview.md)
- [Crossfade engine loops and play spin-up, loop and tail sounds](../play-a-spin-up-loop-tail-sound-and-crossfade-engine-loops/overview.md)
- [Stop and release sound channels at scene change and game exit](../../channels/release-sound-channels-at-scene-change-and-exit/overview.md)
- [Understand KSA coordinate frames and epochs](../../../flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md)
- [Cursor to world ray](../../../ui/input/cursor-to-world-ray/overview.md) (finding a world point to sound at)
- [Simulate mod-owned bodies outside the vehicle system](../../../physics/mod-simulation/simulate-bodies-outside-the-vehicle-system/overview.md)
- [Replace the stock aerodynamic force model](../../../physics/aero-and-ground/replace-the-stock-aerodynamic-force-model/overview.md)
- [Contain exceptions in StarMap hooks](../../../getting-started/lifecycle/contain-exceptions-in-starmap-hooks/overview.md)

[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
[^ksa-soundbehavior]: KSA.SoundBehavior (decompiled, build 2026.10.10.5554)
[^ksa-soundreference]: KSA.SoundReference (decompiled, build 2026.10.10.5554)
[^ksa-spatialaudio]: KSA.SpatialAudio (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-physicalatmospherereference]: KSA.PhysicalAtmosphereReference (decompiled, build 2026.10.10.5554)
[^ksa-channelwrapper]: KSA.ChannelWrapper (decompiled, build 2026.10.10.5554)
