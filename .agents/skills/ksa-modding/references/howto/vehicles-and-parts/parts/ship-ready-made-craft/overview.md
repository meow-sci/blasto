---
type: Game Feature
title: Shipping whole-vehicle presets and single-part craft with a part mod
description: How KSA's two vehicle libraries store craft as VehicleSaveData files, and how to make a single part a complete craft that players can launch
category: vehicles-and-parts
group: parts
topic: ship-ready-made-craft
demonstrated_by:
  - mods/ksacars/features/vehicle-library-presets
  - mods/ksacars/features/car-part-gamedata
  - mods/ksastructures/features/office-furniture-craft
lifecycle: [startup:asset-load]
game_types:
  - KSA.VehicleSave
  - KSA.VehicleSaves
  - KSA.DefaultVehicleSaves
  - KSA.PartTemplate
  - KSA.VehicleEditor
  - KSA.EditorTagDefinition
  - KSA.VehicleSaveData
  - KSA.SaveMetaData
tags: [vehicle-library, craft, preset, root-part, vehicle-save, meta-toml]
---
A craft is not an asset type: KSA stores it as a folder with a `meta.toml` and a `vehicle.xml`
(`VehicleSaveData`), and lists those folders in a library. A part mod therefore has two separate jobs when it
wants players to fly something: make the part *eligible* to be a craft (root-part rules), and *put a craft file
where the game looks*.

## Engine background

- **Two libraries.** `VehicleSaves.SaveFolderPath` is `<Documents>/Vehicles`, with `Constants.DocumentsFolderPath`
  = `My Games/Kitten Space Agency` under the user's Documents [current/decomp/KSA/VehicleSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleSaves.cs#L167)[^ksa-vehiclesaves]
  [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281)[^ksa-constants]. `DefaultVehicleSaves.SaveFolderPath` is `Content/Core/defaultvehicles`, relative to the
  game folder [current/decomp/KSA/DefaultVehicleSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DefaultVehicleSaves.cs#L24)[^ksa-defaultvehiclesaves]. Both scan their folder on startup (`Program` calls
  `VehicleSaves.OnApplicationStart()` and `DefaultVehicleSaves.OnApplicationStart()` before mods load)
  [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L783-L784)[^ksa-program], and `Refresh()` rescans [current/decomp/KSA/VehicleSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleSaves.cs#L280-L290).
- **Per-craft folder.** `UncompressedVehicleSave.FromDirectory` requires `meta.toml` and `vehicle.xml`; a folder
  without the XML is logged and skipped. `meta.toml` fields: `name`, `created`, `updated`, `version`, `systems`
  [current/decomp/KSA/UncompressedVehicleSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedVehicleSave.cs#L242-L279)[^ksa-uncompressedvehiclesave] [current/decomp/KSA/SaveMetaData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SaveMetaData.cs#L10-L30)[^ksa-savemetadata].
- **`vehicle.xml`** is `<VehicleSaveData Id ActiveSequence [Character]>` with one `<RootPartRef InstanceOf
  LocalInstanceId Stage>` (a `PartInstance`, the same type part XML uses) and optional `SequenceEnvironment` and
  `FuelLink` [current/decomp/KSA/VehicleSaveData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleSaveData.cs#L8-L27)[^ksa-vehiclesavedata]. Stock examples live in `Content/Core/defaultvehicles/*`:
  [current/Content/Core/defaultvehicles/Rocket/meta.toml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/defaultvehicles/Rocket/meta.toml#L1-L5)[^content-meta].
- **Who reads which.** The launch menu lists `VehicleSaves.AsSpan()` (the user library)
  [current/decomp/KSA/VehicleLaunchMenu.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleLaunchMenu.cs#L39-L45)[^ksa-vehiclelaunchmenu]; `VehicleTemplate.OnDataLoad` looks a vehicle asset's craft up in
  `DefaultVehicleSaves` by Id [current/decomp/KSA/VehicleTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleTemplate.cs#L56-L90)[^ksa-vehicletemplate].
- **Root-part rule.** A part may start a craft only if a tag has `RootPartWhitelist`, it has at least one
  connector, and no connector carries `ToSurface` (2) or `FromSurface` (4) [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L1262-L1280)[^ksa-vehicleeditor];
  the whitelist is built from `EditorTagDef`s at load [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L7086-L7100). In the picker the part
  is greyed out (not hidden) while the editor is empty [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L337-L341).

## Approaches

| Article | Mod | Subject |
|---|---|---|
| [1: preset files beside the mod](ship-ready-made-craft-1.md) | KSACars | the `meta.toml` + `vehicle.xml` pair, where they go, and an install-time or startup writer |
| [2: `RootPartWhitelist` on a car part](ship-ready-made-craft-2.md) | KSACars | what makes a single part a complete, flyable craft |
| [3: a furniture part as a one-part craft](ship-ready-made-craft-3.md) | KSAStructures | the same rules for props never meant to be built upon |

## See also

- [Spawn a vehicle from code (from the vehicle library or a hand-built part)](../../vehicles/spawn-a-vehicle-at-runtime/overview.md) (loading these craft from code), [Define a part with its own game data and GLB model](../define-a-part-with-game-data/overview.md), [Change a shipped part without breaking saves](../evolve-a-shipped-part-without-breaking-saves/overview.md)
- Mod features: [ksacars/vehicle-library-presets](../../../../mods/ksacars/features/vehicle-library-presets.md), [ksacars/car-part-gamedata](../../../../mods/ksacars/features/car-part-gamedata.md), [ksastructures/office-furniture-craft](../../../../mods/ksastructures/features/office-furniture-craft.md)

[^ksa-defaultvehiclesaves]: current/decomp/KSA/DefaultVehicleSaves.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehiclelaunchmenu]: current/decomp/KSA/VehicleLaunchMenu.cs (decompiled, build 2026.10.10.5554)
[^ksa-constants]: current/decomp/KSA/Constants.cs (decompiled, build 2026.10.10.5554)
[^ksa-uncompressedvehiclesave]: current/decomp/KSA/UncompressedVehicleSave.cs (decompiled, build 2026.10.10.5554)
[^ksa-savemetadata]: current/decomp/KSA/SaveMetaData.cs (decompiled, build 2026.10.10.5554)
[^ksa-program]: current/decomp/KSA/Program.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehiclesavedata]: current/decomp/KSA/VehicleSaveData.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicleeditor]: current/decomp/KSA/VehicleEditor.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicletemplate]: current/decomp/KSA/VehicleTemplate.cs (decompiled, build 2026.10.10.5554)
[^content-meta]: current/Content/Core/defaultvehicles/Rocket/meta.toml (game content, build 2026.10.10.5554)
[^ksa-vehiclesaves]: current/decomp/KSA/VehicleSaves.cs (decompiled, build 2026.10.10.5554)
