---
type: Game Feature
title: Steer by predicted miss (zero-effort-miss guidance) and model homing sensors
description: Steer a missile or falling object by its line-of-sight rate, its zero-effort miss or its predicted impact, and model a sensor's field of view, line of sight and target loss for homing objects
category: flight-and-orbits
group: guidance
topic: steer-by-zero-effort-miss
demonstrated_by:
  - mods/ksarmory/features/proportional-navigation-guidance
  - mods/ksa-advancedflightcomputer/features/aerodynamic-glide-impact-steering
  - mods/ksarmory/features/guided-bomb-tail-kit-and-sight
  - mods/ksarmory/features/ground-target-designation-and-reach
  - mods/ksarmory/features/seekers-uplink-and-laser-homing
lifecycle: [frame:dispatch-solvers, frame:draw-ui, frame:celestials, render:imgui, frame:after-on-frame]
game_types:
  - KSA.Vehicle
  - KSA.Celestial
  - KSA.IParentBody
  - KSA.Universe
  - KSA.Program
  - KSA.Camera
  - KSA.FlightComputer
  - KSA.Part
  - KSA.PartTree
  - KSA.Ray
tags:
  - guidance
  - zero-effort-miss
  - proportional-navigation
  - homing
  - predicted-impact
  - seeker
  - raycast
  - designation
---
KSA has no homing or terminal guidance of its own: the stock flight computer holds attitudes and executes burns, and nothing in the engine steers a projectile at a target. Mods that want a missile, a guided bomb or a booster that glides onto a pad write the guidance law themselves and feed it game state. Two families appear: laws that null the rotation of the line of sight to a moving target (proportional navigation), and laws that predict where the object will end up if nothing more is commanded and steer that zero-effort miss (ZEM) onto a fixed place. Around either sits a sensor model that decides what the guidance may see.

## Engine background

- **Target and ground state.** `Vehicle.GetPositionEcl()` and `GetVelocityEcl()` are cached end-of-step values; `Celestial.GetVelocityEcl()` plus the spin term gives the velocity of the ground or air under a point.[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L954-L963)
- **Spin axis.** Bodies spin about CCI +Z, the parent pole, which is also the axis inclination and LAN are measured against: `IParentBody.GetAngularVelocityCci()` is `(0, 0, GetAngularVelocity())`, and `GetAngularVelocityCce()` rotates it into the ecliptic-aligned frame.[^ksa-iparentbody]
- **Ground frame.** `Celestial.GetBodyFixed2Ecl()` (which returns `GetCcf2Cce()`) and `IParentBody.GetCci2Ccf()` are the rotations that make a place on the ground stand still.[^ksa-celestial] [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L174-L188)
- **The step and its epoch.** `Universe.GetLastSimStep()` (`DeltaTime`, `NextTime`) and `Universe.GetElapsedTime()` say how far the world moved and when the samples are from; homing code back-dates samples by the frame step.[^ksa-universe]
- **Raycasts.** `Part.RayCastEgo` with `Vehicle.GetMatrixAsmb2Ego` and the `KSA.Ray` struct cast a sensor beam against a craft's meshes.[^ksa-part] [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L2534)
- **Timing.** Mod-integrated rounds step in `[StarMapAfterGui]` (`frame:draw-ui`), from a `Program.OnFrameCelestials` prefix when the HUD is hidden (`frame:celestials`), or in `[StarMapAfterOnFrame]`; a guided KSA vehicle steps in a `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`). Overlays draw into ImGui and render in `render:imgui`. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Proportional navigation on a back-dated target sample](steer-by-zero-effort-miss-1.md) | KSArmory | sim boundary, true PN | `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame` | Leads a crossing mover with six lines of law; needs epoch care and a frame velocity; wrong tool for a store falling onto a place |
| [ZEM on a predicted impact point](steer-by-zero-effort-miss-2.md) | KSA-AdvancedFlightComputer | closed-loop guidance, measured Jacobian | `frame:dispatch-solvers` | Gain over tgo squared holds from cutoff to touchdown; needs a drag-integrated predictor with derivatives |
| [Predicted-impact guidance with ground designation](steer-by-zero-effort-miss-3.md) | KSArmory | closed-form ZEM, body-fixed aimpoint | `frame:draw-ui`, `frame:celestials` | Cheap closed-form fall per sub-step; cannot follow a mover; reach decides whether a click is a correction |
| [Gimbal limit, raycast occlusion and uplink loss](steer-by-zero-effort-miss-4.md) | KSArmory | engine raycast, sensor model | `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame` | Realistic failure modes around any law; uplink loss is permanent and has no grace period |

## See also

- [Fly a boostback burn and glide a booster to its landing site](../fly-a-boostback-and-glide-to-a-landing-site/overview.md)
- [Predict impact points with drag and build a ballistic computer](../predict-impact-point-and-build-a-ballistic-computer/overview.md)
- [Anchor points to a rotating body and pick bodies under the cursor](../../map-view/anchor-and-pick-points-on-bodies/overview.md)
- [Find the closest approach to a target along a trajectory](../../map-view/find-closest-approach-along-the-flight-plan/overview.md)

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
