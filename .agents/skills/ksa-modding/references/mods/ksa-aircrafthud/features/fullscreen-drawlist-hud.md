---
type: Mod Feature
title: Fullscreen draw-list HUD
description: Draws a complete fighter HUD (pitch ladder, horizon, flight path marker, heading tape, speed and altitude tapes) as ImGui draw-list geometry in an input-less fullscreen window from a Harmony postfix on Program.OnFrameEditor
mod: ksa-aircrafthud
category: ui
techniques: [imgui-drawlist, imgui-window, harmony-postfix, starmap-lifecycle]
hooks: [starmap StarMapImmediateLoad, harmony-postfix KSA.Program.OnFrameEditor]
lifecycle: [startup:immediate-load, frame:viewports]
game_types:
  - KSA.Program
  - KSA.CameraMode
  - KSA.Vehicle
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImDrawListExtensions
tags: [hud, imgui, draw-list, clip-rect, pitch-ladder, heading-tape, overlay, harmony]
---

The whole HUD is vector geometry pushed into one ImGui draw list: lines, rectangles, circles and
text, each drawn twice (a shadow copy offset by one line width, then the colored copy). There are no
textures, meshes or world-space projection; the flight path marker and pitch ladder are placed with
a fixed degrees-to-pixels factor around the screen center, the way a real collimated HUD is
drawn.[^ah-main]

## How it works

1. **Layout once, from the viewport height.** At `[StarMapImmediateLoad]`, `Init` reads
   `ImGui.GetMainViewport()`, sets `verticalScale = Size.Y / 1440`, and derives every length from
   `localCenter.Y` (half the screen height): `DegToPx = localCenter.Y * 0.05`, tape lengths, ladder
   widths, clip rectangles, font sizes and line thickness
   ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L675-L767)).
2. **Gate.** `DrawHud` returns early when the HUD is disabled, when `Program.GetCameraMode()` is
   `Map` or `Free`, when there is no `Program.ControlledVehicle`, when the orbit parent is null, or
   when barometric altitude is above 999,999 m
   ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L960-L976)). It therefore shows in the
   `Orbit`, `IVA` and `Fixed` camera modes.[^ksa-cameramode] A parent that is not a `Celestial` (a
   star) makes the cast throw instead of returning; see
   [Surface attitude and air data from the controlled vehicle](flight-data-from-vehicle.md#pitfalls-and-version-notes).
3. **Fullscreen, input-less window.** `SetNextWindowPos/Size/Viewport` to the main viewport, then
   `Begin("HUDFullscreenWindow")` with `NoTitleBar | NoResize | NoMove | NoCollapse |
   NoBringToFrontOnFocus | NoBackground | NoFocusOnAppearing | NoInputs | NoNavFocus`, and
   `GetWindowDrawList()` ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L997-L1013)).
4. **Full-size workaround.** A filled rectangle with alpha 1/255 covers the whole window. The
   comment says the window "gets clipped"; the reason is that ShaderExtensions sizes the ImGui shader
   quad from the bounding box of the draw list's vertices, so without this rectangle the curved-glass
   shader would be centered on whatever symbols happen to be drawn
   ([ShaderExtensions/ImGuiShader/ImGuiBaseRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiBaseRenderer.cs#L199-L260)).[^sx-imguibase]
   See [Curved, chromatic HUD window via an ImGuiShader](imgui-window-hud-shader.md).
5. **Tapes with clip rects.** `DrawVerticalScale` walks tick values from the top to the bottom of the
   tape around the current value (altitude: minor 10, major 100; speed: minor 1, major 10). Each tape
   is drawn twice, clipped once above and once below the readout box with
   `draw_list.PushClipRect(..., true)`, so ticks never run through the boxed number
   ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1110-L1153)).
6. **Rotated pitch ladder and horizon.** Roll is applied by rotating each endpoint around the screen
   center with a precomputed `sin`/`cos` (`RotateAroundPointFast`); pitch shifts the ladder by
   `DegToPx * pitch`. Rungs every 5 degrees from 5 to 85, dashed below the horizon, with numeric
   labels positioned (but not rotated) with the rung, all inside a main clip rect
   ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1186-L1278)).
7. **Flight path marker.** Offset from center by `(beta, alpha) * DegToPx`; when either angle exceeds
   90 degrees the marker flips to a retrograde symbol with a cross
   ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1198-L1235)).
8. **Heading tape.** `DrawHeadingScaleTicks` computes the heading at each end of the tape, steps in
   5-degree ticks with 15-degree major labels (`000` format), inside its own clip rect
   ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L439-L502),
   [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1280-L1300)).
9. **Symbology overlay.** A settings toggle prints magenta labels ("altitude (radar)", "apoapsis",
   ...) next to each readout, a cheap legend for new users
   ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1303-L1324)).

## Lifecycle and timing

- **`startup:immediate-load`.** Layout constants come from the main viewport size at mod prepare
  time. ImGui exists by then: the `Program` constructor calls `ImGui.CreateContext()` and
  `ImGuiBackend.Initialize` before `ModLibrary.PrepareAll`
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L842-L899)).[^ksa-program]
- **`frame:viewports`.** The draw happens in a Harmony postfix on the private
  `Program.OnFrameEditor(double)` (the hook itself is described in
  [Always-on ImGui hook from a Program.OnFrameEditor postfix](always-on-onframeeditor-hook.md)) ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1332-L1341)).
  `Program.OnFrame` runs `PrepareFrame` (vehicle solver results applied, next step dispatched),
  `PrepareImGui` (`ImGui.NewFrame`), then `OnFrameEditor`, then `OnFrameViewports`, then the UI calls
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2286)). Consequences:
  - ImGui calls are legal (after `NewFrame`, before `ImGui.Render`).
  - Vehicle state is the step applied in this frame's `PrepareFrame`. The next physics step is
    already running on the worker threads, so the state is safe to read (it does not change until
    the next frame's apply) but not to write; the HUD only reads.
  - On frames where `PrepareFrame` returns `Exit` (font rebuild, window close) the postfix does not
    run and the HUD is missing for that frame.
  - The window is begun before the game's own UI and before `[StarMapBeforeGui]`. With
    `NoBringToFrontOnFocus`, Dear ImGui puts the window at the back of the display order when it is
    created (library behaviour), so it stays behind every other window.
  - `OnFrameEditor` is called outside the `if (DrawUI)` blocks, so the HUD stays visible when the
    player hides the UI. A `[StarMapAfterGui]` hook would vanish with the rest of the UI.
  - The camera mode read here is the mode set before this frame's `OnFrameViewports`.

## Key code

| Path | Role |
|---|---|
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L675-L767) | Layout constants from viewport height |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L335-L437) | Shadowed text, rect, line, dashed line, rotation helpers |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L504-L555) | Vertical tape |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L956-L1329) | `DrawHud` |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1332-L1341) | Harmony postfix on `Program.OnFrameEditor` |

## Snippets

```csharp
// AircraftHUDMain.cs L997-L1013
            // CREATE WINDOW
            ImGuiViewport* viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(viewport->Pos);
            ImGui.SetNextWindowSize(window_size);
            ImGui.SetNextWindowViewport(viewport->ID);
            ImGuiWindowFlags flags =
                ImGuiWindowFlags.NoTitleBar |
                ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoBringToFrontOnFocus |
                ImGuiWindowFlags.NoBackground |
                ImGuiWindowFlags.NoFocusOnAppearing |
                ImGuiWindowFlags.NoInputs |
                ImGuiWindowFlags.NoNavFocus;
            ImGui.Begin("HUDFullscreenWindow", flags);
            ImDrawListPtr draw_list = ImGui.GetWindowDrawList();
```

```csharp
// AircraftHUDMain.cs L410-L437
        unsafe void DrawRotatedLine(ImDrawList* target,
            float2 p1, float2 p2, float2 center,
            uint color, float thickness,
            float sinA, float cosA, bool Dashed = false)
        {
            float2 rp1 = RotateAroundPointFast(p1, center, sinA, cosA);
            float2 rp2 = RotateAroundPointFast(p2, center, sinA, cosA);

            if (Dashed)
            {
                DrawLineDashed(target, rp1, rp2, color, thickness, 15f, 7.5f);
            }
            else
            {
                DrawLine(target, rp1, rp2, color, thickness);
            }
        }

        float2 RotateAroundPointFast(float2 point, float2 pivot, float s, float c)
        {
            float px = point.X - pivot.X;
            float py = point.Y - pivot.Y;

            float rx = px * c - py * s;
            float ry = px * s + py * c;

            return new float2(rx + pivot.X, ry + pivot.Y);
        }
```

```csharp
// AircraftHUDMain.cs L1332-L1341
    [HarmonyPatch(typeof(Program))]
    internal static class ProgramPatches
    {
        [HarmonyPatch("OnFrameEditor")]
        [HarmonyPostfix]
        private static void AfterOnFrameViewports(double dtPlayer)
        {
            HUD.Instance?.DrawHud(dtPlayer);
        }
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Program.OnFrameEditor(double)` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2567-L2578) | Harmony postfix target (private; patched by name) |
| `Program.GetCameraMode()` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L651-L654) | Hide in `Map`/`Free`; effects only in `IVA` |
| `Program.ControlledVehicle` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L502) | Vehicle to display |
| `CameraMode` | [current/decomp/KSA/CameraMode.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CameraMode.cs) | `Orbit`, `Free`, `Map`, `IVA`, `Fixed` |
| `ImDrawListExtensions.AddLine/AddRect/AddRectFilled/AddCircle/AddText/PushClipRect` | [current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs) | HUD primitives[^imgui-drawlistext] |

## Pitfalls and version notes

- Layout is computed once in `Init` from the startup viewport. The size is valid then because the
  loading screen has already run an ImGui frame (`Loading.OnFrame` calls `ImGui.NewFrame`
  ([current/decomp/KSA/Loading.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Loading.cs#L105-L109))
  in the `Program` constructor before `ModLibrary.PrepareAll`),[^ksa-loading] but resizing the game window or changing resolution later leaves the HUD
  laid out for the old size (`window_size` and centers are never recomputed).
- The attitude block (ladder, horizon, flight path marker, heading tape) is wrapped in
  `if (altitude < 999999.0)` after `altitude` has been converted to the display unit
  ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1186)). With imperial distance units it
  disappears above 999,999 ft (about 305 km) while the readouts stay.
- `ImGui.PopFont()` at the end of `DrawHud` runs even when the font failed to load and nothing was
  pushed ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1327)); see
  [Custom TTF font in the ImGui atlas](custom-hud-font.md).
- The postfix method is named `AfterOnFrameViewports` but targets `OnFrameEditor`; the name
  suggests an earlier target. Both methods exist in 5554, and `OnFrameEditor` runs first.
- The HUD hides only in `Map` and `Free`. In `Fixed` camera mode it draws over the scene even though
  its symbology assumes a forward view.
- Pitch ladder labels move with roll but are not rotated (`TO DO` in the code).

## See also

- [Always-on ImGui hook from a Program.OnFrameEditor postfix](always-on-onframeeditor-hook.md)
- [Surface attitude and air data from the controlled vehicle](flight-data-from-vehicle.md)
- [Curved, chromatic HUD window via an ImGuiShader](imgui-window-hud-shader.md)
- [Custom TTF font in the ImGui atlas](custom-hud-font.md)
- [INI settings file and settings window](ini-settings-and-settings-window.md)
- [Full-screen overlay: a NoInputs, NoBackground window sized to the main viewport](../../../howto/ui/overlays/draw-a-fullscreen-click-through-overlay/draw-a-fullscreen-click-through-overlay-1.md) - how-to article demonstrating this feature
- [HUD instruments: clip rects per region, scrolling tapes and endpoint rotation](../../../howto/ui/overlays/draw-hud-instruments-with-the-draw-list/draw-hud-instruments-with-the-draw-list-3.md) - how-to article demonstrating this feature
- [Keep drawing when the HUD is hidden: Harmony postfix on Program.OnFrameEditor](../../../howto/ui/overlays/draw-ui-while-the-hud-is-hidden/draw-ui-while-the-hud-is-hidden-1.md) - how-to article demonstrating this feature

[^ah-main]: KSA_AircraftHUD/AircraftHUDMain.cs
[^sx-imguibase]: ShaderExtensions ImGuiBaseRenderer.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-loading]: KSA.Loading (decompiled, build 2026.10.10.5554)
[^ksa-cameramode]: KSA.CameraMode (decompiled, build 2026.10.10.5554)
[^imgui-drawlistext]: Brutal.ImGuiApi.ImDrawListExtensions (decompiled, build 2026.10.10.5554)
