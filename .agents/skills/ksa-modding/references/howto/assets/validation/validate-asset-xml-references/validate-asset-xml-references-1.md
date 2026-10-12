---
type: How-To
title: Check asset XML ids and paths offline with check-xml.sh and validate-parts.py
description: A well-formedness check and a reference validator that cross-check SubPart InstanceOf, Material and Mesh ids against the mod's own files, the atlas mesh names and Core's library, with case-exact paths and an offline mode, run from the deploy script and CI
category: assets
group: validation
topic: validate-asset-xml-references
approach: 1
difficulty: intermediate
prerequisites: []
demonstrated_by:
  - mods/ksacars/features/art-and-audio-pipeline-tooling
  - mods/ksagolf/features/glb-authoring-and-mesh-checks
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
tags: [validation, xml, ids, paths, case-sensitivity, ci, deploy, silent-failure]
---

A bad mesh id, a mistyped texture path or a malformed XML file is a silent failure in KSA: the part renders untextured, invisible or not at all, with nothing useful in the log. Offline scripts catch each before the game starts. KSACars wrote two: `check-xml.sh` (is the XML readable at all) and `validate-parts.py` (does what the XML says resolve), and runs
both from `deploy.sh` and a pre-push check. KSAGolf copied them. This article shows what each checks and why that check exists.

Demonstrated by [Art and audio pipeline tooling](../../../../mods/ksacars/features/art-and-audio-pipeline-tooling.md) and [GLB authoring pipeline and mesh checks](../../../../mods/ksagolf/features/glb-authoring-and-mesh-checks.md).

## Background: how the game does it

Each failure mode has an engine cause:

| Mistake | What the game does |
|---|---|
| XML not listed in `mod.toml` `assets` | Never read: `Mod.LoadAssetBundles` loops only over the listed entries ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L157-L169)) |
| Missing XML file | `XmlLoader.Deserialize` returns null when `File.Exists` is false; `Load` returns null with no log |
| Malformed XML | `TryDeserialize` logs the exception and `Load` returns null: the assets simply do not exist |
| Mistyped `Path` | `FileReference.Load` logs "mod file ... not found at ..." and leaves the reference unloaded; case mismatches only fail on case-sensitive file systems |
| Unknown `Mesh Id`, `Material Id` or `InstanceOf` | `ModLibrary.Get<T>` throws `NullReferenceException` ("... is null for '...'") when resolved |
| Duplicate id | `SerializedCollection.Register` is first-wins and returns false; the later asset becomes a reference |

([current/decomp/KSA/XmlLoader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlLoader.cs#L1-L52), [current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L67-L146), [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1059-L1085), [current/decomp/KSA/SerializedCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedCollection.cs#L20-L33)).

## Lifecycle and timing

Development time: `deploy.sh` runs `validate-parts.py --offline` before copying files; `check-all.sh` (pre-push hook and CI) runs `check-xml.sh`, `validate-parts.py --offline` and the mesh checks. In game the failures appear at `startup:asset-load` (missing assets) or when a part is first built or drawn (unresolved ids). See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Check that every shipped XML parses.** The cheapest check covers the failure that is hardest to read from the game, and it stays useful when the game is not installed:

   ```bash
   # tools/check-xml.sh L16-L40
   cd "$REPO_ROOT" || exit 1

   python3 - <<'PY'
   import sys
   import xml.etree.ElementTree as ET
   from pathlib import Path

   paths = sorted(Path("src/KSACars").glob("KSACars*.xml"))
   if not paths:
       print("error: no asset XML found under src/KSACars", file=sys.stderr)
       sys.exit(1)

   bad = 0
   for path in paths:
       try:
           ET.parse(path)
       except ET.ParseError as e:
           print(f"  MALFORMED {path}: {e}", file=sys.stderr)
           bad += 1
       else:
           print(f"  {path} ok")

   if bad:
       print(f"\n{bad} malformed file(s)", file=sys.stderr)
       sys.exit(1)
   ```

2. **State what the validator guarantees, in its docstring.** KSACars' validator documents the silent failures it exists for:

   ```python
   # tools/validate-parts.py L2-L21
   """
   Checks the mod's part XML before it ever reaches the game.

   A bad asset Id or a mistyped texture path is a *silent* failure in-game: the part renders
   untextured, or invisible, or not at all, with nothing in any log to say why. This walks the
   XML and verifies, among other things:

     1. every SubPart InstanceOf resolves, against our own file or against Core's library
     2. every Material Id resolves, likewise
     3. every Mesh Id exists in the mesh atlas the file declares
     4. every file path (<MeshAtlas>, <Diffuse>, <Normal>, <AoRoughMetal>) is actually there

   Check 3 is the one that earns its keep -- the mesh names come out of Blender, and nothing
   else in the toolchain would notice a rename.

       ./tools/validate-parts.py
       KSA_DIR=/path/to/KSA ./tools/validate-parts.py

   Exits non-zero if anything is unresolved or the XML is malformed.
   """
   ```

3. **Collect the ids Core declares**, so references to stock assets resolve. Every element with an `Id` in Core's asset XML is recorded by tag:

   ```python
   # tools/validate-parts.py L55-L79
   def collect_core_ids(core_dir):
       """Maps element tag -> set of declared Ids across every Core asset XML."""
       declared = {}
       for path in sorted(core_dir.glob("*.xml")):
           try:
               root = ET.parse(path).getroot()
           except ET.ParseError as exc:
               print(f"  skipping unparseable {path.name}: {exc}", file=sys.stderr)
               continue
           for el in root.iter():
               ident = el.get("Id")
               if ident:
                   declared.setdefault(el.tag, set()).add(ident)
       return declared


   def atlas_mesh_names(glb_path):
       """Mesh names inside a .glb, or None if it cannot be read."""
       try:
           gltf = meshinfo.read_glb_json(str(glb_path))
       except Exception as exc:
           print(f"  could not read {glb_path.name}: {exc}", file=sys.stderr)
           return None
       return {m.get("name") for m in gltf.get("meshes", [])}

   ```

4. **Resolve every reference per file.** Atlas path exists case-exactly and its mesh names are read from the GLB JSON; texture paths exist; each `Mesh Id` is in the declared atlas; each `SubPart InstanceOf` resolves locally or in Core; each `Material Id` is a material or (with a phase suffix) a substance:

   ```python
   # tools/validate-parts.py L96-L130
       meshes_in_atlas = set()
       for el in root.iter("MeshAtlas"):
           checked += 1
           atlas = path.parent / el.get("Path", "")
           if not exists_case_exact(atlas):
               print(f"  MISSING MeshAtlas Path=\"{el.get('Path')}\"", file=sys.stderr)
               problems += 1
               continue
           names = atlas_mesh_names(atlas)
           if names is None:
               problems += 1
           else:
               meshes_in_atlas |= names
               print(f"  atlas: {el.get('Path')}  ({len(names)} meshes)")

       for tag in TEXTURE_TAGS:
           for el in root.iter(tag):
               rel = el.get("Path")
               if not rel:
                   continue          # referenced by Id instead, which the Core check covers
               checked += 1
               if not exists_case_exact(path.parent / rel):
                   print(f"  MISSING {tag} Path=\"{rel}\"", file=sys.stderr)
                   problems += 1

       for el in root.iter("Mesh"):
           ident = el.get("Id")
           if not ident:
               continue
           checked += 1
           # Only meaningful once this file declares an atlas of its own; a mesh Id could
           # otherwise legitimately live in Core's library.
           if meshes_in_atlas and ident not in meshes_in_atlas:
               print(f"  UNRESOLVED Mesh Id=\"{ident}\" (not in the declared atlas)", file=sys.stderr)
               problems += 1
   ```

5. **Make the mod's own ids checkable without Core.** A `SubPart` instancing a `KSACars_*` template that does not exist "kills the game on load with `PartTemplate is null`" and needs no Core to detect: a name used here is declared here.

   ```python
   # tools/validate-parts.py L132-L152
       for el in root.iter("SubPart"):
           source = el.get("InstanceOf")
           if not source:
               continue
           checked += 1

           # The mod's own Ids must resolve in its own file, whether or not Core's library could be
           # read.
           #
           # The Core check below is skipped when that library is unavailable - offline, or a
           # different install layout - and this case must not be skipped with it. A SubPart
           # instancing a KSACars_* template that does not exist kills the game on load with
           # "PartTemplate is null", and detecting that needs no Core: a name used here is declared
           # here.
           if source.startswith("KSACars_") and source not in local_subparts:
               print(f"  UNDECLARED SubPart InstanceOf=\"{source}\" - no such template in this file",
                     file=sys.stderr)
               problems += 1
               continue

           if core_subparts is None:
   ```

6. **Compare case-exactly.** Windows and macOS file systems are case-insensitive, so `Textures/Foo.png` versus `foo.png` loads there and fails on Linux, where KSA also runs; compare against the real directory listing:

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

7. **Check the manifest.** Every asset XML at the mod root must be in `mod.toml` `assets` and vice versa:

   ```python
   # tools/validate-parts.py L244-L251
   def check_assets_declared():
       """Verifies every asset XML at the mod root is listed in mod.toml.

       mod.toml names its assets one by one. A file left out is simply never loaded and nothing
       reports it -- no warning at load, no missing-asset error. Whatever it declared then resolves
       to null at the point something first asks for it, which for a character is a crash inside
       KSA's own constructor rather than anything pointing back here.
       """
   ```

   ```python
   # tools/validate-parts.py L255-L269
       problems = checked = 0
       for path in sorted(MOD.glob("KSACars*.xml")):
           checked += 1
           if path.name not in declared:
               print(f"  UNDECLARED {path.name} -- present but not in mod.toml's assets, so KSA "
                     f"never loads it", file=sys.stderr)
               problems += 1

       for name in sorted(declared):
           if not (MOD / name).is_file():
               print(f"  MISSING {name} -- listed in mod.toml, not on disk", file=sys.stderr)
               problems += 1

       return problems, checked

   ```

8. **Offer `--offline`.** Everything that depends only on the mod's own files still runs without the game installed, which includes case on Linux CI:

   ```python
   # tools/validate-parts.py L424-L466
   def main():
       # Without the game installed, everything that depends only on the mod's own files can still
       # be checked -- and on Linux that includes case, which is the difference between a mod that
       # loads on both platforms and one that only loads on Windows.
       parser = argparse.ArgumentParser(description="Check the part XML against itself, the meshes and Core.")
       parser.add_argument("--offline", action="store_true",
                           help="skip the checks that need KSA's Core assets")
       offline = parser.parse_args().offline

       if offline:
           print("offline: skipping the checks that need KSA's Core assets\n")
           core_subparts = core_materials = core_substances = None
       elif not CORE.is_dir():
           print(f"error: Core content not found at {CORE}", file=sys.stderr)
           print("       set KSA_DIR to your install, or pass --offline", file=sys.stderr)
           return 1
       else:
           print(f"reading Core assets from {CORE}")
           declared = collect_core_ids(CORE)
           core_subparts = declared.get("SubPart", set())
           core_materials = declared.get("PbrMaterial", set())
           core_substances = declared.get("Substance", set())
           print(f"  {len(core_subparts)} subparts, {len(core_materials)} materials, "
                 f"{len(core_substances)} substances declared\n")

       problems = checked = 0
       for path in sorted(MOD.glob("KSACars*.xml")):
           print(f"checking {path.relative_to(REPO)}")
           p, c = check_file(path, core_subparts, core_materials, core_substances)
           problems += p
           checked += c

       for label, check in [
           ("every asset XML is declared in mod.toml", check_assets_declared),
           ("every stacking connector is sized to its part", check_stacking_connectors_sized),
           ("every editor tag a part names is defined", lambda: check_editor_tags(None if offline else CORE)),
           ("no asset id is declared twice", check_asset_id_collisions),
           ("for planes shared between subparts", check_cross_body_planes),
       ]:
           print(f"checking {label}")
           p, c = check()
           problems += p
           checked += c
   ```

9. **Run it from the deploy script and a pre-push hook**, and make it exit non-zero on any problem.

## Complete example

- [tools/check-xml.sh](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/check-xml.sh), [tools/validate-parts.py](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/validate-parts.py), [tools/meshinfo.py](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/meshinfo.py)
- KSAGolf's copy: [tools/validate-parts.py](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/validate-parts.py)

## Variations and gotchas

- **`<Material>` has two meanings.** Under a model component it names a `PbrMaterial`; elsewhere it is a `Substance` referenced by phase (`Aluminum.2014(s)` for the solid declared as `Aluminum.2014`); the validator branches on this.
- **Only top-level ids register.** An `Id` deeper in the tree is a reference (`<Mesh Id>`, a nested `<SubPart Id>` naming an instance); only direct children of `<Assets>` declare a name.
- **`GameData` entries are companions** keyed on another asset's id, not redeclarations.
- **Run the validator in CI against a stub of Core** if the game cannot be installed; otherwise use `--offline`.
- **Extend it** with the checks in [Extend the validator for generated geometry, ids named in C# and shared planes](validate-asset-xml-references-2.md) and the game-serializer route in [Parse an AssetBundle with the game serializer, without OnDataLoad, and apply pure rules](validate-asset-xml-references-3.md).

## See also

- [Extend the validator for generated geometry, ids named in C# and shared planes](validate-asset-xml-references-2.md), [Parse an AssetBundle with the game serializer, without OnDataLoad, and apply pure rules](validate-asset-xml-references-3.md), [Resolve asset ids and file paths correctly across mods](../../asset-xml/resolve-asset-ids-and-mod-relative-paths/overview.md), [Check GLB meshes offline: z-fighting, degenerate UVs, names and bounds](../find-z-fighting-and-degenerate-uvs-in-glb/overview.md),
  [Add a part's art with its own GLB model and PBR textures](../../models-and-textures/add-a-part-model-with-glb-and-pbr-textures/overview.md), [Run one check script from the pre-push hook and from CI](../../../testing-and-tooling/offline-tests/run-repo-checks-from-a-pre-push-hook-and-ci/overview.md)
- [Art and audio pipeline tooling](../../../../mods/ksacars/features/art-and-audio-pipeline-tooling.md), [GLB authoring pipeline and mesh checks](../../../../mods/ksagolf/features/glb-authoring-and-mesh-checks.md)
