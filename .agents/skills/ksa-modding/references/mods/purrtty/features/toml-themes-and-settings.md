---
type: Mod Feature
title: TOML settings and alacritty-format themes
description: Persist mod settings with Tomlyn under the KSA Documents folder, load bundled and user color schemes in alacritty TOML format, and write every file atomically with temp-then-rename
mod: purrtty
category: persistence
techniques: [toml-config, starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.Constants]
tags: [toml, tomlyn, settings, themes, colors, persistence, atomic-write]
---

purrTTY keeps its state in plain TOML files a player can read and edit: one settings file with
defaults for new windows (theme, font, opacities, cursor, lock mode, default shell, game-console
prompt, toggle hotkey, last window geometry), a folder of user themes, and the 18 bundled color
schemes that ship in the mod folder in the alacritty format used by the iTerm2-Color-Schemes
project.[^readme] Every write goes through a temp file and a rename.

## How it works

**Locations.** `TerminalMod.SetProductionConfigPath` sets
`ThemeConfiguration.OverrideConfigDirectory = <MyDocuments>/My Games/Kitten Space Agency` before any
config access; without it the code defaults to a temp folder (safe for tests).[^terminal-mod]

| File | Path |
|---|---|
| Settings | `<KSA documents>/.purrTTY/purrtty.toml` |
| User themes | `<KSA documents>/.purrTTY/themes/<sanitized name>.toml` |
| Layouts, startup-command history | `<KSA documents>/.purrTTY/layouts/`, `startup-command-history.toml` (see [layouts](./saved-terminal-layouts.md)) |
| Bundled themes | `<mod folder>/TerminalThemes/*.toml` (beside the assemblies) |

**Settings** (`ThemeConfiguration`): a POCO with two tables, `[settings]` (user-editable) and
`[do-not-touch]` (UI state such as window geometry and grid size), serialized with Tomlyn's
`TomlSerializer` (`WriteIndented`, null values omitted). `Load` sniffs the shape: a structured file
is deserialized directly, an older flat file goes through `LegacyThemeConfiguration` and is migrated.
`Save` normalizes, serializes and writes atomically. Read/parse errors fall back to defaults.[^theme-config]

**One instance.** The controller owns the loaded config; every writer (display defaults, hotkey,
geometry) mutates that instance before saving, because a second loaded copy would later save stale
values over them (gotcha 6).[^gotchas]

**Themes** (`ThemeCatalog`, `ThemeTomlFormat`, `ThemeDefinition`):

- Sources: a code-built "Default" (xterm colors), every `*.toml` in the bundled folder, and every
  `*.toml` in the user folder; user themes win on name clashes.[^theme-catalog]
- Format: Tomlyn DOM (`TomlTable`). Required `[colors.normal]`, `[colors.bright]`,
  `[colors.primary]`; optional `[colors.cursor]` and `[colors.selection]`. User-saved themes add
  `[meta] name` (file names are sanitized, so the display name is stored), `[font]`, `[window]`
  (three opacities), `[cursor]`, `[focus]` and `[lock]`; missing sections mean "keep the window's
  current value".[^theme-format]
- `ThemeColors.ToEngineTheme()` expands the 16 ANSI colors into the full 256-entry xterm palette
  (6x6x6 cube plus 24 greys) and hands it to the engine, which resolves cell colors before the
  frontend sees them.[^theme-definition]

**Atomic writes.** `AtomicFile.WriteAllText` writes `<path>.tmp` then `File.Move(tmp, path,
overwrite: true)`, so a crash mid-write never truncates a config or theme.[^atomic-file]

## Lifecycle and timing

- Loaded in `InitializeTerminal` at `startup:all-mods-loaded` (`ThemeConfiguration.Load()` and
  `new ThemeCatalog()`), after the config path override is set.
- Saved synchronously on the main thread from UI actions in `frame:draw-ui` (theme dialog edits,
  hotkey save, hiding the terminal persists geometry) and at dispose. Files are small, so no
  background I/O is used.

## Key code

| Path | Role |
|---|---|
| [purrTTY.GameMod/TerminalMod.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMod.cs#L236-L257) | Points config at the KSA documents folder |
| [purrTTY.Display/Configuration/ThemeConfiguration.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Configuration/ThemeConfiguration.cs#L550-L654) | Load (with legacy migration), save, path |
| [purrTTY.Display/Configuration/AtomicFile.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Configuration/AtomicFile.cs) | Temp-then-rename |
| [purrTTY.Display/Theming/ThemeCatalog.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Theming/ThemeCatalog.cs#L26-L136) | Sources and precedence |
| [purrTTY.Display/Theming/ThemeTomlFormat.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Theming/ThemeTomlFormat.cs#L9-L60) | Format description and DOM load |
| [purrTTY.Display/Theming/ThemeDefinition.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Theming/ThemeDefinition.cs#L64-L95) | 256-color palette expansion |
| [purrTTY.GameMod/TerminalThemes/Matrix.toml](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalThemes/Matrix.toml) | A bundled scheme |
| [purrTTY.Display.Tests/ThemeTomlFormatTests.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display.Tests/ThemeTomlFormatTests.cs) | Round-trip and all-bundled-themes-parse tests |

## Snippets

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

```csharp
// purrTTY.Display/Configuration/ThemeConfiguration.cs L631-L648
    public static string GetConfigFilePath()
    {
        string baseDirectory;

        if (!string.IsNullOrEmpty(OverrideConfigDirectory))
        {
            // Explicit override (production or test-specific temp)
            baseDirectory = OverrideConfigDirectory;
        }
        else
        {
            // DEFAULT: Temp directory (safe by default)
            baseDirectory = Path.Combine(Path.GetTempPath(), "purrTTY_config_default");
        }

        var configDirectory = Path.Combine(baseDirectory, ".purrTTY");
        return Path.Combine(configDirectory, "purrtty.toml");
    }
```

```toml
# purrTTY.GameMod/TerminalThemes/Matrix.toml L13-L33
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

## Game APIs touched

| API | Where | Purpose |
|---|---|---|
| `Constants.DocumentsFolderPath` | [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281) | The game's own documents root; purrTTY recomputes the same folder instead of calling it[^ksa-constants] |

## Pitfalls and version notes

- purrTTY builds the documents path from `Environment.SpecialFolder.MyDocuments`, the same folder
  KSA uses by default. StarMap can redirect `Constants.DocumentsFolderPath` with `-InstancePath` or
  `STARMAP_INSTANCE_PATH`; purrTTY's files do not follow that redirect. Reading
  `KSA.Constants.DocumentsFolderPath` would.[^starmap-documents-patch]
- Tomlyn's reflection POCO serializer and Brutal's logging handler need
  `Microsoft.Extensions.ObjectPool`, present in game but not in test reference sets; purrTTY's
  layout files therefore use the DOM and hand-written output (gotcha 30). The settings file still uses
  the POCO serializer.
- Keep one config instance per process; saving from a second copy silently reverts fields.
- `File.Move(..., overwrite: true)` is atomic on the same volume; keep the temp file beside the target.

## See also

- [Named terminal registry and theme dialog](./named-terminal-registry-and-theme-dialog.md)
- [Saved terminal layouts](./saved-terminal-layouts.md)
- [Rebindable toggle hotkey](./rebindable-toggle-hotkey.md)
- [Nerd fonts in the ImGui atlas](./nerd-font-loading-into-imgui-atlas.md)
- [purrTTY overview](../overview.md)
- [Persist a structured TOML config with a do-not-touch table, legacy migration and one shared instance](../../../howto/persistence/settings-formats/persist-mod-settings-toml/persist-mod-settings-toml-2.md) - how-to article demonstrating this feature
- [Flush on close and unload, skip junk keys on load and migrate the previous format](../../../howto/persistence/settings-formats/write-and-load-config-files-safely/write-and-load-config-files-safely-4.md) - how-to article demonstrating this feature
- [Store config in a mod-named subfolder under Constants.DocumentsFolderPath](../../../howto/persistence/user-data/choose-where-a-mod-stores-its-files/choose-where-a-mod-stores-its-files-2.md) - how-to article demonstrating this feature
- [Load bundled and user color themes from TOML files and let the user's theme win a name clash](../../../howto/persistence/user-data/persist-user-created-presets-and-content/persist-user-created-presets-and-content-2.md) - how-to article demonstrating this feature

[^readme]: purrTTY README.md
[^terminal-mod]: purrTTY.GameMod/TerminalMod.cs
[^theme-config]: purrTTY.Display/Configuration/ThemeConfiguration.cs
[^gotchas]: purrTTY docs/gotchas.md
[^theme-catalog]: purrTTY.Display/Theming/ThemeCatalog.cs
[^theme-format]: purrTTY.Display/Theming/ThemeTomlFormat.cs
[^theme-definition]: purrTTY.Display/Theming/ThemeDefinition.cs
[^atomic-file]: purrTTY.Display/Configuration/AtomicFile.cs
[^ksa-constants]: KSA.Constants (decompiled, build 2026.10.10.5554)
[^starmap-documents-patch]: StarMap.Core/Patches/DocumentsPathPatches.cs
