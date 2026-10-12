---
type: Game Feature
title: Import GLB models and their materials at runtime
description: How a mod turns a player-chosen GLB into a CPU-only MeshReference without registering it, converts its materials into private bindless textures, and enumerates the game's loaded asset registries for pickers
category: assets
group: models-and-textures
topic: import-glb-models-at-runtime
demonstrated_by:
  - mods/unscience/features/pebbles-glb-import
  - mods/unscience/features/pebbles-glb-material-conversion
  - mods/unscience/features/rocky-mcrock-face-asset-catalog
  - mods/unscience/features/parts-now-bundle-validation
lifecycle: [frame:draw-ui, frame:dispatch-solvers]
game_types:
  - KSA.MeshReference
  - RenderCore.MeshAsset
  - KSA.ModLibrary
  - KSA.SerializedCollection
  - KSA.Gltf2Reference
  - KSA.FileReference
  - Brutal.GltfApi.GltfLoader
  - Brutal.GltfApi.GltfUtility
  - Brutal.Collections.NativeStrideList
  - KSA.TextureReference
  - RenderCore.SimpleVkTexture
  - RenderCore.Systems.BindlessTextureLibrary
  - Brutal.TextureApi.TextureLoader
  - KSA.Program
  - Core.Renderer
  - KSA.TexturePowerReference
  - KSA.DeviceMeshInterleaved
tags: [glb, import, runtime, registry, materials, bindless]
---

The game loads models at startup through XML and `MeshAtlas`. A mod that lets a player choose any `.glb` while the game is running has to do the work itself: parse a file it does not trust, produce a mesh the downstream renderer accepts, turn glTF materials into the game's texture conventions and keep the result out of (or
consciously inside) the game's registries.

## Engine background

- `MeshReference`: `HostPrimitives`, `PrimitiveMaterialIds`, `Load(loader, mesh, createDeviceMesh)`, `Bind`, finalizer ([current/decomp/KSA/MeshReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshReference.cs#L14-L125)).
- `ModLibrary` registries (`AllFiles`, `AllMaterials`, `AllGltfs`, `AllMeshes`) are internal static fields of `SerializedCollection<T>`; `Register`, `Get<T>`, `TryGet<T>` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L64-L84), [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L575-L800), [current/decomp/KSA/SerializedCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedCollection.cs#L9-L60)).
- `GltfLoader` / `GltfUtility.LoadModel` for the game's own parser ([current/decomp/Brutal.GltfApi/GltfLoader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.GltfApi/GltfLoader.cs#L1-L35)).
- `TextureReference` and `Bind`: `BindlessHandle` with a private setter; `BindlessTextureLibrary.AddTexture` / `FreeTexture`; `SimpleVkTexture` ([current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs#L125-L156), [current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs#L186-L215)).
- Phases: import at `frame:draw-ui`, GPU upload in a window outside ImGui emission (before the GUI or `frame:dispatch-solvers`). See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Parse a GLB with a bounded reader into unregistered CPU meshes with path-plus-hash ids](import-glb-models-at-runtime-1.md) | unscience (Pebbles) | Own bounded GLB 2.0 parser, unregistered CPU `MeshReference`, path-plus-hash ids | `frame:draw-ui`, `frame:dispatch-solvers` | Safe for untrusted files and stable in saves; you own a parser and its limits |
| [Convert glTF materials on the CPU into game maps and upload them as private bindless textures](import-glb-models-at-runtime-2.md) | unscience (Pebbles) | CPU bake into the game's map conventions, private `SimpleVkTexture` and reflected bindless handle | `frame:draw-ui`, `frame:dispatch-solvers` | Shaders see ordinary textures; ordering rules around Vulkan are strict |
| [Enumerate the game's loaded meshes, textures and glTFs for pickers and id checks](import-glb-models-at-runtime-3.md) | unscience (rocky-mcrock-face, parts-now) | Reflect `ModLibrary` registries for pickers and read-only id checks | `frame:draw-ui` | Cheap and read-only; depends on internal field names |

## See also

- [Export or generate a GLB that KSA loads correctly](../export-glb-from-blender-for-ksa/overview.md), [Declare glTF meshes and PBR materials in asset XML and author their texture sets](../declare-gltf-and-pbr-material-assets/overview.md), [Draw your own meshes through the engine's mesh renderers](../../../rendering/meshes/draw-mod-meshes-with-the-engine-renderers/overview.md), [Upload an image or procedural pixels as a GPU texture](../../../rendering/textures-and-decals/upload-an-image-as-a-vulkan-texture/overview.md),
  [Free mod-owned Vulkan resources without device loss](../../../rendering/vulkan-passes/free-vulkan-resources-safely/overview.md), [Load, validate and unload part mods in a running game](../../../vehicles-and-parts/parts/load-and-unload-part-mods-at-runtime/overview.md), [Keep grass, rocks and trees off a custom site, and replace or preserve a planet's ground clutter](../../../celestials/terrain/exclude-or-replace-ground-clutter/overview.md), [Build searchable lists, two-pane browsers and file pickers in ImGui](../../../ui/windows/build-searchable-lists-and-browsers/overview.md),
  [Reserve or grow game GPU buffers for content added at runtime](../../../rendering/meshes/grow-game-gpu-buffers-for-runtime-content/overview.md), [Add planetary rings to a body or restyle existing rings at runtime](../../../celestials/atmospheres-and-rings/add-or-restyle-planetary-rings/overview.md), [Check GLB meshes offline: z-fighting, degenerate UVs, names and bounds](../../validation/find-z-fighting-and-degenerate-uvs-in-glb/overview.md)
- [Bounded GLB 2.0 import into unregistered CPU-only meshes with frozen path and hash ids](../../../../mods/unscience/features/pebbles-glb-import.md), [GLB PBR materials converted to native clutter maps with private bindless textures](../../../../mods/unscience/features/pebbles-glb-material-conversion.md), [Enumerate every loaded mesh and bound texture from ModLibrary's internal registries](../../../../mods/unscience/features/rocky-mcrock-face-asset-catalog.md)
