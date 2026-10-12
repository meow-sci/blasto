---
type: How-To
title: "Cursor to world ray: ScreenToEgoRay with viewport offset and framebuffer scale"
description: Turn the ImGui cursor into an ecliptic ray through the visible viewport under it, ignoring clicks ImGui owns, and keep the tool working with the HUD hidden
category: ui
group: input
topic: cursor-to-world-ray
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/cursor-ray-world-click-tools]
techniques: [imgui-drawlist, starmap-lifecycle, harmony-prefix]
hooks: [starmap StarMapAfterGui, starmap StarMapAfterOnFrame, harmony-prefix KSA.Program.OnFrameCelestials]
lifecycle: [frame:draw-ui, frame:input, frame:after-on-frame]
game_types:
  - KSA.Camera
  - KSA.ViewportRegistry
  - KSA.IGameViewport
  - KSA.IViewport
  - KSA.Ray
  - KSA.Program
  - Brutal.ImGuiApi.ImGuiIOPtr
tags: [cursor, raycast, viewport, imgui, camera, click-tools, hud-hidden]
---
KSArmory lets the operator fire at, lock onto and detonate things by clicking in the 3D view. Every one of
those tools starts from one function that turns `ImGui.GetMousePos()` into a world ray through whichever
visible game viewport the cursor is over. This article is that function, the click rules around it and the
two hooks that call it so the tools keep working when the HUD is hidden. Demonstrated by KSArmory.
Prerequisite: a StarMap mod with a `[StarMapAfterGui]` method (see
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-1.md)).

## Background: how the game does it

- ImGui reports the cursor in screen coordinates that span every OS window; `Program.PrepareImGui` copies it into
  `Cursor.DesktopPosition` right after `ImGui.NewFrame` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2133-L2139)[^ksa-program].
- Each game viewport owns a `Camera`; `IViewport.GetCamera()` returns it, and `IViewport.Position`, `Width` and
  `Height` describe its rectangle in the same screen coordinates [current/decomp/KSA/IViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IViewport.cs#L37-L57)[^ksa-iviewport].
  `ViewportRegistry.GameViews` lists the game viewports [current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L50-L52)[^ksa-viewportregistry].
- `Camera.ScreenToEgoRay(float2)` divides by the camera's own `FramebufferSize` and returns a `Ray` whose
  perspective origin is `(0, 0, 0)` in the camera-relative "ego" frame [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L704-L713)[^ksa-camera].
  `Camera.EgoToEcl` adds `PositionEcl`, so ego axes are ecliptic axes and the direction needs no rotation
  [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L222-L227)[^ksa-camera]. The ego frame is explained in
  [approach 5](cursor-to-world-ray-5.md).
- The engine's own `Cursor.GetEgoRay(viewport)` does the subtraction but not the framebuffer scale, and cannot
  pick the viewport for you ([approach 4](cursor-to-world-ray-4.md)).

## Lifecycle and timing

This is `frame:draw-ui` and `frame:input` work on the main thread; see
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

- `PrepareImGui` runs before the `if (DrawUI)` gate, so `GetMousePos`, `IsMouseClicked` and `GetIO()` are valid
  every frame even when the HUD is hidden (F2).[^ksa-program]
- The camera was updated this frame in `OnFrameViewports`, before `StarMapAfterGui`, so the ray matches the
  picture the player sees.
- KSArmory calls `UpdateWorldClicks` from `OnAfterGui` (a `[StarMapAfterGui]` method) when the UI is drawn, and
  from the end of its simulation step (Harmony prefix on `Program.OnFrameCelestials` or `[StarMapAfterOnFrame]`)
  when it is not. Only the click handling moves to the step; markers are drawn only in the GUI pass.
  See [keep UI work running when the HUD is hidden](../../overlays/draw-ui-while-the-hud-is-hidden/overview.md) for the
  general pattern.

## Steps

1. Skip the click when ImGui owns it, when a reserved modifier is held and when it is not a fresh press. This
   is the whole gate of `Designator.Update`:

```csharp
// src/KSArmory/Ksa/Designator.cs L29-L52
public void Update(IManualFire system, SystemConfig policy)
{
    if (!policy.MouseFire) return;

    // A click on the panel is not a click on the world behind it.
    if (ImGui.GetIO().WantCaptureMouse) return;
    // Shift is the designate gesture, so a shift-click is not a click on the world.
    // Without this, locking a target while this tool is on also sends a round at it.
    if (ImGui.GetIO().KeyShift) return;

    if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left, repeat: false)) return;

    if (!KsaWorld.TryCursorGroundPoint(out double3 groundEcl, out _, out _, out _))
    {
        // Nothing under the cursor is not a reason to hold fire. A gun shoots where it is
        // pointing rather than at a named place, and the sky is where most of its targets are:
        // requiring a ground hit would leave the CIWS unable to fire at anything above the
        // horizon, which is the one thing a CIWS is for.
        if (system.TriggerArmament == ArmamentKind.Belt) system.FireBurst();
        return;
    }

    system.FireAt(Lifted(groundEcl, system));
}
```

2. Write the cursor-to-ray function. It walks the visible game viewports, converts the cursor to that camera's
   framebuffer pixels and unprojects:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L4113-L4152
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

   `CursorAim.TryToFramebuffer` subtracts the viewport's `Position`, rejects a cursor at or past the far edge
   (a cursor at exactly `width` unprojects to the frustum boundary) and scales viewport pixels to framebuffer
   pixels:

```csharp
// src/KSArmory/Sim/CursorAim.cs L60-L76
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

3. Use the result. `originEcl` and `directionEcl` are ecliptic; the origin is the camera position because a
   perspective ray starts at ego zero. The mod then asks "what does the ray hit": craft hulls through
   `Part.RayCastEgo`, ground by marching the height field. Those queries are covered in
   [pick objects under the cursor](../pick-objects-under-the-cursor/overview.md).
4. Call the click handler from both hooks so a hidden HUD does not disable it:

```csharp
// src/KSArmory/Ksa/KSArmoryMod.cs L407-L431
// Every tool that acts on a click in the world. Called from the UI pass when it runs and from the
// step when it does not, because a hidden UI stops only the pass: ImGui's frame, and with it the
// mouse, is read before KSA's DrawUI gate every frame. Its markers are drawn only with the UI.
private void UpdateWorldClicks()
{
    if (_roster is null || _ui is null) return;

    _mover.Update(_config);
    _bursts.Update(_config);

    // Only the system the panel is showing. Every crewed system reading the same cursor would
    // fire every launcher in the world at one click.
    if (_roster.For(_ui.Focused) is { } aimed) _designator.Update(aimed.Weapon, aimed.Policy);

    // Same rule, and the same reason: every crewed computer reading one cursor would re-aim every
    // missile in the world at a single click.
    if (_icbms?.For(_ui.Focused) is { } aimedSite) _sites.Update(aimedSite);

    // Shift-click locks the installation the panel is showing onto whatever is under the cursor
    // -- its turret, and its director if it has one -- scoped to the shown system for the same
    // reason the designator is: every crewed system reading one cursor would lock all of them.
    WeaponSystems.Entry? weapon = _roster.For(_ui.Focused);
    TargetLock.Update(weapon?.Weapon, _heads?.Driving(_ui.Focused)?.Head,
                      weapon is null ? null : (at, what) => _roster.DesignateWeapon(weapon, at, what));
}
```
```csharp
// src/KSArmory/Ksa/KSArmoryMod.cs L351-L355
    using (_budget.Measure("cameras")) DriveCameras(dtPlayer);

    // The UI pass reads clicks when it runs; with the UI hidden it does not run at all.
    if (!KsaWorld.UiDrawn && KsaWorld.InFlight) UpdateWorldClicks();
}
```

5. Scope the click to one consumer. Only the installation the panel is showing reads the cursor, because every
   crewed system reading one click would fire every launcher in the world.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L4113-L4152) | `TryCursorRayEcl` |
| [src/KSArmory/Sim/CursorAim.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/CursorAim.cs#L60-L76) | Viewport offset and framebuffer scale |
| [src/KSArmory/Ksa/Designator.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Designator.cs#L29-L52) | Click gate and use |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L407-L431) | Dispatcher shared by both hooks |

## Variations and gotchas

- **Screen versus framebuffer pixels.** Skipping the `Position` subtraction is right on one full-screen viewport
  and off by a constant everywhere else. The scale is insurance: in this build `GameViewport.SetSize` assigns
  the size and calls `Camera.Resize` on both cameras in the same statement, so the two are equal.[current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L262-L268)[^ksa-gameviewport]
- **Reject bad directions.** A degenerate projection returns zero or non-finite directions; normalising those
  yields NaN that reaches everything downstream (`CursorAim.IsUsableDirection`).
- **Camera bearing is not mount bearing.** Pointing something down the camera ray is exact only at infinity; take the
  point at range along the ray and the bearing from the mount (`CursorAim.TryAimFromMount`).
- **Shift is reserved.** Plain-click tools return on `KeyShift` so a lock gesture does not also fire.
- **The game still sees the click.** The mod does not consume the mouse button; whether KSA's own selection and hover
  handling also reacts was not checked. To swallow it see
  [share input between ImGui, mod tools and the game](../coordinate-imgui-focus-with-game-input/overview.md).
- **Version.** Checked against build 2026.10.10.5554; the mod was written against earlier builds and the members
  used are unchanged.

## See also

- [Overview of cursor-to-world-ray](overview.md)
- [Approach 2: viewport lookup and a projected round trip](cursor-to-world-ray-2.md)
- [Approach 3: framebuffer pixel conversion](cursor-to-world-ray-3.md)
- [Approach 4: Cursor.GetEgoRay](cursor-to-world-ray-4.md)
- [Approach 5: ego frame and construction plane](cursor-to-world-ray-5.md)
- Mod feature: [Cursor ray world click tools](../../../../mods/ksarmory/features/cursor-ray-world-click-tools.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-iviewport]: KSA.IViewport (decompiled, build 2026.10.10.5554)
[^ksa-viewportregistry]: KSA.ViewportRegistry (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
