---
type: How-To
title: Flush on close and unload, skip junk keys on load and migrate the previous format
description: Write side - a dirty flag set by editors, open flags compared every frame, written when the window closes and again at StarMapUnload; load side - unknown keys and malformed values skipped, the old flat color format and purrTTY's legacy flat settings converted into the current structure
category: persistence
group: settings-formats
topic: write-and-load-config-files-safely
approach: 4
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/ksa-measuretools/features/color-palettes-persistence
  - mods/purrtty/features/toml-themes-and-settings
techniques: [starmap-lifecycle, toml-config]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapUnload]
lifecycle: [frame:draw-ui, event:starmap-unload, startup:all-mods-loaded]
game_types: []
tags: [config, migration, dirty-flag, flush-on-close, tolerant-parser, legacy-format]
---

Outcome: two mods that write rarely, read tolerantly and carry an older file forward. KSA-MeasureTools flushes a dirty palette file when its window closes and at unload; purrTTY writes atomically and converts a legacy flat settings file into the structured one. Demonstrated by [Named color palettes persisted to a cfg file next to the mod assembly](../../../../mods/ksa-measuretools/features/color-palettes-persistence.md) and [TOML settings and alacritty-format themes](../../../../mods/purrtty/features/toml-themes-and-settings.md). The full walkthrough of the palette file is [Write a sectioned palette file when a dirty flag is set, on window close and at unload](../persist-mod-settings-ini/persist-mod-settings-ini-4.md) and of the TOML structure [Persist a structured TOML config with a do-not-touch table, legacy migration and one shared instance](../persist-mod-settings-toml/persist-mod-settings-toml-2.md).

## Background: how the game does it

Nothing in the engine helps; the relevant facts are lifecycle ones: window close and `StarMapUnload` are the two moments the mod controls at the end of a session, and a kill skips both (see the [overview](./overview.md#engine-background)).

## Lifecycle and timing

| Step | Hook | Lifecycle |
|---|---|---|
| Load (including migration) | `OnFullyLoaded` (`MeasureColors.Load()`) | `startup:all-mods-loaded` |
| Mark dirty | editors in the window | `frame:draw-ui` |
| Flush on close | `OnClosed()`, shared by both close paths | `frame:draw-ui` |
| Flush at exit | `[StarMapUnload]`: `SaveIfDirty()` then `Reset()` | `event:starmap-unload` |

See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

### Write side

1. **Dirty flag plus two flush points.** Every mutator sets `_dirty`; `SaveIfDirty` writes only if set. The window calls it from the single teardown shared by the close button and the menu close:

   ```csharp
   // MeasureTools/Features/Measure/MeasureWindow.cs L75-L83
   // Shared teardown for both close paths (Close and the title-bar button):
   // measurements are ephemeral by design, the combo cache must not outlive
   // the window, and edited colors persist here.
   private void OnClosed()
   {
       MeasureState.ClearAll();
       ResetAutoPreview();
       MeasureColors.SaveIfDirty();
   }
   ```

   and unload calls it before resetting the static state:

   ```csharp
   // MeasureTools/Mod.cs L80-L99
       [StarMapUnload]
       public void Unload()
       {
           _harmony?.UnpatchAll(_harmony.Id);
           _harmony = null;

           MeasureColors.SaveIfDirty();
           MeasureColors.Reset();
           MeasureState.Reset();
           MeasureWindow.ResetStatic();
           MeasureOverlay.Reset();
           MeshFeatureCache.Reset();
           Patch_MouseButton.Reset();
           DebugConfig.Reset();
           LogHelper.Reset();
   #if DEBUG
           PerfTracker.Reset();
   #endif
           DefaultCategory.Log.Info("[MeasureTools] Unloaded.");
       }
   ```

2. **Make a failed load unable to destroy the file.** If the file could not be read, clear the dirty flag the static constructor set, so the unload save does not replace the unreadable file with the shipped defaults. A clean load clears it too (no pointless rewrite at the first close):

   ```csharp
   // MeasureTools/Features/Measure/MeasureColors.cs L280-L314
           if (flat != null)
               loaded.Insert(0, flat);
           if (loaded.Count == 0)
               return;
           Palettes.Clear();
           Palettes.AddRange(loaded);
           if (flat != null)
           {
               // Migration keeps the user's tweaked colors active and adds the
               // shipped palettes alongside.
               RestoreDefaultPalettes();
               _dirty = true;
               active = 0;
           }
           else
           {
               // A clean load matches the file; without this the dirty flag
               // from the static constructor forces a pointless rewrite on the
               // first close of every session.
               _dirty = false;
           }
           _activeIndex = Math.Clamp(active, 0, Palettes.Count - 1);
           if (DebugConfig.Measure)
               DefaultCategory.Log.Debug($"[MeasureTools] {Palettes.Count} palette(s) loaded from {path}, active '{Active.Name}'.");
       }
       catch (Exception ex)
       {
           // File IO can legitimately fail (permissions, sync tools); the
           // shipped palettes keep the mod fully usable. Clearing the dirty flag
           // the static constructor set is what stops the unload save from
           // overwriting a file we could not read with those defaults.
           _dirty = false;
           LogHelper.WarnOnce("colors-load", $"[MeasureTools] Could not load {path}, keeping default palettes in memory and leaving {FileName} untouched: {ex.Message}");
       }
   }
   ```

3. **Prefer atomic writes where cheap.** purrTTY writes every config and theme through temp-then-rename:

   ```csharp
   // purrTTY.Display/Configuration/AtomicFile.cs L8-L16
   internal static class AtomicFile
   {
       public static void WriteAllText(string path, string contents)
       {
           var tmp = path + ".tmp";
           File.WriteAllText(tmp, contents);
           File.Move(tmp, path, overwrite: true);
       }
   }
   ```

   MeasureTools uses `File.WriteAllLines` directly; its file is small and a short file is recovered by the tolerant reader below.

### Load side

4. **Skip what you cannot read, keep what you can.** Unknown keys are ignored by the `switch`, a color with the wrong number of parts or an out-of-range channel fails `TryParseColor` and the line is skipped, a `[section]` line starts a palette:

   ```csharp
   // MeasureTools/Features/Measure/MeasureColors.cs L231-L279
   public static void Load()
   {
       string path = ConfigPath();
       try
       {
           if (!File.Exists(path))
               return;
           var loaded = new List<ColorPalette>();
           ColorPalette? current = null;
           ColorPalette? flat = null;
           int active = 0;
           foreach (string rawLine in File.ReadAllLines(path))
           {
               string line = rawLine.Trim();
               if (line.Length == 0)
                   continue;
               if (line.StartsWith('[') && line.EndsWith(']'))
               {
                   string name = line[1..^1].Trim();
                   current = new ColorPalette { Name = name.Length > 0 ? name : "Palette" };
                   loaded.Add(current);
                   continue;
               }
               int separator = line.IndexOf('=');
               if (separator <= 0)
                   continue;
               string key = line[..separator].Trim();
               string value = line[(separator + 1)..];
               if (key == "active")
               {
                   int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out active);
                   continue;
               }
               if (!TryParseColor(value, out byte4 color))
                   continue;
               // Color lines before any [section] come from the pre-palette
               // flat format and migrate into a "Custom" palette.
               ColorPalette target = current ?? (flat ??= new ColorPalette { Name = "Custom" });
               switch (key)
               {
                   case nameof(ColorPalette.Measure): target.Measure = color; break;
                   case nameof(ColorPalette.Highlight): target.Highlight = color; break;
                   case nameof(ColorPalette.Pending): target.Pending = color; break;
                   case nameof(ColorPalette.FeatureDot): target.FeatureDot = color; break;
                   case nameof(ColorPalette.Plane): target.Plane = color; break;
                   case nameof(ColorPalette.LabelText): target.LabelText = color; break;
                   case nameof(ColorPalette.LabelPlate): target.LabelPlate = color; break;
               }
           }
   ```

5. **Migrate the previous format into the current structure.** Color lines before any `[section]` belong to the pre-palette flat format; they become a "Custom" palette inserted first, the shipped palettes are added back, and the file is marked dirty so it is rewritten in the new layout:

   ```csharp
   // MeasureTools/Features/Measure/MeasureColors.cs L280-L293
   if (flat != null)
       loaded.Insert(0, flat);
   if (loaded.Count == 0)
       return;
   Palettes.Clear();
   Palettes.AddRange(loaded);
   if (flat != null)
   {
       // Migration keeps the user's tweaked colors active and adds the
       // shipped palettes alongside.
       RestoreDefaultPalettes();
       _dirty = true;
       active = 0;
   }
   ```

6. **Detect the shape and convert (purrTTY).** The DOM is parsed first; a file with a `settings` or `do-not-touch` table is the structured format, anything else is deserialised as the legacy flat class and mapped field by field into the structured one, then normalised:

   ```csharp
   // purrTTY.Display/Configuration/ThemeConfiguration.cs L573-L599
           var tomlTable = TomlSerializer.Deserialize<TomlTable>(tomlContent, TomlOptions);
           if (tomlTable is null)
           {
               return new ThemeConfiguration();
           }

           ThemeConfiguration? config;
           if (IsStructuredFormat(tomlTable))
           {
               config = TomlSerializer.Deserialize<ThemeConfiguration>(tomlContent, TomlOptions);
               config?.Normalize();
           }
           else
           {
               var legacyConfig = TomlSerializer.Deserialize<LegacyThemeConfiguration>(tomlContent, TomlOptions);
               config = FromLegacyConfiguration(legacyConfig);
           }

           return config ?? new ThemeConfiguration();
       }
       catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is TomlException)
       {
           // Log error and return default configuration
           ModLog.Log.Debug($"Error loading theme configuration: {ex.Message}");
           return new ThemeConfiguration();
       }
   }
   ```

   ```csharp
   // purrTTY.Display/Configuration/ThemeConfiguration.cs L663-L700
   private static ThemeConfiguration FromLegacyConfiguration(LegacyThemeConfiguration? legacy)
   {
       if (legacy is null)
       {
           return new ThemeConfiguration();
       }

       var config = new ThemeConfiguration
       {
           SelectedThemeName = legacy.SelectedThemeName,
           BackgroundOpacity = legacy.BackgroundOpacity,
           HideUiWhenNotHovered = legacy.HideUiWhenNotHovered,
           ForegroundOpacity = legacy.ForegroundOpacity,
           CellBackgroundOpacity = legacy.CellBackgroundOpacity,
           FontFamily = legacy.FontFamily,
           FontSize = legacy.FontSize,
           DefaultShellType = legacy.DefaultShellType,
           CustomShellPath = legacy.CustomShellPath,
           DefaultShellArguments = legacy.DefaultShellArguments ?? new List<string>(),
           WslDistribution = legacy.WslDistribution,
           DefaultCustomGameShellId = legacy.DefaultCustomGameShellId,
           GameShellPrompt = legacy.GameShellPrompt,
           ToggleHotkeyKey = legacy.ToggleHotkeyKey,
           ToggleHotkeyShift = legacy.ToggleHotkeyShift,
           ToggleHotkeyCtrl = legacy.ToggleHotkeyCtrl,
           ToggleHotkeyAlt = legacy.ToggleHotkeyAlt,
           ToggleHotkeySuper = legacy.ToggleHotkeySuper,
           TerminalWindowPosX = legacy.TerminalWindowPosX,
           TerminalWindowPosY = legacy.TerminalWindowPosY,
           TerminalWindowWidth = legacy.TerminalWindowWidth,
           TerminalWindowHeight = legacy.TerminalWindowHeight,
           TerminalColumns = legacy.TerminalColumns,
           TerminalRows = legacy.TerminalRows
       };

       config.Normalize();
       return config;
   }
   ```

   Read and I/O failures return defaults and are logged at debug level.

## Complete example

| File | Role |
|---|---|
| [MeasureTools/Features/Measure/MeasureColors.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureColors.cs) | Dirty flag, tolerant load, migration, save |
| [MeasureTools/Features/Measure/MeasureWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureWindow.cs#L75-L83) | Flush on close |
| [MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs#L80-L99) | Flush at unload |
| [purrTTY.Display/Configuration/ThemeConfiguration.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Configuration/ThemeConfiguration.cs#L556-L700) | Load with structured/legacy detection, save |
| [purrTTY.Display/Configuration/AtomicFile.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Configuration/AtomicFile.cs) | Temp-then-rename |

## Variations and gotchas

- **Both flush points are needed.** Unload alone loses everything on a crash or kill; close alone loses an edit made after the last close.
- **Unload is after unpatching.** The unload write needs only `System.IO`; touching patched or game state there is unsafe (see [Clean up on game exit and relaunch the game from a mod](../../../getting-started/lifecycle/clean-up-on-game-exit/overview.md)). Resolve paths earlier if they depend on a redirected getter.
- **Migration is one-way and silent.** The old layout is overwritten at the next save; keep a copy if downgrades matter.
- **Skipping is not reporting.** A bad line is dropped without telling the player (MeasureTools logs only unreadable-file failures; purrTTY logs at debug level). Log skipped keys once if support requests matter.
- **An unreadable purrTTY file is replaced.** `Load` returns defaults on `TomlException` and a later `Save()` replaces the file; MeasureTools avoids that with the dirty-flag trick of step 2.

## See also

- [Mark dirty, debounce, swap the file in atomically, and retry deserialisation by dropping the broken field](write-and-load-config-files-safely-1.md), [Save from Task.Run with an atomic helper, clamp bad values on load and leave an unparseable file alone](write-and-load-config-files-safely-3.md), [overview](./overview.md)
- [Named color palettes persisted to a cfg file next to the mod assembly](../../../../mods/ksa-measuretools/features/color-palettes-persistence.md), [TOML settings and alacritty-format themes](../../../../mods/purrtty/features/toml-themes-and-settings.md)
- [Persist user-created palettes, presets and content files](../../user-data/persist-user-created-presets-and-content/overview.md), [Clean up on game exit and relaunch the game from a mod](../../../getting-started/lifecycle/clean-up-on-game-exit/overview.md)
