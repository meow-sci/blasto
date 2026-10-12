---
type: Game Feature
title: Simulate mod-owned bodies in sub-steps outside the vehicle system
description: How a mod runs missiles, shells or a golf ball as plain objects integrated in fixed sub-steps on KSA's simulated time, with the right frame, epoch and terrain contact
category: physics
group: mod-simulation
topic: simulate-bodies-outside-the-vehicle-system
demonstrated_by:
  - mods/ksarmory/features/mod-simulated-rounds
  - mods/ksarmory/features/analytic-subframe-fuses
  - mods/ksagolf/features/golf-ball-physics
  - mods/ksarmory/features/consume-applied-sim-step
  - mods/ksarmory/features/ecliptic-frames-and-epochs
  - mods/ksacars/features/terrain-contact-under-wheels
  - mods/ksagolf/features/swept-hull-contact
lifecycle: [frame:celestials, frame:after-on-frame, frame:draw-ui, frame:dispatch-solvers]
game_types:
  - KSA.Universe
  - KSA.SimStep
  - KSA.Celestial
  - KSA.Vehicle
  - KSA.Camera
  - KSA.PhysicsStates
  - KSA.Rendering.Water.Data.OceanReference
tags: [physics, custom-physics, sub-stepping, simulated-time, frames, epochs, projectile, headless-tests]
---

Not everything that moves in a mod needs to be a KSA vehicle. A shell, a missile or a golf ball can be a plain
C# object that the mod integrates itself: it is invisible to the save, to Bepu and to the solver, and it can be
stepped as finely as its speed requires. Doing that well comes down to five decisions: which clock to advance
by, which frame to integrate in, which instant each sample belongs to, how to find the ground, and how to keep the
maths testable without the game. The six articles below take one decision each.

This topic applies, and does not repeat, the timing facts in the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md): the physics step is applied at the start of `PrepareFrame`, the
vehicle step for the next frame is already running on workers while a mod's frame hook runs, and every
vehicle and celestial position a hook reads is the end of the step just applied.

## Engine background

| Piece | Role for a mod-simulated body |
|---|---|
| `Universe.GetLastSimStep` / `GetNextSimStep` / `GetElapsedTime` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2380-L2402) | The applied step, the in-flight step and the epoch of every world sample (`_lastSimStep.NextTime`) |
| `Universe.GetJobSimStep` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2615-L2627) | Sizes each step as `dtPlayer * achieved fraction * speed` and chains it from the previous `NextTime` |
| `Universe.ApplyVehicleSolvers` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1952) | Promotes the in-flight step and ends with `CurrentSystem.UpdatePerFrameData()`, the one parent-first walk that writes every body's and vehicle's ecliptic position |
| `SimStep` [current/decomp/KSA/SimStep.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SimStep.cs) | `PreviousTime`, `NextTime`, `DeltaTime` |
| `Program.OnFrameCelestials` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2701-L2732) (private) | A frame point that still runs with the HUD hidden |
| `Camera.GetPositionEgo` [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L236-L250) | Where KSA draws a craft; differs from its analytic ecliptic position when the camera follows a craft in its bubble |
| `PhysicsStates.GetStatesCcf` [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L428-L440) | A vehicle's physics state in the planet-fixed frame |
| `Celestial.GetTerrainHeightFromDirCcf` / `GetTerrainHeightFromDirCce` [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L789-L882) | The height field along a direction |

Frames and clocks used below: Ecl (ecliptic, carries the planet's ~29.8 km/s), Ccf (planet-fixed, no epoch), Ego
(camera-relative). A hook that runs in `frame:draw-ui` (`StarMapAfterGui`), `frame:celestials` (prefix on
`OnFrameCelestials`) or `frame:after-on-frame` sees the same epoch.

## Approaches

| Article | Mod | Technique | Decision it covers |
|---|---|---|---|
| [1: `IProjectile` objects with 5 ms sub-steps and a faithful-step clamp](simulate-bodies-outside-the-vehicle-system-1.md) | KSArmory | sub-stepping | The integration loop; back-dated samples; engine-free `Sim/` and one driver shared with a headless rig |
| [2: Ball integrated at up to 1/240 s in the body-fixed frame](simulate-bodies-outside-the-vehicle-system-2.md) | KSAGolf | sub-stepping | Simulated time, the planet-fixed frame, terrain contact, bounce and rolling |
| [3: Deduplicate `GetLastSimStep` on `NextTime`](simulate-bodies-outside-the-vehicle-system-3.md) | KSArmory | sim-clock | Which clock: the applied step, once, widened across missed frames |
| [4: Ecl state at `GetElapsedTime` with epoch back-dating](simulate-bodies-outside-the-vehicle-system-4.md) | KSArmory | epoch-backdating | Which instant a sample belongs to; Ecl, Ego, Cci, Cce |
| [5: Read `GetStatesCcf` in the `PrepareWorker` prefix](simulate-bodies-outside-the-vehicle-system-5.md) | KSACars | harmony-prefix | Reading a vehicle's physics state in Ccf for ground queries |
| [6: Differences-only inputs and headless tests](simulate-bodies-outside-the-vehicle-system-6.md) | KSAGolf | headless-tests | Keeping the maths free of game types and large coordinates |

## See also

- [Prevent tunnelling of fast bodies](../../mod-simulation/prevent-tunnelling-of-fast-bodies/overview.md): hit detection for the bodies integrated here
- [Apply drag and gravity to a mod-simulated body](../../mod-simulation/apply-drag-and-gravity-to-a-mod-simulated-body/overview.md): the forces integrated here
- [Ray cast against part meshes and terrain](../../mod-simulation/ray-cast-against-vessel-meshes-and-terrain/overview.md)
- The topics understand-ksa-coordinate-frames-and-epochs, query-terrain-height-and-line-of-sight and headless-test-harness-for-game-mods (other batches) cover the frame, terrain and test background in full
- [Detect hits for fast bodies without tunnelling](../prevent-tunnelling-of-fast-bodies/overview.md) - related topic
- [Apply air drag, gravity and aiming to a mod-simulated body](../apply-drag-and-gravity-to-a-mod-simulated-body/overview.md) - related topic
- [Understand KSA coordinate frames and epochs (ECL, CCE, CCI, CCF, ego, body)](../../../flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md) - related topic
- [Query the terrain height field and test line of sight across terrain](../../../celestials/terrain/query-terrain-height-and-line-of-sight/overview.md) - related topic
- [Test a mod against the real game with HeadlessHarness](../../../testing-and-tooling/offline-tests/headless-test-harness-for-game-mods/overview.md) - related topic

