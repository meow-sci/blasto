---
type: How-To
title: Pair the rendered Ego position with ECL offsets from the same instant
description: Draw overlays beside a craft by pairing the position KSA renders it at with the ECL sample the geometry was measured against, which removes the 500 m per frame ecliptic error
category: flight-and-orbits
group: flight-state
topic: understand-ksa-coordinate-frames-and-epochs
approach: 5
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/frame-epoch-draw-anchor]
techniques: [gizmo-drawing, reference-frames, frame-epoch]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:apply-solvers, frame:draw-ui]
game_types: [KSA.Camera, KSA.Vehicle, KSA.Universe, KSA.Program]
tags: [frames, ego, ecl, draw-anchor, gizmos, epoch]
---

Outcome: lines, rings and markers that stay locked to a craft instead of floating up to 500 m away.
KSAGolf's craft mover is the demonstrating mod; [Frame and epoch draw anchor](../../../../mods/ksagolf/features/frame-epoch-draw-anchor.md) KSArmory uses the
same struct (see [approach 2](understand-ksa-coordinate-frames-and-epochs-2.md)). Prerequisite: StarMap for
`StarMapAfterGui`.

## Background: how the game does it

`Camera.GetPositionEgo(IPosition)` is how KSA places a craft in the render frame: `-PositionCce` for the
followed object, the physics-bubble position (`KinematicStates.PositionPhys` through the bubble origin)
for another vehicle in the same bubble, and `GetPositionEcl() - PositionEcl` otherwise.[^ksa-camera] [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L236-L250)
`Camera.EclToEgo` is only the subtraction of `PositionEcl`, so it agrees with the rendered scene only
when the followed craft's analytic and physics positions coincide (true for a landed craft, false in
flight). Near Earth an ECL position also carries 29.8 km/s, so the geometry you measure in ECL must be
compared against an ECL sample taken at the same instant.

## Lifecycle and timing

| Moment | What happens |
|---|---|
| `frame:apply-solvers` (engine, no mod code) | `Universe.ApplyVehicleSolvers` sets the last sim step and rebuilds every ECL position for the new epoch |
| `frame:draw-ui` (`StarMapAfterGui`) | the mod samples the craft's ECL position, calls `BeginDraw`, then submits gizmos |
| window | `Program.OnFrame` resets the gizmo instances before ImGui prepares; gizmos submitted from the GUI hooks are drawn this frame, anything submitted from `StarMapAfterOnFrame` is cleared before it is drawn |

Gizmo overlays must therefore be submitted from the GUI hooks, and are not drawn when the HUD is hidden
(`DrawUI == false`). See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Clear the anchor once per frame** so a caller that forgets `BeginDraw` draws nothing instead of
   something 500 m off (`KsaWorld.BeginFrame`, run in the step).
2. **Take the render camera and pair two samples**: `GetPositionEgo` at draw time, the ECL position at the
   instant the geometry was measured.

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L1110-L1143
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
        // That holds for a landed craft and fails once the camera follows something in
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

3. **Map every overlay point** with `anchor.Ego + (pointEcl - anchor.Ecl)`; the struct is KSA-free so
   tests can pin it:

```csharp
// src/KSAGolf/Sim/DrawAnchor.cs L29-L40
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

4. **Submit to the gizmo renderer** through a helper that converts and returns when the anchor is not
   valid:

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L2050-L2056
public static void DrawLineEcl(double3 startEcl, double3 endEcl, float4 colour)
{
    if (Program.GizmosRenderer is null) return;
    if (!TryEclToEgo(startEcl, out double3 a)) return;
    if (!TryEclToEgo(endEcl, out double3 b)) return;
    Program.GizmosRenderer.DrawLine(a, b, colour);
}
```

5. **Keep measuring offsets against a same-frame platform sample**: a drawn offset is
   `PositionEcl - anchorEcl` measured after the step; never re-project the platform by a `dt`. Use one
   camera for the whole path: a position taken into Ego through one camera and back through another is
   off by the distance between the cameras.

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Sim/DrawAnchor.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/DrawAnchor.cs) | the two-instant mapping, KSA-free |
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L1110-L1157) | `BeginDraw`, `TryEclToEgo` |
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L2016-L2056) | `DrawTorusEcl`, `DrawLineEcl` |
| [tests/KSAGolf.Tests/DrawAnchorTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/DrawAnchorTests.cs) | fails if the two instants are collapsed |

## Variations and gotchas

- **Use `GetPositionEgo`, not `EclToEgo`.** The branches inside the engine call are the engine answering
  correctly per case; the subtraction alone misses a landed craft by metres or a craft in flight by more.
- **Measure error as a vector and divide by 29.8 km/s.** The quotient in milliseconds names the cause
  (1 ms of epoch error is about 30 m).
- A body-fixed object (a golf ball on a planet) can avoid the problem by living in the body's frame and
  being drawn against the body's pose at draw time.
- The gizmo renderer draws only spheres and lines, so a torus is a ring of overlapping spheres.

## See also

- [Overview of frames and epochs](overview.md)
- [Ecl working frame and back-dating](understand-ksa-coordinate-frames-and-epochs-2.md)
- [Frame and epoch draw anchor](../../../../mods/ksagolf/features/frame-epoch-draw-anchor.md)

[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
