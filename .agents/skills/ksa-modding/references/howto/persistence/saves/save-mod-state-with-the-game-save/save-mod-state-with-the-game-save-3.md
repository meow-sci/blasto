---
type: How-To
title: Keep TOML in the mod folder keyed by (save id, vehicle id) and rekey it on save-as
description: Leave the stock save untouched, store execution state in the mod folder, key every entry by save id and vehicle id, load it at startup, and move entries to the new save id after a successful UncompressedSave.Write
category: persistence
group: saves
topic: save-mod-state-with-the-game-save
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/save-scoped-sidecar-registries]
techniques: [save-sidecar, harmony-postfix, toml-config]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-prefix KSA.UncompressedSave.Load
  - harmony-postfix KSA.UncompressedSave.Load
  - harmony-postfix KSA.UncompressedSave.Write
lifecycle: [startup:all-mods-loaded, event:save, event:load]
game_types: [KSA.UncompressedSave, KSA.GameSave, KSA.Vehicle, KSA.GameSaves]
tags: [saves, sidecar, toml, rekey, save-as, atomic-write]
---

Outcome: long-running mod state (a multi-pass departure, an RCS burn in progress) survives a save and reload, without changing the stock save format, so uninstalling the mod loses nothing in the saves. KSA-AdvancedFlightComputer (AFC) stores it in TOML files in the mod folder, keys every entry by `(save id, vehicle id)`, and keeps the keys in step with the game through postfixes on the concrete save class. Demonstrated by [Save-scoped TOML registries beside the mod](../../../../mods/ksa-advancedflightcomputer/features/save-scoped-sidecar-registries.md).

## Background: how the game does it

A save has a stable `Id` (its folder name) but nothing in the game ties mod data to it; the stock `universe.xml` has no extension slot. `UncompressedSave.Write()` returns a `bool` and is the point at which a "save as" (a new name) becomes a real save with a new id. After a successful write, the written save's `Id` is the key under which a later `Load` will find it. A vehicle's `Id` is its display name and can change through `Vehicle.SetName`. Details in the [overview](./overview.md#engine-background) and in [Identify vessels and parts stably across saves](../identify-vessels-and-parts-across-saves/overview.md).

## Lifecycle and timing

| When | What | Lifecycle |
|---|---|---|
| Patch time (`StarMapAllModsLoaded` of AFC's mod) | `MultiPassRegistry.Init()` loads `multipass.toml` immediately | `startup:all-mods-loaded` |
| After a completed load | `SaveScopedState.ResetAll`, `MultiPassRegistry.Load`, `SaveLoaded` event | `event:load` |
| After a successful write | rekey old save id to the written id, `Save()`, `SaveWritten(old, new)` event | `event:save` |

All run on the main thread inside the game's call; see the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md). The reset on load is the subject of [Observe UncompressedSave.Load and reset a registry of save-lifetime caches](../detect-save-load/detect-save-load-3.md); this article is the persistence half.

## Steps

1. **Pick the path outside the save folder.** The registry is `<Documents>/My Games/Kitten Space Agency/mods/AdvancedFlightComputer/multipass.toml`, built once at `Init()`. Loading here, not on the first `UncompressedSave.Load`, matters: the default world may never go through `Load`, and the first save would otherwise overwrite the other saves' entries:

   ```csharp
   // AdvancedFlightComputer/Features/MultiPass/MultiPassRegistry.cs L26-L44
   private static readonly Dictionary<(string SaveId, string VehicleId), MultiPassExecution>
       _byKey = new();

   private static string _modDir = string.Empty;
   private static string _configPath = string.Empty;

   // True when the last load could not read all entries from the file.
   private static bool _lastLoadWasDefective;

   public static void Init()
   {
       string userDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
       _modDir = Path.Combine(userDocs, "My Games", "Kitten Space Agency",
           "mods", "AdvancedFlightComputer");
       _configPath = Path.Combine(_modDir, "multipass.toml");

       // Load at initialization because the default world may never call UncompressedSave.Load. Otherwise its first save could overwrite entries from other saves.
       Load();
   }
   ```

2. **Key by (save id, vehicle id).** One file holds entries for every save on the machine. Lookups use `SaveLoadObserver.CurrentSaveId`, which is empty for a fresh world until its first save ([AdvancedFlightComputer/Features/MultiPass/MultiPassRegistry.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassRegistry.cs#L62-L74)).

3. **Patch `UncompressedSave.Load` as in [Observe UncompressedSave.Load and reset a registry of save-lifetime caches](../detect-save-load/detect-save-load-3.md)** to learn the loaded id, reset caches and reload the registry from disk ([AdvancedFlightComputer/Core/SaveLoadObserver.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SaveLoadObserver.cs#L35-L61)).

4. **Patch `UncompressedSave.Write` and act only on success.** A failed write keeps entries on the previous save id. On success rekey, persist and notify:

   ```csharp
   // AdvancedFlightComputer/Core/SaveLoadObserver.cs L93-L123
   internal static void OnSaveWritten(string newSaveId)
   {
       string oldSaveId = CurrentSaveId;

       if (DebugConfig.MultiPass)
       {
           MultiPassDebug.LogRegistry(
               $"SaveLoadObserver.WritePatch (pre-rekey, save '{oldSaveId}' -> '{newSaveId}')",
               MultiPassRegistry.Snapshot);

           Vehicle? controlled = Program.ControlledVehicle;
           if (controlled != null)
               MultiPassDebug.LogBurnPlan(
                   $"SaveLoadObserver.WritePatch vehicle='{controlled.Id}' (pre-save)",
                   controlled.FlightComputer.BurnPlan);
       }

       // Rekey before writing so the file uses the ID of the save just written.
       MultiPassRegistry.RekeyTo(oldSaveId, newSaveId);

       CurrentSaveId = newSaveId;

       MultiPassRegistry.Save();

       SaveWritten?.Invoke(oldSaveId, newSaveId);

       if (DebugConfig.MultiPass)
           DefaultCategory.Log.Debug(
               $"[AFC] SaveLoadObserver.WritePatch: wrote save '{CurrentSaveId}', " +
               $"persisted registry alongside.");
   }
   ```

   ```csharp
   // AdvancedFlightComputer/Core/SaveLoadObserver.cs L125-L144
   [HarmonyPatch(typeof(UncompressedSave), nameof(UncompressedSave.Write), new Type[0])]
   private static class WritePatch
   {
       static void Postfix(UncompressedSave __instance, bool __result)
       {
           // A failed write keeps the registry on the previous save.
           if (!__result)
               return;

           try
           {
               OnSaveWritten(__instance.Id ?? string.Empty);
           }
           catch (Exception ex)
           {
               DefaultCategory.Log.Warning(
                   $"[AFC] SaveLoadObserver Write Postfix: {ex}");
           }
       }
   }
   ```

5. **Rekey on save-as and clean up an overwritten destination.** Entries of the old id move to the new id; entries already filed under the new id (the save being overwritten) are dropped first:

   ```csharp
   // AdvancedFlightComputer/Features/MultiPass/MultiPassRegistry.cs L119-L152
   public static void RekeyTo(string oldSaveId, string newSaveId)
   {
       if (string.IsNullOrEmpty(newSaveId) || oldSaveId == newSaveId) return;

       var stale = new List<(string, string)>();
       var moved = new List<MultiPassExecution>();
       foreach (var (key, exec) in _byKey)
       {
           if (key.SaveId == newSaveId)
               stale.Add(key);
           else if (key.SaveId == oldSaveId)
               moved.Add(exec);
       }

       foreach (var key in stale)
       {
           _byKey.Remove(key);
           if (MultiPassDebug.Enabled)
               DefaultCategory.Log.Debug(
                   $"[AFC] MultiPassRegistry: dropped stale exec for " +
                   $"vehicle={key.Item2} of overwritten save '{newSaveId}'.");
       }

       foreach (MultiPassExecution exec in moved)
       {
           _byKey.Remove((oldSaveId, exec.VehicleId));
           exec.SaveId = newSaveId;
           _byKey[(newSaveId, exec.VehicleId)] = exec;

           if (MultiPassDebug.Enabled)
               DefaultCategory.Log.Debug(
                   $"[AFC] MultiPassRegistry: rekeyed exec for " +
                   $"vehicle={exec.VehicleId} from save='{oldSaveId}' to '{newSaveId}'.");
       }
   ```

6. **Write atomically and do not destroy what you could not read.** The writer skips creating a file when nothing would be persisted, never erases an unreadable file, writes a temporary file and moves it over the target:

   ```csharp
   // AdvancedFlightComputer/Features/MultiPass/MultiPassRegistry.cs L194-L235
   public static void Save()
   {
       if (string.IsNullOrEmpty(_configPath)) return;

       // Avoid creating a file when no saved execution exists and the feature has never written one.
       bool hasPersistable = false;
       foreach (var exec in _byKey.Values)
       {
           if (!string.IsNullOrEmpty(exec.SaveId)) { hasPersistable = true; break; }
       }
       // Do not erase an unreadable file when there are no entries to preserve.
       if (!hasPersistable && (!File.Exists(_configPath) || _lastLoadWasDefective))
           return;

       // Write a temporary file before replacement so an interrupted write leaves the previous file intact.
       string tempPath = _configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
       try
       {
           Directory.CreateDirectory(_modDir);
           using (var writer = new StreamWriter(tempPath))
           {
               int written = WriteToml(writer, _byKey.Values);
               if (MultiPassDebug.Enabled)
                   DefaultCategory.Log.Debug($"[AFC] MultiPassRegistry: saved {written} persistent entries");
           }

           File.Move(tempPath, _configPath, overwrite: true);
           _lastLoadWasDefective = false;
       }
       catch (Exception ex)
       {
           DefaultCategory.Log.Error(
               $"[AFC] MultiPassRegistry: failed to save {_configPath}: {ex}");

           // Cleanup must not hide the original write failure.
           try
           {
               if (File.Exists(tempPath)) File.Delete(tempPath);
           }
           catch { }
       }
   }
   ```

7. **Follow the vehicle, not just the save.** Entries are keyed by vehicle id, which changes on rename and must not be inherited after disposal. AFC adds a prefix/postfix pair on `Vehicle.SetName(string)` (moving entries only when the rename succeeded, via `__result`) and a postfix on `Vehicle.Dispose(bool)` (the bool overload, because EVA boarding calls it directly):

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

8. **Drop unsaved changes, like the game does.** Changes between saves stay in memory; quitting without saving drops them, exactly as the game drops unsaved burns.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Core/SaveLoadObserver.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SaveLoadObserver.cs) | Load and Write postfixes, save id, events |
| [AdvancedFlightComputer/Features/MultiPass/MultiPassRegistry.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassRegistry.cs) | `multipass.toml` registry: rekey, atomic save, tolerant hand-written TOML parser |
| [AdvancedFlightComputer/Core/SharedVehicleHooks.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SharedVehicleHooks.cs) | Rename and dispose hooks |
| [AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L117) | Where the observer patches are applied |

## Variations and gotchas

- **Hand-written TOML.** AFC ships no TOML package: writers emit `[[execution]]` tables with `CultureInfo.InvariantCulture` and round-trip `R` formatting for doubles, and the parser keeps readable blocks and reports dropped ones with file and line (see [Write config files atomically and load hand-edited ones without losing them](../../settings-formats/write-and-load-config-files-safely/overview.md)).
- **Path.** The file path comes from `Environment.SpecialFolder.MyDocuments`, so a StarMap instance-path override redirects the saves but not this file (see [Find where KSA keeps user data and choose where a mod stores its config and data files](../../user-data/choose-where-a-mod-stores-its-files/overview.md)).
- **Deleting a save leaves its entries** in the file; nothing prunes them. A new save created under the same name inherits the old entries because the key is the name. The `RekeyTo` drop of stale destination entries covers the overwrite case only.
- **Not travelling with the save.** Copying a save folder to another machine does not carry the sidecar; the benefit is that uninstalling the mod loses nothing in the saves.
- **Dispose hook.** A recycled vehicle id would otherwise inherit an old execution.

## See also

- [Keep a JSON sidecar inside the save folder and write it when universe.xml changes](save-mod-state-with-the-game-save-1.md), [Write a per-save settings store from a postfix on UncompressedSave.Make](save-mod-state-with-the-game-save-2.md), [overview](./overview.md)
- [Save-scoped TOML registries beside the mod](../../../../mods/ksa-advancedflightcomputer/features/save-scoped-sidecar-registries.md)
- [Identify vessels and parts stably across saves](../identify-vessels-and-parts-across-saves/overview.md), [Detect a save load or quickload and reset mod state safely](../detect-save-load/overview.md)
