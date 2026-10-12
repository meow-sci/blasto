---
type: Mod Feature
title: JSON config in the KSA documents folder
description: Stores settings, colour overrides and tuning overrides in Documents/My Games/Kitten Space Agency/KSATelemetryOverlay/config.json via Constants.DocumentsFolderPath, tolerates hand edits by dropping unreadable fields one at a time, clamps values, and saves debounced and atomically
mod: ksa-public-telemetry
category: persistence
techniques: [json-config, starmap-lifecycle]
hooks:
  - starmap StarMapAllModsLoaded
  - starmap StarMapAfterGui
  - harmony-postfix KSA.Program.DrawFps
  - starmap StarMapUnload
lifecycle: [startup:all-mods-loaded, frame:draw-ui, event:starmap-unload]
game_types: [KSA.Constants]
tags: [config, json, persistence, documents-folder, system-text-json]
---

All settings live in one JSON file that the README calls "safe to hand-edit".[^kpt-readme] Besides the
settings object it carries only the colours and tuning values the player changed from the defaults,
plus the per-mission liftoff epochs and launch sites. A broken field costs that field, not the whole
file.

## How it works

1. **Path.** `ConfigStore.DirectoryPath = Path.Combine(Constants.DocumentsFolderPath, "KSATelemetryOverlay")`
   and `FilePath` appends `config.json` ([Config/ConfigStore.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/ConfigStore.cs#L28-L30)). The game defines
   `Constants.DocumentsFolderPath` as `Environment.SpecialFolder.Personal` + `My Games/Kitten Space Agency`
   ([current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281)), and StarMap can redirect it per instance by patching that getter when
   launched with `-InstancePath` or `STARMAP_INSTANCE_PATH`
   ([StarMap.Core/Patches/DocumentsPathPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/DocumentsPathPatches.cs#L6-L32)), so using the game constant keeps the
   config next to the right game data.[^ksa-constants] [^starmap-documentspathpatches] StarMap applies
   that patch in `ModLoader.Init`, before any mod is created
   ([StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L46-L50)), and the mod re-reads the getter on every
   access (`DirectoryPath` is an expression-bodied property), so the path follows whatever the getter
   returns at the moment of each load or save.[^starmap-modloader]
2. **Shape.** A wrapper with three properties: `settings` (the whole `OverlayConfig`, public fields
   serialised with `IncludeFields = true`), `colors` (field name to `#RRGGBBAA`), and `tuning` (field
   name to number). Enums are written as names; comments and trailing commas are accepted on read
   ([Config/ConfigStore.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/ConfigStore.cs#L16-L42), [OverlayConfig.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/OverlayConfig.cs#L7-L79)).
3. **Resilient load.** The file is parsed into a `JsonObject` first. Settings deserialization is retried:
   on a `JsonException` it removes the next field from a list of fragile ones (`ToggleKey`,
   `SettingsKey`, `LeftSlots`, `RightSlots`, `Windows`, `MissionEpochs`, `MutedNotifications`,
   `SpeedReference`, `CountdownKey`, `MissionOrigins`), logs which one it dropped, and tries again. The
   `colors` and `tuning` sections are parsed independently and ignored if broken
   ([Config/ConfigStore.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/ConfigStore.cs#L43-L137)).
4. **Sanitise.** Every numeric setting is clamped to the constants declared on `OverlayConfig`, slot
   arrays are trimmed to four with unknown enum values replaced, and null collections are replaced with
   empty ones ([Config/ConfigStore.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/ConfigStore.cs#L194-L251)).
5. **Debounced, atomic save.** UI code calls `MarkDirty()`; `Flush(dt)` runs once per overlay draw and
   saves after one second without further changes. `SaveNow` writes `config.json.tmp` and moves it
   over the real file with `File.Move(..., overwrite: true)`, so a crash mid-write cannot truncate the
   config ([Config/ConfigStore.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/ConfigStore.cs#L139-L192)).
6. **Missing file.** Defaults are used and nothing is written during play until something changes
   ([Config/ConfigStore.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/ConfigStore.cs#L51-L58)); the unconditional `SaveNow` in
   `[StarMapUnload]` then writes the file at shutdown.

## Lifecycle and timing

- **Load in `startup:all-mods-loaded`**, before the renderer and settings UI are built
  ([TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L39-L53)). No game state is needed, only the documents path.
- **Save from `frame:draw-ui`** via `ConfigStore.Flush(dt)` at the end of `DrawEverything` (from
  `[StarMapAfterGui]`, or from the `DrawFps` postfix while F2 hides the UI)
  ([TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L128-L140)). It is synchronous file I/O on the main thread, rate-limited
  to once per second of quiet.
- **Final save at `event:starmap-unload`**: window placement is captured and `SaveNow` runs
  ([TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L149-L156)). This happens after `StarMapCore.DeInit` has called the id-less
  `UnpatchAll()`, which also removes StarMap's documents-path prefix
  ([StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35), [StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L143-L151)); see the pitfall below.[^starmap-starmapcore]

## Key code

| Path | Role |
|---|---|
| [Config/ConfigStore.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/ConfigStore.cs#L28-L42) | Path and file shape |
| [Config/ConfigStore.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/ConfigStore.cs#L108-L137) | Drop-a-field-and-retry deserialization |
| [Config/ConfigStore.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/ConfigStore.cs#L139-L192) | Debounce and atomic write |
| [OverlayConfig.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/OverlayConfig.cs#L7-L79) | Settings model with limits |

## Snippets

```csharp
// Config/ConfigStore.cs L108-L137
    private static readonly string[] _fragileSettings =
        ["ToggleKey", "SettingsKey", "LeftSlots", "RightSlots", "Windows", "MissionEpochs",
         "MutedNotifications", "SpeedReference", "CountdownKey", "MissionOrigins"];

    private static OverlayConfig? LoadSettings(JsonNode? node)
    {
        if (node is not JsonObject settings)
        {
            return null;
        }

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return settings.Deserialize<OverlayConfig>(_options);
            }
            catch (JsonException ex)
            {
                if (attempt >= _fragileSettings.Length)
                {
                    throw;
                }

                string dropped = _fragileSettings[attempt];
                Console.WriteLine(LogPrefix + $"could not read '{dropped}' from the config, using its default: {ex.Message}");
                settings.Remove(dropped);
            }
        }
    }
```

```csharp
// Config/ConfigStore.cs L161-L192
    public static void SaveNow()
    {
        _dirty = false;
        _sinceChange = 0.0;

        if (_tracked is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(DirectoryPath);

            ConfigFile file = new()
            {
                Settings = _tracked,
                Colors = OverlayPalette.CaptureOverrides(),
                Tuning = Rendering.Tuning.CaptureOverrides(),
            };

            string path = FilePath;
            string temp = path + ".tmp";

            File.WriteAllText(temp, JsonSerializer.Serialize(file, _options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine(LogPrefix + $"config save failed: {ex.Message}");
        }
    }
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `Constants.DocumentsFolderPath` | [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281) | Base folder for mod data |

## Pitfalls and version notes

- **Instance path lost at shutdown.** With `-InstancePath` or `STARMAP_INSTANCE_PATH`, loads and the
  debounced saves use the instance folder, but the final `SaveNow` in `[StarMapUnload]` runs after
  StarMap removed every Harmony patch, so `Constants.DocumentsFolderPath` is back to
  `Personal/My Games/Kitten Space Agency` and that save goes to the default folder (follows from the
  code; not tested). A copy should resolve and cache the path once, in `[StarMapAllModsLoaded]`.
- **Fragile list is positional.** Each retry drops the next name in the list regardless of which field
  actually failed, so a single bad `CountdownKey` (ninth in the list) costs the eight fields before it
  too. A copy could parse the failing path out of `JsonException.Path` instead.
- **Field-based model.** `IncludeFields = true` serialises every public field; `[JsonIgnore]` hides the
  runtime-only `Revision` counter. Renaming a field silently resets it to the default.
- **Per-user, not per-save.** Mission epochs are global; two saves that happen to share a
  `LaunchGameTime` key would share a liftoff epoch.

## See also

- [Live tuning window and palette by reflection](live-tuning-and-palette-reflection.md)
- [Every element as its own ImGui window](panels-as-static-imgui-windows.md)
- [Mission clock and liftoff detection](mission-clock-and-liftoff-detection.md)
- [Persist a settings object with override-only colors and tuning, saved by a debounced dirty flag](../../../howto/persistence/settings-formats/persist-mod-settings-as-json/persist-mod-settings-as-json-2.md) - how-to article demonstrating this feature
- [Mark dirty, debounce, swap the file in atomically, and retry deserialisation by dropping the broken field](../../../howto/persistence/settings-formats/write-and-load-config-files-safely/write-and-load-config-files-safely-1.md) - how-to article demonstrating this feature
- [Store config in a mod-named subfolder under Constants.DocumentsFolderPath](../../../howto/persistence/user-data/choose-where-a-mod-stores-its-files/choose-where-a-mod-stores-its-files-2.md) - how-to article demonstrating this feature
- [Store only the colors a player changed, keyed by field name, in the main config](../../../howto/persistence/user-data/persist-user-created-presets-and-content/persist-user-created-presets-and-content-3.md) - how-to article demonstrating this feature

[^kpt-readme]: README.md
[^ksa-constants]: KSA.Constants (decompiled, build 2026.10.10.5554)
[^starmap-documentspathpatches]: StarMap StarMap.Core/Patches/DocumentsPathPatches.cs
[^starmap-modloader]: StarMap StarMap.Core/ModRepository/ModLoader.cs
[^starmap-starmapcore]: StarMap StarMap.Core/StarMapCore.cs
