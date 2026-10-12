---
type: Game Feature
title: Query the terrain height field and test line of sight across terrain
description: Ask KSA how high the ground is along a direction from a body centre, choose between the Ccf and Cce entry points and between fast and accurate sampling, and build sea clamps, ground-contact tests, hover height and line of sight on that one call
category: celestials
group: terrain
topic: query-terrain-height-and-line-of-sight
demonstrated_by:
  - mods/ksagolf/features/terrain-height-queries
  - mods/ksarmory/features/terrain-ground-impact-test
  - mods/ksa-advancedflightcomputer/features/terminal-hover-touchdown
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame, frame:dispatch-solvers]
game_types:
  - KSA.Celestial
  - KSA.Astronomical
  - KSA.CelestialSystem
  - KSA.Universe
  - KSA.Rendering.Water.Data.OceanReference
  - KSA.TerrainRadiusCache
  - KSA.PhysicsEnvironment
  - KSA.TerrainPatch
  - KSA.TerrainImpactFinder
  - KSA.ProceduralModifiersReference
  - KSA.PhysicsStates
tags: [terrain, height-field, ground, ocean, line-of-sight, accurate, ccf, cce]
---
KSA has no terrain raycast and no collider query a mod can call. What it has is one function,
`Celestial.GetTerrainHeightFromDirCcf(direction, accurate)`, which returns the terrain height in
metres above `MeanRadius` along a direction from the body's centre. `GetTerrainHeightFromDirCce`
and `GetTerrainHeightFromDirCci` rotate a direction into the body-fixed frame and call it. Everything
a mod wants from the ground (a ball that rolls, a round that bursts, a hover height, a radar horizon)
is built on that call. The topic covers the entry points, the sea clamp, what `accurate` changes,
how to keep queries affordable and how to turn height samples into a line-of-sight test.

## Engine background

- Entry points: `Celestial.GetTerrainHeightFromDirCce` (L777), `GetTerrainHeightFromDirCci` (L783) and `GetTerrainHeightFromDirCcf` (L789) in [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L777-L795). The `Cce` and `Cci` forms multiply by `GetCcf2Cce().Inverse()` or `GetCcf2Cci().Inverse()`; `Cce` shares its axes with the ecliptic (`Ecl`) and only moves the origin to the body centre, so `position - centre` in `Ecl` is a valid `Cce` position.
- The `Ccf` form returns 0 for a body with no `HasTerrainHeightmap`, interpolates the height cubemap (bicubic when `accurate`, bilinear otherwise) and then, only when the body has a normal cubemap and biome materials, walks the terrain modifier list and calls `ModifierReference.Evaluate` **only when `accurate` is true** ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L789-L881)).
- Modifier data is built once at load: the modifier loop is bounded by `TerrainModifiersRenderData?.NumModifiers`, and that object is created by `Astronomical.SetupModifierRenderData` from `Universe.SetupRenderData` ([current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L446-L460), [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2404-L2427)), which `PlanetMeshLibrary.Create` runs in the `Program` constructor after `Universe.LoadDefaultSystem` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1145-L1149)). Before that (including `Universe.OnLoaded`) no modifier is applied, and later edits to a body's modifier list are never seen by the query.
- Per-frame rotation: `Celestial.UpdatePerFrameData` refreshes the cached `Ccf`-to-`Cce` rotation ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L591-L606)); `Universe.ApplyVehicleSolvers` calls `CurrentSystem.UpdatePerFrameData()` in `PrepareFrame` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1951)), so every mod hook in one frame sees the same planet phase.
- The engine's own users: `PhysicsEnvironment.RecomputePositionalValues` sets `TerrainRadius` through `TerrainRadiusCache.ComputeRadiusCcf(celestial, dir, 0.25)` ([current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L109), [current/decomp/KSA/TerrainRadiusCache.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TerrainRadiusCache.cs#L22-L38)), which calls the height query with the default `accurate: true` and reuses a sample within 0.25 m of arc; the collision patch grid is 2 m ([current/decomp/KSA/TerrainPatch.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TerrainPatch.cs#L9)); `TerrainImpactFinder` samples `accurate: false` ([current/decomp/KSA/TerrainImpactFinder.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TerrainImpactFinder.cs#L64)).
- Vehicle altitude: `Vehicle.GetRadarAltitude` already combines the `Cci` height query with the ocean height ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3060-L3088)); `Vehicle.GetBarometricAltitude` is just `PositionCci.Length() - MeanRadius`.
- Height ceilings: `Astronomical.MaxTerrainRadius` is `MeanRadius + HeightReference.Maximum` ([current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L114-L122)); `Celestial.MaxTerrainHeightApprox` is a field filled in the constructor by `UpdateApproxTerrainAltitudes`, a 16,384-direction Fibonacci sample taken before the modifiers exist ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L929-L982)).
- Frames and epochs: see the [coordinate frames reference](../../../flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md) and the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: Ccf versus Cce, sea clamp, no-claim results, accurate flag, line of sight](query-terrain-height-and-line-of-sight-1.md) | ksagolf | `custom-physics` | `frame:draw-ui` (hidden HUD: `frame:celestials`) | Complete toolbox with sphere test plus fixed-budget band sampling; most code to carry |
| [2: centre and surface radius, nearest body by depth, radar horizon mask](query-terrain-height-and-line-of-sight-2.md) | ksarmory | `terrain-query` | `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame` | One sample per frame serves every sub-step; the surface is a sphere for the frame |
| [3: cache decorator reusing a sample until near the ground or moved](query-terrain-height-and-line-of-sight-3.md) | ksarmory | `decorator-cache` | `frame:draw-ui` | Cuts lookups by orders of magnitude for trajectory previews; stale if the thresholds are wrong |
| [4: height under the craft for hover and touchdown](query-terrain-height-and-line-of-sight-4.md) | ksa-advancedflightcomputer | `harmony-prefix` | `frame:dispatch-solvers` | Smallest possible use (Ccf plus a finite check); default `accurate: true` |

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Understand KSA coordinate frames and epochs](../../../flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md)
- [Measure ground slope and detect ground contact](../../../celestials/terrain/measure-slope-and-detect-ground-contact-on-terrain/overview.md), [Add terrain decals and dig a site](../../../celestials/terrain/add-terrain-decals-and-dig-a-site/overview.md), [Place static objects on a planet surface](../../../celestials/terrain/place-static-objects-on-a-planet-surface/overview.md)
- [Override terrain height for roofs and platforms](../../../physics/aero-and-ground/override-terrain-height-for-roofs-and-platforms/overview.md), [Pick objects under the cursor](../../../ui/input/pick-objects-under-the-cursor/overview.md), [Sample the ground under each wheel](../../../physics/aero-and-ground/sample-the-ground-under-each-wheel/overview.md)
- [Simulate bodies outside the vehicle system](../../../physics/mod-simulation/simulate-bodies-outside-the-vehicle-system/overview.md), Steer by zero effort miss
- [Measure ground slope and surface normal, and detect when a mod-simulated point hits the ground](../measure-slope-and-detect-ground-contact-on-terrain/overview.md) - related topic
- [Add terrain decals from code and flatten or dig the ground under a custom site](../add-terrain-decals-and-dig-a-site/overview.md) - related topic
- [Place a static object on a planet surface at a latitude and longitude, and keep it anchored](../place-static-objects-on-a-planet-surface/overview.md) - related topic
- [Steer by predicted miss (zero-effort-miss guidance) and model homing sensors](../../../flight-and-orbits/guidance/steer-by-zero-effort-miss/overview.md) - related topic

