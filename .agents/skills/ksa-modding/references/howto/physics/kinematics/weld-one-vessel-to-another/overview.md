---
type: Game Feature
title: Weld or anchor one vessel to another
description: How a mod pins a vessel to another vessel or part by re-teleporting it onto the anchor pose every frame, stamped with the right simulation time, with optional eased animation and named presets stored in TOML
category: physics
group: kinematics
topic: weld-one-vessel-to-another
demonstrated_by:
  - mods/unscience/features/garrys-torch-vehicle-welding
  - mods/unscience/features/garrys-torch-scoped-collision-suppression
  - mods/unscience/features/garrys-torch-weld-animation-and-presets
  - mods/gatos/features/welds-vessel-anchoring
lifecycle: [frame:prepare, frame:draw-ui, frame:after-on-frame]
game_types: [KSA.Vehicle, KSA.SimStep, KSA.Universe, KSA.JobSystems]
tags: [physics, kinematics, weld, anchor, teleport, orbit, presets, animation]
---

KSA has no joint between separate vessels. A weld is therefore kinematic: each frame the mod computes where the source vessel
should be (the target's centre of mass or a chosen part, plus an offset in that frame) and teleports it there on a
freshly built orbit with the target's velocity. Done once per frame it looks like a rigid attachment. The work is entirely
in doing the teleport at a moment where it survives the physics step, stamping it with the right time, and keeping the
two vessels from fighting each other's contacts.

## Engine background

| Piece | Role |
|---|---|
| `Vehicle.Teleport(orbit, body2Cce, bodyRates)` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2241-L2293) | Rebuilds the flight plan, removes the vessel from its physics bubble, resets tank particles, overwrites the physics state from the orbit; leaves the bubble, so the finished step's result for it is never applied over the teleport |
| `Orbit.CreateFromStateCci(parent, stateTime, ...)` [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1611-L1617) | The state's epoch: the worker logs a body/origin time mismatch if it disagrees with the queued solver origin |
| `SimStep.PreviousTime` / `NextTime` | `PreviousTime` is `_lastSimStep.NextTime`: the time of the state just applied and the start of the step about to run |
| `PhysicsBubble.ApplyResultsToVehicles` [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L881-L909) | Walks the bubble's `_vehicleStates`; a vessel teleported out of the bubble is skipped |
| `JobSystems.VehicleSolver.Wait()` | Joins the in-flight vehicle step |

Two placements work. The best is **inside `Program.PrepareFrame` at the `Universe.GetJobSimStep` handoff**, after the previous
step was applied and before the next is snapshotted, stamped with `SimStep.PreviousTime`: the finished step's results are
already committed, so a welded craft keeps its robotics and module progress. The alternative is **from a frame hook after
`VehicleSolver.Wait()`**, stamped with the next step's time; it needs no patch but blocks the main thread until the step
finishes, and a source's stale result is not applied because the teleport detached it. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and
[the handoff hook](../../solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-3.md).

## Approaches

| Article | Mod | Technique | Trade-off |
|---|---|---|---|
| [1: `BeforePhysics` callback at the PrepareFrame handoff](weld-one-vessel-to-another-1.md) | unscience (Garry's Torch) | harmony-transpiler | Correct epoch, no lost actuator progress, works with the HUD hidden; needs the shared handoff transpiler; plus collision suppression between welded vessels |
| [2: `VehicleSolver.Wait` then teleport stamped with the next sim step](weld-one-vessel-to-another-2.md) | gatOS | kinematic-teleport | No patch; stalls the main thread while any weld exists; stamps with the in-flight step's end time |
| [3: Per-weld eased animation queues in player time](weld-one-vessel-to-another-3.md) | unscience | custom-physics | Smooth offset, rotation and scale changes; Euler interpolation; presets in TOML with legacy-key migration |

## See also

- [Disable collisions for one vessel without touching others](../../colliders/suppress-collisions-for-one-vessel/overview.md)
- [Teleport a vessel by building a new orbit](../teleport-a-vessel-by-building-a-new-orbit/overview.md)
- [Mutate vehicles at a physics-safe point](../../solver/mutate-vehicles-at-a-physics-safe-point/overview.md)
- The topics change-vehicles-without-racing-the-solver and anchor-to-parts-across-vehicle-changes (other batches) cover related timing and anchoring
- [Changing vehicles without racing the vehicle solver](../../../vehicles-and-parts/vehicles/change-vehicles-without-racing-the-solver/overview.md) - related topic
- [Keeping an anchor on a part across staging, docking, editor changes and saves](../../../vehicles-and-parts/part-geometry/anchor-to-parts-across-vehicle-changes/overview.md) - related topic

