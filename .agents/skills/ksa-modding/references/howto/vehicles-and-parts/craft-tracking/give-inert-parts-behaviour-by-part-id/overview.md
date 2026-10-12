---
type: Game Feature
title: Giving inert parts behaviour by recognising their part Id
description: "How a mod finds capability on a part with no module: a data-driven registry of profiles keyed by part Id, a per-craft survey and a roster keyed by Part"
category: vehicles-and-parts
group: craft-tracking
topic: give-inert-parts-behaviour-by-part-id
demonstrated_by:
  - mods/ksarmory/features/weapon-registry-and-profiles
  - mods/ksarmory/features/countermeasure-dispensers
  - mods/ksarmory/features/weapon-packs-from-mod-folders
lifecycle: [startup:all-mods-loaded, frame:draw-ui, startup:asset-load, frame:after-on-frame]
game_types: [KSA.Part, KSA.PartTemplate, KSA.PartTree, KSA.Vehicle, KSA.ModLibrary, KSA.ModEntry]
tags: [parts, part-id, registry, weapons, decoys, flares, chaff, packs]
---
KSA lets a mod define a part in XML, but there is no hook to attach a new part module without patching the engine, so a mod-defined part is *inert*: structure, mass, a collider and a mesh. The question this topic answers is
how the mod's code finds out "this part is a flare dispenser" or "this part is a missile launcher" on any craft, including craft the mod author never saw. KSArmory's answer is to keep behaviour out of the XML entirely:
the **part Id is the only link**, a data-driven registry maps Ids to profiles, and the mod scans every craft's parts against it.

## Engine background

- **What a craft exposes.** `Vehicle.Parts` is a `PartTree` and `PartTree.Parts` a `ReadOnlySpan<Part>` of its full parts in tree order [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L136)[^ksa-parttree]. Each `Part` has `Id`, `DisplayName`, `Template` (the `PartTemplate`), `InstanceId` and the vehicle-frame pose
  [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1486-L1494)[^ksa-part].
- **What `Part.Id` is.** The constructor sets `Id = ResolveRuntimeId(inName, Template)`, which is the instance name when the craft names the instance and the template Id otherwise [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1486-L1494). KSArmory compares `Part.Id` with its registered Ids, so
  the registered Id must be what appears there for a part in an ordinary craft; matching on `part.Template.Id` (`Part.Template` is the `PartTemplate`) is the alternative if craft files may name instances.
- **No module hook.** A part's behaviour in the game comes from modules created from template data (`MeshViewModule.CreateComponents` is one such creator [current/decomp/KSA/MeshViewModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshViewModule.cs#L34-L47)[^ksa-meshviewmodule]); a mod cannot add a new module type without a patch, so the data-driven registry is the alternative.
- **Connectors say how a part attaches.** `Part.Connector.Flag.ToSurface` / `FromSurface` mark radial attach points [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L193-L199); KSArmory uses them to tell a part that merely rides on a hull from a part that can be a platform.
- **Parts move whole.** `Vehicle.Split` and `MergeFrom` hand the same `Part` objects to another vehicle ([overview of anchoring](../../part-geometry/anchor-to-parts-across-vehicle-changes/overview.md)), so a per-part entry should be keyed by the `Part` reference, not by craft and ordinal.
- **Mods on disk.** `ModLibrary.Manifest.Mods` lists every installed mod entry (`ModEntry.Enabled`); `ModLibrary.Find(id).DirectoryPath` gives a loaded mod's folder [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L160)[^ksa-modlibrary] [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L187).

## Approaches

| Article | Mod | Technique | Subject |
|---|---|---|---|
| [1: weapon registry keyed by part Id, profiles in a game-free layer](give-inert-parts-behaviour-by-part-id-1.md) | KSArmory | `data-driven-registry` | profiles keyed by `PartId`, a survey of each craft, a roster keyed by `Part` |
| [2: dispenser kind decided by which part is fitted](give-inert-parts-behaviour-by-part-id-2.md) | KSArmory | `part-id-lookup` | flare and chaff dispensers; the same pattern with a different consumer; decoys simulated as mod-side points |
| [3: registry fed from a folder in every installed mod](give-inert-parts-behaviour-by-part-id-3.md) | KSArmory | `convention-folder-scan` | data-only add-on packs, strict reader, post-load audit |

## See also

- [Game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Track which craft carry your part, classify them, and detect staging and loss](../track-and-classify-craft/overview.md), [Pick and gate targets for automated weapons](../target-and-gate-automated-weapons/overview.md), [Define a part with Assets and GameData XML and a GLB model](../../parts/define-a-part-with-game-data/overview.md)
- Mod features: [weapon-registry-and-profiles](../../../../mods/ksarmory/features/weapon-registry-and-profiles.md), [countermeasure-dispensers](../../../../mods/ksarmory/features/countermeasure-dispensers.md), [weapon-packs-from-mod-folders](../../../../mods/ksarmory/features/weapon-packs-from-mod-folders.md)

[^ksa-parttree]: current/decomp/KSA/PartTree.cs (decompiled, build 2026.10.10.5554)
[^ksa-part]: current/decomp/KSA/Part.cs (decompiled, build 2026.10.10.5554)
[^ksa-meshviewmodule]: current/decomp/KSA/MeshViewModule.cs (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: current/decomp/KSA/ModLibrary.cs (decompiled, build 2026.10.10.5554)
