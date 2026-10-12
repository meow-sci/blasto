---
type: Mod Feature
title: Multi-window terminals with tabs, hidden chrome and grid snap
description: Run several title-bar-less ImGui terminal windows with tab bars, chrome that fades without changing layout, an invisible grid button that stops drag-moves, and a resize that snaps to whole cells
mod: purrtty
category: ui
techniques: [imgui-window, imgui-drawlist, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types:
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImGuiWindowFlags
  - Brutal.ImGuiApi.ImGuiTabBarFlags
  - Brutal.ImGuiApi.ImGuiViewportPtr
tags: [imgui, windows, tabs, layout, resize, immersion]
---

A player opens as many terminal windows as they like, each with tabs (one shell session per tab),
from the purrTTY menu or the toggle hotkey. The windows are built for immersion: no title bar,
and when the mouse is not over a window its background, border, menu strip and tab bar become fully
transparent, leaving only the text floating over the game. Resizing snaps the window so the grid
fills it with whole character cells.

## How it works

**Controller.** `GhosttyTerminalController` owns a `List<TerminalWindow>`. `OpenWindow` creates a
`SessionManager`, settings from the configured defaults, and a window positioned at the saved
geometry (first window) or cascaded 40 px from the focused one; it then starts the first session on
a pool thread (`Task.Run` -> `CreateSessionAsync`). `OpenTab` adds a session to the focused window.
`FocusTarget` resolves which window menu actions apply to (focused, else last focused, else first).
`Render` prunes windows closed last frame, renders the rest, and sets the static
`IsAnyTerminalActive` used by the game-input gate.[^controller]

**Window.** Each `TerminalWindow.Render` call:

| Concern | Technique |
|---|---|
| No title bar, draggable | `ImGuiWindowFlags` `NoTitleBar`, `MenuBar`, `NoScrollbar`, `NoScrollWithMouse`; the menu-bar strip (title text and an "x" item) is the drag handle |
| Hidden chrome without layout change | When not hovered: push transparent `WindowBg`, `MenuBarBg`, `ResizeGrip*`, zero border size, and render the menu strip and tab bar at `ImGuiStyleVar.Alpha = 0`. They are still submitted, so `GetContentRegionAvail` (and therefore cols/rows and the PTY size) never changes when chrome fades in |
| Terminal background while chrome hidden | Paint the grid rect with the theme background at `BackgroundOpacity` via the window draw list |
| Hover detection | Mouse inside the window rect plus an 8 px margin (covers resize grips), or a selection drag in progress; the result drives next frame's chrome |
| Click-drag selects text instead of moving the window | `ImGui.InvisibleButton("##grid", avail)` over the painted grid; its `IsItemHovered` also gates mouse input |
| Tabs | `BeginTabBar` with `Reorderable`, `AutoSelectNewTabs`, `FittingPolicyScroll`, hidden entirely when there is one session; a newly active session is forced with `ImGuiTabItemFlags.SetSelected` and the stale "previous tab selected" report is ignored that frame |
| Tab labels | Cached per session id (`title (exit N)##tab_<guid>`) so the render path does not allocate strings |
| Grid size | `cols = avail.X / cellWidth`, `rows = avail.Y / cellHeight`; on change, resize the engine surface, the session and the PTY |
| Grid snap | `TrackResizeSnap`: a resize is a size change while the left button is held; on release the window shrinks by the fractional-cell remainder measured as `windowSize - avail` |
| Off-screen geometry | Restored or cascaded geometry is clamped into `ImGui.GetMainViewport().WorkPos/WorkSize` |
| Focus | `RequestFocus()` sets a flag consumed as `ImGui.SetNextWindowFocus()`; `IsWindowFocused()` updates `HasFocus` and raises `FocusChanged` |
| Focus border | `AddRect` on the foreground draw list, never part of layout |

Per-window appearance (theme colors, font, three opacities, cursor, border, lock mode) lives in
`TerminalWindowSettings`; the config file only holds defaults for new windows.

**Visibility.** `IsVisible = true` opens a default-shell window if none is open (no shell is ever
started until the player asks), otherwise focuses the focus target. Hiding persists the primary
window geometry. Closing the last tab retires its window; closing the last window hides the
terminal and saves the pruned window's geometry.

## Lifecycle and timing

- All of this runs in `TerminalMod.OnAfterUi` (`[StarMapAfterGui]`, `frame:draw-ui`), which calls
  `_controller.Update(dt)` then `_controller.Render()` every frame, even while hidden: `Render`
  early-outs when hidden and that early-out is what clears `IsAnyTerminalActive` (gotcha 21).[^terminal-mod][^gotchas]
- Session creation runs on a pool thread. `TerminalWindow` installs `Sessions.SessionConfigurator`
  so theme push and event wiring happen before a new session is published to the render thread
  (gotcha 19). Closing a tab is deliberately synchronous on the render thread because it disposes
  the native engine surface that the render thread ticks.[^terminal-window]
- Geometry (`SetNextWindowPos/Size`) is applied before `Begin`; the snap target computed this frame
  is applied next frame with the mouse up, so it can never be mistaken for a user resize.
- While the HUD is hidden (`Program.DrawUI == false`) the hook does not run and windows are not drawn.

## Key code

| Path | Role |
|---|---|
| [purrTTY.Display/Ghostty/TerminalWindow.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.cs#L300-L579) | `Render`: chrome, placement, focus, grid, tabs, input, border |
| [purrTTY.Display/Ghostty/TerminalWindow.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.cs#L581-L666) | Title strip and tab bar, both kept in layout at alpha 0 |
| [purrTTY.Display/Ghostty/TerminalWindow.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.cs#L708-L772) | Resize snap and work-area clamp |
| [purrTTY.Display/Ghostty/GhosttyTerminalController.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/GhosttyTerminalController.cs#L90-L125) | `OpenWindow`: sessions, defaults, saved or cascaded geometry |
| [purrTTY.Display/Ghostty/GhosttyTerminalController.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/GhosttyTerminalController.cs#L321-L442) | Visibility, hidden drain, prune, render, input-gate flag |
| [purrTTY.Display/Ghostty/TerminalWindowSettings.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindowSettings.cs) | Per-window appearance state |
| [purrTTY.Terminal/Sessions/SessionManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Sessions/SessionManager.cs) | Sessions that back the tabs |

## Snippets

Hidden chrome keeps the strip in layout but draws nothing:

```csharp
// purrTTY.Display/Ghostty/TerminalWindow.cs L581-L607
    private void RenderTitleStrip(bool showChrome)
    {
        if (!showChrome)
        {
            // Keep the strip (and its drag area / layout height) but draw nothing.
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0f);
        }

        if (ImGui.BeginMenuBar())
        {
            ImGui.TextDisabled($" {Title}");

            float closeWidth = ImGui.GetFrameHeight() * 1.5f;
            ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowWidth() - closeWidth));
            if (ImGui.MenuItem("x##close_window") && showChrome)
            {
                Close();
            }

            ImGui.EndMenuBar();
        }

        if (!showChrome)
        {
            ImGui.PopStyleVar();
        }
    }
```

The snap: detect a user resize by "size changed while LMB held", act on release.

```csharp
// purrTTY.Display/Ghostty/TerminalWindow.cs L730-L752
        bool sizeChanged =
            Math.Abs(windowSize.X - _trackedWindowSize.X) > 0.5f ||
            Math.Abs(windowSize.Y - _trackedWindowSize.Y) > 0.5f;
        _trackedWindowSize = windowSize;

        bool mouseDown = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        if (sizeChanged && mouseDown)
        {
            _userResizing = true;
            return;
        }

        if (_userResizing && !mouseDown)
        {
            _userResizing = false;

            float targetX = windowSize.X - (avail.X - cols * _cellWidth);
            float targetY = windowSize.Y - (avail.Y - rows * _cellHeight);
            if (Math.Abs(targetX - windowSize.X) > 0.5f || Math.Abs(targetY - windowSize.Y) > 0.5f)
            {
                _pendingSnapSize = new float2(targetX, targetY);
            }
        }
```

The invisible grid button that keeps body drags from moving a title-bar-less window:

```csharp
// purrTTY.Display/Ghostty/TerminalWindow.cs L523-L536
        // Invisible button over the grid: claims the mouse so click-drag selects
        // text instead of moving the window (the menu strip remains the drag handle).
        bool gridHovered = false;
        if (avail.X >= 1f && avail.Y >= 1f)
        {
            ImGui.SetCursorScreenPos(canvasPos);
            ImGui.InvisibleButton("##grid", avail);
            gridHovered = ImGui.IsItemHovered();
        }

        if (session != null && _hasFocus)
        {
            HandleInput(session, canvasPos, cols, rows, gridHovered);
        }
```

## Game APIs touched

All Brutal ImGui bindings in [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs):[^ksa-imgui]

| API | Purpose |
|---|---|
| `ImGui.Begin` with `NoTitleBar`, `MenuBar`, `NoMouseInputs` (lock mode) | Window shell |
| `ImGui.PushStyleColor` / `PushStyleVar(Alpha, WindowPadding, WindowBorderSize)` | Chrome hiding without layout change |
| `ImGui.BeginMenuBar`, `BeginTabBar`, `BeginTabItem(label, ref open, flags)` | Drag strip and tabs |
| `ImGui.InvisibleButton`, `IsItemHovered`, `IsMouseDown` | Grid hit area and resize detection |
| `ImGui.SetNextWindowPos/Size/Focus`, `GetMainViewport().WorkPos/WorkSize` | Placement, snap and focus |
| `ImGui.GetForegroundDrawList().AddRect` | Focus/hover border overlay |

## Pitfalls and version notes

- Only style colors and alpha may differ between hidden and shown chrome; any layout difference
  changes the grid size and sends a PTY resize every time the mouse enters the window (gotcha 8).[^gotchas]
- Without an item under the cursor, ImGui treats a click-drag on a title-bar-less window body as a
  window move, so selection silently breaks (gotcha 9).
- The tab bar needs the `!activeChanged` guard: on the frame after an external switch ImGui still
  reports the old tab as selected, and switching on that report undoes the change.
- Measure chrome as `windowSize - avail` instead of summing style metrics; it is exact for any style.
- No KSA-specific API is used here, so this part is insensitive to game updates.

## See also

- [Terminal grid drawn with ImGui draw lists](./terminal-grid-drawlist-renderer.md)
- [Keyboard and mouse to VT input](./imgui-keyboard-mouse-to-vt-input.md)
- [Click-through lock mode and hot zone](./click-through-lock-mode-hot-zone.md)
- [Gate game input while a terminal is focused](./gate-game-input-while-terminal-focused.md)
- [Saved terminal layouts](./saved-terminal-layouts.md)
- [purrTTY overview](../overview.md)
- [Tabbed window: a closeable ImGui tab bar with programmatic selection](../../../howto/ui/windows/build-a-tabbed-imgui-window/build-a-tabbed-imgui-window-1.md) - how-to article demonstrating this feature
- [Chrome-less plain-ImGui windows: alpha-0 title bar, InvisibleButton drag guard and resize snapped to a character grid](../../../howto/ui/windows/subclass-the-stock-imguiwindow/subclass-the-stock-imguiwindow-5.md) - how-to article demonstrating this feature

[^controller]: purrTTY.Display/Ghostty/GhosttyTerminalController.cs
[^terminal-mod]: purrTTY.GameMod/TerminalMod.cs
[^gotchas]: purrTTY docs/gotchas.md
[^terminal-window]: purrTTY.Display/Ghostty/TerminalWindow.cs
[^ksa-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
