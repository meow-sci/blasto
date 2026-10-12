---
type: Submod
title: Blinky
description: Builds NxM grids of real engine parts on a live vessel at runtime and lights them as an LCD display by switching engine controllers, with feed wiring, patterns and scrolling text
mod: unscience
submod: blinky
tags: [pixel-grid, engines, runtime-parts, propellant-feed, animation]
mod_id: blinky
---

Blinky turns a vessel into a dot-matrix display. It creates an NxM grid of engine parts (two per
pixel) from a part template, attaches them under the vessel root in one part-tree rebuild, wires each
engine to a tank through its declared feed connector so it can actually ignite, and then shows patterns,
static images or a scrolling marquee by switching engine controllers on and off. Several named grids can
live on one vessel, keyed by `(vehicleId, gridName)`; a global scan finds existing grids by their
`pixel_<grid>_<row>_<col>_<a|b>` part ids. The tiny engine meshes can be hidden through the shared
[part render filter](../features/lib-part-render-filter.md).

## Hosting

- Manifest id `blinky`, entry assembly `MeowSci.Blinky` (development host only; only unscience ships).
- Standalone host: F11 toggles the window; its Patcher applies `BlinkyPatches` (render-skip registration
  with the shared `PartRenderFilter`) and `HotkeyGuard`.
- In unscience: `BlinkySubmod` is constructed in `Mod.cs` and `Patcher.cs` applies `BlinkyPatches` on the
  consolidated Harmony instance.

## Features

| Feature | Category | Lifecycle | Summary |
|---|---|---|---|
| [Feed runtime-created engines through their declared feed connectors](../features/blinky-engine-pixel-feed-wiring.md) | vehicles-and-parts | `frame:draw-ui`, `frame:prepare`, `event:load` | Blinky connects each pixel engine's RocketCore.FeedConnectors entry to a tank part, aligns its stage, rebuilds the resource managers and reads Combustor.ResourceManager.ConsumptionOrder to prove every engine can reach propellant |
| [Build NxM grids of real engine parts on a live vessel at runtime](../features/blinky-runtime-part-grid-builder.md) | vehicles-and-parts | `frame:draw-ui`, `frame:prepare`, `event:load` | Blinky creates hundreds of engine parts from a PartTemplate, wires them under the vessel root by hand through TreeParent and TreeChildren, and rebuilds the vessel with one PartTree.CreateFromNewPartTree call instead of merging part by part |
| [Drive a pixel display by switching engine controllers on and off](../features/blinky-scroll-animation-and-patterns.md) | vehicles-and-parts | `frame:draw-ui`, `frame:input`, `frame:render`, `event:load` | Blinky lights grid pixels with buffered EngineController.SetIsActive calls, offering pattern presets, a diffing static display and a column-stepped scrolling marquee, while the shared PartRenderFilter hides the tiny engine meshes |

## Library classes

`BlinkySubmod` (UI), `BlinkyGridManager` (compound-key registry; pattern, static, scroll and off APIs;
`ScanAllVehicles`), `LcdGridBuilder` (`BuildGrid`, `DestroyGrid`, `ScanExistingGrid`, `RepairFuelFeeds`),
`LcdGridConfig`, `PixelGrid`, `BlinkyPixelGrid`, `PixelPatterns`, `ScrollAnimation`, `BuiltInScrollPixels`,
`PropellantFeedDiagnostics`, `NonLcdEngineCache`, `BlinkyPatches` / `BlinkyPatchState`.

## Scene saves and persistence

Record `blinky` (order 30): the render-meshes flag and, per grid, vehicle id, name, ownership, cell list
and scroll state. Restore does not rebuild parts, because KSA's own save already recreated them: it
re-registers the grid on the reconstructed parts, calls `LcdGridBuilder.RepairFuelFeeds` and resumes the
display, warning per grid on failure. See the [scene save sidecar](../features/unscience-scene-save-sidecar.md).

## Further reading

- [blinky/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky/README.md)
- [blinky.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/README.md)
- [scope/pixel-grids-and-render.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/pixel-grids-and-render.md)

## Entry points

- [blinky/mod.toml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky/mod.toml)
- [blinky/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky/Mod.cs)
- [blinky/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky/Patcher.cs)
- [blinky.lib/BlinkySubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/BlinkySubmod.cs)
- [blinky.lib/BlinkyPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/BlinkyPatches.cs)

## See also

- [unscience overview](../overview.md)
- [Its So Shiny](./its-so-shiny.md) (the same grid idea with light parts)
- [Zippo](./zippo.md) (light control)
