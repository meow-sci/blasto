---
type: How-To
title: Native system XML with library references and an optional local body
description: Create a native system preset that loads Sol and one stock planet, selects a home body, and optionally includes a separately registered local body
category: celestials
group: systems-and-stars
topic: compose-reduced-star-systems
difficulty: intermediate
prerequisites: [KSA native content mod, Core body assets]
demonstrated_by:
  - mods/lonelyplanets/features/single-planet-system-presets
  - mods/lonelyplanets/features/dummy-companion-body
techniques: [xml-asset, toml-config]
hooks:
  - native KSA.Mod.PrepareSystems
  - native KSA.Mod.LoadAssetBundles
  - native KSA.Mod.LoadSystems
  - native KSA.Universe.LoadSystem
lifecycle: [startup:asset-load, startup:game-loaded]
game_types:
  - KSA.Mod
  - KSA.SystemInfo
  - KSA.SystemTemplate
  - KSA.AstronomicalReference
  - KSA.AssetBundle
  - KSA.AtmosphericBodyTemplate
  - KSA.CelestialSystem
tags: [system-xml, celestial-bodies, home-body, library-reference, texture-rights]
---

Create a selectable system with Sol and one stock planet. This native XML/TOML approach is demonstrated by [Lonely Planets' system presets](../../../../mods/lonelyplanets/features/single-planet-system-presets.md) and [local companion body](../../../../mods/lonelyplanets/features/dummy-companion-body.md). It needs KSA's Core assets available and no C# assembly or StarMap hook. The steps use Mars; substitute another existing library body ID for another system.[^ksa-core-astronomicals]

## Background: how the game does it

`Mod.PrepareSystems` reads a mod's `systems` entries as `SystemInfo`; `Mod.LoadSystems` later deserializes the selected XML as `SystemTemplate`. Every listed path is combined with that mod's directory. `<LoadFromLibrary>` uses `AstronomicalReference.Resolve` to look up an existing body definition, while `SystemTemplate.OnDataLoad` joins `Parent` IDs into a tree. `CelestialSystem` instantiates it and honors the chosen `HomeBody`.[^ksa-mod][^ksa-astronomicalreference][^ksa-systemtemplate][^ksa-celestialsystem]

## Lifecycle and timing

- `startup:asset-load`: KSA discovers local system files and asset bundles; during full load it registers library assets and the selected system template. A missing file in this mod directory does not redirect to Core.[^ksa-mod][^ksa-assetbundle]
- `startup:game-loaded`: `Universe.LoadSystem` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L223-L259)) constructs the runtime body tree from the selected template. No per-frame mod callback is involved.[^ksa-universe][^ksa-celestialsystem]

See the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Create a native mod folder and manifest.** Put `mod.toml` and `QuietMars.xml` in the same folder under `Documents/My Games/Kitten Space Agency/mods/`. Give the mod and system distinct IDs if you also install Lonely Planets. This minimal manifest is adapted from its `systems` setting in [mod.toml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/mod.toml#L1-L14):[^lp-manifest]

   ```toml
   # adapted from mod.toml L1-L14
   name = "QuietMars"
   systems = ["QuietMars.xml"]
   ```

   Core supplies its own definitions separately. Do not add Core filenames to this mod's arrays unless those files actually exist in this folder: `Mod.LoadSystems` and `Mod.LoadAssetBundles` resolve both arrays relative to `DirectoryPath`.[^ksa-mod]

2. **Create `QuietMars.xml` with the body tree.** This complete minimal XML is adapted from the header and body references in [MarsOnly.xml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/MarsOnly.xml#L1-L30). `Sol` has no parent; `Mars` points to Sol and is the sole home body. Core defines Sol in [current/Content/Core/Astronomicals.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Astronomicals.xml#L1-L3) and Mars in [current/Content/Core/Astronomicals.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Astronomicals.xml#L2750).[^lp-mars][^ksa-core-astronomicals]

   ```xml
   <!-- adapted from MarsOnly.xml L1-L3, L28-L30 -->
   <System Id="QuietMars">
       <DisplayName Value="Quiet Mars"/>
       <LoadFromLibrary Id="Sol"/>
       <LoadFromLibrary Id="Mars" Parent="Sol" HomeBody="true"/>
   </System>
   ```

3. **Add a local body only if your design needs one.** Lonely Planets lists `Dummy.xml` in `assets` and selects `Dummy` with `<LoadFromLibrary Id="Dummy" Parent="Sol"/>` in three presets. Its purpose is undocumented. The two-body XML above contains no reference to `Dummy`; whether a system without it runs correctly was not tested. If adding a body, use the full [Dummy.xml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/Dummy.xml#L1-L58) structure as a starting point for `YourBody.xml`: change the body ID and parent, choose orbit, mass and radius values suitable for your design, and replace all eight `Textures/Luna*.dds` paths with your own permitted terrain textures in your mod folder. The source's 0.1 km semimajor axis and 1 Yg mass are its declared values, not a physically validated template. The Clouds block is commented out. Do not copy Lonely Planets' excluded texture files; only `Tiny_Texture.ktx2` is covered by its stated CC license, subject to that license's terms.[^lp-dummy][^lp-disclaimer]

   After the asset and its textures exist locally, add `assets = ["YourBody.xml"]` to `mod.toml` and `<LoadFromLibrary Id="YourBody" Parent="Sol"/>` inside `QuietMars.xml`. `AssetBundle.OnDataLoad` registers the body definition, and the system reference selects it.[^ksa-assetbundle][^ksa-systemtemplate]

   ```toml
   # adapted from mod.toml L63-L64
   assets = ["YourBody.xml"]
   ```

   ```xml
   <!-- adapted from MarsOnly.xml L28-L30 -->
   <LoadFromLibrary Id="YourBody" Parent="Sol"/>
   ```

4. **Select and inspect the system in KSA.** For this body-only example, choose a non-Testing game type so startup follows the home body instead of a starting vehicle that the XML does not define ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2534-L2545)). The system picker should discover `QuietMars.xml` through `Mod.PrepareSystems`; when the chosen template loads, `Mod.LoadSystems` logs `loaded system 'QuietMars'`. On construction, `CelestialSystem` logs `instantiated 'QuietMars' system`. Check the map for Sol and Mars, and that the initial camera follows Mars; if you added `YourBody`, check that it appears too.[^ksa-mod][^ksa-celestialsystem][^ksa-universe]

## Complete example

| File | Role |
|---|---|
| [mod.toml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/mod.toml#L8-L14) | Registers three shipped reduced system files; also lists absent copied Core names, so adapt the array to actual local files. |
| [MarsOnly.xml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/MarsOnly.xml#L1-L38) | Full Mars preset, including metadata, local body reference and starter vehicle. |
| [Dummy.xml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/Dummy.xml#L1-L58) | Optional local body example; replace excluded terrain materials with your own permitted files before adapting it. |

## Variations and gotchas

- `VesselTextures`, `TerrainTextures` and `UnscaledTextures` in [MarsOnly.xml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/MarsOnly.xml#L4-L26) are declared summary metadata. `SystemInfo.GetVramEstimate` can calculate from them, but no caller was found in this decompiled build. The checked sources provide no measured runtime memory result, and this example was not run in game.[^ksa-systeminfo]
- The `Clouds` section in [Dummy.xml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/Dummy.xml#L22-L32) is commented out. Its height, ocean and terrain sections are active.[^lp-dummy]
- A missing library ID makes `AstronomicalReference.CreateInto` throw; a missing `Parent` ID is logged while joining the tree. Keep exactly one intended `HomeBody`, since multiple are warned about and KSA uses the first.[^ksa-astronomicalreference][^ksa-systemtemplate][^ksa-celestialsystem]
- The mod's Venus XML is present but absent from its `systems` array; its README reports that Venus does not work in that version. XML well-formedness alone is not a runtime test.[^lp-manifest]

## See also

- [Overview](overview.md)
- [Place starting vessels in a custom system](../place-starting-vessels-in-custom-systems/overview.md)
- [Resolve asset IDs and mod-relative paths](../../../assets/asset-xml/resolve-asset-ids-and-mod-relative-paths/overview.md)
- [Single-planet system presets](../../../../mods/lonelyplanets/features/single-planet-system-presets.md); [Dummy companion body](../../../../mods/lonelyplanets/features/dummy-companion-body.md)

[^lp-manifest]: LonelyPlanets mod.toml
[^lp-mars]: LonelyPlanets MarsOnly.xml
[^lp-dummy]: LonelyPlanets Dummy.xml
[^lp-disclaimer]: LonelyPlanets Textures/DISCLAIMER.txt
[^ksa-mod]: KSA.Mod, build 2026.10.10.5554
[^ksa-systemtemplate]: KSA.SystemTemplate, build 2026.10.10.5554
[^ksa-systeminfo]: KSA.SystemInfo, build 2026.10.10.5554
[^ksa-astronomicalreference]: KSA.AstronomicalReference, build 2026.10.10.5554
[^ksa-assetbundle]: KSA.AssetBundle, build 2026.10.10.5554
[^ksa-celestialsystem]: KSA.CelestialSystem, build 2026.10.10.5554
[^ksa-universe]: KSA.Universe, build 2026.10.10.5554
[^ksa-core-astronomicals]: KSA Core Astronomicals.xml, build 2026.10.10.5554
