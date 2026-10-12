---
type: Submod
title: fixme-mod-name (template)
description: The repository's template mod pair, copied and renamed by mkmod.ts to start a new feature, showing the minimal StarMap host, Harmony patcher with hotkey guard and sibling library project
mod: unscience
submod: fixme-mod-name
tags: [template, scaffold, getting-started]
mod_id: fixme-mod-name
---

`fixme-mod-name` and `fixme-mod-name.lib` are the scaffold every unscience feature started from. The
host is an attribute-based StarMap class with an F11 ImGui window, a Harmony patcher that always
applies the hotkey guard, and a library project for headless code. `mkmod.ts` copies both folders and
replaces the placeholder names. It is not bundled into the supermod.

## Hosting

- Manifest id `fixme-mod-name`, entry assembly `MeowSci.FixmeModName`.
- `fixme-mod-name/Mod.cs` uses `[StarMapImmediateLoad]` (zero-argument, so never dispatched by StarMap), `[StarMapAllModsLoaded]`, `[StarMapBeforeGui]`,
  `[StarMapAfterGui]` and `[StarMapUnload]`; `Patcher.cs` uses `PatchAll` plus `HotkeyGuard.Patch`.

## Features

| Feature | Category | Lifecycle | Summary |
|---|---|---|---|
| [Minimal standalone StarMap mod template with a separate feature library](../features/fixme-mod-name-standalone-mod-template.md) | getting-started | `startup:all-mods-loaded`, `frame:draw-ui` | The fixme-mod-name project pair is the repo's scaffold for a new mod, a StarMap entry class with the four lifecycle attributes, a Harmony patcher that always applies the hotkey guard, an F11 ImGui window and a sibling .lib project, copied and renamed by mkmod.ts |

## Scene saves and persistence

None; the template has no state.

## Further reading

- [fixme-mod-name/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/fixme-mod-name/README.md)
- [mkmod.ts](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/mkmod.ts)
- [AGENTS.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/AGENTS.md)

## Entry points

- [fixme-mod-name/mod.toml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/fixme-mod-name/mod.toml)
- [fixme-mod-name/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/fixme-mod-name/Mod.cs)
- [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs)

## See also

- [unscience overview](../overview.md)
- [ISubmod contract](../features/lib-isubmod-contract.md) (the next step after the template)
