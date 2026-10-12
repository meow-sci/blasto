---
type: How-To
title: Surface normal from four one-metre samples and a cross product
description: Compute a unit surface normal by sampling the height field one metre east, west, north and south of a point, crossing the two chords, flipping outward and falling back to radial
category: celestials
group: terrain
topic: measure-slope-and-detect-ground-contact-on-terrain
approach: 2
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/sphinx-glb-statics-placement]
techniques: [glb-model, closed-form-geometry]
hooks:
  - starmap StarMapBeforeGui (via unscience ISubmod.Update)
  - starmap StarMapAfterGui (via unscience ISubmod.RenderContent beside-vessel button and ISubmod.RenderFloatingWindows armed terrain click)
  - harmony-postfix KSA.StaticObjectRenderer.UpdateRenderData
lifecycle: [frame:draw-ui, frame:render]
game_types: [KSA.Celestial, KSA.Camera]
tags: [terrain, normal, slope, placement, glb]
---
Outcome: a unit surface normal in the body-fixed (`Ccf`) frame at a point on the ground, used to tilt a placed model so it sits flush on a hill. Demonstrated by unscience's Sphinx statics placement ([Sphinx GLB statics placement](../../../../mods/unscience/features/sphinx-glb-statics-placement.md)).

## Background: how the game does it

The engine has no surface-normal query for mod use. The height field is asked along a body-fixed direction with `GetTerrainHeightFromDirCcf(dir, accurate)` ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L789-L795)); the surface point is `direction * (MeanRadius + height)`. A normal comes from four of those points around the one you care about, two chords across them, and a cross product.

## Lifecycle and timing

Sphinx computes the anchor, and with it the normal, in the panel's ImGui pass (`frame:draw-ui`, the `[StarMapAfterGui]` hook calls `RenderWindow`): the "Place beside controlled vessel" button calls `GroundPlacement.BesideControlled` and the armed click calls `GroundPlacement.TryCursor`, each handing the finished `GroundAnchor` to `QueuePlacement`. Only the entry creation (GLB import, collider build) is queued; it runs in the submod's `Update` (a `[StarMapBeforeGui]` hook, also `frame:draw-ui`) after the vehicle and cloth workers have been waited on. Re-grounding an existing entry (`GroundPlacement.At` in the edit panel) happens in the same UI pass. The normal is stored in the anchor and never recomputed per frame; the render hook only builds a camera-relative matrix from it each frame (`frame:render`, see [place a static object](../place-static-objects-on-a-planet-surface/overview.md)). Because it is body-fixed, it survives rotation and floating-origin shifts. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Define the surface function** from the `Ccf` height query, throwing when the field is unavailable:

```csharp
// sphinx.lib/GroundPlacement.cs L85-L90
private static double3 Surface(Celestial body, double3 direction)
{
    double radius = body.MeanRadius + body.GetTerrainHeightFromDirCcf(direction, accurate: true);
    if (!double.IsFinite(radius) || radius <= 0) throw new InvalidOperationException("Surface height is unavailable on this body.");
    return direction * radius;
}
```

2. **Define east** as `UnitZ x radial` with a `UnitY` fallback at the poles, so that headings are measured from local east:

```csharp
// sphinx.lib/GroundPlacement.cs L80-L83
private static double3 East(double3 radial)
{
    var east = double3.Cross(double3.UnitZ, radial).NormalizeOrZero();
    return east.LengthSquared() > .5 ? east : double3.Cross(double3.UnitY, radial).NormalizeOrZero();
```

3. **Sample and cross.** One-metre offsets in the tangent plane are normalised back to directions; the two chords are crossed, the result is flipped to point outward and a radial fallback covers a degenerate cross product:

```csharp
// sphinx.lib/GroundPlacement.cs L12-L26
public static GroundAnchor At(Celestial body, double3 nearCcf)
{
    var radial = nearCcf.NormalizeOrZero();
    if (!Finite(radial) || radial.LengthSquared() < .5) throw new InvalidOperationException("No valid surface direction.");
    double3 point = Surface(body, radial);
    var east = East(radial);
    var north = double3.Cross(radial, east).NormalizeOrZero();
    // One metre samples capture local terrain slope using the current accurate terrain chain.
    var a = Surface(body, (point + east).NormalizeOrZero()) - Surface(body, (point - east).NormalizeOrZero());
    var b = Surface(body, (point + north).NormalizeOrZero()) - Surface(body, (point - north).NormalizeOrZero());
    var normal = double3.Cross(a, b).NormalizeOrZero();
    if (double3.Dot(normal, radial) < 0) normal = -normal;
    if (!Finite(normal) || normal.LengthSquared() < .5) normal = radial;
    return new(body, point, normal);
}
```

4. **Use the normal as the frame's up when alignment is requested**; X is local east projected onto the tangent plane and Z is `X x up`:

```csharp
// sphinx.lib/GroundPlacement.cs L70-L78
public static double4x4 FrameCcf(GroundAnchor anchor, bool align)
{
    var up = align ? anchor.NormalCcf : anchor.PositionCcf.NormalizeOrZero();
    var x = East(anchor.PositionCcf.NormalizeOrZero());
    x = (x - up * double3.Dot(x, up)).NormalizeOrZero();
    var z = double3.Cross(x, up).NormalizeOrZero();
    var p = anchor.PositionCcf;
    return new double4x4(x.X,x.Y,x.Z,0, up.X,up.Y,up.Z,0, z.X,z.Y,z.Z,0, p.X,p.Y,p.Z,1);
}
```

## Complete example

| File | Role |
|---|---|
| [sphinx.lib/GroundPlacement.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/GroundPlacement.cs) | Anchor, normal, cursor ray march, `Ccf` and ego frames |
| [sphinx.lib/PlacementMath.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/PlacementMath.cs) | Grounded local transform from import bounds |

## Variations and gotchas

- **Accuracy.** The code calls `accurate: true`; the fast path would drop modifiers and decals.
- **One metre is a choice.** On a staircase scale (under about a third of a metre) the normal jitters; on very rough terrain it reflects local noise rather than the hill. Pick the offset for the model's footprint.
- **Poles.** `East` has a fallback; the radial fallback covers a cross product that collapses.
- **Sea.** The normal under water is the seabed's; clamp separately if the model should float.
- **A normal is not a collision.** To make a placed model collidable see the static-collider topics, not this one.

## See also

- [Overview](overview.md); [approach 1](measure-slope-and-detect-ground-contact-on-terrain-1.md)
- [Sphinx GLB statics placement](../../../../mods/unscience/features/sphinx-glb-statics-placement.md)
- [Place a static object on a planet surface](../../../celestials/terrain/place-static-objects-on-a-planet-surface/overview.md)
