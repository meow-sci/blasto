---
type: Mod
title: KSA Broadcast Telemetry Overlay
description: StarMap mod that draws a SpaceX-webcast-style telemetry bar over KSA flight with live gauges, an engine diagram built from real nozzle positions, a mission clock, inferred flight events and planned burns, while hiding and restoring the stock HUD
mod: ksa-public-telemetry
tags: [ui, imgui, overlay, telemetry, hud, broadcast, starmap]
repo: KSA-Public-Telemetry
license: MIT
author: elevatorctln
loader: starmap
mod_id: KSATelemetryOverlay
---

KSA Broadcast Telemetry Overlay (Spacedock title "Webcast Telemetry Overlay", repository
`KSA-Public-Telemetry` by elevatorctln) turns KSA flight into a launch broadcast: a bottom bar with
speed and altitude gauges, a T+ clock with a mission timeline, a g-force gauge and an engine diagram
generated from where the active stage's nozzles really are, plus event callouts such as MAX Q, MECO and
STAGE SEP. It is a work in progress and the author says plainly that the event detection is not
totally reliable and that the mod will break with game updates.[^kpt-readme]

For modders it is a compact, well-factored example of a pure ImGui mod: one sampling pass per frame
into a plain snapshot, a renderer that draws everything on the foreground draw list, a reflection-only
Harmony patch for the F2 case, a vendored ModMenu attribute with a fallback window, and a forgiving
JSON config. It touches no Vulkan, no physics and no XML assets.

## Install footprint

| Item | Value |
|---|---|
| Mod folder | `Documents/My Games/Kitten Space Agency/mods/<folder>/` (README: extract the release there)[^kpt-readme] |
| Files shipped | `KSATelemetryOverlay.dll`, `mod.toml`, [`LICENSE.TXT`](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/LICENSE.TXT) (MIT), `montserrat/Montserrat-Bold.ttf`, `montserrat/Montserrat-Regular.ttf`, `montserrat/OFL.txt` ([KSATelemetryOverlay.csproj](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/KSATelemetryOverlay.csproj#L15-L29))[^kpt-csproj] |
| `mod.toml` | `name = "KSATelemetryOverlay"` and `[StarMap] EntryAssembly = "KSATelemetryOverlay"` ([mod.toml](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/mod.toml))[^kpt-modtoml] |
| Manifest id | `KSATelemetryOverlay`, enabled through `manifest.toml` `[[mods]]` |
| Harmony id | `com.elevatorctln.ksatelemetryoverlay` (one postfix, installed by reflection) |
| Config | `Documents/My Games/Kitten Space Agency/KSATelemetryOverlay/config.json` |
| Version | `1.1.0` in the csproj; distributed on GitHub Releases and [Spacedock](https://spacedock.info/mod/4552/Webcast%20Telemetry%20Overlay) |
| Assets | Two Montserrat TTFs (SIL OFL); no XML, GLB, shaders or sounds. `images/` holds README artwork only |

## Loader model and entry class

StarMap mod. The entry class is `TelemetryOverlayMod`, marked `[StarMapMod]`
([TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L10-L237)). It holds the snapshot, window host, config and renderer as
instance state; the sampler, settings UI and config store are static classes.

| Hook | Method | Fires | What the mod does there |
|---|---|---|---|
| `[StarMapBeforeMain]` | `OnBeforeMain()` | Before the game's `Program.Main` | Log line only |
| `[StarMapImmediateLoad]` | `OnImmediateLoad(Mod mod)` | Harmony prefix on `Mod.PrepareSystems`, per mod ([StarMap.Core/Patches/ModPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModPatches.cs#L11-L22)) | Remember `mod.DirectoryPath` for the fonts |
| `[StarMapAllModsLoaded]` | `OnAllModsLoaded()` | Postfix on `ModLibrary.LoadAll` in the `Program` constructor ([StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L17-L28)) | Load config, seed mission epochs, install the `DrawFps` postfix, build renderer, bind settings and tuning UIs |
| `[StarMapAfterOnFrame]` | `OnAfterFrame(currentPlayerTime, dtPlayer)` | Postfix on `Program.OnFrame` ([StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L38-L48)) | Sample the controlled vehicle into the snapshot; record `dt` for the hidden-UI path |
| `[StarMapAfterGui]` | `OnAfterGui(double dt)` | Postfix on `Program.OnDrawUiViewports`, only while `DrawUI` ([StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L26-L36)) | Load fonts once, build windows once, hotkeys, draw overlay, settings fallback and tuning windows, capture window placement, flush config |
| Harmony postfix | `HiddenUiPatch.AfterDrawFps()` | `Program.DrawFps`, called outside the `DrawUI` guard in every frame that reaches the UI phase; acts only when `DrawUI` is false | Draw the overlay with the UI hidden (F2) |
| `[StarMapUnload]` | `OnUnload()` | `ModLoader.Dispose` from `StarMapCore.DeInit`, after the game loop returned and after StarMap's id-less `UnpatchAll()` removed every patch, including this mod's postfix and StarMap's documents-path override ([StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35), [StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L143-L151)) | Save config, hide windows, restore the stock HUD, reset the sampler |

Where these sit in the frame: [Game loop and lifecycle](../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md).
In `Program.OnFrame` the `if (DrawUI)` UI block (with `StarMapAfterGui`), then `DrawFps` and
`OnDrawUiConsole` outside the guard, then `ImGui.Render` come in that order; `StarMapAfterOnFrame` runs
after the render, when `PrepareFrame` has already dispatched the next physics step
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2334)).[^ksa-program]

## Dependencies

| Dependency | How it is referenced | How it resolves at runtime |
|---|---|---|
| StarMap (`StarMap.API`) | `$(ImportDir)StarMap.API.dll`, `Private=false` ([KSATelemetryOverlay.csproj](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/KSATelemetryOverlay.csproj#L35-L71)) | Supplied by StarMap; required (README) |
| Game assemblies | `KSA`, `Brutal.ImGui`, `Brutal.ImGui.Extensions`, `Brutal.ImGui.Abstractions`, `Brutal.Core.*`, `Brutal.Glfw`, `BepuUtilities` from `..\Import\` (overridable `ImportDir`), `Private=false` | The running game's copies |
| Harmony | None at compile time; found by type name in the AppDomain | StarMap loads `0Harmony.dll` into the default context ([StarMap/Program.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/Program.cs#L18)); if absent only the F2 feature is lost |
| ModMenu | None; a local copy of `ModMenuEntryAttribute` | Optional; without it numpad `/` opens a settings window |
| KittenExtensions, ShaderExtensions, native libs | None | None |

## Architecture

| Subsystem | Files | Thread | Role |
|---|---|---|---|
| Entry and guards | [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs) | Main | Hooks, hotkeys, error budget |
| Sampling | [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs), [Telemetry/TelemetrySnapshot.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySnapshot.cs), [Telemetry/SignalState.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/SignalState.cs) | Main, after the frame | Vehicle state into a plain snapshot; freeze logic |
| Missions | [Telemetry/MissionRegistry.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionRegistry.cs), [Telemetry/MissionClock.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionClock.cs), [Telemetry/MissionEventLog.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionEventLog.cs), [Telemetry/MissionEvent.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/MissionEvent.cs) | Main, after the frame | Mission key, liftoff, clock, events, planned burns |
| Rendering | [Rendering/OverlayRenderer.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayRenderer.cs), [Rendering/PanelHost.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/PanelHost.cs), panels, [Rendering/Gfx.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Gfx.cs) | Main, UI phase | Foreground draw-list bar, animation, fonts |
| Windows | [Rendering/WindowHost.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/WindowHost.cs), [Rendering/TelemetryWindow.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/TelemetryWindow.cs) | Main, UI phase (game static-window pass) | Panels as movable windows |
| Flight HUD | [Rendering/FlightUiController.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/FlightUiController.cs) | Main, UI phase | Suppress and restore stock gauge canvases |
| Settings | [Rendering/SettingsUi.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/SettingsUi.cs), [Rendering/TuningUi.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/TuningUi.cs), [Config/ConfigStore.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/ConfigStore.cs), [Config/OverlayPalette.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/OverlayPalette.cs), [Rendering/Tuning.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Tuning.cs) | Main | ModMenu/fallback UI, JSON persistence, reflection-driven tuning |
| F2 hook | [HiddenUiPatch.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/HiddenUiPatch.cs) | Main, UI phase | Reflection-only Harmony postfix |

Data flow: frame N ends, `[StarMapAfterOnFrame]` copies the controlled vehicle's state, as applied in
frame N's `PrepareFrame`, into `TelemetrySnapshot` and updates the mission's clock and events; the
next physics step is already running on the workers with its own copies. Frame N+1's `PrepareFrame`
applies that step, then its UI phase draws the snapshot (in the game's static-window pass for the
standalone windows, then on the foreground draw list from `[StarMapAfterGui]`, or from the `DrawFps`
postfix when the UI is hidden) and toggles the stock HUD. So the overlay shows the state one physics
step behind the game's own gauges. Nothing runs on worker threads and nothing is locked; every
access is on the main thread.

## Features

| Feature | Category | Lifecycle |
|---|---|---|
| [Broadcast overlay on the ImGui foreground draw list](features/foreground-drawlist-broadcast-overlay.md) | ui | frame:draw-ui |
| [Staged intro and outro animation](features/intro-outro-animation.md) | ui | frame:draw-ui |
| [Montserrat fonts in the ImGui atlas](features/montserrat-fonts-in-imgui-atlas.md) | ui | startup:immediate-load, frame:draw-ui |
| [Hide and restore the stock flight HUD](features/hide-and-restore-stock-flight-hud.md) | ui | frame:draw-ui, event:starmap-unload |
| [Every element as its own ImGui window](features/panels-as-static-imgui-windows.md) | ui | frame:draw-ui |
| [ModMenu entry with a standalone fallback window](features/modmenu-entry-with-standalone-fallback.md) | ui | frame:draw-ui |
| [Rebindable numpad hotkeys](features/rebindable-numpad-hotkeys.md) | ui | frame:draw-ui |
| [Live flight telemetry sampling](features/live-flight-telemetry-sampling.md) | flight-and-orbits | frame:after-on-frame |
| [Mission clock and liftoff detection](features/mission-clock-and-liftoff-detection.md) | flight-and-orbits | frame:after-on-frame, frame:draw-ui |
| [Mission timeline with planned burns](features/mission-timeline-with-planned-burns.md) | flight-and-orbits | frame:after-on-frame, frame:draw-ui |
| [Engine diagram from real nozzle positions](features/engine-diagram-from-nozzle-positions.md) | vehicles-and-parts | frame:after-on-frame, frame:draw-ui |
| [In-flight event detection and callouts](features/flight-event-detection-and-callouts.md) | vehicles-and-parts | frame:after-on-frame, frame:draw-ui |
| [Freeze on signal loss](features/freeze-on-signal-loss.md) | vehicles-and-parts | frame:after-on-frame, frame:draw-ui |
| [Keep drawing while the game UI is hidden](features/draw-while-ui-hidden-drawfps-postfix.md) | patching | startup:all-mods-loaded, frame:draw-ui |
| [JSON config in the KSA documents folder](features/json-config-in-documents.md) | persistence | startup:all-mods-loaded, frame:draw-ui, event:starmap-unload |
| [Live tuning window and palette by reflection](features/live-tuning-and-palette-reflection.md) | testing-and-tooling | startup:all-mods-loaded, frame:draw-ui |
| [Error budget and self-disable](features/error-budget-self-disable.md) | getting-started | startup:all-mods-loaded, frame:after-on-frame, frame:draw-ui |
| [Allocation-free formatted readout text](features/allocation-free-readout-text.md) | ui | frame:draw-ui |

## Build, test and tooling

- .NET `net10.0`, C# latest, nullable, `AllowUnsafeBlocks` (for `ImFontConfig*` and `ImDrawVert`
  editing), version 1.1.0 ([KSATelemetryOverlay.csproj](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/KSATelemetryOverlay.csproj#L3-L13)).
- Game and StarMap DLLs are referenced from an `Import` folder next to the repository
  (`$(MSBuildThisFileDirectory)..\Import\`, overridable with `-p:ImportDir=...`); the folder is not in the
  repo, so a builder copies the DLLs there first ([KSATelemetryOverlay.csproj](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/KSATelemetryOverlay.csproj#L31-L33)).
- `mod.toml`, the fonts, `OFL.txt` and `LICENSE.TXT` are copied to the output; there is no deploy target,
  zip step or CI, and no tests in the repository.
- In-game tooling: the "Overlay tuning" window and "Copy as source" (see
  [Live tuning window and palette by reflection](features/live-tuning-and-palette-reflection.md)).

## Compatibility

- The README names no KSA build; claims in this bundle were checked against decompiled build
  2026.10.10.5554. Every member the mod uses exists there with the expected signature.
- Expected breakage points: the private `Program.DrawFps` (F2 feature only), the ImGui window names
  "Menu Bar" and `LAYOUTS###KSA.LayoutSaves+LayoutSavesWindow_`, the
  `ModuleStateful<EngineController, ...>` generic signature, `KSA.ImGuiWindow`'s protected
  `_initialPosition`/`_initialSize`, and `RocketNozzle.LocationAsmb`/`Part.MatrixAsmb2VehicleAsmb`.
- Changing the game's font settings runs `FontManager.RegenerateFonts`, which clears the whole ImGui
  atlas; the overlay's cached font pointers then dangle and are never reloaded (see
  [Montserrat fonts in the ImGui atlas](features/montserrat-fonts-in-imgui-atlas.md)).
- Under a StarMap instance path (`-InstancePath`, `STARMAP_INSTANCE_PATH`) the final config save in
  `[StarMapUnload]` goes to the default documents folder, because StarMap's
  `Constants.DocumentsFolderPath` prefix has been removed by then
  ([StarMap.Core/Patches/DocumentsPathPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/DocumentsPathPatches.cs#L12-L31); see
  [JSON config in the KSA documents folder](features/json-config-in-documents.md)).[^starmap-documentspathpatches]
- The standalone panel windows share one ImGui `###` id, so two open at once merge into one window
  (see [Every element as its own ImGui window](features/panels-as-static-imgui-windows.md)).
- Other HUD mods that toggle `GaugeCanvas`es are overridden while the overlay hides the HUD.

## Secondary sources

- [README.md](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/README.md): feature list, install, settings and hotkeys, and the author's maintenance caveat.
- Code comments worth reading: the HUD-restore notes in [Rendering/OverlayRenderer.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayRenderer.cs), the
  F2 rationale in [HiddenUiPatch.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/HiddenUiPatch.cs), the tuning rationale in [Rendering/Tuning.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Tuning.cs), and
  the licence notes in [KSATelemetryOverlay.csproj](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/KSATelemetryOverlay.csproj).

[^kpt-readme]: README.md
[^kpt-csproj]: KSATelemetryOverlay.csproj
[^kpt-modtoml]: mod.toml
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^starmap-documentspathpatches]: StarMap StarMap.Core/Patches/DocumentsPathPatches.cs
