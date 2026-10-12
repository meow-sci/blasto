---
type: Mod Feature
title: Gunner's sight painted over a borrowed view or a camera window
description: The gunner's sight is drawn with ImGui draw-list strokes over whichever view a director drives, picking the background, platform-viewport foreground or window list per surface, projecting through that view's camera and zooming by magnification
mod: ksarmory
category: ui
techniques: [imgui-drawlist, fov-magnification, per-surface-draw-list, world-to-screen-projection]
hooks:
  - starmap StarMapAfterGui
  - starmap StarMapAfterGui (via StepOnce)
  - subclass-override KSA.FixedController.OnFrame (field of view set from the pose source)
lifecycle: [frame:draw-ui, frame:viewports]
game_types:
  - KSA.Camera
  - KSA.GameViewport
  - KSA.IGameViewport
  - KSA.IViewport
  - KSA.ViewportRegistry
  - KSA.Program
  - RenderCore.ReverseDepthBufferUtils
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImDrawListExtensions
  - Brutal.ImGuiApi.ImDrawListPtr
tags:
  - sight
  - reticle
  - imgui
  - draw-list
  - overlay
  - fov
  - zoom
  - magnification
  - multi-monitor
  - horizon-reference
---

Whenever an optical director drives a view, KSArmory paints a gunner's sight on the glass of that
view: a boresight cross, a horizontal reference through the site, a bracket on the watched contact
that closes as the head settles, the gun's lead pipper sized to what a shell covers at that range, an
edge chevron when the contact is off the picture, the pod's warnings, the laser's state and spot,
and a status block with the zoom.[^ksarmory-sight] The view may be the main view the sight camera has
borrowed or a director's camera window, possibly torn off onto a second monitor, and the sight
resolves per frame which ImGui draw list reaches that picture and which camera projects into it.[^ksarmory-sightsurface]

Zoom is a **magnification**, turned into the field of view a camera is asked for, so the same
setting is the same instrument whatever field the player normally flies at.[^ksarmory-sightzoom]

## How it works

### Which surface (`SightSurface`)

| View | Draw list | Rectangle | Why |
|---|---|---|---|
| Main view | `ImGui.GetBackgroundDrawList()` | `ImGui.GetMainViewport()` | Under every window, so the panel stays readable over the sight |
| A director window the mod draws | `ImGui.GetWindowDrawList()` with `PushClipRect(pos, pos + size, true)`, between that window's `Begin` and `End` (`SightSurface.InWindow`) | The image's screen rectangle | Windows over it cover it as they cover the picture |
| A camera window KSA still draws (fallback) | `ImGui.GetForegroundDrawList(ImGui.FindViewportByID(imGuiId))`, clipped to the picture | `IViewport.Position` and size | The image is opaque, so the background list would sit behind it; the *platform viewport's* list follows a torn-off window onto its monitor |

The platform viewport id is what KSA's own window records each frame (`ImGuiId =
ImGui.GetWindowViewport().ID`, GameViewport.cs L220), and what the mod's window writes back through
`KsaWorld.PlaceViewportPicture`. Until it is known the sight draws nothing for that window rather
than falling back to the main view's list.[^ksarmory-sightsurface][^ksa-gameviewport][^ksarmory-directorwindow]
Every path ends in `SightSurface.Finish`, which pops the clip.[^ksarmory-sightsurface][^imgui-imgui]

### What is drawn (`Sight.Draw`)

| Element | How |
|---|---|
| Horizontal reference | `SightPicture.ReferenceArc`: nine points at zero elevation 30 km out, spanning the field either side of the look direction, projected with `KsaWorld.TryProjectUnbounded` (off-picture points kept, the draw list clips; points behind the camera dropped). An arc rather than a chord, broken at the middle so it never crosses the target[^ksarmory-sightpicture][^ksarmory-sight] |
| Boresight | Four ticks round the centre (gap 5 px, arm 9 px) |
| Drag ring | With mouse aim on the main view: a circle of the dead-zone radius and a line from its edge to the cursor, from the same numbers the head acts on |
| Target bracket | `Reticle.Build` at a fixed 11 px half-size, closed when the head is on target, standing off while it slews; green when settled and unmasked, amber otherwise. Placed at the craft's **drawn** position (for a window, as that window's camera draws it), projected with `KsaWorld.TryProjectEgoOrClamp`[^ksarmory-reticle][^ksarmory-ksaworld] |
| Off-picture | A chevron on the edge pointing the way the contact went, with its range in km |
| Range and closing | `Reticle.RangeAndClosing`, closing speeds under 0.5 m/s printed as 0 so the text does not flicker |
| Why the head is not on it | `OpticFollow.Why`: MOUSE AIM, AIMED BY HAND, WATCHING ELSEWHERE, NOT TRACKING, GIMBAL LIMIT or SLEWING, in the same precedence the head uses to choose its aim[^ksarmory-optichold] |
| Gun pipper | When fire control lays the ring on the gun's lead: the lead carried onto the drawn target as a separation, a ring sized by `SightZoom.ApparentPixels(lethal radius, range, field, height)` clamped to 6-90 px, the lead line from bracket to pipper and the time of flight |
| Warnings | `MASK` (own craft in the line of sight) and `GIMBAL LIMIT` under the crosshair, outside the symbology switch |
| Laser | `LASER <range>`, `NO RETURN` or `BLOCKED`, and a diamond where the spot lands |
| Status | Top left: system name, missiles, gun rounds, which weapon owns the ring; top right: `x<mag>` and the field in degrees read back from the camera |

Every stroke is drawn twice, a dark one offset by (1, 1) at 2.5 px under the coloured one at 1.6 px,
and text the same way, so the sight reads over sky and terrain without a panel behind it. The
primitives are `AddLine`, `AddCircle`, `AddCircleFilled`, `AddText` and `ImGui.CalcTextSize`.[^ksarmory-sight][^imgui-drawlist]

### Projecting through the surface's own camera

`KsaWorld.TryProjectEgoOrClamp(index, posEgo)` takes positions sampled against the main camera and,
for any other window's camera, re-bases them through the ecliptic
(`camera.EclToEgo(main.EgoToEcl(posEgo))`) before `Camera.EgoToScreen`, then adds the viewport's
`Position`. Off the picture it places the point on the edge from the camera's right and up, not from
the projection, because a projection mirrors a point behind the camera (`EgoToScreen` answers NaN
there when `ignoreBehind` is set, Camera.cs L347-L355).[^ksarmory-ksaworld][^ksa-camera]

### Zoom as magnification (`SightZoom`)

- `FovDegreesFor(base, mag) = 2 atan(tan(base / 2) / mag)`, clamped to 1-120 degrees: the optical
  relation, not a ratio of angles (a 25 degree field is 2.06x a 50 degree one).[^ksarmory-sightzoom]
- The base is the player's own field on the main view (`SightCamera` uses the field it recorded when
  it took the view) and KSA's default 50 degrees on a camera window (`Camera._fovRadians` starts at
  0.87266463 rad, Camera.cs L53).[^ksarmory-sightcamera][^ksa-camera]
- Detents 1, 2, 4, 8, 16, 32, 64 up to the optic's own maximum, plus the maximum itself: 16x for the
  EO director, 53x for the LITENING pod, whose 1 degree narrow field is 53x against 50 degrees and
  sits on the floor.[^ksarmory-sightzoom][^ksarmory-arsenal]
- The floor is not a preference: `Camera.SetFieldOfView` does not clamp, and `UpdateProjection` calls
  `CreatePerspectiveFieldOfViewReverseZ`, which throws for a field of zero or at least pi
  (ReverseDepthBufferUtils.cs L8-L13). The ceiling matches `Camera.ChangeFieldOfView`'s 120.[^ksa-camera][^ksa-reversedepth]
- Unit trap: `Camera.GetFieldOfView` returns radians, `SetFieldOfView` takes degrees (Camera.cs
  L417-L420, L790-L793); `KsaWorld.TrySetMainViewFov` and `ViewportFovRad` wrap the two directions.[^ksa-camera][^ksarmory-ksaworld]
- Controls: radio buttons per detent in the panel for the main view, `-`, `x1`, `+` and the wheel
  over a director window (`SightZoom.Stepped`).[^ksarmory-uioptic][^ksarmory-directorwindow]

### When it paints

`ViewClaim.SightPaints`: on the main view only while the sight holds the view and the chase does not
outrank it, so a yielded sight never leaves its bracket over a picture of something else; a camera
window is never contested.[^ksarmory-viewclaim][^ksarmory-mod]

## Lifecycle and timing

| Value | Where (build 5554) | What runs |
|---|---|---|
| `frame:viewports` | `OnFrameViewports` (Program.cs L2263): the fixed controller subclass asks `SightCamera.TryPose`, which answers the field with the pose; `KsaWorld.TrySetMainViewFov` is called there, before `Camera.OnFrame` builds the view-projection matrix (GameViewport.cs L116-L124) | Main view's field and aim for this frame[^ksarmory-levelhorizon][^ksarmory-sightcamera] |
| `frame:draw-ui` | `[StarMapAfterGui]` postfix on `OnDrawUiViewports` (L2268): `StepOnce` first (camera windows aimed, their field set, matrices rebuilt), then the panel, overlays and clicks, then `Sight.Draw` for the main view (KSArmoryMod.cs L615-L624) and for each driven window (L626-L645) | The painting itself[^ksarmory-mod] |

Painting after `StepOnce` in the same hook means the sight projects through the matrices the frame
will render with: the main view's were built this frame in the viewport pass (with the pose source
re-solved there), and a window's were rebuilt by `Camera.OnFrame` inside `StepOnce`. The draw data is
final at `ImGui.Render` and composited in the ImGui pass. With the HUD hidden KSA skips the GUI hook
and the sight is not drawn.[^ksarmory-mod][^ksa-program]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/Sight.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Sight.cs#L41-L416) | Every element of the sight |
| [src/KSArmory/Ksa/SightSurface.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/SightSurface.cs#L66-L151) | Draw list, rectangle and clip per surface |
| [src/KSArmory/Sim/SightPicture.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/SightPicture.cs) | Reference arc, edge direction, stable roll reference |
| [src/KSArmory/Sim/SightZoom.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/SightZoom.cs) | Magnification to field, detents, apparent size |
| [src/KSArmory/Sim/Reticle.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Reticle.cs) | Bracket strokes, range text |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L4018-L4066) | `MainViewFovDeg`, `TrySetMainViewFov` |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L4621-L4706) | `TryProjectUnbounded`, `TryProjectEgoOrClamp` |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L615-L645) | Where the sight is painted, main view then windows |

## Snippets

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

```csharp
// src/KSArmory/Ksa/SightSurface.cs L76-L109
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

```csharp
// src/KSArmory/Ksa/Sight.cs L55-L97
    public static void Draw(IOpticalHead system, OpticConfig policy, ISightPicture? weapon,
                            SightSurface surface)
    {
        if (system.OpticPart is null)
        {
            surface.Finish();
            return;
        }

        try
        {
            ImDrawListPtr draw = surface.Draw;
            float2 centre = surface.Centre;

            if (policy.Symbology)
            {
                DrawReferenceLine(surface, system);
                DrawBoresight(draw, centre);
            }
            // ...
            if (policy.MouseAim && policy.Viewport == KsaWorld.MainViewportIndex)
            {
                DrawDragIndicator(draw, policy, centre);
            }

            DrawTarget(surface, system, policy, weapon);
            // ...
            if (system.Masked) DrawWarning(surface, "MASK", 28f);
            if (system.AtGimbalLimit) DrawWarning(surface, "GIMBAL LIMIT", 46f);
            DrawLaser(surface, system, policy);

            if (policy.Symbology && weapon is not null) DrawStatus(surface, weapon, policy);
        }
        finally
        {
            surface.Finish();
        }
    }
```

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L4047-L4066
    public static bool TrySetMainViewFov(double degrees)
    {
        if (!double.IsFinite(degrees)) return false;

        try
        {
            if (Program.MainViewport?.GetCamera() is not { } camera) return false;

            float wanted = (float)Math.Clamp(degrees, SightZoom.MinFovDeg, SightZoom.MaxFovDeg);
            if (Math.Abs(double.RadiansToDegrees(camera.GetFieldOfView()) - wanted) < 1e-3) return true;

            camera.SetFieldOfView(wanted);
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"could not set the field of view: {e.Message}");
            return false;
        }
    }
```

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `ImGui.GetBackgroundDrawList`, `GetForegroundDrawList(ImGuiViewportPtr)`, `GetWindowDrawList`, `FindViewportByID`, `GetMainViewport`, `GetWindowViewport`, `CalcTextSize` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8033-L8040) | Choosing the list that reaches the picture |
| `ImDrawListExtensions.AddLine`, `AddCircle`, `AddCircleFilled`, `AddText`, `PushClipRect`, `PopClipRect` | [current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L10-L222) | The strokes |
| `Camera.EgoToScreen`, `EclToScreen`, `EclToEgo`, `EgoToEcl`, `GetRightEcl`, `GetUpEcl` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L176-L360) | Projection into the surface's camera |
| `Camera.SetFieldOfView`, `GetFieldOfView`, `ChangeFieldOfView`, `UpdateProjection` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L417-L485) | Zoom; degrees in, radians out; player keys clamp 15-120 |
| `ReverseDepthBufferUtils.CreatePerspectiveFieldOfViewReverseZ` | [current/decomp/RenderCore/ReverseDepthBufferUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ReverseDepthBufferUtils.cs#L8-L13) | Throws outside (0, pi): why the field is clamped |
| `GameViewport.ImGuiId`, `DrawImGui` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L193-L260) | Platform viewport id of a camera window |

## Pitfalls and version notes

- **One list per monitor.** With ImGui platform viewports a torn-off window gets its own platform
  viewport; a list belonging to another one renders on the wrong screen, and the background list of
  the main viewport sits behind an opaque `Image`.[^ksarmory-sightsurface]
- **Project through the surface's camera.** Ego positions are relative to the camera that sampled
  them; handing a main-camera Ego position to a window's camera displaces the bracket by the distance
  between the two cameras.[^ksarmory-ksaworld]
- **Bracket the drawn craft, not the simulated one.** Analytic and drawn positions differ by metres
  on the ground, which is tens of pixels at a few degrees of field.[^ksarmory-sight]
- **Restate the field every frame.** The player's zoom keys go through `ChangeFieldOfView`, which
  clamps to 15-120 degrees, so one keypress discards a magnified field silently.[^ksa-camera][^ksarmory-ksaworld]
- **Size icons in pixels, not apparent size.** A bracket sized to the target fills the screen as the
  target closes, which is when the sight is in use; only the pipper is sized to the world.[^ksarmory-reticle][^ksarmory-sight]
- **Main view uses `GetCamera()`.** `TrySetMainViewFov` reads `Program.MainViewport.GetCamera()`, which
  is the map camera in Map mode; the sight only paints while it holds the view in Fixed mode, so this
  does not arise in normal use (observed in code).[^ksarmory-ksaworld][^ksa-gameviewport]

## See also

- [Optical director heads](./optical-director-heads.md)
- [Director camera windows](./director-camera-window.md)
- [Borrow the main view and hand it back](./borrow-main-view-camera.md)
- [Chase camera riding a round](./chase-camera-on-a-round.md)
- [World-anchored screen markers](./world-anchored-screen-markers.md)
- [Fire control and target allocation](./fire-control-and-target-allocation.md)
- [Seekers, uplink and laser designation](./seekers-uplink-and-laser-homing.md)
- [Edge cue from the camera basis (bearing), not the projection](../../../howto/rendering/overlays/project-world-positions-to-screen/project-world-positions-to-screen-5.md) - how-to article demonstrating this feature
- [Overlays on secondary and torn-off viewports: per-surface draw list and per-viewport ego transforms](../../../howto/ui/overlays/draw-a-fullscreen-click-through-overlay/draw-a-fullscreen-click-through-overlay-5.md) - how-to article demonstrating this feature

[^ksarmory-sight]: src/KSArmory/Ksa/Sight.cs
[^ksarmory-sightsurface]: src/KSArmory/Ksa/SightSurface.cs
[^ksarmory-sightpicture]: src/KSArmory/Sim/SightPicture.cs
[^ksarmory-sightzoom]: src/KSArmory/Sim/SightZoom.cs
[^ksarmory-reticle]: src/KSArmory/Sim/Reticle.cs
[^ksarmory-optichold]: src/KSArmory/Sim/OpticHold.cs
[^ksarmory-viewclaim]: src/KSArmory/Sim/ViewClaim.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-sightcamera]: src/KSArmory/Ksa/SightCamera.cs
[^ksarmory-levelhorizon]: src/KSArmory/Ksa/LevelHorizonController.cs
[^ksarmory-directorwindow]: src/KSArmory/Ksa/Ui/DirectorWindow.cs
[^ksarmory-uioptic]: src/KSArmory/Ksa/Ui/UiOptic.cs
[^ksarmory-arsenal]: src/KSArmory/Sim/Arsenal.cs
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
[^ksa-reversedepth]: RenderCore.ReverseDepthBufferUtils (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^imgui-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^imgui-drawlist]: Brutal.ImGuiApi.ImDrawListExtensions (decompiled, build 2026.10.10.5554)
