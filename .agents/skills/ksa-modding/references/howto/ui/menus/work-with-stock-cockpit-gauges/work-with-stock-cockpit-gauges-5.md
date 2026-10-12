---
type: How-To
title: "Draw a marker on the stock navball: replicate the game's marker maths and paint on the foreground draw list"
description: Convert a direction from the ecliptic into the navball control frame with the same matrices NavBallRenderer uses, copy the marker shader's size and rim-fade constants, and draw the glyph on ImGui's foreground draw list because the navball is rendered by a draw callback
category: ui
group: menus
topic: work-with-stock-cockpit-gauges
approach: 5
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ohmystars/features/navball-star-marker]
techniques: [imgui-drawlist]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.NavBallRenderer
  - KSA.GaugeCanvas
  - KSA.CanvasRect
  - KSA.GaugeBase
  - KSA.Vehicle
  - KSA.ImGuiHelper
  - KSA.Program
tags: [navball, marker, foreground-drawlist, gaugecanvas, body2screen, rim-fade, coverage]
---
Oh My Stars' "Mark Navball" option puts an amber five-pointed star on the stock navball at the selected star's direction, with an optional name label and an amber rim arrow when the star is on the far side of the ball. The navball is drawn by the GPU inside a gauge canvas, so the mod cannot add a marker to the shader; it reproduces the game's marker maths in C# and paints the glyph on ImGui's foreground draw list, replicating the shader's size, rim fade, outline and even the white centre "T" that occludes a stock marker. Prerequisites: StarMap and a per-frame ImGui hook. Demonstrated by [Navball star marker](../../../../mods/ohmystars/features/navball-star-marker.md).

## Background: how the game does it

- **The ball is a draw callback.** `GaugeCanvas.OnDrawUi` begins an ordinary ImGui window and fills it with a draw-list callback (`AddCallback(_gaugeDrawCallback, ...)`) that renders the ball when ImGui's draw data is executed ([current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L819-L857));[^ksa-ksa-gaugecanvas] those canvases are submitted by `Program.OnDrawUiThreadSafe`, after the StarMap after-GUI hook ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3198-L3206)).[^ksa-ksa-program] Anything drawn into a normal window is at the mercy of window order; the foreground draw list is drawn last.
- **Canvas and component names** come from the gauge XML: `Id="Navball"` with a `Component Name="Ball"` ([current/Content/Core/Gauges.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Gauges.xml#L2236-L2242)).[^ksa-current-content-core-gauges-xml]
- **Marker maths.** `NavBallRenderer.GetNavBallMarkers` turns each marker direction (CCI) into navball screen space with `cci2Ctrl = vehicle.GetCtrl2Cci().Inverse()` and a fixed `Body2Screen` permutation:

```csharp
// current/decomp/KSA/NavBallRenderer.cs L78-L86
	private static float4 ToScreenMarker(double3 dirCci, bool valid, doubleQuat cci2Ctrl)
	{
		if (!valid)
		{
			return float4.Zero;
		}
		double3 position = dirCci.Transform(cci2Ctrl);
		return new float4(float3.Pack(position.Transform(Body2Screen).NormalizeOrZero()), 1f);
	}
```

   (`Body2Screen` at [current/decomp/KSA/NavBallRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/NavBallRenderer.cs#L22); [current/decomp/KSA/NavBallRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/NavBallRenderer.cs#L60-L86)[^ksa-ksa-navballrenderer]). `Vehicle.GetCtrl2Cci()` is public ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3137))[^ksa-ksa-vehicle] and `IParentBody.GetCce2Cci()` is on the parent interface ([current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L53)).[^ksa-ksa-iparentbody]
- **Marker styling** lives in the fragment shader: size, border, rim and fade constants ([current/Content/Core/Shaders/Common/NavballMarkers.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/NavballMarkers.glsl#L11-L36))[^ksa-current-content-core-shaders-common-navballmarkers-glsl] and the white T-shaped centre indicator drawn after the markers ([current/Content/Core/Shaders/GaugeNavball.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/GaugeNavball.frag#L395-L400)).[^ksa-current-content-core-shaders-gaugenavball-frag]

## Lifecycle and timing

- **`frame:draw-ui`.** OhMyStars calls `NavballMarkerRenderer.Draw()` from its sky overlay, itself drawn from `[StarMapAfterGui]`, before the overlay window's `ImGui.End()` ([src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs#L111-L113)).[^ohmystars-src-stellariumrenderer]
- **State read.** `GetCtrl2Cci()` and the parent's `GetCce2Cci()` are main-thread values after this frame's solver results were applied, so the marker matches the ball, which renders from the same state.
- **Hidden HUD.** The navball canvas is not drawn with `DrawUI` false, and the gate below refuses to draw.
- See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Check that the ball is actually on screen.** Mirror the stock conditions: not in the editor, `DrawUI` on, a controlled and active vehicle, the `Navball` canvas enabled and context-visible, its `Ball` component's `CanvasRect` non-empty and not fully off the main viewport:

```csharp
// src/NavballMarkerRenderer.cs L156-L169, L171-L181
        if(Program.EditorFlag || !Program.DrawUI || Program.ControlledVehicle == null ||
           !Program.IsControlledVehicleActive)
            return false;

        GaugeCanvas? navballCanvas = null;
        foreach(GaugeCanvas canvas in GaugeCanvas.AllCanvases) {
            if(canvas.Id == "Navball") {
                navballCanvas = canvas;
                break;
            }
        }

        if(navballCanvas == null || !navballCanvas.Enabled || !navballCanvas.IsContextVisible())
            return false;
// ...
        foreach(GaugeComponent component in navballCanvas.Components) {
            if(component.Name != "Ball" || component.Canvas == null)
                continue;

            CanvasRect rect = new CanvasRect(component);
            if(rect.Size.X <= 0f || rect.Size.Y <= 0f)
                return false;

            ballCenter = rect.TopLeft + rect.Size * 0.5f;
            ballExtends = rect.Size.X * 0.5f;

```

2. **Convert the direction.** The star catalog works in ecliptic axes (CCE); navball marker directions are CCI. Rotate through the parent's CCE-to-CCI, then into the control frame, then permute with a copy of `Body2Screen`:

```csharp
// src/NavballMarkerRenderer.cs L55-L60
    // Same body-to-screen permutation as NavBallRenderer.Body2Screen.
    private static readonly double4x4 Body2Screen = new double4x4(
        0.0, 0.0, 1.0, 0.0,
        1.0, 0.0, 0.0, 0.0,
        0.0, 1.0, 0.0, 0.0,
        0.0, 0.0, 0.0, 1.0);
```

```csharp
// src/NavballMarkerRenderer.cs L122-L141
    private static bool TryGetMarkerScreenDirection(Vehicle vehicle, double3 starDirectionCce, out float3 screenDirection) {
        screenDirection = default;

        double3 dirEcl = StellariumRenderer.ApplyAlignment(starDirectionCce);
        if(VectorMath.IsZero(dirEcl))
            return false;

        // The catalog works in ecliptic axes (CCE); navball marker directions are CCI, so convert
        // through the parent body's CCE->CCI rotation, then into the navball's control frame.
        doubleQuat cce2Cci = vehicle.Parent.GetCce2Cci();
        double3 dirCci = dirEcl.Transform(cce2Cci);

        doubleQuat cci2Ctrl = vehicle.GetCtrl2Cci().Inverse();
        double3 dirScreen = dirCci.Transform(cci2Ctrl).Transform(Body2Screen).NormalizeOrZero();
        if(VectorMath.IsZero(dirScreen))
            return false;

        screenDirection = float3.Pack(dirScreen);
        return true;
    }
```

3. **Copy the shader constants.** They are not exposed to C#, so the mod copies the values from `NavballMarkers.glsl` and `GaugeNavball.frag` and comments where each came from:

```csharp
// src/NavballMarkerRenderer.cs L21-L28
    // Constants mirrored from Content/Core/Shaders/Common/NavballMarkers.glsl.
    private const float MarkerSizeFrac = 0.22f;
    private const float MarkerBorderPx = 1.2f;
    private const float MarkerRim = 0.948683298f; // sqrt(0.9)
    private const float MarkerRimMargin = MarkerSizeFrac * 0.25f;
    private const float MarkerMaxReachUv = 0.17f;
    private const float MarkerStrokeFrac = 0.09f; // MARKER_THK
    private const float MarkerDotFrac = 0.05f;    // MARKER_DOT_FRAC
```

   The rim fade is the shader's `smoothstep` with inverted edges, so the glyph fades in near the rim:

```csharp
// src/NavballMarkerRenderer.cs L225-L231
    // Replicates smoothstep(MARKER_RIM, MARKER_RIM - MARKER_RIM_MARGIN, lenXY + MARKER_MAX_REACH_UV)
    // from the glsl (note the inverted edges: fade is 1 well inside the ball and 0 at the rim).
    private static float RimFade(float lenXY) {
        float x = lenXY + MarkerMaxReachUv;
        float t = Math.Clamp((x - MarkerRim) / -MarkerRimMargin, 0f, 1f);
        return t * t * (3f - 2f * t);
    }
```

4. **Draw on the foreground list.** The marker centre is `ballCenter + screenDir.xy * ballExtends`. Use `ImGui.GetForegroundDrawList()` ([current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8038-L8041)),[^ksa-brutal-imguiapi-imgui] because the ball is rendered by a draw callback inside the canvas window:

```csharp
// src/NavballMarkerRenderer.cs L105-L119
        // The ball is rendered by a Vulkan draw callback inside the navball canvas window,
        // so the marker must go to the foreground draw list to land on top of it.
        ImDrawListPtr drawList = ImGui.GetForegroundDrawList();

        float2 markerCenter = ballCenter + new float2(screenDirection.X, screenDirection.Y) * ballExtends;
        float glyphSize = MarkerSizeFrac * ballExtends * GlyphScale;

        if(IsMarkerCovered(markerCenter, navballCanvas))
            return;

        DrawStarGlyph(drawList, markerCenter, glyphSize, fade);
        if(showMarkerLabel && SkyCulturesRenderer.TryGetStarDisplayName(hip, out string displayName)) {
            DrawMarkerLabel(drawList, markerCenter, glyphSize, fade, displayName);
        }
        DrawCenterIndicatorOverMarker(drawList, markerCenter, glyphSize, ballCenter, ballExtends);
```

5. **Draw the glyph as two polylines** (a dark outline band, then the fill) plus a two-layer centre dot, matching the shader's layering:

```csharp
// src/NavballMarkerRenderer.cs L233-L257
    private static void DrawStarGlyph(ImDrawListPtr drawList, float2 center, float glyphSize, float fade) {
        float outerRadius = glyphSize * StarOuterRadiusFrac;
        float innerRadius = glyphSize * StarInnerRadiusFrac;
        float stroke = glyphSize * MarkerStrokeFrac;

        // Five-pointed star outline with one vertex straight up.
        Span<float2> points = stackalloc float2[StarVertexCount];
        for(int i = 0; i < StarVertexCount; i++) {
            float radius = (i & 1) == 0 ? outerRadius : innerRadius;
            float angle = -MathF.PI * 0.5f + i * (MathF.PI / (StarVertexCount / 2));
            float2 direction = new float2(MathF.Cos(angle), MathF.Sin(angle));

            points[i] = center + direction * radius;
        }

        // Same layering as the glsl markers: a dilated dark outline band underneath,
        // the fill-colored stroke on top of it.
        ImDrawListExtensions.AddPolyline(drawList, points, StellariumRenderer.ToLineColor(OutlineColor, fade), ImDrawFlags.Closed, stroke + 2f * GlyphBorderPx);
        ImDrawListExtensions.AddPolyline(drawList, points, StellariumRenderer.ToLineColor(FillColor, fade), ImDrawFlags.Closed, stroke);

        // Small center dot, like the prograde/normal/radial glyphs.
        float dotRadius = glyphSize * MarkerDotFrac;
        ImDrawListExtensions.AddCircleFilled(drawList, center, dotRadius + GlyphBorderPx, StellariumRenderer.ToLineColor(OutlineColor, fade), 0);
        ImDrawListExtensions.AddCircleFilled(drawList, center, dotRadius, StellariumRenderer.ToLineColor(FillColor, fade), 0);
    }
```

6. **Add a rim arrow for the far side.** When the star is on the back hemisphere or past the rim, draw a filled triangle with an outline just inside the rim, pointing along the star's screen direction ([src/NavballMarkerRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/NavballMarkerRenderer.cs#L259-L298)).

7. **Fake depth.** The foreground list sits above everything, so hide the glyph when its centre is inside any other enabled gauge canvas rectangle or the mod's own window. The mod states that the game keeps its UI coverage mask on the GPU with no CPU query:

```csharp
// src/NavballMarkerRenderer.cs L198-L217
    // This marker draws to the foreground list, so it sits above the navball gauge and would
    // also sit above any gauge canvas or window layered over that spot. The game keeps its UI
    // coverage mask on the GPU with no CPU query, so this detects overlap directly against the
    // gauge canvases' screen rects and the mod's own window instead.
    private static bool IsMarkerCovered(float2 markerCenter, GaugeCanvas navballCanvas) {
        foreach(GaugeCanvas canvas in GaugeCanvas.AllCanvases) {
            if(ReferenceEquals(canvas, navballCanvas) || !canvas.Enabled || !canvas.IsContextVisible())
                continue;

            if(PointInRect(markerCenter, canvas.GetPixelsMin(), canvas.GetPixelsSize()))
                return true;
        }

        if(OhMyStarsWindow.TryGetWindowRect(out float2 windowPosition, out float2 windowSize) &&
           PointInRect(markerCenter, windowPosition, windowSize)) {
            return true;
        }

        return false;
    }
```

8. **Label and centre indicator.** The label goes through the game's `ImGuiHelper.DrawText(..., TextAlignment.Center)` so it gets the stock drop shadow ([current/decomp/KSA/ImGuiHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiHelper.cs#L542-L556)):[^ksa-ksa-imguihelper]

```csharp
// src/NavballMarkerRenderer.cs L302-L306
    private static void DrawMarkerLabel(ImDrawListPtr drawList, float2 center, float glyphSize, float fade, string name) {
        float2 position = center + new float2(0f, glyphSize * StarOuterRadiusFrac + GlyphBorderPx + LabelGapPx);
        byte4 color = byte4.Pack(LabelColor, Pack.Float.Normalize).Transparency(fade);
        ImGuiHelper.DrawText(drawList, position, name, color, TextAlignment.Center);
    }
```

   The shader draws the white T-shaped centre indicator after its markers, so the overlay repaints the T over the glyph when they overlap ([src/NavballMarkerRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/NavballMarkerRenderer.cs#L308-L339)).

9. **Check in game.** Open the navball, select a star: the amber glyph sits at the star's direction and fades near the rim; a rim arrow appears for stars behind the ball; moving a gauge over the glyph hides it.

## Complete example

| File | Role |
|---|---|
| [src/NavballMarkerRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/NavballMarkerRenderer.cs#L62-L120) | Gates, direction, fade, draw order |
| [src/NavballMarkerRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/NavballMarkerRenderer.cs#L122-L141) | CCE to CCI to control to navball screen |
| [src/NavballMarkerRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/NavballMarkerRenderer.cs#L143-L223) | Navball discovery and coverage test |
| [src/NavballMarkerRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/NavballMarkerRenderer.cs#L225-L339) | Fade, glyph, rim arrow, label, centre T |
| [src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs#L111-L113) | Call site inside the sky overlay |

## Variations and gotchas

- **Copied constants drift.** Any change to `NavballMarkers.glsl`, `GaugeNavball.frag`, `Body2Screen` or the navball canvas layout breaks the visual match silently. Re-check them on each game update.
- **Coverage is a point test.** Only the glyph centre is tested against canvas rectangles, so a glyph can still be half drawn over a neighbouring gauge.
- **Name-based lookup.** The canvas is found by `Id == "Navball"` and the component by `Name == "Ball"`; a mod that renames or replaces them disables the marker.
- **Foreground list is per viewport.** It is the main viewport's list; a navball dragged into a separate OS window (a detached canvas) would not get the marker.
- **A fixed direction can be wrong for special bodies.** The mod's marker always uses the catalog direction; Sol has no real catalog direction, so a Sol marker would sit at the ecliptic +X direction rather than at the actual Sun (the mod's other features special-case the central star).
- **For world-space markers elsewhere** see [Project world positions to the screen](../../../rendering/overlays/project-world-positions-to-screen/overview.md).

## See also

- [Overview](overview.md)
- [Approach 4: hide and restore the flight HUD](work-with-stock-cockpit-gauges-4.md)
- [Draw a fullscreen click-through overlay](../../overlays/draw-a-fullscreen-click-through-overlay/overview.md)
- [Project world positions to the screen](../../../rendering/overlays/project-world-positions-to-screen/overview.md)
- [Match the stock console UI style](../../windows/match-the-stock-console-ui-style/overview.md)
- Mod feature: [Navball star marker](../../../../mods/ohmystars/features/navball-star-marker.md)

[^ksa-ksa-gaugecanvas]: KSA.GaugeCanvas (decompiled, build 2026.10.10.5554)
[^ksa-ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-current-content-core-gauges-xml]: Game content: current/Content/Core/Gauges.xml
[^ksa-ksa-navballrenderer]: KSA.NavBallRenderer (decompiled, build 2026.10.10.5554)
[^ksa-ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-current-content-core-shaders-common-navballmarkers-glsl]: Game content: current/Content/Core/Shaders/Common/NavballMarkers.glsl
[^ksa-current-content-core-shaders-gaugenavball-frag]: Game content: current/Content/Core/Shaders/GaugeNavball.frag
[^ohmystars-src-stellariumrenderer]: OhMyStars: src/StellariumRenderer.cs
[^ksa-brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^ksa-ksa-imguihelper]: KSA.ImGuiHelper (decompiled, build 2026.10.10.5554)
