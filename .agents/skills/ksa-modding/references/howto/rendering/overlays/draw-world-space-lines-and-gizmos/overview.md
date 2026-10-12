---
type: Game Feature
title: Draw world-space lines and gizmos with the game's own renderers
description: Use the game's gizmo renderer for debug-style depth-tested world overlays that need no Vulkan code, and preview extra predicted orbits through the game's own line instances
category: rendering
group: overlays
topic: draw-world-space-lines-and-gizmos
demonstrated_by:
  - mods/ksarmory/features/gizmo-world-overlays
  - mods/ksagolf/features/frame-epoch-draw-anchor
  - mods/ksa-advancedflightcomputer/features/multi-pass-preview-rendering
lifecycle: [frame:draw-ui, render:scene-opaque, render:gizmos, frame:pre-render, render:orbit-lines]
game_types: [KSA.GizmosRenderer, KSA.Program, KSA.FlightPlan, KSA.OrbitalTransfers]
tags: [gizmos, lines, orbit-lines, debug-draw, world-space]
---

Two game renderers draw lines in the world for mods without any Vulkan code: `GizmosRenderer` (spheres and lines in camera-relative Ego, depth-tested inside the opaque scene scope) and the orbit-line renderer fed by `FlightPlan.AddLineInstances`. Both are batch-style: you submit instances each frame inside a window of the frame, and the game draws them.

## Engine background

- `GizmosRenderer.DrawSphere/DrawLine` [current/decomp/KSA/GizmosRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GizmosRenderer.cs#L181-L217), `ResetInstances` [current/decomp/KSA/GizmosRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GizmosRenderer.cs#L471) (called at [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2260) before `PrepareImGui`), `Render` [current/decomp/KSA/GizmosRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GizmosRenderer.cs#L338) (called at [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4842), inside the opaque offscreen scope; `render:scene-opaque`).
  The separate `GizmoPass.Run` (step 19, `render:gizmos`) is a different renderer used for secondary viewports.
- `FlightPlan.AddLineInstances` [current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L908); `OrbitLinePass.Run` (step 18, `render:orbit-lines`, `DrawUI` only); `TransferPlanner.OnPreRender` [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L1679).
- Submission window: after `ResetInstances` and before `Render`: the GUI hooks (`frame:draw-ui`) and prefixes in that range. See [the render sequence](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence) for the notes on `GizmosRenderer.ResetInstances`.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: spheres and lines from the StarMap GUI postfix](draw-world-space-lines-and-gizmos-1.md) | ksarmory | `gizmo-draw` with an Ego anchor | `frame:draw-ui`, `render:scene-opaque` | Zero Vulkan code; spheres and lines only, vanishes with the HUD |
| [2: gizmos next to a moving craft with a frame-epoch anchor](draw-world-space-lines-and-gizmos-2.md) | ksagolf | Two-instant anchor | `frame:draw-ui` | Exact placement; strict sampling rules |
| [3: FlightPlan.AddLineInstances per plan](draw-world-space-lines-and-gizmos-3.md) | ksa-advancedflightcomputer | `harmony-postfix` on `TransferPlanner.OnPreRender` | `frame:pre-render`, `render:orbit-lines` | Looks like stock orbits; needs cached points and caching of expensive previews |

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Choose a render frame and anchor effects to bodies, parts and terrain](../choose-a-render-frame-and-anchor-effects/overview.md), [Draw markers, brackets and measurement annotations on world objects](../draw-markers-and-annotations-on-world-objects/overview.md)
