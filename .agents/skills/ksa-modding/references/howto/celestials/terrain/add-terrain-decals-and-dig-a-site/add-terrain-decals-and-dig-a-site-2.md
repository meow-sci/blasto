---
type: How-To
title: Levelling decal plus additive pit height map, ordered after the stock pad decal
description: Compose an absolute-level decal with an additive height-map decal so a dug site meets the stock launch pad without a step, and route cover meshes to the terrain draw bucket
category: celestials
group: terrain
topic: add-terrain-decals-and-dig-a-site
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksastructures/features/terrain-decal-pits, mods/ksastructures/features/generated-deep-ramp]
techniques: [terrain-modifier, xml-asset]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded]
game_types: [KSA.DecalModifierReference, KSA.TerrainReference, KSA.StaticObjectModel, KSA.PartModelModule]
tags: [terrain, decal, pit, levelling, order, terrain-bucket]
---
Outcome: a site that is levelled to the stock pad's height and then dug (or built up) without a step where it meets the pad, with the pit walls and cover meshes shaded like ground. Demonstrated by KSAStructures' garage, deep shaft and vault ([Terrain decal pits](../../../../mods/ksastructures/features/terrain-decal-pits.md), [Generated deep ramp](../../../../mods/ksastructures/features/generated-deep-ramp.md)). Start with [approach 1](add-terrain-decals-and-dig-a-site-1.md) for how a decal reaches the body.

## Background: how the game does it

A decal computes a height-map relief term `(sample - 0.5) * Amplitude * weight * feather` (the feather is a smoothstep set by `SmoothFactor`). When `Additive` is true it only adds that term; when `Additive` is false it first lerps the ground toward its `AltitudeOffset` by the feather and then adds the same term, which is zero for a levelling decal with `Amplitude 0` ([current/decomp/KSA/DecalModifierReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DecalModifierReference.cs#L185-L227)). Modifiers are applied in `Order`; Core's pad decals are Order 9999 ([current/Content/Core/Astronomicals.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Astronomicals.xml#L734-L750)). A decal added with a higher `Order` sees the already-levelled ground, so:

1. a **levelling decal** (`Additive false`, `Amplitude 0`, the pad's `AltitudeOffset`) flattens everything in its radius to the pad's height and meets the pad's own flattening at the same level, with no step;
2. a **pit decal** (`Additive true`, a PNG with `Amplitude` metres of depth per 255 grey levels) digs relative to that flat ground.

## Lifecycle and timing

Both decals are added at `[StarMapAllModsLoaded]` with the code from approach 1 (`startup:all-mods-loaded`); the composition is data, not timing. The generated XML for cover meshes loads at `startup:asset-load` like any asset.

## Steps

1. **Level first.** Copy the stock pad's `AltitudeOffset` and use a larger radius than the site. The mod's `KSAOffice_GarageLevel` uses Order 10000, radius 240 and the same `Km="16.97"` as LC-39A, so the ground is flat to the pad's own height with no seam:

```csharp
// src/KSAStructures/Ksa/OfficeTerrain.cs L40-L55
private const string PitXml = """
    <Modifiers>
      <Modifier Type="Decal" Name="KSAOffice_GarageLevel">
        <Amplitude Value="0" />
        <Order Value="10000" />
        <Radius Value="240" />
        <Rotation Degrees="0" />
        <Location Id="KSAOffice-GarageLevel">
          <Latitude Degrees="28.60815444" />
          <Longitude Degrees="-80.60567094" />
        </Location>
        <AltitudeOffset Km="16.97" />
        <SmoothFactor Value="0.4" />
        <Additive Value="false" />
        <HeightMap Id="KSAOffice_GarageLevelMap" Path="Textures/KSAOffice_GaragePit.png" Category="TerrainHeight" />
      </Modifier>
```

2. **Dig additively.** The pit is an additive height map: depth is `Amplitude / 255` metres per grey level. Order is one above the levelling decal:

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

3. **Prefer an absolute level for a deep, simple pit.** A height map's neutral grey is not quite half, which at depth lifts everything round the pit by decimetres. The vault and the deep shaft therefore use another levelling-style decal with a lower `AltitudeOffset` (`16.97 - depth`), and share one decal for shaft and hall because each decal consumes a texture slot:

```csharp
// src/KSAStructures/Ksa/OfficeTerrain.cs L95-L112
private static string DeepXml() => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"""
      <Modifier Type="Decal" Name="KSAOffice_DeepLevel">
        <Amplitude Value="0" />
        <Order Value="10010" />
        <Radius Value="{OfficeDeep.PitRadius}" />
        <Rotation Degrees="0" />
        <Location Id="KSAOffice-Deep">
          <Latitude Degrees="{LatitudeDeg + (OfficeDeep.PitNorth / 110860.0)}" />
          <Longitude Degrees="{TowerLongitudeDeg + ((OfficeDeep.PitEast + 3.0) / 97730.0)}" />
        </Location>
        <AltitudeOffset Km="{16.97 - OfficeDeep.Depth - 0.5}" />
        <SmoothFactor Value="0.05" />
        <Additive Value="false" />
        <HeightMap Id="KSAOffice_DeepLevelMap" Path="Textures/KSAOffice_GaragePit.png" Category="TerrainHeight" />
      </Modifier>

    """);

```

4. **Cover the dug ground.** Pits render badly from a distance, so the ground over everything dug is levelled first and a cover mesh lies a few centimetres above it. A `StaticSubObject` whose `PartModel` has `<Terrain>true</Terrain>` is put in the `OpaqueTerrain` draw bucket, whose pipeline compiles the static-object fragment shader with `SAMPLE_TERRAIN` so it shades like the ground:

```xml
<!-- src/KSAStructures/KSAStructuresOffice.xml L5756-L5763 -->
<!-- The garage's roof and the ledges beside its ramp, shaded as the ground they replace. -->
<StaticSubObject Id="KSAOffice_GarageGround">
  <PartModel Id="KSAOffice_GarageGround_Model">
    <Mesh Id="KSAOffice_GarageTop" />
    <Material Id="KSAOffice_GarageMaterial" />
    <Terrain>true</Terrain>
  </PartModel>
</StaticSubObject>
```

## Complete example

| File | Role |
|---|---|
| [src/KSAStructures/Ksa/OfficeTerrain.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeTerrain.cs#L36-L111) | Level, pit, deep and vault decals |
| [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml#L5756-L5763) | Garage cover as a `Terrain` part model |
| [tools/model/office-deep.py](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/model/office-deep.py) | Generator for the deep-ramp layout, collider boxes and the `PITS` constant |

## Variations and gotchas

- **Clutter follows.** The ground clutter generator dispatches one compute pass per modifier, so grass and rocks lie on the dug terrain. Clearing them is a separate step: see [exclude or replace ground clutter](../../../celestials/terrain/exclude-or-replace-ground-clutter/overview.md).
- **Live material tweaks** such as holding the rim's cliff displacement down near the camera are a separate mechanism, described in [change loaded game data](../../../celestials/atmospheres-and-rings/change-loaded-game-data-and-make-the-renderer-notice/overview.md).
- **Orders must sort after the stock decal** you meet, or the pad flattening lerps your relief away again.
- **Statics are culled from inside a pit** because the engine tests visibility against terrain sampled `accurate: false`; see [place static objects](../../../celestials/terrain/place-static-objects-on-a-planet-surface/overview.md).
- **Do not retry in flight.** Adding or editing decals after startup desynchronizes CPU and GPU data (approach 1).

## See also

- [Overview](overview.md); [approach 1](add-terrain-decals-and-dig-a-site-1.md)
- [Terrain decal pits](../../../../mods/ksastructures/features/terrain-decal-pits.md), [Generated deep ramp](../../../../mods/ksastructures/features/generated-deep-ramp.md)
- [Query the terrain height field and test line of sight](../../../celestials/terrain/query-terrain-height-and-line-of-sight/overview.md)
