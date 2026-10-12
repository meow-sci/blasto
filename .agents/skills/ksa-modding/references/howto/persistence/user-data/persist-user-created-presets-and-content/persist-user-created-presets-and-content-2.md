---
type: How-To
title: Load bundled and user color themes from TOML files and let the user's theme win a name clash
description: Merge a code-built default, a read-only bundled folder and a user folder of alacritty-format TOML themes into one catalog, save the player's current look as a named theme with a display name stored in the file, and expand 16 ANSI colors into the 256-color palette
category: persistence
group: user-data
topic: persist-user-created-presets-and-content
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/purrtty/features/toml-themes-and-settings]
techniques: [toml-config]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.Constants]
tags: [themes, toml, tomlyn, alacritty, presets, user-content, atomic-write]
---
Outcome: a catalog of color themes assembled from three places (one built into the code, a folder shipped with the mod, a folder the player writes to), where the player can save the terminal's current look under a name, apply any theme later, delete their own, and see a warning before overwriting one. purrTTY ships 18 bundled schemes in the alacritty color-scheme format and adds its own tables to user-saved files. Demonstrated by [TOML settings and alacritty-format themes](../../../../mods/purrtty/features/toml-themes-and-settings.md). The settings file itself and the Tomlyn serializer are covered by [Persist a structured TOML config with a do-not-touch table, legacy migration and one shared instance](../../settings-formats/persist-mod-settings-toml/persist-mod-settings-toml-2.md); this article is about the theme catalog.

## Background: how the game does it

KSA has no theme or preset store a mod can plug into; the only game type involved is the documents folder, `Constants.DocumentsFolderPath` ([current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281)). purrTTY does not call it: `TerminalMod.SetProductionConfigPath` rebuilds the same folder from `Environment.SpecialFolder.MyDocuments` + `My Games/Kitten Space Agency` and hands it to the config layer, which falls back to a temp folder when nothing is set (safe for unit tests) ([purrTTY.GameMod/TerminalMod.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMod.cs#L241-L257)). Mod-shipped data lives in the mod folder: the project copies `TerminalThemes/**` next to the assemblies ([purrTTY.GameMod/purrTTY.GameMod.csproj](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/purrTTY.GameMod.csproj#L72-L77)).

## Lifecycle and timing

The catalog is built once in `InitializeTerminal` at `startup:all-mods-loaded`, after the config directory override is set; `Refresh()` rescans both folders on demand (the *Refresh* button) and after every save or delete. Saves and deletes run on the main thread from the theme dialog's button handlers in the UI pass (`frame:draw-ui`). The files are a few hundred bytes, so there is no background I/O. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Pick a documented, hand-editable format and keep it.** Adopting the alacritty color-scheme layout means any of the large public scheme collections can be dropped into the bundled or user folder. A bundled scheme is just the color tables:

   ```toml
   # purrTTY.GameMod/TerminalThemes/Matrix.toml L3-L33
   [colors.bright]
   black = '#688060'
   blue = '#4f7e7e'
   cyan = '#c1ff8a'
   green = '#90d762'
   magenta = '#11ff25'
   red = '#2fc079'
   white = '#678c61'
   yellow = '#faff00'

   [colors.cursor]
   cursor = '#384545'
   text = '#00ff00'

   [colors.normal]
   black = '#0f191c'
   blue = '#3f5242'
   cyan = '#50b45a'
   green = '#82d967'
   magenta = '#409931'
   red = '#23755a'
   white = '#507350'
   yellow = '#ffd700'

   [colors.primary]
   background = '#0f191c'
   foreground = '#426644'

   [colors.selection]
   background = '#18282e'
   text = '#00ff87'
   ```

2. **Build the catalog from three sources.** The built-in list is the code-built `Default` plus the bundled folder; the user list is the user folder. `Refresh` clears and reloads both, and a theme that fails to parse is skipped rather than failing the scan:

   ```csharp
   // purrTTY.Display/Theming/ThemeCatalog.cs L26-L42, L97-L136
   public ThemeCatalog()
   {
       Refresh();
   }

   /// <summary>Reloads both theme directories.</summary>
   public void Refresh()
   {
       _builtIn.Clear();
       _builtIn.Add(Default);
       _builtIn.AddRange(LoadDirectory(GetBuiltInThemesDirectory(), ThemeSource.BuiltIn));
       _builtIn.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

       _user.Clear();
       _user.AddRange(LoadDirectory(GetUserThemesDirectory(), ThemeSource.UserFile));
       _user.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
   }
   ..
   public static string GetBuiltInThemesDirectory()
   {
       var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                         ?? Environment.CurrentDirectory;
       return Path.Combine(assemblyDir, "TerminalThemes");
   }

   public static string GetUserThemesDirectory()
   {
       var configDir = Path.GetDirectoryName(ThemeConfiguration.GetConfigFilePath())
                       ?? Environment.CurrentDirectory;
       return Path.Combine(configDir, "themes");
   }

   private static IEnumerable<ThemeDefinition> LoadDirectory(string directory, ThemeSource source)
   {
       if (!Directory.Exists(directory))
       {
           yield break;
       }

       string[] files;
       try
       {
           files = Directory.GetFiles(directory, "*.toml", SearchOption.TopDirectoryOnly);
       }
       catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
       {
           ModLog.Log.Debug($"ThemeCatalog: cannot read themes directory '{directory}': {ex.Message}");
           yield break;
       }

       foreach (var file in files)
       {
           if (ThemeTomlFormat.Load(file, source) is { } theme)
           {
               yield return theme;
           }
       }
   }
   ```

3. **Resolve names user-first.** `Find` looks in the user list before the built-ins, so a user theme named like a bundled one shadows it without touching the bundled file. The dialog warns only about overwriting another *user* theme:

   ```csharp
   // purrTTY.Display/Theming/ThemeCatalog.cs L44-L58
   /// <summary>Finds a theme by name; user themes take precedence over built-ins.</summary>
   public ThemeDefinition? Find(string? name)
   {
       if (string.IsNullOrWhiteSpace(name))
       {
           return null;
       }

       return _user.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
              ?? _builtIn.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
   }

   /// <summary>True when a user theme with this name already exists (it would be overwritten).</summary>
   public bool UserThemeExists(string name)
       => _user.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
   ```

   ```csharp
   // purrTTY.GameMod/UI/ThemeDialog.cs L261-L292
   if (ImGui.Button(" Save current settings as theme ##theme_save") && canSave)
   {
       try
       {
           var saved = _controller.Catalog.SaveUserTheme(window.SnapshotAsTheme(saveName));
           window.Settings.ThemeName = saved.Name;
           _controller.PersistDisplayDefaults(window);
           _saveNameInput.Clear();
           _saveError = null;
       }
       catch (Exception ex)
       {
           _saveError = $"Save failed: {ex.Message}";
       }
   }

   if (!canSave)
   {
       ImGui.EndDisabled();
   }

   ImGui.SameLine(0, 8);
   if (ImGui.Button(" Refresh ##theme_refresh"))
   {
       _controller.Catalog.Refresh();
   }

   if (canSave && _controller.Catalog.UserThemeExists(saveName))
   {
       ImGui.Spacing();
       ImGui.TextColored(ImGuiWidgets.WarningColor, $"A saved theme named '{saveName}' will be overwritten.");
   }
   ```

4. **Store the display name inside the file.** File names are sanitized (`Path.GetInvalidFileNameChars()` become `_`), so the name cannot round-trip through the file name; user-saved files carry `[meta] name`, bundled files have none and fall back to the file name. Without this a saved theme named `My Theme!` would not resolve on the next launch:

   ```csharp
   // purrTTY.Display/Theming/ThemeCatalog.cs L64-L72, L138-L143
   public ThemeDefinition SaveUserTheme(ThemeDefinition theme)
   {
       var fileName = SanitizeFileName(theme.Name) + ".toml";
       var filePath = Path.Combine(GetUserThemesDirectory(), fileName);
       ThemeTomlFormat.Save(filePath, theme);
       ModLog.Log.Debug($"ThemeCatalog: saved user theme '{theme.Name}' to {filePath}");
       Refresh();
       return Find(theme.Name) ?? theme;
   }
   ..
   private static string SanitizeFileName(string name)
   {
       var invalid = Path.GetInvalidFileNameChars();
       var chars = name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
       return new string(chars);
   }
   ```

5. **Load with the Tomlyn DOM and require only the color tables.** Everything else is optional, and a missing value means "keep the window's current setting" when the theme is applied. Bad files are logged and skipped:

   ```csharp
   // purrTTY.Display/Theming/ThemeTomlFormat.cs L49-L80, L120-L125
   public static ThemeDefinition? Load(string filePath, ThemeSource source)
   {
       try
       {
           var text = File.ReadAllText(filePath);
           if (TomlSerializer.Deserialize<TomlTable>(text, TomlOptions) is not { } root
               || GetTable(root, "colors") is not { } colors
               || GetTable(colors, "normal") is not { } normal
               || GetTable(colors, "bright") is not { } bright
               || GetTable(colors, "primary") is not { } primary)
           {
               ModLog.Log.Debug($"ThemeTomlFormat: '{filePath}' is missing required [colors.*] sections; skipped");
               return null;
           }

           var themeColors = new ThemeColors();
           for (int i = 0; i < 8; i++)
           {
               themeColors.Ansi[i] = ParseHexColor(GetString(normal, AnsiNames[i]));
               themeColors.Ansi[i + 8] = ParseHexColor(GetString(bright, AnsiNames[i]));
           }

           themeColors.Foreground = ParseHexColor(GetString(primary, "foreground"));
           themeColors.Background = ParseHexColor(GetString(primary, "background"));
           themeColors.Cursor = GetTable(colors, "cursor") is { } cursor && GetString(cursor, "cursor") is { } c
               ? ParseHexColor(c)
               : themeColors.Foreground;
           themeColors.SelectionBackground =
               GetTable(colors, "selection") is { } selection && GetString(selection, "background") is { } s
                   ? ParseHexColor(s)
                   : new RgbaColor(0x60, 0x60, 0x60);

   ..
       catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TomlException
                                      or ArgumentException or FormatException)
       {
           ModLog.Log.Debug($"ThemeTomlFormat: failed to load theme '{filePath}': {ex.Message}");
           return null;
       }
   ```

6. **Write only the sections that have values, atomically.** Save builds a `TomlTable` tree, adds the optional `[font]`, `[window]`, `[cursor]`, `[focus]` and `[lock]` tables only when the theme defines them, and writes through a temp file and rename so an interrupted save never truncates a theme:

   ```csharp
   // purrTTY.Display/Theming/ThemeTomlFormat.cs L128-L153, L278-L280
   public static void Save(string filePath, ThemeDefinition theme)
   {
       var normal = new TomlTable();
       var bright = new TomlTable();
       for (int i = 0; i < 8; i++)
       {
           normal[AnsiNames[i]] = ToHex(theme.Colors.Ansi[i]);
           bright[AnsiNames[i]] = ToHex(theme.Colors.Ansi[i + 8]);
       }

       var root = new TomlTable
       {
           ["meta"] = new TomlTable { ["name"] = theme.Name },
           ["colors"] = new TomlTable
           {
               ["normal"] = normal,
               ["bright"] = bright,
               ["primary"] = new TomlTable
               {
                   ["background"] = ToHex(theme.Colors.Background),
                   ["foreground"] = ToHex(theme.Colors.Foreground),
               },
               ["cursor"] = new TomlTable { ["cursor"] = ToHex(theme.Colors.Cursor) },
               ["selection"] = new TomlTable { ["background"] = ToHex(theme.Colors.SelectionBackground) },
           },
       };
   ..
       Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
       purrTTY.Display.Configuration.AtomicFile.WriteAllText(filePath, TomlSerializer.Serialize(root, TomlOptions));
   }
   ```

   ```csharp
   // purrTTY.Display/Configuration/AtomicFile.cs L10-L15
   public static void WriteAllText(string path, string contents)
   {
       var tmp = path + ".tmp";
       File.WriteAllText(tmp, contents);
       File.Move(tmp, path, overwrite: true);
   }
   ```

7. **Parse colors strictly and expand them for the engine.** Theme files use `#rrggbb`; a bad value throws `FormatException`, which `Load` catches. The 16 ANSI colors become the full 256-entry palette (a 6x6x6 cube plus a 24-step grey ramp):

   ```csharp
   // purrTTY.Display/Theming/ThemeTomlFormat.cs L282-L295
   public static RgbaColor ParseHexColor(string? hex)
   {
       if (hex is null || hex.Length != 7 || hex[0] != '#')
       {
           throw new FormatException($"Invalid hex color '{hex}'; expected #rrggbb");
       }

       byte r = byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber);
       byte g = byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber);
       byte b = byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber);
       return new RgbaColor(r, g, b);
   }

   public static string ToHex(RgbaColor c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";
   ```

   ```csharp
   // purrTTY.Display/Theming/ThemeDefinition.cs L64-L94
   public TerminalTheme ToEngineTheme()
   {
       var palette = new RgbaColor[256];
       Ansi.CopyTo(palette, 0);

       ReadOnlySpan<byte> steps = stackalloc byte[] { 0, 95, 135, 175, 215, 255 };
       int idx = 16;
       for (int r = 0; r < 6; r++)
       {
           for (int g = 0; g < 6; g++)
           {
               for (int b = 0; b < 6; b++)
               {
                   palette[idx++] = new RgbaColor(steps[r], steps[g], steps[b]);
               }
           }
       }

       for (int i = 0; i < 24; i++)
       {
           byte v = (byte)(8 + i * 10);
           palette[232 + i] = new RgbaColor(v, v, v);
       }

       return new TerminalTheme(palette)
       {
           DefaultForeground = Foreground,
           DefaultBackground = Background,
           Cursor = Cursor,
       };
   }
   ```

8. **Persist only the theme name in the settings file.** The settings table stores `SelectedThemeName`; the window's colors are not saved separately, so a custom color set survives a restart only if the player saved it as a named theme. Applying a theme copies its colors into the window and sets `Settings.ThemeName` ([purrTTY.Display/Ghostty/TerminalWindow.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.cs#L202-L210)).

## Complete example

| File | Role |
|---|---|
| [purrTTY.Display/Theming/ThemeCatalog.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Theming/ThemeCatalog.cs) | Sources, precedence, save, delete, file-name sanitising |
| [purrTTY.Display/Theming/ThemeTomlFormat.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Theming/ThemeTomlFormat.cs) | Load and save of one theme, hex colors |
| [purrTTY.Display/Theming/ThemeDefinition.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Theming/ThemeDefinition.cs) | Theme model and 256-color expansion |
| [purrTTY.Display/Configuration/AtomicFile.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Configuration/AtomicFile.cs) | Temp-then-rename |
| [purrTTY.GameMod/UI/ThemeDialog.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/UI/ThemeDialog.cs#L255-L298) | Save, delete and overwrite-warning UI |
| [purrTTY.GameMod/TerminalThemes/Matrix.toml](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalThemes/Matrix.toml) | A bundled scheme |

## Variations and gotchas

- **Instance paths.** The user folder is derived from `MyDocuments`, not from `Constants.DocumentsFolderPath`, so it does not follow a StarMap `-InstancePath` redirect; the constant would. The same choice makes unload-time work safe, because StarMap removes the redirect before `[StarMapUnload]` (see [Find where KSA keeps user data and choose where a mod stores its config and data files](../choose-where-a-mod-stores-its-files/overview.md)).
- **Case-insensitive names, case-sensitive file systems.** `Find` and `UserThemeExists` compare names ignoring case, but two names that differ only in case produce two files on Linux and one on Windows and macOS.
- **Overwrite is silent for the bundled name.** Saving a user theme named `Matrix` does not overwrite the bundled file; it creates a user file that wins from then on, and deleting it brings the bundled one back.
- **Hand edits.** A malformed hex color rejects the whole file (logged at debug level and skipped), so a typo makes the theme vanish from the list rather than half-apply.
- **Layouts refer to themes by name.** A saved layout stores the theme name and resolves it with `ThemeCatalog.Find` when applied ([purrTTY.GameMod/Layouts/LayoutManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Layouts/LayoutManager.cs#L87)); `Find` returns null for a name that no longer exists, so renaming or deleting a theme breaks that reference.
- **Settings versus presets.** A preset file captures a bundle; the active selection is a name in the settings file. Keeping the two apart means a corrupt preset cannot break settings loading.

## See also

- Sibling approaches: [Named palettes in a sectioned ini](./persist-user-created-presets-and-content-1.md), [Override-only color table in the main config](./persist-user-created-presets-and-content-3.md), [overview](./overview.md)
- [TOML settings and alacritty-format themes](../../../../mods/purrtty/features/toml-themes-and-settings.md)
- [Persist mod settings as TOML](../../settings-formats/persist-mod-settings-toml/overview.md), [Write config files atomically and load hand-edited ones without losing them](../../settings-formats/write-and-load-config-files-safely/overview.md)
- [Build a mod settings window (and developer panel)](../../../ui/windows/build-a-settings-window/overview.md)
