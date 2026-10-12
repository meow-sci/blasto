---
type: Mod
title: Compendium
description: Celestial encyclopedia window that joins a cross-mod JSON catalog with the live star-system tree and, through Harmony patches on the navball, gauges and flight computer, lets fixed stars and barycenters act as navigation-only targets
mod: compendium
tags: [celestials, imgui, harmony, transpiler, targets, flight-computer, json, fonts, starmap, modmenu]
repo: Compendium
license: MIT
author: nanonestor
loader: starmap
mod_id: Compendium
---

Compendium, by nanonestor, opens a "Compendium" window with information on every body of the loaded
star systems: categories (Stars, Planets, moons by parent, asteroid and comet families and more),
descriptions, factoids and Wikipedia links from bundled JSON, statistics computed from the game's own
data, atmosphere graphs, and controls for orbit lines and camera focus. Its newest and most technical
part targets the interstellar update: the player can select an orbiting star as a normal target, and a
fixed star or root barycenter as a navigation-only target that drives the navball, the TGT gauges and
the flight computer's Toward/Away and TGT-frame hold without the game ever having an orbit for it.[^cmp-readme-md] [^cmp-mod-toml]

For modders the most reusable pieces are the root-target patches (a call-site transpiler, a
presence-test transpiler, and a prefix-plus-finalizer pair that edits the flight computer's input on
the solver threads), the cross-mod JSON data contract, and the small, complete examples of an ImGui
image texture and custom fonts.

## Install footprint

| Item | Value |
|---|---|
| `mod.toml` | `name = "Compendium"`, `version = "0.11.0"`, `author = "nanonestor"`, no `[StarMap]` table ([src/mod.toml](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/mod.toml#L1-L3)) |
| Manifest id | `Compendium`; without a `[StarMap]` table StarMap uses the manifest id as `EntryAssembly`, so it loads `Compendium.dll` ([StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L38-L60)) |
| Harmony id | `Compendium` ([src/Patcher.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Patcher.cs#L9)) |
| Files shipped | `Compendium.dll`, `Compendium.deps.json`, `mod.toml`, `settings.ini`, `ModMenu.Attributes.dll`, `Fonts/` (five Oxanium weights), `Compendium/` (25 JSON catalog files), `Photos/` (logo and icon) |

The csproj builds against the game folder and its `CopyCustomContent` target copies these files and
folders (plus a `Data/` glob that matches nothing) into `%USERPROFILE%\Documents\My Games\Kitten Space Agency\mods\Compendium` after every
build ([src/Compendium.csproj](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Compendium.csproj#L11-L59)).[^cmp-compendium-csproj] [^starmap-runtimemod]

## Loader model and entry class

StarMap mod. The entry class is `Compendium.Compendium`, a `partial class` marked `[StarMapMod]` and
spread over the `Cdm*.cs` files ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L12-L13)).[^cmp-compendiummain]

| Hook | Method | Fires | What Compendium does |
|---|---|---|---|
| `[StarMapImmediateLoad]` | `OnImmediateLoad(Mod mod)` | StarMap prefix on `Mod.PrepareSystems`, inside `ModLibrary.PrepareAll` in the `Program` constructor ([StarMap.Core/Patches/ModPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModPatches.cs#L11-L22)); the renderer, ImGui context, ImGui backend and game fonts already exist ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L794-L899)) | Load `settings.ini`, `Harmony.PatchAll`, add fonts, load the JSON catalog, upload the logo ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L1235-L1265)) |
| `[StarMapAllModsLoaded]` | `OnFullyLoaded()` | StarMap postfix on `ModLibrary.LoadAll` ([StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L17-L28)) | Build a provisional category tree (no universe yet) |
| `[StarMapAfterGui]` | `OnAfterUi(double dt)` | StarMap postfix on `Program.OnDrawUiViewports`, only while the HUD is shown ([StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L26-L36)) | Persist the open state, draw the pointer overlay and the window |
| `[StarMapUnload]` | `Unload()` | `ModLoader.Dispose`, after `StarMapCore.DeInit` has unpatched everything, at process exit ([StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35)) | Clear root targets, unpatch |
| `[ModMenuEntry]` | `DrawSubMenuEntry()` | Called by the ModMenu mod (unverified call site) | Toggle the window |

Harmony patches, all installed by `PatchAll` at immediate load:[^cmp-patcher] [^ksa-program]

| Target | Kind | Runs at | Feature |
|---|---|---|---|
| `ModLibrary.LoadAll` | postfix | `startup:all-mods-loaded` | Log line only ([src/Patcher.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Patcher.cs#L33-L38)) |
| `Astronomical.ShouldDrawLines(Astronomical, IViewport, Orbit?)` | postfix | `frame:pre-render`, nearest-orbit job | [Orbit line visibility and color](features/orbit-line-visibility-and-color.md) |
| `InputEvents.ApplyInputEvents` | prefix | `frame:input` | [Navigation-only root targets](features/root-navigation-targets.md) |
| `Vehicle.SetTarget` | prefix | `frame:input` (usually, from `ChangeTargetData.Apply`) | [Navigation-only root targets](features/root-navigation-targets.md) |
| `Universe.UnsetTargetCommand` | prefix | whenever it is called: UI code (`frame:draw-ui`) or the `unsettarget` console command (`frame:input`) | [Navigation-only root targets](features/root-navigation-targets.md) |
| `FlightComputer.ComputeControl` | prefix + finalizer | `physics:vehicle-solver` | [Flight-computer attitude tracking](features/flight-computer-root-attitude-tracking.md) |
| `Vehicle.UpdateNavBallMarkers`, `Vehicle.UpdateNavballData` | transpiler | `frame:apply-solvers` | [Navball target marker and TGT frame](features/navball-target-marker-and-tgt-frame.md) |
| `GaugeCanvas.IsContextVisible`, `GaugeButtonNavBallMode.IsDisabled`, `OnReleased`, `Vehicle.UpdateNavballData` | transpiler | `frame:draw-ui`; `IsDisabled` also `frame:render` (button `PackData`); `UpdateNavballData` `frame:apply-solvers` | [Navball target marker and TGT frame](features/navball-target-marker-and-tgt-frame.md) |
| `Vehicle.IsDisabled(VehicleReferenceFrame)` | postfix | `frame:draw-ui`, `frame:render` (button `PackData`) | [Navball target marker and TGT frame](features/navball-target-marker-and-tgt-frame.md) |
| `Vehicle.IsDisabled(FlightComputerAttitudeTrackTarget)`, `Vehicle.Hovered(FlightComputerAttitudeTrackTarget)` | postfix, prefix | `frame:draw-ui`; `IsDisabled` also `frame:render` (button `PackData`) | [Flight-computer attitude tracking](features/flight-computer-root-attitude-tracking.md) |
| `Program.UpdateGauges` | postfix | `frame:gauges` | [Closest approach to a root target](features/root-closest-approach-search.md) |

The patches span every phase of a frame and the solver threads; the
[game loop and mod lifecycle](../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
reference explains each point.

## Dependencies

| Dependency | How it is referenced | How it resolves at runtime |
|---|---|---|
| StarMap (`StarMap.API`) | NuGet 0.3.6, `PrivateAssets=all` ([src/Compendium.csproj](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Compendium.csproj#L62-L73)) | Not shipped; StarMap's mod load context returns assemblies already loaded in the default or game context first ([StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L24-L47)) |
| Harmony | NuGet `Lib.Harmony` 2.4.2, `PrivateAssets=all` | Not shipped; provided by StarMap |
| ModMenu | `ModMenu.Attributes.dll` by `HintPath`, checked into `src/` | Shipped in the mod folder; the ModMenu mod itself is optional |
| Game assemblies | `KSA`, `Brutal.*`, `BepuUtilities`, `Planet.Core`, `Planet.Render.Core` by `HintPath` to the game folder ([src/Compendium.csproj](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Compendium.csproj#L98-L149)) | The running game's copies |
| KittenExtensions, ShaderExtensions, native libraries | None | None |

StarMap resolves anything else through the mod's own `Compendium.deps.json`, which is why the build
copies it next to the DLL.[^starmap-modassemblyloadcontext]

## Architecture

| Subsystem | Files | Thread | Role |
|---|---|---|---|
| Entry and lifecycle | [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs) | main | Hooks, window layout, body/category/group pages, atmosphere graph |
| Data | [src/CdmLoadJsonInfo.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmLoadJsonInfo.cs), [src/CdmAttachInfoBodyJsonData.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmAttachInfoBodyJsonData.cs), [src/CdmCategoryLoader.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmCategoryLoader.cs) | main | JSON scan and precedence, game-derived statistics, category tree |
| Systems and lists | [src/CdmStarSystems.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmStarSystems.cs), [src/CdmPrintSelectedCategory.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmPrintSelectedCategory.cs), [src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs) | main | Roots and stars, body lists, lookups, orbit-line and target helpers |
| Presentation extras | [src/CdmLoadFonts.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmLoadFonts.cs), [src/CdmLoadPhotos.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmLoadPhotos.cs), [src/CdmBodyPointer.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmBodyPointer.cs), [src/CdmSettings.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmSettings.cs) | main | Fonts, logo texture, pointer overlay, `settings.ini` |
| Patches | [src/Patcher.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Patcher.cs), [src/RootTargetPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetPatches.cs), [src/RootTargetFlightComputerPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetFlightComputerPatches.cs) | main, vehicle solver workers, nearest-orbit worker | Orbit lines and root-target integration |
| Root-target state | [src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs), [src/RootClosestApproach.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootClosestApproach.cs) | main and workers | Session-only target tables, request queue, closest-approach search |

Data flow: JSON is read at immediate load; the first UI frame with a universe matches it to bodies and
builds the category tree; UI clicks change orbit-line state directly and queue target changes, which
the `ApplyInputEvents` prefix applies at the frame's input point; the vehicle solver threads read the
root target through the burn-plan table; the navball and gauges read it on the main thread. Because
the solver's flight-computer copy is written back over the vehicle's at every `ApplyVehicleSolvers`,
Compendium makes all of its flight-computer writes in the input window, before the next snapshot.

## Features

| Feature | Category | Lifecycle |
|---|---|---|
| [Celestial compendium window](features/celestial-info-window.md) | ui | startup:all-mods-loaded, frame:draw-ui |
| [Star systems and the body tree](features/star-systems-and-body-tree.md) | celestials | frame:draw-ui |
| [Cross-mod JSON catalog](features/cross-mod-json-catalog.md) | assets | startup:immediate-load, frame:draw-ui |
| [Body statistics derived from live game data](features/derived-body-properties.md) | celestials | frame:draw-ui |
| [Atmosphere density and pressure graphs](features/atmosphere-profile-graph.md) | ui | frame:draw-ui |
| [Custom fonts in the ImGui atlas](features/custom-fonts-in-imgui.md) | ui | startup:immediate-load, frame:draw-ui, frame:prepare |
| [Logo image as an ImGui texture](features/logo-image-as-imgui-texture.md) | rendering | startup:immediate-load, frame:draw-ui |
| [Pointer lines to the selected body](features/body-pointer-overlay.md) | ui | frame:draw-ui |
| [Orbit line visibility and color](features/orbit-line-visibility-and-color.md) | flight-and-orbits | frame:draw-ui, frame:pre-render |
| [Target orbiting bodies through the game's settarget command](features/native-target-selection.md) | flight-and-orbits | frame:draw-ui, frame:input |
| [Navigation-only root targets](features/root-navigation-targets.md) | flight-and-orbits | frame:draw-ui, frame:input, event:target-changed, event:starmap-unload |
| [Navball target marker and TGT frame for a target without an orbit](features/navball-target-marker-and-tgt-frame.md) | patching | startup:immediate-load, frame:apply-solvers, frame:draw-ui, frame:render |
| [Flight-computer attitude tracking against a root target](features/flight-computer-root-attitude-tracking.md) | flight-and-orbits | physics:vehicle-solver, frame:apply-solvers, frame:input, frame:draw-ui, frame:render |
| [Closest approach to a root target](features/root-closest-approach-search.md) | flight-and-orbits | frame:gauges |
| [settings.ini persistence](features/settings-ini-persistence.md) | persistence | startup:immediate-load, frame:draw-ui |
| [ModMenu entry that toggles the window](features/modmenu-window-toggle.md) | ui | frame:draw-ui |
| [Local decompiled game source refreshed with ilspycmd](features/decompiled-source-refresh.md) | testing-and-tooling | none (development time) |

## Build, test and tooling

- .NET 10 (`net10.0`), nullable and unsafe code enabled. The game folder defaults to
  `C:\Program Files\Kitten Space Agency` (`KSAFolder`) and the deploy folder to the user mods folder
  (`ModsFolder`), both Windows paths ([src/Compendium.csproj](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Compendium.csproj#L3-L28)).
- Every build copies the default `settings.ini` into the deployed mod, resetting the test copy.
- There are no automated tests. `tools/Refresh-Decompiled.ps1` keeps a local decompiled copy of the
  game for reference (see [the tooling feature](features/decompiled-source-refresh.md)).

## Compatibility

- Neither the README nor `mod.toml` names a target KSA build. The latest commit adds star and
  barycenter targeting and the previous one adapts to the interstellar update (multiple star systems).
- Claims in this bundle were checked against decompiled build 2026.10.10.5554. Every Harmony target
  exists; the IL of the five transpiled methods contains exactly the one call or null test each
  transpiler expects; `ShouldDrawLines` binds to the `IViewport` overload by assignability (the
  attribute names `IGameViewport`). None of the patched methods carries `AggressiveInlining` (their
  implementation flags are plain IL), so no caller can bypass the patches by inlining an attribute-marked
  body. `[StarMapImmediateLoad]` takes the `Mod` parameter StarMap passes, so it does run.
- Breakage to expect: renames of the private `Vehicle` members (`UpdateNavBallMarkers`,
  `UpdateNavballData`, `IsDisabled`, `Hovered`), a second `NavigationTarget.Create` call or a different
  null-test shape in a transpiled method, a change in how the solver copies `FlightComputer.BurnPlan`, a
  renamed `UpdateGauges` parameter, or a new `ShouldDrawLines` overload. Any patch failure aborts
  `PatchAll` inside `OnImmediateLoad` and also skips fonts, JSON data and the logo.
- Interaction with other mods: any mod that also transpiles the navball update or prefixes
  `UnsetTargetCommand` shares those call sites; Compendium's `UnsetTargetCommand` prefix skips the
  original for vehicles with a root target.

## Secondary sources

- [README.md](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/README.md): the player-facing description of root targets, their limits, and the closest-approach
  estimate. Read it first.
- [.github/copilot-instructions.md](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/.github/copilot-instructions.md): JSON catalog rules (prefixes, precedence, duplicate ids) and a
  short description of the multi-star-system model.[^cmp-copilot-instructions-md]
- The decompiled-source workflow in [tools/Refresh-Decompiled.ps1](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/tools/Refresh-Decompiled.ps1).

[^cmp-readme-md]: Compendium: README.md
[^cmp-mod-toml]: Compendium: src/mod.toml
[^cmp-compendium-csproj]: Compendium: src/Compendium.csproj
[^starmap-runtimemod]: StarMap: StarMap.Core/ModRepository/RuntimeMod.cs
[^cmp-compendiummain]: Compendium: src/CompendiumMain.cs
[^cmp-patcher]: Compendium: src/Patcher.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^starmap-modassemblyloadcontext]: StarMap: StarMap.Core/ModRepository/ModAssemblyLoadContext.cs
[^cmp-copilot-instructions-md]: Compendium: .github/copilot-instructions.md
