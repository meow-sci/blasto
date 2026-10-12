---
type: Game Feature
title: Turn the mouse cursor into a world ray and pick points on a construction plane
description: How ImGui's cursor becomes an ecliptic ray through the camera of the viewport it is over, the camera ego frame the ray lives in, and how five mods verify and use it
category: ui
group: input
topic: cursor-to-world-ray
demonstrated_by:
  - mods/ksarmory/features/cursor-ray-world-click-tools
  - mods/ksagolf/features/cursor-picking-and-terrain-ray
  - mods/ksacars/features/craft-mover-mouse-pick-place
  - mods/purrtty/features/ray-pick-focus-for-world-quads
  - mods/ksa-measuretools/features/cursor-unprojection-and-construction-plane
lifecycle: [frame:draw-ui, frame:input, frame:viewports, frame:render, frame:after-on-frame]
game_types:
  - KSA.Camera
  - KSA.Cursor
  - KSA.Ray
  - KSA.ViewportRegistry
  - KSA.IGameViewport
  - KSA.IViewport
  - KSA.Vehicle
  - KSA.Transform3D
  - KSA.GameViewport
  - KSA.MathEx
  - KSA.Astronomical
  - KSA.VehicleEditingSpace
  - KSA.Program
  - Brutal.ImGuiApi.ImGuiIOPtr
tags: [cursor, raycast, camera, ego, viewport, picking, construction-plane, imgui]
---
A mod that reacts to a click in the 3D view needs a ray: where the camera is and which way the pixel under the
cursor points. KSA has no "what is under the mouse" service for mods. It has a camera per viewport that can
unproject a framebuffer pixel, a helper that subtracts the viewport origin, and an ImGui cursor in screen
coordinates that spans every window. Five mods wire those together slightly differently; this topic compares
them and explains the frame the ray lives in. Picking what the ray hits (vessels, parts, terrain, quads) is the
neighbouring topic, [pick objects under the cursor](../pick-objects-under-the-cursor/overview.md).

## Engine background

| Type | File | Role |
|---|---|---|
| `Camera` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs) | `ScreenToEgoRay`, `FramebufferSize`, `EgoToEcl`, `EclToEgo`, `EclToScreen`, `GetPositionEgo`, `GetForwardEcl`, `GetFieldOfView` |
| `Cursor` | [current/decomp/KSA/Cursor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Cursor.cs) | `DesktopPosition`, `GetPosition(viewport)`, `GetEgoRay(viewport)` |
| `Ray` | [current/decomp/KSA/Ray.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Ray.cs) | Origin and direction; `RaycastMollerTrumbore`, `RaycastWatertight`, box and sphere casts |
| `ViewportRegistry`, `IViewport` | [current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs), [current/decomp/KSA/IViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IViewport.cs) | `GameViews`, `MainViewport`; `Position`, `Width`, `Height`, `Visible`, `GetCamera()` |
| `GameViewport` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs) | Map camera in `Map` mode else base camera; `SetSize` resizes both cameras |
| `MathEx` | [current/decomp/KSA/MathEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MathEx.cs) | `RayPlaneIntersection` |

**Frame order** (see [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)).
`Program.PrepareImGui` starts the ImGui frame and copies the mouse into `Cursor.DesktopPosition`, before the
`DrawUI` gate, so cursor and click state are valid every frame even with the HUD hidden.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2133-L2139)[^ksa-program]
Cameras are recomputed in `OnFrameViewports` (`frame:viewports`), before `StarMapAfterGui` (`frame:draw-ui`), so a
ray cast from the GUI hook matches the frame about to render. Click callbacks (`Program.OnMouseButton`,
`frame:input`) run earlier and see last frame's matrices.

**The ego frame.** Per viewport camera there is one ego frame: origin at the camera, axes equal to the ecliptic axes.
`EgoToEcl` is `posEgo + PositionEcl`, a pure translation, so an ego direction is an ecliptic direction and only a ray
origin needs lifting. An orthographic camera (the vehicle editor's toggle) gives every pixel its own near-plane origin
and one shared direction, so never assume the origin is the camera. Ego positions sampled from one camera are not
valid for another. [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L222-L233)[^ksa-camera]

**Pixel spaces.** ImGui's cursor is in screen coordinates across every OS window; a camera unprojects in its own
framebuffer. The conversion is: subtract `viewport.Position`, reject outside `[0, width) x [0, height)`, scale by
`framebuffer / viewport` size (identity in this build because `GameViewport.SetSize` resizes both cameras with the
viewport). KSACars' camera notes make the same point: per-viewport picking must use `viewport.GetCamera().ScreenToEgoRay`
directly.[^ksacars-ksa-cameras]

**Position lag.** `MainViewport.Position` is assigned from the ImGui main viewport at the start of
`OnDrawUiThreadSafe` (`MainViewport.SetPosition`), which runs after `StarMapAfterGui`, and it starts at zero.
A ray built in a `StarMapAfterGui` hook therefore subtracts the main viewport's position from the previous
frame, while a secondary viewport's `Position` is set earlier, inside its own `GameViewport.DrawImGui`. The
lag only shows while the OS window is being moved or on the first frame.

## Approaches

| # | Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [ScreenToEgoRay with viewport offset and framebuffer scale](cursor-to-world-ray-1.md) | KSArmory | Walk `GameViews`, convert to framebuffer pixels, unproject; click gate on `WantCaptureMouse`; called from the GUI hook and from the frame step | `frame:draw-ui`, `frame:after-on-frame` | Works with the HUD hidden; the game still sees the click |
| 2 | [Viewport lookup, EgoToEcl and a projected round trip](cursor-to-world-ray-2.md) | KSAGolf | Same ray plus `DescribeCursorRay`: project the hit back and log the pixel error | `frame:draw-ui` | Cheap proof the conventions are right; first-match viewport |
| 3 | [Framebuffer pixel conversion before ScreenToEgoRay](cursor-to-world-ray-3.md) | KSACars | Offset then scale, headless-tested; panel drawn before the click handler | `frame:draw-ui` | Pure `Sim` code is testable; scale is insurance today |
| 4 | [Cursor.GetEgoRay](cursor-to-world-ray-4.md) | purrTTY | Engine helper plus Moller-Trumbore on quads built in ego space | `frame:draw-ui` | Least code; you pick the viewport, and no framebuffer scale |
| 5 | [Ego frame, EgoToEcl and a construction plane](cursor-to-world-ray-5.md) | KSA-MeasureTools | Lift the ray origin, intersect with a camera-facing or ecliptic plane, store relative to a body | `frame:input`, `frame:draw-ui` | Exact placement with no geometry under the cursor; handles orthographic |

## Choosing

- Need any ray in the main view: approach 4 is one line; approach 1 or 3 when the cursor may be over a secondary or
  detached viewport.
- Debugging an "off by a constant" or "off by a growing amount" aim: approach 2's round trip.
- Placing something where nothing exists to hit: approach 5.

## See also

- [Pick the vessel, part, world quad or terrain point under the cursor](../pick-objects-under-the-cursor/overview.md)
- [Place pieces on a grid with a snapping ghost](../place-pieces-on-a-grid-with-a-ghost/overview.md)
- [Share the keyboard and mouse between ImGui, mod tools and the game](../coordinate-imgui-focus-with-game-input/overview.md)
- [Keep mod UI running when the HUD is hidden](../../overlays/draw-ui-while-the-hud-is-hidden/overview.md)
- [Draw full-screen click-through overlays](../../overlays/draw-a-fullscreen-click-through-overlay/overview.md)
- [Project world and sky positions to screen pixels and point at off-screen targets](../../../rendering/overlays/project-world-positions-to-screen/overview.md)
- [Understand KSA coordinate frames and epochs (ECL, CCE, CCI, CCF, ego, body)](../../../flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md)
- [Choose a render frame and anchor effects to bodies, parts and terrain](../../../rendering/overlays/choose-a-render-frame-and-anchor-effects/overview.md)
- [Draw lines and labels on the sky](../../../rendering/overlays/draw-lines-on-the-sky/overview.md)
- [Aim a secondary viewport camera, rebuild its matrices and give it its nearby body](../../../camera/secondary-viewports/aim-a-secondary-viewport-camera/overview.md)
- [Draw a textured quad attached to a part or the camera](../../../rendering/textures-and-decals/draw-a-textured-quad-in-the-world/overview.md)

[^ksacars-ksa-cameras]: KSACars: docs/KSA-CAMERAS.md
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
