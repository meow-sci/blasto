---
type: How-To
title: Keep named, editable color palettes in a sectioned file with a ColorEdit4 round trip
description: Store shipped and user-made palettes of byte colors under one active index, edit them through float4 ColorEdit4 widgets without drift, derive related shades from one picker, and restore the shipped set by name without touching custom palettes
category: persistence
group: user-data
topic: persist-user-created-presets-and-content
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-measuretools/features/color-palettes-persistence]
techniques: [imgui-window, starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapAfterGui, starmap StarMapUnload]
lifecycle: [startup:all-mods-loaded, frame:draw-ui, event:starmap-unload]
game_types: [Brutal.ImGuiApi.ImGui, Brutal.Numerics.byte4]
tags: [palettes, colors, presets, coloredit4, byte4, imgui, user-content]
---
Outcome: a color scheme the player can edit, duplicate, rename, delete and switch, saved between sessions, where the shipped schemes can be restored at any time without losing the player's own. KSA-MeasureTools does this for the seven colors of its measuring overlay: four palettes ship with the mod (Red, Green, Blue, Contrast) and any number of user palettes sit beside them. Demonstrated by [Named color palettes persisted to a cfg file next to the mod assembly](../../../../mods/ksa-measuretools/features/color-palettes-persistence.md). The file mechanics of this mod (dirty flag, sectioned ini, save on close and unload) are the subject of [Write a sectioned palette file when a dirty flag is set, on window close and at unload](../../settings-formats/persist-mod-settings-ini/persist-mod-settings-ini-4.md); this article is about the palette model and the color round trip.

## Background: how the game does it

KSA exposes Dear ImGui through `Brutal.ImGuiApi`. `ImGui.ColorEdit4` edits a `float4` in place and takes `ImGuiColorEditFlags` such as `NoInputs`, `AlphaBar` and `AlphaPreviewHalf` ([current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L7121), [current/decomp/Brutal.ImGuiApi/ImGuiColorEditFlags.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiColorEditFlags.cs#L13-L22)). The game has no palette store a mod can borrow, so the mod owns the data model. Storing colors as `byte4` keeps the file human-readable (`Measure=235,100,90,235`) but means every picker interaction converts bytes to floats and back ([current/decomp/Brutal.Numerics/byte4.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Numerics/byte4.cs)).

## Lifecycle and timing

| Moment | Call | Why there |
|---|---|---|
| `startup:all-mods-loaded` (`[StarMapAllModsLoaded]`) | `MeasureColors.Load()` | No ImGui state is needed to read a file, and the overlay's first draw already sees the saved palette |
| `frame:draw-ui` (`[StarMapAfterGui]`) | window draw: switch, edit, rename, restore | ImGui widgets are only legal in the UI pass |
| Window closed | `SaveIfDirty()` | A close is the natural "done editing" moment, and a crash later cannot lose it |
| `event:starmap-unload` (`[StarMapUnload]`) | `SaveIfDirty()`, then `Reset()` | Catches edits made in a window that is still open at exit |

The palette file path comes from the mod assembly's own location, not from `Constants.DocumentsFolderPath`, so the unload save is unaffected by the fact that StarMap removes every Harmony patch (including the instance-path redirect of the documents folder) before it invokes `[StarMapUnload]`. A palette store keyed on the documents constant would have its unload-time save land in the default Documents folder instead; see [Find where KSA keeps user data and choose where a mod stores its config and data files](../choose-where-a-mod-stores-its-files/overview.md). The timing vocabulary is explained in the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Model a palette as a named bag of byte colors, plus a collection with an active index and a dirty flag.** The overlay never sees palettes; it reads static properties that forward to the active one, and the preview shades are derived rather than stored.

   ```csharp
   // MeasureTools/Features/Measure/MeasureColors.cs L10-L35, L47-L67
   internal sealed class ColorPalette
   {
       public string Name = "Palette";
       public byte4 Measure;
       public byte4 Highlight;
       public byte4 Pending;
       public byte4 FeatureDot;
       public byte4 Plane;
       public byte4 LabelText;
       public byte4 LabelPlate;

       public ColorPalette Clone(string name)
       {
           return new ColorPalette
           {
               Name = name,
               Measure = Measure,
               Highlight = Highlight,
               Pending = Pending,
               FeatureDot = FeatureDot,
               Plane = Plane,
               LabelText = LabelText,
               LabelPlate = LabelPlate,
           };
       }
   }
   // ...
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
   ```

2. **Ship defaults in code and make "restore" a merge by name.** The static constructor and the *Restore default palettes* button share one routine: it replaces a palette whose name matches a shipped one and appends missing ones, so custom palettes are never touched:

   ```csharp
   // MeasureTools/Features/Measure/MeasureColors.cs L179-L193
   // Re-adds the shipped palettes and restores their colors, matched by name;
   // custom palettes are untouched. Also the initial population, hence the
   // static constructor call.
   public static void RestoreDefaultPalettes()
   {
       foreach (ColorPalette shipped in CreateDefaultPalettes())
       {
           int existing = Palettes.FindIndex(p => p.Name == shipped.Name);
           if (existing >= 0)
               Palettes[existing] = shipped;
           else
               Palettes.Add(shipped);
       }
       _dirty = true;
   }
   ```

3. **Convert at the widget boundary and round the float back.** `ColorEdit4` works in `float4`. Convert on the way in with `/ 255f` and on the way out with `* 255f + 0.5f`, clamped, so the byte you stored is the byte you get back and no edit drifts by one level per round trip:

   ```csharp
   // MeasureTools/Features/Measure/MeasureColors.cs L211-L223
   public static float4 ToFloat4(byte4 color)
   {
       return new float4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
   }

   public static byte4 FromFloat4(float4 color)
   {
       return new byte4(
           (byte)Math.Clamp(color.X * 255f + 0.5f, 0f, 255f),
           (byte)Math.Clamp(color.Y * 255f + 0.5f, 0f, 255f),
           (byte)Math.Clamp(color.Z * 255f + 0.5f, 0f, 255f),
           (byte)Math.Clamp(color.W * 255f + 0.5f, 0f, 255f));
   }
   ```

   The widget row reports a change only when the user actually moved the picker, which is what sets the dirty flag:

   ```csharp
   // MeasureTools/Features/Measure/MeasureWindow.cs L345-L352
   private static bool DrawColorRow(ImString label, ref byte4 color)
   {
       float4 value = MeasureColors.ToFloat4(color);
       if (!ImGui.ColorEdit4(label, ref value, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf))
           return false;
       color = MeasureColors.FromFloat4(value);
       return true;
   }
   ```

4. **Derive related colors instead of storing them.** One picker governs the "pending" color; the two preview shades are the same hue at alpha 160 and 80. Fewer fields means fewer pickers and nothing to keep in sync (step 1 shows `Preview` and `PreviewFaint`).

5. **Manage the collection from the window.** *New* clones the active palette under a fresh name ("Palette 2", ...) and selects it; *Delete* is disabled while one palette remains because the overlay always needs an active scheme:

   ```csharp
   // MeasureTools/Features/Measure/MeasureColors.cs L150-L177
   // The New button: clone the active palette under a fresh name and select it.
   public static void AddPalette()
   {
       int n = Palettes.Count + 1;
       string name;
       do
       {
           name = "Palette " + n;
           n++;
       } while (Palettes.Exists(p => p.Name == name));
       Palettes.Add(Active.Clone(name));
       _activeIndex = Palettes.Count - 1;
       _dirty = true;
   }

   // The Delete button; the last remaining palette stays (the overlay always
   // needs an active scheme).
   public static void DeleteActive()
   {
       if (Palettes.Count <= 1)
           return;
       if (DebugConfig.Measure)
           DefaultCategory.Log.Debug($"[MeasureTools] Palette '{Active.Name}' deleted.");
       Palettes.RemoveAt(_activeIndex);
       if (_activeIndex >= Palettes.Count)
           _activeIndex = Palettes.Count - 1;
       _dirty = true;
   }
   ```

6. **Guard the rename field against the file format.** The names are section headers in the file, so `[`, `]` and `=` are stripped, and the `ImInputString` capacity must exceed the UTF-8 byte length of the loaded name or construction throws ([current/decomp/Brutal.ImGuiApi/ImInputString.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImInputString.cs)). The field is rebuilt whenever a different palette *object* becomes active, because restoring a default or deleting a non-last palette swaps the object at the same index:

   ```csharp
   // MeasureTools/Features/Measure/MeasureWindow.cs L304-L325
   ColorPalette active = MeasureColors.Active;
   if (!ReferenceEquals(_paletteNameFor, active) || _paletteNameInput == null)
   {
       _paletteNameFor = active;
       // Capacity must exceed the content bytes (ImInputString throws
       // otherwise); loaded cfg names can be longer than the UI would type.
       int capacity = Math.Max(64, System.Text.Encoding.UTF8.GetByteCount(active.Name) + 1);
       _paletteNameInput = new ImInputString(capacity, active.Name);
   }
   ImGui.Text("Name"u8);
   ImGui.SameLine();
   ImGui.SetNextItemWidth(-1f);
   if (ImGui.InputText("##palettename"u8, _paletteNameInput))
   {
       // Brackets and equals would corrupt the cfg format.
       string name = _paletteNameInput.ToString().Replace("[", "").Replace("]", "").Replace("=", "").Trim();
       if (name.Length > 0 && name != active.Name)
       {
           active.Name = name;
           MeasureColors.MarkDirty();
       }
   }
   ```

7. **Load tolerantly, write only when dirty.** The file is `active=<index>` plus one `[Name]` section of `Key=r,g,b,a` lines; unknown keys and malformed colors are skipped, a pre-palette flat file migrates into a "Custom" palette, and a failed load clears the dirty flag so the unload save cannot overwrite a file the mod could not read. The write side:

   ```csharp
   // MeasureTools/Features/Measure/MeasureColors.cs L316-L347
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
   ```

## Complete example

| File | Role |
|---|---|
| [MeasureTools/Features/Measure/MeasureColors.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureColors.cs) | Palettes, defaults, restore, conversion, load, save |
| [MeasureTools/Features/Measure/MeasureWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureWindow.cs#L269-L352) | Palette combo, New, Delete, rename, color rows, restore button |
| [MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs#L19-L40) | `Load()` at `[StarMapAllModsLoaded]` |
| [MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs#L80-L95) | `SaveIfDirty()` then `Reset()` at `[StarMapUnload]` |

## Variations and gotchas

- **Restore matches by name.** Renaming a shipped palette makes *Restore default palettes* add a fresh copy next to it rather than reset the renamed one.
- **Name clashes between user palettes.** `AddPalette` avoids them for generated names, but the rename field does not check for duplicates, so two palettes can share a name and `RestoreDefaultPalettes` then replaces the first match only.
- **The overlay must not cache colors across frames** unless it invalidates on `SetActive`; here the properties are read each draw, so a switch shows immediately.
- **The file sits beside the DLL.** A redeploy or an update that wipes the mod folder loses the palettes, and a read-only folder makes saves fail (logged once). To survive updates, move the file to a documents-folder subfolder; the trade-offs are in [Find where KSA keeps user data and choose where a mod stores its config and data files](../choose-where-a-mod-stores-its-files/overview.md).
- **Byte colors lose precision** relative to float storage; if the overlay needs HDR or finer alpha, store floats, as the override-only table in [Persist a settings object with override-only colors and tuning, saved by a debounced dirty flag](../../settings-formats/persist-mod-settings-as-json/persist-mod-settings-as-json-2.md) does with hex strings.

## See also

- Sibling approaches: [Bundled and user theme TOML files, user wins on a name clash](./persist-user-created-presets-and-content-2.md), [Override-only color table in the main config](./persist-user-created-presets-and-content-3.md), [overview](./overview.md)
- [Named color palettes persisted to a cfg file next to the mod assembly](../../../../mods/ksa-measuretools/features/color-palettes-persistence.md)
- [Build a mod settings window (and developer panel)](../../../ui/windows/build-a-settings-window/overview.md), [Persist mod settings in a hand-rolled ini file](../../settings-formats/persist-mod-settings-ini/overview.md)
