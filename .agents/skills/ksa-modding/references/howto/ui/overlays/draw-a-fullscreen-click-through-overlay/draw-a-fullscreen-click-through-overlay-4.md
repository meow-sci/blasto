---
type: How-To
title: "Click-through lock mode: a window that ignores the mouse until a hot zone refocuses it"
description: Let a normal ImGui window become a pure overlay that clicks pass through while unfocused, with a small hot-zone window to take focus back and focus visuals on the foreground list
category: ui
group: overlays
topic: draw-a-fullscreen-click-through-overlay
approach: 4
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/purrtty/features/click-through-lock-mode-hot-zone]
techniques: [imgui-window, imgui-drawlist]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:input]
game_types: [Brutal.ImGuiApi.ImGuiWindowFlags, Brutal.ImGuiApi.ImGui, KSA.Program]
tags: [overlay, click-through, lock-mode, hot-zone, nomouseinputs, focus, window]
---
purrTTY's terminal is a real, interactive window, but with lock mode on, a terminal that does not have focus becomes a pure overlay: the mouse passes through it to the game world or the game UI underneath, so a player can keep a
log or telemetry terminal over the flight view and still click parts and gauges. A small coloured square (the hot zone, top-right by default) stays clickable and refocuses the terminal. This is the "interactive window that
turns into an overlay" pattern, as opposed to [a window that is always input-less](draw-a-fullscreen-click-through-overlay-1.md). Demonstrated by purrTTY.

## Background: how the game does it

- ImGui's `NoMouseInputs` (0x200) removes a window from hover resolution: no widget interaction, no drag or resize, and `WantCaptureMouse` stays false.[current/decomp/Brutal.ImGuiApi/ImGuiWindowFlags.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiWindowFlags.cs#L5-L30)[^brutal-imguiapi-imguiwindowflags]
- `Program.OnMouseButton` returns early only when ImGui wants the mouse over the main viewport (and for popups and bound inputs); otherwise the click reaches the game's click and camera handling. So a window that does not capture
  lets the game see the click.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2003-L2022)[^ksa-program]
- `NoMouseInputs` keeps rendering the window; hiding it would also hide its text.
- No KSA API is patched: the behaviour depends only on ImGui hover rules and on `Program.OnMouseButton` honouring `WantCaptureMouse`, which holds in build 2026.10.10.5554.

## Lifecycle and timing

Everything is decided per frame in `frame:draw-ui` (`[StarMapAfterGui]` -> controller -> `TerminalWindow.Render`). The terminal window is submitted first, then its hot zone. ImGui's display order follows focus, not submission
(`NoFocusOnAppearing` keeps the zone from raising itself), so the zone is not guaranteed to be above the terminal; it still gets the click because the locked terminal has `NoMouseInputs` and is skipped by hover testing, and its fill
is drawn on the foreground list, which renders above every window. Mouse buttons reach `Program.OnMouseButton` during `Glfw.PollEvents` in the next frame's `PrepareFrame`; the `WantCaptureMouse` it checks reflects the windows submitted in
the previous frame's UI pass, so a window's click-through state for a click is the one it had when last drawn. The game-keyboard gate follows focus too, so a locked unfocused terminal never eats keys
(see [block game hotkeys while typing](../../input/block-game-hotkeys-while-typing/block-game-hotkeys-while-typing-2.md)). See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Compute click-through from focus.** A pending focus request and an open context menu count as focused, otherwise the window would turn click-through under its own popup and strand itself unfocused:

```csharp
// purrTTY.Display/Ghostty/TerminalWindow.cs L319-L324
bool clickThrough = Settings.LockMode && !_hasFocus && !_wantFocus && !IsContextMenuOpen;

// While click-through, hovering must not reveal chrome the mouse cannot
// interact with — the hot zone (and optional hover border) are the only
// affordances of a locked window.
bool showChrome = !hideChromeWhenNotHovered || (_wasHoveredLastFrame && !clickThrough);
```

2. **Add `NoMouseInputs` to the `Begin` flags** while click-through:

```csharp
// purrTTY.Display/Ghostty/TerminalWindow.cs L375-L385
var flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.MenuBar;
if (clickThrough)
{
    // NoMouseInputs removes the window from ImGui's hover resolution
    // entirely: no widget interaction, no drag-move, no resize — and no
    // WantCaptureMouse, so the click reaches the game.
    flags |= ImGuiWindowFlags.NoMouseInputs;
}

ImGui.Begin(_imguiName, flags);
```

3. **Submit a second, tiny window for refocus.** It has no decoration, no background and an `InvisibleButton`; press (not release) requests focus so the terminal feels instant. Push `WindowMinSize = (1,1)`, zero padding and
   zero border or a 28 px zone inflates to ImGui's 32 px minimum and eats game clicks. Draw the zone's fill on the foreground list so it stays visible above the terminal regardless of window z-order:

```csharp
// purrTTY.Display/Ghostty/TerminalWindow.HotZone.cs L17-L62
private void RenderHotZone(float2 windowPos, float2 windowSize)
{
    float w = Math.Clamp(Settings.HotZoneWidth, MinHotZoneSize, Math.Max(MinHotZoneSize, windowSize.X));
    float h = Math.Clamp(Settings.HotZoneHeight, MinHotZoneSize, Math.Max(MinHotZoneSize, windowSize.Y));
    var size = new float2(w, h);
    var pos = HotZonePosition(windowPos, windowSize, w, h);

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
}
```

4. **Draw focus and hover borders on the foreground list**, never as window border size or padding, so focus changes never resize the grid. The hover test uses the window rectangle, so a locked window lights up under the mouse
   without capturing it:

```csharp
// purrTTY.Display/Ghostty/TerminalWindow.cs L556-L579
    // Focus/hover border. Drawn on the foreground list as a pure overlay:
    // it never participates in layout, so chrome hiding (which requires a
    // stable grid size) is unaffected. Hover uses the same rect test as
    // chrome visibility, so it also works while click-through — a locked
    // window lights up under the mouse without capturing it.
    if (Settings.BorderOpacity > 0f
        && ((Settings.BorderOnFocus && _hasFocus) || (Settings.BorderOnHover && mouseInBounds)))
    {
        var border = Settings.Colors.Foreground.WithAlpha((byte)Math.Clamp(Settings.BorderOpacity * 255f, 0f, 255f));
        ImGui.GetForegroundDrawList().AddRect(
            windowPos, windowPos + windowSize, FrameGridRenderer.ToU32(border), thickness: BorderThickness);
    }

    ImGui.End();
    ImGui.PopStyleColor(colorPushes);

    // The hot zone is its own tiny window submitted after the terminal: with
    // the terminal click-through it is the one mouse-interactive spot that
    // refocuses the window (and absorbs that click so the game never sees it).
    if (clickThrough && Settings.HotZoneEnabled && HasObservedGeometry)
    {
        RenderHotZone(windowPos, windowSize);
    }
}
```

5. **Re-lock is just focus loss.** Clicking the world or another window unfocuses the terminal; next frame it is click-through again. No chrome is shown while locked (`showChrome` requires `!clickThrough`), so hovering a locked
   terminal does not reveal controls the mouse cannot use.

## Complete example

| File | Role |
|---|---|
| [purrTTY.Display/Ghostty/TerminalWindow.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.cs#L300-L579) | `clickThrough`, `showChrome`, `NoMouseInputs`, foreground border, hot-zone submission |
| [purrTTY.Display/Ghostty/TerminalWindow.HotZone.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.HotZone.cs) | Hot-zone window, activation, placement |
| [purrTTY.Display/Ghostty/TerminalWindowSettings.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindowSettings.cs) | Lock, border and hot-zone settings |

## Variations and gotchas

- **Popups steal ImGui focus.** Treat an open popup as focused (the `IsContextMenuOpen` term) or the window turns click-through under its own context menu.
- **Focus visuals must not touch layout** (no extra border size, no padding change), or the grid and PTY resize when focus changes.
- **Hot-zone placement** is a setting (corner, side, size, colour, opacities) that round-trips through user themes.
- **Not the same as an always-input-less overlay.** This window can be focused and typed into; an always-input-less overlay never can.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of draw-a-fullscreen-click-through-overlay](overview.md)
- [Approach 1: NoInputs/NoBackground fullscreen window](draw-a-fullscreen-click-through-overlay-1.md)
- [Build a mod window on the game's ImGuiWindow base class](../../windows/subclass-the-stock-imguiwindow/overview.md)
- Mod feature: [Click-through lock mode and hot zone](../../../../mods/purrtty/features/click-through-lock-mode-hot-zone.md)

[^brutal-imguiapi-imguiwindowflags]: Brutal.ImGuiApi.ImGuiWindowFlags (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
