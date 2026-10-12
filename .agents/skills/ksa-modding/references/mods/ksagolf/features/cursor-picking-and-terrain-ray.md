---
type: Mod Feature
title: Cursor picking and the terrain ray
description: Turning the ImGui cursor into a world ray through the right viewport's camera, walking that ray to the first place it goes under the terrain, and picking the craft nearest the cursor on screen with a per-item reach
mod: ksagolf
category: ui
techniques: [imgui-input, ray-march, closed-form-geometry, headless-tests]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Camera
  - KSA.ViewportRegistry
  - KSA.IGameViewport
  - KSA.GameViewport
  - KSA.Program
  - KSA.Celestial
  - KSA.CelestialSystem
  - KSA.Universe
  - KSA.Ray
  - Brutal.ImGuiApi.ImGui
tags: [cursor, picking, raycast, viewport, framebuffer, terrain, ray-march, bisection, imgui, screen-space]
---

Three small pieces turn "where is the mouse" into something a mod can act on: a cursor ray through
the camera of whichever viewport the cursor is over, a terrain ray that finds the first place that
ray goes under the ground, and a screen-space pick of the nearest craft with a reach that scales with
how big each craft looks. The craft mover uses all three to pick a vessel up and set it down where
the pointer is; the course builder uses the cursor ray against its own plane.[^golf-craftmover][^golf-coursebuilder]

All the maths is in `Sim/` (`CursorAim`, `TerrainRay`, `Picking`), so it runs headless with fake
viewports and synthetic height fields; `KsaWorld` only feeds it engine numbers.

## How it works

### Cursor to world ray

`KsaWorld.TryCursorRayEcl` reads `ImGui.GetMousePos()` (screen coordinates spanning every window),
walks `ViewportRegistry.GameViews`, and for the first visible viewport containing the cursor:

1. `CursorAim.TryToFramebuffer` subtracts the viewport's `Position`, rejects the far edges
   (a cursor at exactly the width unprojects to the frustum boundary), and scales from viewport
   pixels into the camera's `FramebufferSize`;
2. `Camera.ScreenToEgoRay(local).Direction` unprojects it;
3. `CursorAim.IsUsableDirection` rejects zero or non-finite directions (a degenerate projection
   would otherwise poison everything with NaN);
4. the origin is `Camera.EgoToEcl(0)` and the direction is normalised.[^golf-ksaworld][^golf-cursoraim]

The offset is the real correction: without it the ray is right on a single full-screen viewport
and off by a constant amount everywhere else. The scale is insurance. In this build
`GameViewport.SetSize` assigns `Size` and calls `Camera.Resize` on both of its cameras, which
assigns `FramebufferSize`, so the two are equal; the mod scales anyway because `ScreenToEgoRay`
divides by the framebuffer.[^ksa-gameviewport][^ksa-camera]

### Ray to ground

`KsaWorld.TryGroundAlong(eye, direction)` asks every celestial with `TerrainRay.TryFirstHit`,
through `TerrainHeights(body, accurate: true)`, and keeps the nearest range. `TerrainRay`:[^golf-terrainray]

- limits the walk to the part of the ray inside `meanRadius + maxTerrain`, solved in closed form by
  `TerrainMask.TryBandBelow`;[^golf-terrainmask]
- marches from the eye: each step is as long as the ray's clearance above the ground at that point
  (divided by a maximum slope of 1:1), clamped to 1..1000 m, and never shorter than 0.5% of the range
  walked; at most 512 samples;
- on the first sample below the ground, bisects back to the last clear sample until the crossing is
  known to 0.5 m;
- answers false when the very first sample is already underground (an eye inside a hill points at
  nothing nameable) and skips samples the height field cannot answer.

Walking out from the eye is the point. The obvious alternative, hitting the mean sphere and refining
by the height under that answer, samples ground behind a hill seen side-on and lands the pick on the
terrain beyond what the pointer is on.[^golf-terrainray] `KsaWorld.TryCursorGroundPoint` adds the
body id and latitude and longitude through `Celestial.GetLatitudeFromCce` and `GetLongitudeFromCce`.[^ksa-celestial]
The ceiling `top` is `MaxTerrainRadius - MeanRadius` plus 8 km of modifier headroom, a real bound
on the terrain (see [Terrain height queries](terrain-height-queries.md)).

### Nearest on screen

`Picking.NearestWithin(positions, radii, cursor)` returns the index of the screen position nearest
the cursor among those within their own reach. The craft mover projects each craft with
`KsaWorld.TryProjectAhead` (the main camera's `EclToScreen` with `ignoreBehind: true`, offset by the
viewport position) and gives each a reach equal to its apparent radius in pixels, clamped, so a big
vessel is picked anywhere on it and a small one is not impossible to hit. Off-screen craft get an
unpickable position so indices stay aligned.[^golf-picking][^golf-craftmover]
`Picking.TryHitSphere` (near root, or the exit from inside) and `NearestOnScreen` are there too.

`KsaWorld.DescribeCursorRay` is the debugging tool behind all this: it projects the solved ground
point back to the screen and logs its pixel error against the cursor, the measurement that settles
any convention question.[^golf-ksaworld]

`CursorAim` also carries `TryAimFromMount` (a bearing from a mount to the point under the cursor,
correcting parallax) and a dead-zone and command-strength pair for a cursor-steered view; nothing in
this commit calls them.[^golf-cursoraim]

## Lifecycle and timing

Everything runs on the main thread inside `[StarMapAfterGui]` (Harmony postfix on
`Program.OnDrawUiViewports`, `frame:draw-ui`): `KSAGolfMod.OnAfterGui` draws the panel, then the
course builder's input, then `CraftMover.Update` and `Draw`, each of which may cast a cursor ray.[^golf-mod]
Two things make that the right moment:

- ImGui's cursor position and its capture flags are only meaningful between `ImGui.NewFrame`
  (`Program.PrepareImGui`) and `ImGui.Render`, which bracket the UI pass.[^ksa-program]
- Each viewport's camera was updated earlier in the same frame by `OnFrameViewports`, so the ray
  matches the camera the frame is about to render with. A ray cast from the frame postfix would use
  a camera that has already rendered.

The terrain ray's cost is bounded by its sample cap; the repo's test asserts an ordinary pick costs
at most a few hundred lookups.[^golf-terrainray-tests]

## Key code

| Path | Role |
|---|---|
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L1315-L1354) | `TryCursorRayEcl`: cursor to world ray through the right viewport |
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L419-L505) | `TryCursorGroundPoint` and `TryGroundAlong`: nearest body hit along the ray |
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L1270-L1313) | `DescribeCursorRay`: round-trip pixel error for debugging |
| [src/KSAGolf/Sim/CursorAim.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/CursorAim.cs) | Viewport offset, framebuffer scale, direction check, mount aim, dead zone |
| [src/KSAGolf/Sim/TerrainRay.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/TerrainRay.cs) | Clearance-stepped march and bisection to the first ground crossing |
| [src/KSAGolf/Sim/Picking.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/Picking.cs) | Ray-sphere hit and nearest-on-screen picks |
| [src/KSAGolf/Ksa/CraftMover.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CraftMover.cs#L110-L141) | The craft pick with apparent-size reach |
| [tests/KSAGolf.Tests/CursorAimTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/CursorAimTests.cs) | Offset viewports, edges, framebuffer scale, mount parallax, dead zone |
| [tests/KSAGolf.Tests/TerrainRayTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/TerrainRayTests.cs) | A hill in front is hit, not the ground behind it; sky and eye-in-hill name nothing; cost bound |
| [tests/KSAGolf.Tests/PickingTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/PickingTests.cs) | Near side, behind, inside, per-item reach, overlapping reaches |

## Snippets

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L1315-L1346
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
// src/KSAGolf/Sim/CursorAim.cs L60-L76
    public static bool TryToFramebuffer(float2 cursorScreen, float2 viewportOrigin,
                                        int width, int height,
                                        int framebufferWidth, int framebufferHeight,
                                        out float2 local)
    {
        local = default;

        if (framebufferWidth <= 0 || framebufferHeight <= 0) return false;
        if (!TryToViewport(cursorScreen, viewportOrigin, width, height, out float2 inViewport))
        {
            return false;
        }

        local = new float2(inViewport.X * framebufferWidth / width,
                           inViewport.Y * framebufferHeight / height);
        return true;
    }
```

```csharp
// src/KSAGolf/Sim/TerrainRay.cs L66-L92
        double t = from * maxRange;
        double end = to * maxRange;
        double clearAt = double.NaN;

        for (int i = 0; i < MaxSamples && t <= end; i++)
        {
            double floor = Math.Max(MinStepMetres, t * MinStepFraction);

            if (!TryClearance(origin, dir, t, centre, meanRadius, heights, out double clearance))
            {
                t += floor;
                continue;
            }

            if (clearance <= 0.0)
            {
                if (double.IsNaN(clearAt)) return false;

                range = Refine(origin, dir, clearAt, t, centre, meanRadius, heights);
                return true;
            }

            clearAt = t;
            t += Math.Clamp(Math.Max(clearance / MaxSlope, floor), MinStepMetres, MaxStepMetres);
        }

        return false;
```

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L472-L489
                double3 centre = body.GetPositionEcl();
                double top = MaxTerrainHeightMetres(body);

                // Far enough to cross the whole body. Only the part below its highest ground is
                // walked, and the walk stops at the first place it is under it.
                double reach = 2.0 * (Vec.Len(eye - centre) + body.MeanRadius + top);

                // Terrain only. A launch pad is 8 m of pedestal 40 m across, and adding it here
                // models it as an 8 m thicker planet: at 5 km the resolved point moves 2.8 km, and
                // sweeping the cursor over the pad edge swings the bearing from the mount through
                // 168 degrees between one pixel and the next. Where a structure's surface is has
                // no answer in this engine -- see docs/BLOCKED-ON-KSA.md.
                if (!TerrainRay.TryFirstHit(eye, direction, reach, centre, body.MeanRadius, top,
                                            new TerrainHeights(body, accurate: true), out double range)
                    || range >= nearestRange)
                {
                    continue;
                }
```

## Game APIs touched

| API | Decomp | Purpose |
|---|---|---|
| `Camera.ScreenToEgoRay(float2)` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L704-L713) | Unproject a framebuffer pixel to an `Ego` ray |
| `Camera.FramebufferSize` / `Resize` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L464-L469) | What the unprojection divides by; set from the viewport size |
| `Camera.EgoToEcl` / `EclToScreen(pos, ignoreBehind)` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L223-L345) | Ray origin in `Ecl`; projecting craft to screen for the pick |
| `ViewportRegistry.GameViews` | [current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L52) | The game viewports (each with a camera) in order |
| `IViewport.Visible` / `Position` / `Width` / `Height` | [current/decomp/KSA/IViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IViewport.cs#L27-L47) | The viewport rectangle in screen coordinates |
| `GameViewport.SetSize` (private) | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L262-L268) | Why viewport and framebuffer sizes are equal in this build |
| `Celestial.GetLatitudeFromCce` / `GetLongitudeFromCce` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L691-L733) | Naming the ground point |
| `Celestial.GetTerrainHeightFromDirCce` (through `TerrainHeights`) | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L777-L781) | Each march sample, `accurate: true` |
| `ImGui.GetMousePos` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8254) | The cursor in screen coordinates |

## Pitfalls and version notes

- **ImGui's cursor is in screen space, a camera's framebuffer is not.** Subtract the viewport's
  position before unprojecting, or the aim is off by a constant on any non-primary or offset
  viewport.[^golf-cursoraim]
- **Do not refine a sphere hit.** Looking along a slope, the mean sphere is met behind the hill;
  march from the eye instead.[^golf-terrainray]
- **Terrain only, on purpose.** Adding a launch pad's height to the cursor's ground models it as a
  thicker planet and makes the resolved point jump kilometres at the pad's edge; the ball's ground
  uses the launch-site raycast instead (see [Launch-site surface raycast](launch-site-surface-raycast.md)).[^golf-ksaworld]
- **`accurate: true` matters for picking.** The coarse query skips the procedural modifiers and the
  launch-site levelling decals, which moves the ground the pointer is on by up to the modifiers'
  amplitudes.
- **Visible viewports only, first match wins.** Overlapping windows resolve to the first visible
  game viewport in registry order containing the cursor.
- **A ray through a craft hits the ground behind it.** To place something at a craft, use the craft's
  own surface point (`KsaWorld.TryCraftSurfacePoint`) rather than the cursor ray.[^golf-ksaworld]
- **Version.** Built against 2026.9.22.5482; `Camera`, `GameViewport` and `ViewportRegistry` members
  used here exist with the same signatures in 2026.10.10.5554 (`SetSize` is now on `GameViewport`,
  which the mod's comment calls `Viewport.SetSize`).

## See also

- [Craft mover](craft-mover.md)
- [Top-down minigolf course builder](minigolf-course-builder.md)
- [Terrain height queries off the engine's height field](terrain-height-queries.md)
- [Launch-site surface by raycast into a copied collider](launch-site-surface-raycast.md)
- [Headless simulation tests behind a Sim/Ksa boundary](headless-sim-tests.md)
- [Developer panel and menu-bar entry](developer-panel-and-menu-bar.md)
- [Body-fixed or orbit-relative anchors lifted to ecliptic, then Camera.EclToScreen](../../../howto/rendering/overlays/project-world-positions-to-screen/project-world-positions-to-screen-1.md) - how-to article demonstrating this feature
- [Cursor to world ray: viewport lookup, EgoToEcl and a projected round trip](../../../howto/ui/input/cursor-to-world-ray/cursor-to-world-ray-2.md) - how-to article demonstrating this feature
- [Pick objects under the cursor: screen-space nearest with a per-item reach](../../../howto/ui/input/pick-objects-under-the-cursor/pick-objects-under-the-cursor-1.md) - how-to article demonstrating this feature
- [Pick objects under the cursor: clearance-stepped terrain march with bisection, then a buffered TeleportToLocation](../../../howto/ui/input/pick-objects-under-the-cursor/pick-objects-under-the-cursor-4.md) - how-to article demonstrating this feature

[^golf-cursoraim]: src/KSAGolf/Sim/CursorAim.cs
[^golf-picking]: src/KSAGolf/Sim/Picking.cs
[^golf-terrainray]: src/KSAGolf/Sim/TerrainRay.cs
[^golf-terrainmask]: src/KSAGolf/Sim/TerrainMask.cs
[^golf-ksaworld]: src/KSAGolf/Ksa/KsaWorld.cs
[^golf-craftmover]: src/KSAGolf/Ksa/CraftMover.cs
[^golf-coursebuilder]: src/KSAGolf/Ksa/CourseBuilder.cs
[^golf-mod]: src/KSAGolf/Ksa/KSAGolfMod.cs
[^golf-terrainray-tests]: tests/KSAGolf.Tests/TerrainRayTests.cs
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
