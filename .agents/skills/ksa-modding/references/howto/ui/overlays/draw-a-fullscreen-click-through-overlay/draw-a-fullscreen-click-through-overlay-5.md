---
type: How-To
title: "Overlays on secondary and torn-off viewports: per-surface draw list and per-viewport ego transforms"
description: Resolve per frame which ImGui draw list reaches a camera picture, which camera projects into it, and re-base camera-relative positions between viewports
category: ui
group: overlays
topic: draw-a-fullscreen-click-through-overlay
approach: 5
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/gunner-sight-overlay]
techniques: [imgui-drawlist, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:viewports]
game_types:
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImDrawListPtr
  - Brutal.ImGuiApi.ImGuiViewportPtr
  - KSA.Camera
  - KSA.GameViewport
  - KSA.IGameViewport
  - KSA.IViewport
  - KSA.ViewportRegistry
tags: [overlay, draw-list, secondary-viewport, platform-viewport, ego, projection, reticle]
---
KSArmory paints a gunner's sight on the glass of whatever view an optical director is driving: the main view the sight camera has borrowed, or a camera window, possibly torn off onto a second monitor. The picture lives in a
different place each time, so each frame the sight resolves **which ImGui draw list reaches that picture** and **which camera projects into it**. This article covers both, plus re-basing positions between cameras.
It is the multi-surface counterpart of [approach 2](draw-a-fullscreen-click-through-overlay-2.md). Demonstrated by KSArmory.

## Background: how the game does it

- Every non-main game viewport is drawn by the game as an ImGui window (`GameViewport.DrawImGui`) whose image is an opaque `Image`; it records `ImGuiId = ImGui.GetWindowViewport().ID` (the **platform viewport** id) and
  `Position = ImGui.GetCursorScreenPos()` each frame, and draws the viewport's world UI inside that window.[current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L196-L262)[^ksa-gameviewport]
- The background draw list renders under every window, so a reticle on it would sit behind an opaque camera image; the **foreground list of the window's platform viewport** is the one that lands on top and follows a torn-off
  window onto its own monitor (ImGui gives it a platform viewport of its own).[current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8033-L8042)[^brutal-imguiapi-imgui]
- A camera's ego frame is relative to that camera, so a position sampled against the main camera is a *position* for that camera alone. `Camera.EgoToEcl` / `EclToEgo` are pure translations by `PositionEcl`, so re-basing is
  `otherCamera.EclToEgo(main.EgoToEcl(posEgo))`.[current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L222-L233)[^ksa-camera]
- `Camera.EgoToScreen(posEgo, ignoreBehind)` returns NaN for points behind the camera when asked; a projection mirrors a point behind the camera, so an off-picture cue has to be built from the camera's right and up
  vectors, not from the projection.[current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L347-L356)[^ksa-camera]

## Lifecycle and timing

The sight is painted from `[StarMapAfterGui]` (`frame:draw-ui`) after `StepOnce`: the main view's field of view and aim were set in `frame:viewports` (a fixed-controller pose source is re-solved in the viewport pass), and each
camera window's camera was aimed and its matrices rebuilt by `Camera.OnFrame` inside `StepOnce`, so the sight projects through the matrices the frame will render with. The draw data is final at `ImGui.Render`. With the HUD hidden the
GUI hook is skipped and the sight is not drawn. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Resolve the surface.** Main view: the background list over `ImGui.GetMainViewport()`. A window the mod draws itself: that window's list with a clip rectangle, between its `Begin` and `End`. A camera window KSA still draws:
   the foreground list of its platform viewport, clipped to the picture. Until the platform id is known, draw nothing; never fall back to the main view's list, or the reticle lands on the wrong monitor:

```csharp
// src/KSArmory/Ksa/SightSurface.cs L66-L72,L76-L109
public static bool TryFor(int index, out SightSurface surface)
{
    surface = default;
    if (index < 0) return false;

    try
    {
// ...
        if (index == KsaWorld.MainViewportIndex)
        {
            ImGuiViewportPtr main = ImGui.GetMainViewport();
            if (main.IsNull()) return false;

            surface = new SightSurface(ImGui.GetBackgroundDrawList(), main.Pos, main.Size,
                                       index, clipped: false);
            return true;
        }

        if (!KsaWorld.TryViewportPicture(index, out float2 pos, out float2 size,
                                         out uint imGuiId))
        {
            return false;
        }

        ImGuiViewportPtr platform = imGuiId == 0u ? default : ImGui.FindViewportByID(imGuiId);
        bool resolved = imGuiId != 0u && !platform.IsNull();

        Report(index, imGuiId, resolved, pos, size);

        // Nothing to paint on yet, which is where a camera window sits until ImGui has drawn
        // it once. One blank frame, and never the main view's list instead.
        if (!resolved) return false;

        ImDrawListPtr draw = ImGui.GetForegroundDrawList(platform);

        // Confines the sight to the picture. Without it the reference line, which is drawn
        // far past both edges on purpose, runs out across the desktop -- and where this
        // window is docked inside the game window, over whatever else is open there.
        draw.PushClipRect(pos, pos + size, false);

        surface = new SightSurface(draw, pos, size, index, clipped: true);
        return true;
```

2. **Draw in a mod-owned window** when the mod draws the camera image itself, so windows over it cover the sight as they cover the picture; always release the clip:

```csharp
// src/KSArmory/Ksa/SightSurface.cs L132-L151
/// <summary>
/// The picture in a window the mod draws itself, painted on that window's own list so the
/// windows over it cover the sight as they cover the picture. Call between its Begin and End.
/// </summary>
public static SightSurface InWindow(int index, float2 pos, float2 size)
{
    ImDrawListPtr draw = ImGui.GetWindowDrawList();
    draw.PushClipRect(pos, pos + size, true);

    return new SightSurface(draw, pos, size, index, clipped: true);
}

/// <summary>Releases the clip. Every path that took a surface must reach this.</summary>
public void Finish()
{
    if (!_clipped) return;

    try { Draw.PopClipRect(); }
    catch { /* an unbalanced clip is a cosmetic fault, and throwing here is the frame */ }
}
```

3. **Project through the surface's own camera.** For the main camera use `EgoToScreen`; for any other window's camera, re-base the ego position through the ecliptic first, then add that viewport's `Position`. Off the picture, place the
   cue on the edge from the camera's right and up, not from the projection:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L4651-L4706
public static bool TryProjectEgoOrClamp(int index, double3 posEgo, out float2 screen,
                                        out bool inView)
{
    screen = default;
    inView = false;
    try
    {
        if (!TryViewport(index, out IGameViewport viewport)) return false;
        if (viewport.GetCamera() is not { } camera) return false;

        int w = viewport.Width, h = viewport.Height;
        if (w <= 0 || h <= 0) return false;

        // Ego is measured from the camera that sampled it, and everything upstream samples
        // against the main one -- so it is a *position* for that camera alone. Handing it to
        // another window's camera displaces the bracket by however far the two cameras are
        // apart, which is a few degrees at a kilometre and unbounded when the player pulls
        // their view back. Re-based through Ecl, which is the frame both agree in.
        if (!ReferenceEquals(camera, Program.GetMainCamera()))
        {
            if (Program.GetMainCamera() is not { } sampled) return false;

            posEgo = camera.EclToEgo(sampled.EgoToEcl(posEgo));
            if (!Vec.IsFinite(posEgo)) return false;
        }

        // Ego is camera-relative, so the separation to the target *is* the position. The
        // basis is read in Ecl because Ego is a pure translation of it and the two agree
        // exactly for a direction.
        bool ahead = Vec.Dot(posEgo, camera.GetForwardEcl()) > 0.0;

        if (ahead)
        {
            float2 local = camera.EgoToScreen(posEgo, ignoreBehind: true);
            if (float.IsFinite(local.X) && float.IsFinite(local.Y)
                && local.X >= 0f && local.Y >= 0f && local.X <= w && local.Y <= h)
            {
                inView = true;
                screen = new float2(viewport.Position.X + local.X, viewport.Position.Y + local.Y);
                return true;
            }
        }

        double right = Vec.Dot(posEgo, camera.GetRightEcl());
        double up = Vec.Dot(posEgo, camera.GetUpEcl());
        if (!EdgeCue.TryOffset(right, up, w, h, EdgeMargin, out double dx, out double dy)) return false;

        screen = new float2((float)(viewport.Position.X + w * 0.5 + dx),
                            (float)(viewport.Position.Y + h * 0.5 + dy));
        return true;
    }
    catch
    {
        return false;
    }
}
```

4. **Draw each stroke twice**, a dark one offset by (1, 1) under the coloured one, so the sight reads over sky and terrain without a panel behind it.
5. **Make zoom a magnification**, not a field. `FovDegreesFor(base, mag) = 2 atan(tan(base / 2) / mag)`, clamped to 1-120 degrees: `Camera.SetFieldOfView` does not clamp and the projection throws for a field of zero or at least pi.
   `Camera.GetFieldOfView` returns radians while `SetFieldOfView` takes degrees [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L417-L421)[^ksa-camera][current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L790-L793)[^ksa-camera]; the projection throws for a field `<= 0` or `>= pi` [current/decomp/RenderCore/ReverseDepthBufferUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ReverseDepthBufferUtils.cs#L8-L13)[^rendercore-reversedepthbufferutils]. The mod's helper:

```csharp
// src/KSArmory/Sim/SightZoom.cs L67-L75
public static double FovDegreesFor(double baseFovDeg, double magnification)
{
    double half = double.DegreesToRadians(Sane(baseFovDeg)) * 0.5;
    double mag = Clamp(magnification);

    double fov = 2.0 * double.RadiansToDegrees(Math.Atan(Math.Tan(half) / mag));

    return !double.IsFinite(fov) ? Sane(baseFovDeg) : Math.Clamp(fov, MinFovDeg, MaxFovDeg);
}
```

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/SightSurface.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/SightSurface.cs) | Draw list, rectangle and clip per surface |
| [src/KSArmory/Ksa/Sight.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Sight.cs) | Every element of the sight |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L4651-L4706) | `TryProjectEgoOrClamp` |
| [src/KSArmory/Sim/SightZoom.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/SightZoom.cs) | Magnification to field, detents, apparent size |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs) | Where the sight is painted, main view then windows |

## Variations and gotchas

- **A list belonging to the wrong platform viewport draws on the wrong screen.** The platform id is what makes the sight follow a window onto a second monitor.
- **Clip the picture** (`PushClipRect(pos, pos + size, ...)`). The reference line is drawn far past both edges on purpose; without the clip it runs out across the desktop.
- **Do not use the main camera for another window's picture.** Handing ego positions to a different camera displaces the bracket by the camera separation; re-base through Ecl.
- **Use the drawn position, not the analytic one,** for brackets over a craft: `Camera.GetPositionEgo(vehicle)` is where KSA renders it; the two differ by metres on the ground, several pixels at 3 degrees of field.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of draw-a-fullscreen-click-through-overlay](overview.md)
- [Approach 2: ImGuiHelper.GetOverlayDrawList](draw-a-fullscreen-click-through-overlay-2.md)
- [Cursor to world ray, approach 2](../../input/cursor-to-world-ray/cursor-to-world-ray-2.md)
- [Aim a secondary viewport camera, rebuild its matrices and give it its nearby body](../../../camera/secondary-viewports/aim-a-secondary-viewport-camera/overview.md)
- [Lease a secondary viewport for a mod camera and show it in a window](../../../camera/secondary-viewports/borrow-a-secondary-viewport/overview.md)
- [Project world and sky positions to screen pixels and point at off-screen targets](../../../rendering/overlays/project-world-positions-to-screen/overview.md)
- Mod feature: [Gunner sight overlay](../../../../mods/ksarmory/features/gunner-sight-overlay.md)

[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^rendercore-reversedepthbufferutils]: RenderCore.ReverseDepthBufferUtils (decompiled, build 2026.10.10.5554)
