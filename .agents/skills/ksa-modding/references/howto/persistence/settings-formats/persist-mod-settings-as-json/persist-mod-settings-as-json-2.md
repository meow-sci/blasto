---
type: How-To
title: Persist a settings object with override-only colors and tuning, saved by a debounced dirty flag
description: Store a settings wrapper with settings, colors and tuning sections where colors and tuning keep only values that differ from the compiled defaults, accept comments and trailing commas, and save after a second of quiet and again at StarMapUnload
category: persistence
group: settings-formats
topic: persist-mod-settings-as-json
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/ksa-public-telemetry/features/json-config-in-documents
  - mods/ksa-public-telemetry/features/live-tuning-and-palette-reflection
techniques: [json-config, reflection]
hooks:
  - starmap StarMapAllModsLoaded
  - starmap StarMapAfterGui
  - harmony-postfix KSA.Program.DrawFps
  - starmap StarMapUnload
lifecycle: [startup:all-mods-loaded, frame:draw-ui, event:starmap-unload]
game_types: [KSA.Constants]
tags: [settings, json, dirty-flag, debounce, overrides, reflection, documents-folder]
---

Outcome: one hand-editable `config.json` that carries the settings object and, separately, only the colors and tuning numbers the player changed from the defaults, so a default changed in a later version reaches players who never customised. Writes are debounced and a final save runs at unload. Demonstrated by [JSON config in the KSA documents folder](../../../../mods/ksa-public-telemetry/features/json-config-in-documents.md) and [Live tuning window and palette by reflection](../../../../mods/ksa-public-telemetry/features/live-tuning-and-palette-reflection.md). The load-side resilience (retry, sanitise) and the atomic write are treated as a technique in [Write config files atomically and load hand-edited ones without losing them](../write-and-load-config-files-safely/overview.md).

## Background: how the game does it

The path is `Path.Combine(Constants.DocumentsFolderPath, "KSATelemetryOverlay")` + `config.json` ([Config/ConfigStore.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/ConfigStore.cs#L28-L30)). The game defines the constant as `Personal` + `My Games/Kitten Space Agency` ([current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281)), and StarMap redirects it by patching the getter at launch (`-InstancePath` or `STARMAP_INSTANCE_PATH`), so using the constant keeps the config beside the right game data. `ConfigStore.DirectoryPath` is an expression-bodied property that reads the getter on every access, so the path follows whatever the getter returns at the moment of each load or save. StarMap's patch is removed by the id-less `UnpatchAll()` before `StarMapUnload` (see the pitfall below) ([StarMap.Core/Patches/DocumentsPathPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/DocumentsPathPatches.cs#L12-L45)).

## Lifecycle and timing

| Piece | Hook | Lifecycle |
|---|---|---|
| `ConfigStore.Load()` and `ApplyEpochs` | `[StarMapAllModsLoaded]` ([TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L39-L53)), before the renderer and settings UI are built | `startup:all-mods-loaded` |
| `MarkDirty()` | UI code on any change | `frame:draw-ui` |
| `Flush(dt)` | end of `DrawEverything` (from `[StarMapAfterGui]`, or from a `DrawFps` postfix while the UI is hidden) ([TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L128-L140)) | `frame:draw-ui` |
| `SaveNow()` | `[StarMapUnload]` ([TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L149-L156)) | `event:starmap-unload` |

I/O is synchronous on the main thread, rate-limited to once per second of quiet. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Shape the file as a wrapper with three sections.** `settings` is the whole `OverlayConfig` (public fields, `IncludeFields = true`), `colors` maps field name to `#RRGGBBAA`, `tuning` maps field name to number. Options accept comments and trailing commas and write enums as names:

   ```csharp
   // Config/ConfigStore.cs L16-L42
   private static readonly JsonSerializerOptions _options = new()
   {
       WriteIndented = true,
       IncludeFields = true,
       AllowTrailingCommas = true,
       ReadCommentHandling = JsonCommentHandling.Skip,
       Converters = { new JsonStringEnumConverter() },
   };

   private static bool _dirty;
   private static double _sinceChange;
   private static OverlayConfig? _tracked;
   public static string DirectoryPath => Path.Combine(Constants.DocumentsFolderPath, FolderName);

   public static string FilePath => Path.Combine(DirectoryPath, FileName);

   private sealed class ConfigFile
   {
       [JsonPropertyName("settings")]
       public OverlayConfig? Settings { get; set; }

       [JsonPropertyName("colors")]
       public Dictionary<string, string>? Colors { get; set; }

       [JsonPropertyName("tuning")]
       public Dictionary<string, double>? Tuning { get; set; }
   }
   ```

2. **Capture only what differs from the defaults.** The color table is discovered by reflection over the static `uint` fields of the style class and compared with a defaults class; tuning does the same with `float`/`int` fields:

   ```csharp
   // Config/OverlayPalette.cs L8-L17
   private static readonly FieldInfo[] _fields = typeof(OverlayStyle)
       .GetFields(BindingFlags.Public | BindingFlags.Static)
       .Where(f => f.FieldType == typeof(uint) && !f.IsLiteral)
       .OrderBy(f => f.Name, StringComparer.Ordinal)
       .ToArray();

   private static readonly Dictionary<string, uint> _defaults = typeof(OverlayStyle.Defaults)
       .GetFields(BindingFlags.Public | BindingFlags.Static)
       .Where(f => f.FieldType == typeof(uint))
       .ToDictionary(f => f.Name, f => (uint)f.GetRawConstantValue()!, StringComparer.Ordinal);
   ```

   ```csharp
   // Config/OverlayPalette.cs L49-L62
   public static Dictionary<string, string> CaptureOverrides()
   {
       Dictionary<string, string> overrides = new(StringComparer.Ordinal);

       foreach (string name in Names)
       {
           if (!IsDefault(name))
           {
               overrides[name] = ToHex(Get(name));
           }
       }

       return overrides;
   }
   ```

   ```csharp
   // Rendering/Tuning.cs L94-L110
   public static Dictionary<string, double> CaptureOverrides()
   {
       Dictionary<string, double> overrides = new(StringComparer.Ordinal);

       for (int i = 0; i < _fields.Length; i++)
       {
           FieldInfo field = _fields[i];
           double value = Read(field);

           if (value != _defaults[field.Name])
           {
               overrides[field.Name] = value;
           }
       }

       return overrides;
   }
   ```

3. **Apply overrides on top of reset defaults.** `ApplyOverrides` first resets everything, then applies each recognised key; unknown keys and unparseable hex are ignored:

   ```csharp
   // Config/OverlayPalette.cs L64-L80
   public static void ApplyOverrides(Dictionary<string, string>? overrides)
   {
       ResetAll();

       if (overrides is null)
       {
           return;
       }

       foreach (KeyValuePair<string, string> entry in overrides)
       {
           if (Find(entry.Key) is not null && TryParseHex(entry.Value, out uint packed))
           {
               Set(entry.Key, packed);
           }
       }
   }
   ```

   Hex parse and print are symmetrical (`#RRGGBBAA`, alpha optional on read) ([Config/OverlayPalette.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/OverlayPalette.cs#L82-L127)).

4. **Load once at startup.** A missing file leaves the defaults and logs it; the three sections are applied independently:

   ```csharp
   // Config/ConfigStore.cs L43-L86
   public static OverlayConfig Load()
   {
       OverlayConfig config = new();

       try
       {
           string path = FilePath;

           if (!File.Exists(path))
           {
               OverlayPalette.ResetAll();
               Rendering.Tuning.ResetAll();
               Track(config);
               Console.WriteLine(LogPrefix + $"no config at {path}; using defaults.");
               return config;
           }

           JsonObject? root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
           {
               AllowTrailingCommas = true,
               CommentHandling = JsonCommentHandling.Skip,
           }) as JsonObject;

           if (LoadSettings(root?["settings"]) is { } loaded)
           {
               config = loaded;
               Sanitise(config);
           }

           OverlayPalette.ApplyOverrides(LoadSection<Dictionary<string, string>>(root?["colors"], "colors"));
           Rendering.Tuning.ApplyOverrides(LoadSection<Dictionary<string, double>>(root?["tuning"], "tuning"));
           Console.WriteLine(LogPrefix + $"loaded config from {path}.");
       }
       catch (Exception ex)
       {
           config = new OverlayConfig();
           OverlayPalette.ResetAll();
           Rendering.Tuning.ResetAll();
           Console.WriteLine(LogPrefix + $"config load failed, using defaults: {ex.Message}");
       }

       Track(config);
       return config;
   }
   ```

5. **Debounce the save.** UI code calls `MarkDirty()`; `Flush(dt)` runs once per overlay draw and saves after one second without further changes:

   ```csharp
   // Config/ConfigStore.cs L139-L159
   public static void MarkDirty()
   {
       _dirty = true;
       _sinceChange = 0.0;
   }

   public static void Flush(double dt)
   {
       if (!_dirty)
       {
           return;
       }

       _sinceChange += dt;
       if (_sinceChange < SaveIntervalSeconds)
       {
           return;
       }

       SaveNow();
   }
   ```

6. **Write via a temp file and move**, so a crash mid-write cannot truncate the config:

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

7. **Save windows and settings together at unload.** Capture window placement, then `SaveNow()`:

   ```csharp
   // TelemetryOverlayMod.cs L149-L156
   [StarMapUnload]
   public void OnUnload()
   {
       if (_config is not null)
       {
           _windows.CaptureInto(_config);
           ConfigStore.SaveNow();
       }
   ```

## Complete example

| File | Role |
|---|---|
| [Config/ConfigStore.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/ConfigStore.cs) | Path, shape, load with retry, sanitise, debounce, atomic save |
| [Config/OverlayPalette.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/OverlayPalette.cs) | Color overrides by reflection and hex conversion |
| [Rendering/Tuning.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Tuning.cs) | Tuning overrides by reflection |
| [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L39-L63) and [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L128-L156) | Load, flush and unload hooks |

## Variations and gotchas

- **The unload save misses an instance path.** `StarMapUnload` runs after StarMap's `UnpatchAll()` has removed the documents-path prefix, and `DirectoryPath` re-reads the getter, so under `-InstancePath` the final `SaveNow` writes to the default documents folder instead of the instance's. Resolve and keep the directory in `StarMapAllModsLoaded` (see the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-1.md)).
- **A missing file is written at shutdown.** Nothing is written during play until something changes, but the unconditional `SaveNow` at unload creates the file.
- **An unparseable file is overwritten.** If `JsonNode.Parse` throws, the catch block resets to defaults and tracks them; the later `SaveNow` then replaces the player's file. See [Write config files atomically and load hand-edited ones without losing them](../write-and-load-config-files-safely/overview.md) for a policy that leaves the file alone.
- **Reflection ties the file to field names.** A renamed style field silently drops the override (unknown keys are ignored).
- **Defaults are the migration path.** Because only differences are stored, changing a compiled default changes the look for players who never moved that value, and a customised value stays fixed.

## See also

- [Autosave settings.json by comparing a serialised snapshot every two seconds](persist-mod-settings-as-json-1.md), [overview](./overview.md)
- [JSON config in the KSA documents folder](../../../../mods/ksa-public-telemetry/features/json-config-in-documents.md), [Live tuning window and palette by reflection](../../../../mods/ksa-public-telemetry/features/live-tuning-and-palette-reflection.md)
- [Write config files atomically and load hand-edited ones without losing them](../write-and-load-config-files-safely/overview.md), [Find where KSA keeps user data and choose where a mod stores its config and data files](../../user-data/choose-where-a-mod-stores-its-files/overview.md)
