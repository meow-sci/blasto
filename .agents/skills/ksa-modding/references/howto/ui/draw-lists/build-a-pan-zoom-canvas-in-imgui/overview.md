---
type: Game Feature
title: Build pannable, zoomable and 3D canvases inside an ImGui window
description: A clipped canvas with a full-size InvisibleButton for pan, zoom about the cursor, hover and click, and a 3D preview canvas with wireframe gizmos, ray picking and undo
category: ui
group: draw-lists
topic: build-a-pan-zoom-canvas-in-imgui
demonstrated_by:
  - mods/ksa-deltavmap/features/subway-map-canvas-rendering
  - mods/unscience/features/pebbles-workshop-editor
lifecycle: [frame:draw-ui, render:imgui]
game_types:
  - KSA.ConsoleWidgets
  - KSA.ConsoleStyle
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImDrawListPtr
  - Brutal.ImGuiApi.ImTextureRef
  - Brutal.ImGuiApi.ImInputString
  - KSA.Program
  - Brutal.ImGuiApi.ImDrawListExtensions
tags: [canvas, pan, zoom, hit-testing, wheel, picking, gizmo, undo, draw-list]
---
A canvas is a rectangle inside an ImGui window where you, not ImGui, decide what every pixel means. Two things make it work in KSA's ImGui: a full-size `InvisibleButton` that owns the mouse (so a drag pans the canvas instead of moving the window, and the wheel zooms the canvas instead of scrolling the window or zooming the game camera), and a draw list clipped to the rectangle. Two reference mods show the two ends of the problem. KSA-DeltaVMap draws a 2D diagram with zoom about the cursor and priority-ordered label culling. unscience's Pebbles workshop shows a 3D scene (an offscreen render) with CPU-projected wireframes, draggable gizmos, ray picking and undo. All of it runs in `frame:draw-ui`; the Pebbles preview image is sampled during `render:imgui`. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Engine background

| Piece | File | What it gives you |
|---|---|---|
| `ImGui.InvisibleButton(id, size, flags)` and `SetNextItemAllowOverlap()` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L6255-L6262)[^brutal-imguiapi-imgui], L7938 | The input surface; allow-overlap lets widgets submitted after it still take clicks |
| `ImGui.SetItemKeyOwner(ImGuiKey)` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8186)[^brutal-imguiapi-imgui] | Claims a key (here `MouseWheelY`) for the last item so other consumers do not see it |
| `ImGui.PushClipRect` / `ImDrawListExtensions.PushClipRect` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L7907)[^brutal-imguiapi-imgui], [current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L10)[^brutal-imguiapi-imdrawlistextensions] | Clip drawing to the canvas |
| `ImGui.GetIO()`, `ImGuiIOPtr.MouseWheel` (a writable `ref float`) | [current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs#L172)[^brutal-imguiapi-imguiioptr] | Raw input for the canvas; setting the wheel to 0 consumes it |
| `ImDrawListExtensions.AddImage` | [current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L611)[^brutal-imguiapi-imdrawlistextensions] | Show an offscreen render (an `ImTextureRef`) inside the canvas |
| `Camera.EclToScreen`, `EgoToScreen` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L340-L358)[^ksa-camera] | The game's own world-to-screen projection, if you overlay the real camera instead of an offscreen one |

**Why wheel ownership matters.** ImGui scrolls the focused window with the wheel, and KSA's flight camera reads the same wheel when ImGui does not want it. A canvas therefore sets `SetItemKeyOwner(ImGuiKey.MouseWheelY)` while hovered and consumes the delta (DeltaVMap also zeroes `io.MouseWheel`); otherwise zooming the map also zooms the game camera or scrolls the window. See [Share the keyboard and mouse between ImGui, mod tools and the game](../../../../howto/ui/input/coordinate-imgui-focus-with-game-input/overview.md) for the wider focus rules.

## Approaches

| # | Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [InvisibleButton with allow-overlap, wheel key ownership, PushClipRect and priority label culling](build-a-pan-zoom-canvas-in-imgui-1.md) | KSA-DeltaVMap | 2D zoom about the cursor, hit testing by screen distance, greedy label culling | `frame:draw-ui` | No inverse transform needed; labels fixed in screen size need culling |
| 2 | [3D orbit canvas: offscreen image, draw-list wireframes and gizmos, ray picking and JSON-snapshot undo](build-a-pan-zoom-canvas-in-imgui-2.md) | unscience (Pebbles) | One view-projection for the image and the overlay, `M22` flip, ray-box picking, bounded undo | `frame:draw-ui`, `render:imgui` | Offscreen render trails by a frame; more code |

## Choosing

- A diagram or map with hover and click: approach 1; the single-class transform and hit tests are enough.
- Anything the player edits in 3D: approach 2, and take its undo section even for 2D editors.
- Label-heavy canvases: take approach 1's culling pass regardless of canvas type.

## See also

- [Draw per-frame text efficiently: monospace grids and allocation-free labels](../../../../howto/ui/draw-lists/draw-a-monospace-text-grid/overview.md)
- [Lay out a tree or graph diagram and keep it usable at scale](../../../../howto/ui/draw-lists/lay-out-a-tree-or-graph-diagram/overview.md)
- [Turn the mouse cursor into a world ray and pick points on a construction plane](../../../../howto/ui/input/cursor-to-world-ray/overview.md)
- [Place pieces on a grid with a snapping ghost, top-down camera and world gizmos](../../../../howto/ui/input/place-pieces-on-a-grid-with-a-ghost/overview.md)
- [Share the keyboard and mouse between ImGui, mod tools and the game](../../../../howto/ui/input/coordinate-imgui-focus-with-game-input/overview.md)
- [Draw per-frame text efficiently: monospace grids and allocation-free labels](../draw-a-monospace-text-grid/overview.md) - related topic
- [Lay out a tree or graph diagram and keep it usable at scale](../lay-out-a-tree-or-graph-diagram/overview.md) - related topic
- [Turn the mouse cursor into a world ray and pick points on a construction plane](../../input/cursor-to-world-ray/overview.md) - related topic
- [Place pieces on a grid with a snapping ghost, top-down camera and world gizmos](../../input/place-pieces-on-a-grid-with-a-ghost/overview.md) - related topic

[^brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imdrawlistextensions]: Brutal.ImGuiApi.ImDrawListExtensions (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imguiioptr]: Brutal.ImGuiApi.ImGuiIOPtr (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
