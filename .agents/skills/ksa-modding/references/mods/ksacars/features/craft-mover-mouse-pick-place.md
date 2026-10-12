---
type: Mod Feature
title: Craft mover with mouse picking and ground placement
description: Pick a craft with one click by projecting every vehicle to the screen, find the ground under the cursor by walking a camera ray against the height field, draw gizmo rings anchored to the craft's rendered position, and set the craft down once with Vehicle.TeleportToLocation
mod: ksacars
category: ui
techniques: [imgui-window, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:input]
game_types:
  - KSA.Vehicle
  - KSA.Camera
  - KSA.Program
  - KSA.ViewportRegistry
  - KSA.IGameViewport
  - KSA.Celestial
  - KSA.GizmosRenderer
  - KSA.InputEvents
tags: [ui, mouse, picking, raycast, terrain, gizmos, teleport, ego-frame]
---

The car panel's **Move craft with the mouse** checkbox turns on a small tool: hover a craft and a
ring marks it, click to pick it up, then click the ground (on any body in view) and the craft is set
down there, upright and resting on the terrain.[^ksacars-craftmover] CLAUDE.md notes it was carried
over from the author's KSArmory mod with its helpers.[^ksacars-claude] It is a compact example of
four reusable techniques: screen-space picking of vehicles, a cursor ray in the right pixel space,
a ray-against-heightfield walk, and gizmos anchored to where the engine actually draws a craft.

## How it works

1. **Hover.** Every frame (while enabled and ImGui does not want the mouse), every vehicle is
   projected to the main viewport (`TryProjectAhead`) and given a clickable radius equal to its
   apparent size, clamped to 14-140 px (`TryApparentRadiusPixels` from `MeanRadius`).
   `Picking.NearestWithin` returns the nearest craft whose radius contains the cursor.[^ksacars-picking]
2. **Cursor ray.** `TryCursorRayEcl` walks `ViewportRegistry.GameViews`, converts the ImGui mouse
   position from window pixels to the camera's **framebuffer** pixels (`CursorAim.TryToFramebuffer`,
   because `Camera.ScreenToEgoRay` divides by `FramebufferSize`, which differs under render or
   display scaling), and takes `ScreenToEgoRay(local).Direction` with origin `EgoToEcl(0)`.[^ksacars-cursoraim][^ksa-camera]
3. **Ground under the cursor.** For every `Celestial`, `TerrainRay.TryFirstHit` walks the ray from the
   eye, but only the stretch below the body's highest terrain (`TerrainMask.TryBandBelow`), in steps
   as long as the ray's height above the ground (assuming slopes no steeper than 1:1), between 1 m
   and 1000 m, at most 512 samples, to 0.5 m. The nearest body hit wins, so pointing at a moon on
   the horizon means the moon. This replaces "intersect the mean sphere then look up the height",
   which lands behind a hill seen side-on.[^ksacars-terrainray][^ksacars-terrainmask]
4. **Target.** If the cursor is over a craft, its own footing is used (a ray through a vehicle meets
   the ground behind it); clicking the held craft, or within 12 m of it, leaves it where it is.
5. **Place.** `KsaWorld.TryPlaceOnSurface` finds the body by Id and calls
   `Vehicle.TeleportToLocation(body, lat, lon)`.[^ksacars-ksaworld] The engine builds an initial
   kinematic state from the craft's bounding box and queues a `TeleportInputData` on
   `InputEvents.TeleportInputBuffer`, so the craft arrives upright and resting on the ground at the
   next `ApplyInputEvents`.[^ksa-vehicle] The tool deliberately does not drag the craft every frame:
   each teleport rebuilds its orbit and velocity.
6. **Draw.** `KsaWorld.BeginDraw(anchor, anchorEcl)` builds a `DrawAnchor` from
   `camera.GetPositionEgo(anchor)` (where KSA renders the craft) and the craft's Ecl position;
   `DrawTorusEcl` (a ring of `GizmosRenderer.DrawSphere` beads) and `DrawLineEcl` map Ecl points
   through `Ego + (posEcl - Ecl)`. Using `GetPositionEgo` rather than `EclToEgo(PositionEcl)` keeps
   the overlay on the craft as drawn, which differs once the camera follows something in flight.[^ksacars-drawanchor][^ksacars-pointing]

## Lifecycle and timing

`KSACarsMod.OnAfterGui` (StarMap's postfix on `Program.OnDrawUiViewports`) draws the panel first and
then calls `_mover.Update()` and `_mover.Draw()`, so a click on the panel is the panel's.[^ksacars-mod]
This is the right hook for both halves:

- **Input.** ImGui's frame is open, so `ImGui.IsMouseClicked`, `GetMousePos` and
  `GetIO().WantCaptureMouse` are current for this frame.
- **Gizmos.** `Program.OnFrame` calls `GizmosRenderer.ResetInstances()` before the UI pass and
  renders after it; gizmos submitted from `StarMapAfterGui` are drawn this frame, while ones
  submitted from `StarMapAfterOnFrame` (after the render) are cleared before they are ever drawn.[^ksa-program][^ksacars-notes]
- **Cameras.** `OnFrameViewports` has already built this frame's camera matrices, so projections
  and `GetPositionEgo` match what is about to be rendered.
- **Placement.** `TeleportToLocation` only queues an input event; it is applied in the next frame's
  `PrepareFrame` (`InputEvents.ApplyInputEvents`), on the main thread, between solver steps.

## Key code

| Path | Role |
|---|---|
| [src/KSACars/Ksa/CraftMover.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/CraftMover.cs) | Hover, pick up, put down, draw |
| [src/KSACars/Ksa/KsaWorld.Pointing.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.Pointing.cs) | Cursor ray, ground point, projection, gizmo anchor and rings |
| [src/KSACars/Sim/TerrainRay.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/TerrainRay.cs), [src/KSACars/Sim/TerrainMask.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/TerrainMask.cs) | Ray walk against the height field |
| [src/KSACars/Sim/CursorAim.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/CursorAim.cs), [src/KSACars/Sim/Picking.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/Picking.cs), [src/KSACars/Sim/DrawAnchor.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/DrawAnchor.cs) | Pixel spaces, nearest-within, Ecl-to-Ego anchoring (all unit tested) |
| [src/KSACars/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.cs#L358-L389) | `TryPlaceOnSurface` via `TeleportToLocation` |

## Snippets

```csharp
// src/KSACars/Ksa/KsaWorld.Pointing.cs L67-L98 (excerpt; the method continues)
    public static bool TryCursorRayEcl(out double3 originEcl, out double3 directionEcl)
    {
        originEcl = default;
        directionEcl = default;
        try
        {
            float2 cursor = ImGui.GetMousePos();

            ReadOnlySpan<IGameViewport> viewports = GameViewports;
            for (int i = 0; i < viewports.Length; i++)
            {
                IGameViewport v = viewports[i];
                if (!v.Visible) continue;

                if (v.GetCamera() is not { } camera) continue;

                // Framebuffer pixels, not viewport pixels: ScreenToEgoRay divides by the camera's
                // own framebuffer, and a render or display scale makes those different sizes.
                if (!CursorAim.TryToFramebuffer(cursor, v.Position, v.Width, v.Height,
                                                camera.FramebufferSize.X, camera.FramebufferSize.Y,
                                                out float2 local))
                {
                    continue;
                }

                double3 direction = camera.ScreenToEgoRay(local).Direction;
                if (!CursorAim.IsUsableDirection(direction)) continue;

                originEcl = camera.EgoToEcl(Vec.Zero);
                directionEcl = Vec.Unit(direction);
                return Vec.IsFinite(originEcl);
            }
```

```csharp
// current/decomp/KSA/Vehicle.cs L4219-L4233
	public void TeleportToLocation(Celestial celestial, double lat, double lon)
	{
		double3 double5 = new double3(_props.BoundingBoxAsmb.HalfWidth, _props.BoundingBoxAsmb.HalfHeight, _props.BoundingBoxAsmb.HalfLength);
		double3 boundsMinAsmb = MassToGeometryAsmb - double5;
		double3 boundsMaxAsmb = MassToGeometryAsmb + double5;
		InitialKinematicState initialKinematicStateForLocation = GetInitialKinematicStateForLocation(celestial, Universe.GetNextSimStep().NextTime, lat, lon, boundsMinAsmb, boundsMaxAsmb, double3.Zero, Orbit.OrbitLineColor);
		InputEvents.TeleportInputData data = new InputEvents.TeleportInputData
		{
			Vehicle = this,
			Orbit = initialKinematicStateForLocation.Orbit,
			Body2Cce = initialKinematicStateForLocation.Body2Cce,
			BodyRates = initialKinematicStateForLocation.BodyRates
		};
		InputEvents.TeleportInputBuffer.Add(data);
	}
```

## Game APIs touched

| API | Purpose |
|---|---|
| `Vehicle.TeleportToLocation` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L4219-L4233)) | Buffered placement on a body at lat/lon, resting on its bounding box |
| `Camera.ScreenToEgoRay`, `EgoToEcl`, `GetPositionEgo`, `FramebufferSize` ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L223-L715)) | Cursor ray and render-frame anchoring |
| `ViewportRegistry.GameViews`, `IGameViewport.Visible`, `Position`, `Width`, `Height`, `GetCamera` | Which viewport the cursor is in |
| `Program.GetRenderCamera`, `Program.GetMainCamera`, `Program.MainViewport` | The camera this frame renders with |
| `GizmosRenderer.DrawSphere`, `DrawLine`, `ResetInstances` ([current/decomp/KSA/GizmosRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GizmosRenderer.cs#L165-L471)) | Overlay rings and lines; reset each frame[^ksa-gizmos] |
| `Celestial.GetTerrainHeightFromDirCce`, `GetLatitudeFromCce`, `GetLongitudeFromCce` | Height field and coordinates of the hit |

## Pitfalls and version notes

- **Submit gizmos from the GUI hook.** From the frame postfix they are cleared before being drawn.
- **Anchor overlays with `GetPositionEgo`**, and difference Ecl positions sampled at the same instant;
  near Earth an Ecl position moves about 500 m per frame.
- **Mouse pixels are not framebuffer pixels** when a render scale or display scale is active.
- **`TeleportToLocation` is not a per-frame call**, faces the craft east, stands it on a launch pad
  when the point is within a pad's footprint, and over the sea sets it on the seabed; the mod's notes
  record each of these.[^ksacars-notes]
- The cursor walk ignores launch pads on purpose (the pad as a thicker planet swung the resolved point
  by kilometres at range); pads only matter for the car's wheels.
- Shift-click is ignored (reserved as a "designate" gesture in the shared tooling).

## See also

- [Car panel and ModMenu entry](./car-panel-and-modmenu.md)
- [Terrain contact under each wheel](./terrain-contact-under-wheels.md)
- [File-drop command bridge](./file-drop-command-bridge.md) (`site` uses the same placement)
- [Headless simulation tests](./headless-sim-tests.md)
- [Queue TeleportToLocation once per action](../../../howto/physics/kinematics/place-a-vessel-on-a-surface-at-latitude-and-longitude/place-a-vessel-on-a-surface-at-latitude-and-longitude-1.md) - how-to article demonstrating this feature
- [Walk the ray over the height field in bounded steps](../../../howto/physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/ray-cast-against-vessel-meshes-and-terrain-4.md) - how-to article demonstrating this feature
- [Cursor to world ray: convert to camera framebuffer pixels before ScreenToEgoRay](../../../howto/ui/input/cursor-to-world-ray/cursor-to-world-ray-3.md) - how-to article demonstrating this feature
- [Pick objects under the cursor: screen-space nearest with a per-item reach](../../../howto/ui/input/pick-objects-under-the-cursor/pick-objects-under-the-cursor-1.md) - how-to article demonstrating this feature
- [Pick objects under the cursor: clearance-stepped terrain march with bisection, then a buffered TeleportToLocation](../../../howto/ui/input/pick-objects-under-the-cursor/pick-objects-under-the-cursor-4.md) - how-to article demonstrating this feature
- [Place pieces with a ghost: gizmo rings anchored with GetPositionEgo](../../../howto/ui/input/place-pieces-on-a-grid-with-a-ghost/place-pieces-on-a-grid-with-a-ghost-2.md) - how-to article demonstrating this feature

[^ksacars-craftmover]: src/KSACars/Ksa/CraftMover.cs
[^ksacars-pointing]: src/KSACars/Ksa/KsaWorld.Pointing.cs
[^ksacars-terrainray]: src/KSACars/Sim/TerrainRay.cs
[^ksacars-terrainmask]: src/KSACars/Sim/TerrainMask.cs
[^ksacars-cursoraim]: src/KSACars/Sim/CursorAim.cs
[^ksacars-picking]: src/KSACars/Sim/Picking.cs
[^ksacars-drawanchor]: src/KSACars/Sim/DrawAnchor.cs
[^ksacars-ksaworld]: src/KSACars/Ksa/KsaWorld.cs
[^ksacars-mod]: src/KSACars/Ksa/KSACarsMod.cs
[^ksacars-notes]: docs/KSA-MODDING-NOTES.md
[^ksacars-claude]: CLAUDE.md
[^ksa-vehicle]: KSA.Vehicle (decompiled)
[^ksa-camera]: KSA.Camera (decompiled)
[^ksa-gizmos]: KSA.GizmosRenderer (decompiled)
[^ksa-program]: KSA.Program (decompiled)
