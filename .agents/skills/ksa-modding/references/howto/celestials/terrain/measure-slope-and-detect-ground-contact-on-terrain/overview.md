---
type: Game Feature
title: Measure ground slope and surface normal, and detect when a mod-simulated point hits the ground
description: Derive the local gradient or surface normal from neighbouring height samples and test ground contact for bodies the engine does not simulate, with interpolation and epoch corrections
category: celestials
group: terrain
topic: measure-slope-and-detect-ground-contact-on-terrain
demonstrated_by:
  - mods/ksagolf/features/terrain-height-queries
  - mods/unscience/features/sphinx-glb-statics-placement
  - mods/ksarmory/features/terrain-ground-impact-test
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types:
  - KSA.Celestial
  - KSA.Astronomical
  - KSA.CelestialSystem
  - KSA.Universe
  - KSA.IParentBody
  - KSA.PhysicsEnvironment
  - KSA.TerrainImpactFinder
tags: [terrain, slope, normal, gradient, ground-contact, sub-step]
---
Placing a model flush on a hill, rolling a ball or stopping a round needs the local gradient and a contact test. The engine gives a mod exactly one terrain function, `Celestial.GetTerrainHeightFromDirCcf` and its `Cce` and `Cci` siblings (see [the height-query topic](../query-terrain-height-and-line-of-sight/overview.md)), and no normal or contact query for bodies it does not simulate. Both are built from height samples: a gradient from centred differences or a normal from a cross product of chords, and a contact test that compares `Len(position - centre)` against a per-frame surface radius at each sub-step.

## Engine background

- The only terrain source is `GetTerrainHeightFromDirCcf` / `Cce` ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L777-L795)); there is no normal accessor. Vehicles stand on the engine's own terrain patches (`TerrainPatch`, 2 m quads, [current/decomp/KSA/TerrainPatch.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TerrainPatch.cs#L9)) via `TerrainRadiusCache` ([current/decomp/KSA/TerrainRadiusCache.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TerrainRadiusCache.cs#L22-L38)).
- Sampling scale: the base height grid is kilometres, the detail textures metres, and the direction is packed to single precision before the modifier loop, so sub-third-of-a-metre offsets read as a staircase. Choose the sampling distance for the scale you care about.
- `TerrainImpactFinder` is the engine's own impact predictor and samples `accurate: false` ([current/decomp/KSA/TerrainImpactFinder.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TerrainImpactFinder.cs#L64)).
- Bodies move: `Celestial.UpdatePerFrameData` fixes the frame's centre and `Ccf`-to-`Cce` rotation at the end of the applied physics step ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L591-L606)). A contact test over sub-steps must correct for the body having moved and rotated since ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2701) is where planet streaming runs, `frame:celestials`).
- Frame vocabulary: [coordinate frames and epochs](../../../flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md), [game loop](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: gradient from centred east/west/north/south differences](measure-slope-and-detect-ground-contact-on-terrain-1.md) | ksagolf | `closed-form-geometry` | `frame:draw-ui` | Right slope magnitude and an aim-loop gain; needs a local east/north frame |
| [2: normal from four 1 m samples and a cross product](measure-slope-and-detect-ground-contact-on-terrain-2.md) | unscience | `glb-model` | `frame:draw-ui`, `frame:render` | Direct unit normal for aligning a model; four extra queries per placement |
| [3: per-frame centre and radius, crossing test, interpolation, resample band](measure-slope-and-detect-ground-contact-on-terrain-3.md) | ksarmory | `custom-physics` | `frame:draw-ui` (hidden HUD: `frame:celestials`, then `frame:after-on-frame`) | Contact accurate to millimetres for fast bodies; heaviest to implement |

## See also

- [Query the terrain height field and test line of sight](../../../celestials/terrain/query-terrain-height-and-line-of-sight/overview.md)
- [Place a static object on a planet surface](../../../celestials/terrain/place-static-objects-on-a-planet-surface/overview.md), [Build a site frame and compute a launch window](../../../flight-and-orbits/transfers/build-a-site-frame-and-compute-a-launch-window/overview.md)
- [Prevent tunnelling of fast bodies](../../../physics/mod-simulation/prevent-tunnelling-of-fast-bodies/overview.md), [Simulate bodies outside the vehicle system](../../../physics/mod-simulation/simulate-bodies-outside-the-vehicle-system/overview.md)
- [Understand KSA coordinate frames and epochs](../../../flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md)
- [Place a static object on a planet surface at a latitude and longitude, and keep it anchored](../place-static-objects-on-a-planet-surface/overview.md) - related topic

