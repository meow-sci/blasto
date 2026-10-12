---
type: How-To
title: Swept circle against grown boxes, posts and a regulation cup
description: Replace mesh triangles with exact analytic shapes the mod defines, sweep the ball as a circle against rounded-corner boxes and post circles, and shape the felt over a cup so slow putts drop and fast ones roll over
category: physics
group: mod-simulation
topic: prevent-tunnelling-of-fast-bodies
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/course-surface-exact-shapes]
techniques: [analytic-geometry, continuous-collision]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Celestial]
tags: [physics, swept-collision, analytic, golf, rails, cup, headless-tests]
---

Outcome: a ball that banks off rails, bounces around posts and drops into a cup without ever tunnelling, at any
speed, on geometry the mod authored. KSAGolf's minigolf pieces are a set of exact shapes in code (`PieceShape`):
axis-aligned rail boxes, round bumper posts, a felt floor that is flat or a straight ramp, and a regulation cup. The
Blender models are built from the same numbers, so what is drawn and what is hit cannot drift apart. Prerequisites:
StarMap. The one engine call is the height field for the ground beyond the pieces.

## Background: how the game does it

The engine offers nothing here; the ball is a mod-simulated body ([approach 2](../simulate-bodies-outside-the-vehicle-system/simulate-bodies-outside-the-vehicle-system-2.md)).
Exact shapes instead of the meshes' triangles is the design choice worth copying: triangle soups snag a rolling ball
at every seam and shared edge, while analytic shapes meet at exactly equal heights and can be swept. Off the pieces
the ball falls back to the planet through the terrain height field:[^ksa-celestial]

```csharp
// current/decomp/KSA/Celestial.cs L777-L781
	public double GetTerrainHeightFromDirCce(double3 positionDirCce, bool accurate = true)
	{
		double3 positionDirCcf = positionDirCce.Transform(GetCcf2Cce().Inverse());
		return GetTerrainHeightFromDirCcf(positionDirCcf, accurate);
	}
```

## Lifecycle and timing

| What | When | Thread |
|---|---|---|
| Build a `CourseSurface` | `CourseBuilder.Publish`, after every edit or load | main |
| Query it | `GolfBall.Step` inside the mod's once-per-frame step (`StarMapAfterGui`, `OnFrameCelestials` prefix, or `StarMapAfterOnFrame`) | main |

The surface and the `Course` it wraps are immutable and an edit replaces the whole object, so there is nothing to
lock. The only engine call on the hot path is the beyond ground's height query, safe on the main thread after
`ApplyVehicleSolvers`; see the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Describe each piece in its own unturned metres: rails as boxes, posts as circles, a cup profile. Rails stand
   0.05 m, taller than the 21 mm ball radius, so a rolling ball meets their sides, never their top edge. The felt
   height over the cup follows the rim, so a slow putt drops and a fast one rolls over:

```csharp
// src/KSAGolf/Sim/PieceShape.cs L81-L94
    public double FloorAt(double2 local, double ballRadius)
    {
        if (Cup is not { } cup) return 0.0;

        double d = double2.Distance(local, cup), inside = CupRadiusM - d;
        if (inside <= 0.0) return 0.0;

        // The centre of a ball resting on the rim's edge, less the radius, is the surface the ball sees.
        return inside < ballRadius ? Math.Sqrt((ballRadius * ballRadius) - (inside * inside)) - ballRadius : -CupDepthM;
    }

    /// <summary>Whether the ball has come to rest in the cup: its centre inside the hole and below the rim.</summary>
    public bool Holds(double2 local, double heightAboveFelt, double ballRadius)
        => Cup is { } cup && double2.Distance(local, cup) < CupRadiusM - ballRadius && heightAboveFelt < 0.0;
```

2. Turn a direction into a ground radius by intersecting the level-0 felt plane, looking up the felt height under
   that point, and intersecting again with it. A point off every piece falls through to the ground beyond (terrain, sea, launch site):

```csharp
// src/KSAGolf/Sim/CourseSurface.cs L91-L113
    public bool TrySurfaceRadius(double3 dirCcf, out double radius)
    {
        double along = Vec.Dot(dirCcf, _up);
        if (along > 0.5)
        {
            // Where the direction crosses the level-0 felt, then the felt's height there, found again once.
            double r = Vec.Dot(_origin, _up) / along;
            for (int k = 0; k < 2; k++)
            {
                double3 local = ToLocal(dirCcf * r);
                if (!TryFeltAt(new double2(local.X, local.Y), out double h)) break;

                r = (Vec.Dot(_origin, _up) + h) / along;
                if (k == 1)
                {
                    radius = r;
                    return true;
                }
            }
        }

        return _beyond.TrySurfaceRadius(dirCcf, out radius);
    }
```

3. Sweep the ball as a circle in the course plane. Each rail near the path whose top is above the ball's bottom is
   swept as a box grown by the radius with rounded corners; each post as a circle of `postRadius + radius`; the
   earliest hit wins and its normal comes back in Ccf. Course-plane geometry is fine: over a few metres the planet's
   curvature is hundredths of a millimetre:

```csharp
// src/KSAGolf/Sim/CourseSurface.cs L146-L180
    public bool Sweep(double3 fromCcf, double3 toCcf, double radius, out double fraction, out double3 normalCcf)
    {
        fraction = 1.0;
        normalCcf = default;
        double3 a3 = ToLocal(fromCcf), b3 = ToLocal(toCcf);
        var a = new double2(a3.X, a3.Y);
        var d = new double2(b3.X - a3.X, b3.Y - a3.Y);
        double reach = double2.Distance(a, a + d) + radius + Broad;

        bool hit = false;
        double2 normal = default;
        foreach ((Placed piece, _, RailBox[] rails, (double2 Centre, double Radius)[] posts) in _pieces)
        {
            foreach (RailBox rail in rails)
            {
                if (!Near(rail, a, reach) || !Below(rail, piece, a3, radius)) continue;
                if (Swept.Box(a, d, rail, radius, out double t, out double2 n) && t < fraction)
                {
                    (fraction, normal, hit) = (t, n, true);
                }
            }

            foreach ((double2 centre, double postRadius) in posts)
            {
                if (double2.Distance(centre, a) > reach + postRadius) continue;
                if (Swept.Circle(a, d, centre, postRadius + radius, out double t, out double2 n) && t < fraction)
                {
                    (fraction, normal, hit) = (t, n, true);
                }
            }
        }

        if (hit) normalCcf = Vec.Unit(DirectionToCcf(new double3(normal.X, normal.Y, 0.0)));
        return hit;
    }
```

4. Implement the two primitives. The box sweep tests the four grown faces and the four rounded corners; a circle
   already overlapping and moving further in is a hit at t = 0, pushed out along the nearest face, so a ball that
   starts a step touching is not let through:

```csharp
// src/KSAGolf/Sim/CourseSurface.cs L202-L237
    public static bool Box(double2 a, double2 d, RailBox box, double radius, out double t, out double2 normal)
    {
        t = double.MaxValue;
        normal = default;

        // Already touching: whichever face it is least far through.
        double2 nearest = new(Math.Clamp(a.X, box.X0, box.X1), Math.Clamp(a.Y, box.Y0, box.Y1));
        double2 away = a - nearest;
        double gap = double2.Distance(a, nearest);
        if (gap < radius)
        {
            normal = gap > 1e-12 ? away / gap : Inside(a, box);
            t = 0.0;
            return double2.Dot(d, normal) < 0.0;
        }

        double best = double.MaxValue;
        double2 bestNormal = default;

        // The four faces, pushed out by the radius, each only where the path crosses it within the box's span.
        if (d.X > 0.0) Face((box.X0 - radius - a.X) / d.X, a.Y, d.Y, box.Y0, box.Y1, new double2(-1, 0), ref best, ref bestNormal);
        if (d.X < 0.0) Face((box.X1 + radius - a.X) / d.X, a.Y, d.Y, box.Y0, box.Y1, new double2(1, 0), ref best, ref bestNormal);
        if (d.Y > 0.0) Face((box.Y0 - radius - a.Y) / d.Y, a.X, d.X, box.X0, box.X1, new double2(0, -1), ref best, ref bestNormal);
        if (d.Y < 0.0) Face((box.Y1 + radius - a.Y) / d.Y, a.X, d.X, box.X0, box.X1, new double2(0, 1), ref best, ref bestNormal);

        // The rounded corners.
        foreach (double2 corner in (ReadOnlySpan<double2>)[new(box.X0, box.Y0), new(box.X1, box.Y0), new(box.X0, box.Y1), new(box.X1, box.Y1)])
        {
            if (Circle(a, d, corner, radius, out double tc, out double2 nc) && tc < best) (best, bestNormal) = (tc, nc);
        }

        if (best > 1.0) return false;

        (t, normal) = (best, bestNormal);
        return true;
    }
```

```csharp
// src/KSAGolf/Sim/CourseSurface.cs L255-L280
    public static bool Circle(double2 a, double2 d, double2 centre, double radius, out double t, out double2 normal)
    {
        t = 0.0;
        normal = default;
        double2 m = a - centre;
        double c = double2.Dot(m, m) - (radius * radius);
        if (c < 0.0)
        {
            // Inside already: a hit only if moving further in.
            double len = Math.Sqrt(double2.Dot(m, m));
            normal = len > 1e-12 ? m / len : new double2(1, 0);
            return double2.Dot(d, normal) < 0.0;
        }

        double aa = double2.Dot(d, d);
        if (aa < 1e-18) return false;

        double b = double2.Dot(m, d), disc = (b * b) - (aa * c);
        if (b >= 0.0 || disc < 0.0) return false;

        t = (-b - Math.Sqrt(disc)) / aa;
        if (t > 1.0) return false;

        normal = (m + (d * t)) / radius;
        return true;
    }
```

5. Let the integrator resolve contacts repeatedly. `Advance` sweeps each sub-step, moves to the contact, reflects the
   normal component with restitution 0.7, keeps 95 percent of the tangential speed, and repeats up to four times so a
   corner can take two or three bounces in one sub-step:

```csharp
// src/KSAGolf/Sim/GolfBall.cs L197-L224
    private void Advance(double h, double3 normal)
    {
        // Swept, so no speed carries the ball through a rail; a corner can take a second or third bounce.
        double left = 1.0;
        for (int bounce = 0; bounce < 4 && left > 1e-9; bounce++)
        {
            double3 to = PositionCcf + (VelocityCcf * (h * left));
            if (_walls is null || !_walls.Sweep(PositionCcf, to, RadiusM, out double fraction, out double3 wall))
            {
                PositionCcf = to;
                break;
            }

            PositionCcf += (to - PositionCcf) * fraction;
            left *= 1.0 - fraction;

            double into = Vec.Dot(VelocityCcf, wall);
            if (into < 0.0)
            {
                double3 along = VelocityCcf - (wall * into);
                VelocityCcf = (along * WallGrip) - (wall * (into * WallRestitution));
                if (-into > AudibleImpact)
                {
                    Impacts++;
                    LastImpactSpeed = -into;
                }
            }
        }
```

6. Test the property, not the implementation: bank angle, no tunnelling at any speed, seamless pieces, a slow putt
   holes and a fast one runs over, a turned hole plays the same.

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Sim/PieceShape.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/PieceShape.cs) | Per-piece rails, posts and cup; felt profile; holed test |
| [src/KSAGolf/Sim/CourseSurface.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/CourseSurface.cs#L22-L281) | Ground, normal, cup, swept rails and posts; `Swept` primitives |
| [src/KSAGolf/Sim/GolfBall.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/GolfBall.cs#L189-L233) | `Advance`: up to four swept bounces per sub-step |
| [src/KSAGolf/Ksa/CourseBuilder.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseBuilder.cs#L391-L404) | `Publish`: lays the surface on the body |
| [tests/KSAGolf.Tests/CourseSurfaceTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/CourseSurfaceTests.cs) | The headless tests |

## Variations and gotchas

- The shapes are exact only if the models match them; the repo builds its Blender pieces from `PieceShape` and ships
  them as one glTF file per piece, in metres with the footprint's south-west corner at the origin.
- A rail whose top is under the ball's bottom is ignored, so a bouncing ball can clear rails.
- Kittens do not use this surface: they stand on separate Bepu slabs (see
  [add static colliders to physics bubbles](../../colliders/add-static-colliders-to-physics-bubbles/overview.md)).
- The behaviour is covered by the headless suite rather than confirmed in flight in the repo's own testing note.
- No engine API beyond the height query is involved, so game updates rarely affect it.

## See also

- [Overview and comparison](overview.md)
- [Analytic closest approach](prevent-tunnelling-of-fast-bodies-1.md), [hull casts](prevent-tunnelling-of-fast-bodies-2.md)
- Mod feature: [KSAGolf course surface exact shapes](../../../../mods/ksagolf/features/course-surface-exact-shapes.md)
- [Simulate bodies outside the vehicle system](../simulate-bodies-outside-the-vehicle-system/overview.md)

[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
