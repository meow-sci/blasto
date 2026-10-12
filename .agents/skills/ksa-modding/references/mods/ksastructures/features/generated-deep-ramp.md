---
type: Mod Feature
title: A generated spiral ramp - mesh, shadow caster, box colliders and lamps from one script
description: Generates the deep spiral ramp and hall from a numpy and Pillow script that writes the GLB atlas, an invisible shadow-caster GLB, flat-colour textures, the static sub-objects and stepped box colliders spliced into the asset XML between markers, and the lamp table as C#
mod: ksastructures
category: assets
techniques: [glb-model, xml-asset, code-generation]
hooks: []
lifecycle: [startup:asset-load]
game_types: [KSA.StaticSubObjectTemplate, KSA.StaticObject, KSA.PartModelModule]
tags: [procedural-mesh, glb, gltf, colliders, static-object, code-generation, python, numpy]
---

The deep spiral ramp (a square road winding seven turns down a 54 m shaft into an underground hall)
is not modelled in Blender. `tools/model/office-deep.py` lays it out from a dozen constants and
writes everything the game and the mod need to agree on: the visible mesh, an invisible shadow
caster, textures, static sub-objects with their colliders, the lamp list and a JSON layout. It needs
numpy and Pillow, not Blender.[^mod-gen][^mod-claude]

## How it works

1. **Layout as constants.** Shaft and core rectangles, `TURN = 8.0` m per turn, `LEG = 2.0` m per
   leg, `DEPTH = 54.0`, `TURNS = 7`, the hall's faces and height, the door, the cover height, and one
   circular pit (`PITS`) for the [terrain decal](./terrain-decal-pits.md). Frame: x up, y east, z north,
   the building's own.[^mod-gen]
2. **Solids feed three outputs at once.** `solid()` adds a box to the drawn mesh (inflated by a
   per-body "skin" of a few millimetres so two bodies meeting on one plane do not z-fight), appends
   an axis-aligned collider box, and adds an inset box to the shadow-caster mesh.[^mod-gen]
3. **Ramps become stairs for physics.** KSAStructures gives its static objects box colliders only (the engine's `ColliderModule.Template` also accepts capsule, cylinder, sphere, convex-hull and mesh colliders, see `KSA/ConvexHullColliderTemplate.cs` and `MeshColliderTemplate.cs`), so each sloped leg is
   drawn as one prism but collides as 25 stepped boxes; the generated XML holds 730 colliders,
   per the mod's notes.[^mod-gen][^mod-claude] The engine builds every collider of a static object
   into one Bepu `BigCompound` when it resolves the object.[^ksa-staticobject]
4. **Lamps are computed with the geometry**: one under whatever is over each landing and leg, and a
   grid in the hall, with range and intensity from the clear height.[^mod-gen]
5. **A minimal glTF writer.** `glb()` packs positions, normals, UVs and indices into one binary
   buffer, writes one node and mesh per named mesh (`KSAOffice_Deep`, `KSAOffice_DeepTop`,
   `KSAOffice_DeepCaster`), pads both chunks to four bytes and writes the GLB header by hand. Each
   quad gets its own corner of a 4x4 swatch atlas, so every triangle has UV area for the engine's
   tangent frame.[^mod-gen]
6. **Textures as swatches.** A 64x64 sRGB-encoded diffuse with sixteen flat colours, and flat
   normal and AO/roughness/metal maps, each with its own file because the mod has seen KSA give a
   shared texture path to only one material.[^mod-gen]
7. **XML spliced between markers.** The `PbrMaterial`, a `StaticSubObject KSAOffice_DeepBody`
   (mesh, material, collider boxes) and `KSAOffice_DeepGround` (the cover, with
   `<Terrain>true</Terrain>`) replace whatever lies between `<!-- office-deep.py: begin -->` and
   `<!-- office-deep.py: end -->` in `KSAStructuresOffice.xml`; the tower's `StaticObject` instances
   them.[^mod-gen][^mod-office-xml]
8. **Code and data for the mod.** It writes `Sim/OfficeDeepLayout.cs` (a `partial` of `OfficeDeep`
   holding the lamp tuples the [garage lights](./garage-spot-lights.md) submit) and
   `tools/model/office-deep.json`.[^mod-layout][^mod-genjson]
9. **What it does not regenerate.** The constants in `Sim/OfficeDeep.cs` (used by the
   [terrain height override](./terrain-height-roof-override.md)), the deep shaft and hall bounds in
   `KSAStructuresDark.comp`, and the garage's own lamps in both `OfficeUnderground.cs` and the shader
   are restated by hand and have to move with it.[^mod-claude][^mod-deep][^mod-shader][^mod-underground]

## Lifecycle and timing

Build time only. The outputs load as ordinary assets at `startup:asset-load` (the XML is listed in
`mod.toml`), and the C# lamp table is compiled into the mod.

## Key code

| Path | Role |
|---|---|
| [tools/model/office-deep.py](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/model/office-deep.py#L21-L56) | Layout constants and colours |
| [tools/model/office-deep.py](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/model/office-deep.py#L118-L132) | `skin` and `solid`: mesh, collider, caster |
| [tools/model/office-deep.py](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/model/office-deep.py#L144-L185) | Turns, landings, legs, stepped colliders, lamps |
| [tools/model/office-deep.py](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/model/office-deep.py#L230-L280) | `glb()` writer and the two GLB outputs |
| [tools/model/office-deep.py](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/model/office-deep.py#L295-L346) | XML splice, JSON and C# output |
| [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml#L37-L438) | The generated block |
| [src/KSAStructures/Sim/OfficeDeepLayout.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Sim/OfficeDeepLayout.cs) | Generated lamp table |

## Snippets

One call, three outputs:

```python
# tools/model/office-deep.py L118-L132
def skin():
    """A few millimetres, different for every body drawn: two bodies meeting on one plane z-fight."""
    global drawn
    drawn += 1
    return 0.003 + (drawn * 7 % 23) * 0.0009


def solid(name, x0, x1, ya, yb, za, zb, swatch, top_swatch=None, collide=True):
    e = skin()
    structure.box(x0 - e, x1 + e, ya - e, yb + e, za - e, zb + e, SWATCH[swatch], None if top_swatch is None else SWATCH[top_swatch])
    if collide:
        boxes.append((name, x0, x1, ya, yb, za, zb))
    if min(x1 - x0, yb - ya, zb - za) > 3 * INSET:
        caster.box(x0 + INSET, x1 - INSET, ya + INSET, yb - INSET, za + INSET, zb - INSET, 0)

```

A sloped leg collides as steps:

```python
# tools/model/office-deep.py L164-L175
        # Colliders in steps, since a collider is a box: 8 cm a step.
        steps = 25
        along_y = name in ("N", "S")
        a, b = (rect[0], rect[1]) if along_y else (rect[2], rect[3])
        first, last = (top[0], top[1]) if along_y else (top[0], top[3])
        for s in range(steps):
            f0, f1 = s / steps, (s + 1) / steps
            level = first + (last - first) * (f0 + f1) / 2
            ya, yb = (a + (b - a) * f0, a + (b - a) * f1) if along_y else (rect[0], rect[1])
            za, zb = (rect[2], rect[3]) if along_y else (a + (b - a) * f0, a + (b - a) * f1)
            boxes.append((f"Leg{name}{k}_{s}", level - SLAB, level, ya, yb, za, zb))
            n += 1
```

Splicing the generated XML between markers:

```python
# tools/model/office-deep.py L319-L325
xml_path = ROOT / "KSAStructuresOffice.xml"
xml = xml_path.read_text()
begin, end = "  <!-- office-deep.py: begin -->\n", "  <!-- office-deep.py: end -->\n"
if begin not in xml:
    sys.exit("KSAStructuresOffice.xml has no office-deep.py markers")
xml = xml[:xml.index(begin) + len(begin)] + "\n".join(lines) + "\n" + xml[xml.index(end):]
xml_path.write_text(xml)
```

## Game APIs touched

| API | Decomp | Use |
|---|---|---|
| `StaticObject.Resolve` / `BuildCollisionShape` (private) | [current/decomp/KSA/StaticObject.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObject.cs#L110-L200) | All sub-object colliders become one compound |

## Pitfalls and version notes

- **This mod uses box colliders for its static objects**, so slopes need steps; keep step height under what a kitten
  or a wheel can climb (here about 8 cm per step, per the script's comment).
- **Generated and hand-restated layouts drift.** The script prints its triangle, collider and lamp
  counts so a change is visible; the shader and `OfficeDeep.cs` have to be updated by hand.
- **Do not hand-edit between the markers**; the next run overwrites it.

## See also

- [Terrain decal pits](./terrain-decal-pits.md) - the pit the ramp sits in
- [Invisible shadow casters](./invisible-shadow-casters.md) - what the caster GLB is for
- [Asset and mesh validation tools](./asset-and-mesh-validation-tools.md) - checks over the generated files
- [Launch-pad landmarks](./office-landmarks-static-objects.md) - the static object instancing these sub-objects
- [Emit flat-colour textures from the same script that generates the geometry](../../../howto/assets/models-and-textures/declare-gltf-and-pbr-material-assets/declare-gltf-and-pbr-material-assets-4.md) - how-to article demonstrating this feature
- [Write the GLB, textures, collider XML and C# table from one Python script (no Blender)](../../../howto/assets/models-and-textures/export-glb-from-blender-for-ksa/export-glb-from-blender-for-ksa-4.md) - how-to article demonstrating this feature
- [Edit shadow copies with anchor-verified text patches and generated XML between markers](../../../howto/assets/xml-patching/redirect-game-xml-loading/redirect-game-xml-loading-3.md) - how-to article demonstrating this feature
- [Levelling decal plus additive pit height map, ordered after the stock pad decal](../../../howto/celestials/terrain/add-terrain-decals-and-dig-a-site/add-terrain-decals-and-dig-a-site-2.md) - how-to article demonstrating this feature

[^mod-gen]: tools/model/office-deep.py
[^mod-genjson]: tools/model/office-deep.json
[^mod-layout]: src/KSAStructures/Sim/OfficeDeepLayout.cs
[^mod-deep]: src/KSAStructures/Sim/OfficeDeep.cs
[^mod-office-xml]: src/KSAStructures/KSAStructuresOffice.xml
[^mod-underground]: src/KSAStructures/Ksa/OfficeUnderground.cs
[^mod-shader]: src/KSAStructures/Shaders/KSAStructuresDark.comp
[^mod-claude]: KSAStructures CLAUDE.md (regenerating the deep ramp)
[^ksa-staticobject]: KSA.StaticObject (decompiled, build 2026.10.10.5554)
