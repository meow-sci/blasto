---
type: Game Feature
title: Draw full-screen click-through overlays and choose the right ImGui draw list
description: How mods paint over the game view with ImGui - input-less windows, the background and foreground draw lists, click-through lock mode and per-viewport lists - and what each choice means for z-order, input and secondary viewports
category: ui
group: overlays
topic: draw-a-fullscreen-click-through-overlay
demonstrated_by:
  - mods/ksa-aircrafthud/features/fullscreen-drawlist-hud
  - mods/compendium/features/body-pointer-overlay
  - mods/ksarmory/features/world-anchored-screen-markers
  - mods/ksa-advancedflightcomputer/features/guidance-panel-and-overlays
  - mods/ohmystars/features/sky-overlay-lines-and-labels
  - mods/ksa-measuretools/features/overlay-drawlist-rendering
  - mods/ksa-deltavmap/features/map-mode-transfer-overlay
  - mods/ksa-public-telemetry/features/foreground-drawlist-broadcast-overlay
  - mods/purrtty/features/click-through-lock-mode-hot-zone
  - mods/ksarmory/features/gunner-sight-overlay
lifecycle: [frame:draw-ui, render:imgui, frame:viewports, frame:after-on-frame, frame:input]
game_types:
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImDrawListPtr
  - Brutal.ImGuiApi.ImGuiViewportPtr
  - KSA.ImGuiHelper
  - KSA.Program
  - KSA.Camera
  - KSA.GameViewport
  - KSA.IGameViewport
  - KSA.IViewport
  - KSA.ViewportRegistry
  - RenderCore.ReverseDepthBufferUtils
  - Brutal.ImGuiApi.ImGuiWindowFlags
tags: [overlay, hud, imgui, draw-list, click-through, window-flags, viewport]
---
KSA renders its whole UI through ImGui, and ImGui's draw data is composited over the 3D scene in the final pass (`render:imgui`). So "draw something over the world" means "put geometry in an ImGui draw list", and
the real decisions are which list, which window (if any), and what input the overlay should have. Ten mods paint over the game view; they fall into five patterns.

## Engine background

| Piece | File | Role |
|---|---|---|
| `ImGui.GetBackgroundDrawList(viewport)` / `GetForegroundDrawList(viewport)` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8033-L8042) | Per-platform-viewport lists: background under every window, foreground over them; no `Begin`/`End`, no input |
| `ImGuiHelper.GetMainOverlayDrawList()` / `GetOverlayDrawList(viewport)` | [current/decomp/KSA/ImGuiHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiHelper.cs#L43-L51) | The main viewport's background list; the current window list for other viewports |
| `ImGuiWindowFlags` | [current/decomp/Brutal.ImGuiApi/ImGuiWindowFlags.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiWindowFlags.cs) | `NoInputs`, `NoMouseInputs`, `NoNav`, `NoBackground`, `NoBringToFrontOnFocus` |
| `Program.OnFrame` UI section | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2255-L2290) | `PrepareImGui`, `OnFrameEditor`, `OnFrameViewports`, the `if (DrawUI)` GUI hooks, `DrawFps`, `OnDrawUiConsole`, `ImGui.Render` |
| `GameViewport.DrawImGui` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L196-L262) | Draws non-main game viewports as ImGui windows with their world UI inside; records `ImGuiId` and `Position` |
| `Camera.EclToScreen` / `EgoToScreen` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L333-L376) | Projection with NaN behind the camera |

**Z-order.** A window draw list is z-ordered like the window (it can land over or under panels); the background list is under every window; the foreground list is over every window. **Input.** Only windows take input; draw-list-only
overlays never do. **Viewports.** Only the main viewport has a persistent background list that is under everything; other game viewports draw their world UI inside their own ImGui window, and a torn-off window has a platform viewport of
its own whose list is the right one to draw on.

**Frame order.** Overlays drawn from the StarMap GUI hooks run in `frame:draw-ui`, inside `if (DrawUI)`, after cameras are current, and disappear with the HUD (F2). Overlays that must survive F2 hang on a call that still runs
([keep UI running when the HUD is hidden](../draw-ui-while-the-hud-is-hidden/overview.md)). See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| # | Article | Mod | Technique | Draw list | Input | Trade-off |
|---|---|---|---|---|---|---|
| 1 | [NoInputs/NoBackground fullscreen window](draw-a-fullscreen-click-through-overlay-1.md) | KSA_AircraftHUD, OhMyStars, Compendium, KSArmory, AFC | Window sized to the main viewport, window list | Window | None | z-ordered like a window; clickable parts need separate windows |
| 2 | [ImGuiHelper.GetOverlayDrawList](draw-a-fullscreen-click-through-overlay-2.md) | KSA-MeasureTools, KSA-DeltaVMap | No window; background list | Background | None | Under every window; main viewport only |
| 3 | [ImGui.GetForegroundDrawList anchored to WorkPos/WorkSize](draw-a-fullscreen-click-through-overlay-3.md) | KSA-Public-Telemetry | No window; foreground list with an anchored panel layout | Foreground | None | Always on top; cannot be clicked |
| 4 | [Click-through lock mode with a hot zone](draw-a-fullscreen-click-through-overlay-4.md) | purrTTY | Real window toggling `NoMouseInputs` with a refocus hot zone | Window (+ foreground fills) | Only when focused | Interactive and overlay in one; needs focus bookkeeping |
| 5 | [Per-surface draw list and per-viewport ego transforms](draw-a-fullscreen-click-through-overlay-5.md) | KSArmory | Resolve list and camera per frame; platform-viewport foreground list | Mixed | None | Works on camera windows and second monitors; most code |

## Choosing

- A HUD or markers over the main view, simple: approach 1 (window) or 2 (background list, under panels).
- Something that must be above game windows (broadcast graphics): approach 3.
- A terminal or log that should be both: approach 4.
- Anything that appears on a camera window or another monitor: approach 5.

## See also

- [Keep mod UI and per-frame work running when the HUD is hidden](../draw-ui-while-the-hud-is-hidden/overview.md)
- [Draw HUD instruments with the draw list](../draw-hud-instruments-with-the-draw-list/overview.md)
- [Turn the mouse cursor into a world ray](../../input/cursor-to-world-ray/overview.md)
- [Build a mod window on the game's ImGuiWindow base class](../../windows/subclass-the-stock-imguiwindow/overview.md)
- [Project world and sky positions to screen pixels and point at off-screen targets](../../../rendering/overlays/project-world-positions-to-screen/overview.md)
- [Lease a secondary viewport for a mod camera and show it in a window](../../../camera/secondary-viewports/borrow-a-secondary-viewport/overview.md)
- [Draw markers, brackets and measurement annotations on world objects](../../../rendering/overlays/draw-markers-and-annotations-on-world-objects/overview.md)
- [Defining articulated subparts and animating or hiding them from code](../../../vehicles-and-parts/parts/define-and-animate-subparts/overview.md)

