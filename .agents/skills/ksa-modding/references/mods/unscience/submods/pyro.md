---
type: Submod
title: Pyro
description: Places engine-free volumetric exhaust plumes on any vessel part by submitting mod-owned exhaust instances into the game's own plume batch, with nozzle physics, per-plume look overrides, a template editor, presets and on/off cycles
mod: unscience
submod: pyro
tags: [exhaust, plumes, rendering, nozzle-physics, presets, harmony]
mod_id: pyro
---

Pyro ("Pyro - Engine Plumes") renders the game's volumetric engine exhaust with no engine part. Each plume is
welded to a vessel, part and sub-part with position and rotation offsets and submitted through KSA's own
`VolumetricExhaustRenderer` from a postfix on `Vehicle.AddVolumetricExhaustInstances`, so it gets the same
camera, timing and startup/shutdown transients as stock engines. Plume shape comes from an isentropic nozzle
model fed into the game's `PlumeData`, per-plume look overrides are scoped to Pyro's own submissions, and a
template editor exposes the game's hidden exhaust debug controls.

## Hosting

- Manifest id `pyro`, entry assembly `MeowSci.Pyro` (development host only).
- Standalone host: F11 toggles the window; its Patcher applies `HotkeyGuard` and `PyroPatches`.
- In unscience: `PyroSubmod` is constructed in `Mod.cs` and `Patcher.cs` applies `PyroPatches`.

## Features

| Feature | Category | Lifecycle | Summary |
|---|---|---|---|
| [Edit the game's shared exhaust templates and refresh every live plume](../features/pyro-exhaust-template-editor.md) | rendering | `frame:draw-ui`, `frame:pre-render`, `render:exhaust`, `event:save`, `event:load` | Pyro reproduces the game's hidden Volumetric Exhausts debug editor, writing VolumetricExhaustTemplate absorption, emission, noise, length and quality values, propagating them to every instance and engine nozzle the way the game's editor does, and saving or reverting the edits with the scene |
| [Synthesize plume physics from nozzle parameters with an isentropic model](../features/pyro-isentropic-nozzle-model.md) | rendering | `frame:pre-render` | Pyro turns six nozzle values and a throttle into a full PlumeData by solving exit Mach from the area ratio, expanding the chamber gas isentropically and calling the game's PlumeData.Compute, so engine-free plumes react to ambient pressure like stock ones |
| [Override one caller's data at a shared game method with a scoped prefix](../features/pyro-scoped-exhaust-look-override.md) | patching | `frame:pre-render` | Pyro gives each plume its own absorption density and refraction by prefixing VolumetricExhaustRenderer.AddInstance(ExhaustInstance, int) with a ThreadStatic override that is only set inside a try/finally around Pyro's own submission, so stock engines and shared templates are never touched |
| [Submit engine-free volumetric exhaust plumes into the game's plume batch](../features/pyro-volumetric-exhaust-plumes.md) | rendering | `frame:pre-render`, `render:exhaust`, `frame:draw-ui`, `event:load` | Pyro postfixes Vehicle.AddVolumetricExhaustInstances and, for each plume welded to that vessel, drives a mod-owned VolumetricExhaustInstance through UpdateState and VolumetricExhaustRenderer.AddInstance so it renders, ignites, shuts down and reacts to air like a real engine plume |

## Library classes

`PyroSubmod` (+ `.CreateUi`, `.PlumeUi`, `.PresetUi`, `.TemplateUi`, `.Persistence`, `.TemplatePersistence`),
`PlumeEntry`, `PlumeEmitter`, `PlumePhysics`, `PlumeCycle`, `PlumeTemplates`, `PlumeTemplateIds`,
`PlumeMigrationMath`, `PlumePreset`, `PlumePresetManager`, `SavedPlumeTemplate`, `TemplateRefresher`,
`RotationHelper`, `PyroUi`, `PyroPatches`.

## Scene saves and persistence

Two records. `pyro.templates` (order 30) holds edited shared exhaust-template values so they are applied
before plumes; `pyro` (default order 100) holds each plume's anchor as a
[stable part reference](../features/lib-save-part-identity.md), its full settings, enabled flag and on/off
cycle state. Named presets are a separate global file, `.unscience/pyro-presets.toml`; cycle state is
excluded from presets. Cycle and migration math is covered by `pyro.tests`.

## Further reading

- [pyro/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro/README.md)
- [pyro.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/README.md)
- [scope/exhaust-plumes.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/exhaust-plumes.md)
- [pyro.tests/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.tests/README.md)

## Entry points

- [pyro/mod.toml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro/mod.toml)
- [pyro/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro/Mod.cs)
- [pyro/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro/Patcher.cs)
- [pyro.lib/PyroPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PyroPatches.cs)
- [pyro.lib/PyroSubmod.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PyroSubmod.Persistence.cs)

## See also

- [unscience overview](../overview.md)
- [Humble Arteest](./humble-arteest.md) (engine emissive glow)
- [Garry's Torch](./garrys-torch.md) (the preset pattern Pyro copies)
