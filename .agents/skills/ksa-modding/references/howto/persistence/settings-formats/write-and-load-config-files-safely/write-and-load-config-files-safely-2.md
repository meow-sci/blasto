---
type: How-To
title: Skip the dirty flag by comparing the serialised snapshot with the last write
description: Capture the live state into a DTO on a fixed Stopwatch interval, serialise it and write only when the string differs from the last one, so a clean load never writes and no UI code has to remember to mark anything dirty
category: persistence
group: settings-formats
topic: write-and-load-config-files-safely
approach: 2
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/kitten-engineer-redux/features/json-settings-autosave]
techniques: [json-config]
hooks: [harmony-postfix KSA.Vehicle.OnDrawUi, harmony-postfix KSA.VehicleEditor.OnDrawUi]
lifecycle: [frame:draw-ui, editor:draw-ui]
game_types: []
tags: [config, snapshot-compare, stopwatch, no-dirty-flag, autosave]
---

Outcome: the write policy with the least plumbing. Kitten Engineer Redux serialises a fresh snapshot of its settings every two seconds and writes the file only when the JSON differs from what it last wrote, so no widget has to call anything on change. Demonstrated by [JSON settings with autosave](../../../../mods/kitten-engineer-redux/features/json-settings-autosave.md); the full walkthrough (path, DTO, section registration) is [Autosave settings.json by comparing a serialised snapshot every two seconds](../persist-mod-settings-as-json/persist-mod-settings-as-json-1.md). This article keeps to the safety rules.

## Background: how the game does it

Only the frame loop matters: the tick runs inside the mod's draw call, on the main thread, and only on frames where that draw runs (HUD visible, vehicle UI or editor drawn). See the [overview](./overview.md#engine-background).

## Lifecycle and timing

`SettingsStore.Tick()` is the first statement of `FlightHud.Draw` ([FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L96-L101)) and of `EditorPanel.Draw`, in `frame:draw-ui` (the flight HUD, called from a Harmony postfix on `Vehicle.OnDrawUi` for the controlled vehicle in the main viewport) and `editor:draw-ui` (the editor panel, a postfix on `VehicleEditor.OnDrawUi`). The first call loads, then the timer gates saves to one per two seconds. Nothing is flushed at unload, so edits in the last two seconds before quitting are lost, and nothing is written while the HUD is hidden. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **One entry point, a fixed interval.** A `Stopwatch` restarts every time a save is due:

   ```csharp
   // SettingsStore.cs L62-L73
   public static void Tick()
   {
       if (!_loaded)
       {
           Load();
           return;
       }
       if (Timer.Elapsed.TotalSeconds < 2.0)
           return;
       Timer.Restart();
       Save();
   }
   ```

2. **Seed the comparison string at load.** `Load` records the JSON of the state after applying the file, so a clean load does not trigger a write:

   ```csharp
   // SettingsStore.cs L75-L95
   public static void Load()
   {
       try
       {
           if (File.Exists(SettingsPath))
           {
               SettingsData? data = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(SettingsPath), JsonOptions);
               if (data != null)
                   Apply(data);
           }
       }
       catch (Exception ex)
       {
           DefaultCategory.Log.Info($"[KER] Could not load settings: {ex.Message}");
       }
       finally
       {
           _loaded = true;
           _lastJson = JsonSerializer.Serialize(Capture(), JsonOptions);
       }
   }
   ```

3. **Compare before writing.** Serialise `Capture()`, return if equal to `_lastJson`, otherwise create the folder, write and remember. Log the first save error only:

   ```csharp
   // SettingsStore.cs L97-L119
   public static void Save()
   {
       if (!_loaded)
           return;

       try
       {
           string json = JsonSerializer.Serialize(Capture(), JsonOptions);
           if (json == _lastJson)
               return;
           Directory.CreateDirectory(BaseDirectory);
           File.WriteAllText(SettingsPath, json);
           _lastJson = json;
       }
       catch (Exception ex)
       {
           if (!_saveErrorLogged)
           {
               _saveErrorLogged = true;
               DefaultCategory.Log.Info($"[KER] Could not save settings: {ex.Message}");
           }
       }
   }
   ```

## Complete example

| File | Role |
|---|---|
| [SettingsStore.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SettingsStore.cs#L44-L119) | Path, tick, load, save |
| [FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L96-L101) | The call site |

## Variations and gotchas

- **Cost.** A few kilobytes of allocation every two seconds for the snapshot; fine for a small DTO, wasteful for a large one. A dirty flag ([Mark dirty, debounce, swap the file in atomically, and retry deserialisation by dropping the broken field](write-and-load-config-files-safely-1.md)) allocates nothing when idle.
- **Not atomic.** `File.WriteAllText` truncates then writes, so a crash mid-write leaves a short file; adopt the temp-and-move of approach 1 (it is two lines).
- **Load errors reset to defaults and the next change overwrites the file.** `Load` catches everything, logs one line and continues with defaults; `_lastJson` is then the defaults' JSON, so any later change replaces the unreadable file.
- **Fixed interval, not debounce.** A change made just after a tick waits up to two seconds; there is no "quiet" requirement, so a continuous drag writes every two seconds.
- **Folder choice** is a separate concern (this mod uses `Environment.SpecialFolder.MyDocuments`; see [Find where KSA keeps user data and choose where a mod stores its config and data files](../../user-data/choose-where-a-mod-stores-its-files/overview.md)).

## See also

- [Mark dirty, debounce, swap the file in atomically, and retry deserialisation by dropping the broken field](write-and-load-config-files-safely-1.md), [Save from Task.Run with an atomic helper, clamp bad values on load and leave an unparseable file alone](write-and-load-config-files-safely-3.md), [overview](./overview.md)
- [JSON settings with autosave](../../../../mods/kitten-engineer-redux/features/json-settings-autosave.md)
- [Autosave settings.json by comparing a serialised snapshot every two seconds](../persist-mod-settings-as-json/persist-mod-settings-as-json-1.md)
