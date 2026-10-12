---
type: How-To
title: Enumerate the game's loaded meshes, textures and glTFs for pickers and id checks
description: Read ModLibrary.AllMeshes, AllFiles and AllGltfs by reflection once, filter to meshes with retained CPU geometry and textures with a live bindless handle, list the named meshes of each glTF from its JSON, and refresh until the registry is populated
category: assets
group: models-and-textures
topic: import-glb-models-at-runtime
approach: 3
difficulty: intermediate
prerequisites: [starmap, reflection]
demonstrated_by:
  - mods/unscience/features/rocky-mcrock-face-asset-catalog
  - mods/unscience/features/parts-now-bundle-validation
techniques: [reflection, glb-model]
hooks: [reflection KSA.ModLibrary.AllMeshes, starmap StarMapBeforeGui]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.ModLibrary
  - KSA.SerializedCollection
  - KSA.MeshReference
  - KSA.TextureReference
  - KSA.Gltf2Reference
  - KSA.FileReference
  - Brutal.GltfApi.GltfUtility
tags: [registry, reflection, picker, modlibrary, meshes, textures, gltf, catalog]
---

To let a player pick "any mesh the game has" (a kitten, a helmet, a part, an imported file) or to check that an id exists, a mod has to read `ModLibrary`'s registries. They are `internal static` fields. unscience does it in several places the same way: resolve each collection by reflection once, keep only what is usable, refresh until the
registry is populated, and feed filterable pickers. This article covers the pattern, the filters that matter and the version risk.

Demonstrated by [Enumerate every loaded mesh and bound texture from ModLibrary's internal registries](../../../../mods/unscience/features/rocky-mcrock-face-asset-catalog.md) and [Validate Part asset bundles with fifteen rules before anything is written or registered](../../../../mods/unscience/features/parts-now-bundle-validation.md).

## Background: how the game does it

`ModLibrary` keeps its asset registries as `internal static readonly SerializedCollection<T>` fields (`AllFiles`, `AllMaterials`, `AllGltfs`, `AllMeshes`, parts and others), and `SerializedCollection<T>` is public with `GetList()`, `Find(KeyHash)` and `Register`; there is no removal API in the public surface
([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L64-L84), [current/decomp/KSA/SerializedCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedCollection.cs#L9-L60)). `GetList()` returns the live list, so copy it before iterating. The typed lookups `Has<T>`, `TryGet<T>` and `Get<T>` dispatch on `typeof(T)`; `Get` throws, `TryGet` does not, but `TryGet` has no branch for some types
(KSArmory records that `Has<PartTemplate>` answers false for every part; [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L575-L800)).

## Lifecycle and timing

The registries fill during `ModLibrary.LoadAll` and `ModLibrary.Bind` (`startup:asset-load`), with bindless handles assigned later at bind time. Both mods poll: Pebbles' `Update` rescans its shared library every two seconds and, every five seconds, refreshes the registry views until they are found ([pebbles.lib/PebblesSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/PebblesSubmod.cs#L30-L36)); that runs from `ISubmod.Update` (`[StarMapBeforeGui]`); the ring picker refreshes on demand from the UI
(`frame:draw-ui`). Do not read registries from a loader or worker thread: the lists are live. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Resolve each collection by reflection once, in one place.** A rename in a game update then costs one edit. Return an empty array, not an exception, when a field is missing:

   ```csharp
   // pebbles.lib/Assets/ClutterAssets.cs L107-L112
       internal static T[] Collection<T>(string name) where T : ILibraryData, IListable
       {
           var field = typeof(ModLibrary).GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
               ?? throw new MissingFieldException(typeof(ModLibrary).FullName, name);
           return field.GetValue(null) is SerializedCollection<T> collection ? collection.GetList().ToArray() : [];
       }
   ```

2. **Keep only usable meshes.** `MeshReference` with `HostPrimitives` of length greater than zero (retained CPU geometry), skipping `_VM` meshes (invisible MeshView hulls). Copy the list before iterating:

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

3. **Keep only bound textures.** `TextureReference` entries with a non-zero `BindlessHandle`; split out `TexturePowerReference` for normal maps:

   ```csharp
   // pebbles.lib/Assets/ClutterAssets.cs L48-L58
       public void Refresh()
       {
           _meshes.Clear(); _textures.Clear(); _gltfs.Clear();
           foreach (var mesh in Collection<MeshReference>("AllMeshes"))
               if (!string.IsNullOrEmpty(mesh.Id) && mesh.HostPrimitives is { Length: > 0 }) _meshes.TryAdd(mesh.Id, mesh);
           foreach (var file in Collection<FileReference>("AllFiles"))
               if (file is TextureReference texture && !string.IsNullOrEmpty(texture.Id) && texture.BindlessHandle != 0)
                   _textures.TryAdd(texture.Id, texture);
           foreach (var gltf in Collection<Gltf2Reference>("AllGltfs"))
           {
               if (gltf.Source == null || !File.Exists(gltf.Source.ModPath)) continue;
       // ...
   ```

4. **List a glTF asset's meshes from its JSON header.** Characters and attachments load through a separate pipeline, so list each mesh of every registered glTF by parsing only the JSON with `GltfUtility.LoadModel` and id them `<gltfId>:<meshName>`; load buffers lazily:

   ```csharp
   // pebbles.lib/Assets/ClutterAssets.cs L56-L75
           // ...
           foreach (var gltf in Collection<Gltf2Reference>("AllGltfs"))
           {
               if (gltf.Source == null || !File.Exists(gltf.Source.ModPath)) continue;
               try
               {
                   var json = GltfUtility.LoadModel(gltf.Source.ModPath);
                   if (json.Meshes == null) continue;
                   for (var i = 0; i < json.Meshes.Length; i++)
                   {
                       var name = json.Meshes[i].Name;
                       var id = $"{gltf.Id}:{(string.IsNullOrEmpty(name) ? $"mesh_{i}" : name)}";
                       if (_gltfs.ContainsKey(id)) id += $"#{i}";
                       _gltfs[id] = (gltf.Source.ModPath, i);
                   }
               }
               catch (Exception ex) { Console.WriteLine($"pebbles: cannot inspect {gltf.Id}: {ex.Message}"); }
           }
           RegistryDiscovered = true;
           RefreshSharedLibrary();
       }
   ```

5. **Load a chosen glTF mesh CPU-only.** `MeshReference.Load(loader, index, createDeviceMesh: false)` keeps it off the GPU and out of the registry:

   ```csharp
   // pebbles.lib/Assets/ClutterAssets.cs L83-L96
       public MeshReference ResolveMesh(string id)
       {
           if (GlbLibrary.IsSelection(id)) throw new InvalidOperationException("Resolve the library choice before applying the recipe.");
           if (id.StartsWith(GlbIdentity.Prefix, StringComparison.Ordinal)) return _external.ResolveMesh(id);
           if (_meshes.TryGetValue(id, out var mesh)) return mesh;
           if (_imports.TryGetValue(id, out mesh)) return mesh;
           if (!_gltfs.TryGetValue(id, out var file)) throw new InvalidOperationException($"Mesh '{id}' is unavailable. Refresh assets or select another mesh.");
           using var loader = new GltfLoader(file.Path);
           mesh = new MeshReference { Id = id };
           mesh.Load(loader, file.Index, createDeviceMesh: false);
           mesh.SetHash();
           _imports.Add(id, mesh);
           return mesh;
       }
   ```

6. **Use the same reflection layer for validation.** parts-now keeps every registry member it needs resolved in one static constructor that never throws and reports failures through a self-test; its id-collision rules and editor-tag lookup read from there:

   ```csharp
   // parts-now.lib/Runtime/GameRegistry.cs L69-L88
       static GameRegistry()
       {
           PartsField = Collection<PartTemplate>("AllParts");
           MeshesField = Collection<MeshReference>("AllMeshes");
           FilesField = Collection<FileReference>("AllFiles");
           MaterialsField = Collection<PbrMaterialReference>("AllMaterials");
           PartGameDataField = Collection<PartGameDataReference>("AllPartGameDataReferences");
           EditorTagDefsField = Collection<EditorTagDefinition>("AllEditorTagDefinitions");

           EditorTagLookupField = ResolveEditorTagLookup();

           // Probe the removal path once, on an arbitrary closed generic, so a rename shows up in
           // SelfTest() rather than at the first Unregister() call during a rollback.
           if (CollectionFields<PartTemplate>.Collection is null)
           {
               FatalProblems.Add(
                   "SerializedCollection<T>._collection not found — KSA internals changed "
                   + "(unload and rollback are unavailable).");
           }
       }
   ```

7. **Feed filterable ImGui combos** with the sorted id lists (a shared widget between two mods); see [Build searchable lists, two-pane browsers and file pickers in ImGui](../../../ui/windows/build-searchable-lists-and-browsers/overview.md).

## Complete example

- [pebbles.lib/Assets/ClutterAssets.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Assets/ClutterAssets.cs), [rocky-mcrock-face.lib/RingAssetCatalog.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/rocky-mcrock-face.lib/RingAssetCatalog.cs)
- [parts-now.lib/Runtime/GameRegistry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/GameRegistry.cs) (registry views and removal paths)

## Variations and gotchas

- **Live lists.** `GetList()` returns the live backing list; iterate a copy.
- **Not every mesh is renderable.** Meshes without retained host geometry, textures without a bindless handle (unbound yet) and `_VM` hulls are not useful pickers.
- **Version risk.** Registry field names and types are internal; keep the lookup in one class and fail soft ("could not resolve ModLibrary.AllMeshes").
- **Reading is safe; removing is not.** The public surface has no `Remove`; parts-now reaches the private collection to unload runtime parts ([Load, validate and unload part mods in a running game](../../../vehicles-and-parts/parts/load-and-unload-part-mods-at-runtime/overview.md)).
- **Cost.** Parsing glTF JSON for every registered glTF is cheap but not free; do it on a timer or on demand, not every frame.

## See also

- [Parse a GLB with a bounded reader into unregistered CPU meshes with path-plus-hash ids](import-glb-models-at-runtime-1.md), [Convert glTF materials on the CPU into game maps and upload them as private bindless textures](import-glb-models-at-runtime-2.md), [Build searchable lists, two-pane browsers and file pickers in ImGui](../../../ui/windows/build-searchable-lists-and-browsers/overview.md), [Add planetary rings to a body or restyle existing rings at runtime](../../../celestials/atmospheres-and-rings/add-or-restyle-planetary-rings/overview.md),
  [Reading, writing and calling private game members](../../../patching/internals/call-private-game-members-through-reflection-and-delegates/overview.md)
- [Enumerate every loaded mesh and bound texture from ModLibrary's internal registries](../../../../mods/unscience/features/rocky-mcrock-face-asset-catalog.md), [Validate Part asset bundles with fifteen rules before anything is written or registered](../../../../mods/unscience/features/parts-now-bundle-validation.md)
