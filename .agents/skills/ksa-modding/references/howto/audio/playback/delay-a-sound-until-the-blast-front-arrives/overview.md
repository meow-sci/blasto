---
type: Game Feature
title: Delay a world sound by the time sound takes to reach the listener
description: How a mod makes a distant event heard after it is seen, by following its expanding front against the live audio camera so that flying toward it brings the sound sooner
category: audio
group: playback
topic: delay-a-sound-until-the-blast-front-arrives
demonstrated_by: [mods/ksarmory/features/burst-sound-at-blast-arrival]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types:
  - KSA.GameAudio
  - KSA.PhysicalAtmosphereReference
  - KSA.AtmosphereReference
  - KSA.Camera
  - KSA.Celestial
  - KSA.Universe
tags: [audio, sound-delay, speed-of-sound, atmosphere, explosion, simulated-time]
---

KSA plays a sound at the instant of the event: a sound has a position and a distance falloff, but no travel time. For
most game events that is right (an explosion is heard from within its own blast radius or not at all). For an event
watched from kilometres away, such as a very large explosion, the gap between flash and bang is one of its most
recognisable features, and a mod that wants it has to make the delay itself. The delay has to be live: if the player
flies toward the event, the sound must arrive sooner, so a timer computed once at the start is wrong. This topic
shows the approach of following the event's expanding front against the camera every frame.

## Engine background

| Piece | What it gives a mod |
|---|---|
| `GameAudio.PlaySound(SoundEvent ...)` / `ExplosionSoundSystem.Spawn` [current/decomp/KSA/ExplosionSoundSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ExplosionSoundSystem.cs#L37-L65) | Start a sound at once; there is no time-of-flight in the engine. `Spawn` refuses beyond 50 km and above 10x simulation speed[^ksa-explosionsoundsystem] |
| `GameAudio.GetAudioCamera()` [current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L65-L68) | The listener, `Program.MainViewport.BaseCamera`; the front is compared with its position every frame[^ksa-gameaudio] |
| `Camera.PositionEcl` [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L110-L129) | The camera's ecliptic position; the origin of the Ego frame the sound is placed in[^ksa-camera] |
| `PhysicalAtmosphereReference` [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L6-L88) | KSA's air is isothermal: pressure and density are `sea level * exp(-altitude / scale height)`, and the atmosphere definition carries no temperature or speed of sound (`GasProperties` is the exhaust-gas model of the rocket nozzles). A mod derives a speed of sound from sea-level pressure and density (`sqrt(gamma * P0 / rho0)`) and a gamma of its own[^ksa-physicalatmospherereference] |
| `Astronomical.GetAtmosphereReference()` [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L392-L395) | A body's atmosphere, or null[^ksa-astronomical] |
| `Celestial.GetBodyFixed2Ecl()` [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L174-L182) | The rotation from the body-fixed frame to the ecliptic; anchors a place on a rotating planet[^ksa-celestial] |
| `GameAudio.OnApplicationStart` [current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L104-L110) | FMOD is initialised with `System.Init(1024, InitFlags._3dRightHanded, 0)`, up to 1024 channels; six coincident copies of one bang are six voices of the same sound, which is why the mod merges them |

A burst in vacuum makes no sound, by the same rule: no air means no front. A mod needs three decisions: how fast the
front runs, where it is anchored (a place on a rotating planet is not a fixed ecliptic point), and when it counts as
arrived.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Per-burst front aged on the simulated step and compared with the camera every frame, coincident bursts merged](delay-a-sound-until-the-blast-front-arrives.md) | KSArmory | `live-front-arrival` | `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame` | Correct for a moving listener and for pause and warp; needs an air model, a front law and a ground anchor; the sound itself must be played by hand ([drive parameters from code](../drive-sound-parameters-from-code/overview.md)) |

## See also

- [Drive a sound's Param curves from code and reuse stock sounds](../drive-sound-parameters-from-code/overview.md) (playing the bang when the front arrives)
- [Play a sound at a world position and hold its channel](../play-a-sound-at-a-world-position-and-hold-its-channel/overview.md)
- [Simulate mod-owned bodies outside the vehicle system](../../../physics/mod-simulation/simulate-bodies-outside-the-vehicle-system/overview.md) (the step that ages the front)
- [Spawn particle effects](../../../rendering/lights-and-particles/spawn-particle-effects/overview.md)
- The topic read-body-properties-and-orbit-elements (not yet written) will cover body radius, mass and rotation in full
- [Read a body's atmosphere, ocean, ring, rotation, tilt, tidal-lock and orbit-element data](../../../celestials/systems-and-stars/read-body-properties-and-orbit-elements/overview.md) - related topic

[^ksa-explosionsoundsystem]: KSA.ExplosionSoundSystem (decompiled, build 2026.10.10.5554)
[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-physicalatmospherereference]: KSA.PhysicalAtmosphereReference (decompiled, build 2026.10.10.5554)
[^ksa-astronomical]: KSA.Astronomical (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
