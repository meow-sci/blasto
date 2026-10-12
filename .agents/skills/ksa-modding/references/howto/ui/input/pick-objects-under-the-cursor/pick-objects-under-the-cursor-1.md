---
type: How-To
title: "Pick objects under the cursor: screen-space nearest with a per-item reach"
description: Choose the vessel under the mouse by projecting every vehicle to the screen and taking the nearest one within a reach that scales with its apparent size
category: ui
group: input
topic: pick-objects-under-the-cursor
approach: 1
difficulty: beginner
prerequisites: [starmap]
demonstrated_by:
  - mods/ksagolf/features/cursor-picking-and-terrain-ray
  - mods/ksacars/features/craft-mover-mouse-pick-place
  - mods/ksagolf/features/craft-mover
techniques: [imgui-drawlist, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:viewports]
game_types: [KSA.Vehicle, KSA.Camera, KSA.Program, KSA.Universe, KSA.IGameViewport]
tags: [picking, vehicle, screen-space, hover, craft-mover]
---
The cheapest way to answer "which craft is the player pointing at" needs no ray and no mesh: project every vehicle
to the screen, give each a reach in pixels that matches how big it looks, and take the nearest one the cursor is
inside the reach of. KSAGolf and KSACars use it for a craft mover (hover shows a ring, one click picks up, a second
click sets down). Use it when approximate is enough, the objects are far apart on screen, or you need to pick craft the
engine's hull test cannot hit (meshless craft such as kittens). Demonstrated by KSAGolf and KSACars.

## Background: how the game does it

- `Camera.EclToScreen(posEcl, ignoreBehind)` projects an ecliptic point to viewport-local pixels; it returns
  `(NaN, NaN)` for points behind the camera when `ignoreBehind` is true [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L340-L356)[^ksa-camera]. Add
  `viewport.Position` to get the screen coordinates ImGui reports.
- `Universe.CurrentSystem` lists every `Astronomical`, including `Vehicle` instances; `Vehicle.MeanRadius`-style
  extents (`BoundingSphereRadiusBody`) give a size in metres.
- KSA's own hover outline uses a different method, a hull ray test per part (`Vehicle.UpdateHighlight`, see
  [approach 2](pick-objects-under-the-cursor-2.md)), and only in orbit mode with the cursor free.[current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2903-L2924)[^ksa-vehicle]

## Lifecycle and timing

Everything runs on the main thread in `[StarMapAfterGui]` (`frame:draw-ui`): `OnAfterGui` draws the panel, then calls
`CraftMover.Update`, which reads ImGui's mouse state (valid between `NewFrame` and `Render`) after the panel so a click
on a window is the window's. Cameras were updated in `frame:viewports` earlier in the frame, so the projection matches
what is drawn. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Gate the frame: ignore the click when ImGui wants the mouse or Shift is held; compute the hover every frame, not just on
   click, so the player sees what a click would pick:

```csharp
// src/KSAGolf/Ksa/CraftMover.cs L66-L108
public void Update(Config config)
{
    if (!config.MoveCraftWithMouse)
    {
        Release();
        return;
    }

    if (!KsaWorld.IsAlive(_held)) _held = null;
    if (!KsaWorld.IsAlive(_hovered)) _hovered = null;

    bool wantCapture = ImGui.GetIO().WantCaptureMouse;
    // Shift-click is left to other tools, so it is not a click on the world.
    bool clicked = ImGui.IsMouseClicked(ImGuiMouseButton.Left, repeat: false)
                   && !ImGui.GetIO().KeyShift;
    bool down = ImGui.IsMouseDown(ImGuiMouseButton.Left);

    // Whether a click reaches this hook at all is not obvious -- ImGui, KSA's own input and
    // this mod all read the same mouse -- so it stays traced, quietly.
    if (clicked || down || ++_trace % 120 == 0)
    {
        float2 where = ImGui.GetMousePos();
        Log.Debug(() => $"mover: capture={wantCapture} clicked={clicked} down={down} "
                        + $"cursor={where.X:F0},{where.Y:F0} held={_held is not null}");
    }

    // The panel and the marker labels are windows; a click meant for one of them is not a
    // click on the world behind it.
    if (wantCapture)
    {
        _hovered = null;
        return;
    }

    // Worked out every frame, not only on click: clicking blind and finding out afterwards
    // which craft was nearest is not an interaction, it is a guess.
    _hovered = UnderCursor();

    if (!clicked) return;

    if (_held is null) PickUp();
    else PutDown();
}
```

2. Project each craft. A craft off screen or behind the camera gets a position that cannot be picked, so list indices stay
   aligned with the craft list. `TryProjectAhead` is `viewport.Position + camera.EclToScreen(point, ignoreBehind: true)`
   for the main viewport:

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

3. Give each craft a reach equal to its apparent size, clamped. Apparent radius is measured by projecting a point one
   radius to the camera's right rather than reconstructing the field of view, so the projection answers for the lens:

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L694-L717
public static bool TryApparentRadiusPixels(double3 atEcl, double metres, out float pixels)
{
    pixels = 0f;
    if (!double.IsFinite(metres) || metres <= 0.0) return false;

    try
    {
        if (Program.GetMainCamera() is not { } camera) return false;

        double3 right = camera.GetRightEcl();
        if (!Vec.IsFinite(right) || Vec.Len(right) < 0.5) return false;

        if (!TryProjectAhead(atEcl, out float2 centre)) return false;
        if (!TryProjectAhead(atEcl + Vec.Unit(right) * metres, out float2 edge)) return false;

        float dx = edge.X - centre.X, dy = edge.Y - centre.Y;
        pixels = MathF.Sqrt(dx * dx + dy * dy);
        return float.IsFinite(pixels);
    }
    catch
    {
        return false;
    }
}
```

4. Pick the nearest inside its own reach. A flat radius makes a big vessel as hard to hit as a drone; per-item reach does
   not. Ties go to the nearest centre, not to list order:

```csharp
// src/KSAGolf/Sim/Picking.cs L52-L72
public static int NearestWithin(IReadOnlyList<float2> positions, IReadOnlyList<float> radii,
                                float2 cursor)
{
    int best = -1;
    float bestDistance2 = float.MaxValue;

    for (int i = 0; i < positions.Count && i < radii.Count; i++)
    {
        float dx = positions[i].X - cursor.X;
        float dy = positions[i].Y - cursor.Y;
        float distance2 = dx * dx + dy * dy;

        if (distance2 > radii[i] * radii[i]) continue;
        if (distance2 >= bestDistance2) continue;

        bestDistance2 = distance2;
        best = i;
    }

    return best;
}
```

5. Assemble the loop:

```csharp
// src/KSAGolf/Ksa/CraftMover.cs L111-L141
private Vehicle? UnderCursor()
{
    KsaWorld.CollectVehicles(_craft);
    if (_craft.Count == 0) return null;

    _screen.Clear();
    _reach.Clear();
    for (int i = 0; i < _craft.Count; i++)
    {
        // Off-screen craft get a position that cannot be picked, so the indices stay aligned
        // with _craft and the nearest answer means what it says.
        double3 atEcl = KsaWorld.PositionEcl(_craft[i]);
        _screen.Add(KsaWorld.TryProjectAhead(atEcl, out float2 at)
                        ? at
                        : new float2(float.MaxValue, float.MaxValue));

        // As big as the craft looks, so pointing anywhere on a vessel picks it rather than
        // only its centre. MeanRadius is a bounding measure, so this is its whole extent.
        float reach = MinPickRadius;
        if (KsaWorld.TryApparentRadiusPixels(atEcl, KsaWorld.MeanRadius(_craft[i]),
                                             out float pixels))
        {
            reach = Math.Clamp(pixels, MinPickRadius, MaxPickRadius);
        }

        _reach.Add(reach);
    }

    int pick = Picking.NearestWithin(_screen, _reach, ImGui.GetMousePos());
    return pick < 0 ? null : _craft[pick];
}
```

   The clamp constants are `MinPickRadius = 14f` and `MaxPickRadius = 140f` pixels
   ([src/KSAGolf/Ksa/CraftMover.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CraftMover.cs#L31-L32)).
6. Pair it with a ground pick for placement. Pointing at a craft uses that craft's own footing (a ray through its
   middle meets the ground behind it); otherwise use the terrain ray in
   [approach 4](pick-objects-under-the-cursor-4.md).

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Ksa/CraftMover.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CraftMover.cs) | Hover, pick up, put down |
| [src/KSAGolf/Sim/Picking.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/Picking.cs) | `NearestWithin`, `NearestOnScreen`, `TryHitSphere` (headless-testable) |
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs) | `TryProjectAhead`, `TryApparentRadiusPixels` |
| [src/KSACars/Ksa/CraftMover.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/CraftMover.cs) | The same tool in KSACars (carried over from KSArmory) |

## Variations and gotchas

- **No occlusion.** A craft behind a hill still wins if its projected centre is closest; add a terrain or hull test
  ([approach 2](pick-objects-under-the-cursor-2.md)) if that matters.
- **Overlapping reaches** resolve to the nearer centre, not the nearer surface.
- **Main viewport only.** `TryProjectAhead` uses the main camera; a pick in a secondary viewport needs that viewport's
  camera and `Position` (see [cursor to world ray](../cursor-to-world-ray/overview.md)).
- **Do not drag every frame.** Both mods set the craft down once; each `TeleportToLocation` rebuilds its orbit and velocity.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of pick-objects-under-the-cursor](overview.md)
- [Approach 2: Part.RayCastEgo hull test plus terrain march](pick-objects-under-the-cursor-2.md)
- [Approach 4: clearance-stepped terrain march and TeleportToLocation](pick-objects-under-the-cursor-4.md)
- Mod features: [Cursor picking and terrain ray](../../../../mods/ksagolf/features/cursor-picking-and-terrain-ray.md),
  [Craft mover](../../../../mods/ksagolf/features/craft-mover.md),
  [Craft mover mouse pick and place](../../../../mods/ksacars/features/craft-mover-mouse-pick-place.md)

[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
