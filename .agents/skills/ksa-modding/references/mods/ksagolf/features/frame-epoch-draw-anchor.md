---
type: Mod Feature
title: Frame and epoch draw anchor
description: Drawing world-space gizmos next to a craft without the 500 m-per-frame ecliptic error, by anchoring every overlay to the craft's rendered Ego position sampled this frame and adding Ecl offsets measured against the platform sample from the simulation step
mod: ksagolf
category: rendering
techniques: [gizmo-drawing, reference-frames]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Camera, KSA.GizmosRenderer, KSA.Program, KSA.Vehicle, KSA.Universe]
tags: [reference-frames, ecliptic, ego, gizmos, overlays, epoch, camera]
---

Every KSA position near Earth carries about 29.8 km/s of ecliptic motion, roughly 500 m per frame
at 60 fps. KSAGolf computes in the ecliptic frame (`Ecl`) and converts to camera-relative `Ego` only
at draw time, and it treats any mismatch of sampling instants as a bug with a measurable size
(1 ms of epoch error is 30 m). For gizmo overlays it uses a two-instant `DrawAnchor`: the craft's
`Ego` position as the renderer will draw it this frame, paired with the craft's `Ecl` position from
the same instant the overlay geometry was measured against. Every overlay point is then
`anchor.Ego + (pointEcl - anchor.Ecl)`.[^golf-drawanchor]

In the current code the anchor is used by the craft mover's rings, lines and landing marker. The
ball avoids the problem differently: it lives in the body-fixed frame of the planet under it and is
drawn against the body's pose at draw time (see [Golf ball physics](golf-ball-physics.md) and
[Mod mesh draw hook](mod-mesh-draw-hook.md)).[^golf-claude-md]

## How it works

1. `KsaWorld.BeginFrame()` runs once per frame in `StepOnce` and invalidates the previous anchor,
   so a caller that forgets `BeginDraw` draws nothing (visible) rather than something in the wrong
   place (invisible).[^golf-ksaworld]
2. `KsaWorld.BeginDraw(vehicle, vehicleEcl)` takes the render camera
   (`Program.GetRenderCamera() ?? Program.GetMainCamera()`) and stores
   `new DrawAnchor(camera.GetPositionEgo(vehicle), vehicleEcl)`.
   `Camera.GetPositionEgo` returns `-PositionCce` for the followed object, the physics position
   (`KinematicStates.PositionPhys` through the bubble origin) for another vehicle in the same physics
   bubble, and `GetPositionEcl() - PositionEcl` otherwise, which is exactly how KSA places vehicle
   meshes; `Camera.EclToEgo` would use only the analytic position and miss a landed craft by
   metres.[^ksa-camera]
3. `DrawLineEcl`, `DrawTorusEcl` convert through `TryEclToEgo` and submit to
   `Program.GizmosRenderer.DrawLine` / `DrawSphere` (a torus is a ring of overlapping spheres,
   optionally draped onto the ground, because the gizmo renderer draws only spheres and lines).[^ksa-gizmosrenderer]

Rules from the mod's frame contract that this enforces:[^golf-frames-epochs]

- A drawn offset is `PositionEcl - anchorEcl` measured after the step against the platform sample
  from the same frame; never re-project the platform by a `dt`.
- `Ego` belongs to one camera; a position taken into Ego through one camera and back through
  another is out by the distance between them.
- Measure errors as vectors and divide by 29.8 km/s: the quotient in milliseconds names the cause.

## Lifecycle and timing

| Moment | What happens |
|---|---|
| `frame:apply-solvers` (engine, no mod code) | `Universe.ApplyVehicleSolvers` sets the last sim step and calls `CurrentSystem.UpdatePerFrameData()`, writing every celestial and vehicle position for the new epoch[^ksa-universe] |
| `frame:draw-ui` (`StarMapAfterGui`) | `CraftMover.Draw` samples `PositionEcl`, calls `BeginDraw`, and submits gizmos[^golf-mod][^golf-craftmover] |
| Window | `Program.OnFrame` calls `GizmosRenderer.ResetInstances()` before `PrepareImGui`; gizmos submitted after that and before `Render` are drawn this frame, anything submitted from `StarMapAfterOnFrame` is cleared before it is ever drawn[^ksa-program] |

So gizmo overlays must be submitted from the GUI hooks; with the HUD hidden they are simply not
drawn, which for a development tool is acceptable.

## Key code

| Path | Role |
|---|---|
| [src/KSAGolf/Sim/DrawAnchor.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/DrawAnchor.cs) | The two-instant mapping, KSA-free |
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L1110-L1157) | `BeginDraw`, `TryEclToEgo` |
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L2016-L2056) | `DrawTorusEcl`, `DrawLineEcl` |
| [tests/KSAGolf.Tests/DrawAnchorTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/DrawAnchorTests.cs) | Fails if the two instants are collapsed |

## Snippets

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

## Game APIs touched

| API | Decomp | Purpose |
|---|---|---|
| `Camera.GetPositionEgo(IPosition)`, `Camera.PositionEcl` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs) | Where the renderer puts a vehicle this frame |
| `Program.GetRenderCamera`, `GetMainCamera`, `GizmosRenderer` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs) | Camera and gizmo renderer |
| `GizmosRenderer.DrawLine`, `DrawSphere`, `ResetInstances` | [current/decomp/KSA/GizmosRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GizmosRenderer.cs) | Ego-space line and sphere instances |
| `Universe.ApplyVehicleSolvers` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs) | When positions advance to the new epoch |

## Pitfalls and version notes

- Collapsing the two instants (deriving `Ego` from `Ecl`, or re-reading the platform at draw time)
  puts the whole overlay a frame of ecliptic motion beside the craft; `DrawAnchorTests` asserts the
  collapsed form measurably fails.[^golf-drawanchor-tests]
- `Vehicle.GetPositionEcl` is the analytic (orbit) position; a landed craft's physics position
  differs by metres, so anything drawn against a vehicle should be built from `GetPositionEgo`.[^golf-ksaworld]
- Gizmos submitted from `StarMapAfterOnFrame` are cleared by `ResetInstances` before the next
  render; only the GUI hooks sit inside the draw window.[^ksa-program]
- The mod's docs cite source line numbers (`Universe.cs:1681` etc.) from build 2026.9.22.5482; the
  behaviour was re-checked on 2026.10.10.5554 but the line numbers differ.[^golf-frame-order]

## See also

- [Craft mover](craft-mover.md)
- [Dual-hook frame stepping on simulated time](dual-hook-frame-stepping.md)
- [Drawing a mod's static mesh into every viewport](mod-mesh-draw-hook.md)
- [Golf ball physics in the body-fixed frame](golf-ball-physics.md)
- [Headless simulation tests behind a Sim/Ksa boundary](headless-sim-tests.md)
- [Pair the rendered Ego position with ECL offsets from the same instant](../../../howto/flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/understand-ksa-coordinate-frames-and-epochs-5.md) - how-to article demonstrating this feature
- [Two-instant draw anchor against GetPositionEgo](../../../howto/rendering/overlays/choose-a-render-frame-and-anchor-effects/choose-a-render-frame-and-anchor-effects-1.md) - how-to article demonstrating this feature
- [Gizmos next to a moving craft using a frame-epoch anchor](../../../howto/rendering/overlays/draw-world-space-lines-and-gizmos/draw-world-space-lines-and-gizmos-2.md) - how-to article demonstrating this feature

[^golf-drawanchor]: src/KSAGolf/Sim/DrawAnchor.cs
[^golf-drawanchor-tests]: tests/KSAGolf.Tests/DrawAnchorTests.cs
[^golf-ksaworld]: src/KSAGolf/Ksa/KsaWorld.cs
[^golf-craftmover]: src/KSAGolf/Ksa/CraftMover.cs
[^golf-mod]: src/KSAGolf/Ksa/KSAGolfMod.cs
[^golf-frames-epochs]: docs/FRAMES-AND-EPOCHS.md
[^golf-frame-order]: docs/KSA-FRAME-ORDER.md
[^golf-claude-md]: CLAUDE.md
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-gizmosrenderer]: KSA.GizmosRenderer (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
