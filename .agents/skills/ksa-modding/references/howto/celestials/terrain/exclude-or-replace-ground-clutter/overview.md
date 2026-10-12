---
type: Game Feature
title: Keep grass, rocks and trees off a custom site, and replace or preserve a planet's ground clutter
description: Refill the four launch-pad exclusion zones the engine bakes at load, swap a body's clutter types for imported models at the solver handoff, and carry knocked-out or displaced instances across swaps and saves
category: celestials
group: terrain
topic: exclude-or-replace-ground-clutter
demonstrated_by:
  - mods/ksastructures/features/ground-clutter-exclusion-zones
  - mods/unscience/features/pebbles-ground-clutter-replacement
  - mods/unscience/features/pebbles-private-clutter-materials
  - mods/unscience/features/pebbles-clutter-state-persistence
lifecycle:
  - startup:all-mods-loaded
  - startup:game-loaded
  - frame:dispatch-solvers
  - frame:draw-ui
  - event:renderer-rebuild
  - event:load
  - event:save
game_types:
  - KSA.GroundClutterPlacementData
  - KSA.ClutterCubeCellGrid
  - KSA.ClutterEcotypeRenderData
  - KSA.ClutterEcotypePhysicalData
  - KSA.GroundClutterRenderer
  - KSA.PlanetRenderer
  - KSA.LandmarkReference
  - KSA.StaticObject
  - KSA.Celestial
  - KSA.Camera
  - KSA.Universe
  - KSA.GroundClutterReference
  - KSA.ClutterEcotypeReference
  - KSA.ClutterObjectTemplate
  - KSA.GroundClutterLodReference
  - KSA.PhysicsBubble
  - KSA.BubbleClutterStatics
  - KSA.CelestialTemplate
  - KSA.GameSave
tags: [ground-clutter, exclusion, ecotype, launch-pad, harmony, save]
---
Ground clutter (grass, rocks, trees, pebbles) is placed by the engine from distribution textures and biome masks, drawn by `GroundClutterRenderer` and collided by `ClutterEcotypePhysicalData`. A mod usually wants one of three things: keep clutter off a custom site (the engine only clears it around a few launch pads), replace the models of chosen clutter types on a body, or make sure removals the player caused (since KSA 5447, hitting a rock knocks it loose as a dynamic body) are not forgotten when it swaps those models.

## Engine background

- Clutter is organised as a per-body `GroundClutterReference` of ecotypes, each with objects, LODs and a placement. `GroundClutterPlacementData` is the CPU placement, `ClutterEcotypeRenderData` and `ClutterEcotypePhysicalData` the render and collision halves, all held per body in `GroundClutterRenderer` ([current/decomp/KSA/GroundClutterRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterRenderer.cs#L1799)).
- Exclusion zones: `GroundClutterPlacementData.BuildEcotypeData` calls the private `PopulateLaunchPadExclusionZones`, which walks `Locations` and writes the first **four** launch-pad `LandmarkReference`s whose static object has a positive `FootprintRadius` as `float4(ForwardCcf, (FootprintRadius + 50 m) / MeanRadius)` ([current/decomp/KSA/GroundClutterPlacementData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterPlacementData.cs#L338-L376)). They are baked into each ecotype's uniform data when the renderer builds ([current/decomp/KSA/ClutterEcotypeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ClutterEcotypeRenderData.cs)).
- Removals: each cell has an `ExclusionData` of 256 subcell bits ([current/decomp/KSA/GroundClutterRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterRenderer.cs#L165-L175)); `ExcludeCell` and `GetExclusionData` read and write them, `QueueExclusionUpload` pushes them to the GPU, and displaced instances are `DisplacedObject` records added with `AddDisplacedObject` or `DeserializeDisplacedObject` ([current/decomp/KSA/GroundClutterPlacementData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterPlacementData.cs#L122-L160)). `Universe.SyncGroundClutter` drains each bubble's pending exclusions and calls `PhysicsBubble.SyncGroundClutterStatics` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2157-L2200)).
- `Universe.ExecuteNextClothSolvers` has one caller, `Program.PrepareFrame`, and `ExecuteNextVehicleSolvers` runs `SyncGroundClutter` just before kicking the vehicle task ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2114-L2135)).
- Renderer rebuild: `PlanetRenderer.RebuildGroundClutterResources` creates a new `GroundClutterRenderer` ([current/decomp/KSA/PlanetRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetRenderer.cs#L1725)).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: Harmony postfix on PopulateLaunchPadExclusionZones](exclude-or-replace-ground-clutter-1.md) | ksastructures | `harmony-postfix` | `startup:all-mods-loaded`, `startup:game-loaded` | Four zones near the load-time camera only; zones are baked once |
| [2: prefix on ExecuteNextClothSolvers builds private bundles](exclude-or-replace-ground-clutter-2.md) | unscience | `harmony-prefix` | `frame:dispatch-solvers`, `event:renderer-rebuild` | Whole-body clutter replacement with exact restore; heavy, many private types |
| [3: per-grid memory, public-API snapshot and replay, native plus sidecar saves](exclude-or-replace-ground-clutter-3.md) | unscience | `save-sidecar` | `frame:dispatch-solvers`, `event:save`, `event:load` | Nothing knocked out is resurrected; needs a sidecar for override-only grids |

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Place a static object on a planet surface](../../../celestials/terrain/place-static-objects-on-a-planet-surface/overview.md), [Add terrain decals and dig a site](../../../celestials/terrain/add-terrain-decals-and-dig-a-site/overview.md)
- [Patch a private game method by name](../../../patching/internals/patch-a-private-game-method-by-name/overview.md), [Give ground clutter custom colliders](../../../physics/colliders/give-ground-clutter-custom-colliders/overview.md)
- [Change loaded game data and make the renderer notice](../../../celestials/atmospheres-and-rings/change-loaded-game-data-and-make-the-renderer-notice/overview.md), [Handle renderer rebuilds](../../../rendering/vulkan-passes/handle-renderer-rebuilds/overview.md)
- [Mutate vehicles at a physics-safe point](../../../physics/solver/mutate-vehicles-at-a-physics-safe-point/overview.md), [Detect save and load](../../../persistence/saves/detect-save-load/overview.md), [Save mod state with the scene](../../../persistence/saves/save-mod-state-with-the-scene/overview.md)
- [Place a static object on a planet surface at a latitude and longitude, and keep it anchored](../place-static-objects-on-a-planet-surface/overview.md) - related topic
- [Add terrain decals from code and flatten or dig the ground under a custom site](../add-terrain-decals-and-dig-a-site/overview.md) - related topic
- [Change loaded game data at runtime and make the renderer rebuild from it](../../atmospheres-and-rings/change-loaded-game-data-and-make-the-renderer-notice/overview.md) - related topic

