---
type: How-To
title: Evolve part XML with append-only subpart lists and fixed part Ids
description: The authoring rules that keep old saves and craft loading when a part changes, derived from how PartTree.Deserialize pairs saved subparts by position
category: vehicles-and-parts
group: parts
topic: evolve-a-shipped-part-without-breaking-saves
approach: 1
difficulty: beginner
prerequisites: []
demonstrated_by: [mods/ksarmory/features/weapon-part-definitions-xml]
techniques: [xml-asset]
hooks: [part XML authoring]
lifecycle: [startup:asset-load]
game_types: [KSA.PartTree, KSA.PartTemplate, KSA.PartInstance, KSA.Part, KSA.ModLibrary]
tags: [saves, compatibility, subparts, authoring-rules, part-id]
---
Outcome: you can ship new versions of a part without crashing players' saves. There is nothing to install; the
technique is a set of rules for editing the part XML, each derived from the loader. KSArmory removed twenty
shell subparts from a gun and closed the game on every old save before it learned them.

Prerequisite: a part already defined ([Define a part with its own game data and GLB model](../define-a-part-with-game-data/overview.md)). Demonstrated by
[ksarmory/weapon-part-definitions-xml](../../../../mods/ksarmory/features/weapon-part-definitions-xml.md).

## Background: how the game does it

See the [overview](overview.md) for the load path. In short, the saved list is walked up to *its* length and
indexes *the template's* list:

```csharp
// current/decomp/KSA/PartTree.cs L338-346, L363-364
			for (int i = 0; i < nextNode2.SubPartInstances?.Count; i++)
			{
				Part part2 = nextNode.SubParts[i];
				PartInstance partInstance2 = nextNode2.SubPartInstances[i];
				foreach (ModuleBase.SaveDataBase component2 in partInstance2.Components)
				{
					if (!part2.Modules.ApplySaveData(component2, partTree.States))
					{
						LogCategory log2 = DefaultCategory.Log;
// ...
			}
		}
```

so the outcomes are:

| Change to a shipped part | What an old save does |
|---|---|
| append a subpart at the end of `<Part>` | loads; the new subpart has no saved state and starts unconfigured |
| remove a subpart | `IndexOutOfRangeException` in `PartTree.Deserialize` if the save lists more than the template has; the game closes |
| remove from the middle, or reorder | loads if counts still fit, but saved state is applied to the wrong subparts; module save data that matches no module on that subpart is discarded with a warning |
| rename a subpart's `Id` | position still matches, so it loads; names are not what pairs them |
| rename or delete a part Id | `ModLibrary.Get<PartTemplate>` throws (`NullReferenceException`), load fails |
| rename a mesh Id, edit transforms, masses, colliders | loads; these come from the template, not the save |

## Lifecycle and timing

The load happens when the player confirms a save or craft load, long after `startup:asset-load`. Your authoring
choices at asset-load time decide whether that later load works. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Part Ids are permanent.** Once a part is in a player's save or craft, never rename or delete its `Id`.
   Add a new part instead; keep the old one (set the `Hidden` editor tag so it leaves the picker).
2. **Subpart lists are append-only.** Add new `<SubPart>` instances at the end of the `<Part>`. Never remove
   one, never reorder, never insert in the middle.
3. **To retire a subpart, keep it and hide it.** Empty its model (a mesh-less subpart definition with only the
   `Hidden` editor tag, as the door subparts are), or move it out of sight. KSArmory's replacement rounds use
   hidden subparts for exactly this (see [Define articulated subparts and mounts, animate and hide them from code](../define-and-animate-subparts/overview.md)).
4. **Put new behaviour in new subparts or new game data**, not in reshuffled old ones. `PartGameData` is
   merged by Id on every load, so adding modules there needs no save migration.
5. **Keep subpart definition Ids stable too**: a `SubPartGameData Id` is matched to the definition, and
   `InstanceOf` in the save names it (for module save data).
6. **Test with an old save.** Keep a save and a craft from the previous release in the test set and load both
   after every part change; watch the log for `save data for '...' matched no module on sub-part` warnings.
7. **If a mistake already shipped**, use the repair in article 2.

## Complete example

KSArmory's part files keep the stable-Id discipline in comments and naming
(`KSArmory_<name>_SubPart`): [src/KSArmory/KSArmoryAssets.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryAssets.xml#L293-L323)[^ksarmory-ksarmoryassets] and the validator
[tools/validate-parts.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/validate-parts.py)[^ksarmory-validate-parts] that checks Ids and paths on every build.

## Variations and gotchas

- **Fewer saved than declared is safe**, because the loop is bounded by the save's count
  [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L338)[^ksa-parttree], so appending is the only safe growth.
- **State vs shape.** Anything in the template (meshes, masses, colliders, thrusters) updates for old saves
  for free; only subpart count/order and part Ids bind to the save.
- **Stateful subparts.** A subpart whose `SubPartRef` carries save data (module state) must not be reordered
  or dropped without migrating it; article 2's repair refuses to drop those.
- **Craft files have the same shape.** Library craft use the same `PartInstance` structure, so one rule covers
  saves and craft.

## See also

- [Overview](overview.md), [Article 2](evolve-a-shipped-part-without-breaking-saves-2.md)
- [ksarmory/weapon-part-definitions-xml](../../../../mods/ksarmory/features/weapon-part-definitions-xml.md)
- [Define a part with its own game data and GLB model](../define-a-part-with-game-data/overview.md), [Define articulated subparts and mounts, animate and hide them from code](../define-and-animate-subparts/overview.md)

[^ksa-parttree]: current/decomp/KSA/PartTree.cs (decompiled, build 2026.10.10.5554)
[^ksarmory-validate-parts]: KSArmory: tools/validate-parts.py
[^ksarmory-ksarmoryassets]: KSArmory: src/KSArmory/KSArmoryAssets.xml
