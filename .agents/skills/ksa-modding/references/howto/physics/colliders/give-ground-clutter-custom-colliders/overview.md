---
type: Game Feature
title: "Use ground clutter as colliders: custom shapes and vehicle interaction"
description: How a mod swaps a planet's ground clutter for its own collidable objects, builds the colliders from KSA's ColliderTemplate types, switches clutter collisions on and tunes how easily rocks come loose
category: physics
group: colliders
topic: give-ground-clutter-custom-colliders
demonstrated_by:
  - mods/unscience/features/pebbles-clutter-colliders
  - mods/unscience/features/pebbles-ground-clutter-replacement
  - mods/ksacars/features/scoop-colliders-and-loose-clutter
  - mods/ksagolf/features/course-colliders-as-clutter
lifecycle: [frame:dispatch-solvers, physics:vehicle-solver, physics:bepu, startup:asset-load, frame:draw-ui]
game_types:
  - KSA.ColliderTemplate
  - KSA.ClutterObjectTemplate
  - KSA.ConstraintSim
  - BepuPhysics.Collidables.ConvexHull
  - KSA.GroundClutterRenderer
  - KSA.BubbleClutterStatics
  - KSA.GameSettings
tags: [physics, bepu, clutter, colliders, ground-clutter, rocks, collider-template, convex-hull]
---

Rocks, trees and grass on a planet are "ground clutter". KSA gives collidable clutter real Bepu statics: a
vessel or kitten that hits one can knock it loose, swap it for a dynamic body, or destroy it. Clutter is the
one kind of static a mod can add to, and the one the engine already treats as ground, so it is the lowest-friction way to put
solid things into the world. This topic collects four uses: define the colliders of replaced clutter from KSA's own
template types, swap whole clutter bundles at the solver handoff, tune how easily rocks come loose, and register
your own statics as clutter.

## Engine background

| Piece | What it does |
|---|---|
| `ColliderTemplate` subclasses (`BoxColliderTemplate`, `SphereColliderTemplate`, `CapsuleColliderTemplate`, `CylinderColliderTemplate`, `ConvexHullColliderTemplate`) | Declarative collider; `CreateScaledShape(unlock, scale)` creates a Bepu shape in the global registry; `LocationAsmb`, `Collider2Asmb` position it |
| `ClutterObjectTemplate` | One object kind with its colliders, `MassKg`, scale range; `GetMass(scale)` is `MassKg * scale^3` |
| `ClutterEcotypePhysicalData` | Built per planet ecotype; for each of 16 scale buckets creates the shapes (one collider is a primitive, several a kinematic `Compound` with volume-weighted child masses) |
| `BubbleClutterStatics` | Per bubble: `Update`/`AddCell` add one Bepu static per collider primitive at the instance pose; `ReportHit`/`ApplyPendingHits` displace an instance at `impact energy >= Mass * DisplaceEnergyPerKg` (75.5 J/kg by default) or destroy at `DestroyEnergyPerKg` |
| `PhysicsBubble.SyncGroundClutterStatics` [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L351-L370) | Runs from `Universe.ExecuteNextVehicleSolvers` -> `SyncGroundClutter`; clears all statics unless clutter collisions are on, the bubble is body-fixed and the planet has clutter data |
| `GameSettings.Current.Simulation.GroundClutterCollisions` [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L1051-L1051) | The setting (default false, labelled experimental) |

Everything that creates a shape needs the registry free
([registry topic](../mutate-the-shared-bepu-shape-registry/overview.md)), and statics only appear while clutter
collisions are on. Mass is read only when a static is built, so a change to mass does nothing to rocks already in the
physics world. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Trade-off |
|---|---|---|---|
| [1: Subclass `ColliderTemplate` with an owned convex hull](give-ground-clutter-custom-colliders-1.md) | unscience (Pebbles) | subclass-game-type | Native collisions, displacement and freeing; reproduces stock hull behaviour by hand, so it can drift from the engine |
| [2: Swap clutter bundles in an `ExecuteNextClothSolvers` prefix](give-ground-clutter-custom-colliders-2.md) | unscience (Pebbles) | harmony-prefix | The earliest point after every solver and before both clutter readers; heavy reflection and mirrored engine code |
| [3: `GroundClutterCollisions` setting and `DisplaceEnergyPerKg`](give-ground-clutter-custom-colliders-3.md) | KSACars | bepu-physics | Two static fields and a setting; global side effects and a 400 ms rebuild cycle |
| [4: Register course statics as clutter on every pass](give-ground-clutter-custom-colliders-4.md) | KSAGolf | reflection | Your own boxes as ground; re-register every pass; infinite mass never displaces |

## See also

- [Add mod-owned static colliders](../add-static-colliders-to-physics-bubbles/overview.md)
- [Create and free shapes in the shared registry](../mutate-the-shared-bepu-shape-registry/overview.md)
- [Scale or move a vessel's colliders at runtime](../scale-or-move-a-vessels-colliders-at-runtime/overview.md)
- The topics replace-ground-clutter-meshes and exclude-ground-clutter-near-structures (other batches, celestials) cover the visual side
