---
type: Submod
title: Pebbles
description: Replaces the meshes, materials, scale and colliders of chosen ground clutter types on one celestial with stock or imported GLB models, authored in a floating collider workshop and committed at the solver handoff with exact restore
mod: unscience
submod: pebbles
tags: [ground-clutter, glb, colliders, celestials, vulkan, imgui, saves, library-only]
---

Pebbles lets a player pick a stock mesh or import a self-contained GLB, set a uniform scale, fit
box, sphere, capsule or cylinder colliders in a textured floating editor, and apply the result to
every variant and LOD of selected ground clutter types on one planet, while the game's own placement
still decides where instances grow. Untouched types keep their stock objects (including stock hull
colliders), and **Restore** brings back the exact originals per type or per planet. The native work
(private reference graph, placement/render/physical data, private material buffer and shader
variant) is built and swapped in a `Universe.ExecuteNextClothSolvers` prefix, the only main-thread
point after every solver finished and before the cloth and vehicle steps read clutter. Applied
recipes, removed and knocked-over clutter, and override grids travel with native KSA saves.[^pebbles-readme-md][^scope-ground-clutter-md]

## Hosting

- **No manifest, no standalone host.** Unlike most sub-mods, Pebbles has no `pebbles/` project,
  no `mod.toml` and no development hotkey; `pebbles.lib` "has no standalone StarMap entry" and ships
  only inside unscience, so `mod_id` is null. The assembly is `MeowSci.PebblesLib`
  ([pebbles.lib/pebbles.lib.csproj](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/pebbles.lib.csproj#L3-L9)),
  referenced by the host project
  ([unscience/unscience.csproj](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/unscience.csproj#L29)).[^pebbles-readme-md][^repository-index-md]
- **Construction.** `Mod.OnFullyLoaded` (`[StarMapAllModsLoaded]`) creates `PebblesSubmod` in display
  order, initializes it with the others, and hands its `ClutterController` to the patcher with
  `Patcher.PebblesController = pebbles.Controller` before calling `Patcher.Patch()`
  ([unscience/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Mod.cs#L65-L139)).[^unscience-mod-cs]
- **Patches.** `Patcher.Patch` runs `TryApply("pebbles", () => PebblesController?.ApplyPatches(_harmony!))`
  on the shared `MeowSci.Unscience` Harmony instance, and `Unload` runs the matching `TryRemove`
  ([unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L38-L163)).
  `ClutterHooks.Apply` installs, all at `Priority.First`: constructor prefixes on six native clutter
  ownership types, transpilers on four `ClutterEcotypeRenderData` material call sites, the
  `Universe.ExecuteNextClothSolvers` prefix, a `GroundClutterRenderer.RebuildFrameResources` postfix,
  a `GroundClutterRenderer.Dispose` prefix, a `GroundClutterRenderer.SerializeSave` postfix, a
  prefix and finalizer on `ClutterEcotypeRenderData.RebuildFrameResources` and a
  `ShaderReference.CompileVariantWithCustomOptions` prefix. Removal unpatches only methods declared by
  `ClutterHooks` on the shared owner
  ([pebbles.lib/Runtime/ClutterHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterHooks.cs#L30-L60)).[^unscience-patcher-cs][^clutter-hooks-cs]
- **Per-frame work.** `ISubmod.Update` (unscience `[StarMapBeforeGui]`, or the `HiddenUiFrameHook`
  replay while the HUD is hidden) handles scene changes, requeues suspended overrides, discovery,
  deferred preview and import release, and Workshop preview rendering. `RenderContent` draws the main
  form inside the unscience window; `RenderFloatingWindows` draws the Collider Workshop and the GLB
  file browser every frame, even with the unscience window closed.[^pebbles-submod-cs]
- **Managed checks.** `dotnet run --project pebbles.tests/pebbles.tests.csproj` links the
  game-independent models, `HullMath`, `WorkshopMath`/`WorkshopState` and the shared save serializer;
  it needs no game assemblies.[^pebbles-tests-readme-md]

## Features

| Feature | Category | Lifecycle | Summary |
|---|---|---|---|
| [Cancellable one-shot GPU upload submission with publish-after-fence and retire-after-idle](../features/pebbles-asset-upload-submission.md) | rendering | `frame:draw-ui`, `frame:dispatch-solvers` | Pebbles uploads CPU-built buffers and textures through a private one-shot command buffer and fence, using a StagingPool only for memory so failed recordings are never auto-submitted, publishing after the fence and freeing after Device.WaitIdle |
| [Authored clutter colliders mapped onto native collider templates with a private convex-hull subclass](../features/pebbles-clutter-colliders.md) | physics | `frame:dispatch-solvers`, `physics:vehicle-solver`, `physics:bepu` | Pebbles maps box, sphere, capsule, cylinder and hull collider recipes onto KSA ColliderTemplate types, including a private OwnedHull with stock-matching centroid and volume, so native clutter physics builds, scales and frees the Bepu shapes |
| [Removed and displaced clutter carried per grid spacing across swaps and saves](../features/pebbles-clutter-state-persistence.md) | persistence | `frame:dispatch-solvers`, `event:save`, `event:load` | Pebbles remembers knocked-over and removed clutter per ecotype and exact spacing, replays it into each new placement, rewrites native clutter save entries in a SerializeSave postfix and keeps override grids in a pebbles.clutter-state sidecar record |
| [Bounded GLB 2.0 import into unregistered CPU-only meshes with frozen path and hash ids](../features/pebbles-glb-import.md) | assets | `frame:draw-ui`, `frame:dispatch-solvers` | Pebbles reads self-contained GLB files with its own bounded parser, bakes scene node and UV transforms, wraps the result in CPU-only MeshReference objects never added to ModLibrary, and freezes every library choice to an absolute-path plus SHA-256 mesh id |
| [GLB PBR materials converted to native clutter maps with private bindless textures](../features/pebbles-glb-material-conversion.md) | assets | `frame:draw-ui`, `frame:dispatch-solvers` | Pebbles bakes glTF metallic-roughness materials and common Blender extensions into native diffuse, packed AO-roughness-metal, normal and cutout maps on the CPU, then uploads each as a private SimpleVkTexture with its own bindless slot set by reflection |
| [Per-celestial ground clutter replacement with transactional apply and restore](../features/pebbles-ground-clutter-replacement.md) | celestials | `frame:dispatch-solvers`, `frame:draw-ui`, `event:renderer-rebuild`, `event:load` | Pebbles swaps the meshes, materials, scale and colliders of chosen clutter types on one celestial by building private native clutter bundles in a Universe.ExecuteNextClothSolvers prefix, keeping the originals for exact restore |
| [Private clutter material buffer and source-color shader variant scoped to owned construction](../features/pebbles-private-clutter-materials.md) | rendering | `frame:dispatch-solvers`, `event:renderer-rebuild` | Pebbles redirects the game's ground clutter material buffer lookups to a private per-body buffer and swaps in a source-color ClutterSolidFrag variant, only while a thread-static owned-construction context is active |
| [Floating collider workshop with a textured preview, CPU-projected gizmos and detached undo history](../features/pebbles-workshop-editor.md) | ui | `frame:draw-ui`, `render:imgui` | Pebbles edits a detached clutter object in a floating ImGui window that shows an offscreen preview image, overlays collider wireframes and gizmos projected with that image's own matrix, fits primitives to mesh bounds and keeps JSON-cloned undo history |
| [Independent offscreen Vulkan preview rendered before the GUI and shown as an ImGui image](../features/pebbles-workshop-offscreen-preview.md) | rendering | `frame:draw-ui`, `render:imgui` | Pebbles renders the Workshop mesh into private color and depth images with a dynamic-rendering pipeline and runtime-compiled GLSL, submits it with a fence in StarMapBeforeGui, and shows the result through the ImGui Vulkan backend |

## Library classes

- **Sub-mod surface:** `PebblesSubmod` (partials for the form, applied-clutter controls, GLB import,
  saves) and `PebblesUi` helpers; `PebblesSubmod.Applied` lists live overrides with per-type and
  whole-planet restore and **Restore all and release resources**.[^pebbles-submod-applied-cs]
- **Runtime/:** `ClutterController` (+ `.Grids`), `ClutterHooks`, `ClutterResources`, `ClutterGraph`,
  `ClutterCapture`, `ClutterColliders` (private `OwnedHull`), `HullMath`, `ClutterRetirement`,
  `ClutterLiveRecord`, `ClutterGridNative`.
- **Assets/:** `ClutterAssets`, `ClutterGeometry`, `GlbImportLibrary`, `GlbMaterials`, `GlbTextures`
  (`GlbTexture`), `ImportedPngTexture`, `AssetUploadSubmission`.
- **Models/ (game-independent):** `PebblesRecipe` (with `Vec3`, `ColliderRecipe`, `ObjectRecipe`,
  `EcotypeRecipe`, `RecipeCopy`), `RecipeValidation`, `ClutterAuthoring`, `ClutterGridMemory`,
  `ClutterGridState`, `GlbDocument`, `GlbGeometry`, `GlbScene`, `GlbIdentity`, `GlbMaterialReader`,
  `GlbMaterialSlots`, `GlbPixels`, `GlbCompatibility`, `GlbTextureMapping`. `RecipeValidation`
  bounds every detached recipe before it is queued, saved or kept from the Workshop: at most 128
  ecotypes with unique names, 1 to 51 variants with exactly five LODs each, budgets of 1 to 20 million,
  finite positive scales and dimensions, increasing altitude curves, unique collider ids, and the
  native collision constraints (no smooth-normal orientation, uniform scale).[^recipe-validation-cs]
- **Preview/:** `WorkshopPreview`, `PreviewTarget`, `PreviewPipeline`, `PreviewScene`,
  `PreviewGeometry`, embedded `Workshop.vert.glsl` and `Workshop.frag.glsl`.
- **Workshop/:** `WorkshopEditor` (partials Canvas, Drawing, Hulls, Inspector, Assets),
  `WorkshopState`, `WorkshopHistory`, `WorkshopColliders`, `WorkshopView`, `WorkshopMath`,
  `WorkshopValidation`.
- **Shared with other sub-mods:** Sphinx borrows `ClutterAssets` (GLB geometry and materials),
  `ImportedPngTexture` and `AssetUploadSubmission`; Pebbles uses `ksa-abstractions.lib`'s
  `GlbLibrary`, `LibraryFileBrowser`, `ISubmod` and the save-participant contract.

## Scene saves and persistence

- Participant `pebbles` (order 60, version 1): one `ClutterSave` per applied body (`BodyId` plus the
  complete applied `PebblesRecipe`, colliders included). Validation allows at most 256 unique bodies
  and runs `RecipeValidation.Validate` on each. Reset releases the Workshop preview, cancels pending
  work and synchronously restores original templates and native arrays before native deserialization;
  restore reapplies each recipe synchronously with `ApplyForSaveLoad` and turns failures into save
  warnings.[^pebbles-submod-saves-cs]
- Participant `pebbles.clutter-state` (order 61): removed and displaced clutter on override grids whose
  spacing differs from stock; stock-spacing state goes into the native save through the
  `GroundClutterRenderer.SerializeSave` postfix (see
  [clutter state persistence](../features/pebbles-clutter-state-persistence.md)).
- Not saved: the main form, the Workshop draft and its camera/layout, pending commands and all GPU or
  CPU caches. Imported GLB files persist in the shared `.unscience/glbs` library and are re-resolved by
  copied filename plus SHA-256 hash.[^pebbles-readme-md]

## Further reading

- [pebbles.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/README.md) (its ownership list still mentions an `Import/` folder that was replaced by the shared `LibraryFileBrowser`)
- [pebbles.tests/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.tests/README.md)
- [scope/ground-clutter.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/ground-clutter.md)
- [scope/ground-clutter-glb-materials.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/ground-clutter-glb-materials.md)
- [plans/PEBBLES_SOURCE_MAP.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/plans/PEBBLES_SOURCE_MAP.md) (native source investigation, build 5402)
- [REPOSITORY_INDEX.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/REPOSITORY_INDEX.md#L500-L518)[^scope-ground-clutter-glb-materials-md][^plans-pebbles-source-map-md]

## Entry points

- [pebbles.lib/PebblesSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/PebblesSubmod.cs) (the `ISubmod`; no `mod.toml` or standalone `Mod.cs` exists)
- [unscience/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Mod.cs#L97-L127) (construction and controller wiring)
- [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L100) (`PebblesController?.ApplyPatches`)
- [pebbles.lib/Runtime/ClutterHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterHooks.cs#L30-L46) (every Harmony patch Pebbles installs)

## See also

- [unscience overview](../overview.md)
- [unscience supermod host](./unscience.md) (constructs Pebbles and applies its patches)
- [Sphinx](./sphinx.md) (shares the GLB importer, material conversion and upload primitive)

[^pebbles-readme-md]: pebbles.lib/README.md
[^scope-ground-clutter-md]: scope/ground-clutter.md
[^repository-index-md]: REPOSITORY_INDEX.md
[^unscience-mod-cs]: unscience/Mod.cs
[^unscience-patcher-cs]: unscience/Patcher.cs
[^clutter-hooks-cs]: pebbles.lib/Runtime/ClutterHooks.cs
[^pebbles-submod-cs]: pebbles.lib/PebblesSubmod.cs
[^pebbles-tests-readme-md]: pebbles.tests/README.md
[^pebbles-submod-applied-cs]: pebbles.lib/PebblesSubmod.Applied.cs
[^recipe-validation-cs]: pebbles.lib/Models/RecipeValidation.cs
[^pebbles-submod-saves-cs]: pebbles.lib/PebblesSubmod.Saves.cs
[^scope-ground-clutter-glb-materials-md]: scope/ground-clutter-glb-materials.md
[^plans-pebbles-source-map-md]: plans/PEBBLES_SOURCE_MAP.md
