---
type: How-To
title: "HUD instruments: clip rects per region, scrolling tapes and endpoint rotation"
description: Build a fighter-style HUD from draw-list lines - scrolling altitude and speed tapes drawn twice around a readout box with PushClipRect, a pitch ladder rotated by rotating endpoints, and a heading tape
category: ui
group: overlays
topic: draw-hud-instruments-with-the-draw-list
approach: 3
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-aircrafthud/features/fullscreen-drawlist-hud]
techniques: [imgui-drawlist, harmony-postfix]
hooks: [harmony-postfix KSA.Program.OnFrameEditor, starmap StarMapImmediateLoad]
lifecycle: [frame:viewports, frame:draw-ui]
game_types:
  - Brutal.ImGuiApi.ImDrawListPtr
  - Brutal.ImGuiApi.ImDrawListExtensions
  - Brutal.ImGuiApi.Internal.ImGuiWindowPtr
  - KSA.Program
  - KSA.CameraMode
  - KSA.Vehicle
tags: [hud, tape, pitch-ladder, clip-rect, rotation, heading, aircraft, draw-list]
---
KSA_AircraftHUD draws a whole fighter-style HUD with nothing but lines, rectangles, circles and text pushed into one ImGui draw list: scrolling altitude and speed tapes, a heading tape, a flight-path marker, a horizon and a pitch ladder that rolls with
the aircraft. Every primitive is drawn twice (a shadow copy offset by one line width, then the coloured copy). There are no textures, meshes or world-space projection: the flight-path marker and ladder are placed with a fixed degrees-to-pixels factor around the
screen centre, the way a real collimated HUD is drawn. This article covers the instrument techniques. The window is in [overlays approach 1](../draw-a-fullscreen-click-through-overlay/draw-a-fullscreen-click-through-overlay-1.md) and the always-on hook in
[approach 1 of the hidden-HUD topic](../draw-ui-while-the-hud-is-hidden/draw-ui-while-the-hud-is-hidden-1.md). Demonstrated by KSA_AircraftHUD.

## Background: how the game does it

- The draw list API is the Brutal ImGui wrapper: `PushClipRect(min, max, intersectWithCurrentClipRect)`, `PopClipRect`, `AddLine`, `AddCircle`, `AddRect`, `AddText`.[current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L10-L30)[^brutal-imguiapi-imdrawlistextensions]
  KSA offers no instrument toolkit; the mod computes everything from the vehicle's attitude and air data (read each frame from `Program.ControlledVehicle`).
- `Program.GetCameraMode()` and `CameraMode` gate the HUD: it hides in `Map` and `Free`; the mod has no code for `Fixed`, so it draws over the scene there.
- The `dtPlayer` the hook receives is the player-frame delta (see [approach 2](draw-hud-instruments-with-the-draw-list-2.md)).

## Lifecycle and timing

Drawn from a postfix on `Program.OnFrameEditor` (`frame:viewports`, after `NewFrame`, before the `DrawUI` gate), so the HUD stays visible when the UI is hidden. Vehicle state is the step applied in this frame's `PrepareFrame`; the HUD only reads it. The window is
begun before the game's own UI, so with `NoBringToFrontOnFocus` it stays behind every game window. Layout constants are computed once from the viewport height (`verticalScale = Size.Y / 1440`, `DegToPx = localCenter.Y * 0.05`) at `[StarMapImmediateLoad]`, so a later
resize leaves the HUD laid out for the old size. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Lay out from the viewport height once.** Derive every length (tape lengths, ladder widths, clip rectangles, font sizes, line thickness) from half the screen height, so the HUD scales with resolution.
2. **Draw every primitive twice** for legibility: a shadow copy offset by one line width in a dark colour, then the coloured copy:

```csharp
// AircraftHUDMain.cs L378-L382
unsafe void DrawLine(ImDrawList* target, float2 p1, float2 p2, uint color, float thickness)
{
    ImDrawListExtensions.AddLine(target, p1 + shadowOffset, p2 + shadowOffset, shadowColor, thickness);
    ImDrawListExtensions.AddLine(target, p1, p2, color, thickness);
}
```

3. **Draw a scrolling tape twice, clipped above and below the readout box.** The tape walks tick values from the top to the bottom of its range around the current value (minor and major steps, e.g. altitude minor 10 / major 100, speed minor 1 / major 10); each major tick gets a
   label. Clipping once above and once below the boxed number keeps the ticks from running through it:

```csharp
// AircraftHUDMain.cs L515-L543
float2 clipMin = new float2(centerX - 1000f, scaleMinY);
float2 clipMax = new float2(centerX + 1000f, scaleMaxY);

float scaleRangePx = scaleMaxY - scaleMinY;

float valueAtTop = centerValue - (scaleRangePx * 0.5f / valueScaleUnitsToPx);
float valueAtBottom = centerValue + (scaleRangePx * 0.5f / valueScaleUnitsToPx);

float firstTick = MathF.Floor(valueAtTop / minorStep) * minorStep;

for (float v = firstTick; v <= valueAtBottom; v += minorStep)
{
    float y = (centerValue - v) * valueScaleUnitsToPx + (scaleMinY + scaleRangePx * 0.5f);

    if (y < scaleMinY || y > scaleMaxY)
        continue;

    bool isMajor = MathF.Abs((v % majorStep)) < 0.0001f;

    float len = isMajor ? majorLen : minorLen;

    DrawLine(
        draw_list,
        new float2(centerX, y),
        new float2(flipX ? centerX - len : centerX + len, y),
        color,
        1.5f
    );

```

```csharp
// AircraftHUDMain.cs L1111-L1120
draw_list.PushClipRect(new float2(0f, localCenter.Y - verticalScaleLenY), new float2(window_size.X, localCenter.Y - (textBoxSizeY * 0.5f)), true);
DrawVerticalScale(draw_list,
    altitude,
    altitudeScaleUnitsToPx,
    localCenter.Y - verticalScaleLenY, localCenter.Y + verticalScaleLenY,
    localCenter.X + (textPosX * 0.9f),
    color,
    10f, 100f,
    verticalScaleMinorLen, verticalScaleMajorLen,
    verticalScaleTextLen);
```
```csharp
// AircraftHUDMain.cs L1131-L1133,L1153
// draw a second time, clipped
draw_list.PopClipRect();
draw_list.PushClipRect(new float2(0f, localCenter.Y + (textBoxSizeY * 0.5f)), new float2(window_size.X, localCenter.Y + verticalScaleLenY), true);
// ...
draw_list.PopClipRect();
```

4. **Rotate endpoints, not the draw list.** ImGui draw lists have no transform stack, so roll is applied by rotating each line's endpoints about the screen centre with a precomputed `sin`/`cos`; pitch shifts the ladder by `DegToPx * pitch`:

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

5. **Build the ladder and horizon inside a main clip rect.** Rungs every 5 degrees from 5 to 85, dashed below the horizon, numeric labels positioned (not rotated) with the rung:

```csharp
// AircraftHUDMain.cs L1192-L1194,L1242-L1244,L1247-L1252,L1278
float PitchOffsetY = localCenter.Y + (DegToPx * (float)PitchDeg);

draw_list.PushClipRect(mainClipMin, mainClipMax, true);
// ...
// HORIZON
DrawRotatedLine(draw_list, new float2(localCenter.X + horizonLen1, PitchOffsetY), new float2(localCenter.X + horizonLen2, PitchOffsetY), localCenter, color, line_thickness, RollAngleSin, RollAngleCos);
DrawRotatedLine(draw_list, new float2(localCenter.X - horizonLen1, PitchOffsetY), new float2(localCenter.X - horizonLen2, PitchOffsetY), localCenter, color, line_thickness, RollAngleSin, RollAngleCos);
// ...
float pitchOffsetDeg = 30f;
float pitchLadderOffsetY = (localCenter.Y * 0.05f) * (Math.Clamp(PitchDeg, -pitchOffsetDeg, pitchOffsetDeg) / pitchOffsetDeg);

for (int i = 1; i < 18; i++)
{
    int pitchAngle = i * 5;
// ...
draw_list.PopClipRect();
```

6. **Place the flight-path marker** at `(beta, alpha) * DegToPx` from the centre; when either angle exceeds 90 degrees flip to a retrograde symbol with a cross:

```csharp
// AircraftHUDMain.cs L1198-L1209
// FLIGHT VECTORS
if (Math.Abs(alphaDeg) < 90 && Math.Abs(betaDeg) < 90)
{
    // PROGRADE
    float2 flightVecOffset = new float2(betaDeg * DegToPx, alphaDeg * DegToPx);
    float2 flightVecCenter = new float2(localCenter.X + flightVecOffset.X, localCenter.Y - flightVecOffset.Y);
    ImDrawListExtensions.AddCircle(draw_list, flightVecCenter + shadowOffset, flightVecRadius1, shadowColor, 16, line_thickness);
    ImDrawListExtensions.AddCircle(draw_list, flightVecCenter, flightVecRadius1, color1, 16, line_thickness);
    DrawLine(draw_list, flightVecCenter + new float2(flightVecRadius1, 0.0f), flightVecCenter + new float2(flightVecRadius2, 0.0f), color1, line_thickness);
    DrawLine(draw_list, flightVecCenter - new float2(flightVecRadius1, 0.0f), flightVecCenter - new float2(flightVecRadius2, 0.0f), color1, line_thickness);
    ImDrawListExtensions.AddLine(draw_list, flightVecCenter - new float2(0.0f, flightVecRadius1), flightVecCenter - new float2(0.0f, flightVecRadius2), color1, line_thickness);
}
```

7. **Heading tape** in its own clip rect: compute the heading at each end, step in 5 degree ticks with 15 degree major labels formatted `000`:

```csharp
// AircraftHUDMain.cs L451-L482
{
    const float minorStep = 5f;
    const float majorStep = 15f;

    float dirSign = flipDirection ? -1f : 1f;

    float headingRef = HeadingDeg + phaseOffsetDeg;

    float degAtMin = headingRef + ((headingScaleMinX - localCenter.X) / headingScaleDegToPx) * (1f / dirSign);
    float degAtMax = headingRef + ((headingScaleMaxX - localCenter.X) / headingScaleDegToPx) * (1f / dirSign);

    if (degAtMax < degAtMin)
    {
        float tmp = degAtMin; degAtMin = degAtMax; degAtMax = tmp;
    }

    float firstTick = (float)MathF.Floor(degAtMin / minorStep) * minorStep;

    firstTick -= minorStep;

    float rangeDeg = degAtMax - firstTick;
    int maxTicks = (int)(rangeDeg / minorStep) + 8;

    for (int i = 0; i < maxTicks; i++)
    {
        float H = firstTick + i * minorStep;
        if (H < degAtMin - 0.001f) continue;
        if (H > degAtMax + 0.001f) break;

        float degDelta = H - headingRef;
        float x = localCenter.X + (degDelta * headingScaleDegToPx * dirSign);

```

8. **Optional legend.** A settings toggle prints magenta labels ("altitude (radar)", "apoapsis", ...) next to each readout, a cheap legend for new users.

## Complete example

| File | Role |
|---|---|
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L378-L437) | Shadowed line, dashed line, rotation helpers |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L504-L555) | `DrawVerticalScale` |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L439-L502) | `DrawHeadingScaleTicks` |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L956-L1300) | `DrawHud`: gate, window, tapes, ladder, heading |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1332-L1341) | Harmony postfix on `Program.OnFrameEditor` |

## Variations and gotchas

- **Attitude block gate.** The ladder, horizon, flight-path marker and heading tape are wrapped in `if (altitude < 999999.0)` after `altitude` is converted to the display unit; with imperial units they disappear above 999,999 ft (about 305 km) while the readouts stay.
- **Pitch ladder labels move with roll but are not rotated** (`TO DO` in the code).
- **`ImGui.PopFont()` at the end of `DrawHud`** runs even when the font failed to load and nothing was pushed.
- **A parent that is not a `Celestial`** (a star) makes the cast throw instead of returning.
- **Fixed camera mode** draws the HUD over the scene although its symbology assumes a forward view.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of draw-hud-instruments-with-the-draw-list](overview.md)
- [Approach 1: arcs and gradients](draw-hud-instruments-with-the-draw-list-1.md)
- [Approach 2: staged intro and outro animation](draw-hud-instruments-with-the-draw-list-2.md)
- [Full-screen overlay window](../draw-a-fullscreen-click-through-overlay/draw-a-fullscreen-click-through-overlay-1.md)
- [Keep the HUD running with the UI hidden](../draw-ui-while-the-hud-is-hidden/draw-ui-while-the-hud-is-hidden-1.md)
- [Read a vehicle's position, velocity, attitude, orbit and flight instruments](../../../flight-and-orbits/flight-state/read-vehicle-flight-state/overview.md)
- Mod feature: [Fullscreen draw-list HUD](../../../../mods/ksa-aircrafthud/features/fullscreen-drawlist-hud.md)

[^brutal-imguiapi-imdrawlistextensions]: Brutal.ImGuiApi.ImDrawListExtensions (decompiled, build 2026.10.10.5554)
