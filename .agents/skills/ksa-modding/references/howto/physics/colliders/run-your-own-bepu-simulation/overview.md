---
type: Game Feature
title: Run your own Bepu simulation alongside KSA's
description: How a mod simulates loose props in a separate Bepu world with its own buffer pool and shapes, driven in the vessel's frame with apparent-force terms, and writes the poses onto subparts
category: physics
group: colliders
topic: run-your-own-bepu-simulation
demonstrated_by: [mods/gatos/features/iva-cabin-physics-bepu]
lifecycle: [frame:draw-ui, frame:after-on-frame]
game_types: [BepuPhysics.Simulation, KSA.Part, KSA.Vehicle, KSA.PhysicsStates]
tags: [physics, bepu, simulation, iva, props, cabin, apparent-forces, subpart]
---

KSA ships Bepu 2.5 inside the game, so a mod can create its own `Simulation` with its own `BufferPool` and shapes,
step it in its own hook, and use it for cosmetic physics that must not couple back into flight. The reasons not to use
KSA's vehicle physics for this are strong: each KSA vehicle is one dynamic Bepu body with a coarse compound collider, IVA
interior parts declare no colliders, a cabin body inside that collider would be ejected through the hull and push the
spacecraft, the constraint simulation is not stepped for a coasting vessel, and it runs on solver worker threads with a
shared static `GlobalShapes` registry.

## Engine background

| Piece | Role |
|---|---|
| `BepuPhysics.Simulation.Create(bufferPool, narrowPhase, poseIntegrator, solveDescription)` [current/decomp/BepuPhysics/Simulation.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/BepuPhysics/Simulation.cs#L158-L160) | Builds an independent simulation with your own callbacks |
| `Simulation.Timestep(float dt, IThreadDispatcher = null)` [current/decomp/BepuPhysics/Simulation.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/BepuPhysics/Simulation.cs#L288-L290) | Without a dispatcher every callback runs on the calling thread |
| `Vehicle.AccelerationBody`, `BodyRates`, `AngularAccelerationBody`, `CenterOfMassAsmb` | Main-thread fields written by `ApplyVehicleSolvers`; `AccelerationBody` is a true accelerometer reading |
| `MeshReference.PositionCompare` | The de-indexed triangle positions KSA keeps for part ray casts; usable as interior collision geometry |

The private world never touches `ConstraintSim`'s shared shapes and runs on the main thread, so none of the registry or
worker-thread rules apply. The cost is that nothing in it knows about KSA: you supply the forces and write results onto
objects KSA already draws. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Trade-off |
|---|---|---|---|
| [Single-threaded `Timestep` with its own `BufferPool` in the assembly frame](run-your-own-bepu-simulation.md) | gatOS | bepu-physics | Isolated from flight, full control, rendered by the game for free; you own forces, geometry, wake logic and cleanup |

## See also

- [Disable collisions for one vessel without touching others](../suppress-collisions-for-one-vessel/overview.md)
- [Create and free shapes in the shared registry](../mutate-the-shared-bepu-shape-registry/overview.md), the registry you avoid by using your own
