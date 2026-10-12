---
type: Mod Feature
title: Named terminal registry and per-target theme dialog
description: Address every terminal, 2D ImGui window or in-world texture, by a unique case-insensitive name through one INamedTerminal registry, and drive a non-modal dialog that applies whole appearance bundles to the selected target
mod: purrtty
category: ui
techniques: [imgui-window, toml-config, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui]
tags: [imgui, dialog, themes, registry, naming, ui-architecture]
---

purrTTY has two very different kinds of terminal: ImGui windows on the HUD and in-world terminals
rendered into a texture on a quad. Both implement one small interface, `INamedTerminal`, and
register themselves by name in a static registry. UI that edits terminals (the Theme dialog, the
Layouts manager, the in-world manager) addresses them by name instead of by type, so one dialog
covers both render worlds. A theme is a complete appearance bundle, not just colors.

## How it works

**Interface and registry** (`TerminalTargetRegistry.cs`):

| Member | Meaning |
|---|---|
| `INamedTerminal.Name`, `Kind` (`Window` / `InWorld`), `HasFocus` | Identity and state |
| `ApplyTheme(ThemeDefinition)` | Apply a bundle (colors always; font, opacities, cursor, border, lock only when the theme carries them) |
| `TrySetGridSize(cols, rows)` | Fixed-size targets (in-world) resize; 2D windows return false because their grid follows the pane |
| `TryRename(newName)` | Rename if the name is free (case-insensitive) |
| `TerminalTargetRegistry.Register/Unregister/Resolve/Focused/IsNameAvailable/SuggestUniqueName` | Static list, main-thread only, no locks; `SuggestUniqueName("Terminal")` yields "Terminal", "Terminal 2", ... |

A `TerminalWindow` registers itself in its constructor with an auto-unique name and unregisters in
`Dispose`; its ImGui label is `"{Name}##purrtty_window_{Id}"`, so a rename changes the visible title
but not ImGui's stable window id (position and size survive).[^terminal-window] In-world instances
register on creation the same way.[^inworld-instance]

**Theme dialog** (`ThemeDialog`, menu "Theme..."): a non-modal ImGui window. A filterable target
picker lists `TerminalTargetRegistry.All` (defaulting to the controller's focus target), with
rename. For a `TerminalWindow` target it shows palette, font family/size, the three opacities and
advanced cursor/border/lock/hot-zone sections, each edit applied live and persisted as the
new-window defaults through `controller.PersistDisplayDefaults`; "Save Current As..." snapshots the
bundle into a user theme TOML (`SnapshotAsTheme`). For an in-world target it offers applying a saved
theme; that target's opacities live in the in-world Config window.[^theme-dialog]

**Single clamp-and-apply path.** `TerminalWindowSettings.ApplyThemeOverrides` is used both when a
theme is applied and when new windows are created from defaults, so clamping rules live in one
place.[^window-settings]

## Lifecycle and timing

- `TerminalMod.OnAfterUi` (`[StarMapAfterGui]`, `frame:draw-ui`) calls `_themeDialog.Render()` every
  frame; it early-outs when closed.[^terminal-mod]
- Registration happens when terminals are constructed: windows on the main thread from menu or
  hotkey actions, in-world instances from the manager or layout apply, also inside `OnAfterUi`.
  The registry assumes main-thread access only.
- Theme application pushes the engine palette with `Surface.SetTheme` on the main thread, the same
  thread that ticks the surfaces.

## Key code

| Path | Role |
|---|---|
| [purrTTY.Display/Ghostty/TerminalTargetRegistry.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalTargetRegistry.cs#L22-L174) | Interface and registry |
| [purrTTY.GameMod/UI/ThemeDialog.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/UI/ThemeDialog.cs#L79-L148) | Dialog frame and target resolution |
| [purrTTY.GameMod/UI/ThemeDialog.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/UI/ThemeDialog.cs#L149-L333) | Target picker, palette, save, apply |
| [purrTTY.Display/Ghostty/TerminalWindow.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.cs#L197-L248) | `ApplyTheme`, `SnapshotAsTheme`, `TryRename`, stable ImGui id |
| [purrTTY.Display/Ghostty/TerminalWindowSettings.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindowSettings.cs) | Appearance state and `ApplyThemeOverrides` |
| [purrTTY.Display.Tests/TerminalTargetRegistryTests.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display.Tests/TerminalTargetRegistryTests.cs) | Naming and uniqueness tests |

## Snippets

```csharp
// purrTTY.Display/Ghostty/TerminalWindow.cs L232-L248
    /// </summary>
    public bool TryRename(string newName)
    {
        string trimmed = newName.Trim();
        if (!TerminalTargetRegistry.IsNameAvailable(trimmed, this))
        {
            return false;
        }

        Name = trimmed;
        _imguiName = BuildImguiName();
        return true;
    }

    // Visible title carries the Name; the "##purrtty_window_{Id}" suffix is the
    // stable ImGui id (unchanged across renames) that keys window position/size.
    private string BuildImguiName() => $"{Name}##purrtty_window_{Id}";
```

```csharp
// purrTTY.Display/Ghostty/TerminalTargetRegistry.cs L135-L173
    public static bool IsNameAvailable(string? name, INamedTerminal? excluding = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        for (int i = 0; i < Terminals.Count; i++)
        {
            if (!ReferenceEquals(Terminals[i], excluding)
                && Terminals[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Returns <paramref name="baseName"/> if free, else the first available
    ///     "<c>baseName N</c>" (N = 2, 3, …). Used to auto-name 2D windows.
    /// </summary>
    public static string SuggestUniqueName(string baseName)
    {
        if (IsNameAvailable(baseName))
        {
            return baseName;
        }

        for (int i = 2; ; i++)
        {
            string candidate = $"{baseName} {i}";
            if (IsNameAvailable(candidate))
            {
                return candidate;
            }
        }
    }
```

## Game APIs touched

Only ImGui (`Begin` with a close button, `SeparatorText`, `BeginTable`, combos, sliders) from
`Brutal.ImGuiApi.ImGui`; no KSA gameplay API.

## Pitfalls and version notes

- Use the `"Label##stable-id"` form for windows that can be renamed, or ImGui forgets their position
  and size on every rename.
- Keep the registry main-thread only (it is a plain `List`); register in constructors that run on the
  main thread, never from session pool threads.
- The repo's docs say all appearance edits apply to either kind of target; at this ref the dialog
  shows the full editor only for 2D windows and a theme-apply action for in-world targets.[^claude-md]

## See also

- [TOML settings and alacritty-format themes](./toml-themes-and-settings.md)
- [Saved terminal layouts](./saved-terminal-layouts.md)
- [Off-screen ImGui context render-to-texture](./offscreen-imgui-context-render-to-texture.md)
- [Filterable combo and form-table widgets](./imgui-filter-combo-and-form-widgets.md)
- [purrTTY overview](../overview.md)
- [Stable window ids: Label###stable-id for renameable windows and a fixed id for plain windows](../../../howto/ui/windows/subclass-the-stock-imguiwindow/subclass-the-stock-imguiwindow-4.md) - how-to article demonstrating this feature

[^terminal-window]: purrTTY.Display/Ghostty/TerminalWindow.cs
[^inworld-instance]: purrTTY.GameMod/InWorld/InWorldTerminalInstance.cs
[^theme-dialog]: purrTTY.GameMod/UI/ThemeDialog.cs
[^window-settings]: purrTTY.Display/Ghostty/TerminalWindowSettings.cs
[^terminal-mod]: purrTTY.GameMod/TerminalMod.cs
[^claude-md]: purrTTY CLAUDE.md
