---
type: Mod
title: Oh My Stars
description: StarMap mod that draws constellation lines, names, IAU boundaries and an RA/Dec grid on the sky from Stellarium and AT-HYG data, with a star browser, navball marker, orient-to-star mode and a star field editor
mod: ohmystars
tags: [sky, constellations, stars, imgui, drawlist, harmony, flight-computer, navball, starmap, modmenu]
repo: OhMyStars
license: GPL-2.0
author: nanonestor
loader: starmap
mod_id: OhMyStars
---

Oh My Stars draws constellations on KSA's sky: asterism lines and names from 53 Stellarium sky
cultures, IAU constellation boundaries, star names and an RA/Dec grid, all hidden behind planets and
vessels. Around that it adds a star browser over the AT-HYG catalog, a pointer from its window to the
selected star, a custom navball marker, an "Orient to Star" attitude mode built on the stock flight
computer, and a Star Editor that restyles the game's background stars and swaps star binary files at
runtime.[^oms-readme-md]

The mod is by nanonestor (`author` in `mod.toml`) and is, per the README, a fork and continuation of
StellariumCatalog by DavidK0, done with the original author's blessing; NOTICE.txt credits the
original code to David Krug.[^oms-notice-txt] The git history shows StellariumCatalog 1.0.0 on
2026-06-19, then nanonestor's rework (August 2026), the rename to Oh My Stars (2026-09-05), the
navball marker, orientation mode and RA/Dec grid (September), and the merge of the formerly separate
StarsEdit star editor (2026-10-02, `version = "1.3.0"`). The README carries an AI-assistance
disclaimer.

For modders the headline technique is sky drawing: catalog RA/Dec converted into the game's ecliptic
axes, placed on the same 19.5-unit camera-centred sphere the game uses for its star sprites, projected
with `Camera.EgoToScreen` and stroked into an input-less full-screen ImGui window from
`[StarMapAfterGui]`. The mod is also a dense example of patching the flight computer safely and of
reflecting into the instanced star renderer.

## Install footprint

| Item | Value |
|---|---|
| Mod folder | `Documents\My Games\Kitten Space Agency\mods\OhMyStars\` (written by the build) |
| `mod.toml` | `name="Oh My Stars"`, `version = "1.3.0"`, `author = "nanonestor"`; no `[StarMap]` table ([packaging/mod.toml](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/packaging/mod.toml)) |
| Manifest id | `OhMyStars` (folder name); StarMap then loads `OhMyStars.dll` by default |
| Code | `OhMyStars.dll`, `OhMyStars.deps.json`, `ModMenu.Attributes.dll` |
| Data | `skycultures/<53 folders>/constellationship.fab` + `constellation_names.eng.fab`, `athyg_32_reduced_m10.csv` (69 MB), `lines_in_20.txt` (boundaries) |
| Config | `settings.ini` (shipped defaults, rewritten by the mod), `starsedit_settings.ini` (created at runtime), `saves/<id>/OhMyStars_settings.toml` (per save, empty) |
| Legal | `NOTICE.txt`, `licenses/GPL-2.0.txt`, `licenses/CC_BY-SA_4.0.txt`; repository `LICENSE` is GPL-2.0 |
| Harmony ids | `nanonestor.ohmystars`, `nanonestor.ohmystars.starsedit` |

No XML assets, shaders or textures are added; everything visual is ImGui geometry or a change to the
stock star instances.

## Loader model and entry class

StarMap mod. The entry class `OhMyStars.OhMyStarsEntryPoint` is marked `[StarMapMod]`; all hooks are
static methods, which StarMap accepts because it collects them with `Type.GetMethods()` and checks
only the return type and parameters ([src/OhMyStarsEntryPoint.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsEntryPoint.cs#L9-L55),
[StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L129-L162)).[^oms-ohmystarsentrypoint-cs] [^starmap-modrepository-runtimemod-cs]

| Hook | Method | Fires | What Oh My Stars does |
|---|---|---|---|
| `[StarMapBeforeMain]` | `OnBeforeMain()` | During StarMap's mod initialisation, before KSA's entry point | Installs the star-technique patches (`AddInstance` prefix, constructor prefix) |
| `[StarMapAllModsLoaded]` | `OnFullyLoaded()` | Postfix on `ModLibrary.LoadAll`, inside the `Program` constructor ([StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L17-L28), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L942)) | Save and attitude patches; loads catalog, sky cultures, boundaries, settings; snapshots star binaries |
| `[StarMapBeforeGui]` | `OnBeforeGui(double)` | Prefix on `Program.OnDrawUiFrame`, only when the HUD is shown | Draws the two windows, persists window open state |
| `[StarMapAfterGui]` | `OnAfterUi(double)` | Postfix on `Program.OnDrawUiViewports`, only when the HUD is shown ([StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L14-L36)) | Draws the sky overlay, star pointer and navball marker |
| `[ModMenuEntry("Oh My Stars")]` | `DrawMenu()` | Called by ModMenu | Two menu items toggling the windows |
| Harmony prefix | `Vehicle.PrepareWorker` | Main thread, before each vehicle solver dispatch | Re-applies "Orient to Star" targets |

The frame placement of these hooks is described in
[Game loop and mod lifecycle](../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md).
`[StarMapAllModsLoaded]` runs at `Program.cs` line 942, before the star technique is created (line
1049) and populated (line 1053), and before any universe or vehicle exists.[^ksa-program-cs]

## Dependencies

| Dependency | How it is referenced | How it resolves at runtime |
|---|---|---|
| StarMap (`StarMap.API` 0.3.6) | NuGet ([src/OhMyStars.csproj](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStars.csproj#L50-L55)) | Provided by the loader |
| Harmony (`Lib.Harmony` 2.4.2) | NuGet | The copy StarMap already loaded (the mod's load context checks the default and game contexts first) |
| ModMenu (`ModMenu.Attributes` 1.0.2) | Local DLL by `HintPath`; not on nuget.org per the csproj comment | DLL shipped in the mod folder; ModMenu itself must be installed separately (README) and is not declared in `mod.toml` |
| Game assemblies | `KSA`, `Brutal.Core.Common`, `Brutal.Core.Logging`, `Brutal.Core.Numerics`, `Brutal.ImGui`, `BepuPhysics`, `BepuUtilities`, `Planet.Render.Core` from `$(KSAGameDir)` | The running game's copies |
| KittenExtensions, ShaderExtensions, native libs | None | None |

## Architecture

| Subsystem | Files | Thread | Role |
|---|---|---|---|
| Entry and hooks | [src/OhMyStarsEntryPoint.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsEntryPoint.cs) | Main | Lifecycle wiring |
| Sky overlay | [src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs), [src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs), [src/IAUConstellationsRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/IAUConstellationsRenderer.cs), [src/SkyMarkingsRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyMarkingsRenderer.cs), [src/Math.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/Math.cs) | Main (ImGui) | Data load, projection, occlusion, drawing |
| Navball marker | [src/NavballMarkerRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/NavballMarkerRenderer.cs) | Main (ImGui) | Star glyph on the navball |
| Windows | [src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs), [src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs) | Main (ImGui) | Settings, Information tab, orientation control, Star Editor |
| Attitude patches | [src/StarOrientationObserver.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarOrientationObserver.cs) | Main; the `SetNullRot` and MMU prefixes also run on solver workers | Keep and release "Orient to Star" |
| Star technique patches | [src/StarsEditPatcher.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditPatcher.cs) | Main | Restyle and resize the instanced star field |
| Persistence | [src/ModPaths.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/ModPaths.cs), [src/Settings.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/Settings.cs), [src/SaveScopedSettingsStore.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SaveScopedSettingsStore.cs), [src/SaveLoadObserver.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SaveLoadObserver.cs) | Main | ini files, per-save sidecar |

Data flow: at load the catalog CSV becomes a `hip -> ecliptic unit vector` map; switching sky culture
resolves its `.fab` segments against that map (filtered to stars in the stock star binary). Each frame
`[StarMapAfterGui]` rebuilds the occluder list from the main camera's system, projects every segment
and label on the 19.5 sphere and draws them, then the pointer and navball marker. Orientation targets
flow the other way: the window records a star per vehicle, and the `PrepareWorker` prefix turns it
into flight-computer fields just before the physics snapshot.

## Features

| Feature | Category | Lifecycle |
|---|---|---|
| [Constellation lines and labels on the sky](features/sky-overlay-lines-and-labels.md) | rendering | frame:draw-ui, startup:all-mods-loaded |
| [Occlusion by bodies](features/overlay-occlusion-by-bodies.md) | rendering | frame:draw-ui |
| [RA/Dec grid](features/ra-dec-grid.md) | rendering | frame:draw-ui |
| [Star instance restyle patch](features/star-instance-restyle-patch.md) | rendering | startup:starmap-before-main, startup:all-mods-loaded, frame:draw-ui |
| [Stellarium sky cultures](features/stellarium-sky-cultures.md) | celestials | startup:all-mods-loaded, frame:draw-ui |
| [AT-HYG star catalog and star information](features/hyg-star-catalog.md) | celestials | startup:all-mods-loaded, frame:draw-ui |
| [Star binary switcher](features/star-binary-switcher.md) | celestials | startup:all-mods-loaded, frame:draw-ui |
| [Star pointer](features/star-pointer.md) | ui | frame:draw-ui |
| [Navball star marker](features/navball-star-marker.md) | ui | frame:draw-ui |
| [ModMenu entry and game-styled windows](features/modmenu-entry-and-console-style-windows.md) | ui | frame:draw-ui |
| [Game-styled popups and toasts](features/game-styled-popups-and-toasts.md) | ui | frame:draw-ui |
| [Orient a vessel to a star](features/orient-to-star.md) | flight-and-orbits | frame:draw-ui, frame:dispatch-solvers, physics:vehicle-solver |
| [Attitude-mode hand-off patches](features/attitude-mode-handoff-patches.md) | patching | startup:all-mods-loaded, frame:input, frame:dispatch-solvers, physics:vehicle-solver |
| [settings.ini persistence](features/ini-settings-persistence.md) | persistence | startup:all-mods-loaded, frame:draw-ui |
| [Save-scoped settings sidecar](features/save-scoped-settings-sidecar.md) | persistence | startup:all-mods-loaded, event:save, event:load |
| [Mod root and packaging](features/mod-root-and-packaging.md) | getting-started | startup:all-mods-loaded |

## Build, test and tooling

- `dotnet build src/OhMyStars.csproj` (or the `OhMyStars.slnx` solution) on Windows. The game path
  comes from `KSAGameDir` (environment variable or a git-ignored `OhMyStars.csproj.user.props`), and an
  after-build target copies the mod files into the Documents mods folder, always overwriting
  `settings.ini` with `src/settings.ini` ([src/OhMyStars.csproj](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStars.csproj#L10-L48)).[^oms-ohmystars-csproj]
- There are no tests and no CI. `.vscode/settings.json` auto-approves a few terminal commands,
  including an inline C# program that compares the mod's celestial pole with Earth's in-game pole
  ([.vscode/settings.json](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/.vscode/settings.json)).
- Third-party data and its licences are tracked in `NOTICE.txt`; keep it with any redistribution.

## Compatibility

- The README names no KSA build. Every engine claim in this bundle was checked against the decompiled
  build 2026.10.10.5554.
- Breakage patterns to expect:
  - Reflection on non-public members: `Program._starStarTechnique`, `ModLibrary.Lookup`,
    `Mod.StarBinaries`, `InstancedRenderTechnique<T>._maxInstances`/`Instances`,
    `InstancedStarTechnique._instanceBuffer`/`_instanceMemory`/`CreateInstanceBuffer`.
  - Patches by string name: `KittenEva.HandleKittenAction`,
    `PhysicsBubble.ApplyMmuAttitudeTarget`/`ResetMmuAttitudeTarget`.
  - Copied constants: the navball marker's sizes, rim fade, `Body2Screen` and centre-indicator
    geometry from `NavballMarkers.glsl`, `GaugeNavball.frag` and `NavBallRenderer`.
  - The asterism filter's bit-exact match against the stock star binary
    `briars_binary_dimmer_better_99k_stars.bin`.
  - `FlightComputer.TrackTarget`, `RateHold` and `SetNullRot` are `AggressiveInlining`, so their
    prefixes may miss inlined calls in an optimized build; this build's `KSA.dll` is marked
    `DisableOptimizations`, so they most likely fire, including on worker threads for `SetNullRot`.
- Other mods: KSA_AircraftHUD uses the same overlay window name `HUDFullscreenWindow`; any mod that
  adds stars through `Mod.StarBinaries` loses them when the Star Editor applies a selection.

## Secondary sources

- [README.md](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/README.md): install steps, credits, data sources, and the note about `settings.ini`
  defaults.
- [NOTICE.txt](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/NOTICE.txt): provenance and licences of the sky cultures, catalog and boundary data.
- Code comments are unusually explanatory, especially in
  [src/NavballMarkerRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/NavballMarkerRenderer.cs) (how the stock marker shader and gauge canvas work),
  [src/StarOrientationObserver.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarOrientationObserver.cs) and [src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs) (why each attitude path
  must be caught and why `CustomAttitudeTarget` must be restored), and
  [src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs) (occlusion and the star sphere).

[^oms-readme-md]: OhMyStars README.md
[^oms-notice-txt]: OhMyStars NOTICE.txt
[^oms-ohmystarsentrypoint-cs]: OhMyStars src/OhMyStarsEntryPoint.cs
[^starmap-modrepository-runtimemod-cs]: StarMap StarMap.Core/ModRepository/RuntimeMod.cs
[^ksa-program-cs]: KSA.Program (decompiled, build 2026.10.10.5554)
[^oms-ohmystars-csproj]: OhMyStars src/OhMyStars.csproj
