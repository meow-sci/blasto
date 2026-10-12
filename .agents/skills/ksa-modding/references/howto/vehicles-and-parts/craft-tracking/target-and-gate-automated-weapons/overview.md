---
type: Game Feature
title: Picking and gating targets for automated weapons
description: Closest-approach threat classification, sensor cone and lock dwell with terrain masking, and an explainable ladder of fire gates with a shared per-craft target tally
category: vehicles-and-parts
group: craft-tracking
topic: target-and-gate-automated-weapons
demonstrated_by:
  - mods/ksarmory/features/cpa-threat-classification
  - mods/ksarmory/features/radar-search-and-lock
  - mods/ksarmory/features/fire-control-and-target-allocation
lifecycle: [frame:draw-ui, frame:after-on-frame, frame:celestials]
game_types: [KSA.Vehicle, KSA.Universe, KSA.CelestialSystem, KSA.Program, KSA.Part, KSA.Celestial]
tags: [targeting, radar, cpa, fire-control, iff, terrain-masking, weapons]
---
KSA has no fire-control layer for mod weapons: no notion of threat, sensor volume, lock or "who is already shooting at that". A mod that automates weapons has to turn the vehicle census
(`Universe.CurrentSystem.All`) and each craft's state vectors into decisions: which contact deserves a shot, can this sensor see it, and may this launcher fire right now. KSArmory does it in three layers, each pure arithmetic over relative position and velocity
with no KSA types except at the edges: a closed-form closest-point-of-approach classifier, a sensor model with masking and lock dwell, and an ordered ladder of named gates with a shared per-craft tally of rounds in the air.

## Engine background

- **Inputs.** `Vehicle.GetPositionEcl()`, `GetVelocityEcl()` and `AccelerationBody` (an accelerometer reading, no gravity) [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L954-L963)[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L573); the vehicle census from `Universe.CurrentSystem.All` ([Track which craft carry your part](../track-and-classify-craft/overview.md)).
  `Vehicle.IsDebris` flags fragments shed by part failure [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L393).
- **Epoch.** `Program.PrepareFrame` calls `Universe.ApplyVehicleSolvers()` and then `ExecuteNextVehicleSolvers(...)` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2160-L2172)[^ksa-program] [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2216-L2220); every hook after that sees craft state at the end of the step just applied while the workers integrate the next, so a kinematic sample taken
  from `[StarMapAfterGui]` (`Program.OnDrawUiViewports`, inside `if (DrawUI)`) or `[StarMapAfterOnFrame]` is a consistent single instant [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2262-L2269). In the ecliptic frame all craft share about 29.8 km/s of common motion; differencing two samples of one instant cancels it.
- **Terrain.** `Celestial.GetTerrainHeightFromDirCce(dir, accurate)` returns a height sample along a direction [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L777)[^ksa-celestial]; each call is a height-map fetch, which is why a sensor samples only where closed-form geometry cannot decide.

## Approaches

| Article | Mod | Technique | Subject |
|---|---|---|---|
| [1: closed-form CPA with median-filtered acceleration](target-and-gate-automated-weapons-1.md) | KSArmory | `closed-form-geometry` | a threat is a contact that will pass within a radius inside a horizon, not one that is merely closing |
| [2: radar profile with search cone and lock dwell](target-and-gate-automated-weapons-2.md) | KSArmory | `closed-form-geometry` | cheap-to-expensive reject ladder, terrain sampled only in an ambiguous band, dwell by object identity |
| [3: gate ladder and per-craft target tally](target-and-gate-automated-weapons-3.md) | KSArmory | `gate-ladder` | ordered named gates whose first refusal is the reason; rounds-per-target shared across launchers |

## See also

- [Game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Track which craft carry your part, classify them, and detect staging and loss](../track-and-classify-craft/overview.md) (rosters and teams the classification relies on), [Give inert parts behaviour by recognising their part Id](../give-inert-parts-behaviour-by-part-id/overview.md), [Ray cast against part meshes and terrain](../../../physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/overview.md)
- Mod features: [cpa-threat-classification](../../../../mods/ksarmory/features/cpa-threat-classification.md), [radar-search-and-lock](../../../../mods/ksarmory/features/radar-search-and-lock.md), [fire-control-and-target-allocation](../../../../mods/ksarmory/features/fire-control-and-target-allocation.md)

[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^ksa-program]: current/decomp/KSA/Program.cs (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: current/decomp/KSA/Celestial.cs (decompiled, build 2026.10.10.5554)
