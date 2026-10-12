---
type: Submod
title: Rocky McRock Face
description: Swaps the meshes, textures and rock-field settings of existing planetary rings at runtime by mutating the ring reference tree and rebuilding the ring renderer, with a catalog of every loaded mesh and texture
mod: unscience
submod: rocky-mcrock-face
tags: [rings, celestials, meshes, textures, reflection, renderer-rebuild]
mod_id: rocky-mcrock-face
---

Rocky McRock Face ("Rocky McRock Face - Planetary Ring Swapper") replaces the assets of KSA's planetary ring
system, such as Saturn's instanced rock field and 2D band, with any loaded mesh or texture, including every
part and sub-part mesh. It needs no Harmony patch: it mutates the public XML-backed ring reference tree,
snapshots the originals for Restore Defaults, and recreates the ring renderer. Part meshes are interleaved
in a shared buffer, so they are cloned into private `Simple` meshes the ring pipeline can draw. Its asset
catalog and UI helpers are reused by Bloomin' Onion.

## Hosting

- Manifest id `rocky-mcrock-face`, entry assembly `MeowSci.RockyMcRockFace` (development host only).
- Standalone host: F11 toggles the window; its Patcher applies only `HotkeyGuard`.
- In unscience: `RockyMcRockFaceSubmod` is constructed in `Mod.cs`; nothing is added to the consolidated
  patcher.

## Features

| Feature | Category | Lifecycle | Summary |
|---|---|---|---|
| [Enumerate every loaded mesh and bound texture from ModLibrary's internal registries](../features/rocky-mcrock-face-asset-catalog.md) | assets | `frame:draw-ui`, `frame:prepare` | RingAssetCatalog reflects the internal static ModLibrary.AllMeshes, AllFiles and AllGltfs collections, keeps meshes with retained CPU geometry, textures with a live bindless handle and normal maps, parses glTF headers for character meshes, and feeds filterable ImGui combos shared with Bloomin' Onion |
| [Clone an atlas-interleaved part mesh into a Simple mesh the ring renderer can draw](../features/rocky-mcrock-face-interleaved-mesh-clone.md) | rendering | `frame:draw-ui`, `frame:prepare`, `render:scene-opaque` | Rocky McRock Face clones interleaved part and subpart meshes into private Simple MeshReferences that share the retained CPU HostPrimitives and uploads them as SimpleVkMesh for the ring pipeline, and imports glTF-file meshes the same way |
| [Swap the meshes and textures of existing planetary rings at runtime](../features/rocky-mcrock-face-ring-asset-swap.md) | celestials | `frame:draw-ui`, `frame:prepare`, `render:scene-opaque`, `render:atmosphere` | Rocky McRock Face mutates the public XML-backed ring reference tree of a ringed body (LOD meshes, rock material, band texture, field settings), snapshots the originals, and recreates the rings renderer so the constructor-baked ring data is rebuilt from the edit |

## Library classes

`RockyMcRockFaceSubmod` (+ `.Ui`, `.Saves`), `RingSwapController` (snapshot, apply, restore, rebuild),
`RingAssetCatalog`, `RingMeshFactory` (interleaved-to-Simple clone cache), `RingSelection`, `RockyUi`
(form, grid and combo helpers reused by bloomin-onion).

## Scene saves and persistence

Record `rocky-mcrock-face` (order 40, after Bloomin' Onion's rings at 30): per body, its id, LOD mesh ids,
rock material textures, band texture and field settings. Restore applies each swap to the uniquely resolved
body and warns when a body or asset is missing. (Older text in `REPOSITORY_INDEX.md` calls the overrides
session-only; the save adapter in this checkout persists them.)

## Further reading

- [rocky-mcrock-face/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/rocky-mcrock-face/README.md)
- [rocky-mcrock-face.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/rocky-mcrock-face.lib/README.md)
- [scope/rings.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/rings.md)

## Entry points

- [rocky-mcrock-face/mod.toml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/rocky-mcrock-face/mod.toml)
- [rocky-mcrock-face/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/rocky-mcrock-face/Mod.cs)
- [rocky-mcrock-face/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/rocky-mcrock-face/Patcher.cs)
- [rocky-mcrock-face.lib/RingSwapController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/rocky-mcrock-face.lib/RingSwapController.cs)
- [rocky-mcrock-face.lib/RockyMcRockFaceSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/rocky-mcrock-face.lib/RockyMcRockFaceSubmod.Saves.cs)

## See also

- [unscience overview](../overview.md)
- [Bloomin' Onion](./bloomin-onion.md) (new rings; reuses this catalog)
