---
type: Mod Feature
title: Terrain decal pits dug at load
description: Levels and digs Earth's terrain under the office by deserializing DecalModifierReference XML in code and inserting it into both of the Earth template's modifier lists at StarMapAllModsLoaded, before the terrain-modifier render data and the planet renderer's buffers are sized from the modifier count
mod: ksastructures
category: celestials
techniques: [starmap-lifecycle, xml-asset, terrain-modifier]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded]
game_types:
  - KSA.DecalModifierReference
  - KSA.ModifierReference
  - KSA.TerrainReference
  - KSA.ProceduralModifiersReference
  - KSA.ProceduralModifiersRenderData
  - KSA.DistanceReference
  - KSA.Astronomical
  - KSA.Celestial
  - KSA.CelestialTemplate
  - KSA.CelestialRenderData
  - KSA.PlanetRenderer
  - KSA.PlanetMeshLibrary
  - KSA.GroundClutterRenderer
  - KSA.PartModelModule
  - KSA.StaticObjectModel
tags: [terrain, heightmap, decal, procedural-modifiers, xml-serializer, load-order]
---

The office's garage, its deep spiral ramp and an optional vault lie in pits dug into Earth's own
terrain. The digging is done with the engine's terrain modifiers: the mod writes `Decal` modifier
XML as a C# string, deserializes it into `DecalModifierReference` objects, and adds them to Earth's
`TerrainReference` while the game is still loading. Because the terrain itself goes down, the ground
the engine draws, collides vehicles against and reports heights from all follow the pit.[^mod-office-xml]
This is also why the README says the mod cannot be switched on or off in a running game.[^mod-readme]

## How it works

1. **Four decals, written as XML in code.** `PitXml` holds two `<Modifier Type="Decal">` elements;
   `DeepXml()` and (if a vault is configured) `VaultXml()` are spliced in before `</Modifiers>`.[^mod-terrain]

   | Name | Order | Kind | Purpose |
   |---|---|---|---|
   | `KSAOffice_GarageLevel` | 10000 | absolute level (`Additive false`, `Amplitude 0`), radius 240 | Levels everything round the site to the pad's height, `AltitudeOffset` 16.97 |
   | `KSAOffice_GaragePit` | 10001 | additive height map (`Amplitude 12`), radius 66 | Digs the garage, ramp and tunnel from `Textures/KSAOffice_GaragePit.png` |
   | `KSAOffice_DeepLevel` | 10010 | absolute level, radius `OfficeDeep.PitRadius` | One circular pit under the deep shaft and hall, 54 m down |
   | `KSAOffice_VaultLevel` | 10020 | absolute level, radius 6.2 | Only when `office-vault.txt` beside the DLL holds a depth over 10 m |

   The levelling decal copies Core's own LC-39A flattening (Order 9999, `AltitudeOffset Km="16.97"`,
   radius 275), so the mod's ground meets the pad's without a step and its order sorts after it.[^ksa-core-astronomicals]
2. **Deserialized with the engine's own XML shape.** An `XmlSerializer(typeof(DecalModifierReference),
   new XmlRootAttribute("Modifier"))` reads each element; `DecalModifierReference` declares
   `Location`, `Rotation`, `Radius`, `AltitudeOffset`, `SmoothFactor`, `Additive` and `HeightMap`
   as `XmlElement`s.[^ksa-decal] Each decal then gets `OnDataLoad(Owner)` (the mod's `Mod`, so the
   texture path resolves against the mod folder; `Office.Owner` is set at `[StarMapImmediateLoad]`[^mod-entry]),
   `HeightMap.Load()`, and is added to **both** `terrain.ProceduralModifiers.Modifiers` and
   `terrain.TerrainModifiers`, which is then re-sorted by `Order`.[^mod-terrain]
3. **Why both lists.** `TerrainReference.ProcessModifiers` normally copies `ProceduralModifiers.Modifiers`
   into `TerrainModifiers` and sorts it, but that already ran when Earth's XML loaded.[^ksa-terrainreference]
   `ProceduralModifiersRenderData` takes its modifier **count** from
   `ProceduralModifiers.ModifierCount` (the `Modifiers` list's count) and **walks**
   `TerrainModifiers` to build render parameters, curve LUT rows and texture slots.[^ksa-procmodsdata][^ksa-procmodsref]
   The CPU height function `Celestial.GetTerrainHeightFromDirCcf` loops `i < NumModifiers` over
   `TerrainModifiers` too, so collision and height queries see the pit as well.[^ksa-celestial]
4. **Why only at load (and why the mod cannot be toggled live).** Everything that consumes the
   modifiers is sized once, during the `Program` constructor, after `[StarMapAllModsLoaded]`:
   - `PlanetMeshLibrary.Create` (Program line 1148) calls `Universe.SetupRenderData`, which calls
     `Astronomical.SetupModifierRenderData` for every billboarded body. That builds the body's
     `ProceduralModifiersRenderData` only if it does not exist yet; its constructor reads the count,
     allocates the curve LUT `NumModifiers * 4` texels wide and fixes 16 modifier texture slots.[^ksa-planetmeshlibrary][^ksa-universe][^ksa-astronomical][^ksa-procmodsdata]
   - `new PlanetRenderer` (Program line 1158) sizes one storage buffer of
     `ModifierRenderParams` for all bodies, giving each body `NumModifiers` entries at an offset
     recorded in `_proceduralModifierSlot`, and writes them once.[^ksa-planetrenderer][^ksa-program]
   - The ground clutter generator dispatches one compute pass per modifier up to `NumModifiers`, so
     clutter follows the dug terrain too.[^ksa-clutterrenderer]

   Nothing rebuilds these structures later. A decal added in flight is not uploaded by anything: the
   GPU keeps the old count and parameters, while the CPU height function walks the re-sorted
   `TerrainModifiers` with the old `NumModifiers`, so heights, collision and rendering disagree. The
   only live re-setup, KSA's own terrain editor in `PlanetRenderer.OnDrawUI`, re-runs
   `UpdateModifierCount` and `SetupCelestialTerrainMods` but keeps the LUT and the storage-buffer
   slot sized at startup, so a larger count would overrun the LUT row and write past the body's slot
   into the next body's.[^ksa-planetrenderer][^ksa-procmodsdata] `[StarMapAllModsLoaded]` runs after `ModLibrary.LoadAll` and before all of this
   (Program lines 942, 1148 and 1158), which is the window the mod uses.[^ksa-program] For the same
   reason the vault depth is read from a file at load rather than set in game.[^mod-lift]
5. **One texture slot per decal.** Each texture-requiring modifier consumes a slot of the 16, even
   when several decals name the same PNG, which is why the deep shaft and hall share one circular
   absolute-level decal rather than several.[^ksa-procmodsdata][^mod-terrain]
6. **Covers drawn as terrain.** Pits render badly from a distance, so the ground over everything dug
   is levelled first and a cover mesh lies a few centimetres above it: the `KSAOffice_GarageGround`
   and `KSAOffice_DeepGround` sub-objects set `<Terrain>true</Terrain>` on their `PartModel`.[^mod-office-xml]
   That puts them in `StaticObjectModel.DrawBucket.OpaqueTerrain`, whose pipeline compiles
   `StaticObjectFrag` with `SAMPLE_TERRAIN`.[^ksa-partmodelmodule][^ksa-staticobjectmodel][^ksa-staticobjectrenderer]
7. **Holding the rim down.** The pit's rim is drawn with the cliff material, whose displacement pokes
   through the cover near the camera. The mod clamps it at run time in the planet renderer's mapped
   material buffer; that is a separate mechanism with its own timing, described in
   [live terrain material override](./live-terrain-material-override.md).

## Lifecycle and timing

| When | Hook or engine call | What | Why |
|---|---|---|---|
| `startup:asset-load` | `ModLibrary.LoadAll` -> `TerrainReference.OnDataLoad` | Earth's own modifiers are copied into `TerrainModifiers` and sorted | Already done when the mod runs, hence the second list |
| `startup:all-mods-loaded` | `[StarMapAllModsLoaded]` -> `Office.Place` -> `Dig` | Deserialize and insert the decals | After all templates are loaded, before any `ProceduralModifiersRenderData` is built; the only window where the modifier count can still change |
| Rest of the `Program` constructor | `PlanetMeshLibrary.Create` -> `Universe.SetupRenderData`; `new PlanetRenderer` | Render data, LUT and storage buffer sized from the count | Built once per session |
| Every frame | `Celestial.GetTerrainHeightFromDirCcf` (CPU), the planet and clutter compute passes (GPU) | Heights, collisions, terrain mesh and clutter all include the decals | Same data, no further mod code |

`Dig` is idempotent per name (`terrain.TerrainModifiers.Exists(m => m.Name == decal.Name)`), and
`Place` returns early if the tower landmark already exists, so a second call does nothing.[^mod-office]

## Key code

| Path | Role |
|---|---|
| [src/KSAStructures/Ksa/OfficeTerrain.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeTerrain.cs#L36-L111) | The decal XML: level, pit, deep pit, vault |
| [src/KSAStructures/Ksa/OfficeTerrain.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeTerrain.cs#L113-L148) | `Dig`: deserialize and insert into both lists |
| [src/KSAStructures/Ksa/OfficeTerrain.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeTerrain.cs#L18-L34) | Vault depth read from `office-vault.txt` |
| [src/KSAStructures/Ksa/OfficeLift.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeLift.cs#L18-L23) | Lift stops derived from the vault depth |
| [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml#L5756-L5763) | Garage ground cover as a `Terrain` part model |
| [src/KSAStructures/Textures/KSAOffice_GaragePit.png](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Textures/KSAOffice_GaragePit.png) | The pit height map |

## Snippets

The pit decal as XML, in the engine's own modifier schema:

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

Inserting the decals into Earth's terrain at load:

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

Why the list matters: the render data counts one list and walks the other.

```csharp
// current/decomp/KSA/ProceduralModifiersRenderData.cs L162-L172, L226-L230
	public ProceduralModifiersRenderData(Renderer renderer, TerrainReference terrainReference)
	{
		_terrainReference = terrainReference;
		_renderer = renderer;
		UpdateModifierCount();
		CreateCurveLut(renderer, out _modifierCurveLut, out _modifierCurveImageView, out ModifierCurveData);
		ModifierTextures = new SimpleVkTexture[16];
		ModifierTexturesImageViews = new VkImageView[16];
		SetupCelestialTerrainMods();
	}

	// ...
	public int UpdateModifierCount()
	{
		NumModifiers = _terrainReference.ProceduralModifiers.ModifierCount;
		return NumModifiers;
	}
```

## Game APIs touched

| API | Decomp | Use |
|---|---|---|
| `DecalModifierReference` (`Location`, `Radius`, `AltitudeOffset`, `SmoothFactor`, `Additive`, `HeightMap`, `OnDataLoad`, `Evaluate`) | [current/decomp/KSA/DecalModifierReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DecalModifierReference.cs) | The modifier type and its CPU evaluation |
| `ModifierReference.Order`, `Amplitude`, `BiomeMask` | [current/decomp/KSA/ModifierReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModifierReference.cs) | Sort key, height map scale, biome filter (defaults to all) |
| `TerrainReference.ProceduralModifiers`, `TerrainModifiers`, `ProcessModifiers` | [current/decomp/KSA/TerrainReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TerrainReference.cs#L6-L63) | The two lists |
| `ProceduralModifiersReference.Modifiers`, `ModifierCount` | [current/decomp/KSA/ProceduralModifiersReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ProceduralModifiersReference.cs) | The counted list |
| `ProceduralModifiersRenderData` constructor, `SetupCelestialTerrainMods`, `UpdateModifierCount`, `CreateCurveLut` | [current/decomp/KSA/ProceduralModifiersRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ProceduralModifiersRenderData.cs#L162-L245) | GPU-side sizing at construction |
| `Astronomical.SetupModifierRenderData` | [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L446-L459) | Built once per body |
| `Celestial.GetTerrainHeightFromDirCcf` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L789-L881) | CPU heights include the modifiers when `accurate` |
| `PlanetRenderer` constructor, `ComputeBufferResourceCounts` (`_proceduralModifierSlot`, `_proceduralModifiersBuffer`) | [current/decomp/KSA/PlanetRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetRenderer.cs#L1457-L1496) | `ComputeBufferResourceCounts`, called by the constructor: storage buffer sized per body from `NumModifiers`; the constructor writes it at L1104-L1127 |
| `PlanetRenderer.OnDrawUI` (terrain editor) | [current/decomp/KSA/PlanetRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetRenderer.cs#L2284-L2314) | The only live re-setup, which does not resize |
| `Universe.SetupRenderData`, `PlanetMeshLibrary.Create` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2404-L2428), [current/decomp/KSA/PlanetMeshLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetMeshLibrary.cs#L122-L129) | Where the render data is built at startup |
| `DistanceReference.Km`, `InMeters`, `IsNaN` | [current/decomp/KSA/DistanceReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DistanceReference.cs#L15-L80) | Raw `Km` attribute versus the unit-converted value |
| `Modifier_Decal` (GLSL) | [current/Content/Core/Shaders/Planet/ProceduralModifiersLibrary/Modifiers.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Planet/ProceduralModifiersLibrary/Modifiers.glsl#L481-L531) | GPU twin of `DecalModifierReference.Evaluate` |
| `PartModelModule.Template.Terrain` | [current/decomp/KSA/PartModelModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModelModule.cs#L40-L42) | Draw a static mesh as terrain |

## Pitfalls and version notes

- **Load window only.** Add decals at `[StarMapAllModsLoaded]` (or earlier, after the target
  template exists). Adding one in flight desynchronizes the count from the GPU buffers.
- **Add to both lists and sort.** Adding to `ProceduralModifiers.Modifiers` alone leaves the decal
  out of rendering and height queries; adding to `TerrainModifiers` alone leaves the count short.
- **`AltitudeOffset` is metres, written in the `Km` attribute.** The decal code reads the raw `Km`
  field (`AltitudeOffset.Km`), never the unit-converted value (`DistanceReference` sums `Km * 1000`,
  `M` and the other attributes into `InMeters()`).[^ksa-decal][^ksa-distancereference] On the CPU,
  `DecalModifierReference.Evaluate` lerps toward it inside `GetTerrainHeightFromDirCcf`, whose
  "heightKm" is the height in metres (a lerp of `HeightReference.Minimum`/`Maximum` in metres, with
  `MeanRadius` in metres passed as the radius).[^ksa-celestial] On the GPU, `Modifier_Decal` gets
  `floatParam9 = AltitudeOffset.Km` and a planet radius from `CelestialRenderData.SurfaceRadius`,
  which is `MeanRadius` in metres.[^ksa-modifiers-glsl][^ksa-celestialrenderdata] So Core's LC-39A
  `<AltitudeOffset Km="16.97" />` is a level 16.97 m above the mean radius, and the mod subtracts
  depths in metres from the same number (`16.97 - VaultDepth`). `Amplitude` is metres the same way:
  a height map grey level is `Amplitude / 255` m. Writing `<AltitudeOffset M="16.97" />` instead
  leaves `Km` as NaN while `IsValid` (which tests the converted value) still passes, so the decal
  would feed NaN to both paths.
- **Sixteen modifier textures per body**, shared by every mod that adds decals to it. Earth's stock
  modifiers take nine (four `TilingDetail` and five launch-site `Decal`s, one texture each); the mod
  adds three, or four with a vault, which is why the deep shaft and hall share one decal.[^ksa-core-astronomicals][^mod-terrain]
- **The rim fix** lives in [live terrain material override](./live-terrain-material-override.md).
- **Built against 2026.10.7.5541**; all members above exist in 2026.10.10.5554.

## See also

- [Launch-pad landmarks](./office-landmarks-static-objects.md) - the static objects standing in the pits
- [Terrain height roof override](./terrain-height-roof-override.md) - what wheels and cameras are told the ground is over the pits
- [Dry volume below sea level](./dry-volume-below-sea-level.md) - pits dug below the ocean's radius
- [Generated deep ramp](./generated-deep-ramp.md) - where `OfficeDeep.PitRadius` and the deep layout come from
- [Live terrain material override](./live-terrain-material-override.md) - the rim's cliff displacement held down near the camera
- [Ground clutter exclusion zones](./ground-clutter-exclusion-zones.md) - keeping clutter out of the dug site
- [Deserialize DecalModifierReference XML and add it to both modifier lists before the renderer is built](../../../howto/celestials/terrain/add-terrain-decals-and-dig-a-site/add-terrain-decals-and-dig-a-site-1.md) - how-to article demonstrating this feature
- [Levelling decal plus additive pit height map, ordered after the stock pad decal](../../../howto/celestials/terrain/add-terrain-decals-and-dig-a-site/add-terrain-decals-and-dig-a-site-2.md) - how-to article demonstrating this feature
- [Engine terrain DecalModifier deserialized from XML in code](../../../howto/rendering/textures-and-decals/project-decals-onto-scene-geometry/project-decals-onto-scene-geometry-4.md) - how-to article demonstrating this feature

[^mod-terrain]: src/KSAStructures/Ksa/OfficeTerrain.cs
[^mod-office]: src/KSAStructures/Ksa/Office.cs
[^mod-lift]: src/KSAStructures/Ksa/OfficeLift.cs
[^mod-entry]: src/KSAStructures/Ksa/KSAStructuresMod.cs
[^mod-office-xml]: src/KSAStructures/KSAStructuresOffice.xml
[^mod-readme]: KSAStructures README
[^ksa-decal]: KSA.DecalModifierReference (decompiled, build 2026.10.10.5554)
[^ksa-terrainreference]: KSA.TerrainReference (decompiled, build 2026.10.10.5554)
[^ksa-procmodsref]: KSA.ProceduralModifiersReference (decompiled, build 2026.10.10.5554)
[^ksa-procmodsdata]: KSA.ProceduralModifiersRenderData (decompiled, build 2026.10.10.5554)
[^ksa-astronomical]: KSA.Astronomical (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-planetrenderer]: KSA.PlanetRenderer (decompiled, build 2026.10.10.5554)
[^ksa-partmodelmodule]: KSA.PartModelModule (decompiled, build 2026.10.10.5554)
[^ksa-staticobjectmodel]: KSA.StaticObjectModel (decompiled, build 2026.10.10.5554)
[^ksa-staticobjectrenderer]: KSA.StaticObjectRenderer (decompiled, build 2026.10.10.5554)
[^ksa-core-astronomicals]: Core Astronomicals.xml (Earth launch-site decals)
[^ksa-distancereference]: KSA.DistanceReference (decompiled, build 2026.10.10.5554)
[^ksa-celestialrenderdata]: KSA.CelestialRenderData (decompiled, build 2026.10.10.5554)
[^ksa-planetmeshlibrary]: KSA.PlanetMeshLibrary (decompiled, build 2026.10.10.5554)
[^ksa-clutterrenderer]: KSA.GroundClutterRenderer (decompiled, build 2026.10.10.5554)
[^ksa-modifiers-glsl]: Core shader Planet/ProceduralModifiersLibrary/Modifiers.glsl
