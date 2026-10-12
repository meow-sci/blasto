---
type: How-To
title: Rewrite a flat settings.ini on every widget change
description: Keep every option in one Key=Value file next to the mod DLL, read it once at AllModsLoaded with TryParse and default constants, create it from defaults when missing, and call SaveSettings from each changed widget
category: persistence
group: settings-formats
topic: persist-mod-settings-ini
approach: 1
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ohmystars/features/ini-settings-persistence]
techniques: [toml-config, imgui-window]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapBeforeGui]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.Mod, Brutal.ImGuiApi.ImGui]
tags: [settings, ini, config, invariant-culture, defaults, write-on-change]
---

Outcome: every overlay toggle, color, opacity, grid option and the open state of two windows persists across launches in `settings.ini`, with no library and no save button. OhMyStars reads the file once at startup and rewrites it whenever a widget changes. Demonstrated by [settings.ini persistence](../../../../mods/ohmystars/features/ini-settings-persistence.md). Where the file lives and how updates treat it is in [Find where KSA keeps user data and choose where a mod stores its config and data files](../../user-data/choose-where-a-mod-stores-its-files/overview.md).

## Background: how the game does it

The game offers nothing for this; the file is the mod's. Two engine facts matter: ImGui remembers a window's position and size itself but never whether it is open, and the stock `ConsoleStyle.BeginWindow` forces `NoSavedSettings` ([current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L703)), so the mod calls `ImGui.Begin` directly and keeps the open flag in its own file (the geometry half is [Persist ImGui window state and layouts](../../user-data/persist-imgui-window-state/overview.md)). The mod's own folder comes from the loaded assembly ([src/ModPaths.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/ModPaths.cs#L11-L40)).

## Lifecycle and timing

| Piece | Hook | Lifecycle |
|---|---|---|
| `LoadSettings()` | `[StarMapAllModsLoaded]` `OnFullyLoaded`, after `StellariumRenderer.Init` ([src/OhMyStarsEntryPoint.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsEntryPoint.cs#L13-L26)) | `startup:all-mods-loaded` |
| `SaveSettings()` from widgets | the window's draw code | `frame:draw-ui` |
| `PersistWindowOpenStates()` | `[StarMapBeforeGui]` ([src/OhMyStarsEntryPoint.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsEntryPoint.cs#L44-L49)) | `frame:draw-ui` |

All synchronous on the main thread. Because `StarMapBeforeGui` does not run while the HUD is hidden, nothing is written then; see the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Resolve the path from the loaded assembly.** `ModPaths.Combine("settings.ini")` is the directory of `OhMyStars.dll`; this stays correct when a loader other than the default mods folder places the mod ([src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L1520-L1522)).

2. **Define a `Defaults` class** of constants and use it for both the loader fallbacks and "Reset to defaults":

   ```csharp
   // src/OhMyStarsWindow.cs L28-L50
   private static class Defaults {
       public const bool ShowStarNames = false;
       public const bool ShowIAUConstellations = false;
       public const bool ShowAsterisms = true;
       public const bool ShowAsterismNames = true;
       public const bool ShowNavballMarker = false;
       public const bool ShowNavballMarkerLabel = true;
       public const bool ShowRaDecGrid = false;
       public const int DecSpacingDegrees = 10;
       public const bool ShowRaMinuteBands = false;
       public const float GridLineThickness = 1f;
       public const WindowDisplay Tab = WindowDisplay.Settings;
       public const float IAULineOpacity = 0.4f;
       public static readonly float3 IAULineColor = new float3(1f, 1f, 1f);
       public const float AsterismLineOpacity = 0.4f;
       public static readonly float3 AsterismLineColor = new float3(0f, 0f, 1f);
       public const float AlignmentRotationXDegrees = 0f;
       public const float AlignmentRotationYDegrees = 0f;
       public const float AlignmentRotationZDegrees = 0f;
       public const bool StarPointerDualLines = false;
       public static readonly float3 StarPointerColor = new float3(1f, 1f, 1f);
       public const bool StarPointerRainbow = false;
   }
   ```

3. **Parse tolerantly.** Blank lines, `;` and `#` comments and any `[section]` line are skipped; the first `=` splits key and value; keys are case-insensitive:

   ```csharp
   // src/OhMyStarsWindow.cs L1524-L1545
   private static Dictionary<string, string> ReadSettingsFile(string settingsPath) {
       Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

       foreach(string rawLine in File.ReadAllLines(settingsPath)) {
           string line = rawLine.Trim();
           if(line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal) ||
              line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith("[", StringComparison.Ordinal)) {
               continue;
           }

           int separatorIndex = line.IndexOf('=');
           if(separatorIndex <= 0) {
               continue;
           }

           string key = line[..separatorIndex].Trim();
           string value = line[(separatorIndex + 1)..].Trim();
           values[key] = value;
       }

       return values;
   }
   ```

4. **Typed readers return the default on any failure**, with the invariant culture for numbers and `Enum.TryParse` for the tab:

   ```csharp
   // src/OhMyStarsWindow.cs L1547-L1589
   private static bool ReadBool(Dictionary<string, string> values, string key, bool defaultValue) {
       if(values.TryGetValue(key, out string? rawValue) && bool.TryParse(rawValue, out bool parsed)) {
           return parsed;
       }

       return defaultValue;
   }

   private static WindowDisplay ReadTab(Dictionary<string, string> values, string key, WindowDisplay defaultValue) {
       if(values.TryGetValue(key, out string? rawValue) &&
          Enum.TryParse(rawValue, ignoreCase: true, out WindowDisplay parsed)) {
           return NormalizeTab(parsed);
       }

       return defaultValue;
   }

   // SpectrumLegend is a sub-view of the Information tab, so it is persisted as its parent tab.
   private static WindowDisplay NormalizeTab(WindowDisplay display) {
       return display == WindowDisplay.SpectrumLegend ? WindowDisplay.Information : display;
   }

   private static WindowDisplay GetPersistableTab() {
       return NormalizeTab(_display);
   }

   private static float ReadFloat(Dictionary<string, string> values, string key, float defaultValue) {
       if(values.TryGetValue(key, out string? rawValue) &&
          float.TryParse(rawValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out float parsed)) {
           return parsed;
       }

       return defaultValue;
   }

   private static int ReadInt(Dictionary<string, string> values, string key, int defaultValue) {
       if(values.TryGetValue(key, out string? rawValue) &&
          int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)) {
           return parsed;
       }

       return defaultValue;
   }
   ```

5. **Load: create the file if missing, otherwise assign every static setting.**

   ```csharp
   // src/OhMyStarsWindow.cs L131-L160
   public static void LoadSettings() {
       string? settingsPath = GetSettingsFilePath();
       if(string.IsNullOrWhiteSpace(settingsPath)) {
           return;
       }

       if(!File.Exists(settingsPath)) {
           SaveSettings();
           return;
       }

       Dictionary<string, string> values = ReadSettingsFile(settingsPath);

       _showWindow = ReadBool(values, "ShowWindow", true);
       StarsEditWindow.IsOpen = ReadBool(values, "ShowStarEditor", true);
       _savedShowWindow = _showWindow;
       _savedShowStarEditor = StarsEditWindow.IsOpen;

       StellariumRenderer.showStarNames = ReadBool(values, "ShowStarNames", Defaults.ShowStarNames);
       StellariumRenderer.showIAUConstellations = ReadBool(values, "ShowIAUConstellations", Defaults.ShowIAUConstellations);
       StellariumRenderer.showAsterisms = ReadBool(values, "ShowAsterisms", Defaults.ShowAsterisms);
       StellariumRenderer.showAsterismNames = ReadBool(values, "ShowAsterismNames", Defaults.ShowAsterismNames);
       NavballMarkerRenderer.showNavballMarker = ReadBool(values, "ShowNavballMarker", Defaults.ShowNavballMarker);
       NavballMarkerRenderer.showMarkerLabel = ReadBool(values, "ShowNavballMarkerLabel", Defaults.ShowNavballMarkerLabel);
       StellariumRenderer.showRaDecGrid = ReadBool(values, "ShowRaDecGrid", Defaults.ShowRaDecGrid);
       int decSpacing = ReadInt(values, "DecSpacingDegrees", Defaults.DecSpacingDegrees);
       StellariumRenderer.decSpacingDegrees = decSpacing is 1 or 5 ? decSpacing : Defaults.DecSpacingDegrees;
       StellariumRenderer.showRaMinuteBands = ReadBool(values, "ShowRaMinuteBands", Defaults.ShowRaMinuteBands);
       StellariumRenderer.gridLineThickness = ReadFloat(values, "GridLineThickness", Defaults.GridLineThickness);
   ```

   The remaining lines of `LoadSettings` read the colors, opacities and the pointer options the same way ([src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L161-L185)). A value such as `DecSpacingDegrees` is validated after reading (`decSpacing is 1 or 5 ? decSpacing : Defaults.DecSpacingDegrees`) because `TryParse` only proves it is a number.

6. **Save: rebuild all lines and write the whole file** with `CultureInfo.InvariantCulture` formatting:

   ```csharp
   // src/OhMyStarsWindow.cs L187-L196, L224-L229
   public static void SaveSettings() {
       string? settingsPath = GetSettingsFilePath();
       if(string.IsNullOrWhiteSpace(settingsPath)) {
           return;
       }

       string[] lines = new[] {
           "[Window]",
           $"ShowWindow={_showWindow.ToString(CultureInfo.InvariantCulture)}",
           $"ShowStarEditor={StarsEditWindow.IsOpen.ToString(CultureInfo.InvariantCulture)}",
   // ...
       };

       File.WriteAllLines(settingsPath, lines);
       _savedShowWindow = _showWindow;
       _savedShowStarEditor = StarsEditWindow.IsOpen;
   }
   ```

   The elided lines are the other options in the same `Key=value` form; the end of the method records what was written, so the change detector of step 8 has a baseline.

7. **Call `SaveSettings()` from each widget's change branch.** `ConsoleWidgets.Checkbox`, `SliderFloat` and the color rows return `true` on a change:

   ```csharp
   // src/OhMyStarsWindow.cs L292-L298
   bool showStarNamesTop = StellariumRenderer.showStarNames;
   ConsoleWidgets.BeginRow("Show Star Names");
   if(ConsoleWidgets.Checkbox("ShowStarNames", ref showStarNamesTop, pending: false)) {
       StellariumRenderer.showStarNames = showStarNamesTop;
       SaveSettings();
   }
   ConsoleWidgets.EndRow();
   ```

8. **Catch the open flag changing from outside the widgets.** The window flag also changes from the ModMenu entry and from the window's close button, so compare it with the last saved value every frame:

   ```csharp
   // src/OhMyStarsWindow.cs L120-L129
   // Open/closed state last written to settings.ini; ImGui's imgui.ini only keeps position/size, not visibility.
   private static bool _savedShowWindow = true;
   private static bool _savedShowStarEditor = true;

   // Called every frame so toggles from the mod menu and the windows' close (X) buttons both get persisted.
   public static void PersistWindowOpenStates() {
       if(_showWindow != _savedShowWindow || StarsEditWindow.IsOpen != _savedShowStarEditor) {
           SaveSettings();
       }
   }
   ```

   The legend sub-view is persisted as its parent tab (`NormalizeTab`, [src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L1564-L1571)) so a restored value never points at a view that cannot be the starting tab.

9. **Reset to defaults** assigns the `Defaults` constants and calls `SaveSettings()` ([src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L231-L254)).

## Complete example

| File | Role |
|---|---|
| [src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L120-L254) | Persist open states, load, save, reset |
| [src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L1520-L1589) | File path, parser, typed readers |
| [src/settings.ini](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/settings.ini) | Shipped defaults |
| [src/OhMyStars.csproj](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStars.csproj#L37-L48) | Deploy target that copies the file |

## Variations and gotchas

- **Writes on every change.** A slider drag changes the value on many frames and each calls `SaveSettings()`, which rewrites the whole file synchronously on the main thread. For frequent changes see [Write a sectioned palette file when a dirty flag is set, on window close and at unload](persist-mod-settings-ini-4.md) (dirty flag) or [Write config files atomically and load hand-edited ones without losing them](../write-and-load-config-files-safely/overview.md) (debounce).
- **Not atomic.** `File.WriteAllLines` truncates then writes; a crash mid-write leaves a short file. The reader's per-key fallbacks make a short file harmless; see [Write config files atomically and load hand-edited ones without losing them](../write-and-load-config-files-safely/overview.md) for temp-and-move.
- **The source copy doubles as the shipped defaults, and the build overwrites the installed copy.** `src/settings.ini` is copied to the build output (`CopyToOutputDirectory=Always`) and to the installed mod folder with `SkipUnchangedFiles="false"` on each build ([src/OhMyStars.csproj](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStars.csproj#L43), [src/OhMyStars.csproj](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStars.csproj#L68-L70)); the README tells developers to close the game before building so it cannot write test settings over the defaults. A player update that replaces the mod folder replaces the file; seed a separate live copy if that matters ([Find where KSA keeps user data and choose where a mod stores its config and data files](../../user-data/choose-where-a-mod-stores-its-files/overview.md)).
- **Initializer versus default.** The static field initializers differ from the `Defaults` constants for some settings (line color and opacity), so a file created from "current values" on first run differs from the one a reset produces.
- **Hidden HUD.** No writes while the HUD is hidden.

## See also

- [Load settings first at StarMapImmediateLoad and rewrite on change](persist-mod-settings-ini-2.md), [Save an eleven-key hud.ini only when the player presses a button](persist-mod-settings-ini-3.md), [Write a sectioned palette file when a dirty flag is set, on window close and at unload](persist-mod-settings-ini-4.md), [overview](./overview.md)
- [settings.ini persistence](../../../../mods/ohmystars/features/ini-settings-persistence.md)
- [Persist ImGui window state and layouts](../../user-data/persist-imgui-window-state/overview.md), [Build a mod settings window (and developer panel)](../../../ui/windows/build-a-settings-window/overview.md)
