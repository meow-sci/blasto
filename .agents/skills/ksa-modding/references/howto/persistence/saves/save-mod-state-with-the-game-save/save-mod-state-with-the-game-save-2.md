---
type: How-To
title: Write a per-save settings store from a postfix on UncompressedSave.Make
description: Track the current save id with postfixes on Load and Make and keep a generic store that reads every save folder's file and writes one atomically right after the game's own write
category: persistence
group: saves
topic: save-mod-state-with-the-game-save
approach: 2
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ohmystars/features/save-scoped-settings-sidecar
  - mods/ksa-advancedflightcomputer/features/save-scoped-sidecar-registries
techniques: [save-sidecar, harmony-postfix, toml-config]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-postfix KSA.UncompressedSave.Make
  - harmony-postfix KSA.UncompressedSave.Load
lifecycle: [startup:all-mods-loaded, event:save, event:load]
game_types: [KSA.UncompressedSave, KSA.GameSave, KSA.GameSaves, KSA.Constants]
tags: [saves, sidecar, per-save, harmony, atomic-write]
---

Outcome: settings that belong to a save are read when the save is loaded and written into that save's folder in the same instant the game writes the save. OhMyStars carries a generic version of this pattern (inherited from another mod); in this build its settings type has no fields, so the machinery runs but stores one blank block per save. Treat the article as a pattern reference. Demonstrated by [Save-scoped settings sidecar](../../../../mods/ohmystars/features/save-scoped-settings-sidecar.md) and, for the Write-postfix variant, [Save-scoped TOML registries beside the mod](../../../../mods/ksa-advancedflightcomputer/features/save-scoped-sidecar-registries.md).

## Background: how the game does it

`UncompressedSave.Make(name)` constructs the save, populates it and calls `Write()`, which replaces the save folder and writes `universe.xml`; it returns the `GameSave`, or `null` if the write failed ([current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L108-L146)). A postfix on `Make` therefore runs after the folder has been recreated, which is the only moment a file placed in it will survive. The returned save's `Id` is the sanitised name used as the folder name ([current/decomp/KSA/GameSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSave.cs#L7-L40)). `UncompressedSave.Load()` assigns a fresh `GameSave.UniverseData` and finishes with `Program.OnGameLoaded()` ([current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L45-L83)); the instance's `Id` names the loaded save.

## Lifecycle and timing

| When | What | Lifecycle |
|---|---|---|
| `StarMapAllModsLoaded` | `OhMyStarsSettingsStore.Init()`, `Load()` (reads every save folder's file), then `SaveLoadObserver.ApplyPatches` ([src/OhMyStarsEntryPoint.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsEntryPoint.cs#L13-L20)) | `startup:all-mods-loaded` |
| After the game wrote a save | `Make` postfix: set `CurrentSaveId`, write that save's file | `event:save` (main thread, inside whichever frame phase saved) |
| After a load | `Load` postfix: set `CurrentSaveId`, make that save's settings current | `event:load` |

Both postfixes run synchronously inside the game's call; see the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md). Keep the work small and wrap it in `try`/`catch`: an exception from a postfix propagates into the save or load.

## Steps

1. **Patch both entry points and track the id.** The `Make` postfix takes the id from the returned save and falls back to the typed name; the `Load` postfix takes it from the instance:

   ```csharp
   // src/SaveLoadObserver.cs L7-L56
   internal static class SaveLoadObserver {
       /// <summary>
       /// Save id of the most recently loaded or written save.
       /// Empty when no save is loaded yet.
       /// </summary>
       public static string CurrentSaveId { get; private set; } = string.Empty;

       public static void Reset() {
           CurrentSaveId = string.Empty;
           OhMyStarsWindow.ClearOrientedStar();
       }

       public static void ApplyPatches(Harmony harmony) {
           harmony.CreateClassProcessor(typeof(LoadPatch)).Patch();
           harmony.CreateClassProcessor(typeof(MakePatch)).Patch();
       }

       [HarmonyPatch(typeof(UncompressedSave), nameof(UncompressedSave.Load))]
       private static class LoadPatch {
           static void Postfix(UncompressedSave __instance) {
               try {
                   CurrentSaveId = __instance.Id ?? string.Empty;

                   OhMyStarsSettingsStore.LoadForSave(CurrentSaveId);
               } catch(Exception ex) {
                   DefaultCategory.Log.Warning(
                       $"[OhMyStars] SaveLoadObserver Load Postfix: {ex}");
               }
           }
       }

       [HarmonyPatch(typeof(UncompressedSave), nameof(UncompressedSave.Make))]
       private static class MakePatch {
           static void Postfix(string name, GameSave __result) {
               try {
                   string newSaveId = __result?.Id ?? name ?? string.Empty;

                   if(string.IsNullOrEmpty(newSaveId))
                       return;

                   CurrentSaveId = newSaveId;

                   OhMyStarsSettingsStore.SaveForSave(newSaveId);
               } catch(Exception ex) {
                   DefaultCategory.Log.Warning(
                       $"[OhMyStars] SaveLoadObserver Make Postfix: {ex}");
               }
           }
       }
   }
   ```

   The `__result` parameter is the return value of `Make`; it is `null` when the write failed, in which case the fallback `name` may name a save that does not exist (the AFC variant avoids this, below).

2. **Build a generic store keyed by save id.** The store takes the saves directory, a file name and read/write delegates, so any settings type can be plugged in:

   ```csharp
   // src/SaveScopedSettingsStore.cs L7-L36
   internal sealed class SaveScopedSettingsStore<TSettings> {
       private readonly Dictionary<string, TSettings> _bySaveId = new();

       private readonly string _fileName;
       private readonly string _savesDir;
       private readonly Func<TSettings> _createDefault;
       private readonly Func<SettingsBlock, TSettings> _readBlock;
       private readonly Action<SettingsBlockWriter, string, TSettings> _writeBlock;

       public SaveScopedSettingsStore(
           string savesDir,
           string fileName,
           Func<TSettings> createDefault,
           Func<SettingsBlock, TSettings> readBlock,
           Action<SettingsBlockWriter, string, TSettings> writeBlock) {
           _savesDir = savesDir;
           _fileName = fileName;
           _createDefault = createDefault;
           _readBlock = readBlock;
           _writeBlock = writeBlock;
       }

       public TSettings GetCurrent(string saveId) {
           if(!_bySaveId.TryGetValue(saveId, out TSettings? settings)) {
               settings = _createDefault();
               _bySaveId[saveId] = settings;
           }

           return settings;
       }
   ```

3. **Read every save folder's file at startup.** `Load` enumerates the saves directory and reads `<saves>/<saveId>/<file>` where present, keying by the folder name rather than any id inside the file:

   ```csharp
   // src/SaveScopedSettingsStore.cs L53-L75
   public void Load() {
       _bySaveId.Clear();

       if(!Directory.Exists(_savesDir))
           return;

       foreach(string saveDir in Directory.EnumerateDirectories(_savesDir)) {
           string saveId = Path.GetFileName(saveDir);
           if(string.IsNullOrEmpty(saveId))
               continue;

           string configPath = GetConfigPath(saveId);
           if(!File.Exists(configPath))
               continue;

           foreach(SettingsBlock block in SettingsBlockFile.Read(configPath)) {
               // Since the file is now already scoped to a save directory,
               // prefer the directory saveId. This also tolerates old/missing SaveId values.
               _bySaveId[saveId] = _readBlock(block);
               break;
           }
       }
   }
   ```

4. **Write one save's file atomically.** `SaveOne` writes `<file>.tmp` and moves it over the target, deleting the temp on failure. `Directory.CreateDirectory` is harmless after the game's write because the folder already exists:

   ```csharp
   // src/SaveScopedSettingsStore.cs L98-L125
   private void SaveOne(string saveId, TSettings settings) {
       string configPath = GetConfigPath(saveId);
       string saveDir = Path.GetDirectoryName(configPath)!;
       string tempPath = configPath + ".tmp";

       try {
           Directory.CreateDirectory(saveDir);

           using(var writer = new StreamWriter(tempPath)) {
               var blockWriter = new SettingsBlockWriter(writer);

               // Keep writing the saveId into the block if your existing parser/writer format expects it.
               // Even though the file is now save-scoped, this preserves compatibility.
               _writeBlock(blockWriter, saveId, settings);
           }

           File.Move(tempPath, configPath, overwrite: true);
       } catch {
           if(File.Exists(tempPath))
               File.Delete(tempPath);

           throw;
       }
   }

   private string GetConfigPath(string saveId) {
       return Path.Combine(_savesDir, saveId, _fileName);
   }
   ```

5. **Wire the delegates.** `LoadForSave` makes a clone of that save's settings current; `SaveForSave` stores a clone and writes that save:

   ```csharp
   // src/Settings.cs L23-L43
   public static void LoadForSave(string saveId) {
       EnsureInitialized();
       
       if(string.IsNullOrEmpty(saveId)) {
           _current = new OhMyStarsSettings();
           return;
       }
       
       _store!.Load();
       _current = _store.GetCurrent(saveId).Clone();
   }

   public static void SaveForSave(string saveId) {
       EnsureInitialized();

       if(string.IsNullOrEmpty(saveId))
           return;

       _store!.Set(saveId, _current.Clone());
       _store.Save(saveId);
   }
   ```

   and the one-time construction, which fixes the saves directory and the file name:

   ```csharp
   // src/Settings.cs L54-L69
   public static void Init() {
       string userDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

       string savesDir = Path.Combine(
           userDocs,
           "My Games",
           "Kitten Space Agency",
           "saves");

       _store = new SaveScopedSettingsStore<OhMyStarsSettings>(
           savesDir,
           "OhMyStars_settings.toml",
           () => new OhMyStarsSettings(),
           OhMyStarsSettingsToml.Read,
           OhMyStarsSettingsToml.Write);
   }
   ```

6. **Re-key transient state on save-as.** A session that had never been saved has an empty id; `RekeyTransientTo(newSaveId)` moves the empty-key entry to the new id ([src/SaveScopedSettingsStore.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SaveScopedSettingsStore.cs#L42-L51)), so the first save adopts the live settings.

### Make versus Write postfix

OhMyStars patches `UncompressedSave.Make`, a `static` method that returns the save. KSA-AdvancedFlightComputer patches the instance method `UncompressedSave.Write` instead and acts only when it returned `true`:

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

`Write` is called only from `Make` in this build, so both see every save. Choose by what you need: `Make` gives you the returned save and the requested name; `Write` has the success flag directly and runs also if the game ever writes an existing instance without `Make`. A `Make` postfix should treat a `null` result as "nothing was written" and skip the write (OhMyStars falls back to `name` instead; with a failed write it would create a folder for a save the game does not list).

## Complete example

| File | Role |
|---|---|
| [src/SaveLoadObserver.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SaveLoadObserver.cs) | Patches and current save id |
| [src/SaveScopedSettingsStore.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SaveScopedSettingsStore.cs) | Generic per-save store, atomic write, hand-written TOML-like blocks |
| [src/Settings.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/Settings.cs#L12-L96) | Concrete settings type and wiring (empty in this version) |
| [src/OhMyStarsEntryPoint.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsEntryPoint.cs#L13-L20) | Startup order: init, load, patch |

## Variations and gotchas

- **Instance paths.** The saves directory is built from `Environment.SpecialFolder.MyDocuments` plus `My Games/Kitten Space Agency/saves`. That equals `GameSaves.SaveFolderPath` by default, but not under a StarMap instance-path override, which redirects `Constants.DocumentsFolderPath` ([current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281)). Prefer `GameSaves.SaveFolderPath` (see [Find where KSA keeps user data and choose where a mod stores its config and data files](../../user-data/choose-where-a-mod-stores-its-files/overview.md)).
- **The Load postfix runs for refused and failed loads too**, so `CurrentSaveId` changes even if nothing loaded. Add the `UniverseData` identity check from [Observe UncompressedSave.Load and reset a registry of save-lifetime caches](../detect-save-load/detect-save-load-3.md) when that matters.
- **Overwrites go through `Make`** (`UncompressedSave.Overwrite` calls `Make(base.Id)`), so an overwrite rewrites the sidecar like any save.
- **Per-save files travel with the save only if they are in its folder.** Keeping the file in the mod folder instead is approach [Keep TOML in the mod folder keyed by (save id, vehicle id) and rekey it on save-as](save-mod-state-with-the-game-save-3.md).

## See also

- [Keep a JSON sidecar inside the save folder and write it when universe.xml changes](save-mod-state-with-the-game-save-1.md), [Keep TOML in the mod folder keyed by (save id, vehicle id) and rekey it on save-as](save-mod-state-with-the-game-save-3.md), [overview](./overview.md)
- [Save-scoped settings sidecar](../../../../mods/ohmystars/features/save-scoped-settings-sidecar.md)
- [Detect a save load or quickload and reset mod state safely](../detect-save-load/overview.md), [Write config files atomically and load hand-edited ones without losing them](../../settings-formats/write-and-load-config-files-safely/overview.md)
