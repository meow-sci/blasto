---
type: Mod Feature
title: "Car part assets: GLB atlases, PBR materials and subparts"
description: Declare a new part's look in an Assets XML bundle with a MeshAtlas GLB, a PbrMaterial with diffuse, normal, ORM and emissive PNGs, one SubPart template per moving body, a glass subpart, and a Part that instances and places them
mod: ksacars
category: assets
techniques: [xml-asset, glb-model]
hooks: []
lifecycle: [startup:asset-load]
game_types:
  - KSA.AssetBundle
  - KSA.MeshAtlasFileReference
  - KSA.PbrMaterialReference
  - KSA.TextureReference
  - KSA.PartTemplate
  - KSA.SubPartTemplate
  - KSA.PartInstance
  - KSA.TransformReference
  - KSA.PartModelModule
  - KSA.PartModelGlassModule
  - KSA.MeshViewModule
  - KSA.XmlHelper
  - KSA.Mod
tags: [assets, xml, glb, gltf, pbr, textures, subparts, glass, emissive, attach-node]
---

KSACars ships its own art: one glTF binary per car (plus an iris-hatch atlas and a scoop atlas),
PNG texture sets, and an Assets XML that turns them into parts. The file mirrors how Core's own
`*Assets.xml` files are written: `<MeshAtlas>` registers every mesh in a GLB, `<PbrMaterial>` binds
textures, `<SubPart>` templates pair a mesh with a material, and a `<Part>` instances the subparts
at positions in part space.[^ksacars-assets] Because the cars move their wheels, arms, coil-overs and
steering wheel at runtime, every moving body is its own subpart, exported recentred on its pivot.

## How it works

The XML root is `<Assets>`, deserialised into `KSA.AssetBundle`, whose `[XmlElement]` list maps
each element name to a template type (`MeshAtlas` -> `MeshAtlasFileReference`, `PbrMaterial` ->
`PbrMaterialReference`, `SubPart` -> `SubPartTemplate`, `Part` -> `PartTemplate`, and many more).[^ksa-assetbundle]
Element by element, as KSACars uses them:

| XML | Loader type and field | Notes |
|---|---|---|
| `<MeshAtlas Path="Meshes/KSACars_Buggy.glb" />` | `MeshAtlasFileReference` (`FileReference.LocalPath`, optional `HostOnly`) | Registers one `MeshReference` per glTF **mesh**, Id = the glTF mesh name. First registration of an Id wins; a later duplicate silently reuses the existing mesh[^ksa-meshatlas] |
| `<PbrMaterial Id="...">` | `PbrMaterialReference` | Children `Diffuse`, `Normal`, `AoRoughMetal`, `Emissive` (also `ThinFilm`, `Alpha`, unused here)[^ksa-pbrmaterial] |
| `<Diffuse Path=".png" Category="Vessel" />` | `TextureReference` with `Category` (`TextureCategory.Vessel`) | Category picks the texture-size cap from graphics settings; `Normal` is a `TexturePowerReference`[^ksa-texturereference] |
| `<SubPart Id="..._SubPart">` | `SubPartTemplate : PartTemplate` | A reusable body; module templates inside it go to `PartTemplate.Components` |
| `<PartModel Id><Mesh Id/><Material Id/></PartModel>` | `PartModelModule.Template` | `Mesh` and `Material` by Id; also `RayTracing`, `ShadowCaster`, `Internal` (drawn only in IVA)[^ksa-partmodelmodule] |
| `<MeshView><Mesh Id="..._VM"/></MeshView>` | `MeshViewModule.Template` | The editor's preview mesh[^ksa-meshviewmodule] |
| `<PartModelGlass>` | `PartModelGlassModule.Template` | Windscreen drawn in the glass pass[^ksa-partmodelglassmodule] |
| `<EditorTag Value="Hidden" />` on a subpart | `PartTemplate.EditorTag` | Door subparts with no model at all |
| `<Part Id="..." CrashTolerance="1e12">` | `PartTemplate` (`DisplayName`, `CrashTolerance` attributes) | See [crash veto](./crash-veto.md) for why the tolerance is huge[^ksa-parttemplate] |
| `<SubPart Id="KSACars_Buggy_WheelFL" InstanceOf="KSACars_BuggyWheelFront_SubPart">` | `PartInstance` (`InstanceOf`, `Transform`) | The instance Id is what code finds at runtime[^ksa-partinstance] |
| `<Transform><Position/><Rotation/><Scale/></Transform>` | `TransformReference` | Rotation is XYZ **radians** (`3.14159` = 180 degrees)[^ksa-transformreference] |
| `<Connector Id="_bgConnectorAft"><Transform>...` | `Part.Connector.TemplateBase` (`Id`, `Transform`, `Flags`, `Capabilities`) | One node at the ground plane, facing down, scaled 2x |

Module element names (`PartModel`, `MeshView`, `Collider`, `IVASeat`, ...) are not hard-coded on
`PartTemplate`: `XmlHelper`'s static constructor reflects every `ModuleBase.TemplateDataBase`
subclass in `KSA.dll` and registers it under `PartTemplate.Components` by its `[XmlType]` name.
That is why a mod can use every stock module in XML but cannot add a new module element without
patching the serializer.[^ksa-xmlhelper]

**Paths** (`Path=` on atlases, textures and sounds) are resolved by `Mod.GetPath`, which is
`Path.Combine(Mod.DirectoryPath, localPath)`: relative to the **mod folder**, not to the XML file.[^ksa-filereference][^ksa-mod]
KSACars keeps the XML at the mod root "because it is not documented"; the decomp settles it, and
the layout is correct either way.

**The art contract** (from the repo's Blender skill and notes): part space is +X up, +Y forward,
+Z the car's left; export with Blender's Y-up conversion off; bake transforms into the vertices,
because the atlas loader takes mesh data and ignores the node graph; name every mesh with the mod
prefix, because mesh Ids are global; and keep `_VM` preview meshes alongside.[^ksacars-blender-skill][^ksacars-notes]
`AoRoughMetal` is glTF ORM (R occlusion, G roughness, B metalness). PNG works in every slot.

**Emissive and glass.** The `Emissive` texture is what KSA lights when the part's light switch is
on (see [headlights](./headlights-as-spotlights.md) and [coloured lenses](./coloured-emissive-lenses.md)).
`<PartModelGlass>` subparts are drawn by `MeshGlassIndirect.frag`, which uses a fixed
`glassOpacity = 0.75` and a Fresnel term; the material's diffuse tints it.[^ksa-glass-frag]

## Lifecycle and timing

Content only (`startup:asset-load`): `Mod.LoadAssetBundles` reads each path in `mod.toml`'s
`assets` with `XmlLoader.Load<AssetBundle>` during `ModLibrary.LoadAll`; files referenced by
atlases and materials are loaded through `FileReference`. `PartGameData` with the same Id is merged
afterwards by `ModLibrary.AttachGameData` (see [car part game data](./car-part-gamedata.md)).[^ksa-mod]
At runtime the mod only reads what this XML declared: subpart instances by Id for
[posing](./pose-subparts-at-runtime.md).

## Key code

| Path | Role |
|---|---|
| [src/KSACars/KSACarsAssets.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsAssets.xml#L17-L39) | Buggy atlas, material with emissive, body subpart |
| [src/KSACars/KSACarsAssets.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsAssets.xml#L140-L154) | Glass subpart and mesh-less door subparts |
| [src/KSACars/KSACarsAssets.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsAssets.xml#L156-L263) | The buggy `<Part>`: instances, transforms, attach node |
| [src/KSACars/KSACarsAssets.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsAssets.xml#L265-L858) | Eldorado, iris hatch and scoop atlases; 60 iris blade instances |
| [.claude/skills/ksa-blender/SKILL.md](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/.claude/skills/ksa-blender/SKILL.md) | Export contract for Blender over MCP |

## Snippets

```xml
<!-- src/KSACars/KSACarsAssets.xml L17-L48 -->
  <MeshAtlas Path="Meshes/KSACars_Buggy.glb" />

  <PbrMaterial Id="KSACars_Buggy_Material">
    <Diffuse Path="Textures/KSACars_Buggy_Diffuse.png" Category="Vessel" />
    <Normal Path="Textures/KSACars_Buggy_Normal.png" Category="Vessel" />
    <AoRoughMetal Path="Textures/KSACars_Buggy_PBR.png" Category="Vessel" />
    <Emissive Path="Textures/KSACars_Buggy_Emissive.png" Category="Vessel" />
  </PbrMaterial>

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

```xml
<!-- src/KSACars/KSACarsAssets.xml L162-L180 -->
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
    <SubPart Id="KSACars_Buggy_WheelFR" InstanceOf="KSACars_BuggyWheelFront_SubPart">
      <Transform>
        <Position X="0.34000" Y="1.15000" Z="-0.84000" />
      </Transform>
    </SubPart>
```

```xml
<!-- src/KSACars/KSACarsAssets.xml L140-L146 and L254-L261 -->
  <!-- The windscreen, drawn in KSA's glass pass: blended over what is behind it, never cut out. -->
  <SubPart Id="KSACars_BuggyGlass_SubPart">
    <PartModelGlass Id="KSACars_Subpart_BuggyGlass_Model">
      <Mesh Id="KSACars_Subpart_BuggyGlass" />
      <Material Id="KSACars_Buggy_Material" />
    </PartModelGlass>
  </SubPart>
  <!-- ... -->
    <!-- Underside attach node, at the ground plane and facing down. -->
    <Connector Id="_bgConnectorAft">
      <Transform>
        <Position X="0.00000" />
        <Rotation Z="3.14159" />
        <Scale X="2.00000" Y="2.00000" Z="2.00000" />
      </Transform>
    </Connector>
```

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

## Game APIs touched

| API | Purpose |
|---|---|
| `AssetBundle` ([current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L9-L76)) | `<Assets>` root and the element-to-type table |
| `MeshAtlasFileReference.DoLoad` ([current/decomp/KSA/MeshAtlasFileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshAtlasFileReference.cs#L26-L50)) | GLB meshes registered by glTF mesh name; first wins |
| `FileReference.ModPath` ([current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L12-L26)) / `Mod.GetPath` ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L389-L392)) | Relative paths resolve against the mod folder |
| `PbrMaterialReference` ([current/decomp/KSA/PbrMaterialReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PbrMaterialReference.cs#L7-L28)), `TextureReference.Category` ([current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs#L46-L47)) | Material slots and texture category |
| `PartTemplate` ([current/decomp/KSA/PartTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTemplate.cs#L12-L125)) | `DisplayName`, `CrashTolerance`, `SubPart`, `EditorTag`, `Connector`, `Components` |
| `PartInstance` ([current/decomp/KSA/PartInstance.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartInstance.cs#L7-L36)), `TransformReference` ([current/decomp/KSA/TransformReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransformReference.cs)) | `InstanceOf`, `Transform` (`Position`, `Rotation` in XYZ radians, `Scale`) |
| `PartModelModule.Template` ([current/decomp/KSA/PartModelModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModelModule.cs#L17-L40)), `PartModelGlassModule.Template` ([current/decomp/KSA/PartModelGlassModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModelGlassModule.cs#L10-L17)), `MeshViewModule.Template` ([current/decomp/KSA/MeshViewModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshViewModule.cs#L8-L12)) | Model, glass and preview modules |
| `XmlHelper` static constructor ([current/decomp/KSA/XmlHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlHelper.cs#L17-L45)) | Registers every stock module template under `Components` |

## Pitfalls and version notes

- **Mesh Ids are global and first-registered wins.** A name clash with Core or another mod
  silently gives you the other mesh; prefix every mesh with the mod name.[^ksa-meshatlas]
- **The Id is the glTF *mesh* name**, not the Blender object name, and node transforms are not
  read: bake them.[^ksacars-notes]
- **A shipped part's subpart list is append-only.** `PartTree.Deserialize` pairs saved subparts with
  the current definition by position; removing a subpart throws on load and, because the load runs
  inside the UI pass with nothing catching it, terminates the game. Adding is safe. KSACars' CLAUDE.md
  repeats this as a rule for new cars.[^ksacars-notes][^ksacars-claude]
- **Subpart instance Ids vs template Ids**: code that looks for a subpart at runtime must use the Id
  inside `<Part>`.
- **Hiding a subpart** has no XML switch; the Eldorado declares its scoops at scale 0.001 so the
  editor thumbnail shows none, and the mod scales the chosen one up at runtime.
- Glass is back-face culled and single-sided in the stock shader per the mod's notes; model panes
  facing out.[^ksacars-notes]
- `KSACarsAssets.xml` has no `<Thumbnail>`; the editor renders one from the model.

## See also

- [One folder as both a content mod and a code mod](./mod-folder-content-and-code-mod.md)
- [Car part game data](./car-part-gamedata.md)
- [Seats and EVA doors](./seats-and-eva-doors.md)
- [Pose subparts at runtime](./pose-subparts-at-runtime.md)
- [Art and audio pipeline tooling](./art-and-audio-pipeline-tooling.md)
- [Read the AssetBundle element table against three real asset files](../../../howto/assets/asset-xml/asset-xml-element-reference/asset-xml-element-reference.md) - how-to article demonstrating this feature
- [Lay out mod-folder-relative paths and prefixed global asset ids](../../../howto/assets/asset-xml/resolve-asset-ids-and-mod-relative-paths/resolve-asset-ids-and-mod-relative-paths-1.md) - how-to article demonstrating this feature
- [Build a part from one atlas per vehicle with subparts for moving bodies, glass and hidden doors](../../../howto/assets/models-and-textures/add-a-part-model-with-glb-and-pbr-textures/add-a-part-model-with-glb-and-pbr-textures-1.md) - how-to article demonstrating this feature
- [Bake diffuse, normal, ORM and emissive maps from Blender and dilate the atlas](../../../howto/assets/models-and-textures/declare-gltf-and-pbr-material-assets/declare-gltf-and-pbr-material-assets-2.md) - how-to article demonstrating this feature
- [Export a part-space GLB with pivot-centred subparts and preview it headless](../../../howto/assets/models-and-textures/export-glb-from-blender-for-ksa/export-glb-from-blender-for-ksa-1.md) - how-to article demonstrating this feature
- [Define a driveable part with colliders, a mass shape and its own editor category](../../../howto/vehicles-and-parts/parts/define-a-part-with-game-data/define-a-part-with-game-data-1.md) - how-to article demonstrating this feature
- [Pose wheels, arms and blades from StarMapAfterGui with a frame latch](../../../howto/vehicles-and-parts/parts/define-and-animate-subparts/define-and-animate-subparts-1.md) - how-to article demonstrating this feature

[^ksacars-assets]: src/KSACars/KSACarsAssets.xml
[^ksacars-notes]: docs/KSA-MODDING-NOTES.md
[^ksacars-claude]: CLAUDE.md
[^ksacars-blender-skill]: .claude/skills/ksa-blender/SKILL.md
[^ksa-assetbundle]: KSA.AssetBundle (decompiled)
[^ksa-meshatlas]: KSA.MeshAtlasFileReference (decompiled)
[^ksa-filereference]: KSA.FileReference (decompiled)
[^ksa-mod]: KSA.Mod (decompiled)
[^ksa-pbrmaterial]: KSA.PbrMaterialReference (decompiled)
[^ksa-texturereference]: KSA.TextureReference (decompiled)
[^ksa-parttemplate]: KSA.PartTemplate (decompiled)
[^ksa-partinstance]: KSA.PartInstance (decompiled)
[^ksa-transformreference]: KSA.TransformReference (decompiled)
[^ksa-partmodelmodule]: KSA.PartModelModule (decompiled)
[^ksa-partmodelglassmodule]: KSA.PartModelGlassModule (decompiled)
[^ksa-meshviewmodule]: KSA.MeshViewModule (decompiled)
[^ksa-xmlhelper]: KSA.XmlHelper (decompiled)
[^ksa-glass-frag]: Core MeshGlassIndirect.frag
