---
type: Mod Feature
title: Named color palettes persisted to a cfg file next to the mod assembly
description: Overlay colors live in named palettes (four shipped plus user palettes) edited with ColorEdit4, switched from the window menu bar, and saved with a dirty flag to colors.cfg next to the DLL on close and unload
mod: ksa-measuretools
category: persistence
techniques: [cfg-file, imgui-window, starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapAfterGui, starmap StarMapUnload]
lifecycle: [startup:all-mods-loaded, frame:draw-ui, event:starmap-unload]
game_types: [KSA.ImGuiWindow, KSA.ImGuiHelper]
tags: [persistence, settings, colors, palettes, config-file, imgui, color-picker]
---

Every overlay color (lines, highlight, pending and preview, snap markers, construction plane, label
text, label background) has a color picker, organized into named palettes: Red, Green, Blue and
Contrast ship with the mod, users can add their own, and all of them are editable and persist across
sessions. The window's **Palette** menu switches quickly, for example green lines on a red rocket.[^mt-readme]

## How it works

### Data model

`ColorPalette` is a plain class with a name and seven `byte4` colors. `MeasureColors` holds a
`List<ColorPalette>`, the active index and a dirty flag. The overlay reads colors through static
properties (`MeasureColors.Measure`, `.Pending`, ...) and never sees palettes. The two preview shades
are derived from the pending color at alpha 160 and 80, so one picker governs the whole
pending/preview family.[^mt-colors][^mt-overlay]

The static constructor fills the four shipped palettes. `RestoreDefaultPalettes` re-adds them and
resets their colors by name while leaving custom palettes alone, which doubles as the "Restore default
palettes" button.

### File format and location

`colors.cfg` lives next to the mod assembly (`Path.GetDirectoryName(typeof(MeasureColors).Assembly.Location)`),
so it ends up in `Documents\My Games\Kitten Space Agency\mods\MeasureTools\`. `Assembly.Location`
is a real path here because StarMap loads each mod's entry assembly from disk: its
`ModAssemblyLoadContext.Load` resolves the DLL next to `mod.toml` and calls `LoadFromAssemblyPath`
with the full path. An assembly loaded from a byte stream would report an empty `Location`, and the
mod's `?? "."` fallback would then write into the process working directory, which StarMap sets to
the game folder.[^starmap-modalc] The format is a tiny INI:

- `active=<index>`
- one `[Name]` section per palette with `Measure=r,g,b,a` style lines, keys matching the field names
  (`nameof(ColorPalette.Measure)` and so on), values as invariant-culture bytes.

Loading tolerates junk: unknown keys and malformed values are skipped, and color lines that appear
before any section (the pre-palette flat format) migrate into a "Custom" palette placed first, with
the shipped palettes added after it and the file marked dirty so the next save writes the new
format. Renaming strips `[`, `]` and `=` so a name cannot corrupt the file, and the `ImInputString`
capacity is sized from the UTF-8 byte count because it throws when content exceeds capacity.[^mt-window]

### When it reads and writes

| Event | Call |
|---|---|
| `[StarMapAllModsLoaded]` | `MeasureColors.Load()` |
| Window closed (menu or title-bar X) | `MeasureColors.SaveIfDirty()` |
| `[StarMapUnload]` | `SaveIfDirty()`, then `Reset()` back to the shipped set with the dirty flag cleared |

Load and save failures are logged once (`LogHelper.WarnOnce`) and never throw. A failed load clears
the dirty flag the static constructor set, so the unload save does not overwrite a file the mod could
not read with defaults. A clean load also clears it, so the first close of a session does not rewrite
an unchanged file.[^mt-mod]

### UI

The **Colors** collapsing header shows a palette combo with New (clone the active palette as
"Palette N") and Delete (disabled for the last remaining palette), a name field, seven
`ImGui.ColorEdit4` rows with `NoInputs | AlphaBar | AlphaPreviewHalf`, and the restore button with a
tooltip from `ImGuiHelper.DrawTooltip`. The window's menu bar adds a Palette menu with a checkmark on
the active palette, next to the stock View menu kept by calling `base.DrawMenuBar()`.[^mt-window]

## Lifecycle and timing

Loading happens once at `startup:all-mods-loaded`, before any UI exists, which is why the palette
code touches no ImGui state there. Editing and switching run inside the window draw in
`[StarMapAfterGui]` (`frame:draw-ui`). Saving happens on the main thread when the window closes and in
`[StarMapUnload]`, which StarMap calls from `ModLoader.Dispose` once, after the game's `Main` has
returned (a clean exit only; a crash or killed process loses unsaved palette edits unless the window
was closed first).[^starmap-modloader]
The stock `ImGuiWindow` begins windows with `NoSavedSettings`, so this file is the only state the mod
persists; measurements themselves are session-only.[^ksa-imguiwindow]

## Key code

| Path | Role |
|---|---|
| [MeasureTools/Features/Measure/MeasureColors.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureColors.cs) | Palettes, defaults, load, save, migration |
| [MeasureTools/Features/Measure/MeasureWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureWindow.cs#L269-L352) | Colors section, name field, color rows |
| [MeasureTools/Features/Measure/MeasureWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureWindow.cs#L93-L109) | Palette menu in the window menu bar |
| [MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs) | Load at startup, save and reset at unload |

## Snippets

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
        // ...
    }
    catch (Exception ex)
    {
        LogHelper.WarnOnce("colors-save", $"[MeasureTools] Could not save {path}: {ex.Message}");
    }
}
```

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

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `ImGuiWindow.DrawMenuBar` (virtual, stock View menu), `OnDrawUi` (`NoSavedSettings`) | [current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L449-L458) | Extending the menu bar; why the mod needs its own file |

## Pitfalls and version notes

- Writing next to the assembly works for a user-installed mod folder; it fails silently (logged once)
  if the folder is read-only, and a reinstall that wipes the folder loses the palettes.
- Keep the dirty flag honest: set it on every change, clear it on a clean load and on a failed load,
  otherwise either nothing is saved or a good file is overwritten with defaults.
- Palettes are matched by name when restoring defaults; renaming a shipped palette makes restore add
  a fresh copy next to it.
- Byte colors are converted to and from `float4` for `ColorEdit4` with rounding (`* 255 + 0.5`), so
  a value round-trips exactly.

## See also

- [Overview](../overview.md)
- [Overlay rendering](./overlay-drawlist-rendering.md)
- [Measure menu and tool window](./measure-menu-and-tool-window.md)
- [Write a sectioned palette file when a dirty flag is set, on window close and at unload](../../../howto/persistence/settings-formats/persist-mod-settings-ini/persist-mod-settings-ini-4.md) - how-to article demonstrating this feature
- [Flush on close and unload, skip junk keys on load and migrate the previous format](../../../howto/persistence/settings-formats/write-and-load-config-files-safely/write-and-load-config-files-safely-4.md) - how-to article demonstrating this feature
- [Keep settings beside the mod DLL, resolved from the loaded assembly](../../../howto/persistence/user-data/choose-where-a-mod-stores-its-files/choose-where-a-mod-stores-its-files-1.md) - how-to article demonstrating this feature
- [Keep named, editable color palettes in a sectioned file with a ColorEdit4 round trip](../../../howto/persistence/user-data/persist-user-created-presets-and-content/persist-user-created-presets-and-content-1.md) - how-to article demonstrating this feature

[^mt-colors]: MeasureTools/Features/Measure/MeasureColors.cs
[^mt-window]: MeasureTools/Features/Measure/MeasureWindow.cs
[^mt-overlay]: MeasureTools/Features/Measure/MeasureOverlay.cs
[^mt-mod]: MeasureTools/Mod.cs
[^mt-readme]: KSA-MeasureTools README.md
[^ksa-imguiwindow]: KSA.ImGuiWindow (decompiled, build 2026.10.10.5554)
[^starmap-modloader]: StarMap.Core/ModRepository/ModLoader.cs
[^starmap-modalc]: StarMap.Core/ModRepository/ModAssemblyLoadContext.cs
