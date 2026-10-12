---
type: Game Feature
title: Identify vessels and parts stably across saves
description: Which vessel and part identifiers survive a save and load and which do not, and four ways mods key their data - Vehicle.Id with rename and dispose hooks, display-name keys, a window layout that can only anchor on the vehicle id, and a structural part address with a topology hash
category: persistence
group: saves
topic: identify-vessels-and-parts-across-saves
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/save-scoped-sidecar-registries
  - mods/ksarmory/features/per-craft-settings-json-sidecar
  - mods/purrtty/features/saved-terminal-layouts
  - mods/unscience/features/lib-save-part-identity
lifecycle: [event:save, event:load, event:vehicle-destroyed, frame:prepare]
game_types:
  - KSA.Part
  - KSA.PartInstance
  - KSA.Vehicle
  - KSA.Universe
  - KSA.PartTree
  - KSA.PartTemplate
  - KSA.CelestialSystem
tags: [saves, identity, vehicle-id, part-id, instance-id, topology-hash, keys]
---

A mod that stores data about "this craft" or "this part" across a save and load needs a key that is still true afterwards. KSA offers several identifiers and most of them do not survive. This topic lays out which do, what breaks each key, and four ways mods cope.

## Engine background

| Identifier | Where it comes from | Survives a save and load? |
|---|---|---|
| `Part.InstanceId` | `Universe.GetNextRunningId()` in the `Part` constructor (`Interlocked.Increment` on a static counter) | No. `Universe.DeserializeSave` calls `ResetRunningId()`, and the saved `PartInstance.GlobalInstanceId` is `[XmlIgnore]` ([current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L574), [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1492), [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L177-L185), [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2463), [current/decomp/KSA/PartInstance.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartInstance.cs#L9-L10)) |
| `Part.Id` | `Part.ResolveRuntimeId(instanceName, template)`: the instance name, else `template.Id` | Only if the instance has its own name. Editor-placed parts are created with `new Part(partTemplate.Id, partTemplate)`, so siblings of the same template share one id ([current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1485-L1495)) |
| `PartInstance.LocalInstanceId` | A per-vehicle counter assigned while saving the tree (`localRunningId++`) | Saved in `universe.xml` as an attribute, but only meaningful inside the same saved tree: the game itself resolves `TargetPartId` and `ControlPartId` by walking the live `Part` tree and the saved `PartInstance` tree in parallel and matching this number ([current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L2168), [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L731-L749)) |
| `Vehicle.Id` | The display name; `Vehicle.SetName(name)` returns `false` and changes nothing if `Universe.CurrentSystem.All` already has that name | Yes, it is saved and rebuilt from the save. It can be renamed in a session, and a free name can be reused after `Vehicle.Dispose` deregisters the old vehicle ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1802-L1812), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3775-L3798)) |
| Object references (`Vehicle`, `Part`) | the live objects | No. `DeserializeSave` disposes every vehicle (see [Detect a save load or quickload and reset mod state safely](../detect-save-load/overview.md)) |

The stock launch menu keeps vehicle ids unique within the live universe by appending `_1`, `_2`, ... while the name is taken ([current/decomp/KSA/VehicleLaunchMenu.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleLaunchMenu.cs#L246-L252)), and the collection that holds them does not accept a second item with the same hash unless it is configured to replace ([current/decomp/KSA/LookupCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LookupCollection.cs#L113-L131)). So two live craft normally do not share an id; the realistic collisions are a name reused over time, a mod that spawns with a fixed id, and ids across different saves.

Frame phases for capture and resolve are in the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md): capture at `event:save`, resolve after the world is rebuilt (`event:load`, which unscience performs in the `frame:prepare` handoff).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Key on Vehicle.Id and follow SetName and Dispose with postfixes](./identify-vessels-and-parts-across-saves-1.md) | KSA-AdvancedFlightComputer | `harmony-postfix` | `event:save`, `event:load`, `event:vehicle-destroyed` | Simple and survives renames; needs two hooks to stay correct |
| [Key on the craft display name with a launcher ordinal suffix](./identify-vessels-and-parts-across-saves-2.md) | KSArmory | `save-sidecar` | `event:save`, `event:load` | No hooks; two craft that share a name share an entry; shows the limit of name keys |
| [Why a window layout can only anchor on Vehicle.Id](./identify-vessels-and-parts-across-saves-3.md) | purrtty | `toml-config` | `frame:draw-ui`, `event:load` | Part and sub-part ids resolve to the first match; best effort; the counter-example |
| [SavedPartReference with paths and a topology hash, resolved in the restore scope](./identify-vessels-and-parts-across-saves-4.md) | unscience | `save-sidecar` | `event:save`, `event:load`, `frame:prepare` | Addresses a specific part and refuses to guess; breaks (to null) when the vehicle's tree changes |

## See also

- [Store per-save mod data next to a KSA save and notice when the player saves](../save-mod-state-with-the-game-save/overview.md), [Save a whole mod's scene state as versioned sidecar records, with reset and replay on load](../save-mod-state-with-the-scene/overview.md)
- [Making a vehicle survive crashes and g-loads, and damaging parts against crash tolerance](../../../vehicles-and-parts/vehicles/make-a-vehicle-indestructible/overview.md), [Keeping an anchor on a part across staging, docking, editor changes and saves](../../../vehicles-and-parts/part-geometry/anchor-to-parts-across-vehicle-changes/overview.md)
- [Detect a save load or quickload and reset mod state safely](../detect-save-load/overview.md)
