---
type: Submod
title: Humble Arteest
description: Three recoloring tools on three render paths, per-part vehicle paint in free state-flag bits with an in-memory shader patch, kitten material tints and visor toggle, and engine heat-glow overrides
mod: unscience
submod: humble-arteest
tags: [paint, shaders, materials, kittens, emissive]
mod_id: humble-arteest
---

Humble Arteest bundles three visual tools that each use a different render data path. Vehicle Paint
carries a per-part, per-template or global albedo tint in the unused high bits (11 to 31) of each
part instance's `StateBitFlag`, decoded by an in-memory patched `MeshIndirect.frag` /
`MeshIndirectRaytraced.frag`. Kitten Color writes `AlbedoColor` into `GpuMaterialSystem.BigBuffer`
and adds a transpiler-based visor hide toggle. Engine Emissive prefixes the private
`PartModelDynamic.AddInstance` to override temperature so the stock heat LUT glows. In unscience the
three are grouped under one "Humble Arteest - Paint Stuff" section.

## Hosting

- Manifest id `humble-arteest`, entry assembly `MeowSci.HumbleArteest` (development host only).
- Standalone host: F11 window with three separate panels; its Patcher applies `HotkeyGuard`,
  `VehiclePaintPatches`, `EngineEmissivePatches` and `KittenVisorPatches`, and unload calls
  `VehiclePaint.Cleanup` and `EngineEmissive.Cleanup`.
- In unscience the same three patch classes and both cleanups are driven by `unscience/Patcher.cs`.

## Features

| Feature | Category | Lifecycle | Summary |
|---|---|---|---|
| [Engine glow override through the dynamic part instance submission](../features/humble-arteest-engine-emissive.md) | rendering | `startup:all-mods-loaded`, `frame:draw-ui`, `frame:render` | A Harmony prefix on PartModelDynamic's private AddInstance rewrites each instance's Temperature and TfiThickness just before it is appended to the frame's instance list, so the game's own ENABLE_TEMPERATURE heat lookup and thin-film shading make chosen engine models glow without any shader change |
| [Kitten tinting by staged writes into the GPU material buffer](../features/humble-arteest-kitten-color.md) | kittens | `frame:draw-ui`, `event:load` | Kitten Color finds Program.MaterialSystem and its protected AssetMap and DeviceCtx by reflection and overwrites MaterialData.AlbedoColor for chosen materials with a one-shot Vulkan staging upload into GpuMaterialSystem.BigBuffer, which ModelPbr.frag multiplies into the albedo and alpha-tests |
| [Hide kitten helmet visors with a draw-call transpiler](../features/humble-arteest-kitten-visor-toggle.md) | kittens | `startup:all-mods-loaded`, `frame:draw-ui`, `frame:render` | A Harmony transpiler on KittenRenderable.UpdateRenderData replaces exactly one VisorMesh.Draw(ViewHandle) call with a static helper that skips the draw while a session flag is set, hiding visor glass on every kitten in every pass and view without touching materials or meshes |
| [Per-part vehicle paint through free state-flag bits and an in-memory part shader patch](../features/humble-arteest-vehicle-paint-shader-patch.md) | shaders | `startup:all-mods-loaded`, `frame:draw-ui`, `frame:render` | Vehicle Paint packs a 7:7:7 colour into the unused high bits of each part instance's StateBitFlag via postfixes on PartTreeRenderData's private state writers, and compiles a patched MeshIndirect fragment shader in memory by prefixing ShaderModuleUtils.FromFile during a requested renderer rebuild |

## Library classes

`VehiclePaint`, `VehiclePaintPatches`, `VehiclePaintShaders`, `PaintTargets`, `VehiclePaintSubmod`,
`KittenColor`, `KittenColorSubmod`, `KittenVisorPatches`, `EngineEmissive`, `EngineEmissivePatches`,
`EngineEmissiveSubmod`, `HumbleArteestSubmod` (composite); shared
`ksa-abstractions.lib/Persistence/MaterialColorState.cs`. The `Experiments` folder holds development probes.

## Scene saves and persistence

Participant `humble-arteest` (restore order 80): paint enabled flag and blend mode, global paint,
template colours, per-part colours by saved part reference, kitten material colours by material name
(excluding Doh and Free Fallin materials), the visor flag, and engine emissive global and per-module
settings. Restoring paint re-arms the shader patch through `Program.RendererRebuildNeeded`. The READMEs
still call paint session-only; that is only true for the standalone host.

## Notes

Engine Emissive settings are keyed by the shared `PartModelDynamic`, which exists once per part
template, so "per engine" means per engine type. The paint shader anchor moved to `MeshIndirect.frag`
L108 in 5554 (L114 in 5482); bits 0 to 10 remain the only ones the game writes.

## Further reading

- [humble-arteest/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest/README.md)
- [humble-arteest.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/README.md)
- [scope/character-and-materials.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/character-and-materials.md)
- [ksa-upgrade.tests/VehiclePaintChecks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-upgrade.tests/VehiclePaintChecks.cs)

## Entry points

- [humble-arteest/mod.toml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest/mod.toml)
- [humble-arteest/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest/Mod.cs)
- [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs)

## See also

- [unscience overview](../overview.md)
- [Doh](./doh.md) (runtime kitten materials)
- [Part render filter](../features/lib-part-render-filter.md) (same cached render data)
