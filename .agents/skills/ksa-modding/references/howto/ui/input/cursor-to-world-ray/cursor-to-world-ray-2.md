---
type: How-To
title: "Cursor to world ray: viewport lookup, EgoToEcl and a projected round trip"
description: Find the viewport under the cursor, build an ecliptic ray from its camera and verify it by projecting the hit back and measuring the pixel error
category: ui
group: input
topic: cursor-to-world-ray
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/cursor-picking-and-terrain-ray]
techniques: [imgui-drawlist, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:viewports]
game_types: [KSA.Camera, KSA.ViewportRegistry, KSA.IGameViewport, KSA.IViewport, KSA.Ray]
tags: [cursor, raycast, viewport, debugging, round-trip, projection]
---
KSAGolf needs the point on the ground under the mouse for its craft mover and course builder. This
article is about getting the ray right and proving it: pick the viewport that contains the cursor, unproject
through that viewport's camera, and, because any convention mistake shows up only on screen, project the answer
back and log the pixel error. The ray function itself is the same shape as in
[approach 1](cursor-to-world-ray-1.md), so this article concentrates on viewport selection and the round trip.
Demonstrated by KSAGolf.

## Background: how the game does it

- `ViewportRegistry.GameViews` holds only game viewports, each with a camera; it excludes the part-thumbnail
  renderer, which is not one [current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L50-L52)[^ksa-viewportregistry].
- A viewport is a rectangle: `IViewport.Position` (screen coordinates of its top-left), `Width`, `Height`,
  `Visible` and `GetCamera()` [current/decomp/KSA/IViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IViewport.cs#L19-L51)[^ksa-iviewport]. In `Map` mode `GetCamera()` returns the map camera,
  otherwise the base camera [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L297-L300)[^ksa-gameviewport].
- `Camera.ScreenToEgoRay` needs pixels in the camera's framebuffer, and `EgoToEcl` is a pure translation
  [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L222-L227)[^ksa-camera].
- `Camera.EclToScreen(posEcl, ignoreBehind)` is the inverse direction: `EclToEgo` then `EgoToScreen`, returning
  viewport-local coordinates [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L340-L356)[^ksa-camera].

## Lifecycle and timing

Everything runs on the main thread in `[StarMapAfterGui]` (`frame:draw-ui`): `KSAGolfMod.OnAfterGui` draws the
panel, then the course builder's input, then `CraftMover.Update` and `Draw`. ImGui's cursor and capture flags are
only meaningful between `NewFrame` and `Render`, and each viewport's camera was updated earlier in the same
frame by `OnFrameViewports` (`frame:viewports`), so the ray matches what the frame will render. A ray cast from
`StarMapAfterOnFrame` would use a camera that has already rendered. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Read the viewports defensively. The registry property can throw before the main viewport exists, so KSAGolf wraps
   it:

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L1159-L1169
// The views this mod may drive, in the order its viewport numbers index. GameViews rather than
// Views because only a game viewport has a camera and controllers -- which also keeps the
// part-thumbnail renderer out, since it is not one.
private static ReadOnlySpan<IGameViewport> GameViewports
{
    get
    {
        try { return ViewportRegistry.GameViews; }
        catch { return default; }
    }
}
```

2. Convert the cursor into the first visible viewport that contains it and unproject through that viewport's
   camera, as in approach 1:

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L1315-L1354
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

        return false;
    }
    catch
    {
        return false;
    }
}
```

3. Keep the pixel math in a game-free class so it can be tested with fake viewports. The viewport offset is the real
   correction; the framebuffer scale is insurance:

```csharp
// src/KSAGolf/Sim/CursorAim.cs L35-L52
public static bool TryToViewport(float2 cursorScreen, float2 viewportOrigin,
                                 int width, int height, out float2 local)
{
    local = default;

    if (width <= 0 || height <= 0) return false;
    if (!IsFinite(cursorScreen) || !IsFinite(viewportOrigin)) return false;

    float x = cursorScreen.X - viewportOrigin.X;
    float y = cursorScreen.Y - viewportOrigin.Y;

    // Exclusive at the far edge: a cursor at exactly width unprojects to NDC +1, which is the
    // frustum boundary rather than a point inside it.
    if (x < 0f || y < 0f || x >= width || y >= height) return false;

    local = new float2(x, y);
    return true;
}
```

4. Add the projection back. `TryProjectAhead` is `viewport.Position + camera.EclToScreen(point, ignoreBehind: true)`
   for the main viewport, culling anything behind the camera:

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L1397-L1420
public static bool TryProjectAhead(double3 pointEcl, out float2 screen)
{
    screen = default;
    try
    {
        if (Program.MainViewport is not { } viewport) return false;
        if (viewport.GetCamera() is not { } camera) return false;
        if (viewport.Width <= 0 || viewport.Height <= 0) return false;

        float2 local = camera.EclToScreen(pointEcl, ignoreBehind: true);
        if (!float.IsFinite(local.X) || !float.IsFinite(local.Y)) return false;
        if (local.X < 0f || local.Y < 0f || local.X > viewport.Width || local.Y > viewport.Height)
        {
            return false;
        }

        screen = new float2(viewport.Position.X + local.X, viewport.Position.Y + local.Y);
        return true;
    }
    catch
    {
        return false;
    }
}
```

5. Add the round trip. Given the ecliptic point you solved from the ray, describe which viewport was chosen and the
   error between the cursor and the point projected back:

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L1275-L1313
public static string DescribeCursorRay(double3 solvedEcl)
{
    try
    {
        float2 cursor = ImGui.GetMousePos();
        ImGuiViewportPtr main = ImGui.GetMainViewport();

        string chosen = "none";
        ReadOnlySpan<IGameViewport> viewports = GameViewports;
        for (int i = 0; i < viewports.Length; i++)
        {
            IGameViewport v = viewports[i];
            if (!v.Visible) continue;
            if (!CursorAim.TryToViewport(cursor, v.Position, v.Width, v.Height, out float2 local))
            {
                continue;
            }

            Camera? c = v.GetCamera();
            chosen = $"vp{i} pos={v.Position.X:F0},{v.Position.Y:F0} "
                     + $"size={v.Width}x{v.Height} fb={c?.FramebufferSize.X}x{c?.FramebufferSize.Y} "
                     + $"local={local.X:F0},{local.Y:F0}";
            break;
        }

        string back = TryProjectAhead(solvedEcl, out float2 screen)
                          ? $"back={screen.X:F0},{screen.Y:F0} "
                            + $"err={screen.X - cursor.X:F0},{screen.Y - cursor.Y:F0}"
                          : "back=offscreen";

        return $"cursor={cursor.X:F0},{cursor.Y:F0} "
               + $"mainvp={main.Pos.X:F0},{main.Pos.Y:F0} {main.Size.X:F0}x{main.Size.Y:F0} "
               + $"{chosen} {back}";
    }
    catch (Exception e)
    {
        return $"(describe failed: {e.Message})";
    }
}
```

   `CraftMover` calls it every 120th frame through a lazy `Log.Debug`, so the cost is nil when debug logging is
   off:

```csharp
// src/KSAGolf/Ksa/CraftMover.cs L243
if (++_aimTrace % 120 == 0) Log.Debug(() => $"aim: {KsaWorld.DescribeCursorRay(groundEcl)}");
```

   Expect the log line to read `err=0,0` to within a pixel. A constant offset means the viewport origin was not
   subtracted; an error that grows across the screen means the framebuffer scale is wrong.

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L1315-L1354) | `TryCursorRayEcl` |
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L1275-L1313) | `DescribeCursorRay` round trip |
| [src/KSAGolf/Sim/CursorAim.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/CursorAim.cs#L35-L52) | `TryToViewport` |
| [tests/KSAGolf.Tests/CursorAimTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/CursorAimTests.cs) | Offset viewports, edges, scale, mount parallax |

## Variations and gotchas

- **First visible match wins.** Overlapping viewport windows resolve to the first visible game viewport in registry
  order that contains the cursor, not to the one drawn on top.
- **A ray through a craft hits the ground behind it.** To place something at a craft use its own surface point, not the
  cursor ray.
- **Do not refine a sphere hit.** Looking along a slope the mean sphere is met behind the hill; march from the eye (see
  [pick objects under the cursor](../pick-objects-under-the-cursor/overview.md)).
- **Version.** KSAGolf was built against 2026.9.22.5482; `Camera`, `GameViewport` and `ViewportRegistry` members
  used here have the same signatures in 2026.10.10.5554.

## See also

- [Overview of cursor-to-world-ray](overview.md)
- [Approach 1: ScreenToEgoRay with viewport offset and framebuffer scale](cursor-to-world-ray-1.md)
- [Approach 3: framebuffer pixel conversion](cursor-to-world-ray-3.md)
- Mod feature: [Cursor picking and terrain ray](../../../../mods/ksagolf/features/cursor-picking-and-terrain-ray.md)

[^ksa-viewportregistry]: KSA.ViewportRegistry (decompiled, build 2026.10.10.5554)
[^ksa-iviewport]: KSA.IViewport (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
