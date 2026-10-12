---
type: How-To
title: Write a sectioned palette file when a dirty flag is set, on window close and at unload
description: Keep named color palettes in colors.cfg with one [Name] section each, skip unknown keys and malformed values on load, migrate the older flat format, and save only when a dirty flag is set at window close and StarMapUnload
category: persistence
group: settings-formats
topic: persist-mod-settings-ini
approach: 4
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-measuretools/features/color-palettes-persistence]
techniques: [starmap-lifecycle, toml-config]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapUnload]
lifecycle: [startup:all-mods-loaded, frame:draw-ui, event:starmap-unload]
game_types: [KSA.Mod, Brutal.ImGuiApi.ImGui]
tags: [settings, ini, config, dirty-flag, sections, migration, palettes]
---

Outcome: user-editable color palettes persisted in a sectioned file that is written rarely. KSA-MeasureTools stores `active=<index>` and then one `[Name]` section per palette in `colors.cfg` next to the mod DLL; editors set a dirty flag, and the file is written when the window closes and at unload. The same file migrates an older flat format. Demonstrated by [Named color palettes persisted to a cfg file next to the mod assembly](../../../../mods/ksa-measuretools/features/color-palettes-persistence.md). The palette model and the color-picker round trip are in [Persist user-created palettes, presets and content files](../../user-data/persist-user-created-presets-and-content/overview.md).

## Background: how the game does it

The engine contributes only two timing facts: `StarMapAllModsLoaded` runs before the universe exists (a file read needs no game state), and `StarMapUnload` runs after the game loop has returned and StarMap has removed every Harmony patch, so unload is the last chance to flush but cannot rely on patched behaviour (see [Clean up on game exit and relaunch the game from a mod](../../../getting-started/lifecycle/clean-up-on-game-exit/overview.md)). A file that is only written at unload is lost if the process is killed, so the window-close write exists too.

## Lifecycle and timing

| Piece | Hook | Lifecycle |
|---|---|---|
| `MeasureColors.Load()` | `OnFullyLoaded` (`[StarMapAllModsLoaded]`, [MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs#L19-L30)) | `startup:all-mods-loaded` |
| editors call `MarkDirty()`/`SetActive` | the colors section of the window | `frame:draw-ui` |
| `SaveIfDirty()` | the window's `OnClosed()` (both close paths) ([MeasureTools/Features/Measure/MeasureWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureWindow.cs#L75-L83)) | `frame:draw-ui` |
| `SaveIfDirty()` then `Reset()` | `[StarMapUnload]` ([MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs#L80-L99)) | `event:starmap-unload` |

See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Hold the whole state in a static class with a dirty flag.** The class comment is the format spec: an `active=index` line, then one `[Name]` section with `Name=r,g,b,a` lines per palette; written on window close and unload; loaded at mod load; a pre-palette flat file migrates into a "Custom" palette:

   ```csharp
   // MeasureTools/Features/Measure/MeasureColors.cs L37-L72
   // The palette collection and the active selection the overlay draws with.
   // Persisted as colors.cfg next to the mod assembly: an "active=index" line,
   // then one [Name] section with Name=r,g,b,a lines per palette. Written on
   // window close and unload, loaded at mod load; a pre-palette flat file (color
   // lines without any section) migrates into a "Custom" palette ahead of the
   // shipped ones. Main thread only (draw hook and window).
   internal static class MeasureColors
   {
       private const string FileName = "colors.cfg";

       public static readonly List<ColorPalette> Palettes = new();

       private static int _activeIndex;
       private static bool _dirty;

       public static int ActiveIndex => _activeIndex;

       public static ColorPalette Active => Palettes[_activeIndex];

       // The draw code reads the scheme through these, unaware of palettes. The
       // preview variants keep the pending hue at reduced alpha, so one picker
       // governs the whole pending/preview family.
       public static byte4 Measure => Active.Measure;
       public static byte4 Highlight => Active.Highlight;
       public static byte4 Pending => Active.Pending;
       public static byte4 FeatureDot => Active.FeatureDot;
       public static byte4 Plane => Active.Plane;
       public static byte4 LabelText => Active.LabelText;
       public static byte4 LabelPlate => Active.LabelPlate;
       public static byte4 Preview => WithAlpha(Active.Pending, 160);
       public static byte4 PreviewFaint => WithAlpha(Active.Pending, 80);

       static MeasureColors()
       {
           RestoreDefaultPalettes();
       }
   ```

2. **Mark dirty in every mutator.** `SetActive`, `AddPalette`, `DeleteActive` and `RestoreDefaultPalettes` set `_dirty = true` ([MeasureTools/Features/Measure/MeasureColors.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureColors.cs#L135-L193)); the color editors in the window call `MarkDirty()` after a change ([MeasureTools/Features/Measure/MeasureWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureWindow.cs#L323), [MeasureTools/Features/Measure/MeasureWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureWindow.cs#L338)).

3. **Resolve the path from the assembly:**

   ```csharp
   // MeasureTools/Features/Measure/MeasureColors.cs L225-L229
   private static string ConfigPath()
   {
       string directory = Path.GetDirectoryName(typeof(MeasureColors).Assembly.Location) ?? ".";
       return Path.Combine(directory, FileName);
   }
   ```

4. **Load tolerantly.** Unknown keys and malformed values are skipped, a section header starts a palette, and `byte.TryParse` per channel validates colors. Lines before any section feed a `flat` palette (the migration case):

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

5. **Finish the load: migrate or mark clean.** A flat file is inserted first as "Custom" and the shipped palettes are added back; a clean load clears the dirty flag set by the static constructor, so there is no pointless rewrite at the first close:

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

   The catch block is the important design point: a file that could not be read must not be overwritten by the defaults at unload, so it clears the dirty flag and leaves `colors.cfg` untouched.

6. **Save only if dirty; invariant formatting; log a failure once:**

   ```csharp
   // MeasureTools/Features/Measure/MeasureColors.cs L316-L352
   public static void SaveIfDirty()
   {
       if (!_dirty)
           return;
       string path = ConfigPath();
       try
       {
           var lines = new List<string>(1 + Palettes.Count * 8)
           {
               "active=" + _activeIndex.ToString(CultureInfo.InvariantCulture),
           };
           foreach (ColorPalette palette in Palettes)
           {
               lines.Add("[" + palette.Name + "]");
               lines.Add(Format(nameof(ColorPalette.Measure), palette.Measure));
               lines.Add(Format(nameof(ColorPalette.Highlight), palette.Highlight));
               lines.Add(Format(nameof(ColorPalette.Pending), palette.Pending));
               lines.Add(Format(nameof(ColorPalette.FeatureDot), palette.FeatureDot));
               lines.Add(Format(nameof(ColorPalette.Plane), palette.Plane));
               lines.Add(Format(nameof(ColorPalette.LabelText), palette.LabelText));
               lines.Add(Format(nameof(ColorPalette.LabelPlate), palette.LabelPlate));
           }
           File.WriteAllLines(path, lines);
           _dirty = false;
           if (DebugConfig.Measure)
               DefaultCategory.Log.Debug($"[MeasureTools] {Palettes.Count} palette(s) saved to {path}.");
       }
       catch (Exception ex)
       {
           LogHelper.WarnOnce("colors-save", $"[MeasureTools] Could not save {path}: {ex.Message}");
       }
   }

   private static string Format(string name, byte4 color)
   {
       return string.Create(CultureInfo.InvariantCulture, $"{name}={color.R},{color.G},{color.B},{color.A}");
   }
   ```

7. **Flush at both exits.** The window's shared close path and the unload hook:

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

   `Reset()` after the save drops custom palettes and the selection and clears the dirty flag, so a reload of the mod in the same process starts clean ([MeasureTools/Features/Measure/MeasureColors.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureColors.cs#L195-L204)).

## Complete example

| File | Role |
|---|---|
| [MeasureTools/Features/Measure/MeasureColors.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureColors.cs) | Palette model, file format, load, save, migration |
| [MeasureTools/Features/Measure/MeasureWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureWindow.cs#L75-L83) | Save on window close |
| [MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs#L19-L99) | Load at AllModsLoaded, save and reset at unload |

## Variations and gotchas

- **Not atomic, and a kill loses edits.** The write is `File.WriteAllLines`; between a color edit and the window close the edit lives only in memory. See [Write config files atomically and load hand-edited ones without losing them](../write-and-load-config-files-safely/overview.md) for debounce and temp-and-move.
- **Unload runs after unpatching** and in a shutdown context; the file write needs only `System.IO`, which is why it is safe there. Do not touch ImGui or Vulkan in the unload path.
- **Section names are user data.** A `]` or newline in a palette name would corrupt the file; the mod uses names it generates ("Palette 3") and does not sanitise user input beyond `Trim()`.
- **Colors are `byte4` on disk** (0..255), converted to floats for `ImGui.ColorEdit4` and back with `ToFloat4`/`FromFloat4` (round half up), so the file round-trips exactly.

## See also

- [Rewrite a flat settings.ini on every widget change](persist-mod-settings-ini-1.md), [Save an eleven-key hud.ini only when the player presses a button](persist-mod-settings-ini-3.md), [overview](./overview.md)
- [Named color palettes persisted to a cfg file next to the mod assembly](../../../../mods/ksa-measuretools/features/color-palettes-persistence.md)
- [Persist user-created palettes, presets and content files](../../user-data/persist-user-created-presets-and-content/overview.md), [Write config files atomically and load hand-edited ones without losing them](../write-and-load-config-files-safely/overview.md), [Clean up on game exit and relaunch the game from a mod](../../../getting-started/lifecycle/clean-up-on-game-exit/overview.md)
