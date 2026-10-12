---
type: Mod Feature
title: Course surface from exact shapes
description: The minigolf course as the ball meets it, built from exact rail boxes, posts, felt heights and cup profiles shared with the Blender models, with rails swept rather than sampled and the planet's ground beyond the pieces
mod: ksagolf
category: physics
techniques: [custom-physics, continuous-collision, analytic-geometry, headless-tests]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Celestial]
tags: [minigolf, collision, swept-circle, rails, cup, ramp, felt, seams, exact-geometry]
---

A minigolf hole is a set of modelled pieces, but the ball never touches a mesh. Each piece kind has
an exact description in code (`PieceShape`): axis-aligned rail boxes, round bumper posts, a felt
floor that is flat or a straight ramp, and a regulation cup. The ball rolls on that felt, banks off
those rails and drops into that cup, and the Blender models are built from the same numbers, so what
is drawn and what is hit cannot drift apart.[^golf-pieceshape]

Exact shapes instead of the meshes' triangles is the design choice worth copying: triangle soups
snag a rolling ball at every seam and every shared edge, while analytic shapes meet at exactly equal
heights and can be swept, so no speed carries the ball through a rail.[^golf-claude][^golf-coursesurface]

## How it works

| Part | What it does |
|---|---|
| `PieceShape` (per piece id) | Rails as boxes in the piece's own unturned metres (south-west corner at the origin, felt at 0); each port is flanked by a rail band from 0.04 to 0.10 m in from the edge, so any two pieces meet rail to rail and the lane is `CourseSize.LaneM` (0.8 m) wide. Rails stand 0.05 m, taller than the 21 mm ball radius, so a rolling ball meets their sides, never their top edge. The bumper room has three 0.09 m posts. The cup is 54 mm in radius and 90 mm deep[^golf-pieceshape][^golf-coursefile] |
| `PieceShape.FloorAt` | The felt height the ball's centre sees: 0 outside the cup; over the rim, `sqrt(r^2 - inside^2) - r` (the ball resting on the rim's edge); once the centre is a radius inside, the cup's depth |
| `CourseLines.FloorAt` | The piece's level (0.30 m per level), or along a ramp a straight rise between its two port edges[^golf-courselines] |
| `Placed.ToCourse` / `ToPiece` | Mirror then quarter-turn a piece-local point onto the course grid and back, so one shape table serves every orientation[^golf-course] |
| `CourseSurface` constructor | Lays every piece's rails and posts into course-local metres once (turns and mirrors keep rail boxes square to the grid)[^golf-coursesurface] |
| `TrySurfaceRadius(dirCcf)` | For a direction within 60 degrees of the course's up: intersect the level-0 felt plane, look up the felt height under that point, intersect again with it; no piece there falls through to the ground beyond (terrain, sea, launch site) |
| `TrySurfaceNormal` | Centred differences of the felt at 2 mm, exact on ramps and in the cup; off the course it defers to the beyond ground |
| `Sweep(from, to, radius)` | In the course plane: each rail near the path whose top is above the ball's bottom is swept as a box grown by the radius with rounded corners (four faces plus four corner circles); each post as a circle of `postRadius + radius`; the earliest hit wins and its normal is returned in `Ccf`. A ball already touching and moving further in is a hit at t = 0 |
| `InCup` | A resting ball whose centre is inside `CupRadius - ballRadius` and below the felt is holed |

The course is treated as flat: it is computed in its own plane, because over a few metres the
planet's curvature is hundredths of a millimetre.[^golf-coursesurface] The ball uses the surface
through two interfaces: `IBodyGround` (`TrySurfaceRadius`, `TrySurfaceNormal`) for the ground, and
`IWalls.Sweep` for rails. `GolfBall.Advance` sweeps each sub-step, moves to the contact, reflects
the normal component with restitution 0.7 and keeps 95% of the tangential speed, and repeats up to
four times so a corner can take two or three bounces in one sub-step.[^golf-golfball]

## Lifecycle and timing

| What | When | Thread |
|---|---|---|
| Build a `CourseSurface` | `CourseBuilder.Publish`, after every edit or load (input read at `[StarMapAfterGui]`, or a panel button) | main |
| Query it | `GolfBall.Step` inside `Golf.StepBall`, from the mod's once-per-frame step (`[StarMapAfterGui]`, the `Program.OnFrameCelestials` prefix on F2 frames, or `[StarMapAfterOnFrame]`) | main |

`Golf.Course` holds `(body, surface)`; the ball uses it only while it lies on that body, otherwise
the plain `BodyGround`.[^golf-golf][^golf-coursebuilder] The surface and the `Course` it wraps are
immutable, and an edit replaces the whole object, so there is nothing to lock. The only engine call
on the hot path is the beyond ground's height query, which is safe on the main thread after
`ApplyVehicleSolvers` (see [Terrain height queries](terrain-height-queries.md)).[^ksa-celestial]

## Key code

| Path | Role |
|---|---|
| [src/KSAGolf/Sim/PieceShape.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/PieceShape.cs) | Per-piece rails, posts and cup; felt profile over the cup; holed test |
| [src/KSAGolf/Sim/CourseSurface.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/CourseSurface.cs#L22-L192) | `CourseSurface`: ground, normal, cup, swept rails and posts |
| [src/KSAGolf/Sim/CourseSurface.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/CourseSurface.cs#L194-L281) | `Swept`: moving circle against a rounded box and a circle |
| [src/KSAGolf/Sim/CourseLines.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/CourseLines.cs#L102-L114) | `FloorAt`: level and ramp rise |
| [src/KSAGolf/Sim/GolfBall.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/GolfBall.cs#L189-L233) | `Advance`: up to four swept bounces per sub-step |
| [src/KSAGolf/Ksa/CourseBuilder.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseBuilder.cs#L391-L404) | `Publish`: lays the surface on the body for the ball |
| [src/KSAGolf/KSAGolfCourse.xml](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/KSAGolfCourse.xml) | The drawn pieces, modelled from the same dimensions |
| [tests/KSAGolf.Tests/CourseSurfaceTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/CourseSurfaceTests.cs) | Bank angle, no tunnelling at any speed, seamless pieces, slow putt holes and fast one runs over, ramp roll-back, turned hole plays the same |

## Snippets

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

## Game APIs touched

| API | Decomp | Purpose |
|---|---|---|
| `Celestial.GetTerrainHeightFromDirCcf` (through `Golf.BodyGround`) | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L789-L797) | The ground beyond the pieces; everything else here is the mod's own geometry |

## Pitfalls and version notes

- **One source of dimensions.** The shapes are only exact if the models match them; the repo builds
  its Blender pieces from `PieceShape` and ships them as one `<GltfFile>` per piece in
  `KSAGolfCourse.xml`, in metres with the footprint's south-west corner at the origin.[^golf-course-xml]
- **Rails taller than the ball radius.** At 0.05 m against a 21 mm radius the contact is always on a
  rail's side; a lower rail would let the sweep's 2D test disagree with what is drawn.[^golf-pieceshape]
- **Height check before the sweep.** `Below` ignores a rail whose top is under the ball's bottom, so a
  ball flying over a rail passes it; a bouncing ball can clear rails.
- **The cup is a profile, not a hole.** The felt dips along a rim curve the ball's centre follows,
  so a slow putt drops and a fast one rolls over the hole; the tests cover both.[^golf-surface-tests]
- **Off the course is the planet.** A ball leaving the pieces falls to `BodyGround`, i.e. terrain
  or a launch site, below the course floor (the floor sits 2 cm above the highest ground under any
  piece corner).
- **Kittens do not use this.** A kitten stands on the separate Bepu slabs from
  [Course colliders as clutter](course-colliders-as-clutter.md); rails are not colliders.
- **Unverified in game.** Like the ball itself, this is covered by the headless suite rather than
  confirmed in flight in the repo's testing note.[^golf-claude]
- **Version.** No engine API beyond the height query; unaffected by the 2026.9.22.5482 to
  2026.10.10.5554 changes.

## See also

- [Golf ball physics in the body-fixed frame](golf-ball-physics.md)
- [Course grid model and JSON save file](course-grid-and-save-file.md)
- [Top-down minigolf course builder](minigolf-course-builder.md)
- [Course colliders registered as ground clutter](course-colliders-as-clutter.md)
- [Prop and course asset XML](prop-and-course-asset-xml.md)
- [GLB authoring pipeline and mesh checks](glb-authoring-and-mesh-checks.md)
- [Swept circle against grown boxes, posts and a regulation cup](../../../howto/physics/mod-simulation/prevent-tunnelling-of-fast-bodies/prevent-tunnelling-of-fast-bodies-3.md) - how-to article demonstrating this feature

[^golf-coursesurface]: src/KSAGolf/Sim/CourseSurface.cs
[^golf-pieceshape]: src/KSAGolf/Sim/PieceShape.cs
[^golf-courselines]: src/KSAGolf/Sim/CourseLines.cs
[^golf-course]: src/KSAGolf/Sim/Course.cs
[^golf-golfball]: src/KSAGolf/Sim/GolfBall.cs
[^golf-coursefile]: src/KSAGolf/Sim/CourseFile.cs
[^golf-coursebuilder]: src/KSAGolf/Ksa/CourseBuilder.cs
[^golf-golf]: src/KSAGolf/Ksa/Golf.cs
[^golf-surface-tests]: tests/KSAGolf.Tests/CourseSurfaceTests.cs
[^golf-course-xml]: src/KSAGolf/KSAGolfCourse.xml
[^golf-claude]: CLAUDE.md (where golf starts)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
