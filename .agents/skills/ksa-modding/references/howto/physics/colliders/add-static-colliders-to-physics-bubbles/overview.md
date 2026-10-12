---
type: Game Feature
title: Add mod-owned static colliders that vessels and kittens collide with
description: How a mod makes placed models or course geometry solid, by adding Bepu statics to every nearby physics bubble each pass, or by declaring XML static objects, and why the stock contact filter must be taught to admit them
category: physics
group: colliders
topic: add-static-colliders-to-physics-bubbles
demonstrated_by:
  - mods/unscience/features/sphinx-static-colliders
  - mods/unscience/features/sphinx-glb-statics-placement
  - mods/ksagolf/features/course-colliders-as-clutter
  - mods/ksastructures/features/office-landmarks-static-objects
lifecycle:
  - physics:bepu
  - physics:vehicle-solver
  - frame:dispatch-solvers
  - startup:all-mods-loaded
  - frame:draw-ui
  - frame:render
game_types:
  - KSA.ConstraintSim
  - KSA.PhysicsBubble
  - KSA.StaticObject
  - BepuPhysics.Statics
  - KSA.NarrowPhaseCallbacks
  - KSA.KittenEva
  - BepuPhysics.Collidables.Box
  - BepuPhysics.Collidables.Mesh
tags: [physics, bepu, statics, colliders, contact-filter, kittens, physics-bubble, clutter]
---

A model drawn by a mod is only geometry: the physics knows nothing about it, so vessels sink into it and kittens walk
through it. To make it solid a mod has to put a Bepu static, built from a [shape](../mutate-the-shared-bepu-shape-registry/overview.md),
into the simulation of every physics bubble near it, and then get past the contact filter. KSA gives each physics
bubble its own `ConstraintSim` and `Bepu.Simulation`, so there is no global static list to add to.

## Engine background

| Piece | Role |
|---|---|
| `ConstraintSim` (one per bubble) | Owns `Simulation.Statics`, the bubble's clutter registry, and the per-vessel `BepuHandles` [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L54-L100) |
| `ConstraintSim.BeginStaticObjectPass` [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L545-L583) | Called at the start of each contact pass (`PhysicsBubble.FullPhysicsConstrainedStep` L2220, and `PopulateBepuStatesFromKinematic` L2928); a natural postfix point for per-bubble work |
| `NarrowPhaseCallbacks.AllowContactGeneration` [current/decomp/KSA/NarrowPhaseCallbacks.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/NarrowPhaseCallbacks.cs#L24-L57) | A static-to-body pair generates contacts only if the static is clutter, a terrain block, or the vehicle's own ground surface (`BepuHandles.IsGroundSurface`: its terrain patch or launch-pad `StaticObjectCollider`) |
| `ConstraintSim.IsGroundSurfaceFor` [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L169-L186) | The same three tests, asked for kitten ground friction and terrain-contact bookkeeping |
| `ConstraintSim.UpdateStaticObjectCollider` [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L584-L633) | Gives each body exactly one launch-pad static: the nearest resolved launch pad within 300 m (squared distance under 90000) |

So a plain static in `Simulation.Statics` generates no contacts. There are three ways past that, which are the three
approaches: replace the filter call with a check that also admits your handles; register the static as ground
clutter so the filter already accepts it; or avoid code and declare an XML `StaticObject` that the engine places at a
launch-pad landmark. Statics are bubble-relative, in `float`, and positions must be converted against
the bubble's origin (`PositionBub`); only a bubble in the body-fixed `Ccf` frame (landed) can use body-fixed
positions. The worker adds statics from inside the vehicle step. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Trade-off |
|---|---|---|---|
| [1: Per-bubble static handles from a `BeginStaticObjectPass` postfix](add-static-colliders-to-physics-bubbles-1.md) | unscience (Sphinx) | harmony-postfix | Any Box or two-sided Mesh, any pose; needs a transpiler on a hot engine method; closed-box detection picks the cheap shape |
| [2: Worker prefixes with statics registered as ground clutter](add-static-colliders-to-physics-bubbles-2.md) | KSAGolf | worker-thread-patch | No transpiler, kittens treat it as ground; reflection on a private dictionary; re-register every pass; infinite mass so it never displaces |
| [3: Declare XML static objects and let the engine collide the nearest pad](add-static-colliders-to-physics-bubbles-3.md) | KSAStructures | xml-asset | No physics code at all and kittens can walk on it; only the nearest launch pad within 300 m collides per body |

## See also

- [Create and free shapes in the shared registry](../mutate-the-shared-bepu-shape-registry/overview.md)
- [Use ground clutter as colliders](../give-ground-clutter-custom-colliders/overview.md)
- [Build a moving platform kittens can ride](../build-a-moving-platform-kittens-can-ride/overview.md)
