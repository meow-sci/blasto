---
type: Game Feature
title: Apply air drag, gravity and aiming to a mod-simulated body
description: How a mod gives its own integrated ball, shell or missile the same air, water and gravity the game uses, and aims a moving target with the same model
category: physics
group: mod-simulation
topic: apply-drag-and-gravity-to-a-mod-simulated-body
demonstrated_by:
  - mods/ksagolf/features/golf-ball-physics
  - mods/ksarmory/features/point-gravity-and-atmosphere-model
  - mods/ksarmory/features/unguided-ballistic-lead
  - mods/ksa-advancedflightcomputer/features/boostback-impact-prediction
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame, frame:dispatch-solvers]
game_types:
  - KSA.PhysicalAtmosphereReference
  - KSA.AtmosphereReference
  - KSA.Rendering.Water.Data.OceanReference
  - KSA.Celestial
  - KSA.Astronomical
  - KSA.IParentBody
  - KSA.Vehicle
tags: [physics, drag, gravity, atmosphere, ocean, custom-physics, ballistics, aiming]
---

A body the mod integrates itself (see [simulate bodies outside the vehicle system](../simulate-bodies-outside-the-vehicle-system/overview.md))
gets no forces from the game: the mod must supply gravity and drag, and the answer has to agree with what the game
does to vehicles or the shot will look wrong next to a rocket. KSA does not publish a force API for this, so mods
read the same data the vehicle solver reads: the body's mass through `IParentBody.Mu`, the atmosphere's density
curve, and the ocean's level and density, then apply them in their own sub-steps.

## Engine background

| Piece | What it gives a mod-simulated body |
|---|---|
| `IParentBody.Mu` [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L15-L15) | A default interface member, `Mass * 6.6743E-11`; read through a cast to `IParentBody` |
| `PhysicalAtmosphereReference.GetAtmosphericDensityAtAltitude` [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L85-L88) | `SeaLevelDensity * exp(-altitude / ScaleHeight)`; no cut-off above the atmosphere's `Height`, so the mod applies one |
| `OceanReference` [current/decomp/KSA.Rendering.Water.Data/OceanReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Water.Data/OceanReference.cs#L8-L12) | `Level` (a `DistanceReference`) and `Density` (a `DensityReference`, default 1028.13 kg/m3) |
| `DistanceReference.IsValid` [current/decomp/KSA/DistanceReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DistanceReference.cs#L162-L165) | `|value| > 100000`: Earth's 8 km scale height and the sea's 0 m level read as invalid, so never gate on `IsValid()` |
| `Celestial.MeanRadius`, `GetPositionEcl`, `Parent` | The altitude reference and the primary whose pull the body itself falls in |

The fall of the body is a second gravity term: a shell integrated against its own planet in the ecliptic frame is left
behind by the acceleration the ground shares, 428 m over a 375 s coast on KSA's Earth, unless the planet's own fall
toward its primary is added. All formulas run in the mod's per-frame step on the main thread, reading celestial data
that no worker writes; see the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Trade-off |
|---|---|---|---|
| [1: Density ratio to one reference air, ocean density below the waterline](apply-drag-and-gravity-to-a-mod-simulated-body-1.md) | KSAGolf | custom-physics | Simplest complete model, body-fixed surface gravity; airless is knowledge, not a failed read |
| [2: Shell and missile drag with the same density read](apply-drag-and-gravity-to-a-mod-simulated-body-2.md) | KSArmory | quadratic-drag | Point gravity back-dated to the sub-step plus the body's own fall; plus the ballistic-lead solver that flies the same model |
| [3: Impact prediction in a co-rotating atmosphere](apply-drag-and-gravity-to-a-mod-simulated-body-3.md) | KSA-AdvancedFlightComputer | custom-physics | Gravity and drag on air-relative velocity with the atmosphere rotating with the body; a differentiable integrator |

## See also

- [Simulate bodies outside the vehicle system](../simulate-bodies-outside-the-vehicle-system/overview.md)
- [Detect hits for fast bodies without tunnelling](../prevent-tunnelling-of-fast-bodies/overview.md)
- The topic override-atmosphere-and-ocean-in-the-physics-environment (other batch) covers changing the atmosphere the game's own solver uses
- [Override a body's atmosphere profile or ocean in the physics environment](../../aero-and-ground/override-atmosphere-and-ocean-in-the-physics-environment/overview.md) - related topic

