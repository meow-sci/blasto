---
type: How-To
title: "Chrome-less plain-ImGui windows: alpha-0 title bar, InvisibleButton drag guard and resize snapped to a character grid"
description: Take over a window's chrome entirely with a plain ImGui.Begin - hide it without changing layout, stop body drags moving the window, and snap resizing to whole cells
category: ui
group: windows
topic: subclass-the-stock-imguiwindow
approach: 5
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/purrtty/features/terminal-window-chrome-tabs-and-resize-snap]
techniques: [imgui-window]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImGuiWindowFlags, Brutal.ImGuiApi.ImGuiViewportPtr]
tags: [window, chromeless, resize-snap, invisiblebutton, plain-imgui, terminal, transparent]
---
Deriving from `ImGuiWindow` gives the game's chrome, which is the right default. purrTTY wants the opposite: terminals with **no title bar** that, when the mouse is not over them, drop their background, border, menu strip and tab bar entirely so only text floats over the game, and whose resize
snaps so the grid always fills the window with whole character cells. That needs a plain `ImGui.Begin` window and a handful of tricks about layout stability. This article is that "take over the chrome" variant. Demonstrated by purrTTY. Prerequisite: StarMap.

## Background: how the game does it

Nothing in KSA is special-cased: a plain `ImGui.Begin` window needs no base class. What the stock `ImGuiWindow` offers that you give up: the View menu (always on top, detach, autohide), `NoSavedSettings` placement, centred initial position, aspect-ratio lock. `ImGuiWindowFlags` such as `NoTitleBar`, `MenuBar`,
`NoScrollbar` and `NoScrollWithMouse` are ordinary enum values.[current/decomp/Brutal.ImGuiApi/ImGuiWindowFlags.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiWindowFlags.cs#L5-L35)[^brutal-imguiapi-imguiwindowflags] `ImGui.GetMainViewport().WorkPos/WorkSize` give the usable area for clamping restored geometry.

## Lifecycle and timing

All of this runs in the mod's `[StarMapAfterGui]` method (`frame:draw-ui`): `OnAfterUi` calls the controller's `Update(dt)` and `Render()` every frame, even while hidden (the early-out in `Render` is what clears the game-input gate flag). Geometry (`SetNextWindowPos/Size`) is applied before `Begin`; the snap target computed this
frame is applied next frame with the mouse up, so it can never be mistaken for a user resize. While the HUD is hidden the hook does not run and windows are not drawn. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Pick the shell flags.** No title bar, a menu bar that serves as the drag handle and a title strip, no scrollbar or wheel scroll (the content is a fixed grid). Apply restored or snapped geometry with `ImGuiCond.Always` only on the frames you mean to:

```csharp
// purrTTY.Display/Ghostty/TerminalWindow.cs L346-L367,L375-L385
ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(0f, 0f));
ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, showChrome ? 1f : 0f);

if (_pendingPlacement is { } placement)
{
    // Saved/cascaded geometry can land fully off-screen (different
    // monitor layout since last run); with chrome hidden there would be
    // nothing visible to grab, so clamp into the viewport work area.
    var (clampedPos, clampedSize) = ClampToWorkArea(placement.Pos, placement.Size);
    ImGui.SetNextWindowPos(clampedPos, ImGuiCond.Always);
    ImGui.SetNextWindowSize(clampedSize, ImGuiCond.Always);
    _pendingPlacement = null;
}
else if (_pendingSnapSize is { } snapSize)
{
    ImGui.SetNextWindowSize(snapSize, ImGuiCond.Always);
    _pendingSnapSize = null;
}
else
{
    ImGui.SetNextWindowSize(new float2(880f, 520f), ImGuiCond.FirstUseEver);
}
// ...
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

2. **Hide the chrome without changing layout.** When the mouse is not over the window: push transparent colours for `WindowBg`, `MenuBarBg` and the resize grips, set the border size to 0, and render the title strip and tab bar at `ImGuiStyleVar.Alpha = 0`. They are still submitted, so `GetContentRegionAvail` (and therefore cols/rows and the
   shell's size) never changes when the chrome fades in and out; the grip stays functional while invisible:

```csharp
// purrTTY.Display/Ghostty/TerminalWindow.cs L326-L344
var bg = Settings.Colors.Background;
var windowBg = showChrome
    ? new float4(bg.R / 255f, bg.G / 255f, bg.B / 255f, Settings.BackgroundOpacity)
    : new float4(0f, 0f, 0f, 0f);

int colorPushes = 1;
ImGui.PushStyleColor(ImGuiCol.WindowBg, windowBg);
if (!showChrome)
{
    // The menu-bar strip background and the resize grip are part of the
    // window decorations drawn during Begin, so they have to be hidden
    // via style colors here. The grip stays functional while invisible.
    var transparent = new float4(0f, 0f, 0f, 0f);
    ImGui.PushStyleColor(ImGuiCol.MenuBarBg, transparent);
    ImGui.PushStyleColor(ImGuiCol.ResizeGrip, transparent);
    ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered, transparent);
    ImGui.PushStyleColor(ImGuiCol.ResizeGripActive, transparent);
    colorPushes += 4;
}
```
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

3. **Guard body drags with an `InvisibleButton`.** Without an item under the cursor, ImGui treats a click-drag on a title-bar-less window body as a window move, so text selection silently breaks. An invisible button over the grid claims the mouse; its `IsItemHovered` also gates the app's mouse input. The menu strip stays the drag handle:

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

4. **Snap resizing to the cell grid.** A user resize is detected as "the window size changed while the left button is held"; on release the window shrinks by the fractional-cell remainder, measured as `windowSize - avail`. The target is applied next frame:

```csharp
// purrTTY.Display/Ghostty/TerminalWindow.cs L721-L753
private void TrackResizeSnap(float2 windowSize, float2 avail, int cols, int rows)
{
    if (!_windowSizeTracked)
    {
        _trackedWindowSize = windowSize;
        _windowSizeTracked = true;
        return;
    }

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
}
```

5. **Clamp restored geometry into the work area**, since a different monitor layout can leave a saved window fully off-screen, and with the chrome hidden there would be nothing to grab:

```csharp
// purrTTY.Display/Ghostty/TerminalWindow.cs L755-L772
// Saved or cascaded geometry is clamped into the main viewport's work area:
// restoring fully off-screen with chrome hidden leaves nothing visible to grab.
private static (float2 Pos, float2 Size) ClampToWorkArea(float2 pos, float2 size)
{
    var viewport = ImGui.GetMainViewport();
    var workPos = viewport.WorkPos;
    var workSize = viewport.WorkSize;
    if (workSize.X <= 0f || workSize.Y <= 0f)
    {
        return (pos, size);
    }

    float w = Math.Clamp(size.X, Math.Min(100f, workSize.X), workSize.X);
    float h = Math.Clamp(size.Y, Math.Min(60f, workSize.Y), workSize.Y);
    float x = Math.Clamp(pos.X, workPos.X, Math.Max(workPos.X, workPos.X + workSize.X - w));
    float y = Math.Clamp(pos.Y, workPos.Y, Math.Max(workPos.Y, workPos.Y + workSize.Y - h));
    return (new float2(x, y), new float2(w, h));
}
```

6. **Draw focus and hover borders on the foreground list**, never as layout (see [click-through lock mode](../../overlays/draw-a-fullscreen-click-through-overlay/draw-a-fullscreen-click-through-overlay-4.md)).

## Complete example

| File | Role |
|---|---|
| [purrTTY.Display/Ghostty/TerminalWindow.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.cs#L300-L580) | `Render`: chrome, placement, focus, grid, tabs, input, border |
| [purrTTY.Display/Ghostty/TerminalWindow.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.cs#L581-L607) | Title strip kept in layout at alpha 0 |
| [purrTTY.Display/Ghostty/TerminalWindow.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.cs#L721-L772) | Resize snap and work-area clamp |
| [purrTTY.Display/Ghostty/GhosttyTerminalController.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/GhosttyTerminalController.cs) | Window list, defaults, saved or cascaded geometry |

## Variations and gotchas

- **Only colours and alpha may differ between hidden and shown chrome.** Any layout difference changes the grid size and sends a shell resize every time the mouse enters the window.
- **Hover detection** uses the window rectangle plus an 8 px margin (covers the resize grips) or a selection drag in progress; the result drives next frame's chrome.
- **Tabs** are an ImGui tab bar ([build a tabbed window](../build-a-tabbed-imgui-window/overview.md)) hidden entirely when there is one session.
- **Window id.** Use a stable id for the window ([approach 4](subclass-the-stock-imguiwindow-4.md)).
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of subclass-the-stock-imguiwindow](overview.md)
- [Approach 4: stable window ids](subclass-the-stock-imguiwindow-4.md)
- [Click-through lock mode](../../overlays/draw-a-fullscreen-click-through-overlay/draw-a-fullscreen-click-through-overlay-4.md)
- [Draw per-frame text efficiently: monospace grids and allocation-free labels](../../draw-lists/draw-a-monospace-text-grid/overview.md)
- Mod feature: [Terminal window chrome, tabs and resize snap](../../../../mods/purrtty/features/terminal-window-chrome-tabs-and-resize-snap.md)

[^brutal-imguiapi-imguiwindowflags]: Brutal.ImGuiApi.ImGuiWindowFlags (decompiled, build 2026.10.10.5554)
