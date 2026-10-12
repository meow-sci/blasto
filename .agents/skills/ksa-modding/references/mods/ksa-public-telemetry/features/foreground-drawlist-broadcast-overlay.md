---
type: Mod Feature
title: Broadcast overlay on the ImGui foreground draw list
description: A full-screen webcast-style bottom bar drawn straight into ImGui's foreground draw list, with anchored panels, a gradient backdrop band and sloped shelf plates, instead of an ImGui window
mod: ksa-public-telemetry
category: ui
techniques: [imgui-drawlist, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, harmony-postfix KSA.Program.DrawFps]
lifecycle: [frame:draw-ui]
game_types:
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImDrawListPtr
  - Brutal.ImGuiApi.Internal.ImGuiWindowPtr
  - KSA.Program
tags: [imgui, drawlist, overlay, hud, layout, broadcast]
---

The headline feature: a SpaceX-webcast-style bar along the bottom of the screen with round speed and
altitude gauges on the left, the T+ clock and a mission timeline in the centre, and the g-force gauge
plus the engine diagram on the right, sitting on a dark gradient band with two sloped "shelf" plates
behind the gauge groups.[^kpt-readme] None of it is an ImGui window: every shape and glyph is pushed
into `ImGui.GetForegroundDrawList()` once per frame, positioned from the main viewport's work area, so
the bar has no chrome, takes no input and draws above every ImGui window.

## How it works

1. **One renderer, many panels.** `OverlayRenderer` owns a `PanelHost` and registers six panels with
   an anchor each: primary readouts bottom-left, event notifications top-left, mission clock and
   timeline bottom-centre, engine diagram and secondary readouts bottom-right
   ([Rendering/OverlayRenderer.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayRenderer.cs#L34-L57)). Every panel implements the small `IOverlayPanel`
   contract (`IsVisible`, `Measure`, `Draw(in PanelContext, origin, size)`, `Reset`) and receives a
   `PanelContext` struct carrying the draw list, the telemetry snapshot, the config, `dt`, the intro
   phases and the viewport rectangle ([Rendering/IOverlayPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/IOverlayPanel.cs#L16-L69)).[^kpt-ioverlaypanel]
2. **Anchored stacking layout.** `PanelHost.DrawAll` measures each visible panel (unscaled units times
   `config.Scale`), resolves an origin from the anchor plus a running per-anchor row or column offset,
   and calls `Draw`. Bottom-right panels stack leftwards from the right edge; bottom-left ones stack
   rightwards ([Rendering/PanelHost.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/PanelHost.cs#L63-L138)).
3. **Viewport and menu bar.** The renderer reads `ImGui.GetMainViewport()` `WorkPos`/`WorkSize` and
   pushes the top inset below the game's menu bar by looking that window up by name with
   `ImGui.Internal.FindWindowByName("Menu Bar")` and checking `WasActive`
   ([Rendering/OverlayRenderer.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayRenderer.cs#L131-L177)). The game opens that window with exactly that name in
   `Program.DrawMenuBar`, a separate window that hosts its menus with `BeginMenuBar`
   ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3481-L3487)).[^ksa-program] [^imgui-imgui] The bar is only submitted when the main viewport
   is hovered and it is not auto-hidden ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3186-L3189),
   [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3468-L3480)); otherwise `WasActive` is false and only the fixed clearance applies.
4. **Backdrop band.** `Gfx.BottomFade` stacks 20 `AddRectFilledMultiColor` strips with a quadratic
   ease-out of alpha toward the bottom edge, which reads smoother than one two-colour rect
   ([Rendering/Gfx.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Gfx.cs#L23-L63)).
5. **Shelf plates with a vertex-colour gradient.** `Gfx.ShelfBox` builds a convex polygon (flat top,
   a filleted quadratic shoulder, then a straight diagonal down to the screen bottom), fills it with
   `AddConvexPolyFilled`, then walks the vertices it just appended (`VtxBuffer.Count` before and after)
   and rescales each vertex's alpha by its height, so the plate fades toward the floor. ImGui has no
   gradient polygon fill; editing the freshly emitted `ImDrawVert`s is the trick
   ([Rendering/Gfx.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Gfx.cs#L67-L148)). The plate's flat width comes from measuring the anchor group
   (`PanelHost.MeasureGroupWidth`), so it grows when the player adds readout slots
   ([Rendering/OverlayRenderer.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayRenderer.cs#L179-L234)).
6. **Gauges.** Readouts are capsules: a filled `GaugePlate` circle, a 270 degree arc track and fill
   drawn with `PathArcTo` + `PathStroke` (`Gfx.Arc` rotates the angle origin to 12 o'clock), two
   hairline brackets, and label/value/unit text that shrinks to fit the ring
   ([Rendering/ReadoutPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/ReadoutPanel.cs#L62-L138), [Rendering/Gfx.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Gfx.cs#L200-L303),
   [Rendering/Gfx.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Gfx.cs#L421-L450)). Each readout value is exponentially smoothed with
   `alpha = 1 - exp(-dt / SmoothingSeconds)` ([Rendering/ReadoutPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/ReadoutPanel.cs#L140-L152)).
7. **Visibility.** The bar shows when `config.Enabled`, a vehicle is tracked, and not (`HideOnRails`
   and on rails). With no vehicle it optionally draws a small "TELEMETRY - NO VEHICLE" tag
   ([Rendering/OverlayRenderer.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayRenderer.cs#L59-L130), [Rendering/OverlayRenderer.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayRenderer.cs#L269-L290)).

## Lifecycle and timing

- **`frame:draw-ui` via `[StarMapAfterGui]`.** `TelemetryOverlayMod.OnAfterGui` calls `DrawEverything`
  ([TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L85-L147)). StarMap fires that attribute from a Harmony postfix on
  `Program.OnDrawUiViewports` ([StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L26-L36)), which in
  `Program.OnFrame` runs after `PrepareImGui` (`ImGui.NewFrame`) and before `ImGui.Render`, inside
  `if (DrawUI)` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2261-L2286)).[^starmap-programpatcher]
- **Before the menu bar.** `[StarMapAfterGui]` runs before `OnDrawUiThreadSafe`, which draws the menu
  bar, the gauge canvases and `Universe.OnDrawUi`, so `MenuBarInset` reads the bar window as ImGui left
  it after the previous frame: `WasActive` is copied from `Active` in `ImGui.NewFrame`, and the bar's
  `Pos`/`Size` are last frame's. The bar is pinned to the viewport origin, so that lag is harmless.
- **When the HUD is hidden (F2)** `OnDrawUiViewports` is skipped, so the mod also draws from a postfix on
  `Program.DrawFps`, which `OnFrame` calls outside the `DrawUI` guard just before `OnDrawUiConsole` and
  `ImGui.Render` (see [Keep drawing while the game UI is hidden](draw-while-ui-hidden-drawfps-postfix.md)).
  With the UI hidden the menu bar is not drawn, so the inset falls back to the fixed clearance.
- **Data is one frame old.** The draw reads the `TelemetrySnapshot` filled by `[StarMapAfterOnFrame]` at
  the end of the previous frame (see [Live flight telemetry sampling](live-flight-telemetry-sampling.md)).
  Apart from ImGui windows and the viewport, the draw only touches game state through the flight-HUD
  toggle (`Program.ControlledVehicle`, `GaugeCanvas` flags, see
  [Hide and restore the stock flight HUD](hide-and-restore-stock-flight-hud.md)), none of which the
  physics step running on the workers writes.
- **Why the foreground list.** It is per-viewport, needs no `Begin`/`End`, takes no input focus and
  is composited after all windows, which is what a broadcast graphic wants. The game's own FPS counter
  also draws without a window ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3248-L3255)), but
  `ImGuiHelper.GetMainOverlayDrawList()` returns the main viewport's background draw list
  ([current/decomp/KSA/ImGuiHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiHelper.cs#L43-L46)), which ImGui renders beneath every window, so
  game windows cover the FPS text while they cannot cover this bar.[^ksa-imguihelper]

## Key code

| Path | Role |
|---|---|
| [Rendering/OverlayRenderer.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayRenderer.cs#L59-L157) | Visibility, intro phases, layout and draw per frame |
| [Rendering/PanelHost.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/PanelHost.cs#L63-L138) | Anchor and stacking layout |
| [Rendering/IOverlayPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/IOverlayPanel.cs#L16-L69) | `PanelContext` and the `IOverlayPanel` contract |
| [Rendering/Gfx.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Gfx.cs#L67-L148) | Shelf polygon with per-vertex alpha fade |
| [Rendering/Gfx.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Gfx.cs#L421-L450) | Arc helper on `PathArcTo`/`PathStroke` |
| [Rendering/ReadoutPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/ReadoutPanel.cs#L62-L152) | Readout capsules and value smoothing |

## Snippets

```csharp
// Rendering/OverlayRenderer.cs L131-L148, L158-L177
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
// ...
    private static bool LayoutWindowOpen()
    {
        ImGuiWindowPtr window = ImGui.Internal.FindWindowByName("LAYOUTS###KSA.LayoutSaves+LayoutSavesWindow_"u8);
        return !window.IsNull() && window.WasActive;
    }

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

```csharp
// Rendering/Gfx.cs L133-L147
        int vertexStart = drawList.VtxBuffer.Count;
        drawList.AddConvexPolyFilled(points[..n], OverlayStyle.WithOpacity(OverlayStyle.ShelfBox, opacity));
        int vertexEnd = drawList.VtxBuffer.Count;

        float floorFraction = Math.Clamp(Tuning.ShelfFloorFraction, 0f, 1f);
        Span<ImDrawVert> vertices = drawList.VtxBuffer.Span;

        for (int i = vertexStart; i < vertexEnd; i++)
        {
            ref ImDrawVert vertex = ref vertices[i];
            float t = Math.Clamp((vertex.pos.Y - top) / height, 0f, 1f);
            float fade = 1f - t * (1f - floorFraction);
            uint alpha = (uint)(((vertex.col >> 24) & 0xFFu) * fade);
            vertex.col = (vertex.col & 0x00FFFFFFu) | (alpha << 24);
        }
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `ImGui.GetForegroundDrawList`, `ImGui.GetMainViewport` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8038) | Target draw list and screen rectangle |
| `ImGui.Internal.FindWindowByName`, `ImGuiWindowPtr.WasActive` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L45-L50), [current/decomp/Brutal.ImGuiApi.Internal/ImGuiWindowPtr.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi.Internal/ImGuiWindowPtr.cs#L104) | Find the game's "Menu Bar" window and whether it drew last frame |
| `Program.OnFrame` UI block, "Menu Bar" window | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2261-L2286), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3486) | Where the draw runs; the name the inset lookup depends on |

## Pitfalls and version notes

- **Name-coupled.** The menu bar clearance silently falls back to a fixed margin if the game renames
  its "Menu Bar" window.
- **No hit testing.** Foreground draw-list shapes cannot be clicked or dragged; the mod offers each
  panel as a real ImGui window for that (see [Every element as its own ImGui window](panels-as-static-imgui-windows.md)).
- **Vertex editing is order-sensitive.** The shelf gradient relies on `AddConvexPolyFilled` appending
  vertices at the end of `VtxBuffer`; anything else drawn in between would be recoloured too.
- **One main viewport.** Everything is laid out on `GetMainViewport()`; detached ImGui viewports do
  not get the bar.

## See also

- [Staged intro and outro animation](intro-outro-animation.md)
- [Montserrat fonts in the ImGui atlas](montserrat-fonts-in-imgui-atlas.md)
- [Engine diagram from real nozzle positions](engine-diagram-from-nozzle-positions.md)
- [Mission timeline with planned burns](mission-timeline-with-planned-burns.md)
- [Hide and restore the stock flight HUD](hide-and-restore-stock-flight-hud.md)
- Frame order reference: [Game loop and lifecycle](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Overlays without a window: ImGui.GetForegroundDrawList anchored to WorkPos and WorkSize](../../../howto/ui/overlays/draw-a-fullscreen-click-through-overlay/draw-a-fullscreen-click-through-overlay-3.md) - how-to article demonstrating this feature
- [HUD instruments: PathArcTo plus PathStroke arcs and vertex-alpha gradients](../../../howto/ui/overlays/draw-hud-instruments-with-the-draw-list/draw-hud-instruments-with-the-draw-list-1.md) - how-to article demonstrating this feature

[^kpt-readme]: README.md
[^kpt-ioverlaypanel]: Rendering/IOverlayPanel.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^imgui-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^starmap-programpatcher]: StarMap StarMap.Core/Patches/ProgramPatcher.cs
[^ksa-imguihelper]: KSA.ImGuiHelper (decompiled, build 2026.10.10.5554)
