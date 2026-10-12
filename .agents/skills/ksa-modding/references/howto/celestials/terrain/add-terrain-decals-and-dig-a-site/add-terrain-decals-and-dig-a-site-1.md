---
type: How-To
title: Deserialize DecalModifierReference XML and add it to both modifier lists before the renderer is built
description: Write a Decal modifier as XML in a C# string, deserialize it with the engine's own type, run OnDataLoad with your mod, and add it to a body's ProceduralModifiers and TerrainModifiers at StarMapAllModsLoaded
category: celestials
group: terrain
topic: add-terrain-decals-and-dig-a-site
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksastructures/features/terrain-decal-pits]
techniques: [terrain-modifier, xml-asset]
hooks: [starmap StarMapImmediateLoad, starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded]
game_types:
  - KSA.DecalModifierReference
  - KSA.ModifierReference
  - KSA.TerrainReference
  - KSA.ProceduralModifiersReference
  - KSA.ProceduralModifiersRenderData
  - KSA.CelestialTemplate
  - KSA.PlanetRenderer
  - KSA.PlanetMeshLibrary
  - KSA.GroundClutterRenderer
tags: [terrain, decal, modifier, xml, load-time]
---
Outcome: a terrain decal added from code, consistently drawn, collided with, queried and cluttered. Demonstrated by KSAStructures ([Terrain decal pits](../../../../mods/ksastructures/features/terrain-decal-pits.md)). Prerequisites: StarMap, a height-map PNG in your mod folder, and the body's `CelestialTemplate` (here `Earth`, fetched with `ModLibrary.TryGet`).

## Background: how the game does it

Each body's `TerrainReference` holds two lists of the same `ModifierReference` objects: `ProceduralModifiers.Modifiers` (read for the count) and `TerrainModifiers` (walked for parameters, curve lookup rows and texture slots, and by the CPU height function). `ProcessModifiers` fills the second from the first and sorts by `Order` when the XML loads ([current/decomp/KSA/TerrainReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TerrainReference.cs#L47-L51)). The renderer then sizes buffers from the count once, in the `Program` constructor: `PlanetMeshLibrary.Create` and `new PlanetRenderer` run after `ModLibrary.LoadAll` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L942), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1148), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1158)), and the per-body `ProceduralModifiersRenderData`, created once inside `PlanetMeshLibrary.Create` by `Universe.SetupRenderData` -> `Astronomical.SetupModifierRenderData` after `Universe.LoadDefaultSystem` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2404-L2427)), reads `NumModifiers` from `ModifierCount` and sizes its curve lookup from it ([current/decomp/KSA/ProceduralModifiersRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ProceduralModifiersRenderData.cs#L228-L234)). The ground clutter generator dispatches per modifier, so clutter follows the dug terrain.

## Lifecycle and timing

`[StarMapAllModsLoaded]` (`startup:all-mods-loaded`, a postfix on `ModLibrary.LoadAll`) is the window: every template has run `OnDataLoad` (so `ProcessModifiers` has filled `TerrainModifiers`), while `Universe.LoadDefaultSystem`, the per-body modifier render data and `new PlanetRenderer` all come later in the `Program` constructor. (`ModLibrary.AttachGameData` also runs after this hook; it merges part and clutter game data and does not touch terrain.) `[StarMapImmediateLoad]` earlier hands the mod its own `Mod` object (needed so `OnDataLoad` resolves the PNG against the mod folder). Adding a decal in flight leaves the GPU with the old count and parameters while the CPU height function walks the re-sorted list with the old `NumModifiers`, so heights, collision and rendering disagree. For the same reason the mod cannot be toggled in a running game. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Keep your `Mod` object.** In a `[StarMapImmediateLoad]` method with exactly one `KSA.Mod` parameter, store it (`Office.Owner = mod`):

```csharp
// src/KSAStructures/Ksa/KSAStructuresMod.cs L30-L36
[StarMapImmediateLoad]
public void OnImmediateLoad(Mod mod)
{
    // Before anything asks KSA to compile a shader: the generated header names this machine's
    // own install, and a .comp that includes it will not compile until it is there.
    CoreShaderInclude.Write(mod.DirectoryPath);
    Office.Owner = mod;
```

2. **Write the decal as engine XML.** Orders above the stock pads (Core's LC-39A decal is Order 9999) sort after them, so the new relief wins over the pad:

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

   The stock decal being copied is [current/Content/Core/Astronomicals.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Astronomicals.xml#L734-L750).

3. **Deserialize with the engine's own type.** Run `OnDataLoad(owner)`, load the height map, add the decal to **both** lists, then re-sort `TerrainModifiers` by `Order`; skip any name already present so a second call is a no-op:

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

4. **Call it from `[StarMapAllModsLoaded]` and nowhere later:**

```csharp
// src/KSAStructures/Ksa/KSAStructuresMod.cs L42-L47
[StarMapAllModsLoaded]
public void OnFullyLoaded()
{
    // Here and nowhere later: the pits are terrain decals, and the planet's renderer sizes its
    // buffers from the modifier count when it is built.
    Office.Place();
```

5. **Watch the log.** The mod logs `office: garage pit dug, N terrain modifiers on Earth`; a missing template logs a warning and nothing is added.

## Complete example

| File | Role |
|---|---|
| [src/KSAStructures/Ksa/OfficeTerrain.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeTerrain.cs#L113-L148) | `Dig`: deserialize and insert into both lists |
| [src/KSAStructures/Ksa/Office.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Office.cs#L51-L63) | `Place` fetches the Earth template and calls `Dig` |
| [src/KSAStructures/Textures/KSAOffice_GaragePit.png](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Textures/KSAOffice_GaragePit.png) | The height map |

## Variations and gotchas

- **Both lists, then sort.** Adding to `ProceduralModifiers.Modifiers` only leaves the decal out of rendering and height queries; adding to `TerrainModifiers` only leaves the count short.
- **`AltitudeOffset` is metres, written in the `Km` attribute.** The decal reads the raw `AltitudeOffset.Km` ([current/decomp/KSA/DecalModifierReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DecalModifierReference.cs#L79-L100)), never the unit-converted value; the height query returns metres, so Core's `Km="16.97"` is 16.97 m above the mean radius. Writing `M="16.97"` leaves `Km` as NaN while `IsValid` (which tests the converted value) still passes, and the NaN feeds both paths.
- **The terrain debug panel is not an exception.** `PlanetRenderer`'s "Show Terrain Debug" panel re-runs `UpdateModifierCount` and `SetupCelestialTerrainMods` after a slider edit ([current/decomp/KSA/PlanetRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetRenderer.cs#L2284-L2314)), but only for modifiers that already existed at load: the curve lookup keeps its load-time width, so a modifier added later is not safely picked up by calling those methods.
- **16 modifier textures per body**, shared by every mod; Earth's stock modifiers take nine. Each decal takes one slot whatever map it names, so share one decal across several relief features where possible.
- **`HeightMap` null** defaults to black with a logged error; `Path` is relative to the owning mod.
- **Built against 2026.10.7.5541** upstream; all members above exist in 2026.10.10.5554.

## See also

- [Overview](overview.md); [approach 2](add-terrain-decals-and-dig-a-site-2.md)
- [Terrain decal pits](../../../../mods/ksastructures/features/terrain-decal-pits.md)
- [Query the terrain height field and test line of sight](../../../celestials/terrain/query-terrain-height-and-line-of-sight/overview.md)
- [Change loaded game data and make the renderer notice](../../../celestials/atmospheres-and-rings/change-loaded-game-data-and-make-the-renderer-notice/overview.md)
