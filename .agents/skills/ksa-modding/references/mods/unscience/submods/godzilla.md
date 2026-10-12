---
type: Submod
title: Godzilla
description: Resizes vessels and EVA kittens at runtime with Smart or Basic scaling and independent visual, collider and physics channels, all applied at the physics handoff and exactly restorable
mod: unscience
submod: godzilla
tags: [scaling, colliders, rendering, physics, bepu]
mod_id: godzilla
---

Godzilla resizes a vessel or EVA kitten and restores it exactly. Smart mode is uniform and spaces
full parts about the captured centre of mass; Basic mode writes one raw XYZ scale to every part and
sub-part. Two global toggles (Scale physics, Scale colliders) split three channels: rendering always
scales, physics (mass, inertia, aero, nominal bubble size) follows the physical snapshot path, and
colliders can follow either. All edits are queued with `PhysicsFrameHook.Enqueue`; in 5554 the Bepu
shapes registry lock makes mid-step collision rebuilds throw, so the handoff is mandatory. Godzilla
and Garry's Torch never scale the same vessel (`VehicleScaleOwnership`).

## Hosting

- Manifest id `godzilla`, entry assembly `MeowSci.Godzilla` (development host only).
- Standalone host: F11 toggles "Godzilla"; its Patcher applies `HotkeyGuard`, `PhysicsFrameHook`,
  `KittenScalePatches`, `VisualScalePatches` and `ColliderScalePatches`.
- In unscience `ColliderScalePatches` and `VisualScalePatches` are applied by `unscience/Patcher.cs`;
  kitten scaling comes through the Garry's Torch entry.

## Features

| Feature | Category | Lifecycle | Summary |
|---|---|---|---|
| [Collider-only scaling through native owned shapes with nominal bounds preserved](../features/godzilla-collider-scaling.md) | physics | `frame:prepare`, `physics:vehicle-solver`, `physics:bepu` | Godzilla's collider channel overrides the scale ColliderModule.SetScale receives and the collider centre getter, then rebuilds the collision compound through the private Vehicle.UpdateCollisionGeometry while keeping the nominal physics bounds |
| [Whole-vessel scaling with Smart and Basic modes and three independent channels](../features/godzilla-vessel-scaling.md) | vehicles-and-parts | `frame:prepare`, `frame:draw-ui` | Godzilla resizes a vessel or EVA kitten from a captured snapshot in Smart (uniform, spaced about the centre of mass) or Basic (raw XYZ) mode, with native module and collider refresh at the physics handoff and independent render, physics and collider channels |
| [Render-only vessel scaling with draw-matrix and cull-radius patches](../features/godzilla-visual-scaling.md) | rendering | `frame:render`, `frame:prepare` | Godzilla's visual channel scales a vessel's drawn parts around its live centre of mass and enlarges only its render cull radius, using part-tree draw-matrix patches, a GetWorldMatrix postfix and two guarded call-site transpilers |

## Library classes

`GodzillaSubmod` (sessions, queued apply and restore, rollback, channel toggles, `CheckSessions`
`BeforePhysics` subscriber), `VesselScaleSnapshot` (with collider and persistence partials),
`ColliderScaleState`, `ColliderScalePatches`, `VisualScalePatches`. Public API: `RequestApply`,
`RequestRestore`, `SetScalePhysics`, `SetScaleColliders`, `SetVisualOnly`.

## Scene saves and persistence

Participant `godzilla`, version 1: global channel flags and per vessel `{VehicleId, Smart, Factor,
Physics, Colliders, Baseline}` where the baseline holds the pivot, kitten avatar scalar and per-part
structural references with original and current scale and position. Replay imports the baseline
(the native save already holds effective full-part transforms) and re-applies sub-part scales KSA omits.

## Further reading

- [godzilla/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla/README.md)
- [godzilla.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/README.md)
- [godzilla.tests/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.tests/README.md)
- [scope/vehicle-physics.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/vehicle-physics.md)
- [plans/KSA_5482_UPGRADE.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/plans/KSA_5482_UPGRADE.md)

## Entry points

- [godzilla/mod.toml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla/mod.toml)
- [godzilla/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla/Mod.cs)
- [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs)

## See also

- [unscience overview](../overview.md)
- [Garry's Torch](./garrys-torch.md)
- [I Feel Seen](./i-feel-seen.md) (also bypasses the pixel-size cull)
