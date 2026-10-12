---
type: How-To
title: Keep a JSON sidecar inside the save folder and write it when universe.xml changes
description: Store per-craft settings in saves/<id>/<Mod>/systems.json, adopt a loose file on the first save, never create a missing save folder, and poll the universe.xml write time to learn that the game saved
category: persistence
group: saves
topic: save-mod-state-with-the-game-save
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/per-craft-settings-json-sidecar]
techniques: [save-sidecar, json-config]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, event:save, event:load]
game_types: [KSA.GameSaves, KSA.GameSave, KSA.UncompressedSave, KSA.SaveDirectory, KSA.UniverseData]
tags: [saves, sidecar, json, save-folder, universe-xml, polling]
---

Outcome: per-craft mod settings stored at `saves/<save id>/KSArmory/systems.json`, so deleting, copying or renaming a save in the file system carries them along, and loading a save restores the settings as they were when it was saved. KSArmory does this without a Harmony patch: it notices that the game wrote a save by watching the `universe.xml` timestamp. Prerequisite: a StarMap mod with a per-frame step. Demonstrated by [Per-craft settings as a JSON sidecar inside the save folder](../../../../mods/ksarmory/features/per-craft-settings-json-sidecar.md).

## Background: how the game does it

`UncompressedSave.Write` replaces the save folder, then writes the metadata and `universe.xml` ([current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L108-L118), [current/decomp/KSA/SaveDirectory.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SaveDirectory.cs#L21-L24)). The folder of save `X` is `Path.Combine(GameSaves.SaveFolderPath, "X")`; `GameSaves.SaveFolderPath` derives from `Constants.DocumentsFolderPath`, so it follows an instance-path override (see [Find where KSA keeps user data and choose where a mod stores its config and data files](../../user-data/choose-where-a-mod-stores-its-files/overview.md)). `universe.xml` is written by `UniverseData.WriteTo` ([current/decomp/KSA/UniverseData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UniverseData.cs#L60-L62)) after the folder is recreated, so its last-write time moves on every save, including an overwrite of the same name.

The consequence for any file placed inside the save folder: the next save of that name deletes it. This design only works because the mod rewrites the file after the game's write.

## Lifecycle and timing

All of it runs on the main thread in the mod's own StarMap hooks, never inside the engine's save path:

| Piece | Where | Lifecycle |
|---|---|---|
| Read settings for a newly crewed system | `WeaponSystems.Sync`, called from the UI pass (`[StarMapAfterGui]`) | `frame:draw-ui` |
| Check whether the game saved | `WeaponSystems.Remember()`, the last line of the mod's per-frame step | `frame:draw-ui`, or the frame postfix on HUD-hidden frames |
| Write the sidecar | Inside `Remember()`, only when the stamp changed and a system differs from what is stored | one step after the game's write |
| A load | `WeaponSystems.Discard()`, no write (see [Detect a save load or quickload and reset mod state safely](../detect-save-load/overview.md)) | `event:load` |

The one-step delay is inherent: the mod sees the write on the next step after it happened (at most one frame later). A save made while the HUD is hidden is noticed once the step runs again. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for the frame order.

## Steps

1. **Define a flat DTO with defaults.** `SystemSettings` holds plain properties whose defaults equal the live config's, so a file written by an older version loads new fields at their defaults. KSArmory serialises a `Dictionary<string, SystemSettings>` with `System.Text.Json` (`WriteIndented`) ([src/KSArmory/Ksa/SettingsStore.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/SettingsStore.cs#L43)).

2. **Choose the key.** The craft's display name (`Vehicle.Id`), with `#2`, `#3` for a second launcher on the same craft. Craft built from one blueprint share a name and therefore an entry; the roster warns when it crews the second one. The reason, from the store's own comment: a `Vehicle` reference does not survive a save, and the id is the only thing that does (see [Identify vessels and parts stably across saves](../identify-vessels-and-parts-across-saves/overview.md)).

3. **Resolve the path.** Inside a save: `saves/<id>/KSArmory/systems.json`; with no save open, a loose file beside `saves/`:

   ```csharp
   // src/KSArmory/Ksa/SettingsStore.cs L167-L189
   private static bool InSave() => CurrentScope != NoSave;

   private static string SaveRoot()
       => KsaWorld.TrySaveFolder(KsaWorld.CurrentSaveId(), out string folder) ? folder : string.Empty;

   private static string Path()
   {
       if (InSave() && SaveRoot() is { Length: > 0 } save)
       {
           return System.IO.Path.Combine(save, ModFolderName, FileName);
       }

       return LoosePath();
   }

   // Where settings go with no save to put them in: the KSA user directory, beside saves/ and
   // vehicles/. Not Logs/, which is a folder people clear out.
   private static string LoosePath()
   {
       string logs = Log.Folder;
       string root = Directory.GetParent(logs)?.FullName ?? logs;
       return System.IO.Path.Combine(root, ModFolderName, FileName);
   }
   ```

   The save id is `GameSaves.Selected?.Id` and the folder test refuses ids with no folder on disk:

   ```csharp
   // src/KSArmory/Ksa/KsaWorld.cs L1721-L1738
   public static bool TrySaveFolder(string saveId, out string folder)
   {
       folder = string.Empty;
       if (string.IsNullOrWhiteSpace(saveId)) return false;

       try
       {
           string path = Path.Combine(GameSaves.SaveFolderPath, saveId);
           if (!Directory.Exists(path)) return false;

           folder = path;
           return true;
       }
       catch
       {
           return false;
       }
   }
   ```

   ```csharp
   // src/KSArmory/Ksa/KsaWorld.cs L1828-L1838
   public static string CurrentSaveId()
   {
       try
       {
           return GameSaves.Selected?.Id ?? string.Empty;
       }
       catch
       {
           return string.Empty;
       }
   }
   ```

   `GameSaves.Selected` is not a reliable "loaded save" pointer (it is the browser's highlighted row), as the mod's own comment concedes. A mod that needs certainty patches `UncompressedSave.Load` instead ([Write a per-save settings store from a postfix on UncompressedSave.Make](save-mod-state-with-the-game-save-2.md), [Keep TOML in the mod folder keyed by (save id, vehicle id) and rekey it on save-as](save-mod-state-with-the-game-save-3.md)).

4. **Load lazily and adopt the loose file into a new save.** `Load()` re-reads whenever the resolved path changes; a save with no file yet starts from whatever the session had set:

   ```csharp
   // src/KSArmory/Ksa/SettingsStore.cs L116-L147
   // Reads the file for whichever save is open, unless that is the one already in memory.
   private static void Load()
   {
       string path = Path();
       if (path == _loadedFrom) return;

       _loadedFrom = path;
       _stored = [];

       try
       {
           if (File.Exists(path))
           {
               _stored = ReadOrEmpty(path);
               Log.Info($"settings: loaded {_stored.Count} system(s) from {path}");
               return;
           }

           // A save that has never had settings written adopts whatever the session had set
           // before it was opened.
           if (InSave())
           {
               _stored = ReadOrEmpty(LoosePath());
               Log.Info($"settings: {path} is new; carrying {_stored.Count} system(s) in");
           }
       }
       catch (Exception e)
       {
           _stored = [];
           Log.Warn($"settings: could not read {path} ({e.Message}); starting from defaults");
       }
   }
   ```

5. **Apply stored settings before the object exists.** When a launcher is crewed, restore its settings first so its first frame runs on them:

   ```csharp
   // src/KSArmory/Ksa/WeaponSystems.cs L465-L480
   SystemConfig policy = new();

   // Whatever this weapon was last set to. Applied before the system exists so its
   // first frame runs on the restored settings rather than on defaults it then
   // overwrites.
   if (SettingsStore.For(SettingsKey(craft, ordinal)) is { } stored)
   {
       stored.ApplyTo(policy);

       // And put its teams back on the session's roster. Memberships are stored per
       // system and the names are session-wide, so restoring only the first half
       // leaves every system sure of its allegiance in a world that has forgotten the
       // teams exist. Every contact is then Unknown, which is engageable by default.
       stored.DeclareTeams(_config.TeamNames);
       _config.AdoptRules(policy.Iff);
   }
   ```

6. **Save only when the store changed, and never create a missing save folder.** A deleted save must not be resurrected as a folder holding only the sidecar:

   ```csharp
   // src/KSArmory/Ksa/SettingsStore.cs L93-L114
   public static void Save()
   {
       string path = Path();
       try
       {
           if (System.IO.Path.GetDirectoryName(path) is { Length: > 0 } folder)
           {
               // A save's own folder must already exist. Creating it would resurrect a save the
               // player deleted, as a directory containing nothing but these settings.
               if (InSave() && !Directory.Exists(SaveRoot())) return;

               Directory.CreateDirectory(folder);
           }

           File.WriteAllText(path, JsonSerializer.Serialize(_stored, Options));
           _loadedFrom = path;
       }
       catch (Exception e)
       {
           Log.Warn($"settings: could not write {path} ({e.Message})");
       }
   }
   ```

7. **Detect the game's save by polling `universe.xml`.** StarMap has no save hook, so the file's own timestamp is the event:

   ```csharp
   // src/KSArmory/Ksa/KsaWorld.cs L1749-L1765
   public static long CurrentSaveStamp()
   {
       try
       {
           if (GameSaves.Selected?.Id is not { Length: > 0 } id) return 0;

           string folder = Path.Combine(GameSaves.SaveFolderPath, id);
           string universe = Path.Combine(folder, "universe.xml");

           if (File.Exists(universe)) return File.GetLastWriteTimeUtc(universe).Ticks;
           return Directory.Exists(folder) ? Directory.GetLastWriteTimeUtc(folder).Ticks : 0;
       }
       catch
       {
           return 0;
       }
   }
   ```

   The first sighting only records the stamp ("the first stamp seen is the save as it already was, not the player saving"); a later change triggers the write:

   ```csharp
   // src/KSArmory/Ksa/WeaponSystems.cs L811-L850
   public void Remember()
   {
       string scope = SettingsStore.CurrentScope;
       if (_scope.Length == 0)
       {
           // First pass: Sync has already applied whatever this bucket holds.
           _scope = scope;
       }
       else if (scope != _scope)
       {
           // A different save is open. Adopt what it says instead of writing over it.
           _scope = scope;
           _savedAt = KsaWorld.CurrentSaveStamp();
           Adopt();
           return;
       }

       // Only when the game saves. A load then finds the settings as they were at that moment,
       // because nothing has been written over them since.
       long stamp = KsaWorld.CurrentSaveStamp();
       if (stamp == 0 || stamp == _savedAt) return;

       bool firstSight = _savedAt == 0;
       _savedAt = stamp;

       // The first stamp seen is the save as it already was, not the player saving.
       if (firstSight) return;

       Log.Info("settings: the game saved, writing the systems' settings with it");

       bool changed = false;
       foreach (KeyValuePair<(Vehicle Craft, int Ordinal), Entry> kv in _entries)
       {
           if (!KsaWorld.IsAlive(kv.Key.Craft)) continue;
           changed |= SettingsStore.Remember(SettingsKey(kv.Key.Craft, kv.Key.Ordinal),
                                             kv.Value.Policy);
       }

       if (changed) SettingsStore.Save();
   }
   ```

   Why not write on every change? A continuous write would bring the file up to date with the session, so reloading the save would restore nothing. Writing only when the game writes keeps the file equal to the save.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/SettingsStore.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/SettingsStore.cs) | Path resolution, lazy load, adopt-loose, write, forget |
| [src/KSArmory/Ksa/WeaponSystems.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystems.cs#L459-L480) | Restore on crewing |
| [src/KSArmory/Ksa/WeaponSystems.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystems.cs#L786-L911) | Key, `Remember`, `Adopt`, `WriteNow`, `Clear`, `Discard` |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L1720-L1841) | `TrySaveFolder`, `CurrentSaveStamp`, `CurrentSaveId` |

## Variations and gotchas

- **The wipe.** Because KSA recreates the folder on every save, the sidecar exists after a save only if the mod rewrote it. `Remember` writes only when some live system's settings differ from what is stored (`SettingsStore.Remember` returns false otherwise), so saving over a save when nothing changed since the last write leaves no sidecar on disk. In-session reloads still restore from the in-memory copy; a restart does not. This follows from reading `SettingsStore.cs` and the decomp; it was not flown. A more robust rule is to write on every observed save.
- **A deleted save is not resurrected**: `Save()` returns without writing when the save's folder does not exist.
- **The timestamp depends on the file system clock.** An unchanged or low-resolution stamp (or a save restored with an old timestamp) is missed.
- **Reset to defaults** forgets only the bare-name key, not `#2`, `#3`.
- **Names collide.** The last save wins.
- **Do not write on load.** The file belongs to the save being opened (`Discard`, not `Clear`).

## See also

- [Write a per-save settings store from a postfix on UncompressedSave.Make](save-mod-state-with-the-game-save-2.md), [Keep TOML in the mod folder keyed by (save id, vehicle id) and rekey it on save-as](save-mod-state-with-the-game-save-3.md), [overview](./overview.md)
- [Per-craft settings as a JSON sidecar inside the save folder](../../../../mods/ksarmory/features/per-craft-settings-json-sidecar.md)
- [Detect a save load or quickload and reset mod state safely](../detect-save-load/overview.md), [Write config files atomically and load hand-edited ones without losing them](../../settings-formats/write-and-load-config-files-safely/overview.md)
