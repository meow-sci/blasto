---
type: How-To
title: "Place pieces with a ghost: gizmo rings anchored with GetPositionEgo"
description: Draw hover, held and landing markers as gizmo rings and lines anchored to where the engine renders a craft, submitted from StarMapAfterGui
category: ui
group: input
topic: place-pieces-on-a-grid-with-a-ghost
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksacars/features/craft-mover-mouse-pick-place]
techniques: [starmap-lifecycle, imgui-drawlist]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:render]
game_types: [KSA.GizmosRenderer, KSA.Camera, KSA.Program, KSA.Vehicle, KSA.Celestial]
tags: [gizmos, ghost, marker, ring, placement, anchor, render-frame]
---
Where a placement tool has no mesh to show, the cheapest ghost is a set of gizmo shapes: a ring around the hovered craft, a ring
where the held craft would land, a stalk and a line between them. KSACars' craft mover draws all of it with the engine's
`GizmosRenderer`. The part worth copying is how the shapes are anchored: to where the game renders the craft this frame, not to a
position computed a frame earlier or from the analytic orbit. This is the simpler counterpart of
[the grid builder's gizmo and mesh drawing](place-pieces-on-a-grid-with-a-ghost-1.md). Demonstrated by KSACars.

## Background: how the game does it

- `GizmosRenderer.DrawSphere(positionEgo, radius, color)` and `DrawLine(startEgo, endEgo, color)` take **camera-relative** (ego) positions.
  [current/decomp/KSA/GizmosRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GizmosRenderer.cs#L165-L181)[^ksa-gizmosrenderer][current/decomp/KSA/GizmosRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GizmosRenderer.cs#L217-L234)[^ksa-gizmosrenderer] It draws solid spheres and lines only, no filled polygons, so a ring is a
  ring of overlapping spheres.
- `Program.OnFrame` calls `GizmosRenderer.ResetInstances()` before `PrepareImGui` and the UI pass, and `RenderGame` renders the instances for the
  main view; so gizmos submitted from `StarMapAfterGui` are drawn this frame, while ones submitted from `StarMapAfterOnFrame` (after
  the render) are cleared before they are ever drawn.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2260)[^ksa-program][current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4842)[^ksa-program]
- `Camera.GetPositionEgo(IPosition)` is how KSA places a vehicle: exactly `-PositionCce` for the followed object, `KinematicStates.PositionPhys` for others in
  the same physics bubble, and `GetPositionEcl() - PositionEcl` otherwise. `Vehicle.GetPositionEcl()` is the analytic on-rails position and can differ
  from the physics-drawn one by enough to be visible on a landed craft.[current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L236-L250)[^ksa-camera]
- `Program.GetRenderCamera()` is `RenderedViewport.GetCamera()`; `RenderedViewport` falls back to the main viewport.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L631-L644)[^ksa-program]

## Lifecycle and timing

`KSACarsMod.OnAfterGui` (`[StarMapAfterGui]`, `frame:draw-ui`) draws the panel first, then `_mover.Update()` and `_mover.Draw()`; the gizmos land in
`GizmosRenderer` this frame and are rendered in `frame:render`. `OnFrameViewports` has already built this frame's camera matrices, so the anchor and
the render agree. Ecliptic positions near Earth move about 500 m per frame, so every Ecl value used in a draw must be sampled at the same instant.
See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Define the two-instant anchor.** `Ego` is where the platform is drawn this frame; `Ecl` is the platform's ecliptic position at the instant the
   geometry was measured. The mapping is `Ego + (posEcl - Ecl)`; both fields are sampled at different times on purpose, and collapsing them to one instant puts the
   overlay hundreds of metres from the craft. The type has no KSA references, so it is unit tested headlessly:

```csharp
// src/KSACars/Sim/DrawAnchor.cs L29-L40
internal readonly struct DrawAnchor(double3 ego, double3 ecl)
{
    /// <summary>The platform's position in the render frame, sampled this frame.</summary>
    public double3 Ego { get; } = ego;

    /// <summary>The platform's ecliptic position at the instant the geometry was measured.</summary>
    public double3 Ecl { get; } = ecl;

    public bool IsValid => Vec.IsFinite(Ego) && Vec.IsFinite(Ecl);

    /// <summary>Maps an ecliptic position from the geometry's epoch into the render frame.</summary>
    public double3 ToEgo(double3 posEcl) => Ego + (posEcl - Ecl);
```

2. **Begin a draw pass with an anchor.** Anchor to `camera.GetPositionEgo(craft)`, not `EclToEgo(craft.GetPositionEcl())`; pass in the Ecl position captured with the
   rest of the geometry, not one re-read now:

```csharp
// src/KSACars/Ksa/KsaWorld.Pointing.cs L315-L348
public static bool BeginDraw(Vehicle anchor, double3 anchorEcl)
{
    _anchored = false;
    if (!IsAlive(anchor)) return false;

    try
    {
        // The camera the frame will actually be rendered with, not the main viewport's.
        Camera camera = Program.GetRenderCamera() ?? Program.GetMainCamera();
        if (camera is null) return false;

        // See DrawAnchor for why these are sampled at different instants, and why
        // collapsing them into one puts the whole overlay beside the craft.
        //
        // GetPositionEgo, not EclToEgo. Its branching on what the camera follows is the
        // engine answering correctly per case — exact for the followed craft, physics-based
        // for others in its bubble — and it is the same call KSA renders vehicles with.
        // EclToEgo instead measures against camera.PositionEcl, which only agrees with the
        // rendered scene when the followed craft's analytic and physics positions coincide.
        // That holds for a landed launcher and fails once the camera follows something in
        // flight, which is when anything drawn to a vehicle stops lining up.
        _anchor = new DrawAnchor(camera.GetPositionEgo(anchor), anchorEcl);

        if (!_anchor.IsValid) return false;


        _anchored = true;
        return true;
    }
    catch
    {
        return false;
    }
}
```

3. **Convert and submit.** Every shape goes through `TryEclToEgo`, and a ring is a circle of `DrawSphere` beads (spaced closer than they are wide) each snapped
   to the ground under it so the ring follows a slope:

```csharp
// src/KSACars/Ksa/KsaWorld.Pointing.cs L350-L361
/// <summary>Converts an Ecl position into the anchored Ego frame.</summary>
public static bool TryEclToEgo(double3 posEcl, out double3 posEgo)
{
    if (_anchored)
    {
        posEgo = _anchor.ToEgo(posEcl);
        return true;
    }

    posEgo = Vec.Zero;
    return false;
}
```
```csharp
// src/KSACars/Ksa/KsaWorld.Pointing.cs L452-L492
public static void DrawTorusEcl(double3 centreEcl, double3 normalEcl, double ringRadius,
                                double tubeRadius, float4 colour, bool drape = true)
{
    if (Program.GizmosRenderer is null) return;
    if (!Vec.IsFinite(centreEcl) || !(ringRadius > 0.0) || !(tubeRadius > 0.0)) return;

    double3 up = Vec.Unit(normalEcl);
    if (Vec.Len2(up) < 0.5) return;

    double3 seed = Math.Abs(up.X) < 0.9 ? new double3(1, 0, 0) : new double3(0, 1, 0);
    double3 a = Vec.Unit(Vec.Cross(up, seed));
    double3 b = Vec.Unit(Vec.Cross(up, a));

    // Spaced closer together than they are wide, or it beads. Bounded so a large ring cannot
    // ask for thousands of spheres.
    int steps = (int)Math.Clamp(Math.Ceiling(Math.Tau * ringRadius / tubeRadius), 16, 160);
    Celestial? body = drape ? NearestCelestial(centreEcl) : null;

    for (int i = 0; i < steps; i++)
    {
        double angle = Math.Tau * i / steps;
        double3 at = centreEcl + ((a * Math.Cos(angle)) + (b * Math.Sin(angle))) * ringRadius;

        // Each bead sits on the ground under it, so the ring follows a slope instead of
        // burying one side and floating the other.
        if (body is not null && TrySnapToGround(body, at, out double3 ground, out _)) at = ground;

        if (TryEclToEgo(at, out double3 ego))
        {
            Program.GizmosRenderer.DrawSphere(ego, (float)tubeRadius, colour);
        }
    }
}

public static void DrawLineEcl(double3 startEcl, double3 endEcl, float4 colour)
{
    if (Program.GizmosRenderer is null) return;
    if (!TryEclToEgo(startEcl, out double3 a)) return;
    if (!TryEclToEgo(endEcl, out double3 b)) return;
    Program.GizmosRenderer.DrawLine(a, b, colour);
}
```

4. **Compose the ghost.** Hover: a footprint ring at the craft's base. Held: a footprint ring on the held craft, a ring and a stalk at the target and a line between:

```csharp
// src/KSACars/Ksa/CraftMover.cs L216-L264
/// <summary>Shows what the next click would pick up, or what is held and where it would go.</summary>
public void Draw()
{
    if (!Enabled) return;

    // Nothing held: ring whatever the pointer is over, so the click is aimed rather than
    // taken on trust.
    if (_held is null)
    {
        if (_hovered is not { } candidate || !KsaWorld.IsAlive(candidate)) return;

        double3 atEcl = KsaWorld.PositionEcl(candidate);
        if (!KsaWorld.BeginDraw(candidate, atEcl)) return;

        DrawFootprint(candidate, atEcl, HoverColour);
        return;
    }

    if (!KsaWorld.IsAlive(_held)) return;

    double3 heldEcl = KsaWorld.PositionEcl(_held);
    if (!KsaWorld.BeginDraw(_held, heldEcl)) return;

    // A ring around its feet, so it is obvious which one the next click moves without the
    // marker sitting over the craft it is marking.
    DrawFootprint(_held, heldEcl, HeldColour);

    if (!TryTarget(out double3 groundEcl, out _, out _, out _)) return;

    // Where it would land: a ring on the ground, a short stalk so the spot reads against
    // sloping terrain, and the line from the craft to it.
    double3 up = Up(_held, groundEcl);

    KsaWorld.DrawTorusEcl(groundEcl, up, MarkerRadius, MarkerRadius * 0.14, TargetColour);
    KsaWorld.DrawLineEcl(groundEcl, groundEcl + up * (MarkerRadius * 0.8), TargetColour);
    KsaWorld.DrawLineEcl(heldEcl, groundEcl, TargetColour);
}

// A ring at the craft's feet rather than a sphere around it. A sphere big enough to read as
// "this one" is by construction big enough to hide what it is pointing at.
private static void DrawFootprint(Vehicle craft, double3 atEcl, float4 colour)
{
    double radius = Math.Max(KsaWorld.MeanRadius(craft) * 1.4, 5.0);
    double3 up = Up(craft, atEcl);

    // Dropped to the craft's base, so it lies on the ground rather than cutting through the
    // middle of the hull.
    KsaWorld.DrawTorusEcl(atEcl - up * (radius / 1.4), up, radius, radius * 0.10, colour);
}
```

## Complete example

| File | Role |
|---|---|
| [src/KSACars/Ksa/CraftMover.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/CraftMover.cs#L216-L264) | `Draw`, `DrawFootprint` |
| [src/KSACars/Ksa/KsaWorld.Pointing.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.Pointing.cs#L315-L361) | `BeginDraw`, `TryEclToEgo` |
| [src/KSACars/Ksa/KsaWorld.Pointing.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.Pointing.cs#L452-L492) | `DrawTorusEcl`, `DrawLineEcl` |
| [src/KSACars/Sim/DrawAnchor.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/DrawAnchor.cs) | Two-instant mapping |
| [tests/KSACars.Tests/DrawAnchorTests.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tests/KSACars.Tests/DrawAnchorTests.cs) | The invariant, headless |

## Variations and gotchas

- **Submit gizmos from the GUI hook.** From the frame postfix they are cleared before being drawn.
- **Anchor with `GetPositionEgo`, difference same-instant Ecl positions.** Using `EclToEgo` agrees with the render only when the followed craft's analytic and
  physics positions coincide, which fails once the camera follows something in flight.
- **Main view only.** The gizmo renderer draws in the main view's pass; do not expect these shapes in a secondary viewport.
- **Spheres only.** No filled shapes; a "ghost" is outline-ish by construction. For a real mesh ghost see approach 1.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of place-pieces-on-a-grid-with-a-ghost](overview.md)
- [Approach 1: ray-plane cell pick, snapping ghost, top-down camera](place-pieces-on-a-grid-with-a-ghost-1.md)
- [Pick objects under the cursor, approach 1](../pick-objects-under-the-cursor/pick-objects-under-the-cursor-1.md)
- [Project world and sky positions to screen pixels and point at off-screen targets](../../../rendering/overlays/project-world-positions-to-screen/overview.md)
- Mod feature: [Craft mover mouse pick and place](../../../../mods/ksacars/features/craft-mover-mouse-pick-place.md)

[^ksa-gizmosrenderer]: KSA.GizmosRenderer (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
