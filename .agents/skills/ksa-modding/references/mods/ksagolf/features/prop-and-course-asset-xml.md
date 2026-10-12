---
type: Mod Feature
title: Prop and course asset XML
description: Declaring a mod's own meshes and PBR materials as standalone GltfFile and PbrMaterial assets, drawn by code instead of being built as parts, static objects or clutter
mod: ksagolf
category: assets
techniques: [xml-asset, glb-model]
hooks: []
lifecycle: [startup:asset-load]
game_types:
  - KSA.AssetBundle
  - KSA.Gltf2Reference
  - KSA.PbrMaterialReference
  - KSA.TextureReference
  - KSA.FileReference
  - KSA.ModLibrary
tags: [xml, gltf, glb, pbr-material, textures, asset-bundle, course-pieces, props]
---

KSAGolf has art (a ball, a putter, twelve minigolf course pieces and see-through "ghost" variants
for the course builder) but no parts, no static objects and no clutter. Every mesh is declared as a
bare `<GltfFile>` and every look as a bare `<PbrMaterial>`, and the C# side pairs them at runtime
into a renderable it draws itself (on a kitten's hand, or into each viewport). The asset XML is
therefore only a registry of ids and file paths.

This is the lightest way to get a mod's own mesh into KSA's renderer when nothing about it should be
a vehicle part: no `<Part>`, `<SubPart>`, `<MeshAtlas>`, `<StaticObject>` or `<ClutterObject>` is
involved.

## How it works

Three files, all at the mod root and all listed in `mod.toml`'s `assets`:

| File | Declares | Consumed by |
|---|---|---|
| `KSAGolfProps.xml` | `KSAGolf_Ball_Glb` + `KSAGolf_Ball_Material`, `KSAGolf_Putter_Glb` + `KSAGolf_Putter_Material` | `Golf.cs` builds the ball and putter through `AttachmentMesh.TryBuild(glbId, materialId, ...)`[^golf-golf] |
| `KSAGolfCourse.xml` | Twelve `KSAGolf_Piece_*_Glb` (tee, cup, straight, long straight, corner, wide bend, S-bend and its mirrored twin, ramp, two rooms, plinth), `KSAGolf_Course_Material`, three `KSAGolf_Ghost_{Fits,Open,Blocked}_Material` | `PieceCatalogue` names each piece's glTF id; `CourseBuilder` draws pieces with the course material and the ghost with a tint material, see-through[^golf-piececatalogue][^golf-coursebuilder] |
| `KSAGolfSounds.xml` | Four `<Sound>` assets | See [World-positioned sounds](world-positioned-sounds.md) |

Engine side, each top-level child of `<Assets>` is deserialized through the `[XmlElement]` table on
`AssetBundle`: `GltfFile` maps to `Gltf2Reference`, `PbrMaterial` to `PbrMaterialReference`, and the
same table maps `Part` to `PartTemplate`, `StaticObject` to `StaticObjectTemplate`, `ClutterObject`
to `ClutterObjectTemplate` and `Sound` to `SoundReference` (a `SoundBehavior`).[^ksa-assetbundle]

- `Gltf2Reference` carries a `<Source>` child (a `FileReference`, `Path` attribute). With a source it
  registers itself in `ModLibrary`'s glTF table under its `Id`; without one it is a reference to an
  existing id.[^ksa-gltf2reference]
- `PbrMaterialReference` has `Diffuse`, `Normal` (a `TexturePowerReference`), `AoRoughMetal`,
  `Emissive`, `ThinFilm` and `Alpha` children; each texture has a `Path` and a `Category`
  (`TextureCategory.Default` here).[^ksa-pbrmaterialreference][^ksa-texturereference]
- Every `Path` resolves against the mod's folder (`FileReference.ModPath` -> `Mod.GetPath`), not the
  XML's folder.[^ksa-filereference]
- Core uses the same shape for the kitten itself: `<GltfFile Id="KittenGlb"><Source Path=".../KSA_Cat.gltf"/></GltfFile>`.[^ksa-character-assets]

Authoring conventions the mod states in its XML comments and docs:[^golf-props-xml][^golf-course-xml]

| Asset kind | Units and frame | Mesh shape |
|---|---|---|
| Held props (ball, putter) | Centimetres, because the kitten is drawn at `Scale = 0.01`[^golf-modding-notes] | One mesh, one primitive, one material slot |
| Course pieces | Metres, Z up, footprint's south-west corner at the origin, felt at z = 0 | One per piece, all sharing one texture atlas and one material |
| Chiral pieces | A separate mirrored glTF (`SBendMirror`) | A mirrored draw would turn triangles inside out |
| Raised pieces | A unit `Plinth` glTF scaled under the piece | |
| Ghost tints | Reuse every piece glTF with a single-colour diffuse | "one colour, so any UVs will do" |

The glb files each export one mesh, one primitive and one material slot; the material in the file is
a placeholder, because `AttachmentMesh` overwrites every slot of `GltfPbrAssetRef.Materials` with the
declared `<PbrMaterial>` before building the renderable.[^golf-attachmentmesh]

## Lifecycle and timing

`startup:asset-load` only: `ModLibrary.LoadAll` -> `Mod.LoadAssetBundles()` reads the XML and
registers the ids.[^ksa-modlibrary] No code runs here. The ids are resolved lazily later, on the
main thread, the first time the putting session or the course builder needs a mesh; see
[Kitten held props](kitten-held-props.md) and [Mod mesh draw hook](mod-mesh-draw-hook.md) for those
moments.

## Key code

| Path | Role |
|---|---|
| [src/KSAGolf/KSAGolfProps.xml](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/KSAGolfProps.xml) | Ball and putter glTF and material pairs |
| [src/KSAGolf/KSAGolfCourse.xml](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/KSAGolfCourse.xml) | Twelve course-piece glTFs, one shared course material, three ghost materials |
| [src/KSAGolf/Sim/PieceCatalogue.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/PieceCatalogue.cs#L106-L120) | Maps each piece id to its glTF id (and mirrored twin) |
| [src/KSAGolf/Ksa/AttachmentMesh.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/AttachmentMesh.cs#L26-L69) | Pairs a `<GltfFile>` id with a `<PbrMaterial>` id at runtime |
| [tools/validate-parts.py](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/validate-parts.py) | Case-exact texture paths, declared files, duplicate top-level ids |

## Snippets

```xml
<!-- src/KSAGolf/KSAGolfProps.xml L1-L12 -->
<?xml version="1.0" encoding="utf-8"?>
<!-- Things a kitten holds or strikes, hung through CosmeticAttachments rather than built as parts,
     so each is a <GltfFile> in centimetres with one mesh, one primitive and one material. -->
<Assets>
    <GltfFile Id="KSAGolf_Ball_Glb">
        <Source Path="Meshes/KSAGolf_Ball.glb"/>
    </GltfFile>
    <PbrMaterial Id="KSAGolf_Ball_Material">
        <Diffuse Path="Textures/KSAGolf_Ball_Diffuse.png" Category="Default"/>
        <Normal Path="Textures/KSAGolf_Ball_Normal.png" Category="Default"/>
        <AoRoughMetal Path="Textures/KSAGolf_Ball_PBR.png" Category="Default"/>
    </PbrMaterial>
```

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

```csharp
// src/KSAGolf/Ksa/Golf.cs L308
        if (_ballMesh is null && !AttachmentMesh.TryBuild("KSAGolf_Ball_Glb", "KSAGolf_Ball_Material", out _ballMesh, out string why))
```

## Game APIs touched

| API | Decomp | Purpose |
|---|---|---|
| `AssetBundle` element table | [current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs) | `GltfFile`, `PbrMaterial`, `Sound` (and the unused `Part`, `StaticObject`, `ClutterObject`) |
| `Gltf2Reference.Source` / `OnDataLoad` | [current/decomp/KSA/Gltf2Reference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Gltf2Reference.cs) | Registers the glTF under its `Id` |
| `PbrMaterialReference` children | [current/decomp/KSA/PbrMaterialReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PbrMaterialReference.cs) | `Diffuse`, `Normal`, `AoRoughMetal` texture slots |
| `TextureReference.Category` | [current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs) | Size-cap category per texture |
| `ModLibrary.Get<T>` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs) | Typed lookup by id; throws `NullReferenceException` for an unknown id |

## Pitfalls and version notes

- A mistyped `Id` or texture path is a silent in-game failure (an untextured or invisible mesh).
  `validate-parts.py` checks paths case-exactly, because Windows hides a case typo that breaks Linux,
  and fails if two top-level assets in the mod share an `Id`.[^golf-validate-parts]
- A glb exported with Blender's Materials option set to Placeholder or None has primitives that name
  no material; the mod's checker records that building a renderable from such a `<GltfFile>` throws
  "Index was outside the bounds of the array", and fails the mesh. See
  [GLB authoring and mesh checks](glb-authoring-and-mesh-checks.md).
- A prop exported in metres renders a hundredth of its size inside the kitten's fur, because
  character attachments are drawn through a 0.01 scale; the mod records this in its notes and
  checks props with `checkmesh.py --units-per-metre 100`.[^golf-modding-notes]
- `ModLibrary.Get<T>` throws rather than returning null for an unknown id in build 2026.10.10.5554,
  so code looking assets up must catch.[^ksa-modlibrary]
- None of `StaticObject`, `ClutterObject` or `Part` is used. The course gets collision from code
  instead (see [Course colliders as clutter](course-colliders-as-clutter.md)), because the mod draws
  and positions pieces per hole rather than placing fixed planet statics.

## See also

- [Content-plus-code mod layout](content-and-code-mod-layout.md)
- [GLB authoring pipeline and mesh checks](glb-authoring-and-mesh-checks.md)
- [Mod meshes held by a kitten as cosmetic attachments](kitten-held-props.md)
- [Drawing a mod's static mesh into every viewport](mod-mesh-draw-hook.md)
- [Top-down minigolf course builder](minigolf-course-builder.md)
- [Course grid model and JSON save file](course-grid-and-save-file.md)
- [Read the AssetBundle element table against three real asset files](../../../howto/assets/asset-xml/asset-xml-element-reference/asset-xml-element-reference.md) - how-to article demonstrating this feature
- [Lay out mod-folder-relative paths and prefixed global asset ids](../../../howto/assets/asset-xml/resolve-asset-ids-and-mod-relative-paths/resolve-asset-ids-and-mod-relative-paths-1.md) - how-to article demonstrating this feature
- [Declare bare GltfFile and PbrMaterial assets and pair them by id in code](../../../howto/assets/models-and-textures/declare-gltf-and-pbr-material-assets/declare-gltf-and-pbr-material-assets-1.md) - how-to article demonstrating this feature
- [Add a CosmeticAttachment with a static offset and build the prop mesh from the mod's glTF and material](../../../howto/kittens/appearance/attach-a-prop-to-a-kitten-bone/attach-a-prop-to-a-kitten-bone-1.md) - how-to article demonstrating this feature

[^golf-props-xml]: src/KSAGolf/KSAGolfProps.xml
[^golf-course-xml]: src/KSAGolf/KSAGolfCourse.xml
[^golf-piececatalogue]: src/KSAGolf/Sim/PieceCatalogue.cs
[^golf-attachmentmesh]: src/KSAGolf/Ksa/AttachmentMesh.cs
[^golf-golf]: src/KSAGolf/Ksa/Golf.cs
[^golf-coursebuilder]: src/KSAGolf/Ksa/CourseBuilder.cs
[^golf-modding-notes]: docs/KSA-MODDING-NOTES.md
[^golf-validate-parts]: tools/validate-parts.py
[^ksa-assetbundle]: KSA.AssetBundle (decompiled, build 2026.10.10.5554)
[^ksa-gltf2reference]: KSA.Gltf2Reference (decompiled, build 2026.10.10.5554)
[^ksa-pbrmaterialreference]: KSA.PbrMaterialReference (decompiled, build 2026.10.10.5554)
[^ksa-texturereference]: KSA.TextureReference (decompiled, build 2026.10.10.5554)
[^ksa-filereference]: KSA.FileReference (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-character-assets]: Core/CharacterAssets.xml (build 2026.10.10.5554)
