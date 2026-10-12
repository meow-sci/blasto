---
type: Mod Feature
title: Navball star marker
description: Reproduce NavBallRenderer.ToScreenMarker and the NavballMarkers.glsl styling in C# to paint a custom star marker, rim arrow and label on the stock navball through the ImGui foreground draw list
mod: ohmystars
category: ui
techniques: [imgui-drawlist, shader-constant-mirroring]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.NavBallRenderer
  - KSA.GaugeCanvas
  - KSA.GaugeComponent
  - KSA.CanvasRect
  - KSA.GaugeBase
  - KSA.Vehicle
  - KSA.IParentBody
  - KSA.ImGuiHelper
  - KSA.Program
tags: [navball, marker, gauge, imgui, foreground-drawlist, flight]
---

"Mark Navball" puts an amber five-pointed star glyph on the stock navball at the selected star's
direction, with an optional name label, and an amber rim arrow when the star is on the far side of
the ball. The navball itself is drawn by the GPU inside a gauge canvas, so the mod reproduces the
game's own marker maths in C# and paints the glyph on ImGui's foreground draw list, replicating the
shader's size, rim fade, outline and even the white centre "T" that would occlude a stock
marker.[^oms-navballmarkerrenderer-cs]

## How it works

1. **Is the ball on screen?** `IsNavballBallOnScreen` mirrors the stock gates: not in the editor,
   `Program.DrawUI` on, a controlled and active vehicle, a `GaugeCanvas` with `Id == "Navball"`
   that is `Enabled` and `IsContextVisible()`, its `Ball` component's `CanvasRect` non-empty, and
   the ball not fully off the main viewport
   ([src/NavballMarkerRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/NavballMarkerRenderer.cs#L143-L196)). The canvas and component names come from
   `Gauges.xml` ([current/Content/Core/Gauges.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Gauges.xml#L2236-L2242)).[^ksa-content-core-gauges-xml]
2. **Direction to navball screen space.** The catalog direction (ecliptic, after fine alignment) is
   rotated into the parent body's inertial frame with `vehicle.Parent.GetCce2Cci()`, then into the
   control frame with `vehicle.GetCtrl2Cci().Inverse()`, then permuted with the same `Body2Screen`
   matrix the game uses ([src/NavballMarkerRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/NavballMarkerRenderer.cs#L55-L60),
   [src/NavballMarkerRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/NavballMarkerRenderer.cs#L122-L141)). This is `NavBallRenderer.ToScreenMarker`
   reimplemented for a direction the game does not know about
   ([current/decomp/KSA/NavBallRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/NavBallRenderer.cs#L22),
   [current/decomp/KSA/NavBallRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/NavBallRenderer.cs#L65-L86)).[^ksa-navballrenderer-cs]
3. **Shader constants in C#.** `MarkerSizeFrac 0.22`, `MarkerBorderPx 1.2`, `MarkerRim sqrt(0.9)`,
   `MarkerRimMargin`, `MarkerMaxReachUv 0.17`, `MARKER_THK 0.09`, `MARKER_DOT_FRAC 0.05` and the
   near-black outline colour are copied from `NavballMarkers.glsl`
   ([src/NavballMarkerRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/NavballMarkerRenderer.cs#L16-L53),
   [current/Content/Core/Shaders/Common/NavballMarkers.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/NavballMarkers.glsl#L11-L36)).[^ksa-content-core-shaders-common-navballmarkers-glsl]
   `RimFade` reproduces the shader's inverted `smoothstep` so the glyph fades in near the rim
   ([src/NavballMarkerRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/NavballMarkerRenderer.cs#L225-L231)).
4. **Draw.** Marker centre is `ballCenter + screenDir.xy * ballExtends`. The glyph is a 10-vertex
   closed `AddPolyline` drawn twice (dark outline band, then amber stroke) plus a two-layer centre
   dot ([src/NavballMarkerRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/NavballMarkerRenderer.cs#L233-L257)). The label uses the game's
   `ImGuiHelper.DrawText(..., TextAlignment.Center)` so it gets the stock drop shadow
   ([src/NavballMarkerRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/NavballMarkerRenderer.cs#L300-L306), [current/decomp/KSA/ImGuiHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiHelper.cs#L542)).[^ksa-imguihelper-cs]
5. **Far side.** When the star is on the back hemisphere, past the rim, or within 22 deg of it, a
   filled triangle with an outline is drawn just inside the rim pointing toward the star's screen
   direction ([src/NavballMarkerRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/NavballMarkerRenderer.cs#L259-L298)).
6. **Fake depth.** The foreground list sits above everything, so the mod hides the glyph when its
   centre is inside any other enabled gauge canvas rectangle or the mod's own window
   (`GaugeBase.GetPixelsMin/GetPixelsSize`), because "the game keeps its UI coverage mask on the
   GPU with no CPU query" ([src/NavballMarkerRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/NavballMarkerRenderer.cs#L198-L223)). It also repaints the
   white T-shaped centre indicator (`CROSS_LENGTH`, `CROSS_THICKNESS`, `BORDER_PX` from [current/Content/Core/Shaders/GaugeNavball.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/GaugeNavball.frag#L395-L400)) over
   the glyph when they overlap ([src/NavballMarkerRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/NavballMarkerRenderer.cs#L308-L339)).

## Lifecycle and timing

- **Called from the sky overlay**, inside `StellariumRenderer.Draw` from `[StarMapAfterGui]`
  (`frame:draw-ui`), before the overlay window's `ImGui.End()`
  ([src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs#L111-L113)).[^oms-stellariumrenderer-cs]
- **Why the foreground list.** The navball canvas is an ordinary ImGui window that
  `GaugeCanvas.OnDrawUi` begins and fills with a draw callback (`AddCallback(_gaugeDrawCallback,
  ...)`), which renders the ball with Vulkan when ImGui's draw data is executed
  ([current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L819-L857)).[^ksa-gaugecanvas-cs] Those canvases are
  submitted in `Program.OnDrawUiThreadSafe`, after the StarMap after-GUI hook
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3178-L3210)).[^ksa-program-cs] Anything in a normal
  window would be at the mercy of window order; the foreground list is drawn last.
- **State read.** `vehicle.GetCtrl2Cci()` and the parent's `GetCce2Cci()` are main-thread values
  after this frame's `ApplyVehicleSolvers`; the marker therefore matches the ball, which is rendered
  from the same state.

## Key code

| Path | Role |
|---|---|
| [src/NavballMarkerRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/NavballMarkerRenderer.cs#L62-L120) | Gates, direction, fade, draw order |
| [src/NavballMarkerRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/NavballMarkerRenderer.cs#L122-L141) | CCE to CCI to control to navball screen |
| [src/NavballMarkerRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/NavballMarkerRenderer.cs#L143-L223) | Navball discovery and coverage test |
| [src/NavballMarkerRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/NavballMarkerRenderer.cs#L225-L339) | Fade, glyph, rim arrow, label, centre T |

## Snippets

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

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `NavBallRenderer.Body2Screen`, `ToScreenMarker` | [current/decomp/KSA/NavBallRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/NavBallRenderer.cs#L22-L86) | Reference maths copied by the mod |
| `Vehicle.GetCtrl2Cci()` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3137) | Control frame orientation |
| `IParentBody.GetCce2Cci()` | [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L53) | Ecliptic to body-inertial rotation |
| `GaugeCanvas.AllCanvases`, `Enabled`, `IsContextVisible`, `Components` | [current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L177-L238) | Find the navball canvas and other canvases |
| `CanvasRect(GaugeComponent)` | [current/decomp/KSA/CanvasRect.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CanvasRect.cs#L6-L18) | Ball centre and size in pixels |
| `GaugeBase.GetPixelsMin`, `GetPixelsSize` | [current/decomp/KSA/GaugeBase.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeBase.cs#L123-L134) | Coverage rectangles |
| `Program.EditorFlag`, `IsControlledVehicleActive` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L225-L295) | Same gates as the stock canvas |
| `Program.DrawUI`, `ControlledVehicle` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L502-L540) | HUD shown, vehicle present |
| `ImGuiHelper.DrawText` | [current/decomp/KSA/ImGuiHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiHelper.cs#L542) | Shadowed label |
| `ImGui.GetForegroundDrawList` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8038-L8040) | Topmost draw list |

## Pitfalls and version notes

- **Copied constants drift.** Any change to `NavballMarkers.glsl`, `GaugeNavball.frag`,
  `Body2Screen` or the navball canvas layout breaks the visual match silently. Re-check them on each
  game update.
- **Coverage is a point test.** Only the glyph centre is tested against canvas rectangles, so a
  glyph can still be half drawn over a neighbouring gauge.
- **Sol points at a fixed direction.** The marker always uses the catalog direction
  (`SkyCulturesRenderer.TryGetStarDirection`). Sol's AT-HYG row has no Hipparcos number (synthetic key)
  and RA/Dec 0/0, so a Sol marker sits at the ecliptic +X direction instead of the real Sun, unlike the
  pointer, the sky label and "Orient to Star", which special-case the central star.
- **Name-based lookup.** The canvas is found by `Id == "Navball"` and the component by
  `Name == "Ball"`; a mod that renames or replaces them disables the marker.
- **Foreground list is per viewport.** It is the main viewport's list; a navball dragged into a
  separate OS window (ImGui multi-viewport) would not get the marker.

## See also

- [AT-HYG star catalog and star information](hyg-star-catalog.md)
- [Orient a vessel to a star](orient-to-star.md)
- [Star pointer](star-pointer.md)
- [Draw a marker on the stock navball: replicate the game's marker maths and paint on the foreground draw list](../../../howto/ui/menus/work-with-stock-cockpit-gauges/work-with-stock-cockpit-gauges-5.md) - how-to article demonstrating this feature

[^oms-navballmarkerrenderer-cs]: OhMyStars src/NavballMarkerRenderer.cs
[^ksa-content-core-gauges-xml]: Content/Core/Gauges.xml (game content, build 2026.10.10.5554)
[^ksa-navballrenderer-cs]: KSA.NavBallRenderer (decompiled, build 2026.10.10.5554)
[^ksa-content-core-shaders-common-navballmarkers-glsl]: Content/Core/Shaders/Common/NavballMarkers.glsl (game content, build 2026.10.10.5554)
[^ksa-imguihelper-cs]: KSA.ImGuiHelper (decompiled, build 2026.10.10.5554)
[^oms-stellariumrenderer-cs]: OhMyStars src/StellariumRenderer.cs
[^ksa-gaugecanvas-cs]: KSA.GaugeCanvas (decompiled, build 2026.10.10.5554)
[^ksa-program-cs]: KSA.Program (decompiled, build 2026.10.10.5554)
