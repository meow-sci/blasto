---
type: How-To
title: Walk the ray over the height field in bounded steps
description: Find where the mouse ray meets the ground by converting the cursor to camera framebuffer pixels, building the ray in Ego and stepping along it by its height above the terrain, then refining the crossing
category: physics
group: mod-simulation
topic: ray-cast-against-vessel-meshes-and-terrain
approach: 4
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksacars/features/craft-mover-mouse-pick-place]
techniques: [ray-cast, imgui-input]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Camera, KSA.Celestial, KSA.Vehicle, KSA.Program]
tags: [physics, ray-cast, picking, cursor, terrain, height-field, framebuffer, ego]
---

Outcome: the point on the ground (on any body in view) under the mouse, accurate to 0.5 m, found without
intersecting a sphere and looking up a height. KSACars' craft mover uses it to put a car down where the player
clicks. Prerequisites: StarMap. This is the terrain half of cursor picking; the part-mesh half is
[approach 2](ray-cast-against-vessel-meshes-and-terrain-2.md).

## Background: how the game does it

KSA has no engine ray-versus-terrain query in this build. The ground is the height field along a direction:
`Celestial.GetTerrainHeightFromDirCce(dir, accurate)` returns the height above `MeanRadius`.[^ksa-celestial]

```csharp
// current/decomp/KSA/Celestial.cs L777-L781
	public double GetTerrainHeightFromDirCce(double3 positionDirCce, bool accurate = true)
	{
		double3 positionDirCcf = positionDirCce.Transform(GetCcf2Cce().Inverse());
		return GetTerrainHeightFromDirCcf(positionDirCcf, accurate);
	}
```

The mouse side has one trap. `Camera.ScreenToEgoRay(float2)` takes pixels of the camera's own framebuffer, and a
render scale or display scale makes that a different size from the ImGui viewport, so convert first:

```csharp
// current/decomp/KSA/Camera.cs L704-L713
	public Ray ScreenToEgoRay(float2 inScreenPosition)
	{
		double3 nearPlanePos = ScreenToEgoNearPlane(inScreenPosition);
		if (_orthographic)
		{
			return ScreenToEgoRayOrthographic(new double2(inScreenPosition.X, inScreenPosition.Y), nearPlanePos);
		}
		double3 inDirection = double3.Normalize(new double3(nearPlanePos.X, nearPlanePos.Y, nearPlanePos.Z));
		return new Ray(new double3(0.0, 0.0, 0.0), inDirection);
	}
```

The resulting ray is in Ego (camera-relative), which is a pure translation of the ecliptic frame, so a direction is
the same in both and only the origin needs `camera.EgoToEcl(0)`.

## Lifecycle and timing

Runs in the `StarMapAfterGui` hook on `frame:draw-ui`. ImGui's frame is open, so `ImGui.GetMousePos` and
`GetIO().WantCaptureMouse` are current; `OnFrameViewports` has already built this frame's camera matrices, so
projections match what is about to render. The panel draws first so a click on the panel is the panel's. All reads are
of settled main-thread state; the placement itself is deferred (`Vehicle.TeleportToLocation` only queues an input
event applied in the next `PrepareFrame`). See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Build the cursor ray in Ecl from whichever visible game viewport contains the cursor, converting window pixels
   to camera framebuffer pixels first:

```csharp
// src/KSACars/Ksa/KsaWorld.Pointing.cs L67-L98
    public static bool TryCursorRayEcl(out double3 originEcl, out double3 directionEcl)
    {
        originEcl = default;
        directionEcl = default;
        try
        {
            float2 cursor = ImGui.GetMousePos();

            ReadOnlySpan<IGameViewport> viewports = GameViewports;
            for (int i = 0; i < viewports.Length; i++)
            {
                IGameViewport v = viewports[i];
                if (!v.Visible) continue;

                if (v.GetCamera() is not { } camera) continue;

                // Framebuffer pixels, not viewport pixels: ScreenToEgoRay divides by the camera's
                // own framebuffer, and a render or display scale makes those different sizes.
                if (!CursorAim.TryToFramebuffer(cursor, v.Position, v.Width, v.Height,
                                                camera.FramebufferSize.X, camera.FramebufferSize.Y,
                                                out float2 local))
                {
                    continue;
                }

                double3 direction = camera.ScreenToEgoRay(local).Direction;
                if (!CursorAim.IsUsableDirection(direction)) continue;

                originEcl = camera.EgoToEcl(Vec.Zero);
                directionEcl = Vec.Unit(direction);
                return Vec.IsFinite(originEcl);
            }
```

2. For every `Celestial`, walk the ray from the eye. Restrict the walk to the stretch below the body's highest
   terrain (`TerrainMask.TryBandBelow`), step by the ray's height above the ground (assuming slopes no steeper than
   1:1) clamped to 1 m..1000 m and at least half a percent of the range walked, at most 512 samples:

```csharp
// src/KSACars/Sim/TerrainRay.cs L40-L93
    public static bool TryFirstHit(double3 origin, double3 direction, double maxRange, double3 centre,
                                   double meanRadius, double maxTerrainHeight, ITerrainHeights heights,
                                   out double range)
    {
        range = double.NaN;

        if (heights is null || !Vec.IsFinite(origin) || !Vec.IsFinite(direction) || !Vec.IsFinite(centre))
        {
            return false;
        }

        if (!double.IsFinite(maxRange) || maxRange <= 0.0 || !double.IsFinite(meanRadius) || meanRadius <= 0.0)
        {
            return false;
        }

        double3 dir = Vec.Unit(direction);
        if (!Vec.IsFinite(dir) || Vec.Len2(dir) < 0.5) return false;

        double top = double.IsFinite(maxTerrainHeight) ? Math.Max(0.0, maxTerrainHeight) : 0.0;
        if (!TerrainMask.TryBandBelow(origin, origin + (dir * maxRange), centre, meanRadius + top,
                                      out double from, out double to))
        {
            return false;
        }

        double t = from * maxRange;
        double end = to * maxRange;
        double clearAt = double.NaN;

        for (int i = 0; i < MaxSamples && t <= end; i++)
        {
            double floor = Math.Max(MinStepMetres, t * MinStepFraction);

            if (!TryClearance(origin, dir, t, centre, meanRadius, heights, out double clearance))
            {
                t += floor;
                continue;
            }

            if (clearance <= 0.0)
            {
                if (double.IsNaN(clearAt)) return false;

                range = Refine(origin, dir, clearAt, t, centre, meanRadius, heights);
                return true;
            }

            clearAt = t;
            t += Math.Clamp(Math.Max(clearance / MaxSlope, floor), MinStepMetres, MaxStepMetres);
        }

        return false;
    }
```

3. On the first step that ends under the ground, halve the bracket until it is within 0.5 m:

```csharp
// src/KSACars/Sim/TerrainRay.cs L97-L115
    private static double Refine(double3 origin, double3 dir, double above, double below, double3 centre,
                                 double meanRadius, ITerrainHeights heights)
    {
        for (int i = 0; i < 40 && below - above > ToleranceMetres; i++)
        {
            double mid = 0.5 * (above + below);

            if (TryClearance(origin, dir, mid, centre, meanRadius, heights, out double clearance) && clearance > 0.0)
            {
                above = mid;
            }
            else
            {
                below = mid;
            }
        }

        return 0.5 * (above + below);
    }
```

4. Take the nearest body hit, so pointing at a moon on the horizon means the moon. Convert the hit to latitude and
   longitude (`GetLatitudeFromCce`, `GetLongitudeFromCce`) and place with `Vehicle.TeleportToLocation`, a buffered
   call that must be used once per action; see
   [place a vessel on a surface](../../kinematics/place-a-vessel-on-a-surface-at-latitude-and-longitude/overview.md).

## Complete example

| File | Role |
|---|---|
| [src/KSACars/Ksa/CraftMover.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/CraftMover.cs) | Hover, pick up, put down, draw |
| [src/KSACars/Ksa/KsaWorld.Pointing.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.Pointing.cs) | Cursor ray, ground point, projection, gizmo anchor |
| [src/KSACars/Sim/TerrainRay.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/TerrainRay.cs) | The height-field walk |
| [src/KSACars/Sim/TerrainMask.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/TerrainMask.cs) | The band of the ray below the highest terrain |
| [src/KSACars/Sim/CursorAim.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/CursorAim.cs) | Window to framebuffer pixel conversion |

## Variations and gotchas

- Intersecting the mean sphere and then looking up the height lands behind a hill seen side-on; walking the field does not.
- The walk ignores launch pads on purpose: the pad as a thicker planet swung the resolved point by kilometres at range.
- If the cursor is over a craft, the craft's own footing is used: a ray through a vehicle meets the ground behind it.
- The height field answers the seabed; over the sea the hit is on the sea floor unless you clamp to the ocean level.
- Submit gizmos from the GUI hook, not the frame postfix: gizmos submitted after the render are cleared before they
  are drawn.
- Anchor overlays with `Camera.GetPositionEgo`, and difference Ecl positions sampled at the same instant.

## See also

- [Overview and comparison](overview.md)
- [Cursor ray against every vehicle](ray-cast-against-vessel-meshes-and-terrain-2.md), [layered cast](ray-cast-against-vessel-meshes-and-terrain-5.md)
- Mod feature: [KSACars craft mover](../../../../mods/ksacars/features/craft-mover-mouse-pick-place.md)
- The topic query-terrain-height-and-line-of-sight (other batch) covers height queries

[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
