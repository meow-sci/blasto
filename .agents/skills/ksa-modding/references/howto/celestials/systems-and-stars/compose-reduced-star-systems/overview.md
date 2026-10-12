---
type: Game Feature
title: Compose a reduced star system from library bodies
description: Register a native system XML that selects a star and chosen library bodies, assigns their parents and home body, and optionally adds a local body asset
category: celestials
group: systems-and-stars
topic: compose-reduced-star-systems
demonstrated_by:
  - mods/lonelyplanets/features/single-planet-system-presets
  - mods/lonelyplanets/features/dummy-companion-body
lifecycle: [startup:asset-load, startup:game-loaded]
game_types:
  - KSA.Mod
  - KSA.SystemInfo
  - KSA.SystemTemplate
  - KSA.AstronomicalReference
  - KSA.AssetBundle
  - KSA.CelestialSystem
tags: [system-xml, celestial-bodies, library-reference, native-content]
---

A system file chooses which library bodies to instantiate. Lonely Planets uses separate XML files for Sol plus Mars, Jupiter or Saturn, and marks the selected planet as the home body. Its three registered presets also reference a locally defined `Dummy` body; the repository does not say why that body is present.[^lp-mars][^lp-dummy]

## Engine background

`Mod.PrepareSystems` reads each `systems` path as a light `SystemInfo`. During preload, `Mod.LoadSystems` reads system templates; after selection it loads the chosen full `SystemTemplate`. Both system and asset paths are relative to that mod's directory. `SystemTemplate.Bodies` accepts `<LoadFromLibrary>`, and `AstronomicalReference.Resolve` finds the named body in the shared library.[^ksa-mod][^ksa-systeminfo][^ksa-systemtemplate][^ksa-astronomicalreference]

`SystemTemplate.OnDataLoad` joins each body's `Parent` ID within the selected system. `CelestialSystem.CreateTreeFromRoot` then builds that tree; a child with `HomeBody="true"` becomes the home body.[^ksa-systemtemplate][^ksa-celestialsystem]

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Native system XML with library references and an optional local body](compose-reduced-star-systems.md) | Lonely Planets | `xml-asset` | `startup:asset-load`, `startup:game-loaded` | Small body list and no C# hook; requires exact library IDs, local files and testing of any custom body |

## See also

- [Place starting vessels in a custom system](../place-starting-vessels-in-custom-systems/overview.md)
- [Enumerate star systems and bodies](../enumerate-star-systems-and-bodies/overview.md)
- [Mod XML load order](../../../assets/asset-xml/mod-xml-load-order/overview.md)
- [Resolve asset IDs and mod-relative paths](../../../assets/asset-xml/resolve-asset-ids-and-mod-relative-paths/overview.md)
- [Game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)

[^lp-mars]: LonelyPlanets MarsOnly.xml
[^lp-dummy]: LonelyPlanets Dummy.xml
[^ksa-mod]: KSA.Mod, build 2026.10.10.5554
[^ksa-systeminfo]: KSA.SystemInfo, build 2026.10.10.5554
[^ksa-systemtemplate]: KSA.SystemTemplate, build 2026.10.10.5554
[^ksa-astronomicalreference]: KSA.AstronomicalReference, build 2026.10.10.5554
[^ksa-celestialsystem]: KSA.CelestialSystem, build 2026.10.10.5554
