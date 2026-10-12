---
type: How-To
title: Key mod data on Vehicle.Id and follow renames and disposals with postfixes
description: Use Vehicle.Id as the per-craft key, move entries when SetName succeeds, drop them when Dispose runs so a recycled id cannot inherit another craft's data, and hook the bool Dispose overload
category: persistence
group: saves
topic: identify-vessels-and-parts-across-saves
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/save-scoped-sidecar-registries]
techniques: [harmony-postfix, save-sidecar]
hooks:
  - harmony-prefix KSA.Vehicle.SetName
  - harmony-postfix KSA.Vehicle.SetName
  - harmony-postfix KSA.Vehicle.Dispose(bool)
lifecycle: [event:save, event:load, event:vehicle-destroyed]
game_types: [KSA.Vehicle, KSA.Universe, KSA.CelestialSystem]
tags: [saves, identity, vehicle-id, rename, dispose, harmony]
---

Outcome: per-craft mod data keyed by `Vehicle.Id` that stays attached to the right craft through a rename and cannot be inherited by a different craft that later takes the same name. KSA-AdvancedFlightComputer (AFC) stores its multi-pass and RCS executions this way; the save-id half of its key is in [Keep TOML in the mod folder keyed by (save id, vehicle id) and rekey it on save-as](../save-mod-state-with-the-game-save/save-mod-state-with-the-game-save-3.md). Prerequisites: StarMap, Harmony. Demonstrated by [Save-scoped TOML registries beside the mod](../../../../mods/ksa-advancedflightcomputer/features/save-scoped-sidecar-registries.md).

## Background: how the game does it

`Vehicle.Id` is the craft's display name. It is saved and rebuilt on load, so it is the one craft identifier that is stable across a save (see the [overview](./overview.md#engine-background)). Two operations change what an id means during a session:

- `Vehicle.SetName(string)` returns `false` and leaves the id unchanged when the name is already taken in `Universe.CurrentSystem.All`; on success it sets `Id`, recomputes `Hash` and re-stamps the parts ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1802-L1812)).
- `Vehicle.Dispose(bool endMission)` marks the vehicle disposed and deregisters it from the system, which frees the name for a new craft. `Dispose()` is just `Dispose(endMission: true)` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3770-L3798)), and EVA boarding calls the bool overload directly, so a patch on `Dispose()` alone would miss it.

A disposed vehicle's `FlightComputer` and `BurnPlan` stay intact, so a key that outlives the vehicle must be dropped explicitly.

## Lifecycle and timing

Both hooks are Harmony patches on `Vehicle` methods, so they run synchronously on whichever thread calls them (main thread in practice) in whatever frame phase triggers the rename (UI) or the disposal (destruction, boarding, a load's `DestroyAllVehicles`). During a load every vehicle is disposed before the new ones exist, so the dispose hook also clears entries for the whole old world; the load observer then reloads the registry (see [Observe UncompressedSave.Load and reset a registry of save-lifetime caches](../detect-save-load/detect-save-load-3.md)). Phase terms: [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Capture the old id in a prefix and move entries in the postfix, only on success.** A refused name leaves the vehicle unchanged, so it must not move registry entries:

   ```csharp
   // AdvancedFlightComputer/Core/SharedVehicleHooks.cs L147-L165
   // Vehicle.Dispose() delegates to Dispose(bool), and EVA boarding calls the bool overload directly.
   [HarmonyPatch(typeof(Vehicle), nameof(Vehicle.Dispose), new[] { typeof(bool) })]
   private static class DisposePatch
   {
       static void Postfix(Vehicle __instance) => OnDisposed(__instance);
   }

   // A refused name leaves the vehicle unchanged, so it must not move registry entries.
   [HarmonyPatch(typeof(Vehicle), nameof(Vehicle.SetName))]
   private static class SetNamePatch
   {
       static void Prefix(Vehicle __instance, out string __state) => __state = __instance.Id;

       static void Postfix(Vehicle __instance, bool __result, string __state)
       {
           if (__result)
               OnRenamed(__instance, __state);
       }
   }
   ```

2. **Rename the key, replacing stale state left under a reused name:**

   ```csharp
   // AdvancedFlightComputer/Features/MultiPass/MultiPassRegistry.cs L99-L116
   public static void RenameVehicle(string oldVehicleId, string newVehicleId)
   {
       if (oldVehicleId == newVehicleId) return;

       string saveId = SaveLoadObserver.CurrentSaveId;
       if (!_byKey.Remove((saveId, oldVehicleId), out MultiPassExecution? exec))
           return;

       // Replace stale state left under a reused vehicle name.
       _byKey.Remove((saveId, newVehicleId));
       exec.VehicleId = newVehicleId;
       _byKey[(saveId, newVehicleId)] = exec;

       if (MultiPassDebug.Enabled)
           DefaultCategory.Log.Debug(
               $"[AFC] MultiPassRegistry: rekeyed exec in save='{saveId}' " +
               $"from vehicle='{oldVehicleId}' to '{newVehicleId}'.");
   }
   ```

3. **Drop on dispose so a reused id starts clean:**

   ```csharp
   // AdvancedFlightComputer/Features/MultiPass/VehicleDisposePatch.cs L5-L15
   // Remove saved execution state so a reused vehicle ID cannot inherit it.
   internal static class VehicleDisposePatch
   {
       internal static void Remove(Vehicle vehicle)
       {
           string vehicleId = vehicle.Id;
           if (string.IsNullOrEmpty(vehicleId)) return;
           if (!MultiPassRegistry.Has(vehicleId)) return;

           MultiPassRegistry.Remove(vehicleId);
       }
   ```

   `OnDisposed` fans out to every feature's cache ([AdvancedFlightComputer/Core/SharedVehicleHooks.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SharedVehicleHooks.cs#L88-L106)).

4. **Resolve by id and treat misses as normal.** A lookup that finds nothing means the craft was disposed or never existed in this save; the registry should return "no entry", not fall back to another craft.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Core/SharedVehicleHooks.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SharedVehicleHooks.cs) | Rename and dispose hooks, fan-out to registries and caches |
| [AdvancedFlightComputer/Features/MultiPass/MultiPassRegistry.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassRegistry.cs) | `RenameVehicle`, `RekeyTo`, `Remove` on the id-keyed dictionary |
| [AdvancedFlightComputer/Features/MultiPass/VehicleDisposePatch.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/VehicleDisposePatch.cs) | The dispose clean-up |

## Variations and gotchas

- **Hook the bool overload.** `Dispose()` delegates to `Dispose(bool)` and EVA boarding calls the bool overload directly.
- **Duplicates across saves.** The key also needs the save id (`(SaveId, VehicleId)` in AFC), because two saves can hold craft with the same name.
- **Prefix and postfix share `__state`.** The prefix reads `__instance.Id` before the original runs; the postfix gets it back as `__state` together with `__result`.
- **Id-keyed is not part-keyed.** To address a specific part, see [Reference a specific part with tree paths and a topology hash, resolving to null instead of guessing](identify-vessels-and-parts-across-saves-4.md).

## See also

- [Key settings on the craft display name and warn when two craft share it](identify-vessels-and-parts-across-saves-2.md), [Reference a specific part with tree paths and a topology hash, resolving to null instead of guessing](identify-vessels-and-parts-across-saves-4.md), [overview](./overview.md)
- [Save-scoped TOML registries beside the mod](../../../../mods/ksa-advancedflightcomputer/features/save-scoped-sidecar-registries.md)
- [Store per-save mod data next to a KSA save and notice when the player saves](../save-mod-state-with-the-game-save/overview.md), [Making a vehicle survive crashes and g-loads, and damaging parts against crash tolerance](../../../vehicles-and-parts/vehicles/make-a-vehicle-indestructible/overview.md)
