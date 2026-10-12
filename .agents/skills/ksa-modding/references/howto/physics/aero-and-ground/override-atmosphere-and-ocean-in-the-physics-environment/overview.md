---
type: Game Feature
title: Override a body's atmosphere profile or ocean in the physics environment
description: How a mod makes the solver read its own air density and pressure profile for a body, or zeroes the ocean in a region so a basement can sit below the waterline
category: physics
group: aero-and-ground
topic: override-atmosphere-and-ocean-in-the-physics-environment
demonstrated_by:
  - mods/realatmospheres/features/measured-flight-atmospheres
  - mods/ksastructures/features/dry-volume-below-sea-level
lifecycle:
  - startup:starmap-before-main
  - startup:asset-load
  - startup:all-mods-loaded
  - physics:vehicle-solver
  - render:ocean
  - frame:draw-ui
game_types:
  - KSA.PhysicalAtmosphereReference
  - KSA.AstronomicalTemplate
  - KSA.PhysicsEnvironment
  - KSA.Rendering.Water.Rendering.OceanRenderer
tags: [physics, atmosphere, ocean, drag, buoyancy, harmony, worker-thread]
---

The vehicle solver does not look at a planet's atmosphere or ocean directly. Once per integration step each vehicle
refreshes a small `PhysicsEnvironment` struct (air pressure, air density, ocean radius, immersed volume and surface area)
from two atmosphere functions and the ocean level, and `ComputeDerivatives` and the rocket module read that struct. A
mod that wants different air or no sea in one place therefore has two levers: change what the atmosphere functions
return, or change the struct after the game has filled it. Both run on the vehicle solver's worker threads, so the
rules in [write worker-thread-safe solver patches](../../solver/write-worker-thread-safe-solver-patches/overview.md) apply.

## Engine background

| Piece | Role |
|---|---|
| `PhysicalAtmosphereReference` [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L80-L88) | `GetAtmosphericPressureAtAltitude` and `GetAtmosphericDensityAtAltitude`: the sea-level value times `exp(-h / ScaleHeight)`, no cut-off. `Height` returns the private `_height`, set by `CalculateBoundaryHeight` where the thinner of density (1e-9) and pressure (1e-4) reaches its floor[^ksa-physicalatmospherereference] |
| `AstronomicalTemplate.OnDataLoad` [current/decomp/KSA/AstronomicalTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AstronomicalTemplate.cs#L131-L152) | Calls `AtmosphereReference?.OnDataLoad(mod)` while a body's template loads, which is where the stock boundary is computed[^ksa-astronomicaltemplate] |
| `PhysicsEnvironment.RecomputePositionalValues` [current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L83-L118) | Fills `AtmosphericPressure` and `AtmosphericDensity` from the two functions below `Physical.Height` (zero above), and `TerrainRadius`, `OceanRadius`, then `RecomputeImmersion` fills `OceanVolume` and `OceanSurfaceArea`[^ksa-physicsenvironment] |
| `PhysicsStates.RecomputePositionalEnvironmentValues` [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L526-L539) | The caller: refreshes the environment after the position updates inside the integrators (for example [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1174)), from `SetClosestParent`, and from the constraint simulation, so it is current for the vehicle's position when `ComputeDerivatives` reads it[^ksa-physicsstates] |
| `PhysicsStates.ComputeDerivatives` [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1002-L1015) | Buoyancy and `ComputeDrag` for the ocean when `OceanVolume` or `OceanSurfaceArea` is non-zero, and for the air when `AtmosphericDensity > 0` |
| `Rocket.UpdateRockets` [current/decomp/KSA/Rocket.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Rocket.cs#L181) | Engine state from `Environment.AtmosphericPressure`[^ksa-rocket] |
| `IParentBody.GetAtmosphereRadius`, `AtmosphericBody.GetNearSurfaceRadius` [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L60-L64), [current/decomp/KSA/AtmosphericBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphericBody.cs#L20-L28) | Mean radius plus `Physical.Height` (the second also plus 1 m); stored as `AtmosphereRadius` and `PhysicsRadius` when the closest parent is set, and used by `PhysicsBubble.FreefallNeedsFullPhysics`[^ksa-iparentbody][^ksa-atmosphericbody] |
| `UpdateFlotationRadius` [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L2629-L2637) | Positions a floating vehicle at `Environment.OceanRadius` plus offsets, by calling `RecomputePositionalValues` itself[^ksa-physicsbubble] |
| `OceanRenderer.Render`, `RenderUnderwater`, `GetMaxVisibleDepth` [current/decomp/KSA.Rendering.Water.Rendering/OceanRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Water.Rendering/OceanRenderer.cs#L473-L482) | The visual ocean; separate from the physics ocean, so removing physics water does not hide the surface[^ksa-oceanrenderer] |

The physical ocean is one sphere (mean radius plus `OceanReference.Level`, or the displaced wave height near the surface)
that knows nothing about land. A vehicle counts as immersed whenever that radius is above the terrain under it. That is
why a basement dug below sea level is flooded as far as the physics is concerned.

## When it runs

`Program.PrepareFrame` dispatches the step with `Universe.ExecuteNextVehicleSolvers`, which calls `Vehicle.PrepareWorker`
per vehicle on the main thread and then queues the vehicle solver. `RecomputePositionalValues` and the two atmosphere
functions are then called from `PhysicsStates` and `PhysicsBubble` on worker threads, per substep, for several vehicles at
once; the main thread also reads the atmosphere functions for audio and exhaust effects. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#the-physics-step). The two
approaches differ in when they attach: the atmosphere table must be registered while the template loads
(`startup:asset-load`), so its patches go in at `startup:starmap-before-main`; the ocean postfix only needs to exist before
the first step and goes in at `startup:all-mods-loaded`.

## Approaches

| Article | Mod | Technique | Trade-off |
|---|---|---|---|
| [1: Prefix the density and pressure functions and move the private boundary height](override-atmosphere-and-ocean-in-the-physics-environment-1.md) | RealAtmospheres | harmony-prefix | One profile per body for everything that reads the atmosphere (drag, engines, exhaust, audio, physics radius); lock-free lookups, but patches must exist before content loads and match bodies by template id |
| [2: Postfix PhysicsEnvironment.RecomputePositionalValues on the worker](override-atmosphere-and-ocean-in-the-physics-environment-2.md) | KSAStructures | harmony-postfix | Rewrites the finished struct only inside a region and only for craft that are not floating; the visual ocean needs its own patches |

Pick article 1 to change the air everywhere and article 2 to change the physics around one place. They compose: the
postfix sees whatever the prefixed functions produced.

## See also

- [Replace the stock aerodynamic force model](../replace-the-stock-aerodynamic-force-model/overview.md): the force model that reads this environment
- [Apply air drag, gravity and aiming to a mod-simulated body](../../mod-simulation/apply-drag-and-gravity-to-a-mod-simulated-body/overview.md): bodies the mod integrates itself read the atmosphere functions directly, so approach 1 changes their air too
- [Write worker-thread-safe solver patches](../../solver/write-worker-thread-safe-solver-patches/overview.md)
- The topic query-terrain-height-and-line-of-sight (other batch) covers terrain heights, which this environment also holds as `TerrainRadius`
- [Query the terrain height field and test line of sight across terrain](../../../celestials/terrain/query-terrain-height-and-line-of-sight/overview.md) - related topic

[^ksa-physicalatmospherereference]: KSA.PhysicalAtmosphereReference (decompiled, build 2026.10.10.5554)
[^ksa-astronomicaltemplate]: KSA.AstronomicalTemplate (decompiled, build 2026.10.10.5554)
[^ksa-physicsenvironment]: KSA.PhysicsEnvironment (decompiled, build 2026.10.10.5554)
[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^ksa-rocket]: KSA.Rocket (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-atmosphericbody]: KSA.AtmosphericBody (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^ksa-oceanrenderer]: KSA.Rendering.Water.Rendering.OceanRenderer (decompiled, build 2026.10.10.5554)
