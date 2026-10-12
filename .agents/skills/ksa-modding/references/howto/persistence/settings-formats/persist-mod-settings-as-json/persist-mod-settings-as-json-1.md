---
type: How-To
title: Autosave settings.json by comparing a serialised snapshot every two seconds
description: Capture the live static settings into a DTO, serialise it every two seconds from the draw loop and write only when the JSON differs from the last write, so a clean load never writes and late-constructed sections still get their saved state
category: persistence
group: settings-formats
topic: persist-mod-settings-as-json
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/kitten-engineer-redux/features/json-settings-autosave]
techniques: [json-config]
hooks: [harmony-postfix KSA.Vehicle.OnDrawUi, harmony-postfix KSA.VehicleEditor.OnDrawUi]
lifecycle: [frame:draw-ui, editor:draw-ui]
game_types: [KSA.Constants]
tags: [settings, json, autosave, snapshot-compare, stopwatch, static-fields]
---

Outcome: every visible preference (theme colors, text scale, chosen tiles, alert rules, which sections are open or popped out, panel visibility) survives a restart, with no save button and no dirty flags in the UI code. Kitten Engineer Redux (KER) polls its own static fields on a timer, serialises a snapshot, and writes only when it differs from the last one. Demonstrated by [JSON settings with autosave](../../../../mods/kitten-engineer-redux/features/json-settings-autosave.md).

## Background: how the game does it

Nothing in KSA is involved beyond the folder. KER builds it from `Environment.SpecialFolder.MyDocuments` (`Documents/My Games/Kitten Space Agency/KittenEngineerReduxData`), which equals the default `Constants.DocumentsFolderPath` ([current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281)) but ignores a StarMap instance-path override (see the [overview](./overview.md#engine-background)).

## Lifecycle and timing

`SettingsStore.Tick()` is the first statement of both `FlightHud.Draw` and `EditorPanel.Draw`, before their `Visible` checks ([FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L96-L101), [EditorPanel.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/EditorPanel.cs#L25-L30)). Those are called from two Harmony postfixes rather than from a StarMap attribute: [Patch_FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/Patch_FlightHud.cs) on `Vehicle.OnDrawUi` (`frame:draw-ui`: `Universe.OnDrawUi` runs it for every vehicle inside `Program.OnDrawUiThreadSafe`, and the patch returns early unless the viewport is the main one and the vehicle is `Program.ControlledVehicle`) and [Patch_EditorPanel.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/Patch_EditorPanel.cs) on `VehicleEditor.OnDrawUi` (`editor:draw-ui`). So it runs only while a controlled vehicle's UI or the editor is being drawn. Consequences: the first call loads (so settings are read at the first draw, not at startup, and anything reading them earlier sees defaults); nothing is written while the HUD is hidden or in menus; and nothing is flushed at `StarMapUnload`, so changes in the last two seconds before quitting can be lost. File I/O is synchronous on the main thread: one read, then at most one small write every two seconds and only after a change. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Define a plain DTO** with public auto-properties and defaults. Colors are `float[]` by role, lists hold enum names, sections are keyed by id:

   ```csharp
   // SettingsStore.cs L13-L42
   internal sealed class SectionStateData
   {
       public bool Open { get; set; }
       public bool Popped { get; set; }
   }

   internal sealed class AlertRuleData
   {
       public string Metric { get; set; } = string.Empty;
       public bool Enabled { get; set; }
       public bool Above { get; set; }
       public double Threshold { get; set; }
   }

   internal sealed class SettingsData
   {
       public Dictionary<string, float[]> Colors { get; set; } = new();
       public float CornerRadius { get; set; } = 4f;
       public float TextScale { get; set; } = 1f;
       public List<string> Widgets { get; set; } = new();
       public bool AlertsEnabled { get; set; } = true;
       public List<AlertRuleData> Alerts { get; set; } = new();
       public Dictionary<string, SectionStateData> Sections { get; set; } = new();
       public string GraphMetric { get; set; } = "Altitude";
       public bool FlightHudVisible { get; set; } = true;
       public bool EditorPanelVisible { get; set; } = true;
       public bool UseNativeStaging { get; set; } = true;
       public bool UseAtmosphere { get; set; } = true;
       public double TargetOrbitAltitude { get; set; } = 200_000.0;
   }
   ```

2. **Choose the folder and options.** `WriteIndented` makes the file readable:

   ```csharp
   // SettingsStore.cs L46-L60
   private static readonly string BaseDirectory = Path.Combine(
       Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
       "My Games", "Kitten Space Agency", "KittenEngineerReduxData");

   private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
   private static readonly Stopwatch Timer = Stopwatch.StartNew();
   private static readonly Dictionary<string, SectionStateData> SectionStates = new();

   private static string _lastJson = string.Empty;
   private static bool _loaded;
   private static bool _saveErrorLogged;

   public static string DataDirectory => BaseDirectory;
   public static string ExportDirectory => Path.Combine(BaseDirectory, "exports");
   private static string SettingsPath => Path.Combine(BaseDirectory, "settings.json");
   ```

3. **Drive everything from one `Tick()`.** The first call loads; later calls return until a `Stopwatch` passes two seconds, then restart it and save:

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

4. **Load defensively and seed the comparison string.** Any exception is logged once and ignored; `finally` marks the store loaded and records the JSON of the state *after* applying the file, so a clean load never triggers a write:

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

5. **Save only when the snapshot changed.** Serialise a fresh `Capture()`, compare with the last written string, and write only if different. The first save error is logged and later ones are silent:

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

6. **Capture and apply with fallbacks.** `Capture()` copies the static fields into the DTO; `Apply()` reads them back, using the current value when a color key is missing or short and skipping unknown tile and alert names, so older or hand-edited files load:

   ```csharp
   // SettingsStore.cs L143-L170
   private static SettingsData Capture()
   {
       var data = new SettingsData
       {
           CornerRadius = PanelKit.CornerRadius,
           TextScale = PanelKit.TextScale,
           Widgets = HudWidgets.GetSelectedNames(),
           AlertsEnabled = HudAlerts.Enabled,
           GraphMetric = FlightHud.HistoryMetricName,
           FlightHudVisible = FlightHud.Visible,
           EditorPanelVisible = EditorPanel.Visible,
           UseNativeStaging = FlightHud.UseNativeStaging,
           UseAtmosphere = FlightHud.UseAtmosphere,
           TargetOrbitAltitude = FlightHud.TargetOrbitAltitude,
       };

       data.Colors["WindowBg"] = ToArray(PanelKit.WindowBgColor);
       data.Colors["Border"] = ToArray(PanelKit.BorderColor);
       data.Colors["Header"] = ToArray(PanelKit.HeaderColor);
       data.Colors["Label"] = ToArray(PanelKit.LabelColor);
       data.Colors["Value"] = ToArray(PanelKit.ValueColor);
       data.Colors["Accent"] = ToArray(PanelKit.AccentColor);
       data.Colors["ToggleOff"] = ToArray(PanelKit.ToggleOffColor);
       data.Colors["Warning"] = ToArray(PanelKit.WarningColor);
       data.Colors["Good"] = ToArray(PanelKit.GoodColor);
       data.Colors["RowSeparator"] = ToArray(PanelKit.RowSeparatorColor);
       data.Colors["Highlight"] = ToArray(PanelKit.HighlightColor);
       data.Colors["GraphBg"] = ToArray(PanelKit.GraphBgColor);
   ```

   ```csharp
   // SettingsStore.cs L242-L249
   private static float[] ToArray(float4 color) => new[] { color.X, color.Y, color.Z, color.W };

   private static float4 ReadColor(SettingsData data, string key, float4 fallback)
   {
       if (data.Colors.TryGetValue(key, out float[]? values) && values != null && values.Length >= 4)
           return new float4(values[0], values[1], values[2], values[3]);
       return fallback;
   }
   ```

7. **Give late-constructed sections their saved state.** Section objects register in constructors that run when .NET first initialises `FlightHud` or `EditorPanel`, so their order relative to `Load` is not fixed. `Register` applies the stored state at registration and `Apply` re-applies to every registered section; `Capture` writes the loaded dictionary first and overlays the registered sections, so sections not constructed this session keep their saved state:

   ```csharp
   // SectionHost.cs L31-L40
   internal static class SectionRegistry
   {
       public static readonly List<HudSection> All = new();

       public static void Register(HudSection section)
       {
           All.Add(section);
           SettingsStore.ApplySection(section);
       }
   }
   ```

   ```csharp
   // SettingsStore.cs L185-L188
   foreach (KeyValuePair<string, SectionStateData> pair in SectionStates)
       data.Sections[pair.Key] = pair.Value;
   foreach (HudSection section in SectionRegistry.All)
       data.Sections[section.Id] = new SectionStateData { Open = section.Open, Popped = section.Popped };
   ```

   ```csharp
   // SettingsStore.cs L228-L232
   SectionStates.Clear();
   foreach (KeyValuePair<string, SectionStateData> pair in data.Sections)
       SectionStates[pair.Key] = pair.Value;
   foreach (HudSection section in SectionRegistry.All)
       ApplySection(section);
   ```

## Complete example

| File | Role |
|---|---|
| [SettingsStore.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SettingsStore.cs) | Data classes, tick, load, save, capture, apply |
| [SectionHost.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SectionHost.cs#L10-L40) | `HudSection` registration that applies saved state |
| [FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L96-L101) and [EditorPanel.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/EditorPanel.cs#L25-L30) | Where `Tick()` is called |

## Variations and gotchas

- **Instance paths.** The folder ignores `Constants.DocumentsFolderPath`; use the constant (as in [Persist a settings object with override-only colors and tuning, saved by a debounced dirty flag](persist-mod-settings-as-json-2.md)) if players run instance paths.
- **Late first load.** Because the first `Tick()` is the first draw, a setting read before then (a menu toggle in the main menu) sees defaults.
- **No unload flush.** Add a `SaveNow` at `[StarMapUnload]` if the last two seconds matter; resolve the folder before then if it depends on the redirected getter (see approach 2).
- **Not atomic.** `File.WriteAllText` truncates then writes; see [Write config files atomically and load hand-edited ones without losing them](../write-and-load-config-files-safely/overview.md) for the temp-and-move variant.
- **Snapshot cost.** Serialising every two seconds allocates a few kilobytes; fine at this size, wasteful for a large DTO.
- **Corrupt file.** A parse failure falls back to defaults, and the next change then overwrites the unreadable file (compare the policy in [Write config files atomically and load hand-edited ones without losing them](../write-and-load-config-files-safely/overview.md)).

## See also

- [Persist a settings object with override-only colors and tuning, saved by a debounced dirty flag](persist-mod-settings-as-json-2.md), [overview](./overview.md)
- [JSON settings with autosave](../../../../mods/kitten-engineer-redux/features/json-settings-autosave.md)
- [Write config files atomically and load hand-edited ones without losing them](../write-and-load-config-files-safely/overview.md), [Find where KSA keeps user data and choose where a mod stores its config and data files](../../user-data/choose-where-a-mod-stores-its-files/overview.md), [Persist ImGui window state and layouts](../../user-data/persist-imgui-window-state/overview.md)
