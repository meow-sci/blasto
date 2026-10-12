---
type: How-To
title: Parse the GLB JSON chunk in C# to validate mesh references
description: Read only the JSON chunk of a GLB or glTF in C# to get mesh names without loading geometry, use them to check that every PartModel Mesh Id exists in a declared atlas, and list the meshes of registered glTF assets to pick from
category: assets
group: validation
topic: find-z-fighting-and-degenerate-uvs-in-glb
approach: 3
difficulty: intermediate
prerequisites: []
demonstrated_by:
  - mods/unscience/features/parts-now-bundle-validation
  - mods/unscience/features/rocky-mcrock-face-asset-catalog
techniques: [glb-model, offline-asset-validation]
hooks: [csharp GlbMeshNames.Read]
lifecycle: [frame:draw-ui, startup:asset-load]
game_types: [KSA.MeshAtlasFileReference, KSA.MeshReference, Brutal.GltfApi.GltfUtility, Brutal.GltfApi.GltfLoader]
tags: [glb, json-chunk, mesh-names, csharp, validation, atlas, catalog]
---

The Python checkers run on a developer machine. A mod that validates or loads content inside the game needs the same reading in C#. unscience's Parts Now reads only the JSON chunk of a GLB to learn which mesh names an atlas declares, so a `PartModel` `Mesh Id` can be checked against it before anything is registered; the Rocky McRock Face catalog parses glTF headers to offer character
meshes. This article shows the reader and where the names go.

Demonstrated by [Validate Part asset bundles with fifteen rules before anything is written or registered](../../../../mods/unscience/features/parts-now-bundle-validation.md) and [Enumerate every loaded mesh and bound texture from ModLibrary's internal registries](../../../../mods/unscience/features/rocky-mcrock-face-asset-catalog.md). The Python route is [Find coplanar faces and zero-area UVs with checkmesh.py and read bounds with meshinfo.py](find-z-fighting-and-degenerate-uvs-in-glb-1.md).

## Background: how the game does it

`MeshAtlasFileReference.DoLoad` registers a `MeshReference` per glTF mesh with `Id` equal to the mesh name ([current/decomp/KSA/MeshAtlasFileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshAtlasFileReference.cs#L26-L45)); `GltfUtility.LoadModel` returns the parsed JSON model without decoding buffers, which the catalog uses. The game's own loader needs `Brutal.Gltf.dll`, which a lightweight validation library may not reference, and decoding buffers and images
just to read a handful of names is wasteful. Note that `DoLoad` registers every mesh name, including ones starting with `_`; the `_` skip below is a validator and bundler convention for helper nodes, not engine behaviour ([current/decomp/KSA.GlbImport/PartAssetBundler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.GlbImport/PartAssetBundler.cs#L44-L62)).

## Lifecycle and timing

Runs on the game thread when a document is validated: from the paste panel's Validate button (`[StarMapAfterGui]`, `frame:draw-ui`) and from the loader's Validate state in `Update` (`[StarMapBeforeGui]`), before anything is written or registered. The catalog parses lazily on refresh. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Read the JSON chunk of a `.glb`, or the whole `.gltf`.** Cap the chunk size and bound-check lengths:

   ```csharp
   // parts-now.lib/Runtime/GlbMeshNames.cs L40-L78
       /// <summary>
       /// Reads the names of every mesh node in a <c>.glb</c> or <c>.gltf</c> file, in declaration order.
       /// Names beginning with <c>'_'</c> are omitted because KSA skips them when building an atlas.
       /// </summary>
       /// <param name="filePath">Absolute path to the glTF/GLB file.</param>
       /// <returns>The usable mesh node names.</returns>
       /// <exception cref="InvalidDataException">The file is not a readable glTF 2.0 container.</exception>
       public static List<string> Read(string filePath)
       {
           ArgumentException.ThrowIfNullOrEmpty(filePath);

           byte[] json = IsBinaryContainer(filePath) ? ReadJsonChunk(filePath) : File.ReadAllBytes(filePath);

           List<string> names = new List<string>();
           using JsonDocument document = JsonDocument.Parse(json);

           if (!document.RootElement.TryGetProperty("meshes", out JsonElement meshes)
               || meshes.ValueKind != JsonValueKind.Array)
           {
               return names;
           }

           foreach (JsonElement mesh in meshes.EnumerateArray())
           {
               if (!mesh.TryGetProperty("name", out JsonElement name)
                   || name.ValueKind != JsonValueKind.String)
               {
                   continue;
               }

               string? value = name.GetString();
               if (!string.IsNullOrEmpty(value) && !value.StartsWith('_'))
               {
                   names.Add(value);
               }
           }

           return names;
       }
   ```

   ```csharp
   // parts-now.lib/Runtime/GlbMeshNames.cs L88-L112
       private static byte[] ReadJsonChunk(string filePath)
       {
           using FileStream stream = File.OpenRead(filePath);
           using BinaryReader reader = new BinaryReader(stream);

           // GLB header: magic, version, total length — 12 bytes.
           reader.ReadUInt32();
           reader.ReadUInt32();
           reader.ReadUInt32();

           while (stream.Position + 8 <= stream.Length)
           {
               uint chunkLength = reader.ReadUInt32();
               uint chunkType = reader.ReadUInt32();

               if (chunkLength > MaxJsonChunkBytes || stream.Position + chunkLength > stream.Length)
               {
                   throw new InvalidDataException(
                       "GLB chunk length " + chunkLength + " is out of range for '" + filePath + "'.");
               }

               if (chunkType == JsonChunkType)
               {
                   return reader.ReadBytes((int)chunkLength);
               }
       // ...
   ```

2. **Declare the names an atlas will register.** When indexing a document set, each `<MeshAtlas>` declares one mesh per name, and a `<MeshFile>` mesh takes the file's id; the declared ids join the registered ones for the check. Atlas problems are collected, not thrown:

   ```csharp
   // parts-now.lib/Runtime/BundleValidatorContext.cs L116-L127
       private static void IndexAtlasMeshes(
           ValidationContext context,
           ParsedBundle bundle,
           MeshAtlasFileReference atlas)
       {
           // A mesh atlas declares one mesh per glTF mesh node, named by that node — the ids are only
           // knowable by reading the file, so it must exist on disk at validation time.
           string relative = atlas.LocalPath;
           if (string.IsNullOrEmpty(relative))
           {
               return;
           }
       // ...
   ```

   ```csharp
   // parts-now.lib/Runtime/BundleValidatorContext.cs L154-L166
           try
           {
               foreach (string name in GlbMeshNames.Read(absolute))
               {
                   context.DeclaredMeshIds.Add(name);
                   Declare(context, KindMesh, name, bundle.SourceName);
               }
           }
           catch (Exception ex)
           {
               context.AtlasProblems.Add((bundle.SourceName, relative,
                   ex.GetType().Name + ": " + ex.Message));
           }
   ```

3. **Check every `Mesh Id`** in the document against the set the bundle creates plus the live registry (rule V6): an unknown id throws lazily at model warm-up or draw time in the game, and in the validator it is an error naming the id, with a warning instead when the atlas could not be read ([Validate asset XML ids and paths before launching the game (offline)](../validate-asset-xml-references/overview.md) approach 3 has the full rule list).

4. **List a glTF asset's meshes for a picker.** For glTF-file assets (characters, helmet), parse the header and id each mesh `<gltfId>:<meshName>`, falling back to `mesh_<i>` for unnamed meshes and appending `#<i>` for duplicates:

   ```csharp
   // rocky-mcrock-face.lib/RingAssetCatalog.cs L105-L131
       private void RefreshGltfMeshes()
       {
           _gltfMeshById.Clear();
           var gltfs = Collection<Gltf2Reference>("AllGltfs")?.GetList();
           if (gltfs == null) return;

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
               catch (Exception ex)
               {
                   Console.WriteLine($"rocky-mcrock-face: skipping glTF asset '{gltf.Id}': {ex.Message}");
               }
       // ...
   ```

## Complete example

- [parts-now.lib/Runtime/GlbMeshNames.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/GlbMeshNames.cs), [parts-now.lib/Runtime/BundleValidatorContext.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/BundleValidatorContext.cs), [rocky-mcrock-face.lib/RingAssetCatalog.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/rocky-mcrock-face.lib/RingAssetCatalog.cs)

## Variations and gotchas

- **A name that is empty or not a string is skipped.** An unnamed mesh cannot be referenced by `Mesh Id`.
- **`.gltf` (text) files** are read whole; the binary container is detected by its magic, so the reader serves both.
- **Duplicate names across atlases** are not an engine error (first registration wins), but the validator reports them.
- **Don't decode what you don't need.** Bounds can also come from accessor `min`/`max` ([Find coplanar faces and zero-area UVs with checkmesh.py and read bounds with meshinfo.py](find-z-fighting-and-degenerate-uvs-in-glb-1.md)).

## See also

- [Find coplanar faces and zero-area UVs with checkmesh.py and read bounds with meshinfo.py](find-z-fighting-and-degenerate-uvs-in-glb-1.md), [Validate asset XML ids and paths before launching the game (offline)](../validate-asset-xml-references/overview.md), [Import GLB models and their materials at runtime](../../models-and-textures/import-glb-models-at-runtime/overview.md), [Export or generate a GLB that KSA loads correctly](../../models-and-textures/export-glb-from-blender-for-ksa/overview.md)
- [Validate Part asset bundles with fifteen rules before anything is written or registered](../../../../mods/unscience/features/parts-now-bundle-validation.md), [Enumerate every loaded mesh and bound texture from ModLibrary's internal registries](../../../../mods/unscience/features/rocky-mcrock-face-asset-catalog.md)
