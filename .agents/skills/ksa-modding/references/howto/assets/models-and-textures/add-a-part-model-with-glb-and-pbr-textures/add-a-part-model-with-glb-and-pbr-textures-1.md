---
type: How-To
title: Build a part from one atlas per vehicle with subparts for moving bodies, glass and hidden doors
description: Declare a MeshAtlas and a PbrMaterial, one SubPart per rigid or moving body with PartModel and a MeshView twin, a PartModelGlass window, mesh-less hidden subparts and a Part that instances them with Transform positions
category: assets
group: models-and-textures
topic: add-a-part-model-with-glb-and-pbr-textures
approach: 1
difficulty: intermediate
prerequisites: [starmap, blender]
demonstrated_by: [mods/ksacars/features/car-part-assets-glb-pbr]
techniques: [glb-model, xml-asset]
hooks: [xml Part, xml SubPart]
lifecycle: [startup:asset-load]
game_types:
  - KSA.PartTemplate
  - KSA.SubPartTemplate
  - KSA.PartInstance
  - KSA.PartModelModule
  - KSA.PartModelGlassModule
  - KSA.MeshViewModule
  - KSA.MeshAtlasFileReference
  - KSA.PbrMaterialReference
  - KSA.TransformReference
tags: [part, subpart, glb, atlas, partmodel, meshview, glass, transform, connector]
---

To make a placeable part with your own art you need four kinds of element, in the same shape Core's `*Assets.xml` files use: a `MeshAtlas` (the GLB), a `PbrMaterial`
(its textures), `SubPart` templates that pair a mesh with a material, and a `Part` that instances the subparts at positions in part space. KSACars builds two drivable
cars this way. This article takes the buggy: one atlas per vehicle, one subpart per moving body (wheels, arms, steering wheel), a glass subpart for the windscreen and
mesh-less hidden subparts for the doors. Simulation data (mass, colliders, seats) goes in a separate `PartGameData`, the sibling topic [Define a part with its own game data and GLB model](../../../vehicles-and-parts/parts/define-a-part-with-game-data/overview.md).

Demonstrated by [Car part assets: GLB atlases, PBR materials and subparts](../../../../mods/ksacars/features/car-part-assets-glb-pbr.md). Element reference: [Know which asset XML elements the game accepts and what they load](../../asset-xml/asset-xml-element-reference/overview.md); textures: [Declare glTF meshes and PBR materials in asset XML and author their texture sets](../declare-gltf-and-pbr-material-assets/overview.md).

## Background: how the game does it

`MeshAtlasFileReference` registers one `MeshReference` per glTF mesh, with `Id` equal to the mesh name; the node graph and node transforms are never read
([current/decomp/KSA/MeshAtlasFileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshAtlasFileReference.cs#L26-L50)). A `SubPart` is a `PartTemplate` whose `Components` (module templates registered by reflection,
[Know which asset XML elements the game accepts and what they load](../../asset-xml/asset-xml-element-reference/overview.md)) include `PartModel` for the visible mesh, `PartModelGlass` for glass and `MeshView` for the editor preview:

```csharp
// current/decomp/KSA/PartModelModule.cs L17-L43
	[XmlType(TypeName = "PartModel")]
	public class Template : TemplateDataBase
	{
		[XmlIgnore]
		public static List<Template> RayTracers = new List<Template>();

		[XmlElement("Mesh")]
		public MeshReference? Mesh;

		[XmlElement("Material")]
		public PbrMaterialReference? Material;

		[XmlElement("RayTracing")]
		[DefaultValue(RaytracingMode.Disabled)]
		public RaytracingMode RayTracing = RaytracingMode.Disabled;

		[XmlElement("ShadowCaster")]
		[DefaultValue(true)]
		public bool ShadowCaster = true;

		[XmlElement("Internal")]
		[DefaultValue(false)]
		public bool Internal = false;

		[XmlElement("Terrain")]
		[DefaultValue(false)]
		public bool Terrain = false;
	// ...
```

`PartModelGlass.Template` has `Mesh`, `Material`, `RayTracing` (default false) and `ShadowCaster` (default false) ([current/decomp/KSA/PartModelGlassModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModelGlassModule.cs#L9-L26)); `MeshView.Template` has
a single `Mesh` ([current/decomp/KSA/MeshViewModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshViewModule.cs#L8-L18)). A `Part` lists `SubPart` children, each a `PartInstance` with `InstanceOf` (the subpart template), an optional `Id` that code
finds at runtime, and a `Transform` of `Position`, `Rotation` (XYZ radians) and `Scale`:

```csharp
// current/decomp/KSA/TransformReference.cs L7-L15
{
	[XmlElement("Position")]
	public Vector3Reference? _positionRaw;

	[XmlElement("Rotation")]
	public Vector3Reference? _rotationRaw;

	[XmlElement("Scale")]
	public Vector3Reference? _scaleRaw;
// ...
```

Core itself is generated from GLBs by `KSA.GlbImport.PartAssetBundler`, which shows the contract in code: every atlas node whose name does not start with `_` and does not end with `_VM` becomes a
`SubPart` named after the node, a `<name>_VM` node becomes its `MeshView`, and a child node starting with `_glass` makes it a `PartModelGlass`:

```csharp
// current/decomp/KSA.GlbImport/PartAssetBundler.cs L44-L62
			// ...
			Node[] array = nodes;
			foreach (Node node in array)
			{
				string text = node.Name ?? "";
				if (!text.StartsWith('_'))
				{
					flag |= WarnIfUnappliedTransform(setPrefix, node, gltfLoader);
				}
				if (!text.StartsWith('_') && !text.EndsWith("_VM"))
				{
					assetBundle.Assets.Add(SubPart(setPrefix, gltfLoader, node, nodes, meshes, names, materialId, set.HeatMap, glbCollisionAtlas));
				}
			}
			if (flag)
			{
				string value = setPrefix + "[WARNING] Mesh atlas '" + meshAtlasGlb2.Name + "' has unapplied mesh transforms. These mean that the subpart origins will be offset from the visual mesh locations, which will make positioning game data like rocket nozzles, tanks, and masses very challenging. This should be fixed in the source art assets by applying transforms to vertices.";
				Console.WriteLine(value);
			}
		}
```

## Lifecycle and timing

`startup:asset-load`: `Mod.LoadAssetBundles` reads the XML during `ModLibrary.LoadAll`; the GLB and PNG files load in the parallel `ILoader` pass; `PartGameData` with the same Id is merged
by `ModLibrary.AttachGameData` later. At run time the mod only reads what this XML declared, by instance Id, to pose subparts. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Understand when and in which order KSA loads mod XML](../../asset-xml/mod-xml-load-order/overview.md).

## Steps

1. **Declare the atlas and the material.** The XML sits at the mod root, with `Meshes/` and `Textures/` beside it ([Resolve asset ids and file paths correctly across mods](../../asset-xml/resolve-asset-ids-and-mod-relative-paths/overview.md)):

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

2. **Make one `SubPart` per rigid body that moves independently.** A wheel, an arm, a coil-over and the steering wheel are each exported recentred on their own pivot, so the mod can
   rewrite their `Transform` or rotation each frame (see [Define articulated subparts and mounts, animate and hide them from code](../../../vehicles-and-parts/parts/define-and-animate-subparts/overview.md)). A `PartModel` takes the mesh and material by id; the `MeshView` twin is the editor's preview
   mesh (`_VM` suffix):

   ```xml
   <!-- src/KSACars/KSACarsAssets.xml L26-L48 -->
     <!--
       Each moving body is exported recentred on its own pivot: a wheel on its hub, an arm on its
       chassis bushing, a coil-over on its top mount, the steering wheel on its boss. One front and
       one rear wheel mesh serve both sides, since a wheel is symmetric about its own mid-plane.
     -->
     <SubPart Id="KSACars_BuggyBody_SubPart">
       <PartModel Id="KSACars_Subpart_BuggyBody_Model">
         <Mesh Id="KSACars_Subpart_BuggyBody" />
         <Material Id="KSACars_Buggy_Material" />
       </PartModel>
       <MeshView>
         <Mesh Id="KSACars_Subpart_BuggyBody_VM" />
       </MeshView>
     </SubPart>
     <SubPart Id="KSACars_BuggyWheelFront_SubPart">
       <PartModel Id="KSACars_Subpart_BuggyWheelFront_Model">
         <Mesh Id="KSACars_Subpart_BuggyWheelFront" />
         <Material Id="KSACars_Buggy_Material" />
       </PartModel>
       <MeshView>
         <Mesh Id="KSACars_Subpart_BuggyWheelFront_VM" />
       </MeshView>
     </SubPart>
   ```

   One front and one rear wheel mesh serve both sides, since a wheel is symmetric about its own mid-plane.

3. **Add a glass subpart for windows.** `PartModelGlass` is drawn in the glass pass: blended over what is behind it, never cut out. The fragment shader uses a fixed `glassOpacity` of 0.75
   mixed with a Fresnel term, so the material's diffuse tints the glass but its alpha does not control opacity:

   ```xml
   <!-- src/KSACars/KSACarsAssets.xml L140-L146 -->
     <!-- The windscreen, drawn in KSA's glass pass: blended over what is behind it, never cut out. -->
     <SubPart Id="KSACars_BuggyGlass_SubPart">
       <PartModelGlass Id="KSACars_Subpart_BuggyGlass_Model">
         <Mesh Id="KSACars_Subpart_BuggyGlass" />
         <Material Id="KSACars_Buggy_Material" />
       </PartModelGlass>
     </SubPart>
   ```

   ```glsl
   // current/Content/Core/Shaders/Mesh/MeshGlassIndirect.frag L76-L76
       float glassOpacity = 0.75;
   ```

4. **Declare mesh-less subparts for places, not things.** A door is a position a kitten climbs through; nothing is drawn. A `SubPart` with only `<EditorTag Value="Hidden" />` has no model at all and
   is hidden from the part picker:

   ```xml
   <!-- src/KSACars/KSACarsAssets.xml L148-L153 -->
     <!-- Where a kitten climbs in and out, one beside each seat. Nothing is drawn: a door is a place. -->
     <SubPart Id="KSACars_BuggyDoorL_SubPart">
       <EditorTag Value="Hidden" />
     </SubPart>
     <SubPart Id="KSACars_BuggyDoorR_SubPart">
       <EditorTag Value="Hidden" />
     <!-- ... -->
   ```

5. **Instance the subparts in a `Part`.** The instance `Id` is what code finds at runtime; `InstanceOf` names the template; `Transform/Position` places it in part space. Rotation is in **radians**
   (`3.14159` is 180 degrees). `CrashTolerance` is in pascals; a car is one part, so a huge value stops a clipped rock from destroying it:

   ```xml
   <!-- src/KSACars/KSACarsAssets.xml L162-L175 -->
     <!--
       CrashTolerance is the contact pressure, in pascals, past which KSA breaks a part. A car is one
       part, so its breaking is the car's destruction, and a wheel pad clipping a rock at speed reaches
       what KSA derives from its mass. Set far past anything a contact gives.
     -->
     <Part Id="KSACars_Prefab_Buggy" CrashTolerance="1e12">

       <SubPart Id="KSACars_Buggy_Body" InstanceOf="KSACars_BuggyBody_SubPart" />

       <SubPart Id="KSACars_Buggy_WheelFL" InstanceOf="KSACars_BuggyWheelFront_SubPart">
         <Transform>
           <Position X="0.34000" Y="1.15000" Z="0.84000" />
         </Transform>
       </SubPart>
     <!-- ... -->
   ```

6. **Add an attach node.** A `Connector` with a `Transform` is where the part snaps in the editor. A scaled stacking node sets the node size:

   ```xml
   <!-- src/KSACars/KSACarsAssets.xml L254-L261 -->
       <!-- Underside attach node, at the ground plane and facing down. -->
       <Connector Id="_bgConnectorAft">
         <Transform>
           <Position X="0.00000" />
           <Rotation Z="3.14159" />
           <Scale X="2.00000" Y="2.00000" Z="2.00000" />
         </Transform>
       </Connector>
   ```

7. **List the XML in `mod.toml`** `assets` and ship the GLB and PNG files beside it. Check ids and paths offline ([Validate asset XML ids and paths before launching the game (offline)](../../validation/validate-asset-xml-references/overview.md)).

## Complete example

- [src/KSACars/KSACarsAssets.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsAssets.xml) (whole file: buggy, Eldorado, iris hatch and scoop atlases, 60 iris blade instances)
- [src/KSACars/mod.toml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/mod.toml) (the asset list)
- The game's own generator: [current/decomp/KSA.GlbImport/PartAssetBundler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.GlbImport/PartAssetBundler.cs)

## Variations and gotchas

- **Part space.** KSACars uses +X up, +Y forward, +Z the car's left, written with Blender's "+Y Up" off; meshes carry their final part-space coordinates and node transforms are never read
  ([Export or generate a GLB that KSA loads correctly](../export-glb-from-blender-for-ksa/overview.md)).
- **Moving subparts own their `Transform`.** A mod that moves a subpart rewrites its transform every frame, so the pivot offset belongs in the `Part` XML and the mesh is recentred on the pivot; a mesh
  with a baked offset would be overwritten.
- **Removing a `SubPart` can crash saves** that already hold the part ([Change a shipped part without breaking saves](../../../vehicles-and-parts/parts/evolve-a-shipped-part-without-breaking-saves/overview.md)).
- **A missing mesh or material id throws** when the module is resolved (`PartModelModule.Template.Get` calls `Mesh?.Get()` and `Material?.Get()`).
- **All three texture channels are mandatory** for `PartModel` materials ([Declare glTF meshes and PBR materials in asset XML and author their texture sets](../declare-gltf-and-pbr-material-assets/overview.md)).
- **`RayTracing` and `ShadowCaster`** default to disabled and true for `PartModel` but false for glass.

## See also

- [Share one palette atlas across parts, add _VM preview twins and keep subpart ids apart](add-a-part-model-with-glb-and-pbr-textures-2.md), [Define a part with its own game data and GLB model](../../../vehicles-and-parts/parts/define-a-part-with-game-data/overview.md), [Define articulated subparts and mounts, animate and hide them from code](../../../vehicles-and-parts/parts/define-and-animate-subparts/overview.md), [Export or generate a GLB that KSA loads correctly](../export-glb-from-blender-for-ksa/overview.md),
  [Declare glTF meshes and PBR materials in asset XML and author their texture sets](../declare-gltf-and-pbr-material-assets/overview.md), [Validate asset XML ids and paths before launching the game (offline)](../../validation/validate-asset-xml-references/overview.md)
- [Car part assets: GLB atlases, PBR materials and subparts](../../../../mods/ksacars/features/car-part-assets-glb-pbr.md)
