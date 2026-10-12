---
type: Submod
title: Graffiti
description: Click-to-place projected PNG decals on vehicle hulls, deployed parachute canopies, EVA kittens and terrain, rendered in a custom Vulkan pass after the main view's final resolve
mod: unscience
submod: graffiti
tags: [decals, vulkan, glsl, picking, bindless-textures]
mod_id: graffiti
---

Graffiti places PNG decals with one armed click on vehicle hulls (mesh-precise), deployed parachute
canopies (live cloth triangles), EVA kittens (bounding sphere) and terrain (geodetic), and renders
them as projected decals: a unit cube per decal whose fragment shader rebuilds the scene position
from reverse-Z depth. The pass is recorded in a postfix on the main view's final
`RenderTarget.ResolveAttachments`, the window KSA's grid pass uses. Decals follow their anchors every
frame (part-local, cloth-barycentric or latitude/longitude) and go dormant when an anchor despawns.
It is a port of the gatOS sticker system, works in the flight scene and main viewport only, and uses
no string reflection.

## Hosting

- Manifest id `graffiti`, entry assembly `MeowSci.Graffiti` (development host only).
- Standalone host: F11 toggles the window; its Patcher applies `HotkeyGuard` and `GraffitiPatches`.
- In unscience `GraffitiPatches` is applied by `unscience/Patcher.cs`; the placement hint and click
  capture are drawn from `RenderFloatingWindows`. Public API on `GraffitiSubmod.Instance`
  (`PlaceAtCursor`, `Arm`/`Disarm`, `RemoveDecals`, `ClearDecals`, `RefreshLibrary`, `Decals`).

## Features

| Feature | Category | Lifecycle | Summary |
|---|---|---|---|
| [PNG decals as mod-owned slots in KSA's bindless texture table](../features/graffiti-bindless-decal-textures.md) | rendering | `frame:draw-ui`, `event:load` | Graffiti decodes library PNGs with the game's TextureLoader, uploads them as mip-mapped SimpleVkTextures, claims slots in Program.BindlessTextures so the shader needs only a uint index, hot-swaps changed files on Rescan and destroys freed images after every frame in flight has retired |
| [Per-frame decal anchoring to parts, canopy cloth and terrain](../features/graffiti-decal-anchoring.md) | rendering | `frame:draw-ui` | Decals store part-local, cloth-barycentric or geodetic anchors, re-resolve them by stable ids every frame and recompose a double-precision decal-to-ego box, so they follow staging, inflation, planet rotation and floating-origin shifts and go dormant instead of being deleted when the anchor despawns |
| [One-shot click placement with mesh, cloth, kitten and terrain picking](../features/graffiti-decal-placement-picking.md) | rendering | `frame:draw-ui` | An armed ImGui placement mode turns the next world click into a decal anchor by casting Cursor.GetEgoRay against live parachute cloth triangles, Part.RayCastEgo view meshes, a KittenEva bounding sphere and finally a CPU march over the accurate terrain height field |
| [Projected PNG decal pass recorded after the main-view resolve](../features/graffiti-projected-decal-pass.md) | rendering | `startup:all-mods-loaded`, `frame:draw-ui`, `render:grid` | A Harmony postfix on RenderTarget.ResolveAttachments records a custom Vulkan pass in the post-resolve window KSA's GridPass uses, drawing one unit cube per decal whose fragment shader rebuilds the scene position from reverse-Z depth and projects a PNG onto it |

## Library classes

`GraffitiSubmod` (with `Placement`, `Ui` and `Saves` partials), `GraffitiPatches`, `DecalPicker`
(with a parachute partial), `DecalAnchors`, `DecalEntry`, `DecalRenderer`, `DecalShaders`, the
decal texture cache in `DecalTextures.cs`, `GraffitiUi`; shared `PngLibrary` and `PngFileBrowser`.

## Scene saves and persistence

Participant `graffiti` (restore order 160, no explicit version field): image name, anchor kind, body
id for terrain, a saved part reference for vehicle or canopy parents, canopy index, cloth nodes,
barycentric weights and side, position and normal, roll, size, depth, alpha, brightness and visibility.
Instance ids, matrices and texture handles are rebuilt on load; reset cancels placement, waits for
the device and disposes the renderer and textures. PNG files stay in the shared library, outside saves.

## Notes

Imported images are capped at 2048 px on the longest edge (`MaxDimension` in `DecalTextures.cs`);
the "2048 cap" in the repository index refers to that, not to the number of bindless slots. A code comment saying KSA has no deferred-destroy helper is out of date (5554 has
`RetiredResourceQueue`). KSA 5541 changed the global descriptor binding to `DescriptorSet(ShaderSlot)`
without dynamic offsets, which the current code follows.

## Further reading

- [graffiti/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti/README.md)
- [graffiti.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/README.md)
- [scope/decals.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/decals.md)

## Entry points

- [graffiti/mod.toml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti/mod.toml)
- [graffiti/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti/Mod.cs)
- [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs)
- [graffiti.lib/DecalTextures.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/DecalTextures.cs)
- [REPOSITORY_INDEX.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/REPOSITORY_INDEX.md)

## See also

- [unscience overview](../overview.md)
- [Shared file library](../features/lib-shared-file-library.md)
- [Free Fallin](./free-fallin.md) (canopy appearance without picking)
- [Hot Pursuit](./hot-pursuit.md) (same part ray pick)
