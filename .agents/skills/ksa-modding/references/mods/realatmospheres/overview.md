---
type: Mod
title: Real Atmospheres
description: StarMap mod that rebuilds the Sol system's atmospheres, hazes and clouds from measured data by loading patched copies of KSA's shaders and system XML at launch, and swaps in measured flight atmosphere profiles
mod: realatmospheres
tags: [atmosphere, shaders, glsl, clouds, xml, physics, harmony, starmap, celestials]
repo: RealAtmospheres
license: MIT
author: Brimfyr
loader: starmap
mod_id: RealAtmospheres
---

Real Atmospheres (repository owner Brimfyr; the Harmony id is `gunshy.realatmospheres`) gives Earth,
Venus, Mars, Jupiter, Saturn, Uranus, Neptune, Titan, Pluto and Triton atmospheres derived from their
composition and particle optics, measured density profiles, new cloud decks and hazes, and flight
atmospheres that follow real density and pressure curves. The README's one-line promise is the
interesting part for modders: no game file is modified; the mod loads patched copies of the game's
shaders and system data at launch ([README.md](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/README.md#L1-L5)).[^ra-readme]

It does that with three small redirects (one Harmony prefix on the shader compiler entry point, and
prefixes on four `KSA.Mod` loader methods) and a large, carefully fail-soft text-patching engine that
rebuilds the copies from the player's own install every launch. Physics changes are two prefixes on
`PhysicalAtmosphereReference`, plus a load-time postfix that attaches the measured tables. The build has no reference to any game assembly.

## Install footprint

| Item | Value |
|---|---|
| Mod folder | `Documents\My Games\Kitten Space Agency\mods\RealAtmospheres\` (or the active Borea instance's `mods`) |
| `mod.toml` | `name = "Real Atmospheres"`, `[StarMap] EntryAssembly = "RealAtmospheres"` ([mod.toml](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/mod.toml)) |
| Manifest id | `RealAtmospheres` (the folder name); KSA adds it disabled, the player enables it in `manifest.toml` ([README.md](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/README.md#L91-L104)) |
| Shipped files | `RealAtmospheres.dll`, `mod.toml`, `README.md`, `LICENSE`, `CREDITS.md`, `assets/` with the 22 textures and one GLSL file listed in [release-files.txt](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/release-files.txt) |
| Written at runtime | `mods\RealAtmospheres\ShadowContent\Core\Shaders\**` (full patched shader tree), `mods\_RealSharedShadow\Core\*.xml` (patched `Astronomicals.xml` and Sol templates, shared with the sibling mod Real Surfaces), `My Games\Kitten Space Agency\RealAtmospheres\settings.toml` |
| Uninstall | Delete `mods\RealAtmospheres` and `mods\_RealSharedShadow` ([README.md](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/README.md#L104)) |

The mod's `mod.toml` has no `assets` entry: it adds no asset bundles of its own. All its content
enters the game through the Core mod's redirected files, with absolute texture paths into its
`assets` folder.

## Loader model and entry class

StarMap mod. `ModMain` is the `[StarMapMod]` class and uses one hook
([ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L21-L35)).[^ra-modmain] Everything else attaches through Harmony
patches installed there, or through ModMenu's attribute discovery.

| Hook | Method | Fires | What Real Atmospheres does there |
|---|---|---|---|
| `[StarMapBeforeMain]` | `ModMain.BeforeMain` | From `RuntimeMod.InitializeMod`, before the game's `Program.Main` ([StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L129-L162)) | Build the shader shadow tree and the shared XML shadow; install every patch below |
| Harmony prefix | `KSA.Mod.PrepareSystems`, `LoadSystems`, `LoadAssetBundles`, `LoadEditorTagDefinitions` | `ModLibrary.PrepareAll`, `PreloadAssetBundles`, `LoadEditorTags`, `LoadAll` in the `Program` constructor | Swap Core's `Astronomicals.xml` and Sol template entries for the shadow copies |
| Harmony prefix | `RenderCore.ShaderModuleUtils.FromFile` | Each shader compile; the stock set compiles on thread-pool threads inside `ModLibrary.LoadAll` | Redirect `Content\Core\Shaders\*` into the shadow tree |
| Harmony postfix | `KSA.AstronomicalTemplate.OnDataLoad` | Each body template load in `ModLibrary.LoadAll` | Attach a measured flight table and move the atmosphere boundary |
| Harmony prefixes | `KSA.PhysicalAtmosphereReference.GetAtmosphericDensityAtAltitude`, `GetAtmosphericPressureAtAltitude` | Vehicle worker threads every substep (`PhysicsEnvironment.RecomputePositionalValues`, read by drag, buoyancy, chutes and engines); main-thread audio and exhaust effects | Return measured values |
| Harmony prefix + postfixes | `KSA.AtmosphereRenderer.FillPlanetAtmosphereData` | Main thread while recording the atmosphere pass | Sim clock, band switch and haze spin through unused uniform fields; LUT invalidation |
| Harmony transpiler | `KSA.CelestialSystem.GetNearbyAstronomical` | Every frame in `OnFrameCelestials` and `UpdateShaderData` | Keep a planet as the detailed body while a small moon passes |
| `[ModMenuEntry("Real Atmospheres")]` | `Menu.Draw` | By ModMenu while its menu is open | One settings switch |

There is no `[StarMapAllModsLoaded]`, `[StarMapBeforeGui]` or `[StarMapUnload]` hook: everything must
be in place before `ModLibrary.PrepareAll`. Nothing is undone at shutdown. StarMap's `DeInit` removes
every Harmony patch in the process anyway, and the generated folders stay on disk
([StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35)).

## Dependencies

| Dependency | How it is referenced | How it resolves at runtime |
|---|---|---|
| StarMap (`StarMap.API`) | `HintPath` to the StarMap folder (Borea's loader copy or `StarMapLauncher-0.4.5`), `Private=false` ([RealAtmospheres.csproj](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/RealAtmospheres.csproj#L13-L37)) | Provided by the StarMap loader |
| Harmony (`0Harmony`) | Same folder, `Private=false` | Loaded by StarMap before the game |
| KSA, RenderCore (`Planet.Render.Core.dll`), Brutal.ImGui | Not referenced; reached by name with `AccessTools` | The game's own assemblies; `Planet.Render.Core.dll` is loaded early into the game's load context by the mod |
| ModMenu | Optional; vendored `ModMenuEntryAttribute` | Only used if installed |
| Real Surfaces | Optional sibling; shares `Shared/SharedShadow.cs` and the `_RealSharedShadow` folder | Coordinated through files and a named mutex |
| KittenExtensions, ShaderExtensions, native libs | None | None. KittenExtensions (Continued) does not combine with the XML redirect (see Compatibility) |

## Architecture

| Subsystem | Files | Thread / time | Role |
|---|---|---|---|
| Entry and patch install | [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs) | Main, before `Program.Main` | Reflection lookups, shape checks, Harmony patches, the atmosphere side channel |
| Shadow builder | [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs), [Shared/SharedShadow.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Shared/SharedShadow.cs) | Main, before `Program.Main` | Copy and text-patch shaders and XML |
| Payloads | [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs) | Data | 3,200 lines of stock anchors and replacement GLSL/XML |
| Flight atmospheres | [FlightAtmospheres.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/FlightAtmospheres.cs), [AtmosphereTables.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/AtmosphereTables.cs), [AtmosphereTables.Generated.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/AtmosphereTables.Generated.cs) | Load (attach), vehicle workers (lookups) | Measured density and pressure |
| Nearby body | [NearbyBody.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/NearbyBody.cs) | Main, every frame | Transpiled nearby-body ranking |
| Settings | [Menu.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Menu.cs), [Settings.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Settings.cs) | Main, UI | ModMenu page, `settings.toml` |
| Offline pipeline | `*.py`, `deploy.ps1`, `package.ps1` | Developer machine | Generated tables and textures, packaging |

Data flow: at launch the builder copies `Content/Core/Shaders` and the four Core XML files, applies
anchor-checked edits (GLSL functions and call-site rewrites, XML values, new layers and bodies), and
publishes the paths. The game then loads Core's XML from the shared copy and compiles every Core shader
from the shader copy. During play, the flight prefixes answer the physics, and each frame the
atmosphere prefix writes the haze clock and spin into the uniform data the patched shaders read.

## Features

| Feature | Category | Lifecycle |
|---|---|---|
| [Reflection-only mod build](features/reflection-only-mod-build.md) | getting-started | startup:starmap-before-main |
| [Shader file redirect into a patched shadow tree](features/shader-file-redirect.md) | shaders | startup:starmap-before-main, startup:asset-load |
| [Anchor-verified text patching of the shadow tree](features/shadow-tree-anchor-patching.md) | shaders | startup:starmap-before-main |
| [Core XML entry redirect](features/core-xml-entry-redirect.md) | assets | startup:immediate-load, startup:asset-load |
| [Shared shadow for two cooperating mods](features/cross-mod-shared-shadow.md) | assets | startup:starmap-before-main |
| [Measured flight atmospheres](features/measured-flight-atmospheres.md) | physics | startup:asset-load, physics:vehicle-solver, frame:viewports, frame:pre-render, frame:post-render |
| [Per-body atmosphere optics in the system XML](features/atmosphere-optics-in-system-xml.md) | celestials | startup:starmap-before-main, startup:asset-load |
| [Cloud layers in the system XML](features/cloud-layers-in-system-xml.md) | celestials | startup:starmap-before-main, startup:asset-load |
| [Measured density tables in the atmosphere shaders](features/measured-density-tables-in-shaders.md) | shaders | startup:starmap-before-main, render:atmosphere |
| [Mie phase sentinel dispatch](features/mie-phase-sentinel-dispatch.md) | shaders | startup:starmap-before-main, render:atmosphere |
| [Atmosphere uniform side channel](features/atmosphere-uniform-side-channel.md) | shaders | render:atmosphere |
| [Haze-band sky march](features/haze-band-sky-march.md) | shaders | startup:starmap-before-main, render:atmosphere |
| [Cloud face lighting](features/cloud-face-lighting.md) | shaders | startup:starmap-before-main, render:atmosphere |
| [Volumetric cloud map flow and faint-cloud lift](features/volumetric-cloud-map-flow-and-lift.md) | shaders | startup:starmap-before-main, render:atmosphere |
| [Cloud pass shading and ring shadows](features/cloud-pass-shading-and-ring-shadows.md) | shaders | startup:starmap-before-main, render:atmosphere, render:planet |
| [Relief-softened terminator](features/relief-softened-terminator.md) | shaders | startup:starmap-before-main, render:planet |
| [Nearby-body choice transpiler](features/nearby-body-transpiler.md) | patching | startup:starmap-before-main, frame:celestials, frame:render |
| [ModMenu page and TOML settings](features/modmenu-page-and-toml-settings.md) | ui | frame:draw-ui, render:atmosphere |
| [Measured-atmosphere code generator](features/measured-atmosphere-generator.md) | testing-and-tooling | none (offline) |
| [Texture and phase asset scripts](features/texture-and-phase-asset-scripts.md) | assets | none (offline) |
| [Release packaging and deploy scripts](features/release-packaging-and-deploy.md) | testing-and-tooling | none (build time) |

## Build, test and tooling

- `net10.0`, `EnableDynamicLoading`, nullable; version 1.1.0 ([RealAtmospheres.csproj](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/RealAtmospheres.csproj#L3-L17)).
  `deploy.ps1` builds and installs into the active instance; `package.ps1 -Version x.y.z -GameBuild
  vYYYY.M.D.NNNN` builds the release zip ([README.md](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/README.md#L106-L108)).
- No automated tests. Verification is by log: every patch logs either success or a sentence about what
  stays stock, prefixed `RealAtmospheres - `.
- Python tooling: `make_flight_atmospheres.py` (tables, needs numpy and pyatmos, downloads NASA PDS
  data), the Mie and texture scripts (numpy, Pillow, scipy for one, NVIDIA Texture Tools), and
  `atmosphere_model.py`, a numpy port of KSA's atmosphere renderer used to fit colours offline.

## Compatibility

- **Target build.** The CHANGELOG for 1.1.0 says "Requires KSA 2026.10.7 or later"; releases are named
  with the build they were tested on ([CHANGELOG.md](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/CHANGELOG.md#L3-L14)). For this bundle,
  `ShadowBuilder.Build` was run against build 2026.10.10.5554's `Content/Core` in a scratch harness.
  Every shader and XML anchor matched and no warning was logged. The patched shader tree compiled with
  `glslc` exactly where the stock tree does. The IL shape the nearby-body transpiler expects was
  confirmed by disassembling `KSA.dll`.
- **Breakage pattern.** Game updates that edit any of the patched shader lines, move a value in
  `Astronomicals.xml` or the Sol templates, rename a reflected member or change a patched method's
  parameter names. Most cases degrade one feature with a log line. A missing `Mod` loader method or
  `FromFile` overload aborts the rest of the install after the patches already applied (see
  [Reflection-only mod build](features/reflection-only-mod-build.md)). A payload that no longer
  compiles is the one failure the anchors cannot catch.
- **Platform.** The shader redirect's marker assumes Windows separators, so on Linux the shaders
  would stay stock (inferred; see [Shader file redirect](features/shader-file-redirect.md)). The
  `{ASSETS}\...` texture paths in the XML are portable, because `Mod.GetPath` turns backslashes into
  `/`. The release zip is built to extract on Linux; running the mod there was not tested.
- **KittenExtensions (Continued).** Its transpiled loaders look every `mod.toml` file up by its
  original full path in a document that its `ModLibrary.PrepareAll` prefix built
  ([KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L28-L54)).[^kx-patcher]
  Real Atmospheres has already replaced Core's `Astronomicals.xml` and Sol template entries with
  shadow paths, so those lookups miss and return `null`. Read from the code, the Sol system headers,
  the Sol template and the Core body library then fail to load. The combination was not run in game;
  see [Core XML entry redirect](features/core-xml-entry-redirect.md).
- **Other mods.** Another mod that redirects Core's `Astronomicals.xml` or the Sol templates to its
  own copy overwrites one redirect with the other, unless it shares the
  [shared shadow](features/cross-mod-shared-shadow.md) as Real Surfaces does. Mods that read `p / rho`
  for the speed of sound (KSAero) inherit an altitude-dependent value.

## Secondary sources

- [README.md](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/README.md) and [CHANGELOG.md](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/CHANGELOG.md): player-facing feature list and a
  per-version history of what broke and why (decimal-comma XML, renamed clock, moved anchors).
- [CREDITS.md](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/CREDITS.md): source and licence of every shipped texture, and the scientific sources
  behind each body's numbers.
- The comments in [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs) and [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs): each edit
  records the visual problem it fixes, the measurement it uses and, often, the offline experiment that
  tuned it.
- [FORUM_DESCRIPTION.md](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/FORUM_DESCRIPTION.md) and [SPACEDOCK_DESCRIPTION.md](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/SPACEDOCK_DESCRIPTION.md):
  listing texts with screenshots referenced from `content/`.

[^ra-readme]: README.md
[^ra-modmain]: ModMain.cs
[^kx-patcher]: KittenExtensions: KittenExtensions/Patch/Patcher.cs
