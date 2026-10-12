---
type: Game Feature
title: Read roll, pitch, heading, angle of attack and sideslip in the local horizon frame
description: How to express a vehicle's attitude and velocity relative to the local east-north-up horizon, using the game's own ENU frame helpers and quaternion type, and how to derive angle of attack and sideslip
category: flight-and-orbits
group: flight-state
topic: read-flight-state-in-a-local-horizon-frame
demonstrated_by:
  - mods/ksa-aircrafthud/features/flight-data-from-vehicle
  - mods/ksa-advancedflightcomputer/features/vehicle-state-to-site-frame
lifecycle: [frame:viewports, frame:dispatch-solvers]
game_types:
  - KSA.Vehicle
  - KSA.VehicleReferenceFrameEx
  - KSA.QuaternionEx
  - Brutal.Numerics.doubleQuat
  - KSA.PhysicalAtmosphereReference
tags: [attitude, roll, pitch, heading, enu, angle-of-attack, sideslip, quaternion, horizon]
---

KSA stores a vehicle's attitude as a quaternion in an inertial frame, not as roll, pitch and heading.
To show or control those you need the local horizon, a frame whose axes are east, north and up at the
vehicle's position. The game already contains the helpers; this topic shows two ways to use them and the
conventions you must not assume.

## Engine background

- `Vehicle.GetBody2Cci()` is `Body2Cce` concatenated with the parent's `GetCce2Cci()`.[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3127-L3130)
- `VehicleReferenceFrameEx.GetEnu2Cci(positionCci)` builds the local horizon at a CCI position: east is
  `cross(CCI +Z, position)` normalised, up is the radial direction, north is `cross(up, east)`; it returns
  `null` at the centre or exactly over a pole (where east is undefined). `GetEnuBody2Cci(positionCci)` concatenates
  `Vehicle.BODY2UPFRAME` (a fixed rotation, diag 1, -1, -1) with it.[^ksa-vehiclereferenceframeex]
  [current/decomp/KSA/VehicleReferenceFrameEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleReferenceFrameEx.cs#L80-L110) [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L271)
- Euler extraction is per frame: the game's `QuaternionToEulerAngles` uses `ToRollPitchYawRadians` (XYZ
  order) for `EnuBody` and `ToRollYawPitchRadians` for the other frames.[current/decomp/KSA/VehicleReferenceFrameEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleReferenceFrameEx.cs#L439-L452)
  [^ksa-quaternionex] [current/decomp/KSA/QuaternionEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/QuaternionEx.cs#L151-L160)
- `doubleQuat` is scalar-last (`Identity` is `(0, 0, 0, 1)`).[^ksa-doublequat] [current/decomp/Brutal.Numerics/doubleQuat.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Numerics/doubleQuat.cs#L61)
- The surface-relative (air-relative) velocity is `GetSurfaceVelocityCci()`: CCI velocity minus
  `parent.GetAngularVelocityCci() x PositionCci`. `PhysicsStates.ComputeAirVelocityBody` does the same removal of
  the frame rotation on the worker; there is no separate wind vector in it.[current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2954-L2958)
  [^ksa-physicsstates] [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L181-L190)

**Timing.** Attitude and position are valid on the main thread after `PrepareFrame` applied the step
(`frame:apply-solvers`); see the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).
KSA-AdvancedFlightComputer runs the conversion in its `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`), the
HUD in a postfix on `Program.OnFrameEditor` (`frame:viewports`).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Concatenate with the inverse ENU frame](read-flight-state-in-a-local-horizon-frame-1.md) | KSA_AircraftHUD | custom physics | `frame:viewports` | Three lines, uses the game's own frame; accepts the game's Euler order and a HUD-specific angle shift |
| [Transform basis vectors with the game's quaternion type](read-flight-state-in-a-local-horizon-frame-2.md) | KSA-AdvancedFlightComputer | frame conversion | `frame:dispatch-solvers` | Convention-proof, builds its own site frame; more code, needs a body-axis definition |

## See also

- [Understand KSA coordinate frames and epochs (ECL, CCE, CCI, CCF, ego, body)](../understand-ksa-coordinate-frames-and-epochs/overview.md)
- [Read a vehicle's position, velocity, attitude, orbit and flight instruments](../read-vehicle-flight-state/overview.md)
- [Build a local site frame and compute a launch window into a target plane](../../transfers/build-a-site-frame-and-compute-a-launch-window/overview.md)
- Related topics: [Predict impact points with drag and build a ballistic computer](../../guidance/predict-impact-point-and-build-a-ballistic-computer/overview.md), [Fly a boostback burn and glide a booster to its landing site](../../guidance/fly-a-boostback-and-glide-to-a-landing-site/overview.md)

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-vehiclereferenceframeex]: KSA.VehicleReferenceFrameEx (decompiled, build 2026.10.10.5554)
[^ksa-quaternionex]: KSA.QuaternionEx (decompiled, build 2026.10.10.5554)
[^ksa-doublequat]: Brutal.Numerics.doubleQuat (decompiled, build 2026.10.10.5554)
[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
