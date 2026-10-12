---
type: Submod
title: Sphinx
description: Places imported textured GLBs as body-fixed statics with automatic box or mesh colliders, live transforms and UV mapping, drawn through the game's static-object pipelines from three render postfixes and restored by scene saves
mod: unscience
submod: sphinx
tags: [statics, glb, terrain, colliders, bepu, vulkan, uv, textures]
mod_id: sphinx
---

Sphinx places imported GLB models (from the shared `glbs` catalog, the same one Pebbles uses) on a
planet or moon by clicking the terrain or beside the controlled vessel, anchored in the body-fixed frame
so they turn with the planet. Scale, rotation, offset, slope alignment and texture UV scale/offset
edit live; placements can be hidden, duplicated, snapped to the ground or removed; a PNG from the shared
`pngs` catalog can replace the embedded colours. Models collide through automatically chosen Bepu box
or two-sided mesh colliders added to each nearby surface physics bubble, and they are drawn with the
game's own static-object shaders by recording private buffers inside three `StaticObjectRenderer`
postfixes, so they receive native lighting and shadows without new shaders or shadow casters. It reuses
Pebbles' bounded GLB importer and material conversion.[^sphinx-readme-md][^sphinx-lib-readme-md]

## Hosting

- Manifest id `sphinx`, entry assembly `MeowSci.Sphinx` (development host only; only the supermod
  ships).
- Standalone host: F11 toggles the "Sphinx" window; its Patcher applies `HotkeyGuard` and
  `SphinxPatches`, which installs the three render postfixes and, through `SphinxPhysicsPatches`, the
  `ConstraintSim` postfixes and prefixes and the `NarrowPhaseCallbacks.AllowContactGeneration`
  transpiler. Any failure removes all of them and leaves `SphinxPatches.Ready` false.
- In unscience the sub-mod is created in `Mod.OnFullyLoaded` (unscience/Mod.cs L103) and
  `unscience/Patcher.cs` applies `SphinxPatches` at L97 and removes it at L151. Queued placements and
  edits run from `ISubmod.Update` (also under the hidden-HUD fallback); the terrain click runs in
  `RenderFloatingWindows`.
- Managed checks: `dotnet run --project sphinx.tests/sphinx.tests.csproj` compiles the production
  `PlacementMath`, `TextureMapping` and `CollisionGeometry` sources without the game.[^sphinx-tests-readme-md]

## Features

| Feature | Category | Lifecycle | Summary |
|---|---|---|---|
| [Body-fixed GLB statics placed by terrain click or beside a vessel](../features/sphinx-glb-statics-placement.md) | celestials | `frame:draw-ui`, `frame:render`, `event:save`, `event:load` | Sphinx anchors imported GLB models to a celestial's body-fixed frame from a ray-marched terrain pick or a vessel-relative point, grounds and centers their rotated bounds, applies live XYZ transforms through a queued edit loop and restores them from unscience scene saves |
| [Automatic box or mesh colliders for imported statics with scoped Bepu hooks](../features/sphinx-static-colliders.md) | physics | `physics:bepu`, `physics:vehicle-solver`, `frame:draw-ui` | Sphinx gives each placed GLB a global Bepu Box or two-sided Mesh chosen by conservative closed-box detection, adds per-bubble static handles from a ConstraintSim.BeginStaticObjectPass postfix, and admits them to contact generation and ground-contact logic only for its own handles |
| [Drawing private meshes through the native static-object pipelines with three postfixes](../features/sphinx-static-object-render-hooks.md) | rendering | `frame:render`, `render:prepass`, `render:scene-opaque` | Sphinx postfixes StaticObjectRenderer.UpdateRenderData, the private WriteCommandsColor and WriteCommandsPrePass so its own vertex, index, material and matrix buffers are drawn while the game's static-object pipeline, lighting and shadow descriptor sets are still bound |
| [Live per-static UV scale and offset with shared-PNG texture overrides](../features/sphinx-uv-texture-overrides.md) | rendering | `frame:draw-ui`, `frame:render`, `event:save`, `event:load` | Sphinx keeps each static's imported vertices in managed memory and re-uploads only private vertex buffers with UV times scale plus offset for repeatable edits, while a PNG override rebuilds the whole private resource first so a failed texture never replaces the working one |

## Library classes

`SphinxSubmod` (partials `Ui`, `Editing`, `Saves`: entries, pending-action queue, library refresh,
solver waits), `SphinxEntry`, `GroundPlacement` (`GroundAnchor`), `PlacementMath`, `StaticGeometry`,
`StaticModelResources` (+ `Textures` partial), `TextureMapping` / `StaticVertex`, `CollisionGeometry`
(`CollisionMode`, `CollisionTriangle`), `StaticCollider`, `SphinxPhysics`, `SphinxPatches`,
`SphinxPhysicsPatches`; from `pebbles.lib`: `ClutterAssets`, `ImportedPngTexture`,
`AssetUploadSubmission`, `GlbIdentity`; from `ksa-abstractions.lib`: `GlbLibrary`, `PngLibrary`,
`LibraryFileBrowser`, `PngFileBrowser`, `CelestialProvider`, `VehicleProvider`.

## Scene saves and persistence

Participant `sphinx`, restore order 150: per static the exact body id, Pebbles mesh id (copied file name
plus SHA-256), PNG name, body-fixed anchor and normal, XYZ scale, rotation and offset, UV scale and
offset, visibility, slope alignment and collider mode. Reset clears pending actions, waits for the
solvers and retires every render and physics resource; replay rebuilds meshes and colliders through the
normal budgets and warns per entry for missing bodies or files. GPU buffers, Bepu shapes and bubble
handles are reconstructed, never serialized. The standalone host has no save hooks, which is why the
panel text still calls placements session-only; imported files persist in the shared catalogs either
way.[^sphinx-saves-cs][^sphinx-lib-readme-md]

## Further reading

- [sphinx/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx/README.md) (usage, model support, limits, collision behavior)
- [sphinx.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/README.md) (implementation, native save persistence)
- [sphinx.tests/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.tests/README.md) (managed checks)
- [scope/statics.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/statics.md) (render, physics and shader contracts, per-build verification, native acceptance lists)
- [REPOSITORY_INDEX.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/REPOSITORY_INDEX.md) (project summary)

## Entry points

- [sphinx/mod.toml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx/mod.toml)
- [sphinx/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx/Mod.cs)
- [sphinx/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx/Patcher.cs)
- [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L97)
- [unscience/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Mod.cs#L103)
- [sphinx.tests/sphinx.tests.csproj](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.tests/sphinx.tests.csproj)

## See also

- [unscience overview](../overview.md)
- [Pebbles](./pebbles.md) (GLB importer, material conversion and upload helper reused here)
- [Graffiti](./graffiti.md) and [Free Fallin](./free-fallin.md) (share the `pngs` catalog)
- [Godzilla](./godzilla.md) (another feature that owns native Bepu shapes)

[^sphinx-readme-md]: sphinx/README.md
[^sphinx-lib-readme-md]: sphinx.lib/README.md
[^sphinx-tests-readme-md]: sphinx.tests/README.md
[^sphinx-saves-cs]: sphinx.lib/SphinxSubmod.Saves.cs
