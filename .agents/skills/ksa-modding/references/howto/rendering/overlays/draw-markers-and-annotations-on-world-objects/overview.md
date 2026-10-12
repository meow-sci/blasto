---
type: Game Feature
title: Draw markers, brackets and measurement annotations on world objects
description: Put a bracket on a craft, a lock cue on a contact, a measurement line with a plated label on a hull, a transfer-window gizmo in the map view or a pointer to a selected body using projected points on an ImGui draw list rather than Vulkan
category: rendering
group: overlays
topic: draw-markers-and-annotations-on-world-objects
demonstrated_by:
  - mods/ksarmory/features/world-anchored-screen-markers
  - mods/ksa-measuretools/features/overlay-drawlist-rendering
  - mods/ksa-measuretools/features/circle-and-face-angle-tools
  - mods/ksa-deltavmap/features/map-mode-transfer-overlay
  - mods/ksa-advancedflightcomputer/features/multi-pass-preview-rendering
  - mods/compendium/features/body-pointer-overlay
  - mods/ohmystars/features/star-pointer
lifecycle: [frame:draw-ui]
game_types: [KSA.Camera, KSA.IViewport, KSA.ImGuiHelper, KSA.Vehicle, KSA.Program]
tags: [markers, annotations, overlay, imgui-drawlist, projection]
---

Annotations that live in screen space but belong to world objects need no Vulkan: project the object's position every frame and draw primitives on an ImGui draw list. The game's own overlays (body labels, the Transfer Planner alignment marker, the FPS counter) do the same. The choices are which draw list, which window flags, which glyph, and how to
handle off-screen and occluded targets.

## Engine background

- Projection: `Camera.EclToScreen` / `EgoToScreen` / `EgoToClip` / `IsPointWithinFov` [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L283-L355); add `IViewport.Position`.
- Draw lists: `ImGuiHelper.GetOverlayDrawList(viewport)` (main viewport: `GetBackgroundDrawList`; otherwise the current window's list) [current/decomp/KSA/ImGuiHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiHelper.cs#L43-L51); the stock reference is the Transfer Planner alignment marker and `Universe.DrawGalacticGrid` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1804-L1870).
- Hook: `[StarMapAfterGui]` (`frame:draw-ui`), skipped when `Program.DrawUI` is false. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: constant-pixel-size brackets, cues and bars](draw-markers-and-annotations-on-world-objects-1.md) | ksarmory | Full-screen `NoInputs` window, edge arrows, occlusion marks | `frame:draw-ui` | Readable at any distance; labels need their own windows |
| [2: lines, curves, rings and plated labels](draw-markers-and-annotations-on-world-objects-2.md) | ksa-measuretools | Background list, NaN-broken polylines, throttled picks | `frame:draw-ui`, `frame:input` | Sits under panels; shared list order |
| [3: map-mode markers and gizmos](draw-markers-and-annotations-on-world-objects-3.md) | ksa-deltavmap | Orbit positions lifted to Ecl, probe-projected gizmo | `frame:draw-ui`, `frame:pre-render` | Future positions; only valid in Map mode |
| [4: window-rect pointer to a body](draw-markers-and-annotations-on-world-objects-4.md) | compendium | Previous-frame rectangle, clip-space edge clamp | `frame:draw-ui` | Finds a body anywhere; window draw list, not foreground |
| [5: same technique for a catalog star](draw-markers-and-annotations-on-world-objects-5.md) | ohmystars | Star direction times 19.5, behind-camera flip | `frame:draw-ui` | Pointer to sky objects; hint only when behind |

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Draw full-screen click-through overlays and choose the right ImGui draw list](../../../ui/overlays/draw-a-fullscreen-click-through-overlay/overview.md), [Project world and sky positions to screen pixels and point at off-screen targets](../project-world-positions-to-screen/overview.md), [Draw world-space lines and gizmos with the game's own renderers](../draw-world-space-lines-and-gizmos/overview.md)
- [Keep mod UI and per-frame work running when the HUD is hidden (F2)](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md)
