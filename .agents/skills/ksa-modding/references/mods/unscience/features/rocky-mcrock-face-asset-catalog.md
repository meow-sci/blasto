---
type: Mod Feature
title: Enumerate every loaded mesh and bound texture from ModLibrary's internal registries
description: RingAssetCatalog reflects the internal static ModLibrary.AllMeshes, AllFiles and AllGltfs collections, keeps meshes with retained CPU geometry, textures with a live bindless handle and normal maps, parses glTF headers for character meshes, and feeds filterable ImGui combos shared with Bloomin' Onion
mod: unscience
submod: rocky-mcrock-face
category: assets
techniques: [reflection, imgui-window, glb-model]
hooks:
  - starmap StarMapBeforeGui (via unscience ISubmod.Update; refresh every 2 s until populated)
  - starmap StarMapAfterGui (via unscience ISubmod.RenderContent; Rescan Assets button and combos)
  - reflection KSA.ModLibrary.AllMeshes
  - reflection KSA.ModLibrary.AllFiles
  - reflection KSA.ModLibrary.AllGltfs
lifecycle: [frame:draw-ui, frame:prepare]
game_types:
  - KSA.ModLibrary
  - KSA.SerializedCollection
  - KSA.MeshReference
  - KSA.FileReference
  - KSA.TextureReference
  - KSA.TexturePowerReference
  - KSA.Gltf2Reference
  - KSA.DeviceMeshInterleaved
  - Brutal.GltfApi.GltfUtility
tags: [assets, modlibrary, reflection, meshes, textures, gltf, imgui]
---

Both ring sub-mods let the player pick "any mesh in the game" and "any texture in the game" from
filterable dropdowns: about 800 meshes, including every part and subpart mesh, the meshes inside
the kitten, helmet and MMU glTF files, and every texture that is already on the GPU. The game has
no public listing API for those, so Rocky McRock Face reads the asset registries directly and
filters them down to entries that are actually usable at runtime.[^ring-asset-catalog]

Mechanism in one line: `typeof(ModLibrary).GetField("AllMeshes" | "AllFiles" | "AllGltfs",
Static | NonPublic | Public)` returns the internal `SerializedCollection<T>`, whose public
`GetList()` exposes the live registry list, which is copied and filtered.

## How it works

`RingAssetCatalog.Refresh()` builds four id-keyed dictionaries (case-insensitive):

| Source registry | Filter | Result |
|---|---|---|
| `ModLibrary.AllMeshes` (`SerializedCollection<MeshReference>`) | Non-empty id, `HostPrimitives[0]` present (CPU geometry retained, so convertible), skip ids ending in `_VM` (MeshView pick and collision hulls) | `MeshIds` / `TryGetMesh` |
| `ModLibrary.AllGltfs` (`SerializedCollection<Gltf2Reference>`) | Has a `Source` file that exists; parse only the JSON header with `GltfUtility.LoadModel(path)`; skip files without meshes | One `GltfMeshEntry` per mesh, id `"<gltf id>:<mesh name>"` (suffixed `#i` on collision) |
| `ModLibrary.AllFiles` (`SerializedCollection<FileReference>`) | `is TextureReference`, non-empty id, `BindlessHandle != 0` (bound, so it has an image view) | `TextureIds` / `TryGetTexture` |
| same | `is TexturePowerReference` | `NormalTextureIds` / `TryGetNormalTexture` (the type `PbrMaterialReference.NormalReference` requires) |

The three registries are `internal static readonly` fields in build 5554, and
`SerializedCollection<T>.GetList()` returns the backing `_all` list itself, so the catalog copies
it with `ToArray()` before iterating.[^ksa-mod-library][^ksa-serialized-collection]

Other details:

- `GetMeshIndexCount(id)` reads `DeviceMeshesInterleaved[0].IndexCount` or
  `DevicePrimitives[0].IndexCount`, used by the panels to show triangle cost per LOD.
- glTF-file meshes are imported only when selected (see the mesh clone feature); the catalog scan
  parses JSON headers but loads no buffers.
- Bloomin' Onion owns its own catalog instance and filters it further: control-strip candidates
  must pass `RingReferenceBuilder.IsCpuSampleable` (uncompressed, 4 bytes per texel).[^bloomin-submod]
- `RockyUi` is a small public set of ImGui helpers (`IdCombo`, form tables, parameter grids, danger
  buttons) that Bloomin' Onion reuses. `IdCombo` is a combo whose first entry is an empty id (game
  default), with a focused filter text box that is cleared each time the popup opens.[^rocky-ui-helpers][^bloomin-ui-sections]

## Lifecycle and timing

| Call | Where | Phase |
|---|---|---|
| First population | `ISubmod.Update`, every 2 s until both mesh and texture lists are non-empty | `frame:draw-ui` (also during the hidden-HUD replay) |
| Rescan Assets | Button in `RenderContent` | `frame:draw-ui` |
| Save restore | `RestoreRingSwaps` / `RestoreRings` refresh the catalog before resolving saved ids | `frame:prepare` |

The registries are populated during asset load and textures receive bindless handles when bound,
so an early scan (main menu) can come back empty; the retry loop stops as soon as both lists have
entries. Everything runs on the main thread, where the registries are written by the game's loader
and by runtime part loaders, so no locking is needed. Content registered later (for example parts
added at runtime by Parts Now) appears only after a manual Rescan.

## Key code

| Path | Role |
|---|---|
| [rocky-mcrock-face.lib/RingAssetCatalog.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/rocky-mcrock-face.lib/RingAssetCatalog.cs) | Registry reflection, filters, glTF header scan |
| [rocky-mcrock-face.lib/RockyUi.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/rocky-mcrock-face.lib/RockyUi.cs#L15-L54) | Filterable id combo shared with Bloomin' Onion |
| [rocky-mcrock-face.lib/RockyMcRockFaceSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/rocky-mcrock-face.lib/RockyMcRockFaceSubmod.cs#L35-L53) | Retry-until-populated refresh |
| [bloomin-onion.lib/BloominOnionSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/BloominOnionSubmod.cs#L46-L102) | Second consumer with its own control-strip filter |

## Snippets

The one reflective step: get the internal registry by name:

```csharp
// rocky-mcrock-face.lib/RingAssetCatalog.cs L162-L167
    private static SerializedCollection<T>? Collection<T>(string fieldName) where T : ILibraryData, IListable
    {
        var field = typeof(ModLibrary).GetField(
            fieldName, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        return field?.GetValue(null) as SerializedCollection<T>;
    }
```

Meshes: copy the live list, keep only convertible entries:

```csharp
// rocky-mcrock-face.lib/RingAssetCatalog.cs L78-L98
    private void RefreshMeshes()
    {
        _meshById.Clear();
        var meshes = Collection<MeshReference>("AllMeshes")?.GetList();
        if (meshes == null)
        {
            Console.WriteLine("rocky-mcrock-face: could not resolve ModLibrary.AllMeshes");
            MeshIds = Array.Empty<string>();
            return;
        }

        // Copy before iterating — GetList() returns the live registry list.
        foreach (var mesh in meshes.ToArray())
        {
            if (string.IsNullOrEmpty(mesh.Id)) continue;
            if (mesh.HostPrimitives is not { Length: > 0 } || mesh.HostPrimitives[0] == null) continue;
            // "_VM" meshes are invisible MeshView pick/collision hulls — not useful visually.
            if (mesh.Id.EndsWith("_VM", StringComparison.OrdinalIgnoreCase)) continue;
            _meshById[mesh.Id] = mesh;
        }
    }
```

Textures: only bound ones, with normal maps split out by type:

```csharp
// rocky-mcrock-face.lib/RingAssetCatalog.cs L148-L157
        foreach (var file in files.ToArray())
        {
            if (file is not TextureReference texture) continue;
            if (string.IsNullOrEmpty(texture.Id)) continue;
            // Unbound textures (handle 0) have no GPU image view and would sample as empty.
            if (texture.BindlessHandle == 0) continue;
            _textureById[texture.Id] = texture;
            if (texture is TexturePowerReference normal)
                _normalById[normal.Id] = normal;
        }
```

Character and equipment meshes from glTF files, header only:

```csharp
// rocky-mcrock-face.lib/RingAssetCatalog.cs L111-L127
        foreach (var gltf in gltfs.ToArray())
        {
            if (string.IsNullOrEmpty(gltf.Id) || gltf.Source == null) continue;
            try
            {
                string filePath = gltf.Source.ModPath;
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) continue;
                var json = GltfUtility.LoadModel(filePath);
                if (json?.Meshes == null) continue; // animation-only files carry no meshes
                for (int i = 0; i < json.Meshes.Length; i++)
                {
                    string meshName = string.IsNullOrEmpty(json.Meshes[i].Name) ? $"mesh_{i}" : json.Meshes[i].Name;
                    string id = $"{gltf.Id}:{meshName}";
                    if (_gltfMeshById.ContainsKey(id)) id = $"{gltf.Id}:{meshName}#{i}";
                    _gltfMeshById[id] = new GltfMeshEntry(id, filePath, i);
                }
            }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `ModLibrary.AllFiles`, `AllGltfs`, `AllMeshes` (internal static) | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L68-L80) | Registries read by reflection |
| `SerializedCollection<T>.GetList()` | [current/decomp/KSA/SerializedCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedCollection.cs#L62-L65) | Returns the live backing list |
| `MeshReference.HostPrimitives`, `DevicePrimitives`, `DeviceMeshesInterleaved` | [current/decomp/KSA/MeshReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshReference.cs#L38-L58) | Convertibility and index-count readout |
| `TextureReference.BindlessHandle` | [current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs#L70) | Zero means not bound yet |
| `Gltf2Reference.Source` | [current/decomp/KSA/Gltf2Reference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Gltf2Reference.cs#L10) | File behind a glTF asset |
| `GltfUtility.LoadModel(string)` | [current/decomp/Brutal.GltfApi/GltfUtility.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.GltfApi/GltfUtility.cs#L38-L43) | JSON-only parse for mesh names |

## Pitfalls and version notes

- The field names are strings: a rename returns null and the catalog logs "could not resolve" and
  stays empty; nothing crashes, but the dropdowns are empty.
- Never iterate `GetList()` directly while something might register assets; it is the live list.
- Filtering on `BindlessHandle != 0` hides textures that are loaded but not yet bound (and so would
  sample as empty); the catalog therefore depends on when it is refreshed.
- `TextureIds` includes block-compressed textures. That is fine for GPU-sampled slots (band,
  material maps) but not for the CPU-sampled ring control strip, which Bloomin' Onion filters
  separately.
- The catalog holds references to live game objects; it does not own or dispose them.

## See also

- [Interleaved mesh clone](./rocky-mcrock-face-interleaved-mesh-clone.md) (turning a catalog mesh into a ring LOD)
- [Ring asset swap](./rocky-mcrock-face-ring-asset-swap.md) and [runtime planetary rings](./bloomin-onion-runtime-planetary-rings.md) (consumers)
- [Parts Now runtime part loading](./parts-now-runtime-part-loading.md) (another user of the same registries)
- [Rocky McRock Face sub-mod](../submods/rocky-mcrock-face.md) and [mod overview](../overview.md)
- [Enumerate the game's loaded meshes, textures and glTFs for pickers and id checks](../../../howto/assets/models-and-textures/import-glb-models-at-runtime/import-glb-models-at-runtime-3.md) - how-to article demonstrating this feature
- [Parse the GLB JSON chunk in C# to validate mesh references](../../../howto/assets/validation/find-z-fighting-and-degenerate-uvs-in-glb/find-z-fighting-and-degenerate-uvs-in-glb-3.md) - how-to article demonstrating this feature
- [Mutate the public ring reference tree, snapshot originals and recreate the rings renderer](../../../howto/celestials/atmospheres-and-rings/add-or-restyle-planetary-rings/add-or-restyle-planetary-rings-2.md) - how-to article demonstrating this feature

[^ring-asset-catalog]: rocky-mcrock-face.lib/RingAssetCatalog.cs
[^ksa-mod-library]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-serialized-collection]: KSA.SerializedCollection (decompiled, build 2026.10.10.5554)
[^bloomin-submod]: bloomin-onion.lib/BloominOnionSubmod.cs
[^rocky-ui-helpers]: rocky-mcrock-face.lib/RockyUi.cs
[^bloomin-ui-sections]: bloomin-onion.lib/BloominOnionSubmod.UiSections.cs
