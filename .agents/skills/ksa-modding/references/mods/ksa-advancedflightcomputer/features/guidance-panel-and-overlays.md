---
type: Mod Feature
title: A detachable console-window panel and world-space trajectory overlays
description: Draw a mod panel with the game's ConsoleStyle window skin unpinned from the main viewport, gauge-styled tab content, and click-through full-screen overlays that project CCI or body-fixed points through the main camera, all from StarMapAfterGui with fault isolation
mod: ksa-advancedflightcomputer
category: ui
techniques: [starmap-lifecycle, imgui-window, imgui-drawlist]
hooks: [starmap StarMapAfterGui, harmony-postfix KSA.Program.DrawProgramMenusHook]
lifecycle: [frame:draw-ui]
game_types: [KSA.ConsoleStyle, KSA.ImGuiHelper, KSA.ImGauge, KSA.Camera, KSA.Celestial, KSA.Program]
tags: [imgui, console-window, overlay, projection, camera, gauges, map-view]
---

The AFC Guidance panel is a stock-looking console window that moves, resizes, scrolls and closes like
the Transfer Planner and can be dragged out of the main game window into its own OS window. It holds
the EXECUTE / ABORT / RETARGET row, the tabs (Ascent, Boostback, Deorbit and land, Land from here) and a
RELEASE GUIDANCE footer. In the 3D and map views, overlays draw the planned trajectories, impact points
and target orbits on top of everything.

## How it works

### Entry and isolation

`Mod.DrawGui` is a `[StarMapAfterGui]` method (a postfix on `Program.OnDrawUiViewports`) that calls
`GuidanceFeature.DrawGui`: it skips the editor (`Program.IsEditorOpen`), and wraps the whole draw in
try/catch with a once-per-kind warning so a draw fault cannot end the frame
([AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L216-L218), [AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs#L43-L58)).
The panel starts hidden; it is opened from the AFC Guidance menu (a postfix on the game's empty
`Program.DrawProgramMenusHook`, see [guidance vehicle step loop](guidance-vehicle-step-loop.md)).

### The panel shell

`ConsoleStyle.BeginWindow(id, title, signature, ref open, size, flags, ImGuiCond.FirstUseEver, pinToMainViewport: false)`
gives the stock console skin; `pinToMainViewport: false` is what lets the window leave the main game
window ([AdvancedFlightComputer/Features/Guidance/Ui/Panel.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/Panel.cs#L46-L51), [current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L667)).[^ksa-consolestyle]
The body is plain ImGui inside `ConsoleStyle.PushWidgetStyle`, with `ImGuiHelper.BeginRegion` for
collapsible two-column sections. The commit buttons are drawn above the tab bar, so they act on the tab
selected last frame (one frame of lag, accepted so commit controls are not drawn below the content).

### Gauge-styled content

`GaugeKit` adds two-column rows for `double` values that `ImGuiHelper` lacks, with explicit ids:
`ImGuiHelper`'s own widget id counter is global and reset by `ImGuiHelper.StartFrame`, which stock windows
call, so incrementing it from a mod collides with them ([AdvancedFlightComputer/Features/Guidance/Ui/GaugeKit.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/GaugeKit.cs),
[current/decomp/KSA/ImGuiHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiHelper.cs#L21-L147)). `StatusBlock` draws schematics (arcs, bands, a staging bar) with the
window draw list; because `ImGauge` registers its draw callback at `BeginWindow`, draw-list commands
emitted afterwards land on top of the gauge chrome ([AdvancedFlightComputer/Features/Guidance/Ui/StatusBlock.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/StatusBlock.cs)).

### World-space overlays

- **Window.** `BeginOverlayWindow` opens a full-screen ImGui window with `NoDecoration | NoMove |
  NoSavedSettings | NoBringToFrontOnFocus | NoFocusOnAppearing | NoInputs | NoBackground` and returns its
  draw list; a `ref struct WindowEnd` guarantees `ImGui.End()` even on exceptions, because a missing End
  fails at the end of the frame naming the wrong window ([AdvancedFlightComputer/Features/Guidance/Ui/Overlays/OverlayCore.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/Overlays/OverlayCore.cs#L24-L45)).
- **Projection.** Per frame, `SetupProjection` caches `Program.GetMainCamera()`, the body's ECL position,
  CCI-to-CCF and CCF-to-ECL rotations. `TryProjectCci` maps CCI -> body-fixed -> ECL, rejects points behind
  the camera with `camera.EgoToClip(camera.EclToEgo(ecl)).W <= 0.001`, and gets pixels from
  `camera.EclToScreen`. Ground-fixed points use `TryProjectCcf` so they do not drift with the body's
  rotation. KSA has one camera for flight and map view, so the same projection works at every zoom.
- No depth test: lines draw over terrain and planets, so the far side of an orbit stays visible.
- Overlays: G-FOLD and 6-DOF descent plans, ascent plan and target orbit, boostback arcs and impact points.

## Lifecycle and timing

All drawing happens in `frame:draw-ui` on the main thread, in `StarMapAfterGui` (the postfix on
`Program.OnDrawUiViewports`): after `OnDrawUiFrame` and the secondary viewports, before
`OnDrawUiThreadSafe` draws the menu bar, gauges, `Universe.OnDrawUi` and the Transfer Planner, between
`ImGui.NewFrame` and `ImGui.Render`, and only while the HUD is drawn. Like every StarMap frame hook it
runs after `PrepareFrame` has dispatched the next physics step. The panel reads per-vehicle guidance
state written by the guidance step in the same frame's `PrepareFrame` and committed main-thread vehicle
state, which the running step does not touch until the next apply; clicks set requests that the next
guidance step consumes, because a flight-computer write from here would be overwritten by the worker
copy-back (see [flight-computer worker copy channels](flight-computer-worker-copy-channels.md)).

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Ui/Panel.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/Panel.cs) | Console-window shell, buttons, tabs |
| [AdvancedFlightComputer/Features/Guidance/Ui/Window.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/Window.cs) | Per-frame entry that draws panel and overlays |
| [AdvancedFlightComputer/Features/Guidance/Ui/GaugeKit.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/GaugeKit.cs) | Two-column rows with explicit ids |
| [AdvancedFlightComputer/Features/Guidance/Ui/StatusBlock.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/StatusBlock.cs) | Draw-list schematics over gauge chrome |
| [AdvancedFlightComputer/Features/Guidance/Ui/Overlays/OverlayCore.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/Overlays/OverlayCore.cs) | Overlay window and projection helpers |
| [AdvancedFlightComputer/Features/Guidance/Ui/Overlays/AscentOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/Overlays/AscentOverlay.cs) | Ascent plan and target orbit overlay |
| [AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs) | `DrawGui` with editor skip and fault isolation |

## Snippets

```csharp
// AdvancedFlightComputer/Features/Guidance/Ui/Overlays/OverlayCore.cs L61-L69
    // Project a CCI point to screen pixels; false if it is behind the camera. The chain is CCI -> body-fixed -> ECL (+ body ECL position) -> screen.
    private static bool TryProjectCci(double3 cci, out float2 screen)
    {
        double3 ecl = _ovBodyEcl + cci.Transform(_ovCci2Ccf).Transform(_ovCcf2Ecl);
        float4 clip = _ovCam.EgoToClip(_ovCam.EclToEgo(ecl));
        if (clip.W <= 0.001f) { screen = default; return false; } // behind the camera
        screen = _ovCam.EclToScreen(ecl, false);
        return true;
    }
```

## Game APIs touched

| Member | Use |
|---|---|
| `ConsoleStyle.BeginWindow(..., pinToMainViewport)` ([current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L667)), `PushWidgetStyle`, `EndWindow` | Stock console window skin |
| `ImGuiHelper.BeginRegion` ([current/decomp/KSA/ImGuiHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiHelper.cs#L1442)) | Two-column collapsible sections |
| `Program.GetMainCamera()`, `Camera.EclToEgo`, `EgoToClip`, `EclToScreen` ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L229-L340)) | World-to-screen projection |
| `Astronomical.GetPositionEcl`, `GetBodyFixed2Ecl` ([current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L279-L366)), `IParentBody.GetCci2Ccf` | Frame chain |
| `Program.IsEditorOpen` | Editor skip |

## Pitfalls and version notes

- Pair every `ImGui.Begin` with `End` in a `finally` or a disposable; ImGui reports the mismatch at frame end
  against the wrong window.
- Do not use `ImGuiHelper`'s global widget id counter from mod code.
- `ImGauge` draws only into the main viewport, so gauge-styled content in a detached window needs plain
  ImGui fallbacks.
- The author's devnotes add: an exception inside an ImGui draw is reported against the wrong function,
  and format strings that work in .NET can still produce garbage in ImGui (`docs/guidance/scvx-bridge-devnotes.md`
  section 8).

## See also

- [Guidance vehicle step loop](guidance-vehicle-step-loop.md)
- [Stock-styled console UI](stock-styled-console-ui.md)
- [Multi-pass preview rendering](multi-pass-preview-rendering.md)
- [RCS burn editor and gauge UI](rcs-burn-editor-and-gauge-ui.md)
- [Draw CCI and body-fixed points through a click-through full-screen overlay window with per-viewport projection and fault isolation](../../../howto/flight-and-orbits/map-view/draw-a-predicted-trajectory-and-map-markers/draw-a-predicted-trajectory-and-map-markers-5.md) - how-to article demonstrating this feature
- [Body-fixed or orbit-relative anchors lifted to ecliptic, then Camera.EclToScreen](../../../howto/rendering/overlays/project-world-positions-to-screen/project-world-positions-to-screen-1.md) - how-to article demonstrating this feature
- [Full-screen overlay: a NoInputs, NoBackground window sized to the main viewport](../../../howto/ui/overlays/draw-a-fullscreen-click-through-overlay/draw-a-fullscreen-click-through-overlay-1.md) - how-to article demonstrating this feature
- [Match the stock UI: ConsoleStyle window skin unpinned from the main viewport and mirrored row helpers](../../../howto/ui/windows/match-the-stock-console-ui-style/match-the-stock-console-ui-style-3.md) - how-to article demonstrating this feature

[^ksa-consolestyle]: KSA.ConsoleStyle (decompiled, build 2026.10.10.5554)
