---
type: Mod Feature
title: Saved terminal layouts with startup commands
description: Save and restore named sets of 2D and in-world terminals as hand-emitted TOML, applied only on user request with name-collision skips, per-terminal startup commands typed into the shell, and an MRU command history
mod: purrtty
category: persistence
techniques: [toml-config, imgui-window, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: []
tags: [toml, layouts, persistence, startup-command, dialog, workspace]
---

A player can build a workspace (a log tail in one window, htop in another, a telemetry terminal
anchored to a part of the rocket, a gatOS flight-computer TUI on a HUD billboard) and save it as a
named layout from the "Layouts..." dialog. Loading the layout later recreates every terminal with
its name, theme, placement and shell, and types each terminal's startup command into its shell.
Nothing is applied automatically at game start.

## How it works

**Data** (`purrTTY.Display/Layouts`): `TerminalLayout` = `LayoutHeader` (name, description) plus a
list of `TerminalEntry`. Entries carry name, kind (`window` or `inworld`), theme name, and nullable
kind-specific placement: 2D windows store position and size in pixels only (cols/rows and font are
derived live), in-world entries store cols/rows plus anchor mode, vehicle/part/sub-part ids,
offset, rotation and size in meters, or billboard parameters. `ShellSpec` stores shell type, custom
path or custom shell id, arguments, working directory and `StartupCommand`.[^layout-definition]

**Files.** One TOML file per layout in `<KSA documents>/.purrTTY/layouts/<sanitized>.toml`
(`[layout]` table plus `[[terminal]]` array with a `[terminal.shell]` sub-table).
`LayoutTomlFormat.Load` parses into the Tomlyn DOM; `Save` emits the text by hand with a
`StringBuilder` and writes it atomically.[^layout-format]

**Apply** (`LayoutManager.Apply`): for each entry, skip blank names, skip and log names that collide
with a live terminal (`TerminalTargetRegistry.IsNameAvailable`), resolve the theme by name, then
create an in-world instance (`InWorldTerminalManager.Create(record)`) or a window
(`controller.CreateConfiguredWindow(record, theme)`). Created terminals are tracked per layout so
"Tear down" removes exactly that set through the normal close paths; the result reports created and
skipped counts.[^layout-manager]

**Capture** (`CaptureCurrentAs`): snapshot every live window (`controller.CaptureWindows`) and
in-world instance into entries, by theme name (custom colors persist only if saved as a named theme).

**Startup command.** `TerminalSession.InitializeAsync` writes `StartupCommand` plus a newline to the
shell right after `StartAsync`. No readiness wait is needed because the PTY line discipline buffers
input until the shell reads it; it works for custom shells too (for example the gatOS SSH shell).[^session]
`StartupCommandHistory` keeps the last 20 distinct commands (most recent first) in
`startup-command-history.toml`, loaded once and cached; the in-world create form shows them in a
filterable combo.[^history]

## Lifecycle and timing

- `TerminalMod.OnAfterUi` (`[StarMapAfterGui]`, `frame:draw-ui`) pumps `LayoutManagerUI.Draw()`
  every frame. Apply runs from a button inside that draw, which matters because creating an
  in-world instance allocates GPU resources and measures the font cell, and needs an active ImGui
  frame (gotcha 29).[^terminal-mod][^gotchas]
- Shell start for each created terminal is asynchronous (pool thread), so Apply returns quickly; the
  startup command is sent when each shell has started.
- In-world anchors are best-effort across restarts: only `Vehicle.Id` is reliably stable, part and
  sub-part ids resolve to the first match (gotcha 29).

## Key code

| Path | Role |
|---|---|
| [purrTTY.Display/Layouts/LayoutDefinition.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Layouts/LayoutDefinition.cs) | Data model |
| [purrTTY.Display/Layouts/LayoutTomlFormat.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Layouts/LayoutTomlFormat.cs#L11-L72) | Format description and DOM load |
| [purrTTY.Display/Layouts/LayoutTomlFormat.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Layouts/LayoutTomlFormat.cs#L133-L200) | Hand-emitted save |
| [purrTTY.Display/Layouts/LayoutCatalog.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Layouts/LayoutCatalog.cs) | List, load, save, delete, rename |
| [purrTTY.GameMod/Layouts/LayoutManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Layouts/LayoutManager.cs#L57-L131) | Apply with collision skip and per-set tracking |
| [purrTTY.GameMod/Layouts/UI/LayoutManagerUI.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Layouts/UI/LayoutManagerUI.cs) | List, load, tear down, delete, save-current, per-layout editor |
| [purrTTY.Terminal/Sessions/TerminalSession.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Sessions/TerminalSession.cs#L88-L121) | Startup command typed after start |
| [purrTTY.Display/Configuration/StartupCommandHistory.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Configuration/StartupCommandHistory.cs) | MRU history |
| [purrTTY.Display.Tests/LayoutTomlTests.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display.Tests/LayoutTomlTests.cs) | Round-trip tests |

## Snippets

```csharp
// purrTTY.GameMod/Layouts/LayoutManager.cs L71-L110
        {
            string name = entry.Name?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                skipped.Add(("(unnamed)", "blank terminal name"));
                continue;
            }

            // Name-collision policy: log + skip; never rename or replace a live terminal.
            if (!TerminalTargetRegistry.IsNameAvailable(name))
            {
                ModLog.Log.Error($"Layout '{layoutName}': terminal name '{name}' collides with a live terminal — skipped.");
                skipped.Add((name, "name already in use by a live terminal"));
                continue;
            }

            var theme = _themes.Find(entry.Theme);

            if (entry.Kind == TerminalKind.InWorld)
            {
                if (_inWorld is null)
                {
                    skipped.Add((name, "in-world subsystem unavailable"));
                    continue;
                }

                var instance = _inWorld.Create(ToInWorldRecord(entry, name));
                if (instance is null)
                {
                    skipped.Add((name, "in-world create failed (see log)"));
                    continue;
                }

                created.Add(instance);
            }
            else
            {
                created.Add(_controller.CreateConfiguredWindow(ToWindowRecord(entry, name), theme));
                anyWindow = true;
            }
```

```csharp
// purrTTY.Terminal/Sessions/TerminalSession.cs L96-L111
        try
        {
            LaunchOptions = launchOptions;
            await ProcessManager.StartAsync(launchOptions, cancellationToken);

            // Auto-run command: type it as stdin once the shell is up. The PTY line
            // discipline buffers the bytes until the shell reads them, so no readiness
            // wait/sleep is needed; for a remote (gatOS) shell the interactive login PTY
            // is already established with its env. Newline-terminated so it executes.
            if (!string.IsNullOrEmpty(launchOptions.StartupCommand))
            {
                ProcessManager.Write(
                    launchOptions.StartupCommand.EndsWith('\n')
                        ? launchOptions.StartupCommand
                        : launchOptions.StartupCommand + "\n");
            }
```

## Game APIs touched

None directly in the layout layer. In-world entries carry ids that the in-world subsystem resolves
against `Universe.CurrentSystem` vehicles and parts (see
[anchoring](./anchor-quad-to-part-or-camera.md)).

## Pitfalls and version notes

- Never auto-apply a layout on load: a stale layout could spawn shells the player did not ask for.
- The layout layer avoids both Tomlyn's POCO serializer and `ModLog` because both pull
  `Microsoft.Extensions.ObjectPool`, missing from the test reference set (gotcha 30).
  `StartupCommandHistory` does use Tomlyn's serializer with a small POCO; its tests run in the same
  test project (whether they hit the missing assembly was not checked here).
- Name collisions are skipped, not renamed, so a partially loaded layout is possible; the dialog
  reports how many were skipped.
- Editing a saved layout changes only the file; live terminals are untouched until it is loaded again
  (docs/how-to.md).[^how-to]

## See also

- [Named terminal registry and theme dialog](./named-terminal-registry-and-theme-dialog.md)
- [TOML settings and alacritty-format themes](./toml-themes-and-settings.md)
- [Off-screen ImGui context render-to-texture](./offscreen-imgui-context-render-to-texture.md)
- [Anchor the quad to a part or the camera](./anchor-quad-to-part-or-camera.md)
- [Filterable combo and form-table widgets](./imgui-filter-combo-and-form-widgets.md)
- [purrTTY overview](../overview.md)
- [Anchor a saved window layout on Vehicle.Id and accept best-effort part ids](../../../howto/persistence/saves/identify-vessels-and-parts-across-saves/identify-vessels-and-parts-across-saves-3.md) - how-to article demonstrating this feature
- [Save every live window as a named layout in one TOML file and apply it only on request](../../../howto/persistence/user-data/persist-imgui-window-state/persist-imgui-window-state-3.md) - how-to article demonstrating this feature

[^layout-definition]: purrTTY.Display/Layouts/LayoutDefinition.cs
[^layout-format]: purrTTY.Display/Layouts/LayoutTomlFormat.cs
[^layout-manager]: purrTTY.GameMod/Layouts/LayoutManager.cs
[^session]: purrTTY.Terminal/Sessions/TerminalSession.cs
[^history]: purrTTY.Display/Configuration/StartupCommandHistory.cs
[^terminal-mod]: purrTTY.GameMod/TerminalMod.cs
[^gotchas]: purrTTY docs/gotchas.md
[^how-to]: purrTTY docs/how-to.md
