---
type: Game Feature
title: Plan and total multi-leg and interstellar delta-v routes
description: Classify a vessel's orbit state, derive a reference orbit for each body, find a route through the body tree, sum the leg delta-vs, and estimate fuel and coast time for an interstellar trip
category: flight-and-orbits
group: transfers
topic: plan-multi-leg-and-interstellar-delta-v-routes
demonstrated_by:
  - mods/ksa-deltavmap/features/route-finding-and-accumulation
  - mods/ksa-deltavmap/features/body-state-ladder-and-you-are-here
  - mods/ksa-deltavmap/features/interstellar-routes-and-fuel
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Program
  - KSA.Vehicle
  - KSA.Orbit
  - KSA.IParentBody
  - KSA.ConsoleWidgets
  - KSA.AtmosphericBody
  - KSA.AstronomicalTemplate
  - KSA.PhysicalAtmosphereReference
  - KSA.InterstellarBurnPlan
  - KSA.InterstellarDrive
  - KSA.InterstellarBurnEstimate
  - KSA.OrbitalTransfers
  - KSA.VehicleProperties
tags: [delta-v, route, ladder, lca, interstellar, rocket-equation, classification, delta-v-map]
---

A multi-leg delta-v plan is a path through a tree of states (surface, low orbit, SOI edge, parking orbit at a star) whose edges carry costs. This topic covers the three pieces KSA-DeltaVMap builds on the game's data: a per-body ladder of states and a classifier that puts the vessel on it, a route finder that walks the tree through the lowest common ancestor, and an interstellar leg that reuses the
game's own burn model for fuel.

## Engine background

- **Body data for the ladder** comes from the live celestial: `IParentBody.MeanRadius`, `Mu`, `SphereOfInfluence`, the atmosphere reference (`GetAtmosphereReference().Physical.Height`, `SeaLevelDensity`), rotation rate and terrain height. `Astronomical.GetNearSurfaceRadius()` returns `MaxTerrainRadius + 1`; `AtmosphericBody` overrides it to `MeanRadius + atmosphere height + 1` when it has an atmosphere.[^ksa-iparentbody]
  [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L15-L64) [^ksa-atmosphericbody]
- **Vessel state** uses the game's contact flag: `Vehicle.HasMeaningfulOrbit => !Situation.HasAnyContact()`.[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L551)
- **Interstellar model.** `OrbitalTransfers.SolveInterstellar` sets the transit time to `distance / hyperbolicExcess` and starts with `DepartureSpeedForExcess(mu, r, vInf) = sqrt(vInf^2 + 2 mu / r)`; `MAX_INTERSTELLAR_SPEED` is 269,813,212.2 m/s (0.9 c). `InterstellarDrive.TryGetFrom(vehicle, out drive)` scans active `EngineController`s for an `AntimatterRamCore`; `InterstellarBurnPlan.Burn` and `CoastCollection` integrate the drive.[^ksa-orbitaltransfers]
  [current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs#L228) [current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs#L399-L402) [^ksa-interstellardrive] [current/decomp/KSA/InterstellarDrive.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InterstellarDrive.cs#L7-L37) [^ksa-interstellarburnplan]

All of it runs on the main thread inside the map window's draw (`frame:draw-ui`) and is event-driven. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [LCA path on a re-rooted tree, margin at display](plan-multi-leg-and-interstellar-delta-v-routes-1.md) | KSA-DeltaVMap | tree LCA routing | `frame:draw-ui` | No graph search; unique paths; toggles for landing, aerobraking, plane change, return |
| [Body-state ladder and eccentricity classification](plan-multi-leg-and-interstellar-delta-v-routes-2.md) | KSA-DeltaVMap | vehicle-state classification | `frame:draw-ui` | Sensible ladders for modded systems with no per-body config; heuristics for gas giants |
| [Reuse InterstellarBurnPlan, golden-section speed search](plan-multi-leg-and-interstellar-delta-v-routes-3.md) | KSA-DeltaVMap | stock model reuse | `frame:draw-ui` | Same numbers as the stock planner; non-monotonic search; fuel from staged masses |

## See also

- [Estimate transfer delta-v in closed form with patched conics](../estimate-transfer-delta-v-with-patched-conics/overview.md)
- [Read orbit elements, apsides and event countdowns safely](../../flight-state/read-orbit-elements-and-timing/overview.md)
- [Compute phase angles, synodic periods and the time to the next transfer window](../compute-transfer-windows/overview.md)

[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-atmosphericbody]: KSA.AtmosphericBody (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-orbitaltransfers]: KSA.OrbitalTransfers (decompiled, build 2026.10.10.5554)
[^ksa-interstellardrive]: KSA.InterstellarDrive (decompiled, build 2026.10.10.5554)
[^ksa-interstellarburnplan]: KSA.InterstellarBurnPlan (decompiled, build 2026.10.10.5554)
