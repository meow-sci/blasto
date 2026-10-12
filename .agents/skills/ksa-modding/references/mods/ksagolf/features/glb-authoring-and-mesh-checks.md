---
type: Mod Feature
title: GLB authoring pipeline and mesh checks
description: Authoring KSA meshes live in Blender over MCP with a human geometry sign-off, an export contract checked against Core's atlases, and offline Python checkers that catch zero-UV-area faces, coplanar z-fighting, unmaterialed primitives and bad asset references before the game ever loads them
mod: ksagolf
category: assets
techniques: [glb-model, xml-asset, blender-mcp, asset-validation]
hooks: []
lifecycle: []
game_types: [KSA.Gltf2Reference, KSA.PbrMaterialReference, KSA.MeshAtlasFileReference]
tags: [glb, gltf, blender, mcp, textures, atlas, z-fighting, uv, validation, tooling]
---

KSAGolf's 14 glb files (ball, putter, twelve course pieces) were authored in Blender driven over
MCP in a live session, and the repo carries the rules and tools that keep that art loadable. The
`.blend` sources are not in the repository, so the exported glb is the only artefact and the
checkers are the only gate on it.[^golf-model-readme]

The pieces worth reusing are the export contract (stated with evidence from Core's 44 atlases), the
two in-game defects that no Blender preview shows (zero-UV-area faces and coplanar faces) and the
scripts that find them headlessly.

## How it works

**Authoring loop (`.claude/skills/ksa-blender/SKILL.md`).**[^golf-blender-skill]

1. Build in Blender over the Blender Lab MCP addon, using its dedicated tools and reading values
   back rather than assuming them.
2. Stop before unwrapping: a human looks at the geometry in Blender and says yes. Everything after
   the unwrap (atlas packing, bake) is welded to the shape.
3. Unwrap, bake (with a key-colour background), dilate the atlas, export.
4. Run the checkers; preview the export from several angles.

**Export contract** (verified by the mod against Core's 1054 atlas nodes):

| Rule | Detail |
|---|---|
| glTF binary, `+Y Up` off | Blender coordinates are then what KSA reads |
| Transforms applied | No node transform on a rendered mesh; apply location, rotation, scale |
| Node name equals mesh name | Purge orphan meshes before naming, or Blender silently appends `.001` |
| Materials exported | Never Placeholder or None; a primitive with no material breaks the renderable |
| Parts only | A `_VM` twin per subpart and `_ColPrim_*` collider helpers (KSAGolf ships no parts) |
| Units | Parts and course pieces in metres; character attachments in centimetres[^golf-modding-notes] |
| Textures | PNG; the PBR map is R = ambient occlusion, G = roughness, B = metalness, matching `<AoRoughMetal>`[^golf-model-readme] |

**Checkers.**

| Tool | Finds |
|---|---|
| `tools/model/checkmesh.py` | Triangles with zero UV area (NaN tangents, white speckle), exact coplanar overlaps from different faces (z-fighting), near-parallel faces within 0.3 to 4 mm, node and mesh names that do not pair, primitives with no material, missing `TEXCOORD_0`; `--compare` diffs two exports by canonicalised geometry and node transform because Blender permutes the index buffer; `--units-per-metre 100` scales thresholds for centimetre props; `--near-max 0` disables the proximity advisory for authored meshes[^golf-checkmesh] |
| `tools/validate-parts.py` | Every asset XML declared in `mod.toml`, case-exact texture and mesh paths, mesh ids present in their atlas, `SubPart`/material ids resolving locally or in Core, duplicate top-level ids across the mod, sized stacking connectors, shared planes between subparts placed as the XML places them; `--offline` skips Core lookups[^golf-validate-parts] |
| `tools/meshinfo.py` | Bounds of a named mesh in a glb, so sizes are facts[^golf-meshinfo] |
| `tools/model/dilate-atlas.py` | Fills texels outside UV islands from the nearest baked texel (masked by a key colour), so mipmaps never average in background[^golf-dilate] |
| `tools/model/preview.sh`, `smoketest.py` | Headless Blender render of an export from WSL; proof the Blender toolchain works[^golf-preview][^golf-smoketest] |

CI runs `checkmesh.py --near-max 0` over every glb in the release job, and `validate-parts.py
--offline` in both CI and `deploy.sh`.[^golf-release-yml]

## Lifecycle and timing

Authoring and CI time only. In game, the glb files are read when their `<GltfFile>` assets are
loaded (`startup:asset-load`, `<GltfFile>` maps to `Gltf2Reference` in the `AssetBundle` table) and
turned into GPU meshes when code first asks for them.[^ksa-assetbundle]

## Key code

| Path | Role |
|---|---|
| [.claude/skills/ksa-blender/SKILL.md](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/.claude/skills/ksa-blender/SKILL.md) | Authoring procedure and export contract |
| [tools/model/README.md](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/model/README.md) | Tool index, coordinate system, the two invisible defects |
| [tools/model/checkmesh.py](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/model/checkmesh.py) | Mesh defect checker |
| [tools/validate-parts.py](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/validate-parts.py) | Asset XML reference checker |
| [tools/model/dilate-atlas.py](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/model/dilate-atlas.py) | Atlas background fill |

## Snippets

```python
# tools/model/checkmesh.py L113-L138
def degenerate_uvs(gltf, binary, mesh):
    """Counts triangles whose UV area is zero, and the world area they cover.

    A zero-area UV triangle has no usable tangent frame. Renderers that build one from the UV
    derivative get a zero-length tangent; normalizing it yields NaN, and NaN survives being
    multiplied by zero, so even a flat normal map cannot save the shading. The symptom is
    flickering white speckle in game, invisible in any DCC preview.
    """
    bad = 0
    bad_area = 0.0
    total = 0
    missing = False

    for tri, uv in triangles(gltf, binary, mesh, with_uv=True):
        total += 1
        if uv is None:
            missing = True
            continue
        (u0, v0), (u1, v1), (u2, v2) = uv
        uv_area = abs((u1 - u0) * (v2 - v0) - (u2 - u0) * (v1 - v0)) / 2.0
        if uv_area < 1e-12:
            bad += 1
            _, _, area = plane_of(*tri)
            bad_area += area

    return bad, bad_area, total, missing
```

```python
# tools/model/checkmesh.py L659-L664
        # A <GltfFile> renderable indexes its primitives' materials: a primitive naming none throws
        # "Index was outside the bounds of the array" when the mod builds it, and nothing is drawn.
        # Blender writes none when exported with Materials set to Placeholder or None.
        unnamed = sum(1 for prim in mesh.get("primitives", []) if "material" not in prim)
        if unnamed:
            print(f"\n{name}: {unnamed} primitive(s) with NO MATERIAL — export with Materials: Export")
```

```python
# tools/validate-parts.py L43-L52
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

## Game APIs touched

| API | Decomp | Purpose |
|---|---|---|
| `AssetBundle` (`GltfFile`, `MeshAtlas`, `PbrMaterial`) | [current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs) | The XML elements whose ids and paths the validators check |

## Pitfalls and version notes

- Zero-UV-area faces and coplanar faces look identical in game (flickering white speckle) and
  neither shows in Blender's preview; diagnose with the checker, not by eye.[^golf-claude-md]
- `checkmesh.py` reads one mesh at a time; two bodies sharing a plane are only caught by
  `validate-parts.py`, which places subparts as the XML does. KSAGolf currently has no parts, so
  that check has nothing to compare.[^golf-validate-parts]
- An unchanged re-export shows as modified in git because Blender permutes triangles; use
  `checkmesh.py new.glb --compare old.glb` and revert if the geometry is the same.[^golf-model-readme]
- A hand-built Blender mesh is not selected (so `transform_apply` skips it), keeps whatever winding
  was listed, and a primitive operator already creates a UV layer.[^golf-model-readme]
- The contract and the Core counts were measured by the mod author against build 2026.9.22.5482's
  Core content; they were not re-measured for this page.

## See also

- [Prop and course asset XML](prop-and-course-asset-xml.md)
- [Mod meshes held by a kitten as cosmetic attachments](kitten-held-props.md)
- [Packaging, deploy and release pipeline](packaging-and-release.md)
- [Course surface from exact shapes](course-surface-exact-shapes.md)
- [Bake diffuse, normal, ORM and emissive maps from Blender and dilate the atlas](../../../howto/assets/models-and-textures/declare-gltf-and-pbr-material-assets/declare-gltf-and-pbr-material-assets-2.md) - how-to article demonstrating this feature
- [Verify the export contract against Core and keep a human sign-off before unwrapping](../../../howto/assets/models-and-textures/export-glb-from-blender-for-ksa/export-glb-from-blender-for-ksa-2.md) - how-to article demonstrating this feature
- [Find coplanar faces and zero-area UVs with checkmesh.py and read bounds with meshinfo.py](../../../howto/assets/validation/find-z-fighting-and-degenerate-uvs-in-glb/find-z-fighting-and-degenerate-uvs-in-glb-1.md) - how-to article demonstrating this feature
- [Check asset XML ids and paths offline with check-xml.sh and validate-parts.py](../../../howto/assets/validation/validate-asset-xml-references/validate-asset-xml-references-1.md) - how-to article demonstrating this feature

[^golf-blender-skill]: .claude/skills/ksa-blender/SKILL.md
[^golf-model-readme]: tools/model/README.md
[^golf-checkmesh]: tools/model/checkmesh.py
[^golf-dilate]: tools/model/dilate-atlas.py
[^golf-preview]: tools/model/preview.sh
[^golf-smoketest]: tools/model/smoketest.py
[^golf-meshinfo]: tools/meshinfo.py
[^golf-validate-parts]: tools/validate-parts.py
[^golf-release-yml]: .github/workflows/release.yml
[^golf-claude-md]: CLAUDE.md
[^golf-modding-notes]: docs/KSA-MODDING-NOTES.md
[^ksa-assetbundle]: KSA.AssetBundle (decompiled, build 2026.10.10.5554)
