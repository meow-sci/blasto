---
type: Game Feature
title: Keeping an anchor on a part across staging, docking, editor changes and saves
description: Which game operations move Part objects between vehicles and four ways for a mod to re-find a part afterwards
category: vehicles-and-parts
group: part-geometry
topic: anchor-to-parts-across-vehicle-changes
demonstrated_by:
  - mods/ksa-measuretools/features/part-anchor-rehoming
  - mods/unscience/features/hot-pursuit-mesh-ray-placement
  - mods/unscience/features/lib-save-part-identity
  - mods/ksa-measuretools/features/editor-measuring
lifecycle: [frame:draw-ui, frame:prepare, editor:draw-ui, editor:on-frame, event:save, event:load, frame:input]
game_types: [KSA.Part, KSA.PartTree, KSA.Vehicle, KSA.VehicleEditor, KSA.VehicleEditingSpace]
tags: [parts, identity, staging, docking, editor, save, anchor]
---
A mod that remembers "that strut" has to survive the game moving the strut. KSA keeps parts in a `PartTree` owned by one `Vehicle`, and several
game operations hand the same `Part` object to another owner, or throw the owner away and make a new one, so a stored `Vehicle` reference, a vehicle name or an
index into `vehicle.Parts.Parts` all go stale. This topic compares four ways of keeping a handle on a part: by object identity, by vehicle id plus `Part.InstanceId`, by a
structural path saved with a topology hash, and by reading the editor's own trees.

## Engine background

- **Ownership.** `Vehicle.Parts` is a `PartTree`; `PartTree.Parts` is a `ReadOnlySpan<Part>` of the full parts and `PartTree.Find(ulong id)` is a linear search on `Part.InstanceId`
  [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L1462-L1474)[^ksa-parttree]. A `Part` has `TreeChildren` (the attachment tree) [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L668)[^ksa-part] and `SubParts` (its own sub-assemblies) [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1127).
- **Who moves parts.** `Vehicle.Split` creates the new vehicle with the existing `part2` as its root (`CreateVehicle(..., splitVehicleId, part2, Orbit)`) [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1613)[^ksa-vehicle];
  `Vehicle.MergeFrom` attaches the other vehicle's root under this vehicle's tree (`Connector.ConnectAndMerge` calls `fullPart.AddChild(fullPart2, ...)`) and then calls `otherVehicle.Dispose()`
  [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1738-L1798); `VehicleEditor.Dispose` assigns `ExistingVehicle.Parts = EditingSpace.Parts` or builds a new vehicle from the editing tree's root
  [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L2256-L2315)[^ksa-vehicleeditor]. The `Part` objects survive; their owner changes.
- **Identity values.** `Part.InstanceId` is a `uint` taken from `Universe.GetNextRunningId()` in the `Part` constructor [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1490-L1494), and `Universe.DeserializeSave` resets that counter
  [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2463)[^ksa-universe], so it is stable for a session but not across a save and load. `Part.Id` falls back to the template id when no instance name is given
  [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1486-L1488), so sibling parts of one template share it.
- **Disposal.** `Vehicle.IsDisposed` is set by `Dispose` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3775-L3782); `MergeFrom` disposes the absorbed vehicle.
- **Editor.** `Program.Editor` is the `VehicleEditor` (null in flight); `EditingSpace.Parts` is the tree being edited and `UnattachedPartTrees` holds parts in the hand
  [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L545) [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L689).

## Approaches

| Article | Mod | Technique | Subject |
|---|---|---|---|
| [1: identity tracking and rehoming each frame](anchor-to-parts-across-vehicle-changes-1.md) | KSA-MeasureTools | `object-identity-tracking` | store the `Part`, test it against its owner, search the system for the same object; survives the editor boundary |
| [2: re-resolve by vehicle id and `Part.InstanceId`](anchor-to-parts-across-vehicle-changes-2.md) | unscience (Hot Pursuit) | `imgui-drawlist` | store ids, look both up every frame, go dormant when either is missing |
| [3: saved part reference with tree paths and topology hash](anchor-to-parts-across-vehicle-changes-3.md) | unscience (ksa-abstractions) | `save-sidecar` | structural address that survives a reload and resolves to null rather than a lookalike |
| [4: Harmony hooks in the editor frame, `EditingSpace.Parts` / `AllParts`](anchor-to-parts-across-vehicle-changes-4.md) | KSA-MeasureTools | `harmony-postfix` | reach the craft and the in-hand parts while the editor is open |

Use 1 for live, in-session anchors that must follow a part wherever it goes; 2 when a part must only follow its vehicle and may pause when it is gone; 3 for anything written to a save;
4 for any code that has to look at parts in the editor.

## See also

- [Game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Read part meshes, attach nodes and part-local points in vehicle, world and screen space](../get-part-points-in-vehicle-and-world-frames/overview.md) for the maths that turns the stored part-local point into a position
- [Weld one vessel to another](../../../physics/kinematics/weld-one-vessel-to-another/overview.md) and [Scale a vessel or its parts at runtime](../../vehicles/scale-a-vessel-or-its-parts-at-runtime/overview.md), which change part ownership and transforms
- Mod features: [part-anchor-rehoming](../../../../mods/ksa-measuretools/features/part-anchor-rehoming.md), [hot-pursuit-mesh-ray-placement](../../../../mods/unscience/features/hot-pursuit-mesh-ray-placement.md), [lib-save-part-identity](../../../../mods/unscience/features/lib-save-part-identity.md), [editor-measuring](../../../../mods/ksa-measuretools/features/editor-measuring.md)

[^ksa-parttree]: current/decomp/KSA/PartTree.cs (decompiled, build 2026.10.10.5554)
[^ksa-part]: current/decomp/KSA/Part.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicleeditor]: current/decomp/KSA/VehicleEditor.cs (decompiled, build 2026.10.10.5554)
[^ksa-universe]: current/decomp/KSA/Universe.cs (decompiled, build 2026.10.10.5554)
