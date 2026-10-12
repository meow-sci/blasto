---
type: Submod
title: unscience (supermod host)
description: "The only shipped mod: one StarMap entry class that constructs all 31 bundled feature sub-mods, applies their patches through one Harmony instance, renders them in one toolbox window and adds a per-save scene sidecar"
mod: unscience
submod: unscience
tags: [supermod, host, starmap, harmony, imgui, saves, build]
mod_id: unscience
---

`unscience/` is the host project and the only mod the repository publishes. It contains no feature
logic: its `[StarMapMod]` class constructs every feature library's `ISubmod`, ticks them each frame (also
while the HUD is hidden), renders them as collapsible sections of the "Unscience Toolbox" window, applies all
feature Harmony patches through one consolidated `Patcher`, adds a top-menu-bar entry, and wires the shared
persistence library so scene setups are stored beside native saves. Its build target assembles the single
distributable folder.

## Hosting

- Manifest: `name = "unscience"`, `[StarMap] EntryAssembly = "MeowSci.Unscience"`, no `assets` and no mod
  dependencies; version stamped by CI.
- Lifecycle methods on `MeowSci.Unscience.Mod`: `[StarMapImmediateLoad]` (empty and zero-argument, so StarMap never dispatches it), `[StarMapAllModsLoaded]`
  (construct and initialize submods, restore window state, wire save adapters, `Patcher.Patch()`),
  `[StarMapBeforeGui]` (`Update(dt)` for every submod), `[StarMapAfterGui]` (F11 toggle, toolbox window,
  floating windows, layout autosave timer), `[StarMapUnload]` (dispose submods, saves wiring, unpatch).
- While the HUD is hidden the same per-frame update is replayed from the shared
  [hidden-UI frame hook](../features/lib-hidden-ui-frame-hook.md).

## Features

| Feature | Category | Lifecycle | Summary |
|---|---|---|---|
| [One shipped mod folder from thirty-plus projects, with dist validation and rolling GitHub releases](../features/unscience-build-and-release.md) | testing-and-tooling | `startup:starmap-before-main` | MSBuild resolves the game DLLs once for every project, only the unscience project deploys, copying exactly the MeowSci assemblies of its reference closure plus Tomlyn, and CI stamps the version, validates the folder and publishes rolling releases |
| [One Harmony instance applying 30-plus feature patch classes in isolation](../features/unscience-consolidated-patcher.md) | patching | `startup:all-mods-loaded` | The supermod's Patcher creates a single Harmony instance and applies every feature library's static patch class through a TryApply wrapper, so one stale game target disables only its own feature, then removes them all on unload |
| [Native-free managed test executables that run production patches against stand-in KSA types](../features/unscience-managed-test-harnesses.md) | testing-and-tooling | `frame:prepare`, `event:save`, `event:load` | Eighteen console projects compile production sub-mod files by link, declare small namespace-KSA stand-ins with the game's member names and apply the real Harmony patches to them, so timing, save and patch logic is checked without starting the game |
| [Add an entry to the game's main menu bar through DrawProgramMenusHook](../features/unscience-menu-bar-entry.md) | ui | `frame:draw-ui` | A Harmony postfix on the empty public Program.DrawProgramMenusHook method appends an ImGui menu item to KSA's top menu bar that toggles the mod window |
| [Per-save scene sidecar wiring and the participant inventory](../features/unscience-scene-save-sidecar.md) | persistence | `startup:all-mods-loaded`, `event:save`, `event:load`, `frame:prepare`, `frame:draw-ui` | The unscience host collects every sub-mod's save records, pairs each native save's capture with its write by object identity, writes unscience.json beside universe.xml and reports save and load results in the toolbox |
| [Supermod host that aggregates 31 feature panels into one window](../features/unscience-supermod-host.md) | getting-started | `startup:all-mods-loaded`, `frame:draw-ui` | The unscience StarMap entry class builds every ISubmod at StarMapAllModsLoaded, ticks them all each frame, renders them as collapsible sections of one ImGui window and persists only its own window layout and header state |

## Library classes

Host files: `Mod.cs` (entry class and toolbox), `Patcher.cs` (consolidated Harmony instance and per-feature
`TryApply`), `MenuBarPatch.cs` (postfix on `Program.DrawProgramMenusHook`), `UnscienceSaves.cs` (scene-save
wiring and status UI), `UnscienceState.cs` (`window.ini` and `state.toml` under `.unscience/`). Shared
infrastructure lives in `ksa-abstractions.lib`: `ISubmod`, `HotkeyGuard`, `HiddenUiFrameHook`,
`PhysicsFrameHook`, `PartRenderFilter`, `IvaForceRender`, providers, file libraries and `Persistence/`.

## Scene saves and persistence

The host owns no scene record itself; it aggregates 35 records from 30 submods into `unscience.json` beside each
native save (see the [scene save sidecar](../features/unscience-scene-save-sidecar.md)). Its own UI state
(window layout, open headers, visible submods, tooltip and autosave preferences) is global, in
`.unscience/window.ini` and `.unscience/state.toml`, written on a 1-30 s timer while the window is open and on
unload.

## Further reading

- [unscience/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/README.md)
- [README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/README.md)
- [REPOSITORY_INDEX.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/REPOSITORY_INDEX.md)
- [scope/00-architecture-and-abstractions.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/00-architecture-and-abstractions.md)
- [scope/saves.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/saves.md)

## Entry points

- [unscience/mod.toml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/mod.toml)
- [unscience/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Mod.cs)
- [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs)
- [unscience/MenuBarPatch.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/MenuBarPatch.cs)
- [unscience/UnscienceSaves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/UnscienceSaves.cs)
- [unscience/UnscienceState.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/UnscienceState.cs)
- [unscience/unscience.csproj](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/unscience.csproj)

## See also

- [unscience overview](../overview.md)
- [Standalone mod template](./fixme-mod-name.md) (the per-feature host shape)
- [jplrepo](./jplrepo.md) (menu-bar animation experiment)
