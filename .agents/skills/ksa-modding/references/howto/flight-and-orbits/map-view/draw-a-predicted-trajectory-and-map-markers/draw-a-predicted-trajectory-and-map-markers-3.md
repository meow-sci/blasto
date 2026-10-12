---
type: How-To
title: Draw a flown prediction as an ImGui polyline on the background draw list, anchored to the craft and broken behind the camera
description: Store a predicted path as offsets from the craft that was flown, anchor the draw to the camera's own view of that craft, project each point to the screen and draw two stacked ImGui lines per segment on the background draw list, breaking the line wherever a point is behind the camera
category: flight-and-orbits
group: map-view
topic: draw-a-predicted-trajectory-and-map-markers
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/guided-bomb-tail-kit-and-sight]
techniques: [imgui-drawlist, world-to-screen-projection, platform-relative-cache]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, render:imgui]
game_types:
  - KSA.Camera
  - KSA.Program
  - KSA.Vehicle
  - KSA.IViewport
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImDrawListPtr
tags:
  - trajectory-preview
  - bomb-sight
  - imgui-drawlist
  - background-draw-list
  - polyline
  - projection
  - ego-frame
  - anchor
---
Outcome: a predicted flight path (here, where a released bomb will fall) drawn as a two-layer line on top of the 3D scene that stays glued to the craft while the planet moves under it, never folds across the screen when part of it is behind the camera, and costs a few dozen draw calls. KSArmory's bomb sight draws its arc this way.[Guided bomb tail kit and sight](../../../../mods/ksarmory/features/guided-bomb-tail-kit-and-sight.md)

This is the do-it-yourself end of the topic: no `FlightPlan`, no stock orbit renderer, just a list of points and ImGui. Use it for a prediction that is not an orbit (a ballistic arc, a guidance preview) or when the game's line renderer cannot take your points. How the arc itself is flown is in [predict impact points](../../guidance/predict-impact-point-and-build-a-ballistic-computer/overview.md); how it is re-solved on a wall-clock budget is in [run an expensive solve without stalling the frame](../../guidance/run-an-expensive-solve-without-stalling-the-frame/overview.md).

Prerequisites: StarMap for `[StarMapAfterGui]`, and a list of ecliptic points you have already predicted.

## Background: how the game does it

- **Camera frames.** `Camera.EclToEgo(posEcl)` subtracts the camera's ecliptic position; `Camera.GetPositionEgo(IPosition)` returns the render-frame position of a vehicle, `-PositionCce` for the followed craft and the physics position for others in the same bubble. `EgoToScreen(posEgo, ignoreBehind = true)` returns `(NaN, NaN)` for a point behind the camera.[^ksa-camera] [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L229-L240) [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L347-L355)
- **Why not `GetPositionEcl`.** `Vehicle.GetPositionEcl` is the analytic on-rails position computed from the orbit's state vectors. A craft resting on the ground, or one in the physics bubble, is drawn at its physics position, and the two differ visibly. The mod therefore anchors to what the engine draws.
- **Background list.** `ImGui.GetBackgroundDrawList(ImGuiViewportPtr viewport = default)` returns the draw list that renders underneath every ImGui window; commands recorded on it during the UI pass are rendered by ImGui's draw data in the final pass after the composite (`render:imgui`).[^brutal-imguiapi-imgui] [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8033-L8036) [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4950)
- **Line primitives.** `AddLine(in float2, in float2, ImColor8, float thickness)` and `AddPolyline(ReadOnlySpan<float2>, ImColor8, ImDrawFlags, float)` are extension methods on `ImDrawListPtr`.[^brutal-imguiapi-imdrawlistextensions] [current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L51) [current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L276)

## Lifecycle and timing

The sight solves and draws from the `[StarMapAfterGui]` hook (a postfix on `Program.OnDrawUiViewports`, `frame:draw-ui`), after `PrepareFrame` has applied the finished physics step and before `ImGui.Render`, so the craft sample and the camera are both this frame's. The same hook is used for the solve and the draw on purpose: the stored offsets are paired with the platform sample the anchor uses. When the HUD is hidden, `OnDrawUiViewports` does not run, so the sight is not drawn at all (the solve continues from the `OnFrameCelestials` prefix). See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [draw UI while the HUD is hidden](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md).

## Steps

1. **Store the path as offsets from the platform, never as ecliptic points.** The prediction is measured once and drawn for a fifth of a second (the solve rate); ecliptic positions near a planet move at its orbital speed, about 29.8 km/s for Earth, so a held absolute path would drift kilometres between solves. Subtracting the craft's ecliptic position at the moment of the solve removes that:

```csharp
// src/KSArmory/Ksa/BombSightOverlay.cs L133-L139
        // Already in the ground's frame, so these are plain offsets from the craft.
        _path.Clear();
        for (int i = 0; i < _next.Count; i++) _path.Add(_next[i] - system.PlatformEcl);

        double flightSeconds = Math.Max(0, _next.Count - 1) * IntegrationStep;
        _impactOffset = impact - system.PlatformEcl;
        _solved = true;
```

   The offsets are relative to the craft rather than the ground so the top of the arc stays on the aircraft that would drop the bomb; the mod's comment explains the choice at length ([src/KSArmory/Ksa/BombSightOverlay.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BombSightOverlay.cs#L31-L60)).

2. **Anchor the draw pass to the engine's view of the craft.** `BeginDraw` pairs two samples taken at different instants on purpose: where the engine draws the craft this frame (`Camera.GetPositionEgo`) and the craft's ecliptic position at the instant the geometry was measured. It prefers `Program.GetRenderCamera()` over the main camera and fails closed, so a caller that forgets it draws nothing:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L3438-L3470
    public static bool BeginDraw(Vehicle anchor, double3 anchorEcl)
    {
        _anchored = false;
        if (!IsAlive(anchor)) return false;

        try
        {
            // The camera the frame will actually be rendered with, not the main viewport's.
            Camera camera = Program.GetRenderCamera() ?? Program.GetMainCamera();
            if (camera is null) return false;

            // See DrawAnchor for why these are sampled at different instants, and why
            // collapsing them into one puts the whole overlay beside the craft.
            //
            // GetPositionEgo, not EclToEgo. Its branching on what the camera follows is the
            // engine answering correctly per case — exact for the followed craft, physics-based
            // for others in its bubble — and it is the same call KSA renders vehicles with.
            // EclToEgo instead measures against camera.PositionEcl, which only agrees with the
            // rendered scene when the followed craft's analytic and physics positions coincide.
            // That holds for a landed launcher and fails once the camera follows something in
            // flight, which is when anything drawn to a vehicle stops lining up.
            _anchor = new DrawAnchor(camera.GetPositionEgo(anchor), anchorEcl);

            if (!_anchor.IsValid) return false;

            _anchored = true;
            return true;
        }
        catch
        {
            return false;
        }
    }
```

3. **Map each ecliptic point into the render frame.** `DrawAnchor` is a struct of two fields, `Ego` (where the platform is drawn this frame) and `Ecl` (its ecliptic position when the geometry was measured). Its single operation carries geometry from the older epoch onto the current render position, and it holds no KSA types so it can be unit-tested headlessly:

```csharp
// src/KSArmory/Sim/DrawAnchor.cs L34-L40
    /// <summary>The platform's ecliptic position at the instant the geometry was measured.</summary>
    public double3 Ecl { get; } = ecl;

    public bool IsValid => Vec.IsFinite(Ego) && Vec.IsFinite(Ecl);

    /// <summary>Maps an ecliptic position from the geometry's epoch into the render frame.</summary>
    public double3 ToEgo(double3 posEcl) => Ego + (posEcl - Ecl);
```

4. **Project to the screen and report a point behind the camera.** `TryProjectEgo` uses the main viewport's camera, accepts a point that is off screen (ImGui clips the line) and refuses only a non-finite result, which is what `EgoToScreen` returns behind the camera. `viewport.Position` is added so the result is in main-window coordinates:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L4525-L4543
    public static bool TryProjectEgo(double3 posEgo, out float2 screen)
    {
        screen = default;
        try
        {
            if (Program.MainViewport is not { } viewport) return false;
            if (viewport.GetCamera() is not { } camera) return false;

            float2 local = camera.EgoToScreen(posEgo, ignoreBehind: true);
            if (!float.IsFinite(local.X) || !float.IsFinite(local.Y)) return false;

            screen = new float2(viewport.Position.X + local.X, viewport.Position.Y + local.Y);
            return true;
        }
        catch
        {
            return false;
        }
    }
```

5. **Draw twice, breaking the line where a point is missing.** The arc is a list of `float2?`; a missing point leaves a gap rather than a line folded across the screen. Two passes draw a 4 px dark edge then a 2 px amber line so it reads over sky and terrain; the arc is thinned to about 48 ribs first, because a line per 0.2 s of fall is hundreds of draws on a high drop:

```csharp
// src/KSArmory/Ksa/BombSightOverlay.cs L221-L247
    private void DrawArcOnScreen(double3 here, int stride)
    {
        _screen.Clear();

        for (int i = 0; i < _path.Count; i += stride) AddScreenPoint(here + _path[i]);
        AddScreenPoint(here + _path[^1]);

        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
        for (int pass = 0; pass < 2; pass++)
        {
            uint colour = pass == 0 ? ArcEdge : ArcInk;
            float width = pass == 0 ? 4f : 2f;

            for (int i = 1; i < _screen.Count; i++)
            {
                if (_screen[i - 1] is { } a && _screen[i] is { } b) draw.AddLine(a, b, colour, width);
            }
        }
    }

    // A point behind the camera breaks the line there rather than folding it across the screen.
    private void AddScreenPoint(double3 pointEcl)
        => _screen.Add(KsaWorld.TryEclToEgo(pointEcl, out double3 ego) && KsaWorld.TryProjectEgo(ego, out float2 at)
                           ? at
                           : null);

    private readonly List<float2?> _screen = [];
```

```csharp
// src/KSArmory/Ksa/BombSightOverlay.cs L249-L250
    private static readonly uint ArcInk = ImGui.ColorConvertFloat4ToU32(new float4(1.0f, 0.75f, 0.15f, 1f));
    private static readonly uint ArcEdge = ImGui.ColorConvertFloat4ToU32(new float4(0f, 0f, 0f, 0.55f));
```

6. **Run the pieces in the right order from the hook.** `Draw` bails out when nothing was solved, anchors, re-bases the offsets onto *this* update's platform sample, draws the arc and queues the impact ring:

```csharp
// src/KSArmory/Ksa/BombSightOverlay.cs L261-L286
    public void Draw(WeaponSystem system)
    {
        if (!_solved || _path.Count < 2) return;
        if (system.Platform is not { } platform) return;
        if (!KsaWorld.BeginDraw(platform, system.PlatformEcl)) return;

        // Every rib would be a line per 0.2 s of fall, which is hundreds on a high drop and
        // unreadable. Thinning keeps the arc the same shape and a fraction of the cost.
        // Put back against *this* update's platform sample, which is the one BeginDraw anchored
        // to. Measured and drawn against the same sample, the difference is the offset exactly and
        // carries none of the motion between solves.
        double3 here = system.PlatformEcl;

        int stride = Math.Max(1, _path.Count / ArcRibs);

        DrawArcOnScreen(here, stride);

        // Painted on the terrain, so the ring reads as a place on the ground rather than a disc
        // floating over it.
        double3 impactEcl = here + _impactOffset;

        // The store's own lethal radius, so what the ring circles is what the bomb reaches.
        double radius = Warhead.LethalRadius(system.Munition.ChargeKg);

        GroundRings.Add(impactEcl, radius, radius * 0.15, PaintedRing);
    }
```

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/BombSightOverlay.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BombSightOverlay.cs#L221-L293) | Arc drawing and the draw entry point |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L3438-L3470) | `BeginDraw` anchor |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L4525-L4543) | `TryProjectEgo` |
| [src/KSArmory/Sim/DrawAnchor.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/DrawAnchor.cs) | The headless-testable anchor mapping |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L509-L553) | `[StarMapAfterGui]` hook that solves, then draws |
| [src/KSArmory/Ksa/PreRenderHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/PreRenderHook.cs#L22-L23) | Prefix on `Program.OnFrameCelestials` that keeps the solve running when the HUD is hidden |

## Variations and gotchas

- **Do not re-read the anchor at draw time.** The two samples are meant to be a frame apart; re-reading the craft's ecliptic position when drawing puts the whole overlay beside the craft by one frame of planetary motion (hundreds of metres at 60 fps near Earth). The source comments on `DrawAnchor` call this out.
- **Background list, not a window.** Lines on the background list render under every ImGui window, so a dialog opened over the sight hides it. The list is not clipped to a window, so no window needs to be open.
- **Pass the viewport explicitly if you can.** KSArmory calls `ImGui.GetBackgroundDrawList()` with the default argument. The stock helper `ImGuiHelper.GetOverlayDrawList` passes `ImGui.GetMainViewport()` for the main viewport, which is the unambiguous form (see [approach 4](draw-a-predicted-trajectory-and-map-markers-4.md)).
- **No depth test.** The line is painted over terrain and the craft; the arc is in the air above the ground it lands on, so this rarely shows.
- **Behind the camera is not a clamp.** A projection matrix maps a point behind the camera to the opposite side of the screen; refusing the point (NaN) is what keeps the line honest.
- **Rate-limit the solve in real time.** The solve behind the arc runs on a wall-clock interval (0.2 s, 2 s after an unreachable answer) so time warp cannot defeat it, and a failed solve keeps the last good arc.

## See also

- [Overview](overview.md); [Multi-pass orbit lines](draw-a-predicted-trajectory-and-map-markers-2.md); [Projected markers and arrows](draw-a-predicted-trajectory-and-map-markers-4.md)
- [Steer a falling store by its predicted impact](../../guidance/steer-by-zero-effort-miss/steer-by-zero-effort-miss-3.md)
- [Predict impact points and build a ballistic computer](../../guidance/predict-impact-point-and-build-a-ballistic-computer/overview.md)
- [Anchor and pick points on bodies](../anchor-and-pick-points-on-bodies/overview.md)
- [Project world positions to the screen](../../../rendering/overlays/project-world-positions-to-screen/overview.md)
- [Guided bomb tail kit and sight (mod feature)](../../../../mods/ksarmory/features/guided-bomb-tail-kit-and-sight.md)

[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imdrawlistextensions]: Brutal.ImGuiApi.ImDrawListExtensions (decompiled, build 2026.10.10.5554)
