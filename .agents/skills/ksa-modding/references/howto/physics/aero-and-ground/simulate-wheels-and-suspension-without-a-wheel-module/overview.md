---
type: Game Feature
title: Simulate wheels, suspension and tyres without a wheel module
description: How a mod holds a craft above the ground on per-wheel springs and grips it with friction-circle tyres when KSA has no wheel, tyre or suspension module
category: physics
group: aero-and-ground
topic: simulate-wheels-and-suspension-without-a-wheel-module
demonstrated_by:
  - mods/ksacars/features/wheel-suspension-tyre-model
  - mods/ksacars/features/terrain-contact-under-wheels
lifecycle: [frame:dispatch-solvers]
game_types: [KSA.Vehicle, KSA.Celestial, KSA.PhysicsStates]
tags: [physics, wheels, suspension, tyres, friction-circle, steering, impulse, prepareworker]
---

KSA has no wheel module, no tyre model and no suspension: a part with wheels is a rigid body that rests on its colliders and
drags across the terrain with one friction value for every collider. A craft that drives, corners and rides over bumps needs
those forces from the mod. The approach that works is to keep the colliders clear of the ground, measure how far each wheel
hub is above the ground, work out a spring-damper load and a tyre force per wheel, and add the result to the craft's velocity
and spin once a frame, in the one window where such a write survives.

## Engine background

| Piece | Role for a wheel model |
|---|---|
| `Vehicle.PrepareWorker(SimStep)` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423) | Virtual, called on the main thread once per vehicle per frame right before the state is copied for the worker; a prefix is the last settled point at which a velocity write is kept[^ksa-vehicle] |
| `Universe.ExecuteNextVehicleSolvers` / `PrepareVehicleWorkers` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2155) | Calls `PrepareWorker` per vehicle in `CurrentSystem.All`, then queues the vehicle solver[^ksa-universe] |
| `Vehicle.GetPhysicsStatesMutable` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L922-L931) | The vehicle's `PhysicsStates` with `Kinematic.VelocityPhys` and `AngularVelocityPhys` that a prefix may change |
| `PhysicsStates.GetStatesCcf` [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L428) | Position, velocity and attitude in the planet-fixed frame, where the hub velocity over the ground is simply the velocity[^ksa-physicsstates] |
| `Celestial.GetTerrainHeightFromDirCce` | The ground under each hub; see [sample the ground under each wheel](../sample-the-ground-under-each-wheel/overview.md) |

The engine integrates a frame as one velocity change followed by its own substeps, so an explicit spring is only as stable as
the frame is short: the step the worker is about to integrate is the frame time times the simulation speed, and at 50x warp it
is a second or more.

## When it runs

Everything runs in a Harmony prefix on `Vehicle.PrepareWorker`, on the main thread in `frame:dispatch-solvers`:
`Program.PrepareFrame` has applied the last step and polled input, and `Universe.ExecuteNextVehicleSolvers` is about to snapshot
and queue the next. The model itself is pure arithmetic over a `WheelContact` per wheel, which is why KSACars keeps it in a
KSA-free `Sim/` folder and tests it headlessly. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#the-physics-step) and
[inject forces and impulses at the physics-safe point](../../solver/inject-forces-and-impulses-at-the-physics-safe-point/overview.md).

## Approaches

| Article | Mod | Technique | Trade-off |
|---|---|---|---|
| [Raycast-style suspension impulses in the PrepareWorker prefix](simulate-wheels-and-suspension-without-a-wheel-module.md) | KSACars | custom-physics | A complete spring, anti-roll, bump-stop and friction-circle tyre model as one per-frame impulse; stable at real frame rates, left out under warp; the "engine" is a launch-acceleration curve, not a driveline |

## See also

- [Sample terrain and pad surfaces under each wheel](../sample-the-ground-under-each-wheel/overview.md): where the hub heights come from
- [Override the terrain height cameras and wheels see](../override-terrain-height-for-roofs-and-platforms/overview.md)
- [Inject forces and impulses at the physics-safe point](../../solver/inject-forces-and-impulses-at-the-physics-safe-point/overview.md)
- [Keep a pushed craft off rails](../../solver/keep-a-pushed-craft-off-rails/overview.md)
- The topic query-terrain-height-and-line-of-sight (other batch) covers the height query itself
- [Query the terrain height field and test line of sight across terrain](../../../celestials/terrain/query-terrain-height-and-line-of-sight/overview.md) - related topic

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
