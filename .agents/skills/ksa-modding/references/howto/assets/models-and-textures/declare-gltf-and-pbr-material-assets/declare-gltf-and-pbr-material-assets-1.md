---
type: How-To
title: Declare bare GltfFile and PbrMaterial assets and pair them by id in code
description: Register a mesh and a look with GltfFile and PbrMaterial only, no Part or StaticObject, with all three texture channels, unit conventions, a shared atlas and see-through tint materials, then pair them by id at runtime
category: assets
group: models-and-textures
topic: declare-gltf-and-pbr-material-assets
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/prop-and-course-asset-xml]
techniques: [xml-asset, glb-model]
hooks: [xml GltfFile, xml PbrMaterial]
lifecycle: [startup:asset-load, startup:all-mods-loaded]
game_types:
  - KSA.AssetBundle
  - KSA.Gltf2Reference
  - KSA.PbrMaterialReference
  - KSA.TextureReference
  - KSA.TexturePowerReference
  - KSA.FileReference
  - KSA.ModLibrary
  - KSA.TextureCategory
tags: [gltf, pbr, material, props, atlas, texture-channels, units, ghost]
---

Mods that only need a mesh and a look (held props, course pieces, ghost tints) can skip `Part`, `SubPart` and `StaticObject` entirely. They
declare a bare `<GltfFile>` and a bare `<PbrMaterial>` and let their own code pair them by id when something has to be drawn. KSAGolf draws a
golf ball, a putter, twelve minigolf course pieces and see-through ghost variants this way. This article shows the XML, the three-channel rule, the
unit conventions, how twelve pieces share one atlas and one material, and the runtime lookup. The texture-authoring side is approaches 2 to 4
([Bake diffuse, normal, ORM and emissive maps from Blender and dilate the atlas](declare-gltf-and-pbr-material-assets-2.md)).

Demonstrated by [Prop and course asset XML](../../../../mods/ksagolf/features/prop-and-course-asset-xml.md). The element table is in [Know which asset XML elements the game accepts and what they load](../../asset-xml/asset-xml-element-reference/overview.md).

## Background: how the game does it

`Gltf2Reference` is the class behind `<GltfFile>`. Its `Source` child is a `FileReference` (`Path` attribute); with a source the element
registers itself in the glTF table under its `Id`, without one it is a reference to an existing id. The other children (`Skeleton`, `Image`,
`Material`, `Usage`, animation indices) are for characters:

```csharp
// current/decomp/KSA/Gltf2Reference.cs L7-L34
public class Gltf2Reference : SerializedId, IKeyed
{
	[XmlElement]
	public FileReference? Source;

	[XmlElement]
	public Gltf2Reference? Skeleton;

	[XmlElement]
	public IntegerReference? AnimationStartIndex;

	[XmlElement]
	public IntegerReference? AnimationEndIndex;

	[XmlElement]
	public IntegerReference? AnimIndex;

	[XmlElement("Image")]
	public List<GltfSubReference>? ImageReferences;

	[XmlElement("Material")]
	public List<GltfSubReference>? MaterialReferences;

	[XmlElement]
	public Gltf2Usage? Usage;

	[XmlElement]
	public bool AllowEmbedded;
// ...
```

`PbrMaterialReference` has `Diffuse`, `Normal` (a `TexturePowerReference` with a `Power` attribute), `AoRoughMetal`, `Emissive`, `ThinFilm` and `Alpha`
children. An element that declares none of the first three is a reference, not a definition:

```csharp
// current/decomp/KSA/PbrMaterialReference.cs L7-L28
public class PbrMaterialReference : SerializedId, IKeyed, IComboable
{
	[XmlAttribute]
	public string? DisplayName;

	[XmlElement("Diffuse")]
	public TextureReference? DiffuseReference;

	[XmlElement("Normal")]
	public TexturePowerReference? NormalReference;

	[XmlElement("AoRoughMetal")]
	public TextureReference? PBRMap;

	[XmlElement("Emissive")]
	public TextureReference? EmissiveMap;

	[XmlElement("ThinFilm")]
	public TextureReference? ThinFilmMap;

	[XmlElement("Alpha")]
	public TextureReference? AlphaMap;
// ...
```

Each texture has `Path` and `Category`; the category picks a graphics-setting size cap (`Vessel` and `Terrain` read the player's caps, `Default` and
`TerrainHeight` do not; [current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs#L46-L47), [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L3369-L3390)).

**Which channels are required depends on who draws it.** The part, glass-part and static-object render paths read
`Template.Material.DiffuseReference.BindlessHandle`, `NormalReference.BindlessHandle` and `PBRMap.BindlessHandle` with no null check (only
`Emissive`, `ThinFilm` and `Alpha` are optional, falling back to -1):

```csharp
// current/decomp/KSA/PartModel.cs L516-L523
				new PerDrawData
				{
					DiffuseTextureIndex = Template.Material.DiffuseReference.BindlessHandle,
					NormalTextureIndex = Template.Material.NormalReference.BindlessHandle,
					PbrTextureIndex = Template.Material.PBRMap.BindlessHandle,
					EmissiveTextureIndex = (Template.Material?.EmissiveMap?.BindlessHandle ?? (-1)),
					TfiTextureIndex = (Template.Material?.ThinFilmMap?.BindlessHandle ?? (-1))
				}
```

The material system that serves glTF renderables created by code resolves a missing channel to a default texture instead:

```csharp
// current/decomp/KSA/GpuMaterialSystem.cs L67-L82
	protected override GpuObjectAssetRef CreateAsset(AssetName id)
	{
		if (!ModLibrary.TryGet<PbrMaterialReference>(id.ToString(), out PbrMaterialReference value) || value == null)
		{
			throw new ArgumentException($"ID {id} is not a valid PbrMaterialReference");
		}
		int handle = SendToBuffer(new MaterialData
		{
			AlbedoTexture = (value.DiffuseReference?.Get().BindlessHandle ?? TextureSystem.DefaultWhiteTexture.BindlessHandle),
			Sampler = TextureSystem.SamplerRepeatHandle,
			AlbedoColor = float4.One,
			NormalTexture = (value.NormalReference?.Get().BindlessHandle ?? TextureSystem.DefaultWhiteTexture.BindlessHandle),
			RoughMetallicAOTexture = (value.PBRMap?.Get().BindlessHandle ?? TextureSystem.DefaultWhiteTexture.BindlessHandle),
			RoughnessMetalScale = float4.One,
			EmissiveTexture = (value.EmissiveMap?.Get().BindlessHandle ?? TextureSystem.DefaultBlackTexture.BindlessHandle)
		});
	// ...
```

Declare all three anyway: it is what Core does, it is what the part path needs, and it costs one 8 by 8 flat PNG per channel.

## Lifecycle and timing

`startup:asset-load`: `Mod.LoadAssetBundles` registers the ids during `ModLibrary.LoadAll`, and the files load in the parallel `ILoader` pass. No
mod code runs. The ids are resolved lazily later, on the main thread, the first time something needs a mesh (for KSAGolf: the first putting
session or course builder frame). `ModLibrary.Get<T>` throws a `NullReferenceException` for an unknown id; `TryGet<T>` does not. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Understand when and in which order KSA loads mod XML](../../asset-xml/mod-xml-load-order/overview.md).

## Steps

1. **Declare each mesh as a `GltfFile` with a prefixed id.** The comment in the course file records the conventions the code relies on:

   ```xml
   <!-- src/KSAGolf/KSAGolfCourse.xml L1-L12 -->
   <?xml version="1.0" encoding="utf-8"?>
   <!-- Minigolf course pieces, drawn by the mod rather than built as parts. Each is a <GltfFile> in metres,
        Z up, its footprint's south-west corner at the origin and the felt at z = 0; all share one atlas.
        A chiral piece has a mirrored twin, since a mirrored draw turns its triangles inside out, and the
        plinth is a unit block stretched under any piece laid above the ground. -->
   <Assets>
       <GltfFile Id="KSAGolf_Piece_Tee_Glb">
           <Source Path="Meshes/KSAGolf_Piece_Tee.glb"/>
       </GltfFile>
       <GltfFile Id="KSAGolf_Piece_Cup_Glb">
           <Source Path="Meshes/KSAGolf_Piece_Cup.glb"/>
       </GltfFile>
   <!-- ... -->
   ```

2. **Declare the material with all three channels.** One opaque material for the mesh (`Category="Default"` here because it is not a vessel part):

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

3. **Share one atlas and material across many meshes.** Twelve course pieces use `KSAGolf_Course_Material`; ghost tints reuse every piece glTF with a
   single-colour diffuse (one colour, so any UVs will do) and share the course normal map:

   ```xml
   <!-- src/KSAGolf/KSAGolfCourse.xml L43-L58 -->
       <PbrMaterial Id="KSAGolf_Course_Material">
           <Diffuse Path="Textures/KSAGolf_Course_Diffuse.png" Category="Default"/>
           <Normal Path="Textures/KSAGolf_Course_Normal.png" Category="Default"/>
           <AoRoughMetal Path="Textures/KSAGolf_Course_PBR.png" Category="Default"/>
       </PbrMaterial>
       <!-- The ghost of a piece about to be laid, drawn see-through: one colour, so any UVs will do. -->
       <PbrMaterial Id="KSAGolf_Ghost_Fits_Material">
           <Diffuse Path="Textures/KSAGolf_Ghost_Fits.png" Category="Default"/>
           <Normal Path="Textures/KSAGolf_Course_Normal.png" Category="Default"/>
           <AoRoughMetal Path="Textures/KSAGolf_Ghost_PBR.png" Category="Default"/>
       </PbrMaterial>
       <PbrMaterial Id="KSAGolf_Ghost_Open_Material">
           <Diffuse Path="Textures/KSAGolf_Ghost_Open.png" Category="Default"/>
           <Normal Path="Textures/KSAGolf_Course_Normal.png" Category="Default"/>
           <AoRoughMetal Path="Textures/KSAGolf_Ghost_PBR.png" Category="Default"/>
       </PbrMaterial>
   ```

4. **Follow the unit conventions you declare.** Held props are authored in centimetres because character attachments are drawn through a 0.01 scale;
   course pieces are in metres, Z up, footprint corner at the origin, felt at z = 0. A chiral piece gets a separate mirrored glTF (a mirrored draw turns
   triangles inside out), and raised pieces use a unit plinth glTF scaled under them.

5. **Pair glTF and material by id in code.** KSAGolf builds a renderable from a declared `GltfFile` with a declared `PbrMaterial` in every slot of the
   glTF, reading both through the game's renderer systems by id. A `seeThrough` flag picks the glass technique for ghosts, so one glTF can be drawn both ways:

   ```csharp
   // src/KSAGolf/Ksa/AttachmentMesh.cs L26-L62
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

               // An exported .glb names a material slot, which is what gives this array a length.
               for (int i = 0; i < gltf.Materials.Length; i++) gltf.Materials[i] = material;

               object technique = seeThrough ? Program.Instance.CharacterRenderResources.GlassRenderer : system.MeshRendererStaticPbr;
               mesh = (StaticMeshRenderable?)Activator.CreateInstance(
                   typeof(StaticMeshRenderable), technique, gltfName, system.MeshRendererStaticPrePass, !seeThrough);
               if (mesh is null) why = "the renderable could not be built";
               else if (seeThrough) mesh.CastShadows = false;
               return mesh is not null;
       // ...
   ```

   The GLB's own material is a placeholder; the loop overwrites every slot of `gltf.Materials` with the declared one. Keep the id pairs in one catalogue
   (KSAGolf's `PieceCatalogue` maps each piece to its glTF id).

6. **Export the GLB so it has a material slot.** A primitive with no material makes the build throw "Index was outside the bounds of the array"
   ([Export or generate a GLB that KSA loads correctly](../export-glb-from-blender-for-ksa/overview.md)).

## Complete example

- [src/KSAGolf/KSAGolfProps.xml](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/KSAGolfProps.xml), [src/KSAGolf/KSAGolfCourse.xml](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/KSAGolfCourse.xml)
- [src/KSAGolf/Ksa/AttachmentMesh.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/AttachmentMesh.cs) (pairing), [tools/validate-parts.py](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/validate-parts.py) (declared files, case, duplicate ids)

## Variations and gotchas

- **Do not reuse this for parts.** A `Part` or `StaticObject` uses the part render path, where all three channels are mandatory and a shared texture
  path hands later materials handle 0 (see approach 4).
- **A mistyped id or path is silent in game.** It shows as an untextured or invisible mesh; check offline ([Validate asset XML ids and paths before launching the game (offline)](../../validation/validate-asset-xml-references/overview.md)).
- **Props rendered through character attachments are scaled 0.01**, so a prop exported in metres appears a hundredth of its size.
- **Mip bleed.** A twelve-piece atlas needs its background filled between UV islands, or lower mips average neighbouring islands
  (approach [Bake diffuse, normal, ORM and emissive maps from Blender and dilate the atlas](declare-gltf-and-pbr-material-assets-2.md)).
- **Draw it yourself.** Putting the mesh on screen is another topic ([Draw your own meshes through the engine's mesh renderers](../../../rendering/meshes/draw-mod-meshes-with-the-engine-renderers/overview.md),
  [Attach a prop to a kitten's hand or another bone at runtime, building the prop mesh from mod assets](../../../kittens/appearance/attach-a-prop-to-a-kitten-bone/overview.md)).

## See also

- [Bake diffuse, normal, ORM and emissive maps from Blender and dilate the atlas](declare-gltf-and-pbr-material-assets-2.md), [Generate a flat-swatch palette atlas with a JSON swatch map](declare-gltf-and-pbr-material-assets-3.md), [Emit flat-colour textures from the same script that generates the geometry](declare-gltf-and-pbr-material-assets-4.md),
  [Know which asset XML elements the game accepts and what they load](../../asset-xml/asset-xml-element-reference/overview.md), [Resolve asset ids and file paths correctly across mods](../../asset-xml/resolve-asset-ids-and-mod-relative-paths/overview.md), [Export or generate a GLB that KSA loads correctly](../export-glb-from-blender-for-ksa/overview.md)
- [Prop and course asset XML](../../../../mods/ksagolf/features/prop-and-course-asset-xml.md)
