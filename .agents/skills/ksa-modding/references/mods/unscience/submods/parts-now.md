---
type: Submod
title: Parts Now
description: Loads, reloads and unloads Part and SubPart mods in a running game, from pasted XML or an existing mod folder, by replaying KSA's boot asset pipeline one state per frame behind validation, a reflection self-test and a fail-closed purge
mod: unscience
submod: parts-now
tags: [runtime-loading, parts, modlibrary, asset-bundle, validation, thumbnails, hot-reload, mesh-buffer]
mod_id: parts-now
---

Parts Now loads Part and SubPart mods into a game that is already running. A player can paste Part
XML into a form and get a real, enabled mod folder, or load, reload and unload an existing mod folder
that KSA did not load at boot. New parts show up in the vehicle editor's part browser with mass,
connectors and thumbnails, and an edited GLB, texture or XML really is re-read on reload. The
sub-mod replays KSA's startup asset pipeline (`AssetBundle.OnDataLoad`, `ILoader.Load`,
`IBinder.Bind`, game-data attach, thumbnails) as an explicit state machine. It advances one state per
frame from the pre-GUI hook and puts only the file reads on a worker. Fifteen validation rules run
first, a reflection self-test gates everything, shared mesh-buffer headroom is reserved at startup,
and unload is an ordered purge behind a fail-closed gate.[^parts-now-lib-readme-md]

## Hosting

- Manifest id `parts-now`, entry assembly `MeowSci.PartsNow` (development host only; only the
  supermod ships).[^parts-now-mod-toml]
- Standalone host: `F10` toggles the "Parts Now" window. The key is configurable as `hotkey` in
  `<mods>/parts-now/parts-now.toml` and falls back to F10 with a log line. `[StarMapAllModsLoaded]`
  applies the patcher and calls `PartsNowSubmod.Initialize()`, `[StarMapBeforeGui]` calls `Update`,
  `[StarMapAfterGui]` draws the window, and `ImmediateUnload` is false because the sub-mod holds GPU
  resources (the property is declarative only: StarMap 4dc60f8 never reads it).[^parts-now-mod-cs]
- Patches: none of its own. The standalone `Patcher` applies only the shared `HotkeyGuard`, because
  the sub-mod has large text inputs.[^parts-now-patcher-cs]
- In unscience, `unscience/Mod.cs` adds `PartsNowSubmod` to its list, calls `Initialize()` from its
  single `[StarMapAllModsLoaded]` method, drives `Update(dt)` from `[StarMapBeforeGui]` (or from the
  hidden-HUD replay), and draws `RenderContent()` in the Unscience Toolbox. `unscience/Patcher.cs` has
  no Parts Now entry. Its global `HotkeyGuard` covers the text fields.[^unscience-mod-cs][^unscience-patcher-cs]
- Timing constraint: `Initialize()` must run at `[StarMapAllModsLoaded]`, before `ModLibrary.Bind`
  allocates the shared mesh buffers, and `Update` must be the only caller of `RuntimeModLoader.Step()`,
  once per frame.[^parts-now-submod-cs]

## Features

| Feature | Category | Lifecycle | Summary |
|---|---|---|---|
| [Validate Part asset bundles with fifteen rules before anything is written or registered](../features/parts-now-bundle-validation.md) | assets | `frame:draw-ui` | Parts Now deserializes submitted XML with KSA's own AssetBundle serializer but never calls OnDataLoad, then runs rules V1-V15 as isolated pure functions over the set, blocking the load on any error, including the PbrMaterial channel crash guard and the V8 registry rule |
| [Reserve headroom in KSA's shared interleaved mesh buffer before it is allocated](../features/parts-now-mesh-headroom-reservation.md) | rendering | `startup:all-mods-loaded`, `frame:draw-ui` | Parts Now inflates DeviceMeshInterleaved.Shared size counters from StarMapAllModsLoaded, before ModLibrary.Bind sizes the one shared vertex and index buffer, then rewinds the bump cursor on the first frame so runtime meshes get valid offsets |
| [Turn pasted Part XML into a real, enabled KSA mod folder from inside the game](../features/parts-now-paste-xml-mod-writer.md) | assets | `frame:draw-ui` | Parts Now validates a mod id, writes mod.toml plus up to three XML bundles atomically (UTF-8 without BOM, LF) under the game's own mods folder, adds an enabled manifest entry, and classifies existing mod folders for loading |
| [One reflection layer with a startup self-test that disables loading instead of crashing](../features/parts-now-reflection-self-test.md) | patching | `startup:all-mods-loaded`, `frame:draw-ui` | Parts Now resolves every KSA internal it needs (six ModLibrary registries, SerializedCollection._collection, VehicleEditor._editorTagLookup) once in a non-throwing static constructor, sorts failures into fatal and degraded, and gates loading on the result |
| [Load, reload and roll back Part mods at runtime with a one-state-per-frame loader](../features/parts-now-runtime-part-loading.md) | assets | `frame:draw-ui`, `thread:background` | Parts Now replays KSA's boot asset pipeline for one mod folder after startup, from parse and register through background file loading, GPU binding, incremental game-data attach and thumbnails, one state per frame from the pre-GUI hook |
| [Unload runtime-registered parts behind a fail-closed safety gate](../features/parts-now-runtime-unload-purge.md) | assets | `frame:draw-ui` | Parts Now refuses to unload while anything live could reference a mod's parts, then purges in a fixed order (device idle, templates, caches, textures, meshes, registries) and defers the purge to the pre-GUI hook so freed ImGui textures are never drawn |
| [Render part-browser thumbnails at runtime on KSA's offscreen thumbnail viewport](../features/parts-now-thumbnail-generation.md) | rendering | `frame:draw-ui` | Parts Now gives runtime-loaded parts real editor thumbnails by combining ThumbnailCreator's boot framing with ThumbnailDynamic's per-frame submit, rendering two parts per frame on Program.ThumbnailViewport with a private command pool and one fence wait per part |

## Library classes

All in `parts-now.lib`:

| Area | Classes |
|---|---|
| Entry | `PartsNowSubmod` (`ISubmod`, `ISaveParticipantSource`) |
| Loader | `RuntimeModLoader` (core, `Api`, `States`, `GpuStates`, `Deltas`, `Job` partials), `LoadedModRecord`, `RuntimeModRegistry`, `EditorRefresh` |
| Unload | `RuntimeModUnloader`, `RuntimeModUnloadGate`, `RuntimeModPurgeSteps` |
| Engine access | `GameRegistry` (the only reflection), `MeshBudget`, `PartsNowSettings` |
| Validation | `BundleParser` (+ `BundleParserQueries`), `BundleValidator` (+ `Context`, `RulesIdentity`, `RulesReferences`, `RulesSchema`), `ValidationIssue`, `GlbMeshNames` |
| Thumbnails | `PartThumbnailGenerator`, `ThumbnailReadback` |
| Mod folders | `Io/ModIdValidator`, `Io/ModFolderWriter`, `Io/ModFolderScanner` |
| UI | `StatusPanel` (+ `Settings`), `PastePanel` (+ `Actions`), `XmlTabEditor`, `ModFolderPanel` (+ `Actions`), `ResultsPanel`, `ValidationIssueView`, `PanelStyle` |

Every file carries the same threading rule: everything runs on the game thread except the loader's
`RunLoaders` step, which touches only `ILoader.Load()` on a `Task.Run` worker.

## Scene saves and persistence

- Save participant `parts-now` (restore order 0), supermod only: an array of
  `SavedDependency(ModId, PartIds[])` for every mod Parts Now loaded this session. Reset does nothing,
  because installed templates are process-wide, not save-owned. Restore calls
  `ModLibrary.Get<PartTemplate>` for each id and warns "Install/enable part mod ... and reload the
  save" for any that are missing. Validation rejects more than 1000 mods or more than 10000 part ids
  per mod. Loading a save never loads or unloads a mod.[^parts-now-submod-saves-cs]
- Pasted mods persist as real folders under the game's mods directory with an enabled
  `manifest.toml` entry, so they load at the next launch like any other mod.
- Global settings in `<mods>/parts-now/parts-now.toml` (`vertexHeadroomMiB`, `indexHeadroomMiB`,
  `hotkey`). Headroom changes apply at the next launch.[^parts-now-settings-cs]

## Further reading

- [parts-now/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now/README.md)
- [parts-now.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/README.md) (workflows, rules table, purge order, threading, thumbnail troubleshooting)
- [scope/part-editor-and-robotics.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/part-editor-and-robotics.md) (per-member integration table and update risks U1-U12)[^scope-part-editor-md]
- [plans/done/PARTS_NOW_PLAN.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/plans/done/PARTS_NOW_PLAN.md) (original design and in-game test matrix)[^parts-now-plan-md]
- [ksa-upgrade.tests/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-upgrade.tests/README.md) (managed V8 checks)[^ksa-upgrade-tests-readme-md]

## Entry points

- [parts-now/mod.toml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now/mod.toml)
- [parts-now/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now/Mod.cs)
- [parts-now/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now/Patcher.cs)
- [parts-now.lib/PartsNowSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/PartsNowSubmod.cs)
- [unscience/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Mod.cs#L96) (sub-mod registration)

## See also

- [unscience overview](../overview.md)
- [Hidden-HUD frame hook](../features/lib-hidden-ui-frame-hook.md) (keeps loader jobs running with the HUD hidden in the supermod)
- [Scene save coordinator](../features/lib-scene-save-coordinator.md)
- [Pebbles](./pebbles.md) (another runtime GLB import path)

[^parts-now-lib-readme-md]: parts-now.lib/README.md
[^parts-now-mod-toml]: parts-now/mod.toml
[^parts-now-mod-cs]: parts-now/Mod.cs
[^parts-now-patcher-cs]: parts-now/Patcher.cs
[^unscience-mod-cs]: unscience/Mod.cs
[^unscience-patcher-cs]: unscience/Patcher.cs
[^parts-now-submod-cs]: parts-now.lib/PartsNowSubmod.cs
[^parts-now-submod-saves-cs]: parts-now.lib/PartsNowSubmod.Saves.cs
[^parts-now-settings-cs]: parts-now.lib/Runtime/PartsNowSettings.cs
[^scope-part-editor-md]: scope/part-editor-and-robotics.md
[^parts-now-plan-md]: plans/done/PARTS_NOW_PLAN.md
[^ksa-upgrade-tests-readme-md]: ksa-upgrade.tests/README.md
