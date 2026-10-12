---
type: How-To
title: "Overlays without a window: ImGui.GetForegroundDrawList anchored to WorkPos and WorkSize"
description: Draw an always-on-top broadcast-style overlay into the foreground draw list with an anchored panel layout and a menu-bar inset read from the game's own window
category: ui
group: overlays
topic: draw-a-fullscreen-click-through-overlay
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-public-telemetry/features/foreground-drawlist-broadcast-overlay]
techniques: [imgui-drawlist, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, render:imgui]
game_types: [Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImDrawListPtr, Brutal.ImGuiApi.ImGuiViewportPtr, KSA.Program]
tags: [overlay, foreground-draw-list, layout, anchors, work-area, menu-bar, broadcast]
---
KSA-Public-Telemetry draws a SpaceX-webcast-style bar along the bottom of the screen: round speed and altitude gauges on the left, a T+ clock in the centre, g-force and an engine diagram on the right. None of it is an
ImGui window. Every shape and glyph goes into `ImGui.GetForegroundDrawList()` once per frame, positioned from the main viewport's work area, so the bar has no chrome, takes no input and draws above every window. This
article is the surrounding structure: why the foreground list, the anchored layout, and the menu-bar inset. The drawing primitives (arcs, gradients) are in
[draw HUD instruments](../draw-hud-instruments-with-the-draw-list/overview.md). Demonstrated by KSA-Public-Telemetry.

## Background: how the game does it

- `ImGui.GetForegroundDrawList(viewport)` and `GetBackgroundDrawList(viewport)` return a per-viewport list that needs no `Begin`/`End` and takes no input focus.[current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8033-L8042)[^brutal-imguiapi-imgui]
  The background list is composited under every window, the foreground list over them.
- The stock helper `ImGuiHelper.GetMainOverlayDrawList()` returns the **background** list, so game windows cover the stock FPS text; the stock galactic sky grid uses the foreground list.[current/decomp/KSA/ImGuiHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiHelper.cs#L43-L46)[^ksa-imguihelper][current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1813-L1825)[^ksa-universe]
- The game draws its menu bar as a separate window named `Menu Bar` (`ImGui.Begin("Menu Bar", flags)` with `BeginMenuBar`), only when the main viewport is hovered and it is not auto-hidden.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3486)[^ksa-program]
- `ImGuiViewportPtr.WorkPos` and `WorkSize` are the viewport minus OS decorations.

## Lifecycle and timing

`TelemetryOverlayMod.OnAfterGui` (`[StarMapAfterGui]`, `frame:draw-ui`) calls `DrawEverything`; the postfix on `Program.OnDrawUiViewports` runs after `PrepareImGui` and before `ImGui.Render`, inside `if (DrawUI)`. Because the
hook runs before `OnDrawUiThreadSafe` draws the menu bar, `MenuBarInset` reads the bar window as ImGui left it after the previous frame: `WasActive` is copied from `Active` in `NewFrame`, and the bar's `Pos`/`Size` are last
frame's. The bar is pinned to the viewport origin, so that lag is harmless. The data drawn is the `TelemetrySnapshot` filled by `[StarMapAfterOnFrame]` at the end of the previous frame (one frame old). When the HUD is hidden
(F2), the same draw runs from a postfix on `Program.DrawFps`; see [keep UI running when the HUD is hidden](../draw-ui-while-the-hud-is-hidden/overview.md). See also
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Take the foreground list and the work rectangle once per frame** and bundle them into a context struct passed to every panel (`IOverlayPanel`: `Id`, `DisplayName`, `IsVisible`, `Measure`, `Draw(in PanelContext, origin, size)`, `Reset`):

```csharp
// Rendering/IOverlayPanel.cs L16-L26,L61-L69
public readonly struct PanelContext(
    ImDrawListPtr drawList,
    TelemetrySnapshot snapshot,
    OverlayConfig config,
    double deltaTime,
    IntroPhases intro,
    float2 viewportPos,
    float2 viewportSize,
    IntroAnimator? animator = null)
{
    public readonly ImDrawListPtr DrawList = drawList;
// ...
public interface IOverlayPanel
{
    string Id { get; }
    string DisplayName { get; }
    bool IsVisible(in PanelContext context);
    float2 Measure(in PanelContext context);
    void Draw(in PanelContext context, float2 origin, float2 size);
    void Reset();
}
```

2. **Inset the rectangle below the game's menu bar** by looking the window up by name and checking it drew last frame. If the game renames the window the code falls back to a fixed clearance:

```csharp
// Rendering/OverlayRenderer.cs L164-L177
private static float MenuBarInset(ImGuiViewportPtr viewport)
{
    float clearance = Tuning.MenuBarClearance;
    ImGuiWindowPtr bar = ImGui.Internal.FindWindowByName("Menu Bar"u8);

    if (bar.IsNull() || !bar.WasActive)
    {
        return clearance;
    }

    float overlap = bar.Pos.Y + bar.Size.Y - viewport.WorkPos.Y;

    return overlap > 0f ? overlap + clearance : clearance;
}
```

3. **Draw the backdrop, then the panels** at their anchors:

```csharp
// Rendering/OverlayRenderer.cs L131-L148
ImGuiViewportPtr viewport = ImGui.GetMainViewport();
ImDrawListPtr drawList = ImGui.GetForegroundDrawList();

PanelContext context = new(
    drawList, snapshot, _config, dt, _intro.Phases,
    viewport.WorkPos, viewport.WorkSize, _intro);

if (_config.ShowBackdrop)
{
    DrawBackdrop(in context, drawList, viewport);
}

float topInset = MenuBarInset(viewport);

_host.DrawAll(
    in context,
    new float2(viewport.WorkPos.X, viewport.WorkPos.Y + topInset),
    new float2(viewport.WorkSize.X, viewport.WorkSize.Y - topInset));
```

4. **Lay out with anchors and running offsets.** `PanelHost.DrawAll` measures each visible panel (unscaled units times `config.Scale`), resolves an origin from the anchor plus a running per-anchor column or row offset, and calls `Draw`.
   Bottom-right panels stack leftwards from the right edge; bottom-left ones stack rightwards:

```csharp
// Rendering/PanelHost.cs L63-L106
public void DrawAll(in PanelContext context, float2 viewportPos, float2 viewportSize)
{
    _stackOffsets.Clear();
    _rowOffsets.Clear();

    float scale = context.Scale;
    float marginX = Tuning.PanelEdgeMarginX * scale;
    float marginY = Tuning.PanelEdgeMarginY * scale;
    float gap = Tuning.PanelGap * scale;

    for (int i = 0; i < _slots.Count; i++)
    {
        PanelSlot slot = _slots[i];
        IOverlayPanel panel = slot.Panel;

        if (!panel.IsVisible(in context))
        {
            continue;
        }

        float2 size = panel.Measure(in context) * scale;
        if (size.X <= 0f || size.Y <= 0f)
        {
            continue;
        }

        _stackOffsets.TryGetValue(slot.Anchor, out float stacked);
        _rowOffsets.TryGetValue(slot.Anchor, out float row);

        float2 origin = ResolveOrigin(
            slot, size, viewportPos, viewportSize, marginX, marginY, scale, stacked, row);

        panel.Draw(in context, origin, size);

        if (slot.StackHorizontally)
        {
            _rowOffsets[slot.Anchor] = row + size.X + gap;
        }
        else if (slot.StackVertically)
        {
            _stackOffsets[slot.Anchor] = stacked + size.Y + gap;
        }
    }
}
```

5. **Guard visibility.** Show the bar when `config.Enabled`, a vehicle is tracked and not (`HideOnRails` and on rails); with no vehicle optionally draw a small status tag (`OverlayRenderer.cs`, `Draw`).

## Complete example

| File | Role |
|---|---|
| [Rendering/OverlayRenderer.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayRenderer.cs) | Visibility, intro phases, layout and draw per frame |
| [Rendering/PanelHost.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/PanelHost.cs) | Anchor and stacking layout |
| [Rendering/IOverlayPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/IOverlayPanel.cs) | `PanelContext` and the panel contract |
| [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L98-L140) | The per-frame entry that calls the renderer |

## Variations and gotchas

- **No hit testing.** Foreground shapes cannot be clicked or dragged; the mod offers each panel as a real ImGui window for that purpose.
- **Always on top, including over the game's own panels.** That is the point for a broadcast graphic and the cost for anything that should sit under dialogs; use the background list ([approach 2](draw-a-fullscreen-click-through-overlay-2.md)) for those.
- **One main viewport.** Everything is laid out on `GetMainViewport()`; detached ImGui viewports do not get the bar.
- **Name-coupled.** The menu-bar clearance silently falls back if the game renames its `Menu Bar` window; likewise the Layouts window lookup.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of draw-a-fullscreen-click-through-overlay](overview.md)
- [Approach 1: NoInputs/NoBackground fullscreen window](draw-a-fullscreen-click-through-overlay-1.md)
- [Approach 2: ImGuiHelper.GetOverlayDrawList](draw-a-fullscreen-click-through-overlay-2.md)
- [Draw HUD instruments with the draw list](../draw-hud-instruments-with-the-draw-list/overview.md)
- Mod feature: [Foreground draw-list broadcast overlay](../../../../mods/ksa-public-telemetry/features/foreground-drawlist-broadcast-overlay.md)

[^brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^ksa-imguihelper]: KSA.ImGuiHelper (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
