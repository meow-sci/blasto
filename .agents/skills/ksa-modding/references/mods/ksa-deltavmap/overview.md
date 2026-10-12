---
type: Mod
title: KSA-DeltaVMap (DeltaVMap)
description: An auto-generated, interactive delta-v subway map and transfer-window planner that reads every star system of the loaded universe live, prices routes with closed-form patched conics, and adds an opt-in transfer-window overlay to the game's 3D map mode
mod: ksa-deltavmap
tags:
  - delta-v
  - subway-map
  - transfer-windows
  - interstellar
  - imgui
  - map-view
  - starmap
  - harmony
  - headless-tests
repo: KSA-DeltaVMap
license: MIT
author: Maximilian-Nesslauer
loader: starmap
mod_id: DeltaVMap
---

DeltaVMap draws a KSP-style delta-v "subway map" for Kitten Space Agency, generated live from
whatever universe is loaded: every star system, star, planet, moon, asteroid and comet, with no
per-system configuration. The map is rooted at the body the player's vessel is in, routes are
priced with closed-form patched conics (Hohmann transfers with Oberth-combined departure and
capture), clicking a body highlights the route with a per-segment breakdown and compares it with
the vessel's staged delta-v, and other star systems are reachable through an interstellar leg
priced at a chosen cruise speed. A transfer-window layer adds phase angles, countdowns, synodic
periods and ejection angles as a list, a polar phase clock and an opt-in overlay in the game's 3D
map mode.[^dvmap-readme]

The repository belongs to GitHub user Maximilian-Nesslauer, who also wrote KSA-AdvancedFlightComputer
and KSA-MeasureTools (both linked from the README). Licensed MIT. The README states it is validated
against KSA build 2026.10.10.5554 and tested with StarMap 0.4.6.

## Install footprint

| Item | Value |
|---|---|
| Folder | `Documents\My Games\Kitten Space Agency\mods\DeltaVMap\` |
| Files | `DeltaVMap.dll`, `mod.toml` (no assets, no XML, no shaders) |
| `mod.toml` | `name = "DeltaVMap"`, `[StarMap] EntryAssembly = "DeltaVMap"`[^dvmap-modtoml] |
| Manifest id | `DeltaVMap` (`[[mods]] id = "DeltaVMap" enabled = true` in `manifest.toml`) |
| UI entry | Top-level **Delta-V Map** menu, **Show Map**, in flight and in the editor |
| Test-only extra | `DeltaVMap.HarnessTests` (separate mod folder, never part of a release)[^harness-modtoml] |

## Loader model and lifecycle hooks

Plain StarMap mod: `[StarMapMod] public sealed class Mod` in `DeltaVMap/Mod.cs`, one Harmony
instance (`com.maxi.deltavmap`) that applies a single patch class.[^dvmap-mod]

| Hook | Where it fires (build 2026.10.10.5554) | What DeltaVMap does |
|---|---|---|
| `[StarMapAllModsLoaded] OnFullyLoaded` | StarMap's Harmony postfix on `ModLibrary.LoadAll`[^starmap-modlibrarypatches] | Warn if `VersionInfo.Current.VersionString != "v2026.10.10.5554"`; `CreateClassProcessor(typeof(Patch_MenuBar)).Patch()` in a try/catch |
| Harmony postfix `KSA.Program.DrawProgramMenusHook` | Inside `Program.DrawMenuBar`, after the stock menus: from `Program.OnDrawUiThreadSafe` for the main bar (after AfterGui), or from `GameViewport.DrawImGui` for a hovered secondary viewport's bar (before AfterGui); not while the bar is auto-hidden[^dvmap-patch-menubar][^ksa-gameviewport] | Draw the "Delta-V Map" menu; lazily create the window inside the ImGui frame |
| `[StarMapAfterGui] Draw(double dt)` | StarMap's Harmony postfix on `Program.OnDrawUiViewports`[^starmap-programpatcher] | `MapWindow.DrawActive(Program.MainViewport)`: everything (graph build, layout, routing, staged delta-v, transfer windows, 3D overlay) runs here; DEBUG builds also run the one-shot validation dumps |
| `[StarMapUnload] Unload` | `ModLoader.Dispose`, from `StarMapCore.DeInit` when StarMap shuts down, after StarMap's id-less `Harmony.UnpatchAll()`[^starmap-modloader][^starmap-core] | `UnpatchAll(id)` (a no-op by then), `MapWindow.ResetStatic`, `StagedDv.Reset`, `LogHelper.Reset` |

There is no `StarMapBeforeGui`, `StarMapAfterOnFrame`, physics hook or background thread. In
`Program.OnFrame` the AfterGui point comes after `PrepareFrame` (solver results applied, next
vehicle and orbit batches already dispatched to workers) and camera `OnFrame`, and before
`OnDrawUiThreadSafe` and `ImGui.Render`; all UI hooks are skipped while the HUD is hidden
(`Program.DrawUI == false`), so the window, the staged-delta-v reads and the 3D map-mode overlay
all stop until the HUD returns.[^ksa-program] The mod therefore only reads simulation state, on the
main thread, inside a live ImGui frame, and only while its window is shown.

## Dependencies

| Dependency | Kind | Resolution |
|---|---|---|
| StarMap | Runtime loader (README: tested 0.4.6) | Required; loads `EntryAssembly` |
| `StarMap.API` 0.3.6 | NuGet, build time | Attributes `[StarMapMod]`, `[StarMapAllModsLoaded]`, `[StarMapAfterGui]`, `[StarMapUnload]` |
| `Lib.Harmony` 2.4.2 | NuGet, build time | Not shipped in the release zip; StarMap itself carries Harmony |
| Game assemblies | `HintPath` `..\..\..\refs\game\*.dll`, `Private=false` | `KSA`, `Brutal.Core.Numerics`, `Brutal.Core.Common`, `Brutal.Core.Logging`, `Brutal.ImGui`, `CommunityToolkit.HighPerformance`, `Planet.Render.Core`[^dvmap-csproj] |
| HeadlessHarness | Test-only StarMap mod dependency | Sibling checkout, not in this repository |

No KittenExtensions, ShaderExtensions, ModMenu or native libraries. No inter-mod API is exported.

## Architecture

```
Universe.CurrentSystem --(reference latch)--> SystemGraph (forest per IIndependentRoot, BodyLadder per body)
                                               DvCache (EdgeDv per unordered body pair), ColorPalette
controlled vessel (Parent, Orbit) --> root choice + StateClassifier ("you are here")
SystemGraph + DvCache + root --> ReRooter --> VisualTree (StateNodes, Edges, Interstellar edges)
VisualTree --> VisualTreeAdapter --> LayoutEngine (3 modes, ImGui text metrics) --> SceneComposer (other systems)
click --> RouteFinder (LCA) --> RouteAccumulator (+ TransferBurns, InterstellarSection, StagedDv) --> panel
root siblings --> TransferWindows (+ DvCache) --> list, phase clock, map badges, 3D map-mode overlay
```

| Folder | Role |
|---|---|
| `Core/` | Debug flags, number formats, once-only logging, perf scopes, the stock planner bridge |
| `Dv/` | Closed-form math kernels (pure) and their game-typed readers: ladders, transfers, staged delta-v, interstellar, transfer windows |
| `Model/` | Physical forest, visual tree, re-rooting, edges, transfer windows, interstellar legs |
| `Layout/` | Game-type-free layout engine, scene composition, offline test helpers |
| `Render/` | `MapWindow` (orchestration, input), canvas renderer, glyphs, legend, panels, tooltips, overlays |
| `Route/` | Path finding, accumulation, route options |
| `Patches/` | The single menu-bar postfix |

Caching tiers: per loaded system (graph, transfer cache, palette), per root build (visual tree,
layout, scene parts, transfer-window constants), per frame (two live transfer-window fields, gated
staged delta-v, cached display strings). Settings are not persisted: every option lives on the
`MapWindow` singleton (`ViewOptions` says "All session-only (not persisted yet)") and the stock
`ImGuiWindow` base begins the window with `ImGuiWindowFlags.NoSavedSettings`.[^dvmap-viewoptions][^ksa-imguiwindow]

## Features

| Feature | Category | Lifecycle |
|---|---|---|
| [Menu-bar entry and stock ImGuiWindow subclass drawn from AfterGui](./features/map-window-and-menu-entry.md) | ui | startup:all-mods-loaded, frame:draw-ui, event:starmap-unload |
| [Live celestial forest of every star system, cached per loaded system](./features/live-celestial-system-graph.md) | celestials | frame:draw-ui |
| [Closed-form patched-conic transfer model](./features/patched-conic-transfer-model.md) | flight-and-orbits | frame:draw-ui |
| [Per-body state ladder and "you are here"](./features/body-state-ladder-and-you-are-here.md) | flight-and-orbits | frame:draw-ui |
| [Ego-centric re-rooting that follows the controlled vessel](./features/ego-rerooting-and-vessel-follow.md) | ui | frame:draw-ui |
| [Route finding and accumulation](./features/route-finding-and-accumulation.md) | flight-and-orbits | frame:draw-ui |
| [Interstellar routes and fuel](./features/interstellar-routes-and-fuel.md) | flight-and-orbits | frame:draw-ui |
| [Vehicle staged delta-v readout](./features/vehicle-staged-dv-readout.md) | vehicles-and-parts | frame:draw-ui |
| [Pannable, zoomable ImGui canvas that draws the subway map with draw-list primitives](./features/subway-map-canvas-rendering.md) | ui | frame:draw-ui, render:imgui |
| [Game-free subway-map layout engine with three modes](./features/three-layout-modes.md) | ui | frame:draw-ui |
| [Other star systems as separate graph parts joined by dashed interstellar connectors](./features/multi-system-scene-composition.md) | ui | frame:draw-ui |
| [Search, focus, isolate and minor-body aggregation for dense systems](./features/search-focus-isolate-and-aggregation.md) | ui | frame:draw-ui |
| [Panel rows built on the game's ConsoleWidgets kit](./features/stock-console-widget-panels.md) | ui | frame:draw-ui |
| [Transfer windows list and live polar phase clock](./features/transfer-windows-and-phase-clock.md) | flight-and-orbits | frame:draw-ui |
| [Transfer-window markers drawn into the game's 3D map mode](./features/map-mode-transfer-overlay.md) | flight-and-orbits | frame:draw-ui, render:imgui |
| [Open the stock Transfer Planner preselected on an interstellar plan](./features/stock-transfer-planner-bridge.md) | patching | frame:draw-ui |
| [Headless harness tests that run against the real game simulation](./features/headless-harness-tests.md) | testing-and-tooling | none (HeadlessHarness run, outside the frame) |
| [Offline test executable that links the mod's game-free kernels without KSA.dll](./features/offline-layout-and-math-tests.md) | testing-and-tooling | build time |
| [Debug-build validation dumps, perf scopes and once-only logging](./features/in-game-validation-dumps-and-perf.md) | testing-and-tooling | frame:draw-ui |
| [Shared MSBuild deploy targets, release zip and SpaceDock publishing](./features/mod-deploy-targets-and-release.md) | getting-started | build time |

## Build, test and tooling

- `DeltaVMap.slnx` holds the mod and the offline tests; `net10.0`, x64, nullable on.
- `build/ModDeploy.targets` (imported by the mod and the harness tests) deploys into the game's
  mods folder, writes a StarMap launch profile for fresh worktrees, names a running game that locks
  the DLL, and stamps which checkout deployed. `PackageRelease` zips `DeltaVMap/` in Release;
  `.github/workflows/spacedock-publish.yml` uploads release zips to SpaceDock with the compatible
  build read from `Mod.cs`.
- `DeltaVMap.Tests` is a console exe that source-links the pure kernels and the layout engine with
  no game reference and fails on any overlap or formula regression.
- `DeltaVMap.HarnessTests` runs under HeadlessHarness against the live game and compares the mod with
  stock's own private routines through reflection. The same author's KSA-AdvancedFlightComputer
  documents the harness workflow in its test README.[^afc-harness-readme]
- DEBUG builds run one-shot validation dumps and perf scopes from the AfterGui hook.

## Compatibility notes

- Targets KSA build 2026.10.10.5554 (`TestedGameVersion = "v2026.10.10.5554"`); a mismatch only logs
  a warning.
- Private game surface it depends on: `TransferPlanner` statics and `SetTransferInfo` (stock planner
  button, degrades to "open only"), `PartTree._derivedDirty` (staged delta-v race guard, degrades to
  not waiting), and the formulas it mirrors from `OrbitalTransfers.AlignmentTime` and
  `TransferPlanner.PlanInterstellarFuel`. The harness tests exist to catch drift in all of them.
- The empty `Program.DrawProgramMenusHook` is the only Harmony target; if it disappears the map is
  unreachable but the game keeps running.
- Known conflicts: none stated in the README.
- `StockPlannerBridge.cs` refers to a "README, game update maintenance" section that is not in the
  README at this ref.[^dvmap-bridge]

## Secondary sources

The repository has no `docs/` folder. The most useful reading beyond the code:

- `README.md`: feature list, screenshots of the three layouts and the 3D overlay, install steps.
- Header comments in nearly every file (`MapWindow.cs`, `StagedDv.cs`, `StockPlannerBridge.cs`,
  `SceneComposer.cs`, `TransferWindow.cs`, `InterstellarLegs.cs`) explain design decisions and the
  game internals they rely on; several were checked against the decompiled build for this bundle.
- `DeltaVMap.Tests/Program.cs` and `DeltaVMap.HarnessTests/*.cs` document expected values for stock
  bodies (Earth to Mars lead angle, Alpha Centauri capture, Barnard b arrival).

[^dvmap-readme]: KSA-DeltaVMap README.md
[^dvmap-mod]: DeltaVMap/Mod.cs
[^dvmap-modtoml]: DeltaVMap/mod.toml
[^dvmap-csproj]: DeltaVMap/DeltaVMap.csproj
[^dvmap-patch-menubar]: DeltaVMap/Patches/Patch_MenuBar.cs
[^dvmap-viewoptions]: DeltaVMap/Render/ViewOptions.cs
[^dvmap-bridge]: DeltaVMap/Core/StockPlannerBridge.cs
[^harness-modtoml]: DeltaVMap.HarnessTests/mod.toml
[^starmap-programpatcher]: StarMap.Core/Patches/ProgramPatcher.cs
[^starmap-modlibrarypatches]: StarMap.Core/Patches/ModLibraryPatches.cs
[^starmap-modloader]: StarMap.Core/ModRepository/ModLoader.cs
[^afc-harness-readme]: KSA-AdvancedFlightComputer tests/AdvancedFlightComputer.HarnessTests/README.md
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-imguiwindow]: KSA.ImGuiWindow (decompiled, build 2026.10.10.5554)
[^starmap-core]: StarMap.Core/StarMapCore.cs
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
