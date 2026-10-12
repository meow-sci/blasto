---
type: Game Feature
title: Replace the stock aerodynamic force model and measure the active one
description: How a mod swaps KSA's box drag for its own lift and drag on every vehicle inside the solver, computes Mach from the game atmosphere, and measures whatever model is active by calling the force function on a copied state
category: physics
group: aero-and-ground
topic: replace-the-stock-aerodynamic-force-model
demonstrated_by:
  - mods/ksaero/features/body-of-revolution-aerodynamics
  - mods/ksaero/features/computedrag-call-site-transpiler
  - mods/ksaero/features/stock-drag-delegate-fallback
  - mods/ksaero/features/mach-from-game-atmosphere
  - mods/ksa-advancedflightcomputer/features/drag-table-measurement
  - mods/ksa-advancedflightcomputer/features/aerodynamic-glide-impact-steering
lifecycle: [physics:vehicle-solver, startup:all-mods-loaded, frame:draw-ui, frame:dispatch-solvers]
game_types:
  - KSA.PhysicsStates
  - KSA.PhysicsEnvironment
  - KSA.VehicleProperties
  - KSA.BoundingBoxCdA
  - KSA.PhysicalAtmosphereReference
  - KSA.ReadOnlyPhysicsStates
  - KSA.Vehicle
tags: [physics, aerodynamics, drag, lift, mach, speed-of-sound, harmony, transpiler, measurement]
---

KSA applies air drag to a craft with one small function, and that function knows nothing about lift, Mach number or the
shape of the craft beyond its bounding box. A mod that wants a rocket to lift, stall or feel transonic drag has to
replace that force without copying the physics step around it; a mod that plans flights has to know what force the game
(or an aero mod) really applies. This topic covers both directions: replacing the model and measuring the one that is
active.

The force model is `PhysicsStates.ComputeDerivatives`, which runs on the vehicle solver's worker threads once or twice
per substep per vehicle. Replacing a part of it therefore means writing worker-thread code (see
[write worker-thread-safe solver patches](../../solver/write-worker-thread-safe-solver-patches/overview.md)); measuring
it from the main thread works because the function is a pure static one over copied structs.

## Engine background

| Piece | What it does for aerodynamics |
|---|---|
| `PhysicsStates.ComputeDerivatives` (static, ten parameters) [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L956-L1087) | The force model for one vehicle and one substep: gravity, frame terms, buoyancy, `ComputeDrag` for ocean and atmosphere, nozzle thrust, parachutes. Returns a `Disturbances` value and writes nothing else[^ksa-physicsstates] |
| `PhysicsStates.ComputeDerivatives` instance forwarders [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L951-L954), [current/decomp/KSA/ReadOnlyPhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ReadOnlyPhysicsStates.cs#L116-L119) | Two instance overloads that pass `Origin`, `Kinematic`, `Props` and `Environment` to the static one[^ksa-readonlyphysicsstates] |
| `PhysicsStates.ComputeDrag` [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1089-L1111) | Private, `static`, `AggressiveInlining`: `(CdA + 0.1 * surfaceArea) * q` against the airspeed through the centre of mass, divided by a semi-implicit limiter, plus a rotational damping torque. Called twice from `ComputeDerivatives`: once with `environment.OceanDensity` and once with `environment.AtmosphericDensity` |
| `VehicleProperties.RecomputeAerodynamicProperties` [current/decomp/KSA/VehicleProperties.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleProperties.cs#L115-L130) | Builds the box model: per-face `BoundingBoxCdA` (0.3 on +X, 1.0 on -X, 1.2 on the sides) and `TotalSurfaceArea`[^ksa-vehicleproperties] |
| `BoundingBoxCdA.ComputeCdA` [current/decomp/KSA/BoundingBoxCdA.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BoundingBoxCdA.cs#L11-L17) | Dots the velocity direction with the face values; its result is the `dragCdA` argument and also the gate (`num6 > 0.0`)[^ksa-boundingboxcda] |
| `PhysicsEnvironment` [current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L10-L36) | `AtmosphericPressure` and `AtmosphericDensity` (both `float`), `OceanDensity`, `OceanVolume`, refilled by `RecomputePositionalValues` as the vehicle moves[^ksa-physicsenvironment] |
| `PhysicalAtmosphereReference` [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L80-L88) | `SeaLevelPressure * exp(-h / H)` and `SeaLevelDensity * exp(-h / H)`: same exponent for both, so pressure over density is constant on a body[^ksa-physicalatmospherereference] |

There is no Mach number, no angle of attack and no lift in the stock model. The airspeed direction only picks which
bounding-box faces count.

## Where this runs

`Program.PrepareFrame` applies the finished step (`Universe.ApplyVehicleSolvers`), polls input, and then calls
`Universe.ExecuteNextVehicleSolvers`, which calls `Vehicle.PrepareWorker` for every vehicle on the main thread before
queuing the `VehicleUpdateTask`.[^ksa-program][^ksa-universe] From that point the `PhysicsBubble` substeps call
`ComputeDerivatives` on worker threads until the step is done, while the main thread builds the UI and renders. The
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#the-physics-step) has the full
sequence, and notes that `ComputeDerivatives` runs twice per unconstrained Verlet substep, once per constrained
substep, and 12 times per RK8 long step (which is never used with drag).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: Call-site transpiler on ComputeDerivatives writing the Disturbances accumulator](replace-the-stock-aerodynamic-force-model-1.md) | KSAero | harmony-transpiler | `startup:all-mods-loaded` patch, runs in `physics:vehicle-solver` | Replaces the force for every vehicle with no shared state; the model must fit in the call's arguments and the patch refuses on a changed method shape |
| [2: Speed of sound from the atmosphere reference for a measurement tool](replace-the-stock-aerodynamic-force-model-2.md) | KSA-AdvancedFlightComputer | custom-physics | `frame:dispatch-solvers`, `frame:draw-ui` (main thread) | Measures the active model, whatever it is, by calling the static function on copied state; samples cost many evaluations, and a patch applied below `ComputeDerivatives` is not seen |

Pick article 1 to change what the game does and article 2 to learn what it does. They compose: a flight planner
built on article 2 measures an aero mod built on article 1 without knowing it exists.

## See also

- [Write worker-thread-safe solver patches](../../solver/write-worker-thread-safe-solver-patches/overview.md): how the replacement model may touch shared data
- [Swap one call inside a game method with a transpiler](../../../patching/transpilers/swap-one-call-inside-a-game-method-with-a-transpiler/overview.md): the patching technique in general
- [Apply air drag, gravity and aiming to a mod-simulated body](../../mod-simulation/apply-drag-and-gravity-to-a-mod-simulated-body/overview.md): the same air for bodies the mod integrates itself
- The topic override-atmosphere-and-ocean-in-the-physics-environment (same group) changes the density and pressure this model reads

[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^ksa-readonlyphysicsstates]: KSA.ReadOnlyPhysicsStates (decompiled, build 2026.10.10.5554)
[^ksa-vehicleproperties]: KSA.VehicleProperties (decompiled, build 2026.10.10.5554)
[^ksa-boundingboxcda]: KSA.BoundingBoxCdA (decompiled, build 2026.10.10.5554)
[^ksa-physicsenvironment]: KSA.PhysicsEnvironment (decompiled, build 2026.10.10.5554)
[^ksa-physicalatmospherereference]: KSA.PhysicalAtmosphereReference (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
