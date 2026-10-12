---
type: How-To
title: Filter your own craft out of Universe.SerializeSystems
description: Postfix Universe.SerializeSystems and remove the mod's craft from each CelestialSystemData.Vehicles list so a save made with the mod loaded still opens without it, sparing the craft being flown
category: persistence
group: saves
topic: keep-mod-vehicles-out-of-saves
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksastructures/features/office-furniture-craft]
techniques: [save-filter, harmony-postfix]
hooks: [starmap StarMapAllModsLoaded, harmony-postfix KSA.Universe.SerializeSystems]
lifecycle: [startup:all-mods-loaded, event:save, frame:draw-ui]
game_types: [KSA.Universe, KSA.CelestialSystemData, KSA.UniverseData, KSA.Vehicle]
tags: [saves, serialization, harmony, vehicles, uninstall-safe, filter]
---

Outcome: the mod's helper craft (an office chair and a computer, each a craft of one part) never appear in `universe.xml`, so a save made with the mod loaded opens on a machine without it, and the mod simply sets the craft down again whenever they are missing. Prerequisites: StarMap and Harmony, and craft your mod can recreate. Demonstrated by [Office furniture as single-part craft, kept out of saves](../../../../mods/ksastructures/features/office-furniture-craft.md) (the spawning itself is in [Spawning a vehicle at runtime](../../../vehicles-and-parts/vehicles/spawn-a-vehicle-at-runtime/overview.md)).

## Background: how the game does it

`UniverseData.Create()` assigns `Universe.SerializeSystems()` to `CelestialSystems` ([current/decomp/KSA/UniverseData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UniverseData.cs#L50)). `SerializeSystems` returns a fresh `List<CelestialSystemData>` with one entry from `CurrentSystem.SerializeSave()`, which adds one `VehicleData` per vehicle in the system ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2480-L2491), [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L632-L661)). The list and its `Vehicles` are ordinary mutable lists ([current/decomp/KSA/CelestialSystemData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystemData.cs#L6-L15)), and nothing else reads them before `Write`. KSAStructures' own comment on the failure it avoids: a save names each craft's part templates, and one naming a template that is not installed "ends the game on load" (the mod's observation; unscience's preflight rejects the same case with an error before releasing the world, see [Hook native save and load with five patches, eight callbacks and a deferred world change](../save-mod-state-with-the-scene/save-mod-state-with-the-scene-3.md)).

## Lifecycle and timing

| When | What | Lifecycle |
|---|---|---|
| `StarMapAllModsLoaded` | `Office.Place()` installs its patches, including `KeepOutOfSaves(_harmony)` ([src/KSAStructures/Ksa/Office.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Office.cs#L51-L88), called from [src/KSAStructures/Ksa/KSAStructuresMod.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KSAStructuresMod.cs#L42-L47)) | `startup:all-mods-loaded` |
| Any save | The postfix runs at the end of `SerializeSystems`, on the main thread, during `GameSave.Populate`, in whatever frame phase the save was issued | `event:save` |
| Every 3 s of simulated time | `EnsureChair` (from the mod's step) checks each piece by vehicle id and spawns at most one missing piece per pass | `frame:draw-ui` |

On load the pieces are simply missing and the next pass sets them down again. Frame phases: [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Give your craft stable identities you can match.** KSAStructures keeps a table of the craft it owns, with the vehicle ids it spawns them under; the filter matches `VehicleData.Id` (the vehicle's display name), not a part id:

   ```csharp
   // src/KSAStructures/Ksa/OfficeFurniture.cs L10-L24
   public const string ChairId = "Office chair";
   private const string ChairPart = "KSArmory_Prefab_OfficeChair";

   private const double OfficeFloor = 61.8;
   private const double GroundSurface = 0.0;

   public const string MonitorId = "Office computer";

   // What stands in the office as craft of its own: metres east of the tower's landmark, and the
   // height of what it stands on above the tower's ground.
   private static readonly (string Id, string Part, double East, double Height)[] Furniture =
   [
       (ChairId, ChairPart, 6.7, OfficeFloor + 0.03),
       (MonitorId, "KSArmory_Prefab_OfficeMonitor", 7.76, OfficeFloor + 0.56 + 0.01),
   ];
   ```

2. **Patch `Universe.SerializeSystems` with a postfix, degrading if the target is missing.** The doc comment records why and the one exception:

   ```csharp
   // src/KSAStructures/Ksa/OfficeFurniture.cs L30-L49
   /// <summary>
   /// The furniture left out of a save. A save names each craft's part templates, and one naming a
   /// template that is not installed ends the game on load, so a save holding the chair could not be
   /// opened without this mod. They are set down again whenever they are missing. A piece somebody is
   /// flying or sitting in is the exception: taking it out would take them with it.
   /// </summary>
   public static void KeepOutOfSaves(Harmony harmony)
   {
       try
       {
           MethodInfo? target = AccessTools.Method(typeof(Universe), nameof(Universe.SerializeSystems));
           if (target is null) { Log.Warn("office: no save hook; a save holds the office's furniture"); return; }

           harmony.Patch(target, postfix: new HarmonyMethod(typeof(Office).GetMethod(nameof(AfterSerialize), BindingFlags.NonPublic | BindingFlags.Static)!));
       }
       catch (Exception e)
       {
           Log.Warn($"office: could not hook the save ({e.GetBaseException().Message})");
       }
   }
   ```

3. **Remove the entries from the result.** The flown craft is spared, because removing it would remove the player's own vessel from the save:

   ```csharp
   // src/KSAStructures/Ksa/OfficeFurniture.cs L51-L64
   private static void AfterSerialize(List<CelestialSystemData> __result)
   {
       try
       {
           string? flown = KsaWorld.ControlledVehicle?.Id;
           foreach (CelestialSystemData system in __result)
           {
               system.Vehicles.RemoveAll(v => v.Id != flown && Array.Exists(Furniture, piece => piece.Id == v.Id));
           }
       }
       catch
       {
       }
   }
   ```

   `RemoveAll` runs on `CelestialSystemData.Vehicles` of the returned list only; the live `Vehicle` objects are not touched, and the empty `catch` keeps an exception out of the save path.

4. **Recreate the craft whenever they are missing.** A load or a fresh world has none; the mod's per-pass check finds each piece by id and spawns it ([src/KSAStructures/Ksa/OfficeFurniture.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeFurniture.cs#L66-L104)). Without this the filter would remove the furniture for good.

5. **Keep your part ids frozen.** The part ids keep a `KSArmory_` prefix from where they first shipped: a save made while a piece was flown (the exception in step 3) can name them, so renaming them would break such saves.

## Complete example

| File | Role |
|---|---|
| [src/KSAStructures/Ksa/OfficeFurniture.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeFurniture.cs) | Furniture table, `KeepOutOfSaves`, the postfix and `EnsureChair` |
| [src/KSAStructures/Ksa/Office.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Office.cs#L51-L88) | `Office.Place`, which installs the patch |
| [src/KSAStructures/Ksa/KSAStructuresMod.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KSAStructuresMod.cs#L42-L56) | Where `Place` is called |

## Variations and gotchas

- **Identify by vehicle id, not part id.** The filter matches the ids the mod itself gave its craft; another vessel that happens to use a mod part is saved normally.
- **The flown or sat-in craft stays in the save.** A save made while the player sits in the chair therefore does name the mod's part template and will not open without the mod; this is the cost of not deleting the player's vessel.
- **Other craft keep references.** If another saved record points at a removed craft (a `TargetId`, a crew assignment), it points at nothing after load; filter only craft nothing else refers to.
- **A save is not the only serialisation.** `SerializeSystems` is also what a multiplayer or profile path would call if one is added; in this build only `UniverseData.Create` calls it.
- **Tell players.** Document that the craft are recreated, not saved; state saved on them is lost (their position is re-derived from constants).

## See also

- [overview](./overview.md)
- [Office furniture as single-part craft, kept out of saves](../../../../mods/ksastructures/features/office-furniture-craft.md)
- [Spawning a vehicle at runtime](../../../vehicles-and-parts/vehicles/spawn-a-vehicle-at-runtime/overview.md), [Tracking which craft carry your part, classifying them and detecting staging and loss](../../../vehicles-and-parts/craft-tracking/track-and-classify-craft/overview.md), [Store per-save mod data next to a KSA save and notice when the player saves](../save-mod-state-with-the-game-save/overview.md)
