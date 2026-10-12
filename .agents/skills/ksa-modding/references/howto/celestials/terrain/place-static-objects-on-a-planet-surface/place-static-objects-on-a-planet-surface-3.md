---
type: How-To
title: Course frame anchored at the kitten and saved as latitude, longitude, heading and floor height
description: Anchor a mod-built structure to the ground at a kitten's feet in the body-fixed frame, level its floor above the highest ground, draw its meshes per viewport and save it as latitude, longitude, heading and height above ground
category: celestials
group: terrain
topic: place-static-objects-on-a-planet-surface
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksagolf/features/course-grid-and-save-file
  - mods/ksagolf/features/minigolf-course-builder
  - mods/ksagolf/features/mod-mesh-draw-hook
techniques: [custom-physics, harmony-postfix]
hooks: [starmap StarMapAfterGui, harmony-postfix KSA.SuperMeshRenderSystem.ClearBuckets]
lifecycle: [frame:draw-ui, frame:render]
game_types: [KSA.Celestial, KSA.Camera, KSA.IViewport, KSA.SuperMeshRenderSystem, KSA.Program]
tags: [anchor, ccf, save, latitude-longitude, floor, per-viewport-draw]
---
Outcome: a structure that the player builds beside a kitten (a minigolf course here) is anchored in the body-fixed frame, stands just above the highest ground under any of its corners, is drawn in every viewport and is saved so that a KSA update that reshapes the terrain moves the structure with it instead of burying it. Demonstrated by KSAGolf ([Course grid and save file](../../../../mods/ksagolf/features/course-grid-and-save-file.md), [Minigolf course builder](../../../../mods/ksagolf/features/minigolf-course-builder.md), [Mod mesh draw hook](../../../../mods/ksagolf/features/mod-mesh-draw-hook.md)).

## Background: how the game does it

Anything that must stay put on a rotating planet lives in the body-fixed frame (`Ccf`). The mod keeps four things: an up vector (the radial direction at the anchor), two horizontal axes `i` and `j`, and a floor radius. Converting to the ecliptic for the renderer is `body.GetPositionEcl() + Transform(local, body.GetCcf2Cce())` ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L532-L536)). The ground radius comes from the height query ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L789-L795); see [the height-query topic](../query-terrain-height-and-line-of-sight/overview.md)).

For drawing, the mod postfixes `SuperMeshRenderSystem.ClearBuckets(IViewport)`, which `Program.RenderViewport` (secondary viewports) and `Program.RenderGame` (the main viewport) call right after `SetViewOptions` and before they submit the kitten, parachute and seated-crew draws ([current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L546-L554)). A draw submitted earlier in the frame is collected and then cleared by that very call.

## Lifecycle and timing

Editing, input and the builder's panel run inside `[StarMapAfterGui]` (`frame:draw-ui`). The mesh draw runs in the `ClearBuckets` postfix, once per rendered viewport (`frame:render`), reading the frame's camera and body transform at that moment. Saving and loading are synchronous file IO from the panel. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Anchor at the kitten's feet.** Convert the feet to `Ccf`, take up as the unit radial, project the facing direction to the horizontal as `j`, derive `i = j x up`, and put the first cell a metre ahead:

```csharp
// src/KSAGolf/Ksa/CourseBuilder.cs L141-L156
private void AnchorAt(KittenEva kitten, Celestial body)
{
    double3 feet = ToCcf(body, KittenFrame.ToEcl(kitten, Vec.Zero));
    double3 up = Vec.Unit(feet);
    double3 facing = Vec.RejectFrom(double3.Transform(KittenFrame.DirectionToEcl(kitten, new double3(0, 0, 1)), body.GetCce2Ccf()), up);
    if (!(Vec.Len2(facing) > 1e-12)) facing = Vec.AnyPerpendicular(up);

    double3 j = Vec.Unit(facing);
    double3 i = Vec.Unit(Vec.Cross(j, up));

    // The first cell a metre ahead, centred on the kitten's line.
    Frame(body, feet + (j * 1.0) - (i * (CourseSize.CellM / 2.0)), i, j);
    _edits = new CourseEdits(Course.Empty);
    _view.Centre = new double2(0.5, 3.0);
    Name = "";
    Changed();
```

2. **Settle the ground and floor.** The frame samples the ground at the anchor (terrain plus sea clamp, see approach 1 of the height-query topic) and puts the floor 2 cm above it:

```csharp
// src/KSAGolf/Ksa/CourseBuilder.cs L159-L168
private void Frame(Celestial body, double3 anchorCcf, double3 i, double3 j)
{
    _body = body;
    _upCcf = Vec.Unit(anchorCcf);
    _iCcf = Vec.Unit(Vec.RejectFrom(i, _upCcf));
    _jCcf = Vec.Unit(Vec.Cross(_upCcf, _iCcf));
    _groundAtAnchor = new Golf.BodyGround(body).TrySurfaceRadius(_upCcf, out double r) ? r : Vec.Len(anchorCcf);
    _anchorCcf = _upCcf * _groundAtAnchor;
    _floorRadius = _groundAtAnchor + FloorAboveGroundM;
}
```

3. **Re-level to the highest corner** whenever the course changes, so no piece is buried:

```csharp
// src/KSAGolf/Ksa/CourseBuilder.cs L408-L429
// The floor just above the highest ground under any corner, so no piece is buried.
private void Relevel()
{
    if (_body is not { } body) return;

    var ground = new Golf.BodyGround(body);
    double high = _groundAtAnchor;
    foreach (Placed piece in Course.Pieces)
    {
        (int w, int h) = piece.Footprint;
        for (int a = 0; a <= w; a++)
        {
            for (int b = 0; b <= h; b++)
            {
                double3 corner = LocalToCcf(new double3((piece.Origin.I + a) * CourseSize.CellM, (piece.Origin.J + b) * CourseSize.CellM, 0));
                if (ground.TrySurfaceRadius(Vec.Unit(corner), out double r)) high = Math.Max(high, r);
            }
        }
    }

    _floorRadius = high + FloorAboveGroundM;
}
```

4. **Convert course-local metres** (X along `i`, Y along `j`, Z up from the floor) to `Ccf` and then to the ecliptic:

```csharp
// src/KSAGolf/Ksa/CourseBuilder.cs L716-L718
private double3 LocalToCcf(double3 local) => _anchorCcf + (_upCcf * (_floorRadius - _groundAtAnchor)) + (_iCcf * local.X) + (_jCcf * local.Y) + (_upCcf * local.Z);

private double3 LocalToEcl(Celestial body, double3 local) => body.GetPositionEcl() + double3.Transform(LocalToCcf(local), body.GetCcf2Cce());
```

5. **Save relative to the ground**, not as an absolute radius: latitude and longitude of the up vector (`asin(up.z)`, `atan2(up.y, up.x)`, the convention of `Celestial.GetDirCcfFromLatLon`), the heading of `j` against local north, and the floor height above the ground at the anchor:

```csharp
// src/KSAGolf/Ksa/CourseBuilder.cs L458-L468
double3 north = Vec.Unit(Vec.RejectFrom(new double3(0, 0, 1), _upCcf));
double3 east = Vec.Cross(north, _upCcf);
var where = new CourseFile
{
    Name = Name,
    Body = body.Id,
    LatitudeDeg = double.RadiansToDegrees(Math.Asin(Math.Clamp(_upCcf.Z, -1.0, 1.0))),
    LongitudeDeg = double.RadiansToDegrees(Math.Atan2(_upCcf.Y, _upCcf.X)),
    HeadingDeg = double.RadiansToDegrees(Math.Atan2(Vec.Dot(_jCcf, east), Vec.Dot(_jCcf, north))),
    FloorAboveGroundM = _floorRadius - _groundAtAnchor,
};
```

6. **Load by rebuilding the frame**, re-levelling against today's ground, and warn if the floor moved more than 5 cm from the saved height:

```csharp
// src/KSAGolf/Ksa/CourseBuilder.cs L510-L523
double lat = double.DegreesToRadians(file.LatitudeDeg), lon = double.DegreesToRadians(file.LongitudeDeg);
double3 up = new(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));
double3 north = Vec.Unit(Vec.RejectFrom(new double3(0, 0, 1), up));
double3 east = Vec.Cross(north, up);
double heading = double.DegreesToRadians(file.HeadingDeg);
double3 j = (north * Math.Cos(heading)) + (east * Math.Sin(heading));

Frame(body, up, Vec.Cross(j, up), j);
_edits = new CourseEdits(file.ToCourse(out List<string> unknown));
Name = file.Name.Length > 0 ? file.Name : name;
Changed();

double drift = (_floorRadius - _groundAtAnchor) - file.FloorAboveGroundM;
if (Math.Abs(drift) > 0.05) Log.Warn($"course '{Name}' stands {drift * 100:F0} cm off where it was saved: the ground under it has changed");
```

7. **Draw into every viewport.** Install the draw delegate through a postfix on `ClearBuckets`; the callback computes the origin relative to the viewport camera and hands per-piece axes to the mesh drawing:

```csharp
// src/KSAGolf/Ksa/ViewDrawHook.cs L30-L55
public static void Install(Action<IViewport> draw)
{
    _draw = draw;
    if (Installed) return;

    try
    {
        MethodInfo? target = typeof(SuperMeshRenderSystem).GetMethod(nameof(SuperMeshRenderSystem.ClearBuckets),
                                                                     BindingFlags.Public | BindingFlags.Instance, [typeof(IViewport)]);
        if (target is null)
        {
            Log.Warn("the ball cannot be drawn: KSA has no SuperMeshRenderSystem.ClearBuckets(IViewport)");
            return;
        }

        MethodInfo postfix = typeof(ViewDrawHook).GetMethod(nameof(AfterClearBuckets), BindingFlags.NonPublic | BindingFlags.Static)!;
        _harmony = new Harmony(HarmonyId);
        _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
        Installed = true;
        Log.Info("meshes of the mod's own are drawn, via SuperMeshRenderSystem.ClearBuckets");
    }
    catch (Exception e)
    {
        Log.Warn($"could not hook the viewport render, so the ball is not drawn: {e.Message}");
    }
}
```

```csharp
// src/KSAGolf/Ksa/CourseBuilder.cs L535-L546
public void Draw(IViewport viewport)
{
    if (_body is not { } body) return;
    if (!Active && Course.Pieces.Count == 0) return;

    double3 camera = viewport.GetCamera().PositionEcl;
    double3 origin = LocalToEcl(body, Vec.Zero) - camera;
    if (!Vec.IsFinite(origin) || Vec.Len(origin) > 2000.0) return;

    var axes = (I: DirectionToEcl(body, new double3(1, 0, 0)), J: DirectionToEcl(body, new double3(0, 1, 0)), Up: DirectionToEcl(body, new double3(0, 0, 1)));
    DrawMeshes(viewport, origin, axes.I, axes.J, axes.Up);
    if (!ReferenceEquals(viewport, Program.MainViewport)) return;
```

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Ksa/CourseBuilder.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseBuilder.cs#L141-L168) | Anchor, frame, re-level, save and load, draw |
| [src/KSAGolf/Ksa/ViewDrawHook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/ViewDrawHook.cs) | `ClearBuckets` postfix |
| [src/KSAGolf/Sim/CourseFile.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/CourseFile.cs) | Versioned JSON save record |

## Variations and gotchas

- **Saved as height above ground, not radius.** A KSA update that reshapes the terrain moves the course with it.
- **The hook must not throw.** It runs inside the engine's render; the mod drops the delegate and logs once on failure.
- **Public signature.** The hook depends on `SuperMeshRenderSystem.ClearBuckets(IViewport)`; if it cannot be found the mod logs and draws nothing.
- **Epoch.** The frame uses `body.GetPositionEcl()` and `GetCcf2Cce()` from the same end-of-step state the camera was positioned against (see the frames topic); mixing a step taken at another phase draws the structure hundreds of metres off.

## See also

- [Overview](overview.md); [approach 1](place-static-objects-on-a-planet-surface-1.md); [approach 2](place-static-objects-on-a-planet-surface-2.md)
- [Course grid and save file](../../../../mods/ksagolf/features/course-grid-and-save-file.md), [Mod mesh draw hook](../../../../mods/ksagolf/features/mod-mesh-draw-hook.md)
- [Draw mod meshes with the engine renderers](../../../rendering/meshes/draw-mod-meshes-with-the-engine-renderers/overview.md), [Save mod state with the scene](../../../persistence/saves/save-mod-state-with-the-scene/overview.md)
