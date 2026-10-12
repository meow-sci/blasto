---
type: Mod Feature
title: Dummy companion body
description: A locally defined atmospheric body with tiny height data is loaded beside three stock-planet presets
mod: lonelyplanets
category: celestials
techniques: [xml-asset]
hooks: [native KSA.Mod.LoadAssetBundles, native KSA.Universe.LoadSystem]
lifecycle: [startup:asset-load, startup:game-loaded]
game_types:
  - KSA.AssetBundle
  - KSA.SystemInfo
  - KSA.AstronomicalTemplate
  - KSA.AtmosphericBodyTemplate
  - KSA.CelestialSystem
  - KSA.TextureReference
tags: [celestial-xml, placeholder-body, textures, ocean, terrain]
---

The Mars, Jupiter and Saturn presets add `Dummy` beside Sol and the chosen planet. Its definition is a local `<AtmosphericBody>` in `Dummy.xml`, registered as an asset by `mod.toml`. The repository does not explain why the extra body is present, so its necessity is unverified.[^lp-manifest][^lp-dummy][^lp-mars][^lp-jupiter][^lp-saturn]

## How it works

The definition gives `Dummy` an orbit around Sol with a 0.1 km semimajor axis, 1 km mean radius, 1 Yg mass, a `Height` reference to `Tiny_Texture.ktx2` with minimum and maximum both 0 km, an `Ocean` mesh reference, and ground/slope material maps from the local `Textures/` folder. The commented Clouds block is inactive XML. `VenusOnly.xml` does not reference `Dummy`.[^lp-dummy][^lp-venus]

This is an independent astronomical body, not a replacement for Mars, Jupiter or Saturn. The system XML must reference its `Id` for it to join that system. The material file paths are relative to the defining mod; the referenced textures are present in this checkout.[^ksa-astronomicaltemplate][^ksa-texture]

## Lifecycle and timing

`Mod.LoadAssetBundles` deserializes `Dummy.xml` during preload and full asset load ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L157-L169), [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L449-L461)). During preload, `SystemLibrary.Default` is unset, so `AssetBundle.OnDataLoad` can register this celestial template; during the full load, it registers the template when the selected `SystemInfo.Requires` finds `<LoadFromLibrary Id="Dummy">` ([current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L84-L108), [current/decomp/KSA/SystemInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SystemInfo.cs#L85-L102)). `CelestialSystem.CreateTreeFrom` later creates `Dummy` as a child of Sol. There is no code callback or per-frame mod work. Whether this body behaves usefully in the running game has not been tested.[^ksa-mod][^ksa-modlibrary][^ksa-assetbundle][^ksa-systeminfo][^ksa-celestialsystem]

## Key code

| Path | Role |
|---|---|
| [mod.toml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/mod.toml#L64) | Lists the local body asset file. |
| [Dummy.xml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/Dummy.xml#L3-L20) | Defines the body and tiny height texture; [Dummy.xml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/Dummy.xml#L22-L56) comments out Clouds and references local surface textures. |
| [MarsOnly.xml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/MarsOnly.xml#L28-L30) / [JupiterOnly.xml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/JupiterOnly.xml#L28-L30) / [SaturnOnly.xml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/SaturnOnly.xml#L28-L30) | Select the body into each system. |

## Snippets

```xml
<!-- Dummy.xml L3-L20 -->
    <AtmosphericBody Id="Dummy" Parent="Sol">
        <Orbit DefinitionFrame="Ecliptic">
            <SemiMajorAxis Km="0.1" />
            <Inclination Degrees="0" />
            <Eccentricity Value="0" />
            <LongitudeOfAscendingNode Degrees="0" />
            <ArgumentOfPeriapsis Degrees="0" />
            <TimeAtPeriapsis Seconds="0" />
        </Orbit>

        <MeanRadius Km="1" />
        <Mass Yg="1" />
        <Color R="1" G="1" B="1" />

        <Height Id="Dummy Height" Path="Textures/Tiny_Texture.ktx2" Category="TerrainHeight">
            <Minimum Km="0"/>
            <Maximum Km="0"/>
        </Height>
```

```xml
<!-- Dummy.xml L34-L45 -->
        <Ocean>
            <MeshCollection Id="OceanEarth"/>            
        </Ocean>

        <Terrain>
            <BiomeMaterials>
                <BiomeMaterial Biome="Surface">
                    <GroundMaterial>
                        <Diffuse Id="LunaGroundDiffuse" Path="Textures/Luna_Albedo.dds" Category="Terrain"/>
                        <Normal Id="LunaGroundNormal" Path="Textures/Luna_Normal.dds" Category="Terrain"/>
                        <AoRoughMetal Id="LunaGroundAoRoughMetal" Path="Textures/Luna_AoRoughMetal.dds" Category="Terrain"/>
                        <Displacement Id="LunaGroundDisplacement" Path="Textures/Luna_Displacement.dds" Category="Terrain"/>
```

## Game APIs touched

| API | Purpose |
|---|---|
| [current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L84-L108) | `AssetBundle.OnDataLoad` accepts `<AtmosphericBody>` and registers it according to selected-system requirements. |
| [current/decomp/KSA/SystemInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SystemInfo.cs#L85-L102) | `SystemInfo.Requires` finds the `Dummy` ID among selected system references. |
| [current/decomp/KSA/AstronomicalTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AstronomicalTemplate.cs#L131-L164) | `AstronomicalTemplate.OnDataLoad` connects height, terrain, ocean and other references during full load. |
| [current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs#L84-L104) | `TextureReference.DoLoad` checks the local texture path. |
| [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L215-L251) | `CelestialSystem.CreateTreeFrom` instantiates the child body under Sol. |

## Pitfalls and version notes

- The code establishes that `Dummy` is included in three system templates, but neither the README nor XML says why. Do not copy it as a known workaround without testing its effect.[^lp-dummy]
- The Clouds XML block is inside a comment; `CloudDetail2048.png` is packaged but is not selected by this body.[^lp-dummy]
- The repository's [Textures/DISCLAIMER.txt](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/Textures/DISCLAIMER.txt#L1-L2) excludes all textures **except** `Tiny_Texture.ktx2` from the author's CC BY-NC-SA 4.0 license. The rights of the other texture files are unspecified.[^lp-disclaimer]

## See also

- [Single-planet system presets](single-planet-system-presets.md)
- [Planet starting situations](planet-starting-situations.md)
- [Native system XML with library references and an optional local body](../../../howto/celestials/systems-and-stars/compose-reduced-star-systems/compose-reduced-star-systems.md) - how-to article demonstrating this feature

[^lp-manifest]: LonelyPlanets mod.toml
[^lp-dummy]: LonelyPlanets Dummy.xml
[^lp-mars]: LonelyPlanets MarsOnly.xml
[^lp-jupiter]: LonelyPlanets JupiterOnly.xml
[^lp-saturn]: LonelyPlanets SaturnOnly.xml
[^lp-venus]: LonelyPlanets VenusOnly.xml
[^lp-disclaimer]: LonelyPlanets Textures/DISCLAIMER.txt
[^ksa-assetbundle]: KSA.AssetBundle, build 2026.10.10.5554
[^ksa-systeminfo]: KSA.SystemInfo, build 2026.10.10.5554
[^ksa-astronomicaltemplate]: KSA.AstronomicalTemplate, build 2026.10.10.5554
[^ksa-celestialsystem]: KSA.CelestialSystem, build 2026.10.10.5554
[^ksa-texture]: KSA.TextureReference, build 2026.10.10.5554
[^ksa-mod]: KSA.Mod, build 2026.10.10.5554
[^ksa-modlibrary]: KSA.ModLibrary, build 2026.10.10.5554
