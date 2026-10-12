---
type: Submod
title: Bloomin' Onion
description: Defines brand-new planetary rings at runtime for any celestial body, with painted band textures, dust and rock-field settings, presets, and a renderer rebuild through the game's own settings path
mod: unscience
submod: bloomin-onion
tags: [rings, celestials, procedural-texture, renderer-rebuild, presets]
mod_id: bloomin-onion
---

Bloomin' Onion adds planetary rings to bodies that have none (a moon, Earth, a ringless gas giant). Every
parameter of KSA's ring XML is editable: geometry, the 2D band (painted stripes and gaps, or any game
texture plus a control strip), volumetric dust and the instanced rock field with per-LOD meshes from the
whole game catalog. It builds a complete `PlanetaryRingsReference` tree in code, assigns it to the body
template and makes the game rebuild its ring renderer, so no game data file is touched. It reuses Rocky
McRock Face's asset catalog and mesh cloning.

## Hosting

- Manifest id `bloomin-onion`, entry assembly `MeowSci.BloominOnion` (development host only).
- Standalone host: F11 toggles the window; its Patcher applies only `HotkeyGuard` (the feature itself
  needs no Harmony patch).
- In unscience: `BloominOnionSubmod` is constructed in `Mod.cs`; nothing is added to the consolidated
  patcher.

## Features

| Feature | Category | Lifecycle | Summary |
|---|---|---|---|
| [Procedural ring band textures uploaded through the game's own TextureReference.Bind](../features/bloomin-onion-painted-ring-textures.md) | rendering | `frame:draw-ui`, `frame:prepare`, `render:atmosphere` | Bloomin' Onion rasterizes stripes and ringlet noise into a 2048x1 RGBA8 band plus a control strip, wraps each in a TextureReference subclass whose private TextureAsset is seeded by reflection, and binds it through the game so it gets a bindless handle like any stock texture |
| [Add planetary rings to any celestial at runtime](../features/bloomin-onion-runtime-planetary-rings.md) | celestials | `frame:draw-ui`, `frame:prepare`, `render:atmosphere`, `render:scene-opaque` | Bloomin' Onion builds a complete PlanetaryRingsReference tree in code, assigns it to a body template and forces the planetary rings renderer to be recreated so the ring appears without touching game data |

## Library classes

`BloominOnionSubmod` (+ `.Ui`, `.UiSections`, `.Saves`), `RingDefinition`, `RingDefinitionController`
(apply, remove, snapshots, prune), `RingReferenceBuilder` (definition to game tree, validation),
`RingRendererRebuilder` (rebuild and distant-sphere shadow sync), `RingBandPainter`,
`PaintedTextureReference`, `RingTextureFactory`, `StockRingAssets`, `RingPresetStore`,
`RingDefinitionSerializer`; reuses `RingAssetCatalog` and `RingMeshFactory` from rocky-mcrock-face.lib.

## Scene saves and persistence

Record `bloomin-onion` (order 30, before Rocky McRock Face's overlays at 40): per body, its id and the full
`RingDefinition`. Restore re-applies each definition to the uniquely resolved body and warns when the body
is missing or ambiguous. Named presets are a separate global file, `.unscience/bloomin-onion-rings.toml`.

## Further reading

- [bloomin-onion/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion/README.md)
- [bloomin-onion.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/README.md)
- [scope/rings.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/rings.md)

## Entry points

- [bloomin-onion/mod.toml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion/mod.toml)
- [bloomin-onion/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion/Mod.cs)
- [bloomin-onion/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion/Patcher.cs)
- [bloomin-onion.lib/BloominOnionSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/BloominOnionSubmod.cs)
- [bloomin-onion.lib/BloominOnionSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/BloominOnionSubmod.Saves.cs)

## See also

- [unscience overview](../overview.md)
- [Rocky McRock Face](./rocky-mcrock-face.md) (swaps assets of existing rings; shared catalog)
- [Kiwi's Marbles](./kiwis-marbles.md) (weld a ringed moon to a vessel)
