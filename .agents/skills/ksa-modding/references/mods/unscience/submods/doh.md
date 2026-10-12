---
type: Submod
title: DOH (Dynamically Originating Hominids)
description: Spawns EVA kittens on demand next to any vehicle, optionally with per-kitten tints made by cloning GPU materials inside the game's fixed 512-slot material pool
mod: unscience
submod: doh
tags: [kittens, eva, spawning, materials, gpu, tint, scene-saves]
mod_id: doh
---

DOH puts new `KittenEva` vessels into the world from a panel: choose a reference vehicle, a character
(or random), a body-frame offset and a count, and the kittens appear in a chain in front of the
vehicle with a full MMH/NTO backpack, exactly as if they had left through an EVA door. With "Custom
Color" each batch, or each kitten with "Unique Each" or "I'm Feeling Lucky", gets its own tint by
cloning every material the kitten renders with into new GPU material slots, which can then be
recoloured live, per material. The panel shows the material-pool budget, and despawned kittens give
their slots back. The library is headless and documented for reuse by other mods; all of it is
main-thread only.[^doh-readme][^doh-lib-readme]

## Hosting

- Manifest id `doh`, entry assembly `MeowSci.Doh` (development host only; only unscience ships).[^doh-mod-toml]
- Standalone host: F8 toggles the "DOH — Kitten Spawner" window; its Patcher runs `PatchAll` on its
  own assembly (which holds no patch classes) and applies `HotkeyGuard`. There are no game patches.[^doh-mod-cs][^doh-patcher]
- In unscience, `DohSubmod` is created in `Mod.OnFullyLoaded`, ticked by the host's `UpdateSubmods`
  (from `[StarMapBeforeGui]`, or the hidden-HUD replay) and drawn as a section of the Unscience
  Toolbox; `unscience/Patcher.cs` has no doh entry.[^unscience-mod-cs][^unscience-patcher-cs]

## Features

| Feature | Category | Lifecycle | Summary |
|---|---|---|---|
| [Spawn EVA kittens anywhere by replicating EVADoor.CreateKittenEva](../features/doh-kitten-spawning.md) | kittens | `frame:draw-ui` | doh builds KittenEva vessels beside a chosen vehicle by copying the stock EVA door sequence (backpack part with its own tree, eight-argument constructor, CreateFromStateCci orbit, Teleport) after waiting out the vehicle solver |
| [Per-kitten tints by cloning GPU materials inside the fixed 512-slot material pool](../features/doh-runtime-gpu-materials.md) | kittens | `frame:draw-ui`, `event:save`, `event:load`, `frame:prepare` | doh clones each kitten's materials into new GpuMaterialSystem slots with a custom AlbedoColor, swaps MaterialIndices to the clones, and budgets and frees slots in the fixed 512-slot pool |

## Library classes

`DohSubmod` (panel, vehicle and character pickers, kitten list, `Update` pruning; `.Persistence.cs`
save participant), `KittenSpawner` (spawn, despawn, prune, recolour; partials `.Positioning`,
`.Materials`, `.Catalog`), `SpawnRequest`, `SpawnResult` / `SpawnedKittenInfo`,
`SpawnedKittenRegistry` / `SpawnedKittenEntry`, `MaterialSystemAccessor` (reflection bridge;
`.Pool.cs` capacity, free slots, reserve, release), `MaterialFactory` (clone sets; `.Lifetime.cs`
release and naming), `KittenMaterialSet` / `MaterialEntry`. Shared from `ksa-abstractions.lib`:
`VehicleProvider`, `CelestialProvider`, `XkcdColorHelper`, `SubmodUI`, `HotkeyGuard`,
`MaterialColorState`.[^doh-lib-readme]

## Scene saves and persistence

Participant `doh`, order 60. Native KSA saves own the kittens themselves; the doh record keeps the
spawn controls and, per tracked kitten, its vehicle id, character, tint, optional shared
`MaterialGroup` and per-material colours keyed by source renderable and material name. Reset keeps
the live material sets detached instead of despawning; restore rebinds them to the natively rebuilt
kittens (reusing GPU slots when the source handles still match) and never spawns replacements;
unclaimed sets are released. There are no global config files. Managed tests in `doh.tests` cover
the group, reuse and release rules without the game.[^doh-persistence][^doh-tests-readme]

## Further reading

- [doh/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh/README.md)
- [doh.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/README.md)
- [doh.tests/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.tests/README.md)
- [scope/character-and-materials.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/character-and-materials.md) (integration-point table and update history)[^scope-character]

## Entry points

- [doh/mod.toml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh/mod.toml)
- [doh/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh/Mod.cs)
- [doh/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh/Patcher.cs)
- [unscience/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Mod.cs#L80)
- [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs)

## See also

- [unscience overview](../overview.md)
- [Humble Arteest](./humble-arteest.md) (Kitten Color edits the same material buffer)
- [Kitten Animations](./kitten-animations.md) (animates spawned kittens)
- [Iron Man](./iron-man.md) (another sub-mod that works on live `KittenEva` vessels)

[^doh-readme]: doh/README.md
[^doh-lib-readme]: doh.lib/README.md
[^doh-tests-readme]: doh.tests/README.md
[^scope-character]: scope/character-and-materials.md
[^doh-mod-toml]: doh/mod.toml
[^doh-mod-cs]: doh/Mod.cs
[^doh-patcher]: doh/Patcher.cs
[^doh-persistence]: doh.lib/DohSubmod.Persistence.cs
[^unscience-mod-cs]: unscience/Mod.cs
[^unscience-patcher-cs]: unscience/Patcher.cs
