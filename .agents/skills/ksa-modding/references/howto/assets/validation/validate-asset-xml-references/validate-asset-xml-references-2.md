---
type: How-To
title: Extend the validator for generated geometry, ids named in C# and shared planes
description: Add checks for XML not listed in mod.toml, ids declared twice including atlas mesh names, launch geometry disagreeing with a generated report, textures shared between materials, ids named as strings in C#, unsized stacking connectors and planes shared between subparts
category: assets
group: validation
topic: validate-asset-xml-references
approach: 2
difficulty: advanced
prerequisites: []
demonstrated_by:
  - mods/ksarmory/features/blender-model-pipeline-and-checks
  - mods/ksastructures/features/asset-and-mesh-validation-tools
techniques: [offline-asset-validation]
hooks: [offline python validate-parts.py]
lifecycle: [startup:asset-load]
game_types:
  - KSA.AssetBundle
  - KSA.MeshAtlasFileReference
  - KSA.ModLibrary
  - KSA.XmlHelper
  - KSA.XmlLoader
  - KSA.SerializedId
  - KSA.PartTemplate
tags: [validation, xml, ids, textures, shared-texture, csharp-ids, planes, connectors]
---

The basic validator ([Check asset XML ids and paths offline with check-xml.sh and validate-parts.py](validate-asset-xml-references-1.md)) checks that references resolve. KSArmory and KSAStructures extended it with checks that each encode a failure the author actually hit: ids that appear as string constants in C#, a texture file named by two materials, launch geometry that disagrees with the model it was
modelled in, planes shared between two subparts as the XML places them, stacking connectors with no size. This article lists the checks, the engine behaviour behind each, and the shape of the code.

Demonstrated by [Headless Blender model generation and offline GLB checks](../../../../mods/ksarmory/features/blender-model-pipeline-and-checks.md) and [Asset XML, mesh and shader checks that run without the game](../../../../mods/ksastructures/features/asset-and-mesh-validation-tools.md).

## Background: how the game does it

| Check | Engine behaviour it encodes |
|---|---|
| XML not in `mod.toml` | Only listed files are read ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L157-L169)) |
| Id declared twice | `SerializedCollection.Register` first-wins, per registry; Parts and SubParts share the part table, meshes the mesh table ([current/decomp/KSA/SerializedCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedCollection.cs#L20-L33), [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L581-L676)) |
| Texture file named by two materials | `FileReference.Load` registers a texture path once; later declarations become references that are never bound, and the part render path reads `Material.DiffuseReference.BindlessHandle` from that declaration, which stays 0 ([current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L67-L72), [current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs#L125-L156), [current/decomp/KSA/PartModel.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModel.cs#L516-L523)) |
| Ids named as strings in C# | `ModLibrary.Get<T>` throws for an unknown id; `TryGet<T>` returns false |
| Unsized stacking connector | A part mates a node only if its own is no larger ([Add a part's art with its own GLB model and PBR textures](../../models-and-textures/add-a-part-model-with-glb-and-pbr-textures/overview.md)) |
| Coplanar faces between subparts | The renderer has nothing to separate them ([Check GLB meshes offline: z-fighting, degenerate UVs, names and bounds](../find-z-fighting-and-degenerate-uvs-in-glb/overview.md)) |

## Lifecycle and timing

Development time, from `deploy.sh`, `check-all.sh` and both CI workflows. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Declare nothing twice, counting the meshes the atlas registers** (KSACars' version, copied by KSArmory and KSAStructures). Walk only the direct children of `<Assets>`: a deeper `Id` is a reference. The mod's own comment notes that the game keeps the first and that only an importer enforcing uniqueness (SpaceDock's) rejects the duplicate. In the decompiled registries a SubPart and a mesh are in different tables, so
   treat this as a hygiene rule unless two parts, two meshes or two materials collide:

   ```python
   # KSACars tools/validate-parts.py L271-L285
   def check_asset_id_collisions():
       """No Id may name two different assets, counting the meshes the atlas registers.

       A <MeshAtlas> registers every mesh under its glTF node name, so declaring a SubPart -- or
       anything else -- under that same name puts two assets on one Id and the loader keeps whichever
       it saw first. Nothing fails: the mod loads, the part renders, the game runs. Only an importer
       that enforces uniqueness, such as SpaceDock's, rejects it.
       """
       problems = checked = 0

       # Across the whole mod, not per file: the loader registers one namespace for all of them, and
       # the importer's rule is "declared more than once within this mod". Two files each internally
       # consistent can still collide with each other.
       from_atlas = set()
       declared = {}
   ```

   ```python
   # KSACars tools/validate-parts.py L298-L330
           # Top-level only. An Id deeper in the tree is a *reference* -- <Mesh Id>, <StartSound Id>,
           # a nested <SubPart Id> naming an instance -- and only the direct children of <Assets>
           # register a name. Walking the whole tree flags every reference as a redeclaration.
           for el in root:
               ident = el.get("Id")
               if ident is None:
                   continue

               # A *GameData entry is a companion keyed on another asset's Id -- that is how KSA
               # pairs a part's art with its physics, and Core does the same. It is a different
               # registry, so it is not a redeclaration.
               if el.tag.endswith("GameData"):
                   continue

               checked += 1

               if ident in declared:
                   where, first = declared[ident]
                   print(f"  DUPLICATE Id \"{ident}\" on <{first}> in {where} and <{el.tag}> "
                         f"in {path.name}", file=sys.stderr)
                   problems += 1
               elif ident in from_atlas:
                   print(f"  DUPLICATE Id \"{ident}\" on <{el.tag}> in {path.name} — the atlas "
                         f"already registers a mesh under that name", file=sys.stderr)
                   problems += 1

               declared[ident] = (path.name, el.tag)

       if problems == 0:
           print(f"  {checked} asset id(s), none declared twice")

       return problems, checked

   ```

2. **List the XML files and require each in `mod.toml`** ([Check asset XML ids and paths offline with check-xml.sh and validate-parts.py](validate-asset-xml-references-1.md), `check_assets_declared`).

3. **One texture file per material.** Compare lower-cased paths and report the second material that names a file:

   ```python
   # KSAStructures tools/validate-parts.py L293-L315
   def check_textures_unshared():
       """No texture file may be named by two materials.

       KSA loads a texture path once and hands it to one material; the second material naming the same
       file is drawn without it, and nothing is logged. Each material therefore carries files of its
       own, however alike two of them are.
       """
       problems = checked = 0
       owners = {}
       for path in sorted(MOD.glob("KSAStructures*.xml")):
           for material in ET.parse(path).getroot().iter("PbrMaterial"):
               for el in material:
                   rel = el.get("Path")
                   if el.tag not in TEXTURE_TAGS or not rel:
                       continue
                   checked += 1
                   owner = owners.setdefault(rel.lower(), material.get("Id"))
                   if owner != material.get("Id"):
                       print(f"  SHARED {rel} -- named by {owner} and {material.get('Id')}; KSA loads "
                             f"it for only one of them", file=sys.stderr)
                       problems += 1

       return problems, checked
   ```

4. **Scan C# for ids and paths.** The code finds its static objects, parts and materials by string id, and an id that matches nothing is a tower that is not placed with one warning in the log. Check quoted ids and `Textures/`, `Meshes/`, `Shaders/` paths against the declared set and the disk:

   ```python
   # KSAStructures tools/validate-parts.py L318-L332
   def check_code_names_what_is_declared():
       """Every asset Id and texture path a C# file names is declared in the asset XML or on disk.

       The code finds its static objects, parts, materials and shader by Id, and an Id that matches
       nothing is a tower that is not placed or a pass that does not draw, with one warning in the log.
       """
       declared = set()
       for path in sorted(MOD.glob("KSAStructures*.xml")):
           root = ET.parse(path).getroot()
           declared |= {el.get("Id") for el in root if el.get("Id")}

       problems = checked = 0
       for source in sorted(MOD.glob("Ksa/*.cs")) + sorted(MOD.glob("Sim/*.cs")):
           text = source.read_text()

   ```

   ```python
   # KSAStructures tools/validate-parts.py L339-L350
           for line in text.splitlines():
               # A line of embedded XML names decals, which the code itself declares.
               if "<" in line and "/>" in line or line.lstrip().startswith(("<", "//")):
                   continue
               for ident in re.findall(r'"((?:KSAOffice_|KSArmory_Prefab_Office|KSAStructures)\w*)"', line):
                   if ident in ("KSAStructures",):
                       continue
                   checked += 1
                   if ident not in declared:
                       print(f"  UNDECLARED {ident} -- named in {source.name}, declared in no asset XML",
                             file=sys.stderr)
                       problems += 1
   ```

5. **Compare derived numbers with the model.** KSArmory's launch positions exist twice (the model script and `Sim/Arsenal.cs`) and nothing at build or run time connects them. The validator reads `muzzles.json` written by the build and compares it with the C# profile and the XML, scoping each regex to the profile's own initialiser:

   ```python
   # KSArmory tools/validate-parts.py L186-L207
       """Verifies one turreted launcher's profile still agrees with the mesh it was modelled in.

       The launch positions exist twice: once in the model script, which places the containers and
       the barrels, and once in Sim/Arsenal.cs, which draws markers on them and spawns rounds from
       them. Nothing at build or run time connects the two, and a silent disagreement puts the
       launch markers in mid-air beside the vehicle. So compare them here.

       `key` names the muzzles.json block; None means the top-level one, which is the Pantsir's.
       Every regex is scoped to the profile's own initialiser for the same reason
       check_fixed_launcher_geometry is: an unscoped search binds to whichever launcher appears
       first in the file and passes whatever every other one says.

       Fields absent from the block are skipped rather than missing, because a turret is not
       obliged to carry all of them -- the CIWS has no tubes and no pods.

       Skips quietly if the model has not been built in this checkout -- muzzles.json is written
       by tools/model/build.sh, which needs Blender.
       """
       muzzles = REPO / "tools" / "model" / "muzzles.json"
       source = MOD / "Sim" / "Arsenal.cs"
       if not muzzles.is_file():
           print("  (no tools/model/muzzles.json -- run tools/model/build.sh to enable this check)")
   ```

6. **Verify stacking connectors are sized** and **editor tags are defined** (`check_stacking_connectors_sized`, `check_editor_tags`), and **planes shared between two subparts as the XML places them** (`check_cross_body_planes`) in [tools/validate-parts.py](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/validate-parts.py#L183-L243).

7. **Compile shaders offline too** with glslang in the same script family; that side is in [Compile-check a mod's GLSL without launching the game](../../../shaders/glsl-workflow/check-mod-shaders-offline-with-glslang/overview.md).

8. **Wire the order:** XML parse, references, manifest, ids, textures, code names, geometry. One failing check should not hide the others; each returns `(problems, checked)`.

## Complete example

- KSAStructures (textures, ids in C#): [tools/validate-parts.py](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/validate-parts.py) and [tools/check-xml.sh](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/check-xml.sh), [tools/meshinfo.py](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/meshinfo.py)
- KSArmory (launch geometry and optics against `muzzles.json`): [tools/validate-parts.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/validate-parts.py) (launch geometry and optics against `muzzles.json`)
- KSACars (the base checks, id collisions): [tools/validate-parts.py](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/validate-parts.py) (the base checks)

## Variations and gotchas

- **Shared texture paths are a part-render problem.** KSAGolf's props (drawn through the code-side material system) share a normal map across materials without trouble; parts and static objects do not ([Declare glTF meshes and PBR materials in asset XML and author their texture sets](../../models-and-textures/declare-gltf-and-pbr-material-assets/overview.md)).
- **Scope your regexes.** An unscoped search binds to whichever launcher appears first in the file and passes whatever every other one says.
- **Skipped checks must say so.** KSArmory prints "no tools/model/muzzles.json -- run tools/model/build.sh to enable this check" instead of passing silently.
- **Embedded XML in C# strings** (decals declared in code) must be skipped by the code-names check.

## See also

- [Check asset XML ids and paths offline with check-xml.sh and validate-parts.py](validate-asset-xml-references-1.md), [Parse an AssetBundle with the game serializer, without OnDataLoad, and apply pure rules](validate-asset-xml-references-3.md), [Check GLB meshes offline: z-fighting, degenerate UVs, names and bounds](../find-z-fighting-and-degenerate-uvs-in-glb/overview.md), [Run one check script from the pre-push hook and from CI](../../../testing-and-tooling/offline-tests/run-repo-checks-from-a-pre-push-hook-and-ci/overview.md),
  [Define a part with its own game data and GLB model](../../../vehicles-and-parts/parts/define-a-part-with-game-data/overview.md)
- [Headless Blender model generation and offline GLB checks](../../../../mods/ksarmory/features/blender-model-pipeline-and-checks.md), [Asset XML, mesh and shader checks that run without the game](../../../../mods/ksastructures/features/asset-and-mesh-validation-tools.md)
