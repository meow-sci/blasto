---
type: Submod
title: Zippo
description: Selects vessels and light parts, writes light intensity, color and switch state, queues eased transitions and runs Disco party-light recipes on private per-instance light templates, with a public API and scene-save restore
mod: unscience
submod: zippo
tags: [lights, colors, xkcd, animation, easing, disco, actuators, api]
mod_id: zippo
---

Zippo ("Zippo - Lights!" in the toolbox) is a light-control panel for vessels: pick a vessel and one of
its light parts, switch it on or off, set intensity from 0 to 1 and colour from presets, the full XKCD
named-colour catalog or a custom picker. On top of that it queues eased colour and intensity transitions
per light, and runs repeating Disco recipes (colour cycles, moving light assemblies, spotlight beam
spread) on one light or a whole vessel. Ordinary writes go to the shared part template's light data by
reflection; Disco instead gives each running light a private template copy and restores only what it
still owns. `ZippoSubmod.Instance` exposes light queries, state changes and animation queueing to other
mods, and It's So Shiny reuses its `LightController`.[^zippo-readme-md][^zippo-lib-readme-md]

## Hosting

- Manifest id `zippo`, entry assembly `MeowSci.Zippo` (development host only; only the supermod ships).
- Standalone host: F11 toggles the "zippo - Light Control" window; `[StarMapBeforeGui]` calls
  `ZippoSubmod.Update(dt)` (animation and Disco ticks) and its Patcher applies only `HotkeyGuard`.
- In unscience the sub-mod is created in `Mod.OnFullyLoaded` (unscience/Mod.cs L106). Zippo installs no
  Harmony patch of its own, so `unscience/Patcher.cs` has no Zippo entry; its ticks run from the host's
  `UpdateSubmods` (StarMap `BeforeGui`, or the hidden-HUD fallback while F2 is active).

## Features

| Feature | Category | Lifecycle | Summary |
|---|---|---|---|
| [Disco party lights with per-instance light templates and owned actuators](../features/zippo-disco-party-lights.md) | rendering | `frame:draw-ui`, `frame:render`, `physics:vehicle-solver`, `event:save`, `event:load` | Zippo's Disco runs repeating color, assembly-actuation and spotlight-spread channels on one light or a whole vessel by swapping each LightModule's Template for a private copy, claiming each keyframe actuator for one owner, and restoring only the values it still owns |
| [Per-light queues of eased color and intensity transitions](../features/zippo-light-animation-queue.md) | rendering | `frame:draw-ui`, `frame:render`, `event:save`, `event:load` | Zippo keeps a bounded queue of single-step LightAnimation transitions per light part instance, interpolates color and intensity with power-controlled EasingHelper curves from the UI frame tick, re-captures the live start values when each queued step begins, and persists progress in scene saves |
| [Light part selection and template-level color and intensity writes with a public API](../features/zippo-light-control.md) | rendering | `frame:draw-ui`, `frame:render`, `physics:vehicle-solver`, `event:save`, `event:load` | Zippo finds light parts by walking a vessel's part tree for LightModule template data, writes intensity and RGB color into the shared PartTemplate light components by reflection, toggles PowerConsumer.LightIsActive, ledgers originals for save and reset, and exposes the same operations to other mods |

## Library classes

`ZippoSubmod` (partials `Disco`, `Persistence`: UI, selection, public API, Disco coordination),
`LightController` (+ `Persistence` ledger), `LightPartInfo`, `LightAnimation` and
`LightAnimationManager` (+ `Persistence`), `DiscoRecipe`, `DiscoTiming`, `DiscoLight` (+ `Persistence`),
DTOs `ZippoSaveState`, `SavedLightTemplate`, `SavedLightQueue`, `SavedLightAnimation`,
`SavedOriginalLightColor`, `SavedDiscoLight`, `SavedDiscoActuator`; shared `ksa-abstractions.lib`
`VehicleProvider`, `PartHelpers`, `ReflectionHelpers`, `EasingHelper`, `XkcdColorHelper`.

## Scene saves and persistence

Participant `zippo`, restore order 80 (after Doh's materials), one `ZippoSaveState`: the template ledger
(template id, light-component ordinal, original and applied intensity and colour, including It's So
Shiny's writes), per-part original colours, active and queued transitions with elapsed time, every
running Disco effect (part reference, recipe, seed, phase offsets, elapsed, paused and switch state,
actuator ordinals with original goals) and the unapplied Disco draft recipe. Reset stops Disco, clears
queues and writes every ledgered original back before the native load; replay restores templates first,
then Disco, then queues, rejecting a part that would run both. Light switch states and actuator
positions themselves stay native save data.[^zippo-persistence-cs][^zippo-lib-readme-md]

## Further reading

- [zippo/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo/README.md) (features, Disco behavior, architecture)
- [zippo/SPEC.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo/SPEC.md) (original spec, light XML example)
- [zippo.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/README.md) (Disco ownership, scene saves)
- [scope/celestial-and-lights.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/celestial-and-lights.md) (reflection surface and Disco integration tables)
- [REPOSITORY_INDEX.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/REPOSITORY_INDEX.md) (project summary)

## Entry points

- [zippo/mod.toml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo/mod.toml)
- [zippo/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo/Mod.cs)
- [zippo/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo/Patcher.cs)
- [unscience/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Mod.cs#L106)
- [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs) (no Zippo patches)

## See also

- [unscience overview](../overview.md)
- [It's So Shiny](./its-so-shiny.md) (writes light templates through `LightController`)
- [Blinky](./blinky.md) (pixel grids built from lights)
- [Garry's Torch](./garrys-torch.md) (the same eased-queue pattern for welds)
- [Doh](./doh.md) (also uses the XKCD colour catalog)

[^zippo-readme-md]: zippo/README.md
[^zippo-lib-readme-md]: zippo.lib/README.md
[^zippo-persistence-cs]: zippo.lib/ZippoSubmod.Persistence.cs
