---
type: Game Feature
title: Branch per body inside shared game shaders
description: One stock shader draws every body, so per-body behaviour has to be keyed on values the shader can already see, such as the body radius plus an XML-written number, or a point's own height
category: shaders
group: stock-shaders
topic: branch-per-body-inside-shared-game-shaders
demonstrated_by:
  - mods/realatmospheres/features/measured-density-tables-in-shaders
  - mods/realatmospheres/features/measured-atmosphere-generator
  - mods/realatmospheres/features/relief-softened-terminator
lifecycle: [startup:starmap-before-main, render:atmosphere, render:planet]
game_types:
  - KSA.AtmosphereRenderer
  - KSA.Atmosphere.Rendering.ShaderStructs.AtmosphereData
  - KSA.PlanetRenderer
  - KSA.MieScatteringReference
tags: [glsl, per-body, atmosphere, terminator, lookup-table, generated-code, stock-shaders, realatmospheres]
---
KSA draws the atmosphere of every body, and the terrain of every body, with the same shaders. A mod that wants Pluto to look different from Earth cannot add a second shader; it has to branch inside the shared one on something the shader can already see.
This topic shows the two keys that work in practice: the body radius together with a value only the mod's XML writes, and a fragment's own radius together with the body's relief. It also covers generating the per-body numbers offline from one script so the renderer
and the flight model agree.

## Engine background

- **What a shader knows about the body.** The atmosphere passes read `AtmosphereData` (GLSL `struct AtmosphereData` in [current/Content/Core/Shaders/Atmosphere/AtmosphereData.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/AtmosphereData.glsl#L4-L17)): `bottomRadius`, the Rayleigh and Mie scale heights (in `.w`), `ozoneExtent`,
  `miePhaseFunctionAsymmetry`. The host fills it per body in `AtmosphereRenderer.FillPlanetAtmosphereData` with `BottomRadius = MeanRadius + StartHeight` and scale heights in metres ([current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L1136-L1164)).
- **Planet terrain shaders** read the `Planet` uniform: `radius`, `heightScale` (the body's maximum surface height, [current/decomp/KSA/PlanetRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetRenderer.cs#L724-L730)) and `celestialIndex` ([current/Content/Core/Shaders/Common/Planet.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/Planet.glsl#L44-L50)), plus the `Global` block's
  `celestial.bodies[i]` (centre and radius) and `bodyCount` ([current/Content/Core/Shaders/Common/Global.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/Global.glsl#L77-L78)).
- **When the atmosphere compute passes run.** The transmittance, multiple-scattering and ambient LUTs are computed once per body and kept while `_planetLutsValid[slot]` is true, so a per-body change that affects them must go through the same function in every pass.

## Choosing a key

| Key | Reliability | Used for |
|---|---|---|
| Body radius alone | Several bodies share round radii; mods that edit the body break it | Only as a first filter |
| Radius within 20 km plus an exact scale height the mod wrote into the body's XML | Turns itself off if anyone else edits that atmosphere | Measured density tables for ten bodies |
| Sentinel values in an XML parameter | Needs values outside the physical range | See [Smuggle data into stock shaders through unused uniforms, state bits and sentinel values](../send-extra-data-to-stock-shaders-without-new-uniforms/overview.md) |
| `uboPlanet.celestialIndex` bounded by `bodyCount` | Index into the global body list | Terrain and clutter shaders |
| A point's own radius and the body's `heightScale` | Always available in the planet shaders | Relief-aware shading |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Measured ln-density tables recognised by radius and an exact scale height](branch-per-body-inside-shared-game-shaders-1.md) | RealAtmospheres | `glsl-shader` | `startup:starmap-before-main`, `render:atmosphere` | One injected function replaces six exponential sites in four shaders; tables and flight data come from one generator |
| [Relief-aware terminator closing over a sqrt(2 relief / R) band](branch-per-body-inside-shared-game-shaders-2.md) | RealAtmospheres | `glsl-shader` | `startup:starmap-before-main`, `render:planet` | Keeps other bodies' eclipses; a pure formula, but two shaders must agree |

## See also

- [Measured density tables in the atmosphere shaders](../../../../mods/realatmospheres/features/measured-density-tables-in-shaders.md), [Measured-atmosphere code generator](../../../../mods/realatmospheres/features/measured-atmosphere-generator.md), [Relief-softened terminator](../../../../mods/realatmospheres/features/relief-softened-terminator.md)
- [Smuggle data into stock shaders through unused uniforms, state bits and sentinel values](../send-extra-data-to-stock-shaders-without-new-uniforms/overview.md), [Extend KSA's sky and cloud shaders: per-pixel haze and cloud lighting fixes](../extend-stock-sky-and-cloud-shaders/overview.md), [Patch vehicle paint and canopy shaders in memory at compile time](../patch-game-shaders-in-memory-at-compile-time/overview.md), [Compile-check a mod's GLSL without launching the game](../../glsl-workflow/check-mod-shaders-offline-with-glslang/overview.md), [Replace or text-patch the game's GLSL without editing game files](../replace-or-patch-game-glsl-without-editing-game-files/overview.md)
