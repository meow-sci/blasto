---
type: Submod
title: It's So Shiny
description: Builds Blinky-style pixel grids from stock light parts instead of engines, powered through battery connections and switched with the stock light switch, with pattern, static and scroll controls
mod: unscience
submod: its-so-shiny
tags: [pixel-grid, lights, runtime-parts, power, render-filter]
mod_id: its-so-shiny
---

It's So Shiny ("Its So Shiny - Light Grids") reuses Blinky's runtime grid technique with KSA's built-in
`LightPart`, which avoids engine ignition, thrust cancellation and propellant routing. Each pixel is one light
part named `shiny_<grid>_<row>_<col>`; lights are connected to battery-bearing parts so the stock power
consumer can switch them, recolored with one template write per part template, and their meshes are drawn
only while lit through the shared [part render filter](../features/lib-part-render-filter.md).

## Hosting

- Manifest id `its-so-shiny`, entry assembly `MeowSci.ItsSoShiny` (development host only).
- Standalone host: F11 toggles the window; its Patcher applies `HotkeyGuard` and `ShinyPatches`.
- In unscience: `ItsSoShinySubmod` is constructed in `Mod.cs` and `Patcher.cs` applies `ShinyPatches`.

## Features

| Feature | Category | Lifecycle | Summary |
|---|---|---|---|
| [Light-part pixel grids switched through stock light switches](../features/its-so-shiny-light-part-grids.md) | rendering | `frame:draw-ui`, `frame:render`, `physics:vehicle-solver`, `event:load` | It's So Shiny builds Blinky-style grids from the stock LightPart, powers them by connecting each light to a battery part, toggles pixels through PowerConsumer.LightIsActive, recolors them with one template write per PartTemplate, and draws a light's mesh only while it is lit |

## Library classes

`ItsSoShinySubmod` (+ `.Persistence`), `ShinyGridManager` (registration, patterns, static display,
scroll), `ShinyGridBuilder` (runtime creation and destruction), `ShinyBuiltGrid`, `ShinyGridConfig`,
`ShinyPixelGrid`, `ShinyPixelCell`, `ShinyPixelPatterns`, `ShinyScrollAnimation`, `ShinyPatches` /
`ShinyPatchState`.

## Scene saves and persistence

Record `its-so-shiny` (order 30): the render-parts flag and, per grid, vehicle id, name, ownership, color,
intensity, cells and scroll state. Restore re-registers grids on the light parts KSA reconstructed and
warns per grid on failure.

## Further reading

- [its-so-shiny/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/its-so-shiny/README.md)
- [its-so-shiny.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/its-so-shiny.lib/README.md)
- [scope/pixel-grids-and-render.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/pixel-grids-and-render.md)

## Entry points

- [its-so-shiny/mod.toml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/its-so-shiny/mod.toml)
- [its-so-shiny/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/its-so-shiny/Mod.cs)
- [its-so-shiny/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/its-so-shiny/Patcher.cs)
- [its-so-shiny.lib/ShinyPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/its-so-shiny.lib/ShinyPatches.cs)
- [its-so-shiny.lib/ItsSoShinySubmod.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/its-so-shiny.lib/ItsSoShinySubmod.Persistence.cs)

## See also

- [unscience overview](../overview.md)
- [Blinky](./blinky.md) (engine-part grids)
- [Zippo](./zippo.md) (owner of the light controller it reuses)
