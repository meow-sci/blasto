---
type: Mod Feature
title: Art and audio pipeline tooling
description: Offline checks and generators for a mod's authored assets, covering GLB bounds from accessor min/max, coplanar-face and zero-UV detection, atlas dilation, Blender previews, XML Id and path validation against Core, and engine loops cut from CC0 recordings with their provenance
mod: ksacars
category: assets
techniques: [glb-model, xml-asset]
hooks: []
lifecycle: []
game_types: [KSA.MeshAtlasFileReference]
tags: [tooling, glb, gltf, blender, mcp, textures, mipmaps, z-fighting, audio, python, validation]
---

Every asset problem in KSA fails quietly: a mistyped mesh Id renders nothing, a missing texture
renders untextured, a malformed XML file means the part simply does not exist, two coplanar faces
speckle in game but not in Blender's preview, and an unfilled atlas background bleeds into islands
only at a distance. KSACars answers each with a small offline tool, most of them run by `deploy.sh`
and by CI's `check-all.sh`.[^ksacars-checkall] The car art is authored in Blender over MCP, the
`.blend` sources live outside the repository, and the checkers are "the only gate on art nobody can
rebuild from a clean checkout".[^ksacars-model-readme]

## How it works

**Models**

| Tool | What it does |
|---|---|
| `tools/meshinfo.py <atlas.glb> [filter]` | Lists every mesh in a GLB with its bounding box, read from the glTF accessors' `min`/`max` without decoding vertices; for laying parts out against Core's atlases[^ksacars-meshinfo] |
| `tools/model/checkmesh.py <glb> [--near-max 0] [--compare b.glb]` | Reduces every triangle to a quantised plane and flags overlapping faces on the same plane from different directions (z-fighting), zero-UV-area triangles and unpaired node/mesh names; `--compare` diffs two exports by geometry; exits non-zero on conflicts[^ksacars-checkmesh] |
| `tools/model/dilate-atlas.py --key 0,255,0 <png...>` | Fills the keyed background around baked islands from the nearest baked texel, so lower mips never average an island with its neighbour (a wide bake margin instead overwrote 12.95 % of a neighbouring island)[^ksacars-dilate] |
| `tools/model/preview-glb.py`, `preview.sh` | Renders a GLB from several angles in headless Blender, "node transforms and all", so an export can be judged before it is declared; `preview.sh` converts paths for the Windows Blender binary under WSL[^ksacars-preview][^ksacars-preview-sh] |
| `tools/model/split-lenses.py`, `lens-emissive.py` | Lens subparts and emissive masks; see [coloured emissive lenses](./coloured-emissive-lenses.md) |

**The export contract** (`.claude/skills/ksa-blender/SKILL.md`): part space +X up, +Y forward, +Z the
car's left, written with Blender's "+Y Up" off; no node transform on a rendered subpart (translation,
rotation and scale baked into the vertices); node name equal to mesh name; a `_VM` twin of every
subpart for the editor preview; each moving body recentred on its pivot; plus baking recipes (diffuse
without lighting, metalness routed through emission, `use_clear` overriding the scene setting).[^ksacars-blender-skill]
The engine side of that contract is verifiable in the decomp: `MeshAtlasFileReference.DoLoad`
registers each glTF *mesh* by its name and never reads node transforms, and an Id already registered
(by Core or another mod) is reused rather than loaded.[^ksa-meshatlas]

**XML**

| Tool | What it does |
|---|---|
| `tools/check-xml.sh` | Parses every shipped asset XML; KSA reports a malformed file only as a missing part[^ksacars-checkxml] |
| `tools/validate-parts.py [--offline]` | Checks that every `SubPart InstanceOf` and `Material Id` resolves against the mod's own XML or Core's library, every `Mesh Id` exists in a declared atlas (the check "that earns its keep", since names come out of Blender), and every atlas and texture path exists with the exact case; `--offline` checks only the mod's own files[^ksacars-validateparts] |

**Audio.** `tools/buggy-sounds.py` and `tools/eldorado-sounds.py` cut four mono WAVs per car (start,
idle loop, load loop, stop) from recordings kept in `tools/audio/`: they find the steadiest window
by tracking the strongest engine line, cut loops where the waveform returns to its start, crossfade
the seam (20 ms) and normalise peaks (NumPy and SciPy `resample_poly`).[^ksacars-buggysounds][^ksacars-eldosounds]
`tools/audio/README.md` records each recording's freesound id and CC0 licence, and the RPM each loop
was recorded at (for example the Cadillac's 95 Hz fast idle, about 1400 rpm), which the profile's
`IdleRecordedRpm`/`LoadRecordedRpm` use to re-pitch the loops (see [engine sound](./engine-sound-crossfade.md)).[^ksacars-audio-readme]

## Lifecycle and timing

Development time only. `deploy.sh` runs `validate-parts.py --offline` before copying files;
`check-all.sh` (pre-push hook and CI) runs `check-xml.sh`, `validate-parts.py --offline` and
`checkmesh.py --near-max 0` over every authored atlas, globbed so a new car cannot ship unchecked.

## Key code

| Path | Role |
|---|---|
| [tools/meshinfo.py](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/meshinfo.py) | GLB mesh bounds |
| [tools/model/checkmesh.py](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/model/checkmesh.py) | Z-fighting and UV defect finder |
| [tools/model/dilate-atlas.py](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/model/dilate-atlas.py) | Atlas background fill for mipmaps |
| [tools/model/preview-glb.py](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/model/preview-glb.py) | Headless Blender preview |
| [tools/validate-parts.py](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/validate-parts.py) | Ids and paths against files and Core |
| [tools/check-xml.sh](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/check-xml.sh) | XML well-formedness |
| [tools/buggy-sounds.py](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/buggy-sounds.py), [tools/eldorado-sounds.py](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/eldorado-sounds.py) | Engine loops cut from recordings |
| [tools/audio/README.md](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/audio/README.md) | Provenance and recorded RPMs |
| [.claude/skills/ksa-blender/SKILL.md](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/.claude/skills/ksa-blender/SKILL.md) | Authoring over MCP and the export contract |

## Snippets

```python
# tools/meshinfo.py L1-L11
#!/usr/bin/env python3
"""
Prints the meshes inside a KSA mesh atlas (.glb) with their bounding boxes.

KSA parts are assembled by referencing named sub-meshes out of a shared atlas and placing
them with <Transform> offsets, so you need real dimensions to lay a part out. glTF stores
per-accessor min/max for POSITION, which gives exact bounds without decoding any vertices.

    ./tools/meshinfo.py "/mnt/c/Program Files/Kitten Space Agency/Content/Core/Meshes/CoreStructuralA_MeshAtlas.glb"
    ./tools/meshinfo.py <atlas.glb> Tube      # only meshes whose name contains "Tube"
"""
```

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

## Game APIs touched

None at runtime. The tools read GLB, PNG, WAV and XML files and Core's content folder; the one
engine behaviour they encode is the atlas loader's naming and registration rule
(`MeshAtlasFileReference.DoLoad` ([current/decomp/KSA/MeshAtlasFileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshAtlasFileReference.cs#L26-L50))).

## Pitfalls and version notes

- **Coplanar faces do not show in Blender's preview**; only a geometric check or the game reveals them.
- **Mipmaps expose atlas backgrounds**; fill them rather than widening the bake margin.
- **Mesh renames break XML silently**; validate Ids against the atlas, not just the XML.
- **Case matters on Linux**; `validate-parts.py --offline` runs on Linux CI against the real
  directory listing for that reason.
- **Keep sound provenance with the sources**, filenames as freesound produced them; the shipped
  `.wav` files are mono because the engine spatialises the source.
- The tools assume WSL paths for Blender and the KSA install (`KSA_DIR` overrides the latter).

## See also

- [Car part assets: GLB, PBR materials and subparts](./car-part-assets-glb-pbr.md)
- [Coloured emissive lenses](./coloured-emissive-lenses.md)
- [Engine sound crossfade](./engine-sound-crossfade.md)
- [Package, deploy and release](./package-deploy-release.md)
- [Headless simulation tests](./headless-sim-tests.md)
- [Bake diffuse, normal, ORM and emissive maps from Blender and dilate the atlas](../../../howto/assets/models-and-textures/declare-gltf-and-pbr-material-assets/declare-gltf-and-pbr-material-assets-2.md) - how-to article demonstrating this feature
- [Export a part-space GLB with pivot-centred subparts and preview it headless](../../../howto/assets/models-and-textures/export-glb-from-blender-for-ksa/export-glb-from-blender-for-ksa-1.md) - how-to article demonstrating this feature
- [Find coplanar faces and zero-area UVs with checkmesh.py and read bounds with meshinfo.py](../../../howto/assets/validation/find-z-fighting-and-degenerate-uvs-in-glb/find-z-fighting-and-degenerate-uvs-in-glb-1.md) - how-to article demonstrating this feature
- [Check asset XML ids and paths offline with check-xml.sh and validate-parts.py](../../../howto/assets/validation/validate-asset-xml-references/validate-asset-xml-references-1.md) - how-to article demonstrating this feature
- [Crossfade an idle loop and a load loop by RPM, with loops cut from recordings](../../../howto/audio/playback/play-a-spin-up-loop-tail-sound-and-crossfade-engine-loops/play-a-spin-up-loop-tail-sound-and-crossfade-engine-loops-1.md) - how-to article demonstrating this feature

[^ksacars-meshinfo]: tools/meshinfo.py
[^ksacars-checkmesh]: tools/model/checkmesh.py
[^ksacars-dilate]: tools/model/dilate-atlas.py
[^ksacars-preview]: tools/model/preview-glb.py
[^ksacars-preview-sh]: tools/model/preview.sh
[^ksacars-model-readme]: tools/model/README.md
[^ksacars-validateparts]: tools/validate-parts.py
[^ksacars-checkxml]: tools/check-xml.sh
[^ksacars-buggysounds]: tools/buggy-sounds.py
[^ksacars-eldosounds]: tools/eldorado-sounds.py
[^ksacars-audio-readme]: tools/audio/README.md
[^ksacars-blender-skill]: .claude/skills/ksa-blender/SKILL.md
[^ksacars-checkall]: tools/check-all.sh
[^ksa-meshatlas]: KSA.MeshAtlasFileReference (decompiled)
