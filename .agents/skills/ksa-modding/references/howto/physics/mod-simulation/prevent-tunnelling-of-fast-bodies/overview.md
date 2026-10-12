---
type: Game Feature
title: Detect hits for fast bodies without tunnelling
description: How a mod finds the moment of contact for small fast objects by solving closest approach, hull casts or swept shapes over each sub-step instead of testing end points
category: physics
group: mod-simulation
topic: prevent-tunnelling-of-fast-bodies
demonstrated_by:
  - mods/ksarmory/features/analytic-subframe-fuses
  - mods/ksagolf/features/swept-hull-contact
  - mods/ksagolf/features/course-surface-exact-shapes
lifecycle: [frame:celestials, frame:after-on-frame, frame:draw-ui]
game_types: [KSA.Vehicle, KSA.Part, KSA.Celestial, KSA.Ray]
tags: [physics, tunnelling, continuous-collision, closest-approach, swept, fuse, hull]
---

A mod-simulated body (see [simulate bodies outside the vehicle system](../simulate-bodies-outside-the-vehicle-system/overview.md))
has no Bepu body, so the engine never tells it that it hit something. It must find contact itself, and a naive
distance test at the two ends of a step lets a fast object cross a small target between samples: a round at 2 km/s
moves 10 m per 5 ms sub-step, a golf ball over a metre per frame. The cure in all three solutions is to test the
step as a path, not its end points, and to report when inside the step contact occurred so the burst, bounce or
effect is placed at the right instant.

## Engine background

| Piece | Why it matters |
|---|---|
| `Part.RayCastEgo(ref readonly double4x4, Ray, ...)` [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L2534-L2545) | The engine's watertight ray cast against a part's subpart meshes; takes the assembly-to-ego matrix, so the origin is the caller's choice |
| `Vehicle.GetMatrixAsmb2Ego(double3 bodyPositionEgo)` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1276-L1281) | Builds that matrix with no camera; pass the mover-relative separation to work at metre scale |
| `Vehicle.MeanRadius` | `BoundingSphereRadiusBody`: a bounding measure, metres clear of a rocket's skin; use it for the broad phase only |
| `Celestial.GetTerrainHeightFromDirCce` [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L777-L781) | The terrain height field along a direction, for ground crossings |
| `Universe.GetLastSimStep` / `GetElapsedTime` | The epoch of every sample; targets must be moved to the instant the mover is at, see [approach 4 of the simulation topic](../simulate-bodies-outside-the-vehicle-system/simulate-bodies-outside-the-vehicle-system-4.md) |

All three approaches run on the main thread in the mod's once-per-frame step, after `PrepareFrame` has applied the
solvers, so every vehicle read is a finished, consistent step. Details are in the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Trade-off |
|---|---|---|---|
| [1: Analytic closest approach `t = -r.v / v.v` clamped to the step](prevent-tunnelling-of-fast-bodies-1.md) | KSArmory | analytic-closest-approach | Cheap and exact for linear relative motion and spherical targets; blind to hull shape and to curvature inside a sub-step |
| [2: Sweep against a bounding sphere, then cast the hull triangles](prevent-tunnelling-of-fast-bodies-2.md) | KSAGolf | continuous-collision | Hull-accurate, reuses the engine's mesh cast; main thread only, and not called by the shipping mod at the pinned commit |
| [3: Swept circle against grown boxes, posts and a cup](prevent-tunnelling-of-fast-bodies-3.md) | KSAGolf | analytic-geometry | Exact and seam-free for geometry the mod defines; useless for meshes it did not author |

Approaches 1 and 2 compose: KSArmory's `ContactSweep.TryStrike` is the same two-phase shape as KSAGolf's, with the
sphere test from approach 1 as the broad phase.

## See also

- [Simulate bodies outside the vehicle system](../simulate-bodies-outside-the-vehicle-system/overview.md)
- [Ray cast against part meshes and terrain](../ray-cast-against-vessel-meshes-and-terrain/overview.md)
- [Apply drag and gravity to a mod-simulated body](../apply-drag-and-gravity-to-a-mod-simulated-body/overview.md)
- The topics query-terrain-height-and-line-of-sight and understand-ksa-coordinate-frames-and-epochs (other batches) cover terrain queries and frames
