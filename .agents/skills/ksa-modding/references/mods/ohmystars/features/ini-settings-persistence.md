---
type: Mod Feature
title: settings.ini persistence
description: Persist every overlay and window option as key=value lines in a settings.ini beside the DLL, saving on each change and every frame the window open state differs, while ImGui keeps window geometry
mod: ohmystars
category: persistence
techniques: [ini-config, imgui-window]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapBeforeGui]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.ConsoleStyle, Brutal.ImGuiApi.ImGui]
tags: [settings, ini, persistence, imgui, window-state, build]
---

Every overlay toggle, colour, opacity, grid option, pointer style, alignment angle, the active tab
and whether each of the two windows is open lives in a flat `settings.ini` next to the mod's DLL.
The file is read once at load and rewritten whenever a widget changes; window position and size are
left to ImGui's own `imgui.ini`. The source copy in the repository doubles as the shipped defaults,
and the build deliberately overwrites the installed copy.[^oms-ohmystarswindow-cs] [^oms-readme-md]

## How it works

1. **Location.** `ModPaths.Combine("settings.ini")`, i.e. the directory of the loaded
   `OhMyStars.dll` (see [Mod root and packaging](mod-root-and-packaging.md))
   ([src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L1520-L1522)).
2. **Format.** A `[Window]` header and `Key=Value` lines (`ShowWindow=True`,
   `AsterismLineColorB=1`, ...) ([src/settings.ini](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/settings.ini)).[^oms-settings-ini] The reader skips blank
   lines, `;`/`#` comments and any `[section]` line, splits on the first `=`, and stores keys
   case-insensitively; values parse with `bool.TryParse`, `int.TryParse`/`float.TryParse` in the
   invariant culture, or `Enum.TryParse` for the tab, each falling back to a `Defaults` constant
   ([src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L1524-L1589), [src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L28-L50)).
3. **Load.** `LoadSettings` writes a fresh file from the current static field values if none exists
   (their initializers differ from the `Defaults` constants for line colour and opacity, for example
   `iauLineOpacity = 1f` against `Defaults.IAULineOpacity = 0.4f`), otherwise
   assigns every static setting on the renderers and windows
   ([src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L131-L185)).
4. **Save on change.** Each widget's change branch calls `SaveSettings()`, which rebuilds all lines
   and `File.WriteAllLines` the whole file ([src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L187-L229)). Window
   visibility can also change from the window's close button or the ModMenu items, so
   `PersistWindowOpenStates()` compares both open flags with the last saved values every frame and
   saves when they differ ([src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L120-L129)). The Spectrum legend sub-view is
   persisted as its parent tab.
5. **Position and size via `imgui.ini`.** Both windows use a plain `ImGui.Begin(title, ref open,
   ImGuiWindowFlags.None)` with `SetNextWindowSize(..., ImGuiCond.FirstUseEver)`; the comment notes
   that the game's `ConsoleStyle.BeginWindow` would force `NoSavedSettings`, which the decomp
   confirms ([src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L260-L267),
   [current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L703)).[^ksa-consolestyle-cs]
6. **Reset.** "Reset to Defaults" assigns the `Defaults` constants and saves
   ([src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L231-L254)).
7. **Ship-the-defaults build step.** `src/settings.ini` is copied to the build output with
   `CopyToOutputDirectory=Always` and to the installed mod folder with `SkipUnchangedFiles="false"`,
   overwriting the player's copy on every build; the README tells developers to close the game
   before building so it cannot save test settings over the defaults
   ([src/OhMyStars.csproj](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStars.csproj#L43), [src/OhMyStars.csproj](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStars.csproj#L68-L70), [README.md](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/README.md)).[^oms-ohmystars-csproj]
8. **Second file.** The Star Editor keeps its own sectioned `starsedit_settings.ini` with presets
   (see [Star binary switcher](star-binary-switcher.md)).

## Lifecycle and timing

- **Read at `startup:all-mods-loaded`** from `OnFullyLoaded`, after `StellariumRenderer.Init`
  ([src/OhMyStarsEntryPoint.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsEntryPoint.cs#L13-L26)).[^oms-ohmystarsentrypoint-cs]
- **Written during `frame:draw-ui`**, synchronously on the main thread, from the window code and
  `PersistWindowOpenStates` in `[StarMapBeforeGui]` ([src/OhMyStarsEntryPoint.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsEntryPoint.cs#L44-L49)).
  Nothing is written while the HUD is hidden because the hook does not run.

## Key code

| Path | Role |
|---|---|
| [src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L120-L254) | Persist open states, load, save, reset |
| [src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L1520-L1589) | File path, parser, typed readers |
| [src/settings.ini](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/settings.ini) | Shipped defaults |
| [src/OhMyStars.csproj](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStars.csproj#L37-L48) | Deploy target that overwrites the installed file |

## Snippets

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

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `ConsoleStyle.BeginWindow` (not used) | [current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L667-L703) | Forces `NoSavedSettings`, the reason for plain `ImGui.Begin` |
| `ImGui.Begin`, `SetNextWindowSize`, `ImGuiCond.FirstUseEver` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L5584-L5595) | Windows whose geometry ImGui persists |

## Pitfalls and version notes

- **Disk write per frame while dragging.** Slider and colour-picker change branches call
  `SaveSettings()` every frame the value changes, so dragging a slider rewrites the file each frame.
  Debounce (save on release or after a delay) in your own mod.
- **No atomic write.** `File.WriteAllLines` overwrites in place; a crash mid-write can truncate the
  file. The mod's save sidecar code shows the temp-file-and-move pattern it could have used.
- **Not everything is persisted.** The active sky culture, the Information tab's selection mode and
  selected star, the pointer toggle and orientation state reset every session.
- **Builds clobber user settings.** Intended for development, but worth knowing if you build from
  source while playing.

## See also

- [Mod root and packaging](mod-root-and-packaging.md)
- [Save-scoped settings sidecar](save-scoped-settings-sidecar.md)
- [ModMenu entry and game-styled windows](modmenu-entry-and-console-style-windows.md)
- [Star binary switcher](star-binary-switcher.md)
- [Rewrite a flat settings.ini on every widget change](../../../howto/persistence/settings-formats/persist-mod-settings-ini/persist-mod-settings-ini-1.md) - how-to article demonstrating this feature
- [Keep settings beside the mod DLL, resolved from the loaded assembly](../../../howto/persistence/user-data/choose-where-a-mod-stores-its-files/choose-where-a-mod-stores-its-files-1.md) - how-to article demonstrating this feature
- [Let imgui.ini restore window geometry with a plain ImGui.Begin and persist the open flag yourself](../../../howto/persistence/user-data/persist-imgui-window-state/persist-imgui-window-state-1.md) - how-to article demonstrating this feature

[^oms-ohmystarswindow-cs]: OhMyStars src/OhMyStarsWindow.cs
[^oms-readme-md]: OhMyStars README.md
[^oms-settings-ini]: OhMyStars src/settings.ini
[^ksa-consolestyle-cs]: KSA.ConsoleStyle (decompiled, build 2026.10.10.5554)
[^oms-ohmystars-csproj]: OhMyStars src/OhMyStars.csproj
[^oms-ohmystarsentrypoint-cs]: OhMyStars src/OhMyStarsEntryPoint.cs
