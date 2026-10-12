---
type: Mod Feature
title: Asset XML, mesh and shader checks that run without the game
description: Python and shell checks that catch silent asset failures before launch, including undeclared or colliding ids, missing or case-wrong paths, shared texture files, C# names with no asset, z-fighting and degenerate UVs in GLB meshes, and GLSL that will not compile
mod: ksastructures
category: testing-and-tooling
techniques: [xml-asset, glb-model, glsl-shader, static-analysis]
hooks: []
lifecycle: []
game_types: []
tags: [validation, xml, glb, gltf, z-fighting, uv, glslang, python, ci]
---

Most asset mistakes in KSA fail silently: a mistyped mesh id renders nothing, a texture path named
by two materials is drawn without its texture on one of them, an XML file left out of `mod.toml`
is never loaded, and a GLSL error only shows when the game compiles the shader at load. The mod
ships small, dependency-light checks for each, wired into `tools/check-all.sh` and CI.[^mod-checkall][^mod-claude]

## How it works

1. **`tools/check-xml.sh`** parses every `KSAStructures*.xml` with Python's `ElementTree`. A
   malformed asset file otherwise shows up in game only as the part not existing.[^mod-checkxml]
2. **`tools/validate-parts.py`** checks what the XML says, against itself, the meshes, the C# code
   and (unless `--offline`) the ids declared by every XML under the install's `Content/Core`:[^mod-validate]

   | Check | Catches |
   |---|---|
   | `SubPart InstanceOf`, `Material Id` resolve, here or in Core | Missing references |
   | every `Mesh Id` exists in the declared `MeshAtlas` (node names read from the GLB's JSON chunk) | Blender renames |
   | every `Path` exists with exact case | Works on Windows, fails on Linux |
   | every `StaticObject` `SubObject` names a declared `StaticSubObject` | Missing static parts |
   | `check_assets_declared` | An asset XML not listed in `mod.toml`'s `assets` is never loaded[^mod-modtoml] |
   | `check_asset_id_collisions` | Two assets on one id, counting meshes an atlas registers by node name |
   | `check_textures_unshared` | One texture file named by two materials |
   | `check_code_names_what_is_declared` | Quoted `KSAOffice_*` and related ids, and `Textures/`/`Meshes/`/`Shaders/` paths in `Ksa/*.cs` and `Sim/*.cs`, with no declaration |
   | `check_editor_tags`, `check_stacking_connectors_sized`, `check_cross_body_planes` | Undefined editor tags, unsized stacking connectors, coplanar faces between sub-parts as placed |

   It runs in CI (`--offline`) and in `tools/deploy.sh` before every install.[^mod-deploy]
3. **`tools/meshinfo.py`** prints every mesh in a `.glb` atlas with its bounding box, read from the
   glTF accessors' `min`/`max` without decoding vertices, so parts can be laid out with real
   dimensions.[^mod-meshinfo]
4. **`tools/model/checkmesh.py`** finds z-fighting without the game: each triangle is reduced to a
   sign-canonical, quantized plane, and any plane holding triangles from different directions with
   overlapping extents is reported, along with zero-area UV triangles. `--compare` diffs two versions
   of a model; `check-all.sh` runs it over every drawn atlas (shadow casters are excluded, since two
   faces on one plane cast one shadow).[^mod-checkmesh][^mod-checkall]
5. **`tools/check-shaders.sh`** compiles `Shaders/*.comp` with Khronos `glslangValidator`
   (`--install` fetches one) for Vulkan 1.3, writing a temporary `Content/KSAStructuresCore.glsl` with
   relative includes and passing the install's `Content/Core/Shaders` as `-I`. It inserts
   `#extension GL_GOOGLE_include_directive : require` because glslang needs it where KSA's shaderc
   does not, and skips with a notice when no install is present.[^mod-checkshaders] See
   [core shader library include](./core-shader-library-include.md) for the in-game side.

## Lifecycle and timing

None in game. These run from `tools/check-all.sh` (pre-push hook and CI) and from `tools/deploy.sh`.

## Key code

| Path | Role |
|---|---|
| [tools/validate-parts.py](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/validate-parts.py#L266-L353) | mod.toml, shared-texture and code-name checks |
| [tools/validate-parts.py](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/validate-parts.py#L509-L565) | Entry point and the check list |
| [tools/meshinfo.py](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/meshinfo.py) | GLB JSON chunk reader and bounds |
| [tools/model/checkmesh.py](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/model/checkmesh.py) | Coplanar-face and UV checks |
| [tools/check-xml.sh](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/check-xml.sh) | Well-formedness |
| [tools/check-shaders.sh](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/check-shaders.sh) | Offline GLSL compile |

## Snippets

Reading mesh names straight from a GLB's JSON chunk:

```python
# tools/meshinfo.py L17-L34

def read_glb_json(path):
    """Returns the JSON chunk of a .glb as a dict."""
    with open(path, "rb") as fh:
        magic, version, _total = struct.unpack("<III", fh.read(12))
        if magic != 0x46546C67:  # 'glTF'
            raise ValueError(f"{path} is not a GLB (bad magic)")
        if version != 2:
            raise ValueError(f"unsupported glTF version {version}")

        while True:
            header = fh.read(8)
            if len(header) < 8:
                raise ValueError("no JSON chunk found")
            length, kind = struct.unpack("<II", header)
            payload = fh.read(length)
            if kind == 0x4E4F534A:  # 'JSON'
                return json.loads(payload.decode("utf-8"))
```

No texture file shared between materials:

```python
# tools/validate-parts.py L293-L315
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

Compiling the shader against the game's library outside the game:

```bash
# tools/check-shaders.sh L45-L66

mkdir -p "$WORK/Content"
printf '#include "Common/Global.glsl"\n#include "Atmosphere/AtmosphereLuts.glsl"\n#include "Common/TextureSet.glsl"\n' \
    > "$WORK/Content/KSAStructuresCore.glsl"

failed=0
for shader in "$REPO_ROOT"/src/KSAStructures/Shaders/*.comp; do
    name="$(basename "$shader")"

    # shaderc takes #include without being asked; glslang wants the extension named.
    awk 'NR==1 { print; print "#extension GL_GOOGLE_include_directive : require"; next } 1' \
        "$shader" > "$WORK/$name"

    if out="$("$GLSLANG" -V --target-env vulkan1.3 "-I$LIBRARY" -o /dev/null "$WORK/$name" 2>&1)"; then
        echo "  $name compiles"
    else
        failed=1
        # The line numbers are the file's own plus the one inserted above.
        echo "$out" | grep -E "ERROR" | sed "s#$WORK/##" | sed 's/^/  /'
        echo "  (line numbers are one past the file's: the include directive is inserted)"
    fi
done
```

## Game APIs touched

None at runtime. The checks read the mod's own XML and GLB files and, online, the install's
`Content/Core` XML and shader library.

## Pitfalls and version notes

- **"A texture path loads for only one material"** is the mod's observation of KSA's loader; it is
  recorded in the mod's notes and enforced here, not verified against the decomp in this corpus.
- **Case matters on Linux.** `exists_case_exact` compares against the directory listing, because
  `Path.is_file()` is case-insensitive on Windows and macOS.
- **The offline mode skips Core lookups**, so a reference to a Core sub-part or material is only
  checked on a machine with the game installed.

## See also

- [Headless tests and CI](./headless-tests-and-ci.md) - the script and pipeline that run these
- [Generated deep ramp](./generated-deep-ramp.md) - generated assets these checks cover
- [Core shader library include](./core-shader-library-include.md) - the shader being compiled
- [Office furniture as single-part craft](./office-furniture-craft.md) - the fixed part ids
- [Extend the validator for generated geometry, ids named in C# and shared planes](../../../howto/assets/validation/validate-asset-xml-references/validate-asset-xml-references-2.md) - how-to article demonstrating this feature
- [Check shaders offline: a per-folder check beside asset and mesh validation](../../../howto/shaders/glsl-workflow/check-mod-shaders-offline-with-glslang/check-mod-shaders-offline-with-glslang-2.md) - how-to article demonstrating this feature

[^mod-validate]: tools/validate-parts.py
[^mod-meshinfo]: tools/meshinfo.py
[^mod-checkmesh]: tools/model/checkmesh.py
[^mod-checkxml]: tools/check-xml.sh
[^mod-checkshaders]: tools/check-shaders.sh
[^mod-checkall]: tools/check-all.sh
[^mod-deploy]: tools/deploy.sh
[^mod-modtoml]: src/KSAStructures/mod.toml
[^mod-claude]: KSAStructures CLAUDE.md (asset ids and engine facts)
