---
type: How-To
title: Mark dirty, debounce, swap the file in atomically, and retry deserialisation by dropping the broken field
description: Write side - MarkDirty from the UI, Flush(dt) from the draw loop after one second of quiet, a temp file and File.Move overwrite, a final SaveNow at unload; load side - parse to a JsonObject, remove the fragile field and retry on JsonException, parse sections independently, then sanitise
category: persistence
group: settings-formats
topic: write-and-load-config-files-safely
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-public-telemetry/features/json-config-in-documents]
techniques: [json-config]
hooks:
  - starmap StarMapAllModsLoaded
  - starmap StarMapAfterGui
  - harmony-postfix KSA.Program.DrawFps
  - starmap StarMapUnload
lifecycle: [frame:draw-ui, event:starmap-unload, startup:all-mods-loaded]
game_types: []
tags: [config, atomic-write, debounce, dirty-flag, jsonobject, retry, sanitise]
---

Outcome: a JSON config that is written rarely, never truncated by a crash, and loaded in a way where a hand-edit mistake costs the mistaken field rather than the file. KSA-Public-Telemetry's `ConfigStore` is a compact reference for both halves. Demonstrated by [JSON config in the KSA documents folder](../../../../mods/ksa-public-telemetry/features/json-config-in-documents.md); the file shape and the override-only colors are in [Persist a settings object with override-only colors and tuning, saved by a debounced dirty flag](../persist-mod-settings-as-json/persist-mod-settings-as-json-2.md).

## Background: how the game does it

No engine support is involved; the guarantees come from `File.Move(temp, path, overwrite: true)` (an atomic replace on the platforms KSA runs on) and from doing the work at points where it is safe: load before the UI exists, write from the draw loop on the main thread, final write at unload. `StarMapUnload` is after the game loop and after unpatching, so the final write must not depend on patched behavior (see the [overview](./overview.md#engine-background)).

## Lifecycle and timing

| Step | Hook | Lifecycle |
|---|---|---|
| `ConfigStore.Load()` | `[StarMapAllModsLoaded]`, inside a `try` that calls `Fail("starting up", ex)` ([TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L39-L63)) | `startup:all-mods-loaded` |
| `MarkDirty()` | any UI change | `frame:draw-ui` |
| `Flush(dt)` | end of `DrawEverything`, which runs from `[StarMapAfterGui]` or, while the HUD is hidden, from a postfix on `Program.DrawFps` ([TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L128-L140)) | `frame:draw-ui` |
| `SaveNow()` | `[StarMapUnload]` ([TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L149-L156)) | `event:starmap-unload` |

## Steps

### Write side

1. **Set a dirty flag and a quiet timer.** `MarkDirty` records that something changed and resets the timer, so a burst of edits (a slider drag) becomes one write:

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

2. **Flush from the draw loop**, with the frame's `dt`, after `SaveIntervalSeconds` (1.0) without further changes ([Config/ConfigStore.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/ConfigStore.cs#L14)). The call is the last line of `DrawEverything`.

3. **Write to a temp file and move it over the real one.** An interrupted write leaves the previous file intact. `Directory.CreateDirectory` first, and catch and log every failure so a read-only folder cannot throw into the draw loop:

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

4. **Flush once more at unload**, after capturing any state that only exists in memory (window placement):

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

### Load side

5. **Parse to a `JsonObject` first, with tolerant options.** Comments and trailing commas are accepted (`JsonCommentHandling.Skip`, `AllowTrailingCommas`), so the file is "safe to hand-edit":

   ```csharp
   // Config/ConfigStore.cs L16-L23
   private static readonly JsonSerializerOptions _options = new()
   {
       WriteIndented = true,
       IncludeFields = true,
       AllowTrailingCommas = true,
       ReadCommentHandling = JsonCommentHandling.Skip,
       Converters = { new JsonStringEnumConverter() },
   };
   ```

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

6. **Retry deserialisation, dropping one fragile field per failure.** The settings object is deserialised from the node; on a `JsonException` the next name from a list of fragile fields (keys, slot arrays, dictionaries, enums) is removed from the node, logged, and the deserialisation retried, so one bad field falls back to its default and the rest are kept. The last attempt rethrows:

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

7. **Parse independent sections independently.** The `colors` and `tuning` sections each go through `LoadSection<T>`, which ignores a broken section with a log line instead of failing the load ([Config/ConfigStore.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/ConfigStore.cs#L90-L106)).

8. **Sanitise after loading.** Clamp every numeric setting to constants declared on the settings class, trim slot arrays and replace unknown enum values, and replace null collections with empty ones:

   ```csharp
   // Config/ConfigStore.cs L194-L251
   private static void Sanitise(OverlayConfig config)
   {
       config.Scale = Math.Clamp(config.Scale, OverlayConfig.MinScale, OverlayConfig.MaxScale);
       config.Opacity = Math.Clamp(config.Opacity, OverlayConfig.MinOpacity, OverlayConfig.MaxOpacity);
       config.SmoothingSeconds = Math.Clamp(
           config.SmoothingSeconds, OverlayConfig.MinSmoothing, OverlayConfig.MaxSmoothing);
       config.TimelineWindowSeconds = Math.Clamp(
           config.TimelineWindowSeconds, OverlayConfig.MinTimelineWindow, OverlayConfig.MaxTimelineWindow);
       config.CountdownSeconds = Math.Clamp(
           config.CountdownSeconds, OverlayConfig.MinCountdown, OverlayConfig.MaxCountdown);
       config.SpeedArcFullScale = Math.Clamp(
           config.SpeedArcFullScale, OverlayConfig.MinSpeedArc, OverlayConfig.MaxSpeedArc);
       config.AltitudeArcFullScaleKm = Math.Clamp(
           config.AltitudeArcFullScaleKm, OverlayConfig.MinAltitudeArc, OverlayConfig.MaxAltitudeArc);
       config.GForceArcFullScale = Math.Clamp(
           config.GForceArcFullScale, OverlayConfig.MinGForceArc, OverlayConfig.MaxGForceArc);
       config.EngineDiagramRotation = Math.Clamp(
           config.EngineDiagramRotation,
           OverlayConfig.MinEngineRotation,
           OverlayConfig.MaxEngineRotation);
       config.FreezeAtToleranceFraction = Math.Clamp(
           config.FreezeAtToleranceFraction,
           OverlayConfig.MinFreezeTolerance,
           OverlayConfig.MaxFreezeTolerance);

       config.LeftSlots = SanitiseSlots(config.LeftSlots, [ReadoutKind.Speed, ReadoutKind.Altitude]);
       config.RightSlots = SanitiseSlots(config.RightSlots, [ReadoutKind.GForce]);

       if (string.IsNullOrWhiteSpace(config.MissionName))
       {
           config.MissionName = null;
       }

       config.Windows ??= [];
       config.Windows.RemoveAll(static w => w is null || string.IsNullOrEmpty(w.Id));

       config.MissionEpochs ??= [];
       config.MissionOrigins ??= [];
       config.MutedNotifications ??= [];
   }

   private static ReadoutKind[] SanitiseSlots(ReadoutKind[]? slots, ReadoutKind[] fallback)
   {
       if (slots is null || slots.Length == 0)
       {
           return fallback;
       }

       int count = Math.Min(slots.Length, OverlayConfig.MaxSlotsPerSide);
       ReadoutKind[] trimmed = new ReadoutKind[count];

       for (int i = 0; i < count; i++)
       {
           trimmed[i] = Enum.IsDefined(slots[i]) ? slots[i] : ReadoutKind.Speed;
       }

       return trimmed;
   }
   ```

## Complete example

| File | Role |
|---|---|
| [Config/ConfigStore.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/ConfigStore.cs) | Path, shape, load with retry, sanitise, debounce, atomic save |
| [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L39-L63) and [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L128-L156) | Load, flush and unload hooks |

## Variations and gotchas

- **A fully unparseable file is overwritten.** If `JsonNode.Parse` throws, the catch block resets to defaults and `Track(config)` registers them; the unload `SaveNow` (and any later change) then replaces the player's file with defaults. Combine this approach with the "leave it alone" rule of [Save from Task.Run with an atomic helper, clamp bad values on load and leave an unparseable file alone](write-and-load-config-files-safely-3.md) if the player's edits matter more than the convenience.
- **The unload write under an instance path.** `DirectoryPath` re-reads `Constants.DocumentsFolderPath` on each access; at `StarMapUnload` the StarMap prefix is gone, so the final save goes to the default folder. Cache the resolved path at load (see [Persist a settings object with override-only colors and tuning, saved by a debounced dirty flag](../persist-mod-settings-as-json/persist-mod-settings-as-json-2.md)).
- **The retry list is manual.** A field that can throw but is not in the list fails the whole settings object (the last attempt rethrows, the outer catch applies defaults).
- **A failed write is not retried.** `SaveNow` clears the dirty flag before it writes, so an I/O failure (another process holding the file, a read-only folder) is logged and the data stays in memory until the next change or the unload save. `File.Move` with overwrite is a single replace call.

## See also

- [Skip the dirty flag by comparing the serialised snapshot with the last write](write-and-load-config-files-safely-2.md), [Save from Task.Run with an atomic helper, clamp bad values on load and leave an unparseable file alone](write-and-load-config-files-safely-3.md), [Flush on close and unload, skip junk keys on load and migrate the previous format](write-and-load-config-files-safely-4.md), [overview](./overview.md)
- [JSON config in the KSA documents folder](../../../../mods/ksa-public-telemetry/features/json-config-in-documents.md)
- [Persist mod settings as JSON with System.Text.Json](../persist-mod-settings-as-json/overview.md), [Persist user-created palettes, presets and content files](../../user-data/persist-user-created-presets-and-content/overview.md)
