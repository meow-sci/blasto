---
type: Mod Feature
title: Per-craft settings as a JSON sidecar inside the save folder
description: Each weapon system's switches are flattened into a versioned-by-defaults DTO and kept in saves/<save>/KSArmory/systems.json keyed by craft name, read when a system is crewed and written only when universe.xml's timestamp shows the game saved
mod: ksarmory
category: persistence
techniques: [json-config, save-sidecar, file-timestamp-polling]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame, event:save]
game_types: [KSA.GameSaves, KSA.GameSave, KSA.UncompressedSave, KSA.SaveDirectory, KSA.UniverseData, KSA.Vehicle]
tags: [settings, json, save-sidecar, system-text-json, persistence, save-detection, file-timestamp]
---

KSA's save format has no room for a mod (`UniverseData` is a fixed XML-mapped class) and StarMap has
no save or load hook. KSArmory still restores each weapon system's switches (auto-engage, arming,
turret mode, team and IFF rules, scope range) with the save they were set in, by writing a small
JSON file into the save's own folder: `saves/<save id>/KSArmory/systems.json`. Deleting, copying or
renaming a save in the file system carries the settings with it. A session with no save open uses a
loose file in the KSA user folder, which the first save that opens adopts.[^ksarmory-settingsstore]

## How it works

| Piece | Role |
|---|---|
| `SystemConfig` | What the panel edits and fire control reads; one per weapon system, grows freely[^ksarmory-systemconfig] |
| `SystemSettings` | Flat DTO of plain properties whose defaults equal `SystemConfig`'s, so a file from an older version loads new fields at their defaults; `From(config)`, `ApplyTo(config)`, `Differs(other)`, `DeclareTeams(names)`[^ksarmory-systemsettings] |
| `Config` | Session-wide settings (team names, drawing, sound, warp limiting); not persisted by this store[^ksarmory-config] |
| `SettingsStore` | `Dictionary<string, SystemSettings>` serialized with `System.Text.Json` (`WriteIndented`), re-read whenever the resolved path changes |

Keys and paths:

- **Key** = the craft's display name (`Vehicle.Id`); a second launcher on the same craft is
  `"Name#2"`, and so on. Craft built from one blueprint share a name and therefore an entry; the
  roster warns when it crews the second one.[^ksarmory-weaponsystems]
- **Scope** = `GameSaves.Selected?.Id`; the path is `Path.Combine(GameSaves.SaveFolderPath, id,
  "KSArmory", "systems.json")` when that folder exists.[^ksarmory-ksaworld]
- **Loose path** = parent of the mod's log folder + `KSArmory/systems.json`, i.e. beside `saves/`.
- `Save()` refuses to create a save folder that does not exist, so a deleted save is not resurrected
  as a folder holding only this file. Every read or write failure is logged and swallowed.

When it reads and writes:

| Event | Code | Effect |
|---|---|---|
| A launcher is crewed | `WeaponSystems.Sync` -> `SettingsStore.For(key)` | `ApplyTo` the new `SystemConfig` before the system exists; re-declare its team names on the session roster; adopt the stored rules |
| Every step (end of `StepSimulation`) | `WeaponSystems.Remember()` | If the scope changed: `Adopt()` (re-read for live systems), no write. Else read `universe.xml`'s last-write time (`KsaWorld.CurrentSaveStamp`); when it changes (after the first sighting), `Remember` each live system and `Save()` if any differ |
| Panel "Reset settings" | `SettingsStore.Forget(name)` then defaults and `WriteNow()` | Clears the bare-name entry |
| Unload or fault | `WeaponSystems.Clear()` -> `WriteNow()` | Writes regardless of the save stamp |
| Save load | `WeaponSystems.Discard()` | No write: the file now belongs to the save being opened |

Writing only when the game saves is deliberate: a continuous write would bring the file up to date
with the session, and reloading the save would then restore nothing.

## Lifecycle and timing

All of it runs on the main thread inside the mod's StarMap hooks, never inside the engine's save
path:

- **Read**: `WeaponSystems.Sync` is called from `Ui.Draw`, inside `[StarMapAfterGui]` (Program.cs
  L2268), after `StepOnce`. With the HUD hidden it does not run, so no new system is crewed and no
  settings are read until it returns.[^ksarmory-ui]
- **Write check**: `_roster.Remember()` is the last line of `StepSimulation` (KSArmoryMod.cs L984),
  so it runs wherever the frame's step runs: GUI postfix, `OnFrameCelestials` prefix, or OnFrame
  postfix.[^ksarmory-mod]
- **KSA's save**: `UncompressedSave.Write()` first calls `SaveDirectory.TryReplace`, which deletes
  the save folder recursively and recreates it, then writes metadata and `universe.xml`
  (UncompressedSave.cs L108-L118; SaveDirectory.cs L21-L24, L95-L109; UniverseData.cs L62).[^ksa-uncompressedsave][^ksa-savedirectory][^ksa-universedata]
  The mod notices on the next `Remember()` after the write, at most one frame later.
- `GameSaves.SaveFolderPath` is `Constants.DocumentsFolderPath + "saves"` (GameSaves.cs L124;
  Constants.cs L281), so it follows StarMap's `-InstancePath` override.[^ksa-gamesaves][^ksa-constants]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/SettingsStore.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/SettingsStore.cs) | Path resolution, lazy load, adopt-loose, write, forget |
| [src/KSArmory/Sim/SystemSettings.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/SystemSettings.cs) | The persisted DTO |
| [src/KSArmory/Ksa/WeaponSystems.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystems.cs#L459-L480) | Restore on crewing |
| [src/KSArmory/Ksa/WeaponSystems.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystems.cs#L786-L911) | `SettingsKey`, `Remember`, `Adopt`, `WriteNow`, `Clear`, `Discard` |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L1720-L1841) | `TrySaveFolder`, `CurrentSaveStamp`, `CurrentSaveId` |
| [src/KSArmory/Ksa/Ui/UiSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiSystem.cs#L419-L428) | "Reset settings" button[^ksarmory-uisystem] |

## Snippets

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

```csharp
// current/decomp/KSA/UncompressedSave.cs L108-L118
	public override bool Write()
	{
		if (!SaveDirectory.TryReplace(Directory, GameSaves.SaveFolderPath, "save", base.Id))
		{
			return false;
		}
		try
		{
			MetaData.Write();
			UniverseData.WriteTo(Directory);
		}
```

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `GameSaves.Selected`, `GameSaves.SaveFolderPath`, `GameSaves.OnSelected` | [current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L124-L132) | Which save the settings belong to, and where it lives |
| `UncompressedSave.Write`, `UncompressedSave.DrawInTable` | [current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L108-L139) | How a save is written; where `Selected` is set (L228-L230) |
| `SaveDirectory.TryReplace` | [current/decomp/KSA/SaveDirectory.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SaveDirectory.cs#L21-L24) | Deletes and recreates the save folder on every write |
| `UniverseData.WriteTo` (`universe.xml`) | [current/decomp/KSA/UniverseData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UniverseData.cs#L22) | The file whose timestamp is watched |
| `Vehicle.Id` | (via `KsaWorld.DisplayName`) | Settings key |

## Pitfalls and version notes

- **KSA wipes the save folder on every save (5554).** `TryReplace` deletes `saves/<id>/` recursively,
  `KSArmory/systems.json` included. The mod rewrites the file only if some live system's settings
  differ from what it last stored, so saving over a save with nothing changed since the last write
  leaves no sidecar on disk; in-session reloads still restore from the in-memory copy, a restart
  does not. (Derived from the code and the decomp; not flown.)
- **`GameSaves.Selected` is the browser's highlighted row, not the loaded save.** It is set only by
  clicking a row (UncompressedSave.cs L228-L230 -> GameSaves.cs L327-L330). A console `load`, a
  "save as" under a new name, or a single click on another row without loading all leave it
  pointing elsewhere; the click case makes `Remember()` see a new scope and `Adopt()` the other
  save's settings onto the live systems. The mod's own comment acknowledges it is "not a guaranteed
  currently loaded pointer".
- **Names collide.** Every craft named alike shares one entry, and the last write wins.
- **"Reset settings" forgets only the bare name**, not the `#2`, `#3` keys of other launchers on the
  same craft.[^ksarmory-uisystem]
- **The loose bucket's location comes from the log folder guess**, not from KSA's
  `Constants.DocumentsFolderPath`; see [own log file](./own-log-file-and-developer-switch.md).

## See also

- [Detect a save load via OnGameLoaded](./detect-save-load-ongameloaded.md)
- [Own log file and developer switch](./own-log-file-and-developer-switch.md)
- [Startup save repair](./startup-save-repair.md)
- [Operator panel (ImGui)](./operator-panel-imgui.md)
- [IFF teams and allegiance](./iff-teams-and-allegiance.md)
- [Key settings on the craft display name and warn when two craft share it](../../../howto/persistence/saves/identify-vessels-and-parts-across-saves/identify-vessels-and-parts-across-saves-2.md) - how-to article demonstrating this feature
- [Keep a JSON sidecar inside the save folder and write it when universe.xml changes](../../../howto/persistence/saves/save-mod-state-with-the-game-save/save-mod-state-with-the-game-save-1.md) - how-to article demonstrating this feature

[^ksarmory-settingsstore]: src/KSArmory/Ksa/SettingsStore.cs
[^ksarmory-systemsettings]: src/KSArmory/Sim/SystemSettings.cs
[^ksarmory-systemconfig]: src/KSArmory/Sim/SystemConfig.cs
[^ksarmory-config]: src/KSArmory/Sim/Config.cs
[^ksarmory-weaponsystems]: src/KSArmory/Ksa/WeaponSystems.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-ui]: src/KSArmory/Ksa/Ui/Ui.cs
[^ksarmory-uisystem]: src/KSArmory/Ksa/Ui/UiSystem.cs
[^ksa-gamesaves]: KSA.GameSaves (decompiled)
[^ksa-uncompressedsave]: KSA.UncompressedSave (decompiled)
[^ksa-savedirectory]: KSA.SaveDirectory (decompiled)
[^ksa-universedata]: KSA.UniverseData (decompiled)
[^ksa-constants]: KSA.Constants (decompiled)
