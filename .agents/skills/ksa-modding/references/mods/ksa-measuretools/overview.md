---
type: Mod
title: KSA-MeasureTools (MeasureTools)
description: Click-to-measure ruler, protractor, surface, circle and face-angle tools for the map view, the flight view and the vehicle editor, with snapping to bodies, disc edges, orbit lines and part geometry down to mesh vertices and feature edges
mod: ksa-measuretools
tags:
  - measurement
  - ruler
  - protractor
  - map-view
  - parts
  - cad-snapping
  - editor
  - overlay
  - imgui
  - starmap
  - harmony
repo: KSA-MeasureTools
license: MIT
author: Maximilian-Nesslauer
loader: starmap
mod_id: MeasureTools
---

MeasureTools adds click-to-measure tools to Kitten Space Agency: a **Ruler** (two points, straight
distance), a **Protractor** (arm, apex, arm: true 3D angle plus both arm lengths, for example the
phase angle between two planets), a **Surface** tool (great-circle distance, chord and bearing between
two pins on a body), a **Circle** tool (diameter, radius and circumference of a part rim from one
click), and a **Face angle** tool (angle between two part surface normals). Points snap to bodies,
body discs, orbit lines and, close to a vehicle, to individual parts with centimeter precision:
attach nodes, part centers, mesh vertices, feature edges, rim centers and the mirror of the previous
point. Measurements stay live as everything moves, follow parts across staging, docking and the
editor boundary, and can be copied to the clipboard.[^mt-readme]

Author: GitHub user Maximilian-Nesslauer (also KSA-AdvancedFlightComputer and KSA-DeltaVMap).
License MIT. The README states it is validated against KSA build 2026.10.10.5554 and tested with
StarMap 0.4.6.

## Install footprint

| Item | Value |
|---|---|
| Folder | `Documents\My Games\Kitten Space Agency\mods\MeasureTools\` |
| Files | `MeasureTools.dll`, `mod.toml`; `colors.cfg` is written next to the DLL at runtime |
| `mod.toml` | `name = "MeasureTools"`, `[StarMap] EntryAssembly = "MeasureTools"`[^mt-modtoml] |
| Manifest id | `MeasureTools` (`[[mods]] id = "MeasureTools" enabled = true` in `manifest.toml`) |
| Assets | None: no XML, GLB, shaders or fonts |
| UI entry | Top-level **Measure -> Show Window** menu, in flight and in the editor |
| Distribution | GitHub releases and SpaceDock (mod 4319) |

## Loader model and lifecycle hooks

Plain StarMap mod: `[StarMapMod] public sealed class Mod` in `MeasureTools/Mod.cs` with one Harmony
instance (`com.maxi.measuretools`) that applies three patch classes one at a time.[^mt-mod]

| Hook | Where it fires (build 2026.10.10.5554) | What MeasureTools does |
|---|---|---|
| `[StarMapAllModsLoaded] OnFullyLoaded` | StarMap's postfix on `ModLibrary.LoadAll`[^starmap-modlibrarypatches] | Version warning, reflection drift check, load `colors.cfg`, apply patches |
| Harmony prefix `KSA.Program.OnMouseButton` | GLFW callback inside `Glfw.PollEvents` in `Program.PrepareFrame` (`frame:input`)[^ksa-program] | Own left clicks while armed; place points; short right click cancels or pauses[^mt-patch-mouse] |
| `[StarMapAfterGui] Draw` | StarMap's postfix on `Program.OnDrawUiViewports`[^starmap-programpatcher] | `MeasureState.Prune`, window draw, overlay draw and hover preview |
| Harmony postfix `KSA.Program.DrawProgramMenusHook` | `Program.DrawMenuBar`: the main bar in `OnDrawUiThreadSafe` (after AfterGui) when the main viewport is hovered, in flight and in the editor; also a hovered secondary viewport's bar inside `OnDrawUiViewports` | Measure menu, lazy window creation[^mt-patch-menubar] |
| Harmony postfix `KSA.Universe.DeserializeSave` | Save load | Clear all measurements[^mt-patch-loadsave] |
| `[StarMapUnload] Unload` | `ModLoader.Dispose` at StarMap shutdown, after the game loop returned and after StarMap's id-less `Harmony.UnpatchAll()` removed every patch[^starmap-modloader] | Save colors, reset every static; its own `UnpatchAll(_harmony.Id)` is a no-op by then |

Reflection (read and write): `Program._heldMouseButtons`, `Program._inputViewport`,
`BurnContextMenu._rightClickValid`, used only to hand back the mouse latches after consuming a right
release. There is no `StarMapBeforeGui`, `StarMapAfterOnFrame`, render hook, physics hook or
background thread; the mod never writes simulation state. See the
[game loop and lifecycle reference](../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
for where these points sit in the frame.

## Dependencies

| Dependency | Kind | Resolution |
|---|---|---|
| StarMap | Runtime loader (README: tested 0.4.6) | Required; loads `EntryAssembly` |
| `StarMap.API` 0.3.6 | NuGet, build time | `[StarMapMod]`, `[StarMapAllModsLoaded]`, `[StarMapAfterGui]`, `[StarMapUnload]` |
| `Lib.Harmony` 2.4.2 | NuGet, build time | Not shipped in the zip; StarMap carries Harmony |
| Game assemblies | `HintPath` `..\..\..\refs\game\*.dll`, `Private=false` | `KSA`, `Brutal.Core.Numerics`, `Brutal.Core.Logging`, `Brutal.ImGui`, `Brutal.Glfw`, `BepuUtilities`, `Brutal.Core.Common`, `Planet.Render.Core`[^mt-csproj] |

No KittenExtensions, ShaderExtensions, ModMenu or native libraries. No inter-mod API.

## Architecture

```text
GLFW click --> Patch_MouseButton (prefix) --> MapPicker.Pick / PickCircle --> Anchor --> MeasureState (Pending, Measurements)
StarMapAfterGui --> MeasureState.Prune (re-home / drop) --> MeasureWindow (list, hover index, copy) --> MeasureOverlay (draw + throttled preview pick)
MapPicker: part tiers (Part.RayCastEgo + MeshFeatureCache) > bodies/limb > orbit lines (Orbit.GetNearestPosition) > construction plane
```

| Folder or file | Role |
|---|---|
| `Mod.cs` | Entry class and hooks |
| `Patches/` | Mouse prefix, menu postfix, save-load postfix |
| `Features/Measure/MapPicker.cs` | All picking: parts, bodies, limbs, orbit lines, surface pins, free points[^mt-mappicker] |
| `Features/Measure/Anchor.cs`, `Measurement.cs` | Frame-relative anchors and live values[^mt-anchor] |
| `Features/Measure/MeshFeatureCache.cs` | Feature edges and rim circles per mesh[^mt-meshcache] |
| `Features/Measure/MeasureState.cs` | Tool state, arming, prune and re-home[^mt-state] |
| `Features/Measure/MeasureOverlay.cs` | Background draw-list overlay and preview[^mt-overlay] |
| `Features/Measure/MeasureWindow.cs`, `MeasureColors.cs` | Tool window, palettes and `colors.cfg`[^mt-window][^mt-colors] |
| `Core/` | Reflection handles, once-only logging, debug flags, perf scopes, console-row helpers |

All work is on the main thread. Picking reads the runtime `Part` graph and each subpart's
`MeshViewModule.MeshView.PositionCompare` triangle list, the same data the game's own part raycast
uses; it never touches `PartModel` (render data) or `PartInstance` (serialized data).[^ksa-part][^ksa-meshreference]
Projection and unprojection use the viewport's camera (`Camera.ScreenToEgoRay`, `EclToScreen`),
relying on ego axes being ECL axes.[^ksa-camera]

## Features

| Feature | Category | Lifecycle |
|---|---|---|
| [Own placement clicks with a guarded Program.OnMouseButton prefix](./features/click-capture-mouse-prefix.md) | patching | frame:input |
| [Unproject the cursor through the view camera onto a construction plane](./features/cursor-unprojection-and-construction-plane.md) | camera | frame:input, frame:draw-ui |
| [Snap to bodies and to the edge of their projected disc](./features/body-and-limb-snapping.md) | flight-and-orbits | frame:input, frame:draw-ui |
| [Snap to any visible orbit line with the stock nearest-orbit-point machinery](./features/orbit-line-snapping.md) | flight-and-orbits | frame:input, frame:draw-ui |
| [Live anchors in body, orbit, surface and part frames](./features/live-body-relative-anchors.md) | flight-and-orbits | frame:input, frame:draw-ui, event:save-load |
| [Pick hull points, attach nodes and part centers on vehicles](./features/part-mesh-raycast-picking.md) | vehicles-and-parts | frame:input, frame:draw-ui |
| [Extract feature edges and fit rim circles from a part's triangle list](./features/mesh-feature-edges-and-circle-fitting.md) | vehicles-and-parts | frame:input, frame:draw-ui |
| [CAD-style snapping: nodes, rim centers, mirror point, vertices, edges, axial/radial](./features/cad-style-snap-tiers.md) | vehicles-and-parts | frame:input, frame:draw-ui |
| [Part-local anchors that survive staging, docking and the editor boundary](./features/part-anchor-rehoming.md) | vehicles-and-parts | frame:draw-ui |
| [Circle and face-angle tools on part geometry](./features/circle-and-face-angle-tools.md) | vehicles-and-parts | frame:input, frame:draw-ui |
| [Surface tool with body-fixed pins and an occlusion-aware great-circle arc](./features/surface-great-circle-tool.md) | celestials | frame:input, frame:draw-ui |
| [Measuring inside the vehicle editor, including parts held in the hand](./features/editor-measuring.md) | vehicles-and-parts | frame:input, frame:draw-ui (while the editor is open) |
| [Measure menu and a stock-styled tool window with hover sync and click-to-copy](./features/measure-menu-and-tool-window.md) | ui | frame:draw-ui |
| [World-space measurement overlay on the ImGui background draw list](./features/overlay-drawlist-rendering.md) | ui | frame:draw-ui, render:imgui |
| [Named color palettes persisted to a cfg file next to the mod assembly](./features/color-palettes-persistence.md) | persistence | startup:all-mods-loaded, frame:draw-ui, event:starmap-unload |
| [Per-patch Harmony application, drift checks, once-only logging and perf scopes](./features/resilient-patching-and-diagnostics.md) | getting-started | startup:all-mods-loaded, frame:draw-ui, event:starmap-unload |
| [Shared MSBuild deploy targets, release zip and SpaceDock publishing](./features/mod-deploy-targets-and-release.md) | getting-started | build time |

## Build, test and tooling

- `MeasureTools.slnx` holds the single project; `net10.0`, x64, nullable on.
- `build/ModDeploy.targets` (identical to KSA-DeltaVMap's) deploys into the mods folder, generates a
  StarMap launch profile for fresh worktrees, reports a running game that locks the DLL, and stamps
  which checkout deployed. `PackageRelease` zips DLL plus `mod.toml` in Release.[^mt-deploy-targets]
- `.github/workflows/spacedock-publish.yml` uploads the release zip to SpaceDock, reading the
  compatible build from `TestedGameVersion` in `Mod.cs`.[^mt-spacedock]
- No automated tests in the repository. DEBUG builds add measure-event logging, `PerfTracker` scopes
  that report every 5 seconds, and a "Debug logging" header in the window to toggle both at runtime.

## Compatibility notes

- Targets KSA build 2026.10.10.5554 (`TestedGameVersion = "v2026.10.10.5554"`); a mismatch only logs a
  warning.
- Private surface it depends on: `Program._heldMouseButtons`, `Program._inputViewport`,
  `BurnContextMenu._rightClickValid` (type-checked; drift degrades right-click cancel to "stock menu
  also opens"). Harmony targets: `Program.OnMouseButton`, `Program.DrawProgramMenusHook`,
  `Universe.DeserializeSave`, each applied separately.
- Stock behaviors it mirrors and would need re-checking after an update: `Vehicle.GetMatrixAsmb2Ego`
  and `VehicleEditingSpace.GetMatrixAsmb2Ego` (anchor resolution), `Part.RayCastEgo` and
  `MeshReference.PositionCompare` (picking), `Orbit.GetNearestPoint` acceptance (the NaN and
  integer-aspect quirks it works around), `CelestialSystem.SetNearestOrbitPoint` candidate rules.
- The README's Usage section says to open the tool from **View -> Measure** in flight; the code at
  this ref only adds the top-level **Measure** menu.
- Two source comments about timing do not match the decomp frame order. Both stock hover sources are
  filled in `Universe.OnDrawUi`, which `OnDrawUiThreadSafe` calls after `StarMapAfterGui`, so in the
  AfterGui hover preview `ViewportPartPicker.Part` is the previous frame's result, and
  `Astronomical.CursorHoverEligible`, which `OnFrameViewports` clears earlier in the same frame, reads
  `false` for every body (the comments say "this frame" and "current frame"). The click path in
  `PollEvents` sees the previous frame's values of both, as the comments say. Expected effects (not
  checked in game): the preview omits center snaps to dot-sized bodies that a click would make, and
  part previews can lag one pick when the cursor first enters a vehicle (details in the part-picking
  and body-snapping concepts).
- Known conflicts: none stated. Any other mod that also consumes left clicks in `Program.OnMouseButton`
  will compete with the armed tool.

## Secondary sources

The repository has no `docs/` folder. The README (feature list, input table, screenshots) and the
unusually detailed header comments in `MapPicker.cs`, `Anchor.cs`, `Patch_MouseButton.cs`,
`GameReflection.cs`, `MeasureState.cs` and `MeshFeatureCache.cs` are the best reading; they name the
stock methods each technique mirrors (`Vehicle.UpdateHighlight`,
`VehicleEditor.HandleConnectorConnections`, `CelestialSystem.SetNearestOrbitPoint`,
`OrbitHoverCandidate.Wins`) and were checked against the decompiled build for this bundle.[^ksa-vehicleeditor]

[^mt-readme]: KSA-MeasureTools README.md
[^mt-modtoml]: MeasureTools/mod.toml
[^mt-csproj]: MeasureTools/MeasureTools.csproj
[^mt-mod]: MeasureTools/Mod.cs
[^mt-deploy-targets]: build/ModDeploy.targets
[^mt-spacedock]: .github/workflows/spacedock-publish.yml
[^mt-patch-menubar]: MeasureTools/Patches/Patch_MenuBar.cs
[^mt-patch-mouse]: MeasureTools/Patches/Patch_MouseButton.cs
[^mt-patch-loadsave]: MeasureTools/Patches/Patch_LoadSave.cs
[^mt-mappicker]: MeasureTools/Features/Measure/MapPicker.cs
[^mt-anchor]: MeasureTools/Features/Measure/Anchor.cs
[^mt-state]: MeasureTools/Features/Measure/MeasureState.cs
[^mt-overlay]: MeasureTools/Features/Measure/MeasureOverlay.cs
[^mt-window]: MeasureTools/Features/Measure/MeasureWindow.cs
[^mt-meshcache]: MeasureTools/Features/Measure/MeshFeatureCache.cs
[^mt-colors]: MeasureTools/Features/Measure/MeasureColors.cs
[^starmap-programpatcher]: StarMap.Core/Patches/ProgramPatcher.cs
[^starmap-modlibrarypatches]: StarMap.Core/Patches/ModLibraryPatches.cs
[^starmap-modloader]: StarMap.Core/ModRepository/ModLoader.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-meshreference]: KSA.MeshReference (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-vehicleeditor]: KSA.VehicleEditor (decompiled, build 2026.10.10.5554)
