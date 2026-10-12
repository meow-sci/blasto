---
type: Game Feature
title: Add terrain decals from code and flatten or dig the ground under a custom site
description: Add DecalModifierReference terrain modifiers to a body at load so the ground is drawn, collided with, queried and cluttered consistently, and compose a levelling decal with a height-map pit that meets the stock launch pad
category: celestials
group: terrain
topic: add-terrain-decals-and-dig-a-site
demonstrated_by: [mods/ksastructures/features/terrain-decal-pits, mods/ksastructures/features/generated-deep-ramp]
lifecycle: [startup:all-mods-loaded]
game_types:
  - KSA.DecalModifierReference
  - KSA.ModifierReference
  - KSA.TerrainReference
  - KSA.ProceduralModifiersReference
  - KSA.ProceduralModifiersRenderData
  - KSA.DistanceReference
  - KSA.Celestial
  - KSA.CelestialTemplate
  - KSA.PlanetRenderer
  - KSA.PlanetMeshLibrary
  - KSA.GroundClutterRenderer
  - KSA.StaticObjectModel
  - KSA.PartModelModule
tags: [terrain, decal, modifier, pit, levelling, xml]
---
Terrain modifiers are XML data that shape a planet's height field. A `Decal` modifier places a circle of a given radius at a latitude and longitude on a body and either lerps the ground toward an absolute altitude (a levelling decal) or adds a height-map relief (a pit or a bump). The stock game uses them for its launch pads. A mod can build the same objects in code and add them to a body's `TerrainReference` while the game is loading, so the ground that is drawn, collided with, queried by `Celestial.GetTerrainHeightFromDirCcf` and populated with clutter all follow the change.

## Engine background

- `DecalModifierReference` (XML `Type="Decal"`) declares `Location`, `Rotation`, `Radius`, `AltitudeOffset`, `SmoothFactor`, `Additive`, `NoCurvature` and `HeightMap` as `XmlElement`s; `OnDataLoad(Mod)` resolves them and adds the height map to its texture list; `Evaluate` is the CPU height function ([current/decomp/KSA/DecalModifierReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DecalModifierReference.cs#L9-L37), [current/decomp/KSA/DecalModifierReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DecalModifierReference.cs#L154-L172), [current/decomp/KSA/DecalModifierReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DecalModifierReference.cs#L185-L227)).
- `TerrainReference.ProcessModifiers` copies `ProceduralModifiers.Modifiers` into `TerrainModifiers` and sorts by `Order`; it has already run by the time a mod starts ([current/decomp/KSA/TerrainReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TerrainReference.cs#L47-L51)).
- `ProceduralModifiersRenderData` takes its modifier count from `ProceduralModifiersReference.ModifierCount` (the `Modifiers` list's count, [current/decomp/KSA/ProceduralModifiersReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ProceduralModifiersReference.cs#L21)) and sizes its curve lookup and storage from it ([current/decomp/KSA/ProceduralModifiersRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ProceduralModifiersRenderData.cs#L228-L234)).
- `Celestial.GetTerrainHeightFromDirCcf` loops over `TerrainModifiers` and calls `Evaluate` only when `accurate` is true ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L789-L881)); see [the height-query topic](../query-terrain-height-and-line-of-sight/overview.md).
- The render side is built once in the `Program` constructor, after `ModLibrary.LoadAll` at [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L942): `PlanetMeshLibrary.Create` at [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1148) and `new PlanetRenderer` at [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1158). The per-body modifier data (`Celestial.TerrainModifiersRenderData`, a `ProceduralModifiersRenderData`) is created inside `PlanetMeshLibrary.Create` by `Universe.SetupRenderData` -> `Astronomical.SetupModifierRenderData`, after `Universe.LoadDefaultSystem` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2404-L2427), [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L446-L460)); its curve lookup is sized from the count at construction. The only later rebuild is the engine's terrain debug panel, which re-runs `UpdateModifierCount` and `SetupCelestialTerrainMods` after an edit to an existing modifier's parameters ([current/decomp/KSA/PlanetRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetRenderer.cs#L2284-L2314)); nothing re-sizes the data for an added modifier.
- Stock example: the LC-39A levelling decal (`Order 9999`, `Radius 275`, `AltitudeOffset Km="16.97"`, `Additive false`) in [current/Content/Core/Astronomicals.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Astronomicals.xml#L734-L750).
- A `PartModel` with `<Terrain>true</Terrain>` routes to the `OpaqueTerrain` draw bucket so it shades like ground ([current/decomp/KSA/PartModelModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModelModule.cs#L41-L43), [current/decomp/KSA/StaticObjectModel.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObjectModel.cs#L260)).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: XmlSerializer into DecalModifierReference, both lists, re-sort](add-terrain-decals-and-dig-a-site-1.md) | ksastructures | `terrain-modifier` | `startup:all-mods-loaded` | Uses the engine's own XML shape; load-time only, cannot toggle live |
| [2: levelling decal plus additive pit height map, ordered after the pad decal](add-terrain-decals-and-dig-a-site-2.md) | ksastructures | `terrain-modifier` | `startup:all-mods-loaded` | Seam-free site; each decal costs one of 16 texture slots |

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Query the terrain height field and test line of sight](../../../celestials/terrain/query-terrain-height-and-line-of-sight/overview.md), [Place a static object on a planet surface](../../../celestials/terrain/place-static-objects-on-a-planet-surface/overview.md)
- [Exclude or replace ground clutter](../../../celestials/terrain/exclude-or-replace-ground-clutter/overview.md), [Change loaded game data and make the renderer notice](../../../celestials/atmospheres-and-rings/change-loaded-game-data-and-make-the-renderer-notice/overview.md)
- [Project decals onto scene geometry](../../../rendering/textures-and-decals/project-decals-onto-scene-geometry/overview.md), [Redirect game XML loading](../../../assets/xml-patching/redirect-game-xml-loading/overview.md)
- [Keep grass, rocks and trees off a custom site, and replace or preserve a planet's ground clutter](../exclude-or-replace-ground-clutter/overview.md) - related topic
- [Change loaded game data at runtime and make the renderer rebuild from it](../../atmospheres-and-rings/change-loaded-game-data-and-make-the-renderer-notice/overview.md) - related topic
- [Place a static object on a planet surface at a latitude and longitude, and keep it anchored](../place-static-objects-on-a-planet-surface/overview.md) - related topic

