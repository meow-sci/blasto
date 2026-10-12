---
type: How-To
title: Key settings on the craft display name and warn when two craft share it
description: File per-craft settings under the Vehicle.Id display name with a launcher ordinal suffix, and report when a second craft carries a name already in use, which is the limit of name keys
category: persistence
group: saves
topic: identify-vessels-and-parts-across-saves
approach: 2
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/per-craft-settings-json-sidecar]
techniques: [save-sidecar, json-config]
hooks: [starmap StarMapAfterGui]
lifecycle: [event:save, event:load, frame:draw-ui]
game_types: [KSA.Vehicle, KSA.Universe]
tags: [saves, identity, vehicle-id, display-name, collision]
---

Outcome: the simplest stable key. KSArmory files each weapon system's settings under the craft's display name, so the settings come back with the save. The article is as much about where that key breaks as how to use it. Demonstrated by [Per-craft settings as a JSON sidecar inside the save folder](../../../../mods/ksarmory/features/per-craft-settings-json-sidecar.md); the sidecar placement is in [Keep a JSON sidecar inside the save folder and write it when universe.xml changes](../save-mod-state-with-the-game-save/save-mod-state-with-the-game-save-1.md).

## Background: how the game does it

`Vehicle.Id` is the display name, saved with the vehicle (see the [overview](./overview.md#engine-background)). Inside one running universe it is unique: the stock launch menu appends `_1`, `_2`, ... while the name is taken ([current/decomp/KSA/VehicleLaunchMenu.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleLaunchMenu.cs#L246-L252)) and `Vehicle.SetName` refuses a taken name ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1802-L1812)). Uniqueness is therefore guaranteed only at one moment: a destroyed craft frees its name; a mod that spawns craft with a fixed id gets an error and no registered vehicle when the name is taken ([current/decomp/KSA/LookupCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LookupCollection.cs#L113-L140)); and two saves can hold different craft with one name.

`Vehicle` object references do not survive a save, so the name is the only thing available to a mod that does not patch `SetName` and `Dispose` (compare [Key mod data on Vehicle.Id and follow renames and disposals with postfixes](identify-vessels-and-parts-across-saves-1.md)).

## Lifecycle and timing

The store is read when a launcher is crewed and written when the game's save is noticed, all inside the mod's StarMap hooks (`frame:draw-ui` and the step that follows); nothing here patches the engine. Frame order: [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Build the key from the display name.** The first launcher on a craft keeps the bare name, so a save written before a craft could carry several launchers still restores. Further launchers get a `#n` suffix, because two racks on one craft sharing one entry would share an arm switch:

   ```csharp
   // src/KSArmory/Ksa/WeaponSystems.cs L786-L793
   // What a weapon's settings are filed under.
   //
   // The first launcher keeps the bare craft name, so a save written before a craft could carry
   // several still restores. Anything beyond it is suffixed, because two racks on one craft
   // sharing one entry would share an arm switch -- and arming one to drop a bomb would arm the
   // other.
   private static string SettingsKey(Vehicle craft, int ordinal)
       => ordinal == 0 ? KsaWorld.DisplayName(craft) : $"{KsaWorld.DisplayName(craft)}#{ordinal + 1}";
   ```

   The suffix is the launcher's ordinal on the same craft; it is not a deduplication of craft that have the same name.

2. **Warn on a shared name where it is visible.** When a launcher is crewed, look for another crewed craft with the same display name and log it once:

   ```csharp
   // src/KSArmory/Ksa/WeaponSystems.cs L186-L201
   private void WarnIfNameIsTaken(Vehicle craft)
   {
       string name = KsaWorld.DisplayName(craft);

       foreach ((Vehicle other, int ordinal) in _entries.Keys)
       {
           if (ordinal != 0) continue;
           if (ReferenceEquals(other, craft)) continue;
           if (!string.Equals(KsaWorld.DisplayName(other), name, StringComparison.Ordinal)) continue;
           if (!_reportedShared.Add(name)) return;

           Log.Warn($"two crewed craft are both called '{name}'; they share one settings entry, "
                    + "so each will restore the other's and the last saved wins. Rename one.");
           return;
       }
   }
   ```

3. **Accept the limit and document it.** The store's class comment states it: a `Vehicle` reference does not survive a save and the id is the only thing that does, so there is "no fix here to make":

   ```csharp
   // src/KSArmory/Ksa/SettingsStore.cs L9-L14
   /// <para><b>Keyed on the craft's display name, which is not unique.</b> A squadron built from one
   /// blueprint shares an Id, so those craft share one entry: they all restore the same settings and
   /// the last one saved overwrites the rest. There is no fix here to make, because a
   /// <c>Vehicle</c> reference does not survive a save and the Id is the only thing that does. The
   /// collision is reported when the roster crews the second craft, which is where it is visible.
   /// </para>
   ```

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/SettingsStore.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/SettingsStore.cs#L1-L60) | Store header and comments on the key |
| [src/KSArmory/Ksa/WeaponSystems.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystems.cs#L186-L201) | Shared-name warning |
| [src/KSArmory/Ksa/WeaponSystems.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystems.cs#L786-L793) | `SettingsKey` |

## Variations and gotchas

- **Rename loses the entry.** Nothing hooks `SetName`, so renaming a craft in the session starts it from defaults and leaves the old entry behind. Use [Key mod data on Vehicle.Id and follow renames and disposals with postfixes](identify-vessels-and-parts-across-saves-1.md) if renames matter.
- **A reused name inherits.** A new craft launched under a name freed by a destroyed one finds the old craft's entry.
- **Two craft with one name share an entry** and the last write wins, as the warning says. The stock launch path avoids this by suffixing, so it takes a spawner with a fixed id or a name reused across time.
- **Case.** The game's collection key is `KeyHash.Make(id)`, which lowercases the id with `ToLowerInvariant` before hashing ([current/decomp/KSA/KeyHash.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KeyHash.cs#L15-L44)); keys compared with `StringComparer.Ordinal` treat `Probe` and `probe` as different although the game treats them as one id.

## See also

- [Key mod data on Vehicle.Id and follow renames and disposals with postfixes](identify-vessels-and-parts-across-saves-1.md), [Anchor a saved window layout on Vehicle.Id and accept best-effort part ids](identify-vessels-and-parts-across-saves-3.md), [overview](./overview.md)
- [Per-craft settings as a JSON sidecar inside the save folder](../../../../mods/ksarmory/features/per-craft-settings-json-sidecar.md)
- [Store per-save mod data next to a KSA save and notice when the player saves](../save-mod-state-with-the-game-save/overview.md)
