---
type: Game Feature
title: Change loaded game data at runtime and make the renderer rebuild from it
description: Many KSA renderers bake XML-backed reference data into GPU buffers in their constructors, so editing the reference object does nothing until the renderer is recreated; four patterns for mutate-and-recreate, transactional swap, template assignment and in-place buffer writes
category: celestials
group: atmospheres-and-rings
topic: change-loaded-game-data-and-make-the-renderer-notice
demonstrated_by:
  - mods/unscience/features/rocky-mcrock-face-ring-asset-swap
  - mods/unscience/features/pebbles-ground-clutter-replacement
  - mods/unscience/features/bloomin-onion-runtime-planetary-rings
  - mods/ksastructures/features/live-terrain-material-override
lifecycle:
  - frame:prepare
  - frame:dispatch-solvers
  - frame:draw-ui
  - frame:celestials
  - frame:after-on-frame
  - render:scene-opaque
game_types:
  - KSA.Program
  - KSA.PlanetTransparenciesRenderer
  - KSA.GroundClutterRenderer
  - KSA.PlanetRenderer
  - KSA.AstronomicalTemplate
  - KSA.BiomeMaterialReference
  - KSA.BiomeMaterialsReference
  - KSA.TerrainReference
  - KSA.Celestial
  - KSA.Camera
tags: [renderer-rebuild, reflection, baked-data, template, biome-materials, mapped-buffer]
---
KSA loads its content as XML-backed reference objects, then builds renderers that copy those objects into GPU buffers, descriptor sets and instance data in their **constructors**. The reference objects stay public and mutable, so a mod can edit them, but the editing does nothing visible until the renderer that baked the old values is recreated, rebuilt or told to re-read. This topic compares four ways to make a change land, from least to most invasive.

## Engine background

- `Program.RebuildRenderer(bool skipSwapchain = false)` rebuilds render targets and calls each renderer's `RebuildFrameResources` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5105)). The game triggers it through `Program.RendererRebuildNeeded` (read at the top of `PrepareFrame`, in `PostRender` and in `Render`: [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2143-L2147), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2351-L2358), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2445-L2450)).
- A renderer's `RebuildFrameResources` usually rebuilds pipelines and frame images only. `PlanetTransparenciesRenderer` calls its rings renderer's rebuild only when `_ringRendererCreated` is true ([current/decomp/KSA/PlanetTransparenciesRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetTransparenciesRenderer.cs#L374-L430)); `PlanetRenderer.RebuildFrameResources` rebuilds its pipelines and calls `RebuildGroundClutterResources`, which re-creates or only rebuilds the `GroundClutterRenderer` depending on `_groundClutterRendererCreated` ([current/decomp/KSA/PlanetRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetRenderer.cs#L1716-L1740)).
- The planet renderer allocates one persistently mapped, host-coherent storage buffer for every body's biome materials (`_biomeMaterialsMap`, offsets in `_biomeMaterialSlot`), writes it once, and its own debug editor writes the same memory in place ([current/decomp/KSA/PlanetRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetRenderer.cs#L664-L705), [current/decomp/KSA/PlanetRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetRenderer.cs#L2233-L2248)).
- Template data lives on `AstronomicalTemplate` (`AtmosphereReference`, `CloudsReference`, `RingsReference`, `TerrainReference`, `GroundClutterReference`) and is read by renderers at construction ([current/decomp/KSA/AstronomicalTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AstronomicalTemplate.cs#L50-L70)).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: snapshot, mutate the public tree, recreate one renderer by reflection](change-loaded-game-data-and-make-the-renderer-notice-1.md) | unscience | `reflection` | `frame:draw-ui`, `frame:prepare` | Scoped to one renderer; needs private flags |
| [2: transactional apply and exact restore of a body's clutter](change-loaded-game-data-and-make-the-renderer-notice-2.md) | unscience | `harmony-prefix` | `frame:dispatch-solvers`, `event:renderer-rebuild` | Safe against workers; many private types |
| [3: assign to the template and call Program.RebuildRenderer](change-loaded-game-data-and-make-the-renderer-notice-3.md) | unscience | `reflection` | `frame:draw-ui` | The heavy hammer: simple, a short hitch |
| [4: reflect the mapped buffer and slot, clamp when near, restore on leaving](change-loaded-game-data-and-make-the-renderer-notice-4.md) | ksastructures | `reflection` | `frame:draw-ui`, `frame:celestials`, `render:scene-opaque` | No rebuild at all; planet-wide value and private fields |

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Handle renderer rebuilds](../../../rendering/vulkan-passes/handle-renderer-rebuilds/overview.md), [Add or restyle planetary rings](../../../celestials/atmospheres-and-rings/add-or-restyle-planetary-rings/overview.md), [Exclude or replace ground clutter](../../../celestials/terrain/exclude-or-replace-ground-clutter/overview.md)
- [Patch game shaders in memory at compile time](../../../shaders/stock-shaders/patch-game-shaders-in-memory-at-compile-time/overview.md), [Add terrain decals and dig a site](../../../celestials/terrain/add-terrain-decals-and-dig-a-site/overview.md), [Draw exhaust plumes without an engine](../../../rendering/lights-and-particles/draw-exhaust-plumes-without-an-engine/overview.md)
- [Add planetary rings to a body or restyle existing rings at runtime](../add-or-restyle-planetary-rings/overview.md) - related topic
- [Keep grass, rocks and trees off a custom site, and replace or preserve a planet's ground clutter](../../terrain/exclude-or-replace-ground-clutter/overview.md) - related topic
- [Add terrain decals from code and flatten or dig the ground under a custom site](../../terrain/add-terrain-decals-and-dig-a-site/overview.md) - related topic

