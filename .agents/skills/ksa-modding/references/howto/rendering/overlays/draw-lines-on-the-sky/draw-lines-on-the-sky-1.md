---
type: How-To
title: ImGui window draw list from StarMapAfterGui with ego-space sky directions
description: Draw constellation lines, boundaries and names from catalog RA/Dec by converting to the game's ecliptic frame, placing the directions around the camera and stroking straight screen segments into a full-screen window draw list
category: rendering
group: overlays
topic: draw-lines-on-the-sky
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ohmystars/features/sky-overlay-lines-and-labels]
techniques: [imgui-drawlist, sky-projection, starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapAfterGui]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types:
  - KSA.Camera
  - KSA.Program
  - KSA.Universe
  - KSA.GalacticPlane
  - KSA.InstancedStarTechnique
  - KSA.ImGuiHelper
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImDrawListExtensions
tags: [sky, constellations, ra-dec, ecliptic, great-circle, imgui-drawlist, labels]
---

Outcome: constellation lines, constellation boundaries and star or constellation names drawn over
KSA's own star field from catalog data, correct from any camera position in the system and glued
to the star sprites while the camera turns. Everything is 2D ImGui geometry in an invisible,
click-through, full-screen window; no Vulkan code is involved. The recipe: RA/Dec (J2000) to a unit
vector, rotate into the game's ecliptic axes, place it around the camera (Oh My Stars multiplies by
19.5, the radius of KSA's star sphere), `Camera.EgoToScreen`, add the ImGui main viewport origin,
`AddLine`/`AddText`. Demonstrated by Oh My Stars
([Constellation lines and labels on the sky](../../../../mods/ohmystars/features/sky-overlay-lines-and-labels.md)).

Prerequisites: a StarMap mod project (a `[StarMapMod]` class referencing `StarMap.API`, the game
assemblies and `Brutal.ImGuiApi`), compiled with `AllowUnsafeBlocks` because the ImGui viewport is
a pointer. Projection background is in
[project world positions to screen](../project-world-positions-to-screen/project-world-positions-to-screen-2.md);
hiding lines behind planets is in
[CPU occlusion by bodies](../project-world-positions-to-screen/project-world-positions-to-screen-6.md).

What you should see: with the HUD visible, thin white lines joining the bright stars of each
constellation, names near each constellation's centre, and (if you add them) boundary polylines;
the lines stay on the stars while you rotate or move the camera and vanish when the HUD is hidden.

## Background: how the game does it

KSA's sky is star sprites on a sphere of radius 19.5 centred on the camera
(`InstancedStarTechnique.AddInstance` stores `NormalizeOrZero(forward) * distance`
[current/decomp/KSA/InstancedStarTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InstancedStarTechnique.cs#L68-L83)),
in ecliptic-aligned axes.[^ksa-instancedstartechnique-cs] The game converts catalog directions with
`GalacticPlane.EquatorialToEcliptic(double3)`
[current/decomp/KSA/GalacticPlane.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GalacticPlane.cs#L43-L48),
a public static rotation about X by the J2000 obliquity (0.4090928040284034 rad).[^ksa-galacticplane-cs]

Ego space is the ecliptic frame translated so the camera is at the origin (`Camera.EclToEgo` only
subtracts `PositionEcl`), and `Camera.OnFrame` builds the view matrix from the `Ego2View` rotation
alone
[current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L487-L497).
Two consequences matter for sky overlays:[^ksa-camera-cs]

- A direction placed around the ego origin projects to the same pixel at any radius: the
  perspective divide cancels the scale. 19.5 matches the sprites, the stock grid uses 1000, and
  either is right. What must not change is the centre: points placed around anything other than
  the camera drift against the stars as the camera moves.
- `Camera.EgoToScreen(double3, ignoreBehind = true)` returns `(NaN, NaN)` for a point behind the
  camera plane, and otherwise framebuffer pixels relative to the viewport
  [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L347-L355).

The stock galactic grid, `Universe.DrawGalacticGrid`
[current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1804-L1870)
(terminal command `skygrid`), is the reference for drawing on this sphere: it takes the viewport
camera, uses `ImGui.GetForegroundDrawList()`, steps each galactic parallel in 2 degree pieces,
projects `plane.GetDirectionEcl(l, b) * 1000.0` with `camera.EgoToScreen`, skips any piece with a
NaN endpoint, and adds the viewport position.[^ksa-universe-cs]

```csharp
// current/decomp/KSA/Universe.cs L1853-L1869
private static void DrawGalacticParallel(ImDrawListPtr drawList, float2 origin, Camera camera, GalacticPlane plane, double latitude, uint color, float thickness)
{
	float2 float5 = GalacticToScreen(camera, plane, 0.0, latitude);
	for (int i = 1; i <= 180; i++)
	{
		float2 float6 = GalacticToScreen(camera, plane, Math.PI * 2.0 * (double)i / 180.0, latitude);
		if (!float.IsNaN(float5.X) && !float.IsNaN(float6.X))
		{
			drawList.AddLine(origin + float5, origin + float6, color, thickness);
		}
		float5 = float6;
	}
}

private static float2 GalacticToScreen(Camera camera, GalacticPlane plane, double longitude, double latitude)
{
	return camera.EgoToScreen(plane.GetDirectionEcl(longitude, latitude) * 1000.0);
```

## Lifecycle and timing

| Step | Where | Why there |
|---|---|---|
| Parse catalog, sky cultures, boundaries; convert directions once | `[StarMapAllModsLoaded]` (`startup:all-mods-loaded`), main thread | Mod files are readable and nothing per-frame depends on them yet; the conversion is pure maths |
| Draw every frame | `[StarMapAfterGui]` (`frame:draw-ui`), a StarMap postfix on `Program.OnDrawUiViewports` | `OnFrameViewports` has already run `Camera.OnFrame`, so the view-projection and frustum planes match the image rendered this frame; ImGui is between `NewFrame` and `Render` |

`StarMapAfterGui` only fires inside `if (DrawUI)`, so the overlay disappears with the HUD; only the
main viewport and camera are drawn. The draw list is consumed later in the same frame by
`ImGuiBackend.Vulkan.RenderDrawData` inside the swapchain render pass, after the final composite
(`render:imgui`), so the lines always sit on top of planets; hiding them is the mod's job.
See rows 11 to 16 of the per-frame table and step 25 of `RenderGame` in
[the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).[^starmap-programpatcher-cs]

## Steps

1. **Wire the two hooks.** StarMap finds static methods by attribute on a `[StarMapMod]` class. The
   signatures are checked: `StarMapAllModsLoaded` takes no parameters and `StarMapAfterGui` takes
   one `double`; a method with the wrong signature is silently never called.[^starmap-base-attributes-cs] [^starmap-ongui-attributes-cs]

   ```csharp
   // src/OhMyStarsEntryPoint.cs L9-L26, L51-L54
   [StarMapMod]
   public class OhMyStarsEntryPoint {
       private static Harmony? _harmony;

       [StarMapAllModsLoaded]
       public static void OnFullyLoaded() {
           _harmony = new Harmony("nanonestor.ohmystars");

           OhMyStarsSettingsStore.Init();
           OhMyStarsSettingsStore.Load();
           SaveLoadObserver.ApplyPatches(_harmony);
           StarOrientationObserver.ApplyPatches(_harmony);

           StellariumRenderer.Init();
           OhMyStarsWindow.LoadSettings();

           StarsEditWindow.OnAllModsLoaded();
       }
   // ...
       [StarMapAfterGui]
       public static void OnAfterUi(double dt) {
           StellariumRenderer.Draw();
       }
   ```

   Only `StellariumRenderer.Init()` (catalog and boundary loading) and `StellariumRenderer.Draw()`
   matter for lines; the Harmony patches serve other Oh My Stars features.

2. **Convert catalog RA/Dec to the ecliptic once at load.** Build the J2000 equatorial unit vector
   (`x = cos ra cos dec`, `y = sin ra cos dec`, `z = sin dec`) and rotate about +X by the obliquity.
   Note the units: right ascension in **hours**, declination in degrees. This is the same rotation,
   written out, as the game's `GalacticPlane.EquatorialToEcliptic`; the two constants differ by
   about 0.04 arcsec.[^ohmystars-math-cs]

   ```csharp
   // src/Math.cs L148-L168
   public static class StarDirectionConverter {
       public static double3 RaDecToDirection(double raHours, double decDegrees) {
           double ra = raHours * System.Math.PI / 12.0;
           double dec = decDegrees * System.Math.PI / 180.0;

           double cosDec = System.Math.Cos(dec);

           // J2000 equatorial direction
           double x = System.Math.Cos(ra) * cosDec;
           double y = System.Math.Sin(ra) * cosDec;
           double z = System.Math.Sin(dec);

           // Rotate by -obliquity around X, matching the star catalog's equatorial-to-ecliptic conversion
           const double obliquityRadians = -23.439281 * System.Math.PI / 180.0;
           double cosObliquity = System.Math.Cos(obliquityRadians);
           double sinObliquity = System.Math.Sin(obliquityRadians);
           double yRotated = y * cosObliquity - z * sinObliquity;
           double zRotated = y * sinObliquity + z * cosObliquity;

           return new double3(x, yRotated, zRotated);
       }
   ```

   In your own mod call the game's function instead:
   `GalacticPlane.EquatorialToEcliptic(new double3(x, y, z))`. For **galactic** coordinates use the
   loaded system's plane, `Universe.CurrentSystem.GalacticPlane.GetDirectionEcl(longitudeRad, latitudeRad)`
   (an instance method that needs a loaded system,
   [current/decomp/KSA/GalacticPlane.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GalacticPlane.cs#L77-L87)),
   which is what the stock grid draws with. Keep one conversion path: the same direction feeds
   overlay drawing and any orient-to-star target.

3. **Create one transparent full-screen window** each frame and take its draw list. Oh My Stars
   sizes it to the main viewport and makes it input-less so clicks reach the game:

   ```csharp
   // src/StellariumRenderer.cs L346-L367
   public static ImDrawListPtr? CreateWindow(ImGuiViewport* viewport) {

       float2 window_size = viewport->Size;
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
       if(!ImGui.Begin("HUDFullscreenWindow", flags)) {
           ImGui.End();
           return null;
       }
       return ImGui.GetWindowDrawList();
   }
   ```

   Do not copy the window name: KSA_AircraftHUD begins a window with the same name, and ImGui
   would merge both mods' draws into one window. A window draw list follows ImGui window order;
   if the lines must be under every window, use the stock
   `ImGuiHelper.GetMainOverlayDrawList()` (the main viewport's background draw list,
   [current/decomp/KSA/ImGuiHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiHelper.cs#L43-L46))
   and skip the window entirely.[^ksa-imguihelper-cs]

4. **Project with the main camera and add the viewport origin.** `EgoToScreen` returns pixels
   relative to the viewport, while draw-list coordinates are absolute ImGui coordinates:

   ```csharp
   // src/StellariumRenderer.cs L56-L63
   public static float2 EgoToOverlayScreen(Camera camera, double3 position) {
       float2 screen = camera.EgoToScreen(position);
       ImGuiViewport* viewport = ImGui.GetMainViewport();

       return viewport == null
           ? screen
           : screen + viewport->Pos;
   }
   ```

5. **Draw the layers in one place each frame**, with `center = double3.Zero` (the camera) and
   `radius = 19.5`, ending the window when done:[^ohmystars-stellariumrenderer-cs]

   ```csharp
   // src/StellariumRenderer.cs L79-L114
   public static void Draw() {
       Camera camera = Program.GetMainCamera();
       ImGuiViewport* viewport = ImGui.GetMainViewport();

       if(camera == null || viewport == null)
           return;

       ImDrawListPtr? draw_list = CreateWindow(viewport);
       if(draw_list == null)
           return;

       IParentBody? occluderRoot = GetOccluderRoot(camera.Following);
       if(occluderRoot is null) {
           celestialOccluders.Clear();
       } else {
           CollectCelestialOccluders(camera, occluderRoot);
       }

       // StarsEdit places stars at this fixed distance from the camera, so the overlays must use
       // the same camera-centered sphere to avoid a camera-offset parallax error.
       double3 center = double3.Zero;
       double radius = StarDistance;

       SkyMarkingsRenderer.Draw(draw_list.Value, camera, center, radius);
       SkyCulturesRenderer.Draw(draw_list.Value, camera, center, radius, showAsterisms, showAsterismNames, showStarNames);
       SkyCulturesRenderer.DrawSelectedDropdownStarName(draw_list.Value, camera, center, radius);

       if(showIAUConstellations)
           IAUConstellationsRenderer.Draw(draw_list.Value, camera, center, radius);

       DrawStarPointer(draw_list.Value, camera, radius);

       NavballMarkerRenderer.Draw();

       ImGui.End();
   }
   ```

   `Program.GetMainCamera()` is `MainViewport.GetCamera()`
   ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L631-L634)).[^ksa-program-cs]
   The occluder list is the per-frame sphere list used by `IsVisibleFromCamera` below.

6. **Stroke asterism segments:** for every resolved segment place both endpoints, apply the culling
   tests, project, and `AddLine` with a 2 px thickness:[^ohmystars-skyculturesrenderer-cs]

   ```csharp
   // src/SkyCulturesRenderer.cs L219-L237
   public static void DrawAsterisms(ImDrawListPtr draw_list, Camera camera, double3 center, double radius) {
       ImColor8 lineColor = StellariumRenderer.ToLineColor(StellariumRenderer.asterismLineColor, StellariumRenderer.asterismLineOpacity);

       foreach(ResolvedConstellationSegment segment in resolvedSegments) {
           double3 a = center + StellariumRenderer.ApplyAlignment(segment.A) * radius;
           double3 b = center + StellariumRenderer.ApplyAlignment(segment.B) * radius;

           double3 midpoint = (a + b) * 0.5d;
           if((camera.IsPointWithinFov(a) || camera.IsPointWithinFov(b) || camera.IsPointWithinFov(center)) &&
               StellariumRenderer.IsVisibleFromCamera(a) &&
               StellariumRenderer.IsVisibleFromCamera(midpoint) &&
               StellariumRenderer.IsVisibleFromCamera(b)) {
               ImDrawListExtensions.AddLine(
                   draw_list,
                   StellariumRenderer.EgoToOverlayScreen(camera, a),
                   StellariumRenderer.EgoToOverlayScreen(camera, b),
                   lineColor,
                   2f);
           }
   ```

   `ApplyAlignment` is the user's optional fine rotation (zero by default). Segments are resolved
   once per sky-culture switch from Hipparcos ids, and only between stars the game actually
   renders.

7. **Labels.** A constellation's label direction is the normalised sum of the directions of every
   star its segments use (computed once per culture switch in `ResolveSegments`,
   [src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L375-L405)).
   Each frame the label is drawn at its projected point if it is inside the field of view and not
   hidden by a body; text starts at the point, with no centring:

   ```csharp
   // src/SkyCulturesRenderer.cs L252-L275
   public static void DrawAsterismNames(
       ImDrawListPtr draw_list,
       Camera camera,
       double3 center,
       double radius) {

       foreach(ResolvedConstellationLabel label in resolvedLabels) {
           double3 position = center + StellariumRenderer.ApplyAlignment(label.Direction) * radius;

           float2 screen = StellariumRenderer.EgoToOverlayScreen(camera, position);

           if (!camera.IsPointWithinFov(position)) {
               continue;
           }
           if(!StellariumRenderer.IsVisibleFromCamera(position)) {
               continue;
           }
           ImDrawListExtensions.AddText(
               draw_list,
               screen,
               white,
               label.Text);
       }
   }
   ```

   For centred, non-overlapping labels see approach 2.

8. **Boundaries:** rows `RA(hours) Dec(deg) key` of `lines_in_20.txt` become one segment per pair
   of consecutive rows with the same key, so each key is a polyline. The shipped file is already
   densified (consecutive points at most 1 degree apart), so no subdivision is needed. Drawing is
   the same as step 6 but with occlusion tests only, no field-of-view test.[^ohmystars-iauconstellationsrenderer-cs]

   ```csharp
   // src/IAUConstellationsRenderer.cs L50-L78
   foreach(string rawLine in File.ReadLines(path)) {
       string line = rawLine.Trim();

       if(line.Length == 0) {
           continue;
       }

       string[] parts = line.Split(
           (char[]?)null,
           StringSplitOptions.RemoveEmptyEntries);

       if(parts.Length < 3) {
           continue;
       }

       double raHours = double.Parse(parts[0], CultureInfo.InvariantCulture);
       double decDegrees = double.Parse(parts[1], CultureInfo.InvariantCulture);
       string key = parts[2];

       double3 point = StarDirectionConverter.RaDecToDirection(raHours, decDegrees);

       if(hasPreviousPoint && key == previousKey) {
           segments.Add(new Segment(previousPoint, point));
       }

       previousPoint = point;
       previousKey = key;
       hasPreviousPoint = true;
   }
   ```

9. **Your own version: fix the three gaps.** Oh My Stars passes NaN endpoints to `AddLine`, reuses
   a shared window name and has no `try`/`finally` around `Begin`/`End`. A minimal draw routine
   for your mod that keeps its recipe and closes those gaps; `Directions` stands for your list of
   ecliptic unit-vector pairs built in step 2:

   ```csharp
   // adapted from src/StellariumRenderer.cs L56-L114, L346-L367 and current/decomp/KSA/Universe.cs L1853-L1869
   [StarMapAfterGui]
   public static unsafe void OnAfterGui(double dt)
   {
       Camera camera = Program.GetMainCamera();
       ImGuiViewport* viewport = ImGui.GetMainViewport();
       if (camera == null || viewport == null)
           return;

       ImGui.SetNextWindowPos(viewport->Pos);
       ImGui.SetNextWindowSize(viewport->Size);
       ImGui.SetNextWindowViewport(viewport->ID);
       bool open = ImGui.Begin("##MySkyLinesOverlay", OverlayFlags); // the nine flags from step 3
       try
       {
           if (!open)
               return;
           ImDrawListPtr drawList = ImGui.GetWindowDrawList();
           float2 origin = viewport->Pos;
           ImColor8 color = new ImColor8(255, 255, 255, 160);
           foreach ((double3 a, double3 b) in Directions)
               DrawSkySegment(drawList, camera, origin, a, b, color);
       }
       finally
       {
           ImGui.End(); // End is required whether or not Begin returned true
       }
   }

   // Split long segments into pieces of at most 2 degrees (normalised lerp stays on the great
   // circle) so a segment crossing the camera plane loses only the piece that crosses it.
   static void DrawSkySegment(ImDrawListPtr drawList, Camera camera, float2 origin, double3 a, double3 b, ImColor8 color)
   {
       double angle = Math.Acos(Math.Clamp(double3.Dot(a, b), -1.0, 1.0));
       int pieces = Math.Max(1, (int)Math.Ceiling(angle / (2.0 * Math.PI / 180.0)));
       float2 previous = camera.EgoToScreen(a * 19.5);
       for (int i = 1; i <= pieces; i++)
       {
           double3 direction = double3.Normalize(a + (b - a) * ((double)i / pieces));
           float2 next = camera.EgoToScreen(direction * 19.5);
           if (!float.IsNaN(previous.X) && !float.IsNaN(next.X))
               drawList.AddLine(origin + previous, origin + next, color, 2f);
           previous = next;
       }
   }
   ```

   Add `IsVisibleFromCamera`-style occlusion from
   [approach 6 of the projection topic](../project-world-positions-to-screen/project-world-positions-to-screen-6.md)
   if lines must disappear behind planets.

## Sub-step: great circles without subdivision

The points sit on a sphere centred on the eye and the camera is a perspective projection, which is
a gnomonic projection of that sphere: every great circle maps to a straight screen line. A straight
`AddLine` between two projected stars is therefore the exact great-circle arc, as long as both
endpoints are in front of the camera. Subdividing a great-circle segment (step 9) does not change
its shape; it only limits what is lost when one end is behind the camera. Small circles
(declination parallels, a circle of fixed angular radius around a star) are not great circles and
must be chopped into short pieces; see approach 2.

## Complete example

| File | Role |
|---|---|
| [src/OhMyStarsEntryPoint.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsEntryPoint.cs#L9-L54) | StarMap hooks: load at `StarMapAllModsLoaded`, draw at `StarMapAfterGui` |
| [src/Math.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/Math.cs#L148-L191) | RA/Dec to ecliptic and fine alignment |
| [src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs) | Window, projection helper, sphere placement, occluders |
| [src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs) | Catalog load, segment and label resolution, asterisms, labels |
| [src/IAUConstellationsRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/IAUConstellationsRenderer.cs#L17-L79) | Boundary polylines: load and draw |

## Variations and gotchas

- **NaN endpoints are not filtered** in Oh My Stars: segments with an endpoint behind the camera
  pass NaN to `AddLine`. Skip them as the stock grid does, after subdividing long segments
  (step 9).
- **The asterism field-of-view test is effectively off:** the third operand tests
  `IsPointWithinFov(center)` with `center` at the eye, where the side planes pass through the
  origin, so it returns true and every segment reaches the occlusion tests (inferred from the
  decomp, not measured).
- **Wrong frame, wrong place.** Feeding raw equatorial vectors (skipping the obliquity rotation)
  tilts every line by about 23.4 degrees against the stars; feeding RA in degrees to a function
  that expects hours scatters them.
- **Forgetting the viewport origin** shifts everything by the main viewport position, which is
  not always zero.
- **Hidden HUD.** The overlay vanishes with the HUD because `StarMapAfterGui` does not run. To keep
  it, draw from a Harmony prefix on `Program.OnDrawUiConsole` instead (see
  [Keep mod UI and per-frame work running when the HUD is hidden](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md)).
- **Secondary camera windows get nothing.** To draw there too, project with each viewport's own
  camera and offset, as the stock grid does from `Universe.OnDrawUi(IGameViewport)`.
- **Fixed J2000.** No precession, nutation or proper motion; this matches the engine's own J2000
  constants, not any planet's in-game spin axis.
- **Shared window name.** KSA_AircraftHUD also begins `HUDFullscreenWindow`; with both installed
  ImGui appends both draws to one window. Use a `##`-prefixed unique name.
- **`Begin`/`End` pairing.** Any exception between them would leave the window open for the rest of
  the frame; guard with `try`/`finally`.

## See also

- [Overview](overview.md)
- [Approach 2: one-degree arcs, frustum cull and label rectangles](draw-lines-on-the-sky-2.md)
- [Constellation lines and labels on the sky](../../../../mods/ohmystars/features/sky-overlay-lines-and-labels.md), [Occlusion by bodies](../../../../mods/ohmystars/features/overlay-occlusion-by-bodies.md)
- [Project world and sky positions to screen pixels and point at off-screen targets](../project-world-positions-to-screen/overview.md), [Draw full-screen click-through overlays and choose the right ImGui draw list](../../../ui/overlays/draw-a-fullscreen-click-through-overlay/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)

[^ksa-instancedstartechnique-cs]: KSA.InstancedStarTechnique (decompiled, build 2026.10.10.5554)
[^ksa-galacticplane-cs]: KSA.GalacticPlane (decompiled, build 2026.10.10.5554)
[^ksa-camera-cs]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-universe-cs]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^starmap-programpatcher-cs]: StarMap StarMap.Core/Patches/ProgramPatcher.cs
[^starmap-base-attributes-cs]: StarMap StarMap.API/BaseAttributes.cs
[^starmap-ongui-attributes-cs]: StarMap StarMap.API/OnGuiAttributes.cs
[^ohmystars-math-cs]: OhMyStars/src/Math.cs
[^ksa-imguihelper-cs]: KSA.ImGuiHelper (decompiled, build 2026.10.10.5554)
[^ohmystars-stellariumrenderer-cs]: OhMyStars/src/StellariumRenderer.cs
[^ksa-program-cs]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ohmystars-skyculturesrenderer-cs]: OhMyStars/src/SkyCulturesRenderer.cs
[^ohmystars-iauconstellationsrenderer-cs]: OhMyStars/src/IAUConstellationsRenderer.cs
