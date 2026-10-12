---
type: Mod Feature
title: Constellation lines and labels on the sky
description: Convert J2000 RA/Dec to the game ecliptic frame, place points on the camera-centred 19.5-unit star sphere, project with Camera.EgoToScreen and draw lines and labels into a transparent full-screen ImGui window from StarMapAfterGui
mod: ohmystars
category: rendering
techniques: [imgui-drawlist, starmap-lifecycle, sky-projection, gnomonic-projection]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapAfterGui]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types:
  - KSA.Camera
  - KSA.Program
  - KSA.GalacticPlane
  - KSA.InstancedStarTechnique
  - KSA.Universe
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImDrawListExtensions
tags: [sky, constellations, imgui, drawlist, projection, camera, ecliptic, j2000, great-circle]
---

Oh My Stars draws asterism lines, constellation names, star names and IAU constellation boundaries
over KSA's own star field, from any camera position in the system. Everything is 2D ImGui geometry:
each catalog direction is turned into a point on the same camera-centred sphere the game uses for
its star sprites, projected with the main camera, and stroked into the draw list of an invisible
full-screen ImGui window from `[StarMapAfterGui]`.[^oms-stellariumrenderer-cs]

The mechanism in one line: RA/Dec (J2000) to unit vector, rotate by the J2000 obliquity into the
game's ecliptic axes, multiply by 19.5, `Camera.EgoToScreen`, add the ImGui main viewport origin,
`AddLine` / `AddText`.

## How it works

1. **Celestial to ecliptic, once at load.** `StarDirectionConverter.RaDecToDirection` builds the
   J2000 equatorial unit vector (`x = cos ra cos dec`, `y = sin ra cos dec`, `z = sin dec`) and
   rotates it about +X by the obliquity 23.439281 deg
   ([src/Math.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/Math.cs#L148-L168)).[^oms-math-cs] This is the same rotation, written out, as the
   game's own `GalacticPlane.EquatorialToEcliptic`, which uses `J2000_OBLIQUITY_DEGREES = 23.4392911`
   ([current/decomp/KSA/GalacticPlane.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GalacticPlane.cs#L20-L55)); the two constants differ by about 0.04
   arcsec.[^ksa-galacticplane-cs] The result is in the ecliptic ("Ecl"/CCE) axes every KSA
   position uses. There is no precession, nutation, proper motion or epoch handling: the catalog,
   the sky-culture lines and the boundary file are all treated as fixed J2000 directions. Star
   directions are computed in `LoadCatalog`, boundary points in `LoadConstellationLines`, grid
   points in `SkyMarkingsRenderer.Rebuild`.
2. **Optional fine alignment, per frame.** `StellariumRenderer.ApplyAlignment` rotates every
   direction about the X, then Y, then Z axis by the user's "Alignment" degrees (default 0,
   persisted in `settings.ini`) before it is placed ([src/Math.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/Math.cs#L174-L190),
   [src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs#L48-L54)). The UI calls it "mainly a debug feature" for
   residual offsets against the game's star field.
3. **Place on the game's star sphere.** `Draw` uses `center = double3.Zero` and
   `radius = StarDistance = 19.5`, so a point is `direction * 19.5` in the camera's ego frame
   ([src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs#L79-L114)). Ego space in KSA is ecliptic-aligned with the
   camera at the origin: `Camera.EclToEgo` only subtracts `PositionEcl`
   ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L228-L233)).[^ksa-camera-cs] The game puts its stars on
   exactly this sphere: `InstancedStarTechnique.AddInstance` stores `NormalizeOrZero(forward) *
   distance` and `Mod.LoadStarBinaries` passes `distance: 19.5f`
   ([current/decomp/KSA/InstancedStarTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InstancedStarTechnique.cs#L68-L83),
   [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L231-L285)), and `Star.vert` transforms that position by
   `global.camera.viewProjection` ([current/Content/Core/Shaders/Star.vert](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Star.vert)), whose view
   part is a pure rotation built from `Ego2View` in `Camera.OnFrame`
   ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L487-L497)).[^ksa-instancedstartechnique-cs] [^ksa-content-core-shaders-star-vert]
   Using the same sphere means the overlay has no parallax
   against the sprites wherever the camera is; the comment in `Draw` says so explicitly.
4. **Project.** `EgoToOverlayScreen` calls `camera.EgoToScreen(position)` and adds
   `ImGui.GetMainViewport()->Pos` ([src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs#L56-L63)).
   `EgoToScreen(double3, ignoreBehind = true)` returns `(NaN, NaN)` when the point is behind the
   camera (`Dot(posEgo, GetForwardEcl()) < 0`), otherwise clip to NDC to framebuffer pixels via
   `NdcToScreen` ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L347-L355),
   [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L394-L397)). Pixels are relative to the framebuffer, hence
   the viewport offset; the stock sky grid does the same with `inViewport.Position`.
5. **Draw into a transparent full-screen window.** `CreateWindow` sizes a window to the main
   viewport with `NoTitleBar | NoResize | NoMove | NoCollapse | NoBringToFrontOnFocus |
   NoBackground | NoFocusOnAppearing | NoInputs | NoNavFocus`, calls
   `ImGui.Begin("HUDFullscreenWindow", flags)` and returns `ImGui.GetWindowDrawList()`
   ([src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs#L346-L367)). It is not the background or the foreground draw
   list. Asterism and boundary lines use `ImDrawListExtensions.AddLine(..., 2f)` and labels
   use `AddText` in white ([current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L51-L57)).
   `Draw` ends the window with `ImGui.End()` after the navball marker has also been drawn.
6. **Asterisms.** For every resolved segment of the active sky culture, both endpoints are placed
   and projected and a straight screen line is drawn
   ([src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L219-L250)).[^oms-skyculturesrenderer-cs]
7. **Constellation and star labels.** A constellation's label direction is the normalised sum of
   the directions of every star its segments use, computed once per culture switch in
   `ResolveSegments` ([src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L333-L407)). Star-name labels are the
   catalog display names of the stars that appear in segments. Labels are drawn at the projected
   point with no centring ([src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L252-L298)).
8. **IAU boundaries.** `lines_in_20.txt` rows are `RA(hours) Dec(deg) key`; consecutive rows with
   the same key become one segment, so each key is a polyline
   ([src/IAUConstellationsRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/IAUConstellationsRenderer.cs#L41-L79)).[^oms-iauconstellationsrenderer-cs] The
   file is already densified: its 6,524 consecutive same-key pairs are all at most 1 deg apart
   (computed from the shipped file).

### Great circles without subdivision

The mod never subdivides an asterism segment, and it does not need to. The points sit on a sphere
centred on the eye and the camera is a perspective projection (`CreatePerspectiveFieldOfViewReverseZ`,
[current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L471-L485)), which is a gnomonic projection of that sphere:
every great circle maps to a straight screen line. A straight `AddLine` between two projected
stars is therefore the exact great-circle arc, as long as both endpoints are in front of the
camera. Small circles are different: declination parallels are not great circles, which is why
the RA/Dec grid chops everything into 1 deg pieces (see [RA/Dec grid](ra-dec-grid.md)).

### Culling

| Layer | Field-of-view test | Behind-camera handling | Occlusion |
|---|---|---|---|
| Asterism lines | `IsPointWithinFov(a) \|\| IsPointWithinFov(b) \|\| IsPointWithinFov(center)` | none explicit | a, midpoint, b |
| Boundaries | none | none explicit | a, midpoint, b |
| Labels | `IsPointWithinFov(position)` | covered by the FOV test | the label point |

`Camera.IsPointWithinFov` rejects points behind the near-plane normal and outside the four side
planes; it ignores the last two planes ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L529-L545)).
Occlusion is the mod's own ray-sphere test against planets, moons, the star and vehicles
([Occlusion by bodies](overlay-occlusion-by-bodies.md)). There is no distance, altitude,
daylight or atmosphere fading: line opacity is a fixed user setting.

### Stock reference: the galactic sky grid

KSA has its own sky overlay, toggled with the `skygrid` terminal command and drawn by
`Universe.DrawGalacticGrid` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1804-L1870)).[^ksa-universe-cs]
It takes the camera from the viewport, uses `ImGui.GetForegroundDrawList()`, steps each galactic
parallel in 2 deg pieces (180 per circle), projects `plane.GetDirectionEcl(l, b) * 1000.0` with
`camera.EgoToScreen`, skips any piece with a `NaN` endpoint, and adds `inViewport.Position`. It runs
from `Universe.OnDrawUi`, inside `Program.OnDrawUiThreadSafe`. Oh My Stars follows the same
projection recipe but uses a window draw list, a 19.5 radius and no `NaN` check.

## Lifecycle and timing

- **Hook: `frame:draw-ui`, after the viewports.** `OnAfterUi` is `[StarMapAfterGui]`
  ([src/OhMyStarsEntryPoint.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsEntryPoint.cs#L51-L54)), which StarMap invokes from a Harmony postfix on
  `Program.OnDrawUiViewports` ([StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L26-L36)).[^starmap-patches-programpatcher-cs]
  In `Program.OnFrame` that call comes after `PrepareImGui` and `OnFrameViewports` and inside
  `if (DrawUI)` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2286)).[^ksa-program-cs]
- **Camera state is current.** `OnFrameViewports` runs the viewport cameras' `OnFrame`, which
  rebuilds `_vp.viewProjection` and the frustum planes this frame
  ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L487-L513)), so `EgoToScreen` and `IsPointWithinFov` match
  the image that will be rendered.
- **The next physics step is already running.** `Program.PrepareFrame` applied the previous step and
  dispatched the next one before the ImGui frame began, so the body and vehicle positions read for
  occlusion are the main-thread copies from the last apply; the workers do not write them until the
  next frame's apply.
- **Ordering against the HUD.** The overlay window is begun before `OnDrawUiThreadSafe` draws the
  menu bar, gauge canvases (including the navball) and `Universe.OnDrawUi`, and before
  `ImGui.Render`. The ImGui pass is composited over the rendered scene, so the lines always sit on
  top of planets; hiding them behind bodies is the mod's job.
- **Hidden HUD hides the sky overlay.** `StarMapAfterGui` is skipped when `Program.DrawUI` is
  false, so lines, labels and grid disappear with the HUD. Only the main viewport is drawn
  (`Program.GetMainCamera()`, `ImGui.GetMainViewport()`); secondary camera viewports get nothing.
- **Load-time work** (`StellariumRenderer.Init` from `[StarMapAllModsLoaded]`) parses the catalog,
  sky cultures and boundaries on the main thread before the game window shows a universe
  ([src/OhMyStarsEntryPoint.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsEntryPoint.cs#L13-L26)).

## Key code

| Path | Role |
|---|---|
| [src/Math.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/Math.cs#L148-L190) | RA/Dec to J2000 equatorial to ecliptic; fine-alignment rotation |
| [src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs#L79-L114) | Per-frame entry: camera, window, occluders, layers, pointer, navball marker |
| [src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs#L346-L367) | Transparent full-viewport input-less window and its draw list |
| [src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs#L56-L71) | Ego to overlay pixels, colour packing |
| [src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L219-L298) | Asterism lines, constellation names, star names |
| [src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L333-L407) | Segment resolution and label centroids |
| [src/IAUConstellationsRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/IAUConstellationsRenderer.cs#L17-L79) | Boundary polylines: load and draw |

## Snippets

```csharp
// src/Math.cs L149-L168
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

```csharp
// src/StellariumRenderer.cs L56-L63, L346-L367
    public static float2 EgoToOverlayScreen(Camera camera, double3 position) {
        float2 screen = camera.EgoToScreen(position);
        ImGuiViewport* viewport = ImGui.GetMainViewport();

        return viewport == null
            ? screen
            : screen + viewport->Pos;
    }
// ...
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

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `Program.GetMainCamera()` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L631-L634) | The camera of the main viewport |
| `Camera.EgoToScreen(double3, bool)` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L347-L355) | Ego position to framebuffer pixels, `NaN` behind the camera |
| `Camera.IsPointWithinFov(double3)` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L529-L545) | Label and asterism visibility |
| `Camera.EclToEgo`, `Camera.OnFrame` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L228-L233) | Defines the ecliptic-aligned, camera-centred ego frame |
| `GalacticPlane.EquatorialToEcliptic` | [current/decomp/KSA/GalacticPlane.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GalacticPlane.cs#L43-L48) | Reference for the obliquity rotation (not called) |
| `InstancedStarTechnique.AddInstance` | [current/decomp/KSA/InstancedStarTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InstancedStarTechnique.cs#L68-L83) | Reference for the 19.5 star sphere (not called by the overlay) |
| `Universe.DrawGalacticGrid` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1813-L1841) | Stock sky-overlay pattern |
| `ImGui.GetMainViewport`, `Begin`, `GetWindowDrawList`, `End` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8028-L8030) | Overlay window |
| `ImDrawListExtensions.AddLine`, `AddText` | [current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L211-L220) | Strokes and labels |

## Pitfalls and version notes

- **`NaN` endpoints are not filtered.** Asterism and boundary segments are emitted even when one
  endpoint is behind the camera, so `AddLine` receives `NaN` coordinates instead of a clipped
  line. The stock grid checks `float.IsNaN` and skips the piece; copy that. What ImGui and the GPU
  do with the `NaN` vertices was not verified.
- **The asterism field-of-view test is effectively off.** The third operand tests
  `IsPointWithinFov(center)` with `center = double3.Zero`, the eye itself. For a perspective
  camera the four side planes extracted in `ExtractFrustumPlanesEgo` pass through the origin
  (their `D` comes from `M44` and `M41`/`M42` of a rotation-only view times a perspective
  projection, which are zero), so the test returns true and every segment goes on to the occlusion
  tests (inferred from the decomp, not measured in game).
- **Window draw list, not a fixed layer.** Because the overlay is an ordinary ImGui window, its
  stacking against other windows follows ImGui window order; it is neither guaranteed behind nor in
  front of all UI. `GetBackgroundDrawList()` would put it strictly under every window.
- **Shared window name.** KSA_AircraftHUD also begins a window named `HUDFullscreenWindow`
  ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1012)).[^aircrafthud-aircrafthudmain-cs] With both mods installed, ImGui
  appends both draws to one window. Use a unique name such as `##OhMyStarsOverlay` in your own mod.
- **Fixed J2000.** Without precession the lines are a J2000 sky. The game's galactic pole data and
  obliquity are also J2000 values, so this matches the engine; it is not tied to any planet's
  in-game spin axis.
- **Begin/End pairing.** `CreateWindow` calls `End` itself when `Begin` returns false and the
  caller returns early; otherwise `Draw` calls `End`. Any exception between them would leave the
  window open for the rest of the frame.

## See also

- [Occlusion by bodies](overlay-occlusion-by-bodies.md)
- [RA/Dec grid](ra-dec-grid.md)
- [Stellarium sky cultures](stellarium-sky-cultures.md)
- [AT-HYG star catalog and star information](hyg-star-catalog.md)
- [Star pointer](star-pointer.md)
- [Game loop and mod lifecycle](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [ImGui window draw list from StarMapAfterGui with ego-space sky directions](../../../howto/rendering/overlays/draw-lines-on-the-sky/draw-lines-on-the-sky-1.md) - how-to article demonstrating this feature
- [Ego-space direction times a sphere radius, then Camera.EgoToScreen plus viewport origin](../../../howto/rendering/overlays/project-world-positions-to-screen/project-world-positions-to-screen-2.md) - how-to article demonstrating this feature
- [Full-screen overlay: a NoInputs, NoBackground window sized to the main viewport](../../../howto/ui/overlays/draw-a-fullscreen-click-through-overlay/draw-a-fullscreen-click-through-overlay-1.md) - how-to article demonstrating this feature

[^oms-stellariumrenderer-cs]: OhMyStars src/StellariumRenderer.cs
[^oms-math-cs]: OhMyStars src/Math.cs
[^ksa-galacticplane-cs]: KSA.GalacticPlane (decompiled, build 2026.10.10.5554)
[^ksa-camera-cs]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-instancedstartechnique-cs]: KSA.InstancedStarTechnique (decompiled, build 2026.10.10.5554)
[^ksa-content-core-shaders-star-vert]: Content/Core/Shaders/Star.vert (game content, build 2026.10.10.5554)
[^oms-skyculturesrenderer-cs]: OhMyStars src/SkyCulturesRenderer.cs
[^oms-iauconstellationsrenderer-cs]: OhMyStars src/IAUConstellationsRenderer.cs
[^ksa-universe-cs]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^starmap-patches-programpatcher-cs]: StarMap StarMap.Core/Patches/ProgramPatcher.cs
[^ksa-program-cs]: KSA.Program (decompiled, build 2026.10.10.5554)
[^aircrafthud-aircrafthudmain-cs]: KSA_AircraftHUD AircraftHUDMain.cs
