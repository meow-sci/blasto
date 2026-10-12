---
type: Game Feature
title: Inject forces, impulses and velocity changes at the physics safe point
description: How a mod pushes a craft (driven car, blast, boost, hover, lift, flip) by writing velocity, spin or pose deltas where they are integrated, and how to keep those pushes bounded at any time warp
category: physics
group: solver
topic: inject-forces-and-impulses-at-the-physics-safe-point
demonstrated_by:
  - mods/ksacars/features/prepareworker-impulse-injection
  - mods/ksarmory/features/blast-shove-impulse
  - mods/gatos/features/debug-cheats-teleport-refuel-warp
  - mods/ksacars/features/rocket-lift-hover-boost-downforce
  - mods/ksa-advancedflightcomputer/features/terminal-hover-touchdown
lifecycle: [frame:dispatch-solvers, frame:draw-ui]
game_types:
  - KSA.Vehicle
  - KSA.PhysicsStates
  - KSA.KinematicStates
  - KSA.Universe
  - KSA.Celestial
  - KSA.FlightComputer
  - KSA.SimStep
tags: [physics, vehicle, impulse, velocity, force, prepareworker, time-warp, hover, lift, blast]
---

KSA has no public "apply force" call. A mod that wants to push a craft (a car's tyres and rockets, a blast
wave, a cheat that kicks a vessel, a hover) computes a change of velocity and spin and writes it into the
vehicle's physics state at a moment where the physics step will integrate it. This topic covers what to write
(velocity deltas, spin deltas, pose changes), where (the `Vehicle.PrepareWorker` prefix or the
solver-dispatch window), and how to keep the push bounded when the physics step is long because the player is
warping time.

Where the safe moments are, and why a write anywhere else is lost, is the subject of
[mutate vehicles at a physics-safe point](../../solver/mutate-vehicles-at-a-physics-safe-point/overview.md) and of
the lifecycle reference's [settled-window approach](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md). This topic assumes
that and does not repeat it.

## Engine background

| Piece | Role |
|---|---|
| `Vehicle.PrepareWorker(SimStep)` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423) | Main-thread, per vehicle, immediately before `PrepareFromVehicle` snapshots kinematics for the worker |
| `Vehicle.GetPhysicsStatesMutable` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L922-L931) | A `PhysicsStates` whose `Kinematic`, `Props`, `Origin` and `Environment` alias the vehicle's own fields |
| `KinematicStates` [current/decomp/KSA/KinematicStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KinematicStates.cs) | `VelocityPhys`, `AngularVelocityPhys`, `PositionPhys`, `Body2Phys` in the bubble's physics frame |
| `PhysicsStates.UpdateFromAnalytic` [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L835-L860) | Rebuilds the integrated state from the orbit for a craft on rails |
| `Vehicle.Split` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1639-L1647) | The engine's own impulse pattern: velocity and position written, `SetOnRails(false)` |
| `SimStep` [current/decomp/KSA/SimStep.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SimStep.cs) | `DeltaTime` is the length of the step about to be integrated; it is 0 while paused and can be seconds at high warp |
| `Universe.ExecuteNextVehicleSolvers` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2139) | The other window: once per frame, before any snapshot |

Rules that follow for any push:

- The write is a change of velocity, not a force: KSA takes the push at the start of its step and then
  integrates gravity and its own forces through it.
- A railed craft ignores velocity writes: rebuild its state from the orbit, take it off rails, and refresh the
  flight plan (see [keep a craft under full physics while your mod pushes it](../../solver/keep-a-pushed-craft-off-rails/overview.md)).
- Because the push is one write per frame, a force you intend to hold must be restated every frame, and its
  size must be derived from `SimStep.DeltaTime`.

## Approaches

| Article | Mod | Technique | Trade-off |
|---|---|---|---|
| [1: Add velocity deltas in the `PrepareWorker` prefix](inject-forces-and-impulses-at-the-physics-safe-point-1.md) | KSACars | harmony-prefix | The generic recipe; includes rotating or lifting a craft in place by rewriting position, attitude and both velocities together |
| [2: Summed per-part shove with torque from a blast front](inject-forces-and-impulses-at-the-physics-safe-point-2.md) | KSArmory | impulse-write | Per-part lever arms and an angular impulse, saturated at the driving wind speed so nothing outruns the flow |
| [3: Impulsive kick from the solver-dispatch window](inject-forces-and-impulses-at-the-physics-safe-point-3.md) | gatOS | harmony-prefix | Simplest variant: one instantaneous delta-v by rebuilding the orbit; no per-part model, no torque |
| [4: Velocity and spin deltas gated by hatches](inject-forces-and-impulses-at-the-physics-safe-point-4.md) | KSACars | harmony-prefix | Lift, hover, boost, downforce as per-step deltas; exponential shares and per-step caps keep it stable at any warp |
| [5: Hover and touchdown under real engine power with a PID](inject-forces-and-impulses-at-the-physics-safe-point-5.md) | KSA-AdvancedFlightComputer | harmony-prefix | Commands the engines instead of injecting velocity; physically honest, but needs a throttleable engine |

| Choose | When |
|---|---|
| 1 or 4 | A craft with its own propulsion model that must hold a force every frame |
| 2 | A one-shot event with a spatial footprint (a blast, a collision) |
| 3 | A scripted kick, no per-frame state |
| 5 | The craft really has engines and the player should see them work |

## See also

- [Mutate vehicles at a physics-safe point](../../solver/mutate-vehicles-at-a-physics-safe-point/overview.md)
- [Keep a craft under full physics while your mod pushes it](../../solver/keep-a-pushed-craft-off-rails/overview.md)
- [Place a vessel on a surface at a latitude and longitude](../../kinematics/place-a-vessel-on-a-surface-at-latitude-and-longitude/overview.md) for putting a craft on the ground rather than pushing it
- The topic query-terrain-height-and-line-of-sight (another batch) for terrain heights used by the hover approaches
- [Keep a craft under full physics while your mod pushes it](../keep-a-pushed-craft-off-rails/overview.md) - related topic
- [Mutate vehicles at a physics-safe point](../mutate-vehicles-at-a-physics-safe-point/overview.md) - related topic
- [Query the terrain height field and test line of sight across terrain](../../../celestials/terrain/query-terrain-height-and-line-of-sight/overview.md) - related topic

