---
type: Game Feature
title: Disable collisions for one vessel without touching others
description: How a mod takes a single vessel out of Bepu collision detection for the duration of each constraint-sim pass, by swapping its shape for an empty one and restoring it in a finalizer
category: physics
group: colliders
topic: suppress-collisions-for-one-vessel
demonstrated_by: [mods/unscience/features/garrys-torch-scoped-collision-suppression]
lifecycle: [frame:prepare, physics:vehicle-solver, physics:bepu]
game_types: [KSA.ConstraintSim, BepuPhysics.BodyReference]
tags: [physics, bepu, collisions, suppression, welds, harmony, finalizer, broad-phase]
---

Sometimes a vessel must be held overlapping another: a welded source pinned to a target every frame produces violent
contact impulses, dents, and (since KSA 5402) structural part failure. The wanted behaviour is "keep simulating,
but touch nothing". KSA has no per-vessel collision flag, and its contact filter (`NarrowPhaseCallbacks`) is not
a place a patch can be relied on, so the only single-lever answer found is to remove the vessel's collidable from the
Bepu broad phase for the length of a pass.

## Engine background

| Piece | Role |
|---|---|
| `ConstraintSim.DetectCollisions(double)` and `Simulate(double, ref readonly SimStep)` [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L870-L902) | The two entry points that run Bepu collision work for one bubble: each begins `BeginContactPass()`, calls Bepu synchronously with the sim's `_workerDispatcher`, and `Simulate` then runs `ClutterStatics.ApplyPendingHits` and `PartContactLoad.Apply` |
| `ConstraintSim.HandleToState` | Bepu body handle to `VehicleUpdateState` (`ReadOnlyVehicle`) for every vehicle in the bubble |
| `Bodies.SetShape` [current/decomp/BepuPhysics/Bodies.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/BepuPhysics/Bodies.cs#L349-L379) | Changing the shape's existence adds or removes the collidable from the broad phase (`UpdateForShapeChange`) and updates bounds; writing `Collidable.Shape` alone leaves a stale entry |
| `NarrowPhaseCallbacks` | An internal readonly struct whose methods are `AggressiveInlining` and consumed as a generic struct by Bepu, so a Harmony patch on it cannot be relied on to run |

Each `ConstraintSim` is driven by one island job at a time, so a prefix and a finalizer on those methods bracket all
collision dispatch for that simulation on one thread. The finalizer matters: a pass can throw, and a shape left removed
would make the vessel permanently intangible. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Trade-off |
|---|---|---|---|
| [BodyReference.SetShape(default) in a prefix with a restoring finalizer](suppress-collisions-for-one-vessel.md) | unscience (Garry's Torch) | harmony-finalizer | Scoped to a pass, exception-safe; the suppressed vessel still takes gravity, aero and non-contact damage, and is woken every pass |

## See also

- [Weld or anchor one vessel to another](../../kinematics/weld-one-vessel-to-another/overview.md), the feature this was built for
- [Write worker-thread-safe patches inside the vehicle solver](../../solver/write-worker-thread-safe-solver-patches/overview.md)
