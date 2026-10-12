---
type: Game Feature
title: Changing a shipped part without breaking saves
description: How KSA rebuilds a saved part from its current template and pairs saved subparts by position, why removing or reordering subparts crashes old saves, and the authoring rule plus a startup repair
category: vehicles-and-parts
group: parts
topic: evolve-a-shipped-part-without-breaking-saves
demonstrated_by: [mods/ksarmory/features/weapon-part-definitions-xml, mods/ksarmory/features/startup-save-repair]
lifecycle: [startup:all-mods-loaded, startup:asset-load]
game_types:
  - KSA.PartTree
  - KSA.PartTemplate
  - KSA.VehicleSave
  - KSA.PartInstance
  - KSA.VehicleSaveData
  - KSA.VehicleSaves
  - KSA.GameSaves
  - KSA.SaveDirectory
  - KSA.Part
tags: [saves, subparts, compatibility, repair, part-id]
---
A save does not store a part's shape; it stores a reference (`InstanceOf`) to the current part template, plus the
per-part and per-subpart state. When a mod update changes the template, the engine rebuilds the part from the
new template and then lines the saved state up with it. The line-up is by **position**, and that is what
breaks.

## Engine background

- **Rebuild from the template.** `PartTree.Deserialize(PartInstance)` calls `Part.RegenerateWholePartInstanceTree`;
  every part is constructed from `inInstance.GetTemplate()` = `ModLibrary.Get<PartTemplate>(InstanceOf)`, which
  throws `NullReferenceException` for an unknown Id [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1475-L1498)[^ksa-part] [current/decomp/KSA/PartInstance.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartInstance.cs#L113-L116)[^ksa-partinstance]
  [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1119-L1125)[^ksa-modlibrary]. A part's subparts are built from `Template.SubPartInstances`, not from the
  save [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1500-L1560).
- **Pair by position.** Then the loop that applies saved state walks the save's `SubPartInstances` and indexes
  the rebuilt part's `SubParts` by the same index: `Part part2 = nextNode.SubParts[i]; PartInstance partInstance2 =
  nextNode2.SubPartInstances[i];` [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L299-L369)[^ksa-parttree]. A save listing more subparts than the template
  now has throws `IndexOutOfRangeException` inside the load; one listing fewer loads, and the extra subparts
  start unconfigured. A reordered list loads but applies state to the wrong subpart; `ApplySaveData` matches
  modules by declaring type and `InstanceOf`, so mismatched state is discarded with a warning ("matched no
  module on sub-part ...; its saved state was discarded") [current/decomp/KSA/ModuleList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleList.cs#L154-L169)[^ksa-modulelist].
- **Where it fires.** The crash site is `Universe.DeserializeSave` -> `Vehicle` deserialisation, run when the
  player confirms a load; nothing up the stack catches it and the game closes (per KSArmory's analysis).
- **Libraries.** Saved universes live in `<Documents>/saves/<name>/universe.xml` and craft in
  `<Documents>/Vehicles/<name>/vehicle.xml` [current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L124)[^ksa-gamesaves] [current/decomp/KSA/VehicleSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleSaves.cs#L167)[^ksa-vehiclesaves].

## Approaches

| Article | Mod | Technique |
|---|---|---|
| [1: append-only subpart lists and fixed Ids](evolve-a-shipped-part-without-breaking-saves-1.md) | KSArmory | the authoring rule, derived from `PartTree.Deserialize` |
| [2: rewrite saves at AllModsLoaded](evolve-a-shipped-part-without-breaking-saves-2.md) | KSArmory | trim surplus `SubPartRef`s before any load is possible; offline twin script |

## See also

- [Define a part with its own game data and GLB model](../define-a-part-with-game-data/overview.md), [Define articulated subparts and mounts, animate and hide them from code](../define-and-animate-subparts/overview.md), `choose-where-a-mod-stores-its-files`, [Load, validate and unload part mods in a running game](../load-and-unload-part-mods-at-runtime/overview.md)
- Mod features: [ksarmory/weapon-part-definitions-xml](../../../../mods/ksarmory/features/weapon-part-definitions-xml.md), [ksarmory/startup-save-repair](../../../../mods/ksarmory/features/startup-save-repair.md)
- [Find where KSA keeps user data and choose where a mod stores its config and data files](../../../persistence/user-data/choose-where-a-mod-stores-its-files/overview.md) - related topic

[^ksa-modlibrary]: current/decomp/KSA/ModLibrary.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehiclesaves]: current/decomp/KSA/VehicleSaves.cs (decompiled, build 2026.10.10.5554)
[^ksa-modulelist]: current/decomp/KSA/ModuleList.cs (decompiled, build 2026.10.10.5554)
[^ksa-part]: current/decomp/KSA/Part.cs (decompiled, build 2026.10.10.5554)
[^ksa-gamesaves]: current/decomp/KSA/GameSaves.cs (decompiled, build 2026.10.10.5554)
[^ksa-parttree]: current/decomp/KSA/PartTree.cs (decompiled, build 2026.10.10.5554)
[^ksa-partinstance]: current/decomp/KSA/PartInstance.cs (decompiled, build 2026.10.10.5554)
