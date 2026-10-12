---
type: Game Feature
title: Read KSA's star binary files and reload or replace the star field at runtime
description: The 16-byte star record format named by mod.toml, how to read it for catalog joins, and how to swap the star field at runtime by rewriting Mod.StarBinaries, resetting the instanced star technique and refilling it within its capacity
category: celestials
group: systems-and-stars
topic: read-and-replace-star-binaries
demonstrated_by: [mods/ohmystars/features/stellarium-sky-cultures, mods/ohmystars/features/star-binary-switcher]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types:
  - KSA.Mod
  - KSA.ModLibrary
  - KSA.InstancedStarTechnique
  - KSA.Program
  - KSA.ViewportRegistry
  - KSA.Popup
  - KSA.TimedAlert
tags: [star-binary, starfield, instanced-star-technique, reflection, reload]
---
KSA stores its star field as `.bin` files named by a mod's `mod.toml` (`starBinaries`). Reading them lets a mod match stars to a catalog, count or sample them, or build a replacement set in the same format. Replacing the field at runtime means rewriting the private `Mod.StarBinaries`, resetting the instanced star technique, re-adding instances within its capacity and uploading the instance buffer.

## Engine background

- **Format.** `int32` count, then that many 16-byte records: three `float`s (a direction, normalised on load), one `byte` scale, three colour bytes (RGB; alpha is fixed at 255). Each record is added with `starTechnique.AddInstance(viewport: Program.MainViewport, forward, scale, color, distance: 19.5f)` ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L231-L285)). The file list is the private `string[] StarBinaries` field of `Mod` ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L68-L69)).
- **Capacity.** `Program` creates the technique with `ModLibrary.GetStarCount()` instances, which is **200 plus the sum of every mod's header counts** ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L551-L559), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1049)), then `ModLibrary.PopulateStars` loads every mod's binaries and uploads ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L561-L573)). `InstancedRenderTechnique.AddInstance` throws "Instance buffer full" beyond `_maxInstances` ([current/decomp/KSA/InstancedRenderTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InstancedRenderTechnique.cs#L86-L96)); `ResetInstances(viewport)` sets that viewport's count to zero ([current/decomp/KSA/InstancedRenderTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InstancedRenderTechnique.cs#L100-L103)); `UpdateInstanceBuffer(viewport, frameIndex)` copies the CPU instances into the mapped buffer ([current/decomp/KSA/InstancedStarTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InstancedStarTechnique.cs#L43-L50)).
- **Where it lives.** The technique is the private static `Program._starStarTechnique` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L207)); mods are listed in `ModLibrary.Lookup`, which is internal (use `GetList`).
- **Rendering.** The stars are drawn in several render paths (`_starStarTechnique.Render`, [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4527)); packing: colour and scale into a `uint` ([current/decomp/KSA/InstancedStarTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InstancedStarTechnique.cs#L68-L83)).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: mirror Mod.LoadStarBinaries when parsing](read-and-replace-star-binaries-1.md) | ohmystars | `data-file-parsing` | `startup:all-mods-loaded` | Read-only and safe; float bits as identity |
| [2: reflect Mod.StarBinaries, reset and refill the star technique](read-and-replace-star-binaries-2.md) | ohmystars | `reflection` | `startup:all-mods-loaded`, `frame:draw-ui` | Live star-field switching; private members and buffer growth |

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Bundle a star catalog and join it to the sky](../../../celestials/systems-and-stars/bundle-and-query-a-star-catalog/overview.md), [Grow game GPU buffers for runtime content](../../../rendering/meshes/grow-game-gpu-buffers-for-runtime-content/overview.md), [Show alerts and popups in game](../../../ui/windows/show-alerts-and-popups-in-game/overview.md)
- [Bundle a star catalog and constellation data and join them to the game's sky](../bundle-and-query-a-star-catalog/overview.md) - related topic

