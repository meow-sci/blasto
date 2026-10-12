---
type: Game Feature
title: Generate measured atmosphere tables from reference profiles
description: Build compact altitude tables offline from probe and reference-atmosphere data, apply KSA's own boundary rule, decimate to the nodes that reproduce ln density within tolerance, and emit one generated source file for flight and shaders
category: celestials
group: atmospheres-and-rings
topic: generate-measured-atmosphere-tables-offline
demonstrated_by:
  - mods/realatmospheres/features/measured-atmosphere-generator
  - mods/realatmospheres/features/measured-flight-atmospheres
lifecycle: [startup:asset-load, physics:vehicle-solver]
game_types: [KSA.PhysicalAtmosphereReference, KSA.AstronomicalTemplate, KSA.AtmosphereReference]
tags: [atmosphere, tables, offline, generator, python, boundary, decimation]
---
KSA's flight atmosphere is one isothermal exponential per body: density and pressure are the sea-level value times `exp(-h / H)` with one scale height for both ([current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L80-L88)). That is a single number per body to describe a profile with temperature layers, mesospheres and hazes. To feed drag, engine back-pressure and shaders with measured values, a mod can build compact altitude tables offline from probe and reference-atmosphere data and replace the two lookup functions with table lookups.

## Engine background

- `PhysicalAtmosphereReference` holds `SeaLevelPressure`, `SeaLevelDensity` and `ScaleHeight`; `GetAtmosphericDensityAtAltitude` and `GetAtmosphericPressureAtAltitude` are `sea * exp(-h / H)` ([current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L7-L27), [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L80-L88)).
- Boundary rule: `CalculateBoundaryHeight` takes the higher of the altitudes where density falls to `MIN_DENSITY = 1e-9` and pressure to `MIN_PRESSURE = 1e-4` ([current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L8-L10), [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L42-L47)); it is computed at `OnDataLoad`.
- Readers of the physical atmosphere: `PhysicsEnvironment.RecomputePositionalValues` (per-vehicle pressure and density, zeroed above `Physical.Height`), drag and buoyancy in `PhysicsStates.ComputeDerivatives`, engine back-pressure, and audio and exhaust effects on the main thread ([current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L83-L102), [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1007-L1015)).
- `Physical.Height` also sets the atmosphere radius and near-surface radius of every `AtmosphericBody`, so a higher measured boundary makes the vehicle step force full physics for freefall from higher up ([current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L61-L65), [current/decomp/KSA/AtmosphericBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphericBody.cs#L20-L28)).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Python generator emitting checked-in C# tables for flight and shaders](generate-measured-atmosphere-tables-offline.md) | realatmospheres | `xml-asset` | build time; `startup:asset-load`, `physics:vehicle-solver` | One source of truth for C#, XML and GLSL; building the mod needs no Python because the output is checked in |

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Override atmosphere and ocean in the physics environment](../../../physics/aero-and-ground/override-atmosphere-and-ocean-in-the-physics-environment/overview.md), [Define atmospheres and cloud layers in system XML](../../../celestials/atmospheres-and-rings/define-atmospheres-and-cloud-layers-in-system-xml/overview.md)
- [Branch per body inside shared game shaders](../../../shaders/stock-shaders/branch-per-body-inside-shared-game-shaders/overview.md), [Validate asset XML references](../../../assets/validation/validate-asset-xml-references/overview.md)
- [Define atmospheres, scattering and cloud layers for a body in system XML](../define-atmospheres-and-cloud-layers-in-system-xml/overview.md) - related topic

