---
type: Mod Feature
title: Click-through lock mode with a refocus hot zone
description: Make an unfocused ImGui window ignore the mouse so clicks reach the game, and give it a tiny separate hot-zone window that refocuses it, with focus visuals drawn on the foreground draw list
mod: purrtty
category: ui
techniques: [imgui-window, imgui-drawlist, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImGuiWindowFlags, KSA.Program, KSA.GameViewport]
tags: [imgui, mouse, click-through, focus, overlay, hud]
---

With lock mode on, a terminal that does not have focus becomes a pure overlay: the mouse passes
through it to the game world or game UI underneath, so a player can keep a log or telemetry
terminal over the flight view and still click parts and gauges. A small colored square (the hot
zone, top-right by default, configurable corner/side, size, color and opacities) stays clickable and
refocuses the terminal. Optional borders show focus and hover without affecting layout.

## How it works

- **Click-through.** `TerminalWindow.Render` computes
  `clickThrough = LockMode && !_hasFocus && !_wantFocus && !IsContextMenuOpen` and adds
  `ImGuiWindowFlags.NoMouseInputs` to the `Begin` flags. ImGui then excludes the window from hover
  resolution: no widget interaction, no drag or resize, and `WantCaptureMouse` stays false, so KSA's
  `Program.OnMouseButton` handles the click (it bails out only when ImGui wants the mouse over the
  main viewport).[^terminal-window][^ksa-program]
- **Refocus window.** A `NoMouseInputs` window cannot receive the click that would refocus it, so
  `RenderHotZone` submits a second, decoration-less window (`NoDecoration`, `NoMove`,
  `NoSavedSettings`, `NoBackground`, `NoFocusOnAppearing`, `NoNav`) positioned on the terminal rect
  with an `InvisibleButton`. `ImGui.IsItemActivated()` (press, not release) calls `RequestFocus()`;
  next frame `SetNextWindowFocus` is applied and click-through is already off because `_wantFocus`
  is part of the condition.[^hot-zone]
- **No dead pixels.** The hot-zone window pushes `WindowMinSize = (1,1)`, zero padding and zero
  border, so a 28 px zone does not grow to ImGui's 32 px minimum and eat game clicks.
- **Fill drawn on the foreground list.** The zone's fill and the terminal's focus/hover border use
  `ImGui.GetForegroundDrawList()`, which renders above all windows and never takes part in layout.
- **Re-lock is just focus loss.** Clicking the world or another window unfocuses the terminal; the
  next frame it is click-through again. The open grid context menu counts as focus so the popup
  does not vanish under the cursor.
- **No chrome while locked.** `showChrome` also requires `!clickThrough`, so hovering a locked
  terminal does not reveal a menu strip the mouse cannot use.
- The settings (`LockMode`, `HotZoneEnabled`, `HotZonePlacement`, width/height, color, opacity,
  hover opacity, `BorderOnFocus`, `BorderOnHover`, `BorderOpacity`) live in
  `TerminalWindowSettings` and round-trip through user themes.[^window-settings]

## Lifecycle and timing

- Everything is decided per frame in `frame:draw-ui` (`[StarMapAfterGui]` -> controller ->
  `TerminalWindow.Render`). The terminal window is submitted first, then its hot zone. ImGui's
  display order follows focus, not submission (`NoFocusOnAppearing` keeps the zone from raising
  itself), so the zone is not guaranteed to be above the terminal. It still gets the click because
  the locked terminal has `NoMouseInputs` and is skipped by ImGui's hover test, and its fill is drawn
  on the foreground draw list, which renders above every window.
- Mouse buttons reach `Program.OnMouseButton` during `Glfw.PollEvents` in the next frame's
  `PrepareFrame`; the `WantCaptureMouse` it checks reflects the windows submitted in the previous
  frame's UI pass, so a window's click-through state for a click is the one it had when last drawn.
- The game-keyboard gate follows focus too, so a locked, unfocused terminal never eats keys (see
  [input gating](./gate-game-input-while-terminal-focused.md)).

## Key code

| Path | Role |
|---|---|
| [purrTTY.Display/Ghostty/TerminalWindow.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.cs#L310-L324) | `clickThrough` and `showChrome` |
| [purrTTY.Display/Ghostty/TerminalWindow.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.cs#L375-L385) | `NoMouseInputs` on `Begin` |
| [purrTTY.Display/Ghostty/TerminalWindow.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.cs#L556-L578) | Foreground-list border and hot-zone submission |
| [purrTTY.Display/Ghostty/TerminalWindow.HotZone.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.HotZone.cs#L17-L84) | Hot-zone window, activation, placement |
| [purrTTY.Display/Ghostty/TerminalWindowSettings.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindowSettings.cs#L25-L57) | Lock, border and hot-zone settings |
| [purrTTY.Display/Theming/ThemeDefinition.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Theming/ThemeDefinition.cs#L20-L30) | `HotZonePlacement` values |

## Snippets

```csharp
// purrTTY.Display/Ghostty/TerminalWindow.HotZone.cs L24-L61
        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        // WindowMinSize would otherwise inflate a small zone to 32x32, leaving
        // dead window area that eats game clicks the user expects to pass through.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, new float2(1f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(0f, 0f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.Begin(_hotZoneImguiName,
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleVar(3);

        ImGui.SetCursorScreenPos(pos);
        ImGui.InvisibleButton("##hotzone", size);
        bool hovered = ImGui.IsItemHovered();
        if (ImGui.IsItemActivated())
        {
            // Focus on press (not click-release) so the terminal feels instant;
            // the next frame drops click-through and input works normally.
            RequestFocus();
        }

        if (hovered)
        {
            ImGui.SetTooltip("Focus terminal"u8);
        }

        float opacity = hovered ? Settings.HotZoneHoverOpacity : Settings.HotZoneOpacity;
        if (opacity > 0f)
        {
            // Foreground list: the fill must stay visible above the terminal
            // window's background regardless of ImGui window z-order.
            var fill = Settings.HotZoneColor.WithAlpha((byte)Math.Clamp(opacity * 255f, 0f, 255f));
            ImGui.GetForegroundDrawList().AddRectFilled(pos, pos + size, FrameGridRenderer.ToU32(fill), 3f);
        }

        ImGui.End();
```

```csharp
// purrTTY.Display/Ghostty/TerminalWindow.cs L319-L324
        bool clickThrough = Settings.LockMode && !_hasFocus && !_wantFocus && !IsContextMenuOpen;

        // While click-through, hovering must not reveal chrome the mouse cannot
        // interact with — the hot zone (and optional hover border) are the only
        // affordances of a locked window.
        bool showChrome = !hideChromeWhenNotHovered || (_wasHoveredLastFrame && !clickThrough);
```

## Game APIs touched

| API | Where | Purpose |
|---|---|---|
| `ImGuiWindowFlags.NoMouseInputs`, `NoDecoration`, `NoFocusOnAppearing` | [current/decomp/Brutal.ImGuiApi/ImGuiWindowFlags.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiWindowFlags.cs) | Click-through and the auxiliary window[^ksa-window-flags] |
| `ImGui.IsItemActivated`, `InvisibleButton`, `GetForegroundDrawList`, `SetNextWindowFocus` | `Brutal.ImGuiApi.ImGui` | Press-to-focus and overlay drawing |
| `Program.OnMouseButton` (`WantCaptureMouse && InputViewport.IsMain()` early-out) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2003-L2025) | Why a non-capturing window lets clicks reach the game |

## Pitfalls and version notes

- Hiding the window instead of using `NoMouseInputs` would also hide the text; `NoMouseInputs` keeps
  rendering while removing it from hit testing.
- Popups opened from the window steal ImGui focus; treat an open popup as focused or the window
  turns click-through under its own context menu (gotcha 22).[^gotchas]
- Focus visuals must not touch layout (no extra border size, no padding change), or the grid and PTY
  resize when focus changes.
- No KSA API is patched here; the behavior depends only on ImGui hover rules and on
  `Program.OnMouseButton` honoring `WantCaptureMouse`, which holds in build 2026.10.10.5554.

## See also

- [Multi-window terminals with tabs, hidden chrome and grid snap](./terminal-window-chrome-tabs-and-resize-snap.md)
- [Gate game input while a terminal is focused](./gate-game-input-while-terminal-focused.md)
- [TOML themes and settings](./toml-themes-and-settings.md)
- [purrTTY overview](../overview.md)
- [Click-through lock mode: a window that ignores the mouse until a hot zone refocuses it](../../../howto/ui/overlays/draw-a-fullscreen-click-through-overlay/draw-a-fullscreen-click-through-overlay-4.md) - how-to article demonstrating this feature

[^terminal-window]: purrTTY.Display/Ghostty/TerminalWindow.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^hot-zone]: purrTTY.Display/Ghostty/TerminalWindow.HotZone.cs
[^window-settings]: purrTTY.Display/Ghostty/TerminalWindowSettings.cs
[^gotchas]: purrTTY docs/gotchas.md
[^ksa-window-flags]: Brutal.ImGuiApi.ImGuiWindowFlags (decompiled, build 2026.10.10.5554)
