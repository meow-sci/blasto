---
type: How-To
title: "Cursor to world ray: convert to camera framebuffer pixels before ScreenToEgoRay"
description: Why the cursor must be offset by the viewport origin and scaled into the camera framebuffer before unprojection, with a headless-tested conversion and a pick-up-and-place tool built on it
category: ui
group: input
topic: cursor-to-world-ray
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksacars/features/craft-mover-mouse-pick-place]
techniques: [imgui-drawlist, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:viewports]
game_types: [KSA.Camera, KSA.IGameViewport, KSA.ViewportRegistry, KSA.GameViewport, KSA.Vehicle]
tags: [cursor, raycast, framebuffer, viewport, tests, craft-mover]
---
KSACars has a small tool that lets the player click a craft to pick it up and click the ground to set it down.
The ray it uses is the same ray as in [approach 1](cursor-to-world-ray-1.md); what this article adds is the reason
the conversion has two parts (offset, then scale), how the author proved both with unit tests that need no game,
and the hook ordering that makes a panel click not count as a world click. Demonstrated by KSACars (the tool
was carried over from KSArmory).

## Background: how the game does it

- `Camera.ScreenToEgoNearPlane` turns a screen position into NDC by dividing by the camera's own `FramebufferSize`
  [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L674-L687)[^ksa-camera]. It does not know about the OS window, the ImGui window or any display scale.
- `GameViewport.SetSize` assigns the viewport size and calls `Resize` on both cameras in one statement, so in
  build 2026.10.10.5554 viewport size and framebuffer size are equal [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L262-L268)[^ksa-gameviewport].
- ImGui reports the cursor in screen space across every window, so the camera's idea of "pixel (0,0)" and ImGui's
  cursor only agree on a full-screen main viewport.

## Lifecycle and timing

`KSACarsMod.OnAfterGui` (a `[StarMapAfterGui]` method, `frame:draw-ui`) runs the panel first, then
`_mover.Update()` and `_mover.Draw()`:

```csharp
// src/KSACars/Ksa/KSACarsMod.cs L88-L110
public void OnAfterGui(double dt)
{
    try
    {
        if (_disabled) return;
        StepOnce();
        if (!KsaWorld.InFlightScene)
        {
            _mover.Release();
            return;
        }

        CarPanel.Draw(_mover);
        // After the panel, so a click on its checkbox is the panel's and not the world's; and
        // here rather than in the frame hook, which is past the point gizmos are drawn from.
        _mover.Update();
        _mover.Draw();
    }
    catch (Exception e)
    {
        Fault("gui", e);
    }
}
```

- ImGui's frame is open, so `IsMouseClicked`, `GetMousePos` and `GetIO().WantCaptureMouse` are current.
- The panel is drawn first so a click on its checkbox is the panel's. A hook on `StarMapAfterOnFrame` would be past
  the point gizmos are drawn from.
- `OnFrameViewports` already built this frame's camera matrices, so the ray and the gizmos agree with the render.

See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Convert the cursor in two stages: subtract the viewport origin, then scale from viewport pixels to the camera's
   framebuffer pixels. Reject cursors outside the viewport, including the exclusive far edge:

```csharp
// src/KSACars/Sim/CursorAim.cs L35-L76
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

/// <summary>
/// The cursor in the camera's framebuffer coordinates: offset into the viewport, then scaled
/// from viewport pixels into framebuffer pixels.
/// </summary>
/// <param name="framebufferWidth">Width the camera unprojects against, which is what matters.</param>
/// <param name="framebufferHeight">Height the camera unprojects against.</param>
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

2. Unproject through the visible viewport that contains the cursor and lift the origin with `EgoToEcl`:

```csharp
// src/KSACars/Ksa/KsaWorld.Pointing.cs L67-L106
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

3. Pin both conversions down with tests that never touch the game. The scale test is the interesting one: an 800x600
   viewport rendered at 1600x1200 must keep the centre at the centre and map the far corner to the far corner:

```csharp
// tests/KSACars.Tests/CursorAimTests.cs L106-L130
[Fact]
public void TheCursorIsScaledIntoFramebufferPixels()
{
    // A 800x600 viewport rendered at 1600x1200: the centre must stay the centre, and the
    // far corner must reach the far corner of the framebuffer rather than its midpoint.
    Assert.True(CursorAim.TryToFramebuffer(new float2(400, 300), new float2(0, 0),
                                           800, 600, 1600, 1200, out float2 centre));
    Assert.Equal(800f, centre.X, 3);
    Assert.Equal(600f, centre.Y, 3);

    Assert.True(CursorAim.TryToFramebuffer(new float2(799, 599), new float2(0, 0),
                                           800, 600, 1600, 1200, out float2 corner));
    Assert.Equal(1598f, corner.X, 3);
    Assert.Equal(1198f, corner.Y, 3);
}

/// <summary>The offset still has to happen; the scale is on top of it, not instead.</summary>
[Fact]
public void TheViewportOffsetStillApplies()
{
    Assert.True(CursorAim.TryToFramebuffer(new float2(1000, 400), new float2(600, 100),
                                           800, 600, 800, 600, out float2 local));
    Assert.Equal(400f, local.X, 3);
    Assert.Equal(300f, local.Y, 3);
}
```

4. Gate the tool so it does not eat panel clicks and so each press counts once. The tool keeps a trace so a click that
   never arrives can be diagnosed:

```csharp
// src/KSACars/Ksa/CraftMover.cs L68-L110
public void Update()
{
    if (!Enabled)
    {
        Release();
        return;
    }

    if (!KsaWorld.IsAlive(_held)) _held = null;
    if (!KsaWorld.IsAlive(_hovered)) _hovered = null;

    bool wantCapture = ImGui.GetIO().WantCaptureMouse;
    // Shift is the designate gesture, so a shift-click is not a click on the world.
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

5. Use the ray where it matters. When the cursor is over a craft, the tool uses that craft's own footing instead of the
   ray, because a ray through a vehicle's middle carries on and meets the ground behind it:

```csharp
// src/KSACars/Ksa/CraftMover.cs L194-L214
// Where the next click would put the held craft.
//
// A craft under the pointer answers with its own footing rather than with the ray: a ray
// through a vehicle's middle carries on and meets the ground behind it, so aiming at one and
// taking the ray lands a vehicle-height's worth of parallax past it. Aiming at the held craft
// itself therefore leaves it exactly where it is, which is what clicking it twice should do.
private bool TryTarget(out double3 groundEcl, out double latitudeDeg, out double longitudeDeg,
                       out string bodyName)
{
    // The hover is already worked out this frame; asking again would project every craft
    // twice more per frame for the same answer.
    if (_hovered is { } over
        && KsaWorld.TryCraftSurfacePoint(over, out groundEcl, out latitudeDeg,
                                         out longitudeDeg, out bodyName))
    {
        return true;
    }

    return KsaWorld.TryCursorGroundPoint(out groundEcl, out latitudeDeg, out longitudeDeg,
                                         out bodyName);
}
```

   Picking the craft and placing it are covered in
   [pick objects under the cursor](../pick-objects-under-the-cursor/overview.md).

## Complete example

| File | Role |
|---|---|
| [src/KSACars/Ksa/KsaWorld.Pointing.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.Pointing.cs#L67-L106) | `TryCursorRayEcl` |
| [src/KSACars/Sim/CursorAim.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/CursorAim.cs#L35-L76) | `TryToViewport`, `TryToFramebuffer` |
| [src/KSACars/Ksa/CraftMover.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/CraftMover.cs#L68-L110) | Click gate and frame update |
| [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs#L88-L110) | Hook order |
| [tests/KSACars.Tests/CursorAimTests.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tests/KSACars.Tests/CursorAimTests.cs#L106-L130) | Scale and offset tests |

## Variations and gotchas

- **Offset and scale are separate bugs.** With only the offset right the error is zero at the top-left and grows across
  the screen; it reads as "close, but not under the pointer".
- **Mouse pixels are not framebuffer pixels** whenever a render scale or display scale is active; today the two are equal
  by construction, which is why the scale is insurance rather than a correction.
- **Submit gizmos from the GUI hook.** Gizmos submitted from `StarMapAfterOnFrame` are cleared before they are drawn.
- **`TeleportToLocation` is not a per-frame call.** Each teleport rebuilds the craft's orbit and velocity, so the tool
  places once per click.
- **Shift-click is ignored**, because shift is a "designate" gesture elsewhere in the author's tooling.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of cursor-to-world-ray](overview.md)
- [Approach 1: ScreenToEgoRay with viewport offset and framebuffer scale](cursor-to-world-ray-1.md)
- [Approach 2: viewport lookup and a projected round trip](cursor-to-world-ray-2.md)
- Mod feature: [Craft mover mouse pick and place](../../../../mods/ksacars/features/craft-mover-mouse-pick-place.md)

[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
