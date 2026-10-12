---
type: How-To
title: Lay out mod-folder-relative paths and prefixed global asset ids
description: Keep Path attributes relative to the mod folder, prefix every Id with the mod name, know that a second registration of an Id is ignored and that glTF mesh names are Ids, and keep paths case-exact for Linux
category: assets
group: asset-xml
topic: resolve-asset-ids-and-mod-relative-paths
approach: 1
difficulty: beginner
prerequisites: []
demonstrated_by: [mods/ksacars/features/car-part-assets-glb-pbr, mods/ksagolf/features/prop-and-course-asset-xml]
techniques: [xml-asset]
hooks: [xml Path and Id attributes]
lifecycle: [startup:asset-load]
game_types:
  - KSA.Mod
  - KSA.FileReference
  - KSA.SerializedId
  - KSA.ModLibrary
  - KSA.MeshAtlasFileReference
  - KSA.MeshReference
tags: [xml, ids, paths, mod-folder, registry, first-wins, case-sensitivity]
---

Every `Path` in your asset XML is resolved against the folder that holds `mod.toml`, not against the XML file, and every
`Id` is a key in a registry shared by all mods (one registry per asset type). Get these two rules wrong and the symptoms are quiet: an
untextured part, an invisible mesh, a sound that never plays, or your asset replaced by someone else's. This article
lays out the folder and naming scheme KSACars and KSAGolf use and ties each rule to the engine code.

Demonstrated by [Car part assets: GLB atlases, PBR materials and subparts](../../../../mods/ksacars/features/car-part-assets-glb-pbr.md) and [Prop and course asset XML](../../../../mods/ksagolf/features/prop-and-course-asset-xml.md). The sibling
approach for reaching files that belong to another mod or to Core is [Reach another mod's or Core's files with absolute paths and Id-only references](resolve-asset-ids-and-mod-relative-paths-2.md).

## Background: how the game does it

`FileReference.LocalPath` is the XML `Path` attribute. `ModPath` joins it to the mod through `Mod.GetPath`:

```csharp
// current/decomp/KSA/FileReference.cs L10-L26
public class FileReference : SerializedId, ILoader, IKeyed
{
	[XmlAttribute("Path")]
	public string LocalPath = string.Empty;

	[XmlIgnore]
	public long Size = 0L;

	[XmlIgnore]
	private bool _isReference;

	[XmlIgnore]
	public int LookupIndex { get; set; }

	public string ModPath => base.Mod?.GetPath(LocalPath) ?? throw new NullReferenceException("Mod is null for '" + LocalPath + "'");

	public string ContentPath => Path.Combine("Content", ModPath);
// ...
```

`Mod.GetPath` is a `Path.Combine` with the mod's directory (the folder that holds `mod.toml`) plus a separator
normalisation:

```csharp
// current/decomp/KSA/Mod.cs L389-L392
	public string GetPath(string localPath)
	{
		return Filepath.CorrectSeparators(Path.Combine(DirectoryPath, localPath));
	}
```

`SerializedId.Id` is the registry key. If a file element gives no `Id`, `FileReference.OnDataLoad` makes the Id the
absolute path. Outside preload, an element with an empty `Path` is a reference to an already registered asset, and
`Load()` downgrades a file element to a reference if registration is refused because the Id exists:

```csharp
// current/decomp/KSA/FileReference.cs L39-L72
	public override void OnDataLoad(Mod mod)
	{
		base.OnDataLoad(mod);
		if (!base.IsReferenceable)
		{
			base.Id = ModPath;
			SetHash();
		}
		if (!mod.Preload)
		{
			_isReference = string.IsNullOrEmpty(LocalPath);
			if (!_isReference)
			{
				ModLibrary.RegisterLoader(this);
			}
		}
	}

	public override bool IsReference()
	{
		return _isReference;
	}

	public override SerializedId Populate()
	{
		throw new NotImplementedException();
	}

	public void Load()
	{
		if (!ModLibrary.Register(this))
		{
			_isReference = true;
			return;
	// ...
```

`SerializedCollection.Register` is a `TryAdd`, so the first registration wins and later ones are ignored without any
log ([current/decomp/KSA/SerializedCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedCollection.cs#L20-L33)). A `MeshAtlas` makes this matter for meshes: it registers one
`MeshReference` per glTF mesh with `Id` equal to the mesh name, and a duplicate quietly reuses the existing mesh:

```csharp
// current/decomp/KSA/MeshAtlasFileReference.cs L26-L50
	internal override void DoLoad()
	{
		using GltfLoader gltfLoader = new GltfLoader(base.ModPath);
		Material[] materials = gltfLoader.GltfJson.Materials;
		MaterialCount = ((materials == null) ? 1 : materials.Length);
		for (int i = 0; i < gltfLoader.GltfJson.Meshes.Length; i++)
		{
			MeshReference meshReference = new MeshReference();
			meshReference.Id = gltfLoader.GltfJson.Meshes[i].Name;
			meshReference.Simple = false;
			meshReference.Interleaved = true;
			meshReference.OnDataLoad(base.Mod);
			if (!ModLibrary.Register(meshReference))
			{
				Meshes.Add(ModLibrary.Get<MeshReference>(meshReference.Id));
				continue;
			}
			meshReference.Load(gltfLoader, i, !HostOnly);
			Meshes.Add(meshReference);
		}
		if (!IsReference())
		{
			ModLibrary.Register(this);
		}
	}
```

## Lifecycle and timing

`startup:asset-load`. Ids are registered while `Mod.LoadAssetBundles` runs (`ModLibrary.LoadAll`), file contents load in
the parallel `ILoader` pass after all XML is read, and code looks ids up later (`ModLibrary.Get<T>` throws a
`NullReferenceException` for an unknown id, `TryGet<T>` and `Has<T>` do not;
[current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L575-L580), [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1059-L1085)). Mods are visited in manifest order with Core first, so an
earlier mod's id wins a collision. See [Understand when and in which order KSA loads mod XML](../mod-xml-load-order/overview.md) and [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Put the XML at the mod root with `Meshes/`, `Textures/` and `Sounds/` beside it.** This mirrors Core and makes
   the question of "relative to what?" moot. KSACars installs as:

   ```text
   mods/FastAndPurrious/
     KSACars.dll
     mod.toml
     KSACarsAssets.xml
     KSACarsGameData.xml
     KSACarsSounds.xml
     Meshes/KSACars_*.glb
     Textures/KSACars_*.png
     Sounds/KSACars_*.wav
   ```

   (layout from the KSACars notes, see [One folder as both a content mod and a StarMap code mod](../../../../mods/ksacars/features/mod-folder-content-and-code-mod.md).) List every XML in `mod.toml`
   `assets`.

2. **Write every `Path` relative to that folder**, with forward slashes (`Mod.GetPath` converts backslashes to `/`):

   ```xml
   <!-- src/KSACars/KSACarsAssets.xml L17-L24 -->
     <MeshAtlas Path="Meshes/KSACars_Buggy.glb" />

     <PbrMaterial Id="KSACars_Buggy_Material">
       <Diffuse Path="Textures/KSACars_Buggy_Diffuse.png" Category="Vessel" />
       <Normal Path="Textures/KSACars_Buggy_Normal.png" Category="Vessel" />
       <AoRoughMetal Path="Textures/KSACars_Buggy_PBR.png" Category="Vessel" />
       <Emissive Path="Textures/KSACars_Buggy_Emissive.png" Category="Vessel" />
     </PbrMaterial>
   ```

3. **Prefix every `Id` with your mod name** (`KSACars_`, `KSAGolf_`, `KSArmory_`). Ids are shared across all mods
   and Core within each registry, and the first registration wins:

   ```xml
   <!-- src/KSAGolf/KSAGolfProps.xml L5-L12 -->
       <GltfFile Id="KSAGolf_Ball_Glb">
           <Source Path="Meshes/KSAGolf_Ball.glb"/>
       </GltfFile>
       <PbrMaterial Id="KSAGolf_Ball_Material">
           <Diffuse Path="Textures/KSAGolf_Ball_Diffuse.png" Category="Default"/>
           <Normal Path="Textures/KSAGolf_Ball_Normal.png" Category="Default"/>
           <AoRoughMetal Path="Textures/KSAGolf_Ball_PBR.png" Category="Default"/>
       </PbrMaterial>
   ```

4. **Know that registries are per type.** A `SubPart` is registered in the part table (`AllParts`), a glTF mesh in the mesh table (`AllMeshes`), a
   material in `AllMaterials`; `ModLibrary.Register` has one overload per type ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L581-L676)). A part and a mesh may therefore
   share a name without clashing: Core's own files do (the SubPart `CoreFuelTankA_Subpart_RadialMPQuarterWA` and its mesh have the same Id):

   ```xml
   <!-- current/Content/Core/CoreFuelTankAAssets.xml L11-L19 -->
     <SubPart Id="CoreFuelTankA_Subpart_RadialMPQuarterWA">
       <PartModelDynamic Id="CoreFuelTankA_Subpart_RadialMPQuarterWA_Model">
         <Mesh Id="CoreFuelTankA_Subpart_RadialMPQuarterWA" />
         <Material Id="CoreFuelTankA_Material" />
       </PartModelDynamic>
       <MeshView>
         <Mesh Id="CoreFuelTankA_Subpart_RadialMPQuarterWA_VM" />
       </MeshView>
     </SubPart>
   ```

   KSArmory still keeps the two namespaces apart (subparts `KSArmory_<name>_SubPart`, atlas meshes `KSArmory_Subpart_<name>`), for a reason outside the
   game: an external importer (SpaceDock's, warning KSAM-0602 per the mod's comment) flags the duplicate, and its offline validator can then fail on any
   accidental duplicate. (The same comment says the game loader keeps whichever of the two it saw first; with the
   separate `AllParts` and `AllMeshes` tables that is not what the decompiled registration does, and the mod reports that it loaded and ran anyway.)

   ```xml
   <!-- src/KSArmory/KSArmoryAssets.xml L326-L334 -->
     <SubPart Id="KSArmory_CiwsBase_SubPart">
       <PartModel Id="KSArmory_Subpart_CiwsBase_Model">
         <Mesh Id="KSArmory_Subpart_Mk15Base" />
         <Material Id="KSArmory_Ciws_Material" />
       </PartModel>
       <MeshView>
         <Mesh Id="KSArmory_Subpart_Mk15Base_VM" />
       </MeshView>
     </SubPart>
   ```

   What does clash is two registrations of the same type: two meshes of one name (in any atlas or mod), two parts, two materials.

5. **Name each mesh uniquely in the GLB**, because the mesh name is the Id; and use the same name for the node
   (see [Export or generate a GLB that KSA loads correctly](../../models-and-textures/export-glb-from-blender-for-ksa/overview.md)).

6. **Keep paths case-exact.** Windows and macOS file systems are case-insensitive, so a typo in
   `Textures/airdefence_diffuse.png` loads there and fails on Linux, where KSA also runs. KSAGolf's offline checker
   compares the name against the real directory listing:

   ```python
   # tools/validate-parts.py L43-L53
   def exists_case_exact(path):
       """True only if the file exists with exactly this name.

       plain is_file() is not enough: Windows and macOS filesystems are case-insensitive, so a
       `Textures/airdefence_diffuse.png` typo loads there and fails on Linux, where KSA also runs.
       Comparing against the real directory listing catches it on any platform.
       """
       if not path.is_file():
           return False
       return path.name in {entry.name for entry in path.parent.iterdir()}

   ```

7. **Look the assets up by id from code**, as KSAGolf does: the XML only registers `KSAGolf_Ball_Glb` and
   `KSAGolf_Ball_Material`, and the code asks for them by string through the game's renderer systems:

   ```csharp
   // src/KSAGolf/Ksa/AttachmentMesh.cs L26-L52
       public static bool TryBuild(string gltfId, string materialId, out StaticMeshRenderable? mesh, out string why, bool seeThrough = false)
       {
           mesh = null;
           why = string.Empty;
           try
           {
               if (AssetNameType is null)
               {
                   why = "LoadedAssetRef.Id not found";
                   return false;
               }

               SuperMeshRenderSystem system = Program.Instance.SuperMeshRenderSystem;
               object gltfName = Activator.CreateInstance(AssetNameType, gltfId)!;
               object materialName = Activator.CreateInstance(AssetNameType, materialId)!;

               if (Load(system, "GltfSystem", gltfName) is not GltfPbrAssetRef gltf)
               {
                   why = $"glTF '{gltfId}' did not load";
                   return false;
               }

               if (Load(system, "MaterialSystem", materialName) is not GpuObjectAssetRef material)
               {
                   why = $"material '{materialId}' did not load";
                   return false;
               }
       // ...
   ```

   Keep the ids in constants or a catalogue (KSAGolf's `PieceCatalogue` maps piece ids to glTF ids) so a rename is
   one edit.

## Complete example

- [src/KSACars/KSACarsAssets.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsAssets.xml) (atlas, material, subparts, part, all `KSACars_` prefixed)
- [src/KSAGolf/KSAGolfProps.xml](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/KSAGolfProps.xml), [src/KSAGolf/KSAGolfCourse.xml](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/KSAGolfCourse.xml) (ids used only by code)
- [tools/validate-parts.py](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/validate-parts.py) (duplicate ids, case-exact paths; see [Validate asset XML ids and paths before launching the game (offline)](../../validation/validate-asset-xml-references/overview.md))

## Variations and gotchas

- **Collisions are silent** (and per type). Two mods that both register `Skin_Diffuse` leave the later mod using the earlier mod's
  texture. Prefixes are the only protection.
- **An Id-less texture gets its absolute path as Id.** Two materials that point at the same `Path` register one texture; the
  second becomes a reference to the first. KSAGolf shares one normal map across its ghost materials this way;
  KSAStructures instead gives every material its own texture files, citing an observed problem with a shared path.
- **A mistyped `Path` errors, a mistyped `Id` throws.** A missing file logs `mod file '...' not found at '...'`
  from `FileReference.Load`; an unknown `Mesh Id` or `Material Id` throws `NullReferenceException` when the reference is
  resolved (`PartModel.Template.Get` calls `Mesh?.Get()` and `Material?.Get()`; [current/decomp/KSA/PartModelModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModelModule.cs#L55-L61)).
- **Removing or renaming a shipped `Id`** breaks saves that stored it ([Change a shipped part without breaking saves](../../../vehicles-and-parts/parts/evolve-a-shipped-part-without-breaking-saves/overview.md)).
- **Paths that start with a drive or slash** escape the mod folder (next article).

## See also

- [Reach another mod's or Core's files with absolute paths and Id-only references](resolve-asset-ids-and-mod-relative-paths-2.md), [Know which asset XML elements the game accepts and what they load](../asset-xml-element-reference/overview.md), [Declare glTF meshes and PBR materials in asset XML and author their texture sets](../../models-and-textures/declare-gltf-and-pbr-material-assets/overview.md),
  [Ship assets and code in one mod folder and find it at runtime](../../../getting-started/loader/ship-content-and-code-in-one-mod/overview.md)
- [Car part assets: GLB atlases, PBR materials and subparts](../../../../mods/ksacars/features/car-part-assets-glb-pbr.md), [Prop and course asset XML](../../../../mods/ksagolf/features/prop-and-course-asset-xml.md)
