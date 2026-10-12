---
type: Mod Feature
title: World-anchored screen markers on full-screen ImGui overlay windows
description: Brackets, lock cues, chase brackets and part-health bars are projected through the viewport camera's EclToScreen/EgoToScreen onto transparent, input-less, full-screen ImGui windows, with off-screen targets clamped to the screen edge from the camera basis, not the projection
mod: ksarmory
category: ui
techniques: [starmap-lifecycle, imgui-window, imgui-drawlist, world-to-screen, edge-clamp]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Camera
  - KSA.Program
  - KSA.ViewportRegistry
  - KSA.IViewport
  - KSA.IGameViewport
  - KSA.Vehicle
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImDrawListExtensions
tags: [imgui, overlay, hud, markers, projection, camera, edge-indicator, lock-cue, draw-list]
---

KSArmory puts four kinds of marker "on the glass" over things in the 3D world, all at a constant
pixel size so a distant target is never a sub-pixel dot and terrain never hides the mark:

| Overlay | What it marks | Switch |
|---|---|---|
| `Markers` | Every craft carrying a weapon system: four-corner bracket, crossed when behind a planet, edge arrow when off-screen; hover or pin for a clickable name and range label | `Config.DrawSystemMarkers` (on) |
| `LockCueOverlay` | The contact the trigger's weapon has locked: brackets that close as dwell builds, red with the hold reason when fire control refuses, a diamond when clear to fire; edge caret when off-screen | `Config.DrawLockCue` (on) |
| `ChaseHud` | What a chased round is flying at: brackets that grow over the flight, plus range in metres | while a round is being chased |
| `PartHealthBars` | Every part's remaining health as a bar with a percentage | `Config.DrawPartHealth` (off; debug) |

The technique is the same in all four: project a world position through the viewport's
`KSA.Camera`, then draw lines, triangles, rects and text on the draw list of a transparent,
input-less ImGui window that covers the whole main viewport.[^ksarmory-markers][^ksarmory-lockcueoverlay][^ksarmory-config]

## How it works

**The overlay window.** Each overlay opens its own window sized to `ImGui.GetMainViewport()` with
`SetNextWindowBgAlpha(0)` and flags `NoDecoration | NoInputs | NoNav | NoFocusOnAppearing |
NoBringToFrontOnFocus | NoSavedSettings | NoBackground`, then draws with `ImGui.GetWindowDrawList()`.
It is the **window** draw list, not the background or foreground list. `NoInputs` lets clicks fall
through to the game; hover is computed from `ImGui.GetMousePos()` against the projected point
(18 px radius for markers).

**Clickable labels.** Because the overlay ignores input, a marker's label is a separate small
window (`##KSArmoryLabel_{name}`, `AlwaysAutoResize | NoMove | NoDecoration ...`) placed beside the
bracket and clamped on screen, with a `[ ]`/`[x]` lock button. Label windows are submitted after the
overlay's `End()` because ImGui windows do not nest; hovering the label extends its timer so the
pointer can reach the button.

**Projection** (all in `KsaWorld`, all wrapped in try/catch):[^ksarmory-ksaworld]

| Helper | Camera call | Off-screen behaviour | Used by |
|---|---|---|---|
| `TryProjectOrClamp(pointEcl)` | `Program.MainViewport.GetCamera().EclToScreen(p, ignoreBehind: true)` if ahead | Clamp to the edge along the bearing from `GetRightEcl`/`GetUpEcl` (`EdgeCue.TryOffset`, 28 px margin) | `Markers` |
| `TryProjectEgoOrClamp(index, posEgo)` | `EgoToScreen`; re-bases Ego through Ecl if the viewport camera is not the main camera | Same edge clamp | `LockCueOverlay` |
| `TryProjectIntoViewport(index, pointEcl)` | `EclToScreen(p, ignoreBehind: false)`; uses `BaseCamera` for fixed-mode viewports | Refuses | `ChaseHud` |
| `TryProjectEgo(posEgo)` | `EgoToScreen(p, ignoreBehind: true)` | Returns off-screen coordinates for the draw list to clip | `PartHealthBars` |

Every result is the camera's viewport-local pixel plus `viewport.Position`, so it lands in ImGui's
absolute screen space. Viewports come from `ViewportRegistry.GameViews`; the main one is found by
reference against `Program.MainViewport`, not by index 0.

**Why the edge clamp uses the camera basis.** A projection matrix maps a point behind the camera to
the opposite side of the screen, so an arrow built from it points the wrong way. `EdgeCue.TryOffset`
takes the target direction's components along the camera's right and up, normalises, and scales to
the inset screen rectangle.[^ksarmory-edgecue]

**Drawn position vs simulated position.** The lock cue projects `IContact.TryDrawEgo`, not
`Track.PositionEcl`: for a craft `Camera.GetPositionEgo(vehicle)`, which for a craft in the followed
craft's physics bubble is built from `KinematicStates.PositionPhys` rather than `GetPositionEcl`; for
a mod round the launcher's drawn position plus the round's offset. Per the mod, drawing the raw
position makes the bracket jump whenever warp changes.[^ksarmory-contact][^ksarmory-roundcontact][^ksa-camera]

**Lock cue math** (`LockCue`, `Sim/`, tested): `Phase(hasTrack, locked, clearToFire, held)` gives
None, Acquiring, Locked, Held or ClearToFire; `Acquisition = HeldSeconds / LockSeconds` clamped;
`Standoff = 1.6 → 1.0` linear in acquisition (linear on purpose, so half-closed means half-way);
`Reticle.Build` turns centre, size and standoff into line strokes.[^ksarmory-lockcue][^ksarmory-reticle]

**Marker culling.** No bracket when the craft subtends more than 0.03 rad (already plainly
visible); crossed and dimmed when `KsaWorld.IsOccluded` finds any body between camera and craft;
labels fade over the last 2 s of a 10 s show timer.

## Lifecycle and timing

All four are drawn from `KSArmoryMod.OnAfterGui` (`[StarMapAfterGui]`, Harmony postfix on
`Program.OnDrawUiViewports`, Program.cs L2268), after `StepOnce` and after `_ui.Draw()`, each
guarded by `KsaWorld.InFlight` and its config switch (KSArmoryMod.cs L582-L608).[^ksarmory-mod]

| Requirement | Where the frame provides it |
|---|---|
| Camera matrices for this frame | `OnFrameViewports` (Program.cs L2263, body L2608) calls each viewport's `OnFrame` before the UI pass |
| Legal ImGui calls | Between `PrepareImGui` (L2261) and `ImGui.Render()` (L2286) |
| Settled world state | After `PrepareFrame` applied the vehicle step (`Universe.ApplyVehicleSolvers`, Universe.cs L1922/L1948); positions are at `Universe.GetElapsedTime()` (L2399) |

The mod's own state (tracks, rounds, dwell) was advanced by this frame's `StepOnce` earlier in the
same hook, which is why the drawn-position pairing matters: the camera was built before the mod
stepped.[^ksa-program][^ksa-universe]

**When `Program.DrawUI` is false** (F2, or `ScreenshotCapture` for the duration of a capture),
`OnDrawUiViewports` is not called (Program.cs L2265-L2269), so none of these overlays are drawn;
the simulation still steps from the fallback hooks and the chase camera is still driven from
`StepOnce`, but its brackets disappear. The submission order inside `OnAfterGui` (panel first,
overlays after) is relied on by the mod to keep overlays above the scene and below the panel
(its comment at KSArmoryMod.cs L582-L584; ImGui's z-ordering itself not verified here).

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/Markers.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Markers.cs#L89-L175) | Overlay window, cull, project, bracket or edge arrow, hover, label queue |
| [src/KSArmory/Ksa/Markers.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Markers.cs#L228-L297) | Clickable label window with lock button |
| [src/KSArmory/Ksa/LockCueOverlay.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/LockCueOverlay.cs) | Drawn-position projection, phase colour, closing brackets, caret |
| [src/KSArmory/Ksa/ChaseHud.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ChaseHud.cs) | Brackets and range over a chased round's aim point, with drop shadow |
| [src/KSArmory/Ksa/PartHealthBars.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/PartHealthBars.cs) | Per-part bars from `PartHealth.World` |
| [src/KSArmory/Sim/LockCue.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/LockCue.cs) | Phase, acquisition, standoff, caret direction |
| [src/KSArmory/Sim/EdgeCue.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/EdgeCue.cs) | Edge position from a camera-basis bearing |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L3929-L3958) | `TryProjectIntoViewport` |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L4496-L4700) | `TryProjectAhead`, `TryProjectEgo`, `TryProjectOrClamp`, `TryProjectUnbounded`, `TryProjectEgoOrClamp` |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L3517-L3540) | `TryVehicleEgo`: drawn position from the render or main camera |

## Snippets

```csharp
// src/KSArmory/Ksa/Markers.cs L95-L118
        ImGuiViewportPtr main = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(main.Pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(main.Size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);

        // NoInputs: the overlay covers the screen, so anything else would swallow every click in
        // the game. Hovering is worked out from the pointer position instead.
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration
                                       | ImGuiWindowFlags.NoInputs
                                       | ImGuiWindowFlags.NoNav
                                       | ImGuiWindowFlags.NoFocusOnAppearing
                                       | ImGuiWindowFlags.NoBringToFrontOnFocus
                                       | ImGuiWindowFlags.NoSavedSettings
                                       | ImGuiWindowFlags.NoBackground;

        if (!ImGui.Begin("##KSArmoryMarkers", flags))
        {
            ImGui.End();
            return;
        }

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        float2 cursor = ImGui.GetMousePos();
        double3 eye = KsaWorld.CameraPositionEcl();
```

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L4559-L4595
    public static bool TryProjectOrClamp(double3 pointEcl, out float2 screen, out bool inView)
    {
        screen = default;
        inView = false;
        try
        {
            if (Program.MainViewport is not { } viewport) return false;
            if (viewport.GetCamera() is not { } camera) return false;

            int w = viewport.Width, h = viewport.Height;
            if (w <= 0 || h <= 0) return false;

            double3 toTarget = pointEcl - camera.EgoToEcl(Vec.Zero);
            bool ahead = Vec.Dot(toTarget, camera.GetForwardEcl()) > 0.0;

            if (ahead)
            {
                float2 local = camera.EclToScreen(pointEcl, ignoreBehind: true);
                if (float.IsFinite(local.X) && float.IsFinite(local.Y)
                    && local.X >= 0f && local.Y >= 0f && local.X <= w && local.Y <= h)
                {
                    inView = true;
                    screen = new float2(viewport.Position.X + local.X, viewport.Position.Y + local.Y);
                    return true;
                }
            }

            // Off-screen or behind: bearing from the camera basis, then out to the edge.
            double right = Vec.Dot(toTarget, camera.GetRightEcl());
            double up = Vec.Dot(toTarget, camera.GetUpEcl());
            if (!EdgeCue.TryOffset(right, up, w, h, EdgeMargin, out double dx, out double dy)) return false;

            screen = new float2((float)(viewport.Position.X + w * 0.5 + dx),
                                (float)(viewport.Position.Y + h * 0.5 + dy));
            return true;
        }
```

```csharp
// src/KSArmory/Sim/EdgeCue.cs L18-L36
    public static bool TryOffset(double right, double up, double width, double height, double margin,
                                 out double dx, out double dy)
    {
        dx = 0.0;
        dy = 0.0;
        if (!double.IsFinite(right) || !double.IsFinite(up)) return false;
        if (Math.Abs(right) < 1e-9 && Math.Abs(up) < 1e-9) return false;

        double len = Math.Sqrt((right * right) + (up * up));
        double ux = right / len, uy = -up / len;

        double halfW = (width * 0.5) - margin, halfH = (height * 0.5) - margin;
        double scale = Math.Min(Math.Abs(ux) > 1e-9 ? halfW / Math.Abs(ux) : double.MaxValue,
                                Math.Abs(uy) > 1e-9 ? halfH / Math.Abs(uy) : double.MaxValue);

        dx = ux * scale;
        dy = uy * scale;
        return true;
    }
```

```csharp
// current/decomp/KSA/Camera.cs L339-L355
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public float2 EclToScreen(double3 posEcl, bool ignoreBehind = true)
	{
		double3 posEgo = EclToEgo(posEcl);
		return EgoToScreen(posEgo, ignoreBehind);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public float2 EgoToScreen(double3 posEgo, bool ignoreBehind = true)
	{
		if (ignoreBehind && double3.Dot(posEgo, GetForwardEcl()) < 0.0)
		{
			return new float2(float.NaN, float.NaN);
		}
		float3 positionNdc = EgoToNdc(posEgo);
		return NdcToScreen(positionNdc);
	}
```

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `Camera.EclToScreen`, `Camera.EgoToScreen` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L339-L355) | World to viewport pixels; `ignoreBehind` returns NaN behind the camera |
| `Camera.GetForwardEcl`, `GetRightEcl`, `GetUpEcl`, `EgoToEcl`, `EclToEgo` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L176-L229) | Ahead test, edge bearing, camera position, re-basing Ego between cameras |
| `Camera.GetPositionEgo(IPosition)` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L236-L249) | Drawn (bubble-relative) position of a craft |
| `Program.MainViewport`, `Program.GetMainCamera`, `Program.GetRenderCamera` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L484-L641) | Which camera projects (L484, L631, L641) |
| `ViewportRegistry.GameViews`, `ViewportRegistry.MainViewport` | [current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L50-L52) | Viewport list and main viewport[^ksa-viewportregistry] |
| `IViewport.Width`, `Height`, `Position`, `GetCamera` | [current/decomp/KSA/IViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IViewport.cs#L37-L51) | Bounds test and screen offset[^ksa-iviewport] |
| `ImGui.GetMainViewport`, `SetNextWindowPos/Size/BgAlpha`, `Begin`, `GetWindowDrawList`, `GetMousePos` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L5584-L5648) | Full-screen overlay window and its draw list[^brutal-imgui] |

## Pitfalls and version notes

- **Behind-camera points.** `EclToScreen(..., ignoreBehind: false)` mirrors a point behind the camera
  onto the screen; use `ignoreBehind: true` (NaN) or test against `GetForwardEcl()` first, and take
  edge bearings from the camera basis.
- **Add the viewport origin.** `EclToScreen` is viewport-local; ImGui wants absolute screen pixels.
- **Ego is per camera.** An Ego position sampled against the main camera is wrong for another
  viewport's camera; `TryProjectEgoOrClamp` re-bases through Ecl.
- **Simulated vs drawn position.** A craft's `GetPositionEcl` and where its mesh is drawn can differ
  by metres; overlay over the drawn position (`Camera.GetPositionEgo`).
- **Do not use a full-screen window without `NoInputs`.** It would swallow every click in the game;
  put clickable parts in their own small windows.
- **The overlays are UI**: they vanish with F2 and in screenshots, unlike the gizmo-based world
  overlays (see gizmo world overlays), which the mod uses for diagnostics instead.
- **Static label state holds `Vehicle` references.** `Markers.Forget()` is called on save load
  (`ForgetTheWorld`) so destroyed craft are not kept alive.

## See also

- [Gizmo world overlays](./gizmo-world-overlays.md)
- [Radar search cone, masking and lock dwell](./radar-search-and-lock.md)
- [Fire control and target allocation](./fire-control-and-target-allocation.md)
- [Chase camera on a round](./chase-camera-on-a-round.md)
- [Gunner sight overlay](./gunner-sight-overlay.md)
- [Blast damage and part health](./blast-damage-and-part-health.md)
- [Cursor-ray world click tools](./cursor-ray-world-click-tools.md)
- [Operator panel in ImGui](./operator-panel-imgui.md)
- [Ecliptic frames and epochs](./ecliptic-frames-and-epochs.md)
- [Constant-pixel-size brackets, cues and bars projected from craft and contact positions](../../../howto/rendering/overlays/draw-markers-and-annotations-on-world-objects/draw-markers-and-annotations-on-world-objects-1.md) - how-to article demonstrating this feature
- [Body-fixed or orbit-relative anchors lifted to ecliptic, then Camera.EclToScreen](../../../howto/rendering/overlays/project-world-positions-to-screen/project-world-positions-to-screen-1.md) - how-to article demonstrating this feature
- [Edge cue from the camera basis (bearing), not the projection](../../../howto/rendering/overlays/project-world-positions-to-screen/project-world-positions-to-screen-5.md) - how-to article demonstrating this feature
- [Full-screen overlay: a NoInputs, NoBackground window sized to the main viewport](../../../howto/ui/overlays/draw-a-fullscreen-click-through-overlay/draw-a-fullscreen-click-through-overlay-1.md) - how-to article demonstrating this feature

[^ksarmory-markers]: src/KSArmory/Ksa/Markers.cs
[^ksarmory-lockcueoverlay]: src/KSArmory/Ksa/LockCueOverlay.cs
[^ksarmory-config]: src/KSArmory/Sim/Config.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-edgecue]: src/KSArmory/Sim/EdgeCue.cs
[^ksarmory-contact]: src/KSArmory/Ksa/Contact.cs
[^ksarmory-roundcontact]: src/KSArmory/Ksa/RoundContact.cs
[^ksarmory-lockcue]: src/KSArmory/Sim/LockCue.cs
[^ksarmory-reticle]: src/KSArmory/Sim/Reticle.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksa-camera]: KSA.Camera (decompiled)
[^ksa-program]: KSA.Program (decompiled)
[^ksa-universe]: KSA.Universe (decompiled)
[^ksa-viewportregistry]: KSA.ViewportRegistry (decompiled)
[^ksa-iviewport]: KSA.IViewport (decompiled)
[^brutal-imgui]: Brutal.ImGuiApi.ImGui (decompiled)
