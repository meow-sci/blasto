---
type: Mod Feature
title: Weapon parts as inert KSA parts split into an Assets file and a GameData file
description: Each weapon is a plain KSA part declared in two mod.toml-listed XML bundles (art and subparts in KSArmoryAssets.xml, mass, colliders, connectors, editor tags and real RCS thrusters in KSArmoryGameData.xml), with no behaviour in XML because the C# finds the part by Id
mod: ksarmory
category: assets
techniques: [xml-asset, glb-model, editor-tag-definition, subpart-gamedata-thrusters, assets-gamedata-split]
hooks: [mod.toml assets (KSA asset-bundle loader)]
lifecycle: [startup:asset-load]
game_types:
  - KSA.AssetBundle
  - KSA.PartTemplate
  - KSA.SubPartTemplate
  - KSA.PartGameDataReference
  - KSA.SubPartGameDataReference
  - KSA.EditorTagDefinition
  - KSA.MeshViewModule
  - KSA.VehicleEditor
  - KSA.CombustorTemplate
  - KSA.ModLibrary
  - KSA.GlbImport.PartAssetBundler
tags: [part-xml, glb, editor-tags, colliders, connectors, thrusters, subparts]
---

KSArmory adds fourteen parts (the Pantsir-S1 truck, missile and bomb rails, a B61 rack, a Phalanx,
a Mk 42 and an M197 mount, a MIRV bus, optical directors, a Litening pod and two AN/ALE-47
dispensers) and none of them carries behaviour in XML. KSA has no hook for a mod to register a part
module, so each part is declared as **inert structure** (mesh, mass, colliders, connectors, editor
tags) and the C# recognises it by its `Id`.[^gamedata-xml][^from-ksp-doc] The XML is still worth
studying because it records, in comments, every silent failure the author hit while making a
non-standard part behave in KSA's editor and physics.

## How it works

Two bundles, both listed explicitly in `mod.toml` `assets` (no globbing: an unlisted file is
silently never loaded), sitting at the mod root so relative mesh and texture paths resolve the
same whether the loader reads them against the mod folder or the XML's folder.[^mod-toml][^assets-xml]

| File | Elements used | Role |
|---|---|---|
| `KSArmoryAssets.xml` | `MeshAtlas`, `PbrMaterial` (`Diffuse`, `Normal`, `AoRoughMetal`, `Category="Vessel"`), `SubPart` (`PartModel` + `Mesh` + `Material`, `MeshView` with the `_VM` mesh), `Part` (`SubPart InstanceOf=...` with `Transform`, `Connector`) | Art and structure: 11 atlases, 14 materials, 14 parts[^assets-xml] |
| `KSArmoryGameData.xml` | `EditorTagDef`, `PartGameData` (`EditorTag`, `Diameter`, `Connector`/`Flags`, `Control`, `Battery`, `SolidSphereMass`, `Collider`/`Box`), `SubPartGameData` (`RocketThrusterController`, `Rocket`, `Combustor`, `DeLavalNozzle`), `Tank`, `ConsumerFeedWiring` | Simulation: mass, collision, attachment, editor placement, the MIRV bus's real thrusters[^gamedata-xml] |

All of these are element names `AssetBundle.Assets` maps to template types (`Part` ->
`PartTemplate`, `SubPart` -> `SubPartTemplate`, `PartGameData` -> `PartGameDataReference`,
`SubPartGameData` -> `SubPartGameDataReference`, `EditorTagDef` -> `EditorTagDefinition`, ...).[^ksa-assetbundle]
`ModLibrary.AttachGameData` later matches each `PartGameDataReference` to the `PartTemplate` with
the same `Id` and calls `ApplyGameData`, which merges subpart instances, editor tags, rockets,
controllers, feed wiring and the rest.[^ksa-modlibrary][^ksa-parttemplate]

### Techniques worth copying

| Problem | What the XML does |
|---|---|
| A tab of its own in the part picker | `<EditorTagDef Id="Weapons" RootPartWhitelist="true" FaceSnapTargetWhitelist="true" DiameterFilterlist="true" />` (and `Sensors`). The three flags are copied from Structural; dropping them silently loses root-part eligibility, face snapping and diameter filtering. The Id is generic on purpose so another weapons mod shares the tab[^gamedata-xml][^ksa-editortagdef] |
| A part that can be a craft's root | No `ToSurface`/`FromSurface` connector: `VehicleEditor.IsAllowedAsRootPart` requires a root-whitelisted tag and rejects the part if any connector has flag bit 2 (`ToSurface`) or 4 (`FromSurface`). Root-capable and radially attached are mutually exclusive[^gamedata-xml][^ksa-vehicleeditor][^ksa-part] |
| Stack vs radial | Stacking mounts (Phalanx, MIRV bus) use a plain `<Connector>`; rails, racks and dispensers use `<Flags>ToSurface</Flags>` plus the `Radial` and `NoFaceSnapping` tags[^gamedata-xml] |
| Seating on a tank node | The stacking connector's `Transform/Scale` is the node size; without it the mount sinks onto a tank's smaller internal node (8 cm into a 2 m tank)[^assets-xml] |
| A truck you can fly | `<Control />` and a `<Battery>` on the Pantsir; a rail or director deliberately has neither[^gamedata-xml] |
| A post-boost bus that steers after separation | Sixteen RCS nozzle subparts carry a `SubPartGameData` with a real `RocketThrusterController`, `Combustor` (MMH/NTO) and `DeLavalNozzle`; the part has its own spherical `Tank`, a `ConsumerFeedWiring` entry naming the tank, `<Control />` and a battery, so the thrusters answer to the bus, not the stack below[^gamedata-xml] |
| Finer RCS pulses | `MinimumPulseTime Seconds="0.001"`, the floor: `CombustorTemplate` clamps the value to at least `0.001` s at load[^gamedata-xml][^ksa-combustor] |
| Moving pieces | Every articulated assembly (turret, pods, guns, radar) and every round is its own `SubPart` instance with a pivot-recentred mesh, so the C# can rewrite its transform per frame[^assets-xml] |
| Collision | Hand-placed `Box` colliders in part space; authored GLBs also carry a `_ColPrim_` box, Core's convention, read by KSA's own GLB bundler (`GlbColliders`), and the XML copies it[^assets-xml][^ksa-glbcolliders] |
| View-model meshes | Each `SubPart` names `<Mesh>` and a `MeshView` `_VM` twin, the pairing KSA's own `PartAssetBundler` generates when a `<name>_VM` node exists[^assets-xml][^ksa-meshview][^ksa-partassetbundler] |

### Id hygiene

A `SubPart` Id must not equal a mesh Id: the atlas registers every glTF node name as a mesh Id, so
reusing it registers two assets under one key and the loader keeps the first. KSArmory names
subparts `KSArmory_<name>_SubPart` against meshes `KSArmory_Subpart_<name>`, and
`tools/validate-parts.py` fails the build if the namespaces touch. That script also resolves every
`InstanceOf`, `Material` and mesh Id against the mod's own files and Core's, and checks every
texture and atlas path exists.[^assets-xml][^validate-parts]

## Lifecycle and timing

- **`startup:asset-load`.** KSA reads both files from `mod.toml` `assets` inside
  `ModLibrary.LoadAll` (Program.cs calls `PrepareAll`, then `LoadAll`, then `AttachGameData`).
  No mod code runs; the C# only meets these parts later by `Id`.[^ksa-modlibrary]
- **`AttachGameData` after `StarMapAllModsLoaded`.** StarMap's all-mods-loaded hook is a postfix on
  `LoadAll`, so a mod reading a `PartTemplate` there sees the Assets definition but not yet the
  merged game data (editor tags, rockets, feed wiring).[^ksa-modlibrary]
- **Removal is not free at save load.** KSA pairs a saved part's subparts positionally with the
  current template, so removing a `<SubPart>` crashes saves holding that part; see
  [Startup save repair](./startup-save-repair.md).[^weapon-packs-doc]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/mod.toml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/mod.toml) | Explicit `assets` list |
| [src/KSArmory/KSArmoryAssets.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryAssets.xml#L1286-L1326) | Phalanx part: stacked connector, articulated subparts |
| [src/KSArmory/KSArmoryGameData.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryGameData.xml#L16-L76) | Editor tab definitions, root-capable Pantsir |
| [src/KSArmory/KSArmoryGameData.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryGameData.xml#L465-L582) | MIRV bus RCS thrusters, tank, feed wiring, control |
| [src/KSArmory/KSArmory.csproj](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmory.csproj) | Copies `KSArmory*.xml`, meshes and textures to the output |
| [tools/validate-parts.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/validate-parts.py) | Offline Id, mesh and path checks |

## Snippets

```xml
<!-- src/KSArmory/KSArmoryGameData.xml L31-L32 -->
  <EditorTagDef Id="Weapons" RootPartWhitelist="true" FaceSnapTargetWhitelist="true"
                DiameterFilterlist="true" />
```

```xml
<!-- src/KSArmory/KSArmoryAssets.xml L1286-L1326 -->
  <Part Id="KSArmory_Prefab_Ciws">

    <SubPart Id="KSArmory_Ciws_Base" InstanceOf="KSArmory_CiwsBase_SubPart" />

    <!--
      Traverses about the stack centreline. Its mesh is exported recentred on the pivot, so KSA
      spins it in place; the offset comes back here.
    -->
    <SubPart Id="KSArmory_Ciws_Turret" InstanceOf="KSArmory_CiwsTurret_SubPart">
      <Transform>
        <Position X="0.80000" Y="0.00000" Z="0.00000" />
      </Transform>
    </SubPart>
    <!-- ... -->
    <Connector Id="_ciwsConnectorBase">
      <Transform>
        <Position X="0.00000" />
        <Rotation X="3.14159" Z="3.14159" />
        <Scale X="3.00000" Y="3.00000" Z="3.00000" />
      </Transform>
    </Connector>

  </Part>
```

```xml
<!-- src/KSArmory/KSArmoryGameData.xml L479-L488 -->
  <SubPartGameData Id="KSArmory_MirvRcs_SubPart">
    <RocketThrusterController Id="MirvACS">
      <RocketReference Id="Thruster" />
    </RocketThrusterController>
    <Rocket Id="Thruster">
      <Core Id="Chamber" />
      <Nozzle Id="Nozzle" />
    </Rocket>
    <Combustor Id="Chamber">
      <FeedsFrom Parent="true" />
```

```csharp
// current/decomp/KSA/VehicleEditor.cs L1262-L1280
	private bool IsAllowedAsRootPart(PartTemplate part)
	{
		if (EditorTag.MatchAny(part.EditorTags.AsSpan(), _rootPartWhitelist.AsSpan()))
		{
			if (part.Connectors.Count == 0)
			{
				return false;
			}
			foreach (Part.Connector.TemplateBase connector in part.Connectors)
			{
				if (EnumEx.IsSet((int)connector.Flags, 4) || EnumEx.IsSet((int)connector.Flags, 2))
				{
					return false;
				}
			}
			return true;
		}
		return false;
	}
```

## Game APIs touched

| Member | Use |
|---|---|
| `AssetBundle.Assets` element map ([current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L15-L77)) | Which XML elements a bundle may contain |
| `ModLibrary.AttachGameData` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1863)) | Merges `PartGameData` into the matching `PartTemplate` |
| `PartTemplate.ApplyGameData` ([current/decomp/KSA/PartTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTemplate.cs#L265)) | What the merge copies |
| `SubPartTemplate` ([current/decomp/KSA/SubPartTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SubPartTemplate.cs)) | A `PartTemplate` subclass, so subparts can carry thrusters |
| `EditorTagDefinition.RootPartWhitelist` / `FaceSnapTargetWhitelist` / `DiameterFilterlist` ([current/decomp/KSA/EditorTagDefinition.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EditorTagDefinition.cs)) | Editor tab behaviour |
| `VehicleEditor.IsAllowedAsRootPart` ([current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L1262)) | Root-part rule |
| `Part.Connector` flags `ToSurface = 2`, `FromSurface = 4` ([current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L197)) | Radial attach |
| `CombustorTemplate.MINIMUM_PULSE_TIME` and load clamp ([current/decomp/KSA/CombustorTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CombustorTemplate.cs#L90)) | 1 ms pulse floor |
| `MeshViewModule` (`MeshView`) ([current/decomp/KSA/MeshViewModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshViewModule.cs)) | The `_VM` mesh reference |
| `PartAssetBundler` / `GlbColliders` ([current/decomp/KSA.GlbImport/PartAssetBundler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.GlbImport/PartAssetBundler.cs#L52)) | KSA's own GLB conventions: `_VM` twins, `_`-prefixed helper nodes, `_ColPrim_Box` colliders |

## Pitfalls and version notes

- **Every failure here is silent**: a part file left out of `assets`, a missing `InstanceOf`
  template (the comments record a session-ending "PartTemplate is null for 'KSArmory_Fins_SubPart'"),
  a tag missing its whitelist flags, a `Radial` tag (on the face-snap blacklist, which beats the
  whitelist) on a part you want to bolt things to.[^assets-xml][^gamedata-xml]
- **`FeedsFrom` alone does not wire a thruster**: the part also needs a `ConsumerFeedWiring` naming
  the tank, or KSA logs "no ConsumerFeedWiring wiring for it" and nothing fires.[^gamedata-xml]
- **Face snapping aligns the part's -Z**, the connector path its -X; a root part with +X up laid on
  a surface by face snapping ends up on its side, hence `NoFaceSnapping`.[^gamedata-xml]
- Gameplay numbers (blast radii) are inflated because KSA exposes only whole-part destruction, not
  damage.[^weapon-packs-doc]

## See also

- [Weapon registry and profiles](./weapon-registry-and-profiles.md)
- [Weapon packs from mod folders](./weapon-packs-from-mod-folders.md)
- [Articulated mount subparts](./articulated-mount-subparts.md)
- [Rounds as hidden launcher subparts](./rounds-as-hidden-launcher-subparts.md)
- [Countermeasure dispensers](./countermeasure-dispensers.md)
- [Blender model pipeline and checks](./blender-model-pipeline-and-checks.md)
- [MIRV bus trim and release](./mirv-bus-trim-and-release.md)
- [Read the AssetBundle element table against three real asset files](../../../howto/assets/asset-xml/asset-xml-element-reference/asset-xml-element-reference.md) - how-to article demonstrating this feature
- [Share one palette atlas across parts, add _VM preview twins and keep subpart ids apart](../../../howto/assets/models-and-textures/add-a-part-model-with-glb-and-pbr-textures/add-a-part-model-with-glb-and-pbr-textures-2.md) - how-to article demonstrating this feature
- [Split inert weapon parts into Assets and GameData with real RCS thrusters on subparts](../../../howto/vehicles-and-parts/parts/define-a-part-with-game-data/define-a-part-with-game-data-2.md) - how-to article demonstrating this feature
- [Evolve part XML with append-only subpart lists and fixed part Ids](../../../howto/vehicles-and-parts/parts/evolve-a-shipped-part-without-breaking-saves/evolve-a-shipped-part-without-breaking-saves-1.md) - how-to article demonstrating this feature

[^mod-toml]: src/KSArmory/mod.toml
[^assets-xml]: src/KSArmory/KSArmoryAssets.xml
[^gamedata-xml]: src/KSArmory/KSArmoryGameData.xml
[^validate-parts]: tools/validate-parts.py
[^weapon-packs-doc]: docs/WEAPON-PACKS.md
[^from-ksp-doc]: docs/FROM-KSP-MODDING.md
[^ksa-assetbundle]: KSA.AssetBundle (decompiled, build 2026.10.10.5554)
[^ksa-parttemplate]: KSA.PartTemplate (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-editortagdef]: KSA.EditorTagDefinition (decompiled, build 2026.10.10.5554)
[^ksa-vehicleeditor]: KSA.VehicleEditor (decompiled, build 2026.10.10.5554)
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-meshview]: KSA.MeshViewModule (decompiled, build 2026.10.10.5554)
[^ksa-combustor]: KSA.CombustorTemplate (decompiled, build 2026.10.10.5554)
[^ksa-partassetbundler]: KSA.GlbImport.PartAssetBundler (decompiled, build 2026.10.10.5554)
[^ksa-glbcolliders]: KSA.GlbImport.GlbColliders (decompiled, build 2026.10.10.5554)
