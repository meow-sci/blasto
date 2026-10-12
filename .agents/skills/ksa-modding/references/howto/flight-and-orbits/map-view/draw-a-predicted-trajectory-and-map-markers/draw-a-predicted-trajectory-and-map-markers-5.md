---
type: How-To
title: Draw CCI and body-fixed points through a click-through full-screen overlay window with per-viewport projection and fault isolation
description: Open a transparent, input-transparent full-screen ImGui window per overlay, project inertial or body-fixed points with the main camera and drop points behind it, break polylines at gaps, guarantee ImGui.End on any exit and isolate faults so one overlay cannot take the frame down
category: flight-and-orbits
group: map-view
topic: draw-a-predicted-trajectory-and-map-markers
approach: 5
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/guidance-panel-and-overlays]
techniques: [imgui-drawlist, imgui-window, world-to-screen-projection]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, render:imgui]
game_types:
  - KSA.Camera
  - KSA.Celestial
  - KSA.IParentBody
  - KSA.IGameViewport
  - KSA.CameraMode
  - KSA.Program
  - Brutal.ImGuiApi.ImGuiWindowFlags
tags: [map-view, overlay, click-through, projection, cci, ccf, polyline, fault-isolation, guidance]
---
Outcome: trajectory plans, target orbits, ground tracks and impact crosshairs that follow the planet in the 3D and map views, drawn on top of everything, that never intercept a mouse click, and that cannot crash the frame if one of them throws. KSA-AdvancedFlightComputer (AFC) draws its ascent, descent and boostback plans this way, one overlay window per plan.[Guidance panel and overlays](../../../../mods/ksa-advancedflightcomputer/features/guidance-panel-and-overlays.md)

Compare with [approach 3](draw-a-predicted-trajectory-and-map-markers-3.md), which paints on the background draw list (under every window) and anchors to a craft, and [approach 4](draw-a-predicted-trajectory-and-map-markers-4.md), which uses the viewport overlay list. This one uses a real ImGui window that covers the viewport, so its commands layer in ImGui window order, after the panel that was drawn first. The mechanics of such windows are in [draw a fullscreen click-through overlay](../../../ui/overlays/draw-a-fullscreen-click-through-overlay/overview.md).

Prerequisites: StarMap for `[StarMapAfterGui]`; points in the parent body's inertial frame (CCI) or its body-fixed frame (CCF).

## Background: how the game does it

- **Frames.** CCI is the parent's inertial frame, CCF is fixed to the rotating surface, ECL is the ecliptic. `IParentBody.GetCci2Ccf()` rotates CCI to CCF and `Celestial.GetBodyFixed2Ecl()` rotates CCF to ECL; `Astronomical.GetPositionEcl()` is the body's position.[^ksa-iparentbody] [^ksa-celestial] [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L41) [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L174) [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L367) See [understand KSA coordinate frames and epochs](../../flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md).
- **Camera.** `Program.GetMainCamera()` is the main viewport's camera. `Camera.EclToEgo(ecl)` and `EgoToClip(ego)` give a clip-space `float4`; a `W` at or below zero means the point is behind the camera. `EclToScreen(ecl, ignoreBehind)` returns pixels.[^ksa-camera] [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L631-L634) [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L283-L286)
- **Viewport size.** `IViewport.Width` and `Height` are the viewport's size in pixels.[^ksa-iviewport] [current/decomp/KSA/IViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IViewport.cs#L37-L39)
- **Flags.** `ImGuiWindowFlags.NoDecoration`, `NoInputs` and `NoBackground` make a window that is invisible and does not take mouse input.

## Lifecycle and timing

Everything draws in `frame:draw-ui` on the main thread from the `[StarMapAfterGui]` method, a postfix on `Program.OnDrawUiViewports`: after the frame's UI, between `ImGui.NewFrame` and `ImGui.Render`, and only when the HUD is drawn. The state it reads is the plan the guidance step solved earlier in `PrepareFrame` and committed vehicle state, which the running step does not touch until the next apply. The draw never writes simulated state; a click that should change a target is stored as a request for the next guidance step, because a write from here would be overwritten by the worker copy-back. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [flight-computer worker copy channels](../../../../mods/ksa-advancedflightcomputer/features/flight-computer-worker-copy-channels.md).

## Steps

1. **Hook once, skip the editor, isolate faults.** One `[StarMapAfterGui]` method calls a feature entry that returns in the vehicle editor and catches everything, logging once per exception type:

```csharp
// AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs L44-L59
    internal static void DrawGui()
    {
        // Guidance flies a craft, so its panel and overlays are flight UI and stay out of the editor. Guidance itself keeps running; only the drawing is skipped.
        if (!SharedVehicleHooks.GuidanceEnabled || Program.IsEditorOpen)
            return;

        try
        {
            GuidanceWindow.Draw(Program.MainViewport);
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce($"guidance-draw:{ex.GetType().Name}",
                $"[AFC] Guidance panel draw failed for '{Program.ControlledVehicle?.Id}': {ex}");
        }
    }
```

2. **Draw the panel first.** The per-frame entry draws the panel before anything that can throw, so an overlay fault cannot starve it, then draws only the overlays whose tab is on screen:

```csharp
// AdvancedFlightComputer/Features/Guidance/Ui/Window.cs L64-L65
        // FIRST. Everything below can throw, and GuidanceFeature.DrawGui catches the lot into one log line per fault - so anything drawn at the END of this method is starved by an unrelated fault upstream, and looks exactly like "my window doesn't work".
        DrawGuidancePanel(vehicle, orbit, parent, bodyRadius);
```

```csharp
// AdvancedFlightComputer/Features/Guidance/Ui/Window.cs L79-L88
        // World-space overlays (each its own full-screen window, drawn after the panel so they layer correctly). Each also no-ops unless its own toggle is on.
        if (ascentUi)
            DrawAscentOverlay(viewport, orbit, parent, bodyRadius);
        if (landingUi)
        {
            DrawGfoldOverlay(viewport, vehicle, orbit, parent);
            Draw6DofOverlay(viewport, parent);
        }
        if (boostbackUi)
            DrawBoostbackOverlay(viewport, vehicle, orbit, parent);
```

3. **Open a full-screen, click-through window and return its draw list.** The flags make the window invisible and input-transparent; its size is the viewport's:

```csharp
// AdvancedFlightComputer/Features/Guidance/Ui/Overlays/OverlayCore.cs L25-L38
    private const ImGuiWindowFlags OverlayFlags =
        ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus |
        ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoInputs |
        ImGuiWindowFlags.NoBackground;

    // Open a full-screen, click-through overlay window and hand back its draw list. The caller holds a WindowEnd for the rest of its draw, so the window ends however that draw exits.
    private static ImDrawListPtr BeginOverlayWindow(IGameViewport vp, string name)
    {
        ImGui.SetNextWindowPos(new float2(0f, 0f));
        ImGui.SetNextWindowSize(new float2(vp.Width, vp.Height));
        ImGui.Begin(name, OverlayFlags);
        return ImGui.GetWindowDrawList();
    }
```

4. **Guarantee `ImGui.End`.** ImGui keeps a window stack; a `Begin` without its `End` fails at the end of the frame, naming the wrong window. A `ref struct` with `Dispose` closes the window however the draw exits:

```csharp
// AdvancedFlightComputer/Features/Guidance/Ui/Overlays/OverlayCore.cs L40-L44
    // Ends the ImGui window it guards when its scope exits, an exception included. ImGui keeps a window stack, so a Begin left without its End does not fail where the fault is. It fails at the end of the frame as a missing End that names the wrong window.
    private ref struct WindowEnd
    {
        public void Dispose() => ImGui.End();
    }
```

   An overlay uses it as `ImDrawListPtr dl = BeginOverlayWindow(vp, "##ascent_overlay"); using WindowEnd end = default;` right after its gate checks.

5. **Cache the frame's projection context once.** One call per overlay fetches the camera and the three transforms, and refuses to draw when there is no camera or the parent is not a `Celestial`:

```csharp
// AdvancedFlightComputer/Features/Guidance/Ui/Overlays/OverlayCore.cs L46-L59
    // Set the per-frame projection context (camera + body transforms) used by TryProjectCci. False if there's no camera or the parent isn't a Celestial.
    private static bool SetupProjection(IParentBody parent)
    {
        if (parent is not Celestial body)
            return false;
        Camera cam = Program.GetMainCamera();
        if (cam == null)
            return false;
        _ovCam = cam;
        _ovBodyEcl = body.GetPositionEcl();
        _ovCci2Ccf = parent.GetCci2Ccf();
        _ovCcf2Ecl = body.GetBodyFixed2Ecl();
        return true;
    }
```

6. **Project CCI or CCF points and reject those behind the camera.** The chain is CCI to CCF to ECL (plus the body's ECL position) to screen. Ground-fixed things use the CCF form so they do not drift with the body's rotation; the clip test replaces the NaN that `EclToScreen` would return, because the call passes `ignoreBehind: false`:

```csharp
// AdvancedFlightComputer/Features/Guidance/Ui/Overlays/OverlayCore.cs L61-L79
    // Project a CCI point to screen pixels; false if it is behind the camera. The chain is CCI -> body-fixed -> ECL (+ body ECL position) -> screen.
    private static bool TryProjectCci(double3 cci, out float2 screen)
    {
        double3 ecl = _ovBodyEcl + cci.Transform(_ovCci2Ccf).Transform(_ovCcf2Ecl);
        float4 clip = _ovCam.EgoToClip(_ovCam.EclToEgo(ecl));
        if (clip.W <= 0.001f) { screen = default; return false; } // behind the camera
        screen = _ovCam.EclToScreen(ecl, false);
        return true;
    }

    // Project a BODY-FIXED point. Cheaper than routing through CCI (it skips the Ccf->Cci->Ccf round trip that would cancel out) and, more to the point, it is what anything glued to the ground should use: a CCF point does not move between frames just because time passed, so a marker drawn this way stays put on the terrain instead of drifting with the body's rotation.
    private static bool TryProjectCcf(double3 ccf, out float2 screen)
    {
        double3 ecl = _ovBodyEcl + ccf.Transform(_ovCcf2Ecl);
        float4 clip = _ovCam.EgoToClip(_ovCam.EclToEgo(ecl));
        if (clip.W <= 0.001f) { screen = default; return false; } // behind the camera
        screen = _ovCam.EclToScreen(ecl, false);
        return true;
    }
```

7. **Break polylines where points are missing.** A run of projected points is one batched `AddPolyline`; a rejected point ends the run, so a trajectory that goes behind the camera is two lines, not a line across the screen. A scratch array grows on demand and is never shrunk:

```csharp
// AdvancedFlightComputer/Features/Guidance/Ui/Overlays/OverlayCore.cs L81-L102
    // A run of BODY-FIXED points as one batched polyline. Same breaking rule as DrawCciPolyline; see TryProjectCcf for why a ground track wants this one.
    private static void DrawCcfPolyline(ImDrawListPtr dl, ReadOnlySpan<double3> points,
                                        ImColor8 col, float thick)
    {
        if (_ovPoly.Length < points.Length)
            _ovPoly = new float2[Math.Max(points.Length, _ovPoly.Length * 2)];

        int run = 0;
        for (int i = 0; i < points.Length; i++)
        {
            if (TryProjectCcf(points[i], out float2 s))
            {
                _ovPoly[run++] = s;
                continue;
            }
            if (run >= 2)
                dl.AddPolyline(_ovPoly.AsSpan(0, run), col, ImDrawFlags.None, thick);
            run = 0;
        }
        if (run >= 2)
            dl.AddPolyline(_ovPoly.AsSpan(0, run), col, ImDrawFlags.None, thick);
    }
```

8. **Make text readable over anything.** There is no depth test and the overlay draws over terrain, ocean and cloud, so every label gets a one-pixel black shadow:

```csharp
// AdvancedFlightComputer/Features/Guidance/Ui/Overlays/OverlayCore.cs L104-L109
    // Overlay text with a drop shadow. ImGui has no outlined text and the overlays draw over terrain, cloud and ocean without a depth test, so plain coloured text is routinely unreadable against a bright background. Drawing it black first, offset by a pixel, costs one extra AddText and makes it legible over anything.
    private static void OvText(ImDrawListPtr dl, float2 at, ImColor8 col, string text)
    {
        dl.AddText(at + new float2(1f, 1f), new ImColor8(0, 0, 0, 200), text);
        dl.AddText(at, col, text);
    }
```

9. **Gate large overlays by camera mode.** The ascent overlay draws a target orbit tens of thousands of kilometres across; from the flight camera it would be lines sweeping over the craft, so it draws only in the map view, using `viewport.Mode`:

```csharp
// AdvancedFlightComputer/Features/Guidance/Ui/Overlays/AscentOverlay.cs L78-L96
    private static void DrawAscentOverlay(IGameViewport vp, Orbit orbit, IParentBody parent,
                                          double bodyRadius)
    {
        // MAP VIEW ONLY. The target orbit is a full ellipse tens of thousands of km across and the trace is the whole flown arc: from the flight camera they project to lines sweeping across the screen, over the vehicle you are trying to fly. There is one camera, not a separate map camera, so the mode is the only thing distinguishing the two.
        // The target orbit, the plan and the trace are all the CAT-S / UPFG ascent's.
        if (vp.Mode != CameraMode.Map || !_showAscentOverlay || _s.AscentMethod != AscentMethod.CatsUpfg
            || !SetupProjection(parent))
            return;

        ImDrawListPtr dl = BeginOverlayWindow(vp, "##ascent_overlay");
        using WindowEnd end = default;

        var targetCol = new ImColor8(90, 225, 255);   // cyan  - target orbit
        var traceCol = new ImColor8(255, 60, 220);    // magenta - flown so far

        DrawTargetOrbit(dl, orbit, parent, bodyRadius, targetCol);
        DrawConvexPlan(dl, orbit, parent);
        DrawTrace(dl, traceCol);
    }
```

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Ui/Overlays/OverlayCore.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/Overlays/OverlayCore.cs) | Window, projection, polyline and text helpers |
| [AdvancedFlightComputer/Features/Guidance/Ui/Overlays/AscentOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/Overlays/AscentOverlay.cs#L78-L96) | Map-view-only ascent overlay |
| [AdvancedFlightComputer/Features/Guidance/Ui/Overlays/BoostbackOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/Overlays/BoostbackOverlay.cs#L395-L460) | Impact crosshair, ground track and miss line in body-fixed points |
| [AdvancedFlightComputer/Features/Guidance/Ui/Window.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/Window.cs#L55-L98) | Per-frame entry that draws the panel then the overlays |
| [AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs#L44-L59) | `DrawGui` with editor skip and fault isolation |

## Variations and gotchas

- **The cached camera is the main camera.** The overlays project through `Program.GetMainCamera()`, so they draw correctly in the main viewport only; a secondary viewport would need its own `viewport.GetCamera()` and context. Approach 4 shows the per-viewport form.
- **No depth test.** Lines draw over terrain and planets, so the far side of an orbit stays visible; if a feature must be hidden behind the planet you have to cull it yourself.
- **Missing `End` is a late, misleading error.** ImGui reports it at the end of the frame against the wrong window. Use a disposable or `finally` for every `Begin`.
- **Do not step ImGui's id counter.** `ImGuiHelper` keeps a global widget id counter that stock windows reset each frame; incrementing it from mod code collides with them.
- **Cost.** Each overlay is one `Begin`/`End` pair plus a few batched polylines; the plan arrays are scratch buffers reused across frames.

## See also

- [Overview](overview.md); [Projected markers and arrows](draw-a-predicted-trajectory-and-map-markers-4.md); [Force or hide a body's orbit line](draw-a-predicted-trajectory-and-map-markers-6.md)
- [Draw a fullscreen click-through overlay](../../../ui/overlays/draw-a-fullscreen-click-through-overlay/overview.md)
- [Fly closed-loop ascent guidance](../../guidance/fly-closed-loop-ascent-guidance/overview.md)
- [Fly a boostback and glide to a landing site](../../guidance/fly-a-boostback-and-glide-to-a-landing-site/overview.md)
- [Project world positions to the screen](../../../rendering/overlays/project-world-positions-to-screen/overview.md)
- [Guidance panel and overlays (mod feature)](../../../../mods/ksa-advancedflightcomputer/features/guidance-panel-and-overlays.md)

[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-iviewport]: KSA.IViewport (decompiled, build 2026.10.10.5554)
