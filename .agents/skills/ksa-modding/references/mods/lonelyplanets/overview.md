---
type: Mod
title: Lonely Planets
description: Native XML systems select one planet around Sol with a custom starter vessel and situation
mod: lonelyplanets
tags: [celestials, system-xml, low-vram, native-mod, situations]
repo: LonelyPlanets
license: CC-BY-NC-SA-4.0
author: Jared Melchers
loader: native
mod_id: LonelyPlanets
---

Lonely Planets is Jared Melchers's native content mod for selecting a smaller, one-planet Sol system when the full Solar System exceeds available VRAM. The repository offers Mars, Jupiter and Saturn presets; a Venus preset exists but is omitted from `systems` because the author reports it does not work. These are repository claims, not runtime test results for the pinned game build.[^lp-readme][^lp-manifest]

## Install footprint

| Item | Value |
|---|---|
| Folder and identity | Extract the `LonelyPlanets` folder into the KSA `mods` directory; `ModLibrary.PrepareAll` passes the folder entry's ID to `Mod.MakeUsing` as `Mod.Id`, while `mod.toml` declares `name = "LonelyPlanets"` ([README.md](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/README.md#L5-L6), [mod.toml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/mod.toml#L1), [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L373-L443), [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L103-L110)). |
| New selectable systems | `JupiterOnly.xml`, `SaturnOnly.xml`, `MarsOnly.xml` are the three local entries in `systems`; `VenusOnly.xml` is present but disabled ([mod.toml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/mod.toml#L12-L14)). |
| New asset bundles | `LonelySituations.xml` (four orbit situations) and `Dummy.xml` (one astronomical body) are the only `assets` entries present in this folder ([mod.toml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/mod.toml#L63-L64)). |
| Textures | `Textures/` contains a tiny height texture and Luna-derived surface textures referenced by `Dummy.xml`; the Clouds block referencing `CloudDetail2048.png` is commented out ([Dummy.xml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/Dummy.xml#L17-L56)). |
| Other manifest settings | Texture caps, shadow maps, thumbnail sizes, simulation speeds, planet mesh and heightmap collection paths ([mod.toml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/mod.toml#L3-L10)), plus a star binary, font and startup console commands ([mod.toml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/mod.toml#L66-L74)), are listed in the manifest. |

The manifest also lists many Core system, asset, mesh, star-binary and font filenames that are absent from this mod folder. In build 2026.10.10.5554, `Mod` combines each listed name with **its own** `DirectoryPath` and `XmlLoader` returns no object for a missing path. The names do not inherit or redirect to `Content/Core`; Core must be loaded independently for the shared Sol, planet, vehicle and material definitions.[^ksa-mod][^ksa-loader]

## Loader model and timing

This is a native XML/TOML mod: there is no C# entry assembly, StarMap attribute or Harmony patch. KSA's `ModLibrary` finds its `mod.toml`, calls `Mod.PrepareSystems` before system selection, then preloads asset bundles and local system templates ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L373-L460), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L899-L942)). After selection, `ModLibrary.LoadAll` loads asset bundles and the selected full system template ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L478-L520)).[^ksa-library][^ksa-program]

| Native call | When | Lonely Planets contribution |
|---|---|---|
| `Mod.PrepareSystems` | Before the system selection screen | Register the three local `SystemInfo` choices from XML headers. Missing copied Core paths yield no local choice ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L417-L441)). |
| `Mod.LoadAssetBundles` / `Mod.LoadSystems` | Preload and full `ModLibrary.LoadAll` | Register `LonelySituations.xml` and `Dummy.xml`; preload every local system and fully load the selected one ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L128-L167)). |
| `Universe.LoadSystem` | After assets load | Instantiate Sol, the selected planet, the `Dummy` (in Mars/Jupiter/Saturn), and (in Testing mode) `Gemini7` from the template ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L223-L259)). |
| `Universe.OnLoaded` | After `Universe.LoadSystem` constructs the selected system | Run the manifest's `console.onLoad` commands (`simspeed`, two `camera map` calls, then `camera orbit`) after the engine's initial follow/control command ([mod.toml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/mod.toml#L69-L74), [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L498-L505), [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2534-L2546)). |

There is no per-frame or physics hook in this repository. The XML describes objects the stock engine subsequently simulates and renders.[^ksa-universe]

## Dependencies and architecture

KSA's built-in `ModLibrary`, XML serializers, system selection and celestial loader implement the mod. It uses no StarMap, Harmony, KittenExtensions, ShaderExtensions, ModMenu or native library. The three system files reuse library identifiers such as `Sol`, `Mars` and `Gemini7`, so Core's definitions and default vehicle save must be available. `LonelySituations.xml` supplies orbit templates; `Dummy.xml` supplies the additional body referenced by Mars, Jupiter and Saturn. Texture paths in `Dummy.xml` resolve within LonelyPlanets.[^lp-mars][^lp-dummy][^lp-situations][^ksa-mod]

## Features

| Feature | Category | Lifecycle |
|---|---|---|
| [Single-planet system presets](features/single-planet-system-presets.md) | celestials | startup:asset-load, startup:game-loaded |
| [Dummy companion body](features/dummy-companion-body.md) | celestials | startup:asset-load, startup:game-loaded |
| [Planet starting situations](features/planet-starting-situations.md) | flight-and-orbits | startup:asset-load, startup:game-loaded |

## Build, test and compatibility

There is no build project or automated test in this XML/TOML repository. All six XML documents parsed as XML in this analysis, and the cited engine paths and members were checked against decompiled build 2026.10.10.5554. KSA was not run; loading, VRAM savings and in-game behavior are unverified. The author reports that Venus fails and that the vehicle creator does not work in the other three presets, for which Testing mode is required.[^lp-readme] The engine removes system vehicle templates outside Testing mode, independently confirming that Testing is needed for their bundled `Gemini7` starter, while the vehicle creator issue itself was not reproduced.[^ksa-system]

The VRAM numbers in each system XML are declared summaries, not a measurement of loaded memory. `SystemInfo.GetVramEstimate` can compute an estimate from those summaries ([current/decomp/KSA/SystemInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SystemInfo.cs#L56-L82)), but no call site was found in the pinned decompiled build. Any change to Core body IDs, the `Gemini7` default save, XML schemas, texture formats or load order could affect this mod. [JupiterOnly.xml](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/JupiterOnly.xml#L1-L2) also spells its display name `Juipiter Only`; the ID remains `JupiterOnly`.[^lp-jupiter][^ksa-systeminfo]

The README and `LICENSE.txt` license the author's work under CC BY-NC-SA 4.0. [Textures/DISCLAIMER.txt](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/Textures/DISCLAIMER.txt#L1-L2) explicitly excludes **every texture except `Tiny_Texture.ktx2`** from that license because the author does not own the excluded textures. Their separate rights are not specified; do not treat this repository's CC license as permission to reuse them.[^lp-readme][^lp-license][^lp-disclaimer]

## Secondary sources

- [README.md](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/README.md#L1-L14) records the intended low-VRAM use, the supported presets and the author's known issues.
- [Textures/DISCLAIMER.txt](https://github.com/Jared-Melchers/LonelyPlanets/blob/a5dd50de5ffad938d9737f86526a2fae00aa4c98/Textures/DISCLAIMER.txt#L1-L2) narrows the repository license for textures.

[^lp-readme]: LonelyPlanets README.md
[^lp-manifest]: LonelyPlanets mod.toml
[^lp-mars]: LonelyPlanets MarsOnly.xml
[^lp-jupiter]: LonelyPlanets JupiterOnly.xml
[^lp-situations]: LonelyPlanets LonelySituations.xml
[^lp-dummy]: LonelyPlanets Dummy.xml
[^lp-license]: LonelyPlanets LICENSE.txt
[^lp-disclaimer]: LonelyPlanets Textures/DISCLAIMER.txt
[^ksa-mod]: KSA.Mod, build 2026.10.10.5554
[^ksa-loader]: KSA.XmlLoader, build 2026.10.10.5554
[^ksa-library]: KSA.ModLibrary, build 2026.10.10.5554
[^ksa-program]: KSA.Program, build 2026.10.10.5554
[^ksa-universe]: KSA.Universe, build 2026.10.10.5554
[^ksa-system]: KSA.SystemTemplate, build 2026.10.10.5554
[^ksa-systeminfo]: KSA.SystemInfo, build 2026.10.10.5554
