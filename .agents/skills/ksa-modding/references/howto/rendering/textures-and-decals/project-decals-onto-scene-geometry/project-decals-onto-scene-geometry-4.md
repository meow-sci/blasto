---
type: How-To
title: Engine terrain DecalModifier deserialized from XML in code
description: Dig pits into a planet's terrain by deserializing Decal modifier XML into DecalModifierReference objects and adding them to the terrain while the game is still loading, so rendering, colliders and height queries all follow
category: rendering
group: textures-and-decals
topic: project-decals-onto-scene-geometry
approach: 4
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksastructures/features/terrain-decal-pits]
techniques: [xml-asset]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded]
game_types: [KSA.DecalModifierReference, KSA.TerrainReference]
tags: [decal, terrain, modifier, xml, load-window, heightmap]
---

Outcome: pits and flattened pads in a planet's own terrain. This is not a render pass: terrain modifiers change the height function itself, so the ground the engine draws, collides vehicles against and reports heights from all follow the pit. The mod writes `Decal` modifier XML as a C# string, deserializes it with the engine's own XML shape, and adds the modifiers to the planet's `TerrainReference` while the game is still
loading. Because everything that consumes the modifiers is sized once at startup, the mod cannot be toggled in a running game. Demonstrated by KSAStructures ([Terrain decal pits dug at load](../../../../mods/ksastructures/features/terrain-decal-pits.md)). Prerequisites: StarMap.

## Background: how the game does it

`DecalModifierReference` [current/decomp/KSA/DecalModifierReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DecalModifierReference.cs#L9) declares `Location`, `Rotation`, `Radius`, `AltitudeOffset`, `SmoothFactor`, `Additive`, `NoCurvature` and `HeightMap` as `XmlElement`s. `TerrainReference` [current/decomp/KSA/TerrainReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TerrainReference.cs) holds `ProceduralModifiers` (whose `Modifiers` list sets the count) and `TerrainModifiers` (a copy sorted by `Order`, made by `ProcessModifiers()` when the XML loaded).
`ProceduralModifiersRenderData` takes `NumModifiers` from `ProceduralModifiers.ModifierCount` and then sizes a curve LUT and 16 modifier texture slots from it [current/decomp/KSA/ProceduralModifiersRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ProceduralModifiersRenderData.cs#L162-L172); it walks `TerrainModifiers` to build render parameters. The CPU height function `Celestial.GetTerrainHeightFromDirCcf` loops over `TerrainModifiers`, so collisions and height queries see the pit.

## Lifecycle and timing

| When | Hook or engine call | What |
|---|---|---|
| `startup:asset-load` | `ModLibrary.LoadAll` -> `TerrainReference.OnDataLoad` | Earth's own modifiers copied into `TerrainModifiers` and sorted (already done when the mod runs, hence the second list) |
| `startup:all-mods-loaded` | `[StarMapAllModsLoaded]` | Deserialize and insert the decals: after all templates are loaded, before any render data is built; the only window where the modifier count can still change |
| rest of the `Program` constructor | `PlanetMeshLibrary.Create` -> `Universe.SetupRenderData`; `new PlanetRenderer` | Render data, LUT and storage buffer sized from the count, once per session |

`ModLibrary.LoadAll` runs at Program.cs L942 and render targets and the planet renderer are built later in the constructor ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L942)). A decal added in flight is not uploaded by anything: the GPU keeps the old count while the CPU height function walks the new list, so heights, collision and rendering disagree. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Write the modifier as XML** in the engine's own schema (an absolute level decal to flatten the site, an additive height-map decal to dig):

   ```csharp
   // src/KSAStructures/Ksa/OfficeTerrain.cs L56-L69
     <Modifier Type="Decal" Name="KSAOffice_GaragePit">
       <Amplitude Value="12" />
       <Order Value="10001" />
       <Radius Value="66" />
       <Rotation Degrees="0" />
       <Location Id="KSAOffice-Garage">
         <Latitude Degrees="28.60868304" />
         <Longitude Degrees="-80.60575894" />
       </Location>
       <SmoothFactor Value="0.05" />
       <Additive Value="true" />
       <HeightMap Id="KSAOffice_GaragePit" Path="Textures/KSAOffice_GaragePit.png" Category="TerrainHeight" />
     </Modifier>
   </Modifiers>
   ```

2. **Deserialize and insert while loading:** `XmlSerializer(typeof(DecalModifierReference), new XmlRootAttribute("Modifier"))`, `OnDataLoad(Owner)` with the mod's `Mod` (so the height-map path resolves against the mod folder), `HeightMap.Load()`, add to **both** `ProceduralModifiers.Modifiers` and `TerrainModifiers`, then re-sort by `Order`. Skip names that already exist.

   ```csharp
   // src/KSAStructures/Ksa/OfficeTerrain.cs L113-L148
   // At load only: the planet's renderer sizes its buffers from the modifier count when it is
   // built, and one added afterwards overruns them. Into both lists, because the count is read off
   // one and the modifiers are walked off the other.
   private static void Dig(CelestialTemplate earth)
   {
       try
       {
           if (Owner is null || earth.TerrainReference is not { } terrain) { Log.Warn("office: no terrain to dig the garage in"); return; }

           var serializer = new XmlSerializer(typeof(DecalModifierReference), new XmlRootAttribute("Modifier"));
           var document = new System.Xml.XmlDocument();
           document.LoadXml(PitXml.Replace("</Modifiers>", DeepXml() + (VaultDepth > 0.0 ? VaultXml() : string.Empty) + "</Modifiers>"));

           foreach (System.Xml.XmlNode node in document.DocumentElement!.ChildNodes)
           {
               if (node is not System.Xml.XmlElement element) continue;

               using var reader = new System.Xml.XmlNodeReader(element);
               if (serializer.Deserialize(reader) is not DecalModifierReference decal) continue;
               if (terrain.TerrainModifiers.Exists(m => m.Name == decal.Name)) continue;

               decal.OnDataLoad(Owner);
               decal.HeightMap?.Load();
               terrain.ProceduralModifiers.Modifiers.Add(decal);
               terrain.TerrainModifiers.Add(decal);
           }

           terrain.TerrainModifiers.Sort((a, b) => a.Order.Value.CompareTo(b.Order));

           Log.Info($"office: garage pit dug, {terrain.TerrainModifiers.Count} terrain modifiers on Earth");
       }
       catch (Exception e)
       {
           Log.Warn($"office: could not dig the garage ({e.GetBaseException().Message})");
       }
   }
   ```

3. **Level first:** copy the pad's own flattening decal (Order just above it) so the mod's ground meets the pad without a step; give each decal a distinct `Order`.

4. **Cover what you dig:** pits render badly from a distance, so level the ground over everything dug and lay a cover mesh a few centimetres above it with `<Terrain>true</Terrain>` on its `PartModel` (the terrain draw bucket).

## Complete example

| File | Role |
|---|---|
| [src/KSAStructures/Ksa/OfficeTerrain.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeTerrain.cs) | XML strings, deserialize, `Dig` |
| [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml) | Part and cover-mesh XML |

## Variations and gotchas

- **Load window only:** add decals at `[StarMapAllModsLoaded]` (or earlier, after the target template exists).
- **Add to both lists and sort:** adding to `ProceduralModifiers.Modifiers` alone leaves the decal out of rendering and height queries; adding to `TerrainModifiers` alone leaves the count short.
- **`AltitudeOffset` is metres, written in the `Km` attribute;** writing `M` leaves `Km` as NaN while validation still passes, so the decal feeds NaN to both CPU and GPU.
- **Sixteen modifier textures per body,** shared by every mod that adds decals; each texture-requiring modifier takes one slot even when several name the same PNG.
- **Built against an earlier build;** all members above exist in the pinned build.

## See also

- [Overview](overview.md)
- [Approach 1: unit-cube decal volumes](project-decals-onto-scene-geometry-1.md)
- [Terrain decal pits dug at load](../../../../mods/ksastructures/features/terrain-decal-pits.md)
- [Choose a render frame and anchor effects to bodies, parts and terrain](../../overlays/choose-a-render-frame-and-anchor-effects/overview.md)
