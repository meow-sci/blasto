---
type: Mod Feature
title: JSON settings with autosave
description: Lazily loads a System.Text.Json settings file from Documents on the first UI frame and re-serialises the live static state at most every two seconds, writing only when the JSON text changed, with late-registered UI sections picking up their saved state on construction
mod: kitten-engineer-redux
category: persistence
techniques: [json-config]
hooks: [harmony-postfix KSA.Vehicle.OnDrawUi, harmony-postfix KSA.VehicleEditor.OnDrawUi]
lifecycle: [frame:draw-ui, editor:draw-ui]
game_types: [Brutal.Logging.DefaultCategory]
tags: [settings, json, persistence, autosave, documents, system-text-json]
---

Every visible preference in KER survives a restart: theme colours, corner radius, text scale, the
chosen stat tiles, alert rules, which sections are open or popped out, the graph metric, panel
visibility, the flight HUD's analysis toggles and the planner's target altitude. There is no save
button and no hook into the game's save system; the mod polls its own static fields and writes a
JSON file when they change.

## How it works

- **Location.** `Documents/My Games/Kitten Space Agency/KittenEngineerReduxData/settings.json`
  (via `Environment.SpecialFolder.MyDocuments`), with CSV exports in an `exports` subfolder
  ([SettingsStore.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SettingsStore.cs#L44-L60)).[^ker-settingsstore]
- **Schema.** `SettingsData` is a plain class with public auto-properties: a `Dictionary<string, float[]>`
  of RGBA colours keyed by role, scalars, `List<string>` of tile enum names, a list of
  `AlertRuleData`, and a `Dictionary<string, SectionStateData>` of `{Open, Popped}` keyed by section id
  ([SettingsStore.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SettingsStore.cs#L13-L42)).
- **Tick.** `SettingsStore.Tick()` is the only entry point. The first call loads; later calls return
  until a `Stopwatch` passes 2 s, then restart it and `Save()` ([SettingsStore.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SettingsStore.cs#L62-L73)).
- **Change detection.** `Save` serialises a fresh snapshot (`Capture()`) and compares the string with
  the last written one; identical JSON is not written. `Load` seeds `_lastJson` from the state after
  applying the file, so a clean load never triggers a write.
- **Apply.** Colours fall back to the current value when a key is missing or short, tile and alert
  names are parsed back into enums and unknown ones skipped, so older or hand-edited files load.
- **Section state for late registrations.** Loaded section states are kept in a dictionary.
  `HudSection` instances register in their constructors, which run from the static field
  initialisers of `FlightHud` and `EditorPanel` when .NET first initialises each class (first draw,
  the menu toggles, or `Load` itself touching `FlightHud`/`EditorPanel` properties), so their order
  relative to `Load` is not fixed. `SectionRegistry.Register` calls `SettingsStore.ApplySection`, and
  `Apply` re-applies to every registered section, so a section gets its saved state whichever comes
  first. `Capture` writes the loaded dictionary first and then overlays the
  registered sections, so sections not constructed this session keep their saved state
  ([SectionHost.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SectionHost.cs#L31-L40)).[^ker-sectionhost]
- **Errors.** Load errors are logged once through `DefaultCategory.Log.Info` and ignored; the first
  save error is logged and later ones are silent.

## Lifecycle and timing

- `Tick()` is the first statement of both `FlightHud.Draw` and `EditorPanel.Draw`, before their
  `Visible` checks ([FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L96-L101), [EditorPanel.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/EditorPanel.cs#L25-L30)).[^ker-flighthud] [^ker-editorpanel]
  It therefore runs in `frame:draw-ui` from the two Harmony postfixes, i.e. only while a controlled
  vehicle's UI or the editor is being drawn. Settings are not loaded until the first such frame, so
  anything reading them earlier (for example the fallback menu's `Visible` toggles in the main menu)
  sees defaults.
- File I/O is synchronous on the main thread: one read at load, then at most one small write every
  2 s and only after a change. Serialising the snapshot every 2 s allocates a few kilobytes.
- Nothing is flushed on `[StarMapUnload]`; changes made in the last two seconds before quitting can be
  lost.

## Key code

| Path | Role |
|---|---|
| [SettingsStore.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SettingsStore.cs) | Data classes, tick, load, save, capture, apply, open-folder helper |
| [SectionHost.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SectionHost.cs#L10-L40) | `HudSection` registration that applies saved state |

## Snippets

```csharp
// SettingsStore.cs L62-L119
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

## Game APIs touched

None beyond logging (`Brutal.Logging.DefaultCategory.Log`). The store is plain .NET:
`System.Text.Json`, `File`, `Directory`, `Stopwatch` and `Process.Start` for "Open data folder".

## Pitfalls and version notes

- Settings are machine-wide, not per save game; nothing ties them to the current universe.
- The editor panel's own analysis toggles are not part of `SettingsData`, while the flight HUD's are,
  so the two windows behave differently after a restart.
- Polling and string comparison is simple and robust against missing change notifications, at the
  cost of a serialisation every 2 s; a dirty flag set by the UI would avoid it.
- `Process.Start` with `UseShellExecute = true` on a directory opens the platform file browser on
  Windows; behaviour on other platforms was not checked here.

## See also

- [Draw-list widget kit with pop-out sections](draw-list-widget-kit.md)
- [HUD stat tiles and threshold alerts](hud-widgets-and-threshold-alerts.md)
- [ModMenu entry with a standalone menu fallback](modmenu-entry-with-standalone-fallback.md)
- [Telemetry history and CSV export](telemetry-history-and-csv-export.md)
- [Autosave settings.json by comparing a serialised snapshot every two seconds](../../../howto/persistence/settings-formats/persist-mod-settings-as-json/persist-mod-settings-as-json-1.md) - how-to article demonstrating this feature
- [Skip the dirty flag by comparing the serialised snapshot with the last write](../../../howto/persistence/settings-formats/write-and-load-config-files-safely/write-and-load-config-files-safely-2.md) - how-to article demonstrating this feature
- [Store config in a mod-named subfolder under Constants.DocumentsFolderPath](../../../howto/persistence/user-data/choose-where-a-mod-stores-its-files/choose-where-a-mod-stores-its-files-2.md) - how-to article demonstrating this feature

[^ker-settingsstore]: SettingsStore.cs
[^ker-sectionhost]: SectionHost.cs
[^ker-flighthud]: FlightHud.cs
[^ker-editorpanel]: EditorPanel.cs
