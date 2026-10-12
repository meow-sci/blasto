---
type: Game Feature
title: Defining a part with its own game data and GLB model
description: How KSA loads Assets and GameData XML bundles into PartTemplates, which XML elements a part accepts, and three mods' part definitions
category: vehicles-and-parts
group: parts
topic: define-a-part-with-game-data
demonstrated_by:
  - mods/ksacars/features/car-part-gamedata
  - mods/ksacars/features/car-part-assets-glb-pbr
  - mods/ksarmory/features/weapon-part-definitions-xml
  - mods/ksastructures/features/office-furniture-craft
lifecycle: [startup:asset-load, startup:all-mods-loaded]
game_types:
  - KSA.PartTemplate
  - KSA.PartGameDataReference
  - KSA.SubPartGameDataReference
  - KSA.EditorTagDefinition
  - KSA.ModLibrary
  - KSA.AssetBundle
  - KSA.XmlLoader
  - KSA.XmlHelper
  - KSA.Mod
tags: [xml, part, game-data, glb, editor-tag, collider, mass, connector]
---
A KSA part is defined by XML and nothing else: no code registers a part or a module. The XML is split in two
by convention, an **Assets** bundle (art and structure: meshes, materials, subparts, the `<Part>` itself) and a
**GameData** bundle (what the simulator needs: mass, colliders, connectors, editor tags, seats, controls,
thrusters). Both are plain `<Assets>` documents listed in `mod.toml`, and the game merges the second into the
first by Id after everything is loaded.

## Engine background

**Loading.** `mod.toml` has an `assets = [ ... ]` string array (a `TomlField("assets")` on `Mod`)
[current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L66-L67)[^ksa-mod]. `Mod.LoadAssetBundles` calls `XmlLoader.Load<AssetBundle>(Path.Combine(DirectoryPath,
path), mod)` for each entry and registers the bundle [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L157-L169). The files are listed one by one;
a file not in the list is never loaded and nothing reports it. `XmlLoader.TryDeserialize` catches any XML
exception and logs it, so a malformed file yields `null` and only a log line [current/decomp/KSA/XmlLoader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlLoader.cs#L1-L53)[^ksa-xmlloader].

**The bundle.** The XML root is `<Assets>` (`[XmlRoot("Assets")] AssetBundle`), whose `[XmlElement]` list maps
element names to types: `Part` -> `PartTemplate`, `SubPart` -> `SubPartTemplate`, `PartGameData` ->
`PartGameDataReference`, `SubPartGameData` -> `SubPartGameDataReference`, `EditorTagDef` ->
`EditorTagDefinition`, `MeshAtlas`, `PbrMaterial`, `GltfFile`, `Texture` and many more
[current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L8-L77)[^ksa-assetbundle].

**Order.** `Program` runs `ModLibrary.LoadEditorTags()` (each mod's `LoadEditorTagDefinitions`, which
deserialises every bundle once just to register `EditorTagDef`s) and then `ModLibrary.LoadAll()` (which loads
the bundles for real), and much later, after the renderer is bound, `ModLibrary.AttachGameData()`
[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L935-L990)[^ksa-program] [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L171-L190). `AttachGameData` matches each `PartGameDataReference` to the
`PartTemplate` with the same Id and calls `PartTemplate.ApplyGameData`
[current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1863-L1876)[^ksa-modlibrary]. StarMap's all-mods-loaded hook is a postfix on `LoadAll`, so a mod reading a
`PartTemplate` there sees the Assets definition but not the merged game data (see
[game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)).

**What a part accepts.** `PartTemplate` is the schema for `<Part>`, `<SubPart>`, `<PartGameData>` and
`<SubPartGameData>` ([current/decomp/KSA/PartTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTemplate.cs#L1-L110)[^ksa-parttemplate]):

| Element | Type or meaning |
|---|---|
| attributes `DisplayName`, `CrashTolerance` | name; per-part crash pressure in pascals (NaN derives) |
| `SubPart` (repeated) | a `PartInstance`: `InstanceOf`, `Transform`, `Stage`, `Sequence`... |
| `EditorTag Value=` | editor category tag(s) |
| `Connector` | `Id`, `Transform`, `Flags`, `Capabilities`, `Sibling` [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L201-L232)[^ksa-part] |
| `Diameter`, `Grab`, `Aligned`, `SymmetryGroup`, `SubPartGroup`, `Thumbnail` | editor and picking data |
| `Control`, `EVADoor`, `DockingPort`, `PrimarySequenceModule` | command source, crew door, docking, staging |
| mass: `CustomMass`, `SolidSphereMass`, `HollowSphereMass`, `SolidCylinderMass`, `SolidConeMass`, `SolidCuboidMass`, `HollowCuboidMass`, and the other hollow, ogive and truncated-cone variants | one or more inert mass shapes |
| `Battery`, `Generator`, `SolarPanel`, `SolarTracker`, `PowerConsumer`, `MagneticScoop` | power modules |
| `RocketEngineController`, `RocketThrusterController`, `Rocket`, `Combustor`, `SolidMotor`, `ThermalCore`, `AntimatterRamCore`, `DeLavalNozzle`, `MagneticNozzle`, `SolidMotorNozzle`, `ConsumerFeedWiring` | engines and RCS |
| any module template, by its `[XmlType]` name | the `Components` list: `Collider`, `IVASeat`, `Tank`, `Light`, `AttachedInternal`, `PartModel`, `PartModelGlass`, `PartModelDynamic`, `MeshView`, `Decoupler`, `FuelPort`, `Parachute`, `SolidGrainSegment`, `KeyframeAnimationModule` |

The last row is not hard-coded on `PartTemplate`: `XmlHelper`'s static constructor reflects every
`ModuleBase.TemplateDataBase` subclass in the game assembly and registers it under `PartTemplate.Components`
by its `[XmlType]` name [current/decomp/KSA/XmlHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlHelper.cs#L1-L45)[^ksa-xmlhelper]. A mod can use every stock module in XML but cannot add a new
module element without patching the serializer.

**Merge rules.** `ApplyGameData` appends editor tags, rocket parts, masses, power modules and component
templates; merges a connector that has the same Id by OR-ing its `Flags` and `Capabilities`; replaces
`DockingPort`, `EVADoor`, `Control`, `PrimarySequenceModule` and `Thumbnail` when the game data has one; copies
`DisplayName` if non-empty; and merges `SubPart` instances by Id [current/decomp/KSA/PartTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTemplate.cs#L265-L351).

## Approaches

| Article | Mod | What it shows |
|---|---|---|
| [1: one part per car](define-a-part-with-game-data-1.md) | KSACars | colliders clear of the ground, mass-shape choice, a new editor category, GLB/PBR assets |
| [2: weapons split in two bundles](define-a-part-with-game-data-2.md) | KSArmory | Assets vs GameData split, real RCS thrusters on subparts, inert parts found by Id |
| [3: furniture as minimal game data](define-a-part-with-game-data-3.md) | KSAStructures | the smallest part that doubles as a craft, hidden door subpart |

## See also

- [Ship whole-vehicle presets and single-part craft with a part mod](../ship-ready-made-craft/overview.md), [Add crew seats and EVA doors to a part](../add-crew-seats-and-eva-doors/overview.md), [Define articulated subparts and mounts, animate and hide them from code](../define-and-animate-subparts/overview.md), [Change a shipped part without breaking saves](../evolve-a-shipped-part-without-breaking-saves/overview.md), [Load, validate and unload part mods in a running game](../load-and-unload-part-mods-at-runtime/overview.md)
- Mod features: [ksacars/car-part-gamedata](../../../../mods/ksacars/features/car-part-gamedata.md), [ksacars/car-part-assets-glb-pbr](../../../../mods/ksacars/features/car-part-assets-glb-pbr.md), [ksarmory/weapon-part-definitions-xml](../../../../mods/ksarmory/features/weapon-part-definitions-xml.md), [ksastructures/office-furniture-craft](../../../../mods/ksastructures/features/office-furniture-craft.md)
- Stock examples: [current/Content/Core/CoreCommandAGameData.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/CoreCommandAGameData.xml#L1-L30)[^content-corecommandagamedata] and [current/Content/Core/CoreEditorTagsGameData.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/CoreEditorTagsGameData.xml#L1-L24)[^content-coreeditortagsgamedata]

[^content-corecommandagamedata]: current/Content/Core/CoreCommandAGameData.xml (game content, build 2026.10.10.5554)
[^ksa-modlibrary]: current/decomp/KSA/ModLibrary.cs (decompiled, build 2026.10.10.5554)
[^ksa-xmlhelper]: current/decomp/KSA/XmlHelper.cs (decompiled, build 2026.10.10.5554)
[^ksa-program]: current/decomp/KSA/Program.cs (decompiled, build 2026.10.10.5554)
[^ksa-parttemplate]: current/decomp/KSA/PartTemplate.cs (decompiled, build 2026.10.10.5554)
[^ksa-mod]: current/decomp/KSA/Mod.cs (decompiled, build 2026.10.10.5554)
[^ksa-part]: current/decomp/KSA/Part.cs (decompiled, build 2026.10.10.5554)
[^ksa-xmlloader]: current/decomp/KSA/XmlLoader.cs (decompiled, build 2026.10.10.5554)
[^ksa-assetbundle]: current/decomp/KSA/AssetBundle.cs (decompiled, build 2026.10.10.5554)
[^content-coreeditortagsgamedata]: current/Content/Core/CoreEditorTagsGameData.xml (game content, build 2026.10.10.5554)
