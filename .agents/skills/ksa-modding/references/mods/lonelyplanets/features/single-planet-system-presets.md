---
type: Mod Feature
title: Single-planet system presets
description: Three native System XML files select Sol and one stock planet as a smaller playable system
mod: lonelyplanets
category: celestials
techniques: [xml-asset, toml-config]
hooks: [native KSA.Mod.PrepareSystems, native KSA.Mod.LoadSystems, native KSA.Universe.LoadSystem]
lifecycle: [startup:asset-load, startup:game-loaded]
game_types: [KSA.Mod, KSA.SystemInfo, KSA.SystemTemplate, KSA.AstronomicalReference, KSA.CelestialSystem]
tags: [system-xml, low-vram, system-selection, library-reference]
---

Lonely Planets adds three choices to KSA's system picker through `systems` in `mod.toml`. Each XML template builds Sol plus one stock planet and selects that planet as `HomeBody`. A fourth Venus template is present but not registered.[^lp-manifest][^lp-readme]

## How it works

`MarsOnly.xml`, `JupiterOnly.xml` and `SaturnOnly.xml` each declare a unique `<System Id>`, a display name, texture-summary metadata, then `<LoadFromLibrary>` references for `Sol`, `Dummy` and the named planet. Only the selected planet carries `HomeBody="true"`. The XML does **not** define the planet's terrain, atmosphere or orbit: `AstronomicalReference.Resolve` looks up the stock body by ID. The `Dummy` reference is explained [separately](dummy-companion-body.md).[^lp-mars][^lp-jupiter][^lp-saturn][^ksa-astronomicalreference]

The three categories of texture summary in each system are deserialized by `SystemInfo`. Its `GetVramEstimate` method can compute an estimate from them, but no caller was found in the pinned decompiled build. `TerrainTextures Size="2 GB"` is copied across all four custom files; it is declared metadata, not measured VRAM usage. The selected system's body list controls which bodies KSA instantiates, but actual savings were not measured here.[^ksa-systeminfo]

## Lifecycle and timing

Before the picker, `Mod.PrepareSystems` reads each locally present file in `systems` as a light `SystemInfo`, and `Mod.LoadSystems` preloads all local system templates ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L128-L155), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L899-L942)). Selection then sets `SystemLibrary.Default`; during the full asset load, `Mod.LoadSystems` deserializes only the chosen file as a `SystemTemplate`, whose `OnDataLoad` joins its body references into a hierarchy. Later `Universe.LoadSystem` creates the runtime `CelestialSystem` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L223-L259)). There is no mod callback after that: the stock engine owns physics, streaming and rendering.[^ksa-mod][^ksa-systemtemplate][^ksa-celestialsystem][^ksa-program][^ksa-universe]

The manifest also lists four stock system filenames absent from LonelyPlanets. `Mod` looks only relative to LonelyPlanets' directory, and `XmlLoader` silently returns no object when one is absent. Those entries do not load Core's copies; Core has its own mod entry and supplies them independently.[^ksa-mod][^ksa-loader]

## Key code

| Path | Role |
|---|---|
| [mod.toml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/mod.toml#L8-L14) | Registers local system filenames; Venus is omitted. |
| [MarsOnly.xml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/MarsOnly.xml#L28-L37) | Sol, Dummy, Mars and starter-vessel composition. |
| [JupiterOnly.xml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/JupiterOnly.xml#L28-L37) / [SaturnOnly.xml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/SaturnOnly.xml#L28-L37) | Other registered body and vessel compositions. |
| [VenusOnly.xml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/VenusOnly.xml#L28-L36) | Unregistered Venus body and vessel composition. |

## Snippets

```toml
# mod.toml L12-L14
systems = [ "SolSystem.xml", "SolSystemDense.xml", "EarthSystem.xml" , "EarthOnly.xml" , "JupiterOnly.xml", "SaturnOnly.xml", "MarsOnly.xml"]

# VenusOnly.xml is disabled currently as it does not work currently.
```

```xml
<!-- MarsOnly.xml L28-L33 -->
    <LoadFromLibrary Id="Sol"/>
    <LoadFromLibrary Id="Dummy" Parent="Sol"/>
    <LoadFromLibrary Id="Mars" Parent="Sol" HomeBody="true"/>
    
    <LoadVehicleFromLibrary Id="Gemini7" Parent="Mars">
        <SituationRef InstanceOf="MarsStartingSituation"/>
```

## Game APIs touched

| API | Purpose |
|---|---|
| [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L128-L156) | `Mod.LoadSystems` deserializes the chosen full system; `Mod.PrepareSystems` registers picker headers ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L417-L441)). |
| [current/decomp/KSA/SystemInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SystemInfo.cs#L56-L82) | `SystemInfo.GetVramEstimate` can calculate from declared texture summaries. |
| [current/decomp/KSA/SystemTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SystemTemplate.cs#L20-L32) | `SystemTemplate.Bodies` maps the XML library references; [current/decomp/KSA/SystemTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SystemTemplate.cs#L68-L91) shows `OnDataLoad` joining their parent tree. |
| [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L223-L259) | `Universe.LoadSystem` constructs the runtime system from the selected template. |
| [current/decomp/KSA/AstronomicalReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AstronomicalReference.cs#L18-L30) | `AstronomicalReference.Resolve` looks up a stock body by ID. |
| [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L197-L251) | `CelestialSystem.CreateTreeFrom` instantiates referenced bodies and marks the designated home body. |

## Pitfalls and version notes

- `VenusOnly.xml` is omitted from `systems` and the README says it fails. The file itself is well-formed XML, which does not establish that KSA can load or play it.[^lp-venus][^lp-readme]
- The `JupiterOnly.xml` display name is misspelled `Juipiter Only`; its system ID is spelled correctly.[^lp-jupiter]
- The shared Core body IDs must remain available. Adding a file name to another mod's `systems` list does not make KSA search Core's folder.[^ksa-mod][^ksa-loader]
- The texture summaries are estimates, not evidence that these presets fit a particular GPU. No runtime or memory test was available.[^ksa-systeminfo]

## See also

- [Dummy companion body](dummy-companion-body.md)
- [Planet starting situations](planet-starting-situations.md)
- [Native system XML with library references and an optional local body](../../../howto/celestials/systems-and-stars/compose-reduced-star-systems/compose-reduced-star-systems.md) - how-to article demonstrating this feature

[^lp-manifest]: LonelyPlanets mod.toml
[^lp-readme]: LonelyPlanets README.md
[^lp-mars]: LonelyPlanets MarsOnly.xml
[^lp-jupiter]: LonelyPlanets JupiterOnly.xml
[^lp-saturn]: LonelyPlanets SaturnOnly.xml
[^lp-venus]: LonelyPlanets VenusOnly.xml
[^ksa-mod]: KSA.Mod, build 2026.10.10.5554
[^ksa-systeminfo]: KSA.SystemInfo, build 2026.10.10.5554
[^ksa-systemtemplate]: KSA.SystemTemplate, build 2026.10.10.5554
[^ksa-astronomicalreference]: KSA.AstronomicalReference, build 2026.10.10.5554
[^ksa-celestialsystem]: KSA.CelestialSystem, build 2026.10.10.5554
[^ksa-loader]: KSA.XmlLoader, build 2026.10.10.5554
[^ksa-program]: KSA.Program, build 2026.10.10.5554
[^ksa-universe]: KSA.Universe, build 2026.10.10.5554
