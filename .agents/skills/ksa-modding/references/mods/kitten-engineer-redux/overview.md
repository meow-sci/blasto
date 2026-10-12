---
type: Mod
title: Kitten Engineer Redux
description: Small StarMap mod in the spirit of Kerbal Engineer Redux that adds a staging and mass analysis panel to the vehicle editor and a configurable flight HUD with orbit, suicide-burn, planner, alert and telemetry-graph sections, all drawn from Harmony postfixes on the game's own UI methods
mod: kitten-engineer-redux
tags: [editor, flight-hud, delta-v, staging, imgui, harmony, modmenu, telemetry, starmap]
repo: Kitten-Engineer-Redux
license: MIT
author: BarneyTheGod
loader: starmap
mod_id: KittenEngineerRedux
---

Kitten Engineer Redux (KER) recreates the Kerbal Engineer Redux experience for Kitten Space Agency.
The source header credits BarneyTheGod (GitHub `BarneyCompiler`) and says a few helper snippets
were taken from Maximilian-Nesslauer's StageInfo and Advanced Flight Computer mods; the README notes
that generative AI was used to search decompiled game code in ILSpy and that most code is human
written.[^ker-mod] [^ker-readme]

What the player gets:

- **Vehicle editor:** a panel with total delta-v, wet and dry mass, part count, and a stage table
  (delta-v, burn time, TWR, fuel bar, expandable details and feed diagnostics), computed either from
  the game's own staging simulator or from KER's own estimate at a chosen altitude.
- **Flight:** a HUD with up to nine player-chosen stat tiles, threshold alerts, and sections for live
  thrust and TWR, fuel gauges, orbit, suicide burn, stages, orbit elements, a Hohmann planner, surface
  position and a telemetry history graph with CSV export. Any section can be popped out into its own
  window. Colours, text scale and layout are themable and saved.
- **Menu:** a "Kitten Engineer Redux" menu (inside ModMenu if installed, otherwise in the game menu
  bar) to show or hide the two windows.

For modders, the interesting parts are where it hooks (postfixes on `VehicleEditor.OnDrawUi`,
`Vehicle.OnDrawUi` and the empty `Program.DrawProgramMenusHook`, instead of StarMap's GUI attributes),
how it reuses `PartTree.PerformanceSequences` and when that cache is valid, and a compact
draw-list widget kit and chart that need no ImPlot.

## Install footprint

| Item | Value |
|---|---|
| Mod folder | `Documents\My Games\Kitten Space Agency\mods\KittenEngineerRedux\` |
| Files shipped | `KittenEngineerRedux.dll`, `mod.toml`, `ModMenu.Attributes.dll` ([dist/KittenEngineerRedux.zip](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/dist/KittenEngineerRedux.zip))[^ker-dist] |
| `mod.toml` | `name = "KittenEngineerRedux"` only, no `[StarMap]` table ([mod.toml](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/mod.toml))[^ker-modtoml] |
| Manifest id | `KittenEngineerRedux`: the game's `ModEntry` id is the mod folder name ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L355-L357)), and the `name` key is only the display name[^ksa-modlibrary] |
| Harmony id | `com.matthew.kittenengineerredux` |
| Data folder | `Documents\My Games\Kitten Space Agency\KittenEngineerReduxData\` (`settings.json`, `exports\*.csv`)[^ker-settingsstore] |
| Assets | None (no XML, GLB, shaders, fonts or sounds) |

With no `[StarMap]` table, StarMap uses the manifest id (the folder name, not the `name` key) as
`EntryAssembly` and loads `KittenEngineerRedux.dll` from the mod folder into its own
`ModAssemblyLoadContext`; a mod folder renamed by the player therefore silently stops loading
([StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L49-L63)).[^starmap-runtimemod]

## Loader model and entry class

StarMap mod. Entry class `KittenEngineerRedux.Mod` ([Mod.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/Mod.cs#L16-L37)):

| Hook | Method | Fires | What KER does there |
|---|---|---|---|
| `[StarMapAllModsLoaded]` | `OnFullyLoaded()` | StarMap's postfix on `ModLibrary.LoadAll`, before any universe exists ([StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L17-L28)) | `new Harmony(...).PatchAll(assembly)`: installs the three postfixes below |
| `[StarMapUnload]` | `Unload()` | `ModLoader.Dispose` at shutdown, after `StarMapCore.DeInit` has already called `UnpatchAll()` without an id, which removes every Harmony patch in the process ([StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35), [StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L143-L151)) | `UnpatchAll(id)` (a no-op by then), clears the analyzer's pooled collections |

All per-frame work runs from Harmony postfixes, every one of them inside `Program.OnDrawUiThreadSafe`
or a viewport's `DrawImGui` (`frame:draw-ui`):[^starmap-modlibrarypatches] [^starmap-modloader] [^starmap-starmapcore]

| Patch | Target | Called from | Drives |
|---|---|---|---|
| [Patch_EditorPanel.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/Patch_EditorPanel.cs) | postfix `VehicleEditor.OnDrawUi(IGameViewport)` | `Program.OnDrawUiThreadSafe` → `Editor?.OnDrawUi(MainViewport)` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3239)) | Editor panel |
| [Patch_FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/Patch_FlightHud.cs) | postfix `Vehicle.OnDrawUi(IGameViewport)`, filtered to main viewport and controlled vehicle | `Universe.OnDrawUi` → `CelestialSystem.OnDrawUi` per drawn object ([current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L364-L406)) | Flight HUD, telemetry, alerts |
| [StandaloneMenu.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/StandaloneMenu.cs) | postfix `Program.DrawProgramMenusHook()` | `Program.DrawMenuBar` inside `BeginMenuBar` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4005-L4020)) | Fallback menu when ModMenu does not claim the entry |

[^ker-patch-editorpanel] [^ker-patch-flighthud] [^ker-standalonemenu] [^ksa-program] [^ksa-celestialsystem]

## Dependencies

| Dependency | How it is referenced | How it resolves at runtime |
|---|---|---|
| StarMap (`StarMap.API`) | NuGet 0.3.6 | Provided by the loader; StarMap's load context returns assemblies already loaded in the default or game context first ([StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L24-L72))[^starmap-modalc] |
| Harmony | NuGet `Lib.Harmony` 2.4.2, `PrivateAssets=all` | Not shipped; StarMap's copy (also `Lib.Harmony` 2.4.2, [StarMap.Core/StarMap.Core.csproj](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMap.Core.csproj#L11))[^starmap-csproj] |
| ModMenu (optional) | NuGet `ModMenu.Attributes` 1.0.2 (the copy in the release zip is assembly version 1.0.2 with the two-argument `ModMenuEntryAttribute`) | `ModMenu.Attributes.dll` shipped next to the mod DLL and found by StarMap's `AssemblyDependencyResolver`; the ModMenu mod itself is optional and is not declared as a dependency |
| CommunityToolkit.HighPerformance | NuGet 8.4.2 | Not used directly by the source; the game ships its own copy |
| Game assemblies | `KSA`, `BepuUtilities`, `Brutal.Core.*`, `Brutal.Glfw`, `Brutal.ImGui`, `Brutal.ImGui.Extensions` from `C:\Program Files\Kitten Space Agency\`, `Private=false` ([KittenEngineerRedux.csproj](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/KittenEngineerRedux.csproj#L23-L60)) | The running game's copies[^ker-csproj] |
| KittenExtensions, ShaderExtensions, native libraries | None | None |

## Architecture

Everything runs on the main thread inside the ImGui frame; KER owns no threads, timers or jobs.

| Subsystem | Files | Role |
|---|---|---|
| Entry and patches | `Mod.cs`, `Patch_EditorPanel.cs`, `Patch_FlightHud.cs`, `StandaloneMenu.cs` | Install patches; forward UI calls |
| Analysis (`KittenEngineerRedux.Analysis`) | `SequenceAnalyzer.cs`, `MassAnalyzer.cs`, `MassHelpers.cs`, `FlowHelpers.cs`, `ActiveEngineThrust.cs`, `EnvironmentHelpers.cs`, `SuicideBurnCalculator.cs`, `OrbitSummary.cs`, `OrbitPlanner.cs` | Pure computations over game state, returning records |
| Editor (`KittenEngineerRedux.Editor`) | `EditorPanel.cs` | Editor window |
| Flight (`KittenEngineerRedux.Flight`) | `FlightHud.cs`, `HudWidgets.cs`, `HudAlerts.cs`, `TelemetryHistory.cs`, `BuildMarker.cs` | Per-frame data record, tiles, alerts, history buffer |
| UI (`KittenEngineerRedux.UI`) | `PanelKit.cs`, `SectionHost.cs`, `GraphView.cs`, `StageListView.cs`, `MenuContent.cs`, `ModMenuIntegration.cs`, `SettingsStore.cs` | Draw-list widgets, sections, chart, menus, settings |

Data flow per flight frame: the postfix fires for the controlled vehicle → `SettingsStore.Tick()` →
orbit summary → suicide burn → stage analysis (native cache or estimate) → live thrust and TWR →
telemetry sample → alert evaluation → window and sections drawn from the cached `FrameData` →
popped-out section windows ([FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L96-L200)). The editor frame is the same
shape with the editor's `PartTree` and the home body ([EditorPanel.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/EditorPanel.cs#L25-L90)).[^ker-flighthud] [^ker-editorpanel]

The native staging numbers are only as fresh as the game's `SequencePerformanceList`. In the editor
KER calls `RecomputeIfDirty()` itself on the main thread during the draw, so the game's own
background job, queued after the draw-UI block only when the list is still dirty, finds it clean and
is skipped. In flight the game recomputes only on a vehicle-solver worker, for the controlled vehicle,
while the `Sequence` or `EngineControl` gauge canvas is enabled
([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L793-L796),
[current/decomp/KSA/ResourceGroups.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ResourceGroups.cs#L15)).
Both canvases start enabled ([current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L143)), so with the default UI the
cache is refreshed every vehicle step. With both turned off nothing recomputes it in flight: a new
or edited list stays dirty, and KER falls back to its own vacuum estimate, while a list that was
clean keeps the numbers from its last recompute, because burning propellant never calls
`SetDirty`.[^ker-sequenceanalyzer] [^ksa-seqperflist] [^ksa-physicsbubble] [^ksa-resourcegroups] [^ksa-gaugecanvas]

## Features

| Feature | Category | Lifecycle |
|---|---|---|
| [Editor panel injected into the vehicle editor](features/editor-panel-injection.md) | ui | frame:draw-ui, editor:draw-ui |
| [Native stage performance readout](features/native-stage-performance-readout.md) | vehicles-and-parts | frame:draw-ui, editor:draw-ui |
| [Custom stage delta-v estimator](features/custom-stage-delta-v-estimator.md) | vehicles-and-parts | frame:draw-ui, editor:draw-ui |
| [Live and rated engine thrust with TWR](features/live-engine-thrust-and-twr.md) | vehicles-and-parts | frame:draw-ui, editor:draw-ui |
| [Flight HUD on the controlled vehicle](features/flight-hud-controlled-vehicle-postfix.md) | ui | frame:draw-ui |
| [HUD stat tiles and threshold alerts](features/hud-widgets-and-threshold-alerts.md) | ui | frame:draw-ui |
| [Suicide-burn calculator](features/suicide-burn-calculator.md) | flight-and-orbits | frame:draw-ui |
| [Orbit readouts and Hohmann estimate](features/orbit-readouts-and-hohmann-estimate.md) | flight-and-orbits | frame:draw-ui |
| [Telemetry history and CSV export](features/telemetry-history-and-csv-export.md) | external-io | frame:draw-ui |
| [Draw-list line graph](features/draw-list-line-graph.md) | ui | frame:draw-ui |
| [Draw-list widget kit with pop-out sections](features/draw-list-widget-kit.md) | ui | frame:draw-ui, editor:draw-ui |
| [ModMenu entry with a standalone menu fallback](features/modmenu-entry-with-standalone-fallback.md) | ui | frame:draw-ui |
| [JSON settings with autosave](features/json-settings-autosave.md) | persistence | frame:draw-ui, editor:draw-ui |
| [Minimal mod.toml, post-build deploy and release zip](features/dist-zip-and-post-build-deploy.md) | getting-started | startup:all-mods-loaded |

## Build, test and tooling

- `dotnet build` on `KittenEngineerRedux.csproj` (`net10.0`); game references are absolute
  `C:\Program Files\Kitten Space Agency\` hint paths, and a `PostBuild` target copies the DLL and
  `ModMenu.Attributes.dll` to one hard-coded user's mods folder ([KittenEngineerRedux.csproj](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/KittenEngineerRedux.csproj#L68-L83)).
- `KSA_Source/` (a local ILSpy export) is git-ignored and excluded from compilation.
- There are no tests, CI or packaging scripts; `dist/KittenEngineerRedux.zip` is committed by hand.
- `BuildMarker.Stamp` (`v0.5.7` at the pinned commit) is shown in the HUD to identify the loaded
  build.[^ker-buildmarker]

## Compatibility

- The repository states no target KSA build. Claims in this bundle were checked against decompiled
  build 2026.10.10.5554. Commit `9ad3975` (2026-10-02, "Update KSA API compatibility and ModMenu
  fallback") moved the two UI postfixes from `Viewport` to `GameViewport` parameters and the menu
  fallback to `IGameViewportLifecycle.SetMenuBarInUse`; the stale `Patch_StandaloneMenu` file (added
  in `ed306fc`, no `.cs` extension, so not compiled) still shows the older
  `Program.MainViewport.MenuBarInUse = true` form, which matches builds up to 2026.8.22.5348, where
  `MainViewport` was a `Viewport` with a public `MenuBarInUse` field.[^ker-patch-standalonemenu]
- Both UI postfixes declare a `GameViewport inViewport` parameter while the targets take
  `IGameViewport inViewport` in every decompiled build from 2026.9.7.5402 to 5554. Harmony binds
  injected parameters by name; in Lib.Harmony 2.4.2 (the version StarMap ships) the generated call
  loads the original argument with a plain `ldarg` and no type check or cast when both types are
  reference types (`MethodCreatorTools.EmitCallParameter`, read from the decompiled 2.4.2 assembly), so
  the patch binds and runs. It is sound only because `GameViewport` is the sole `IGameViewport`
  implementation in build 5554; declare `IGameViewport` in your own patches.[^ksa-vehicleeditor] [^ksa-vehicle]
- The committed release zip contains an older DLL (stamp `v0.5.2`, last changed in commit `8635580`)
  than the source (`v0.5.7`), so players installing from `dist/` do not get the analysed behaviour.
- Breakage to expect: renames of `VehicleEditor.OnDrawUi`, `Vehicle.OnDrawUi` or
  `Program.DrawProgramMenusHook`; changes to `SequencePerformance` fields or the dirty and recompute
  rules of `SequencePerformanceList`; changes to `RocketControllerData`, `FlowOrder<T>` or
  `ResourceManager.ConsumptionOrder` used by the estimator.
- Other mods that also postfix `Vehicle.OnDrawUi` or `DrawProgramMenusHook` coexist; menus add in
  patch order. gatOS uses the same fallback hook but detects ModMenu with `ModLibrary.Find("ModMenu")`
  rather than an attribute-driven flag ([gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L192-L213)).[^gatos-modgame]

## Secondary sources

- [README.md](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/README.md): one paragraph plus the AI-use note; no install or version information.
- The header comment of [Mod.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/Mod.cs) credits the borrowed helper snippets.
- The git history (`git log` in the checkout) is the best changelog: commit messages track the
  orbit planner, telemetry history, native staging readouts, pop-out windows and colour settings.

[^ker-mod]: Mod.cs
[^ker-readme]: README.md
[^ker-dist]: dist/KittenEngineerRedux.zip
[^ker-modtoml]: mod.toml
[^ker-settingsstore]: SettingsStore.cs
[^starmap-runtimemod]: StarMap.Core/ModRepository/RuntimeMod.cs
[^starmap-modlibrarypatches]: StarMap.Core/Patches/ModLibraryPatches.cs
[^starmap-modloader]: StarMap.Core/ModRepository/ModLoader.cs
[^ker-patch-editorpanel]: Patch_EditorPanel.cs
[^ker-patch-flighthud]: Patch_FlightHud.cs
[^ker-standalonemenu]: StandaloneMenu.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled, build 2026.10.10.5554)
[^starmap-modalc]: StarMap.Core/ModRepository/ModAssemblyLoadContext.cs
[^ker-csproj]: KittenEngineerRedux.csproj
[^ker-flighthud]: FlightHud.cs
[^ker-editorpanel]: EditorPanel.cs
[^ker-sequenceanalyzer]: SequenceAnalyzer.cs
[^ksa-seqperflist]: KSA.SequencePerformanceList (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^ker-buildmarker]: BuildMarker.cs
[^ker-patch-standalonemenu]: Patch_StandaloneMenu
[^ksa-vehicleeditor]: KSA.VehicleEditor (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^gatos-modgame]: gatOS.GameMod/Game/Mod.Game.cs
[^starmap-starmapcore]: StarMap.Core/StarMapCore.cs
[^starmap-csproj]: StarMap.Core/StarMap.Core.csproj
[^ksa-resourcegroups]: KSA.ResourceGroups (decompiled, build 2026.10.10.5554)
[^ksa-gaugecanvas]: KSA.GaugeCanvas (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
