---
type: How-To
title: Sweep against a bounding sphere, then cast the hull triangles
description: Use closest approach over the step against a craft's bounding sphere as the broad phase, then cast the step as a ray through the engine's per-triangle test, with an unknown verdict leaving the sphere standing
category: physics
group: mod-simulation
topic: prevent-tunnelling-of-fast-bodies
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/swept-hull-contact]
techniques: [continuous-collision, ray-cast]
hooks: [starmap StarMapAfterGui, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:after-on-frame]
game_types: [KSA.Vehicle, KSA.Part, KSA.Ray, KSA.PartTree]
tags: [physics, continuous-collision, hull, ray-cast, bounding-sphere, broad-phase, narrow-phase]
---

Outcome: a small fast mover decides contact with a craft against the craft's real geometry, not its bounding
sphere, with no tunnelling at any speed. KSAGolf's ball is 43 mm across and a long drive moves it over a metre per
frame, so a distance measured at the two ends of a step says nothing about the middle. Prerequisites: StarMap. The
test is written and unit tested in KSAGolf but has no caller at the pinned commit: `ContactSweep.TryStrike` and
`HullTest.Shared` are not used by the shipping mod, and the ball rolls on the ground and course only. Use this as a
recipe, not as shipped behaviour. The test boundary is covered by
[approach 6 of the simulation topic](../simulate-bodies-outside-the-vehicle-system/simulate-bodies-outside-the-vehicle-system-6.md).

## Background: how the game does it

Why the sphere alone is not enough: `Vehicle.MeanRadius` is `BoundingSphereRadiusBody`, a bounding measure that
stands metres clear of a rocket's skin, so a sphere-only contact registers well off the hull.[^ksa-vehicle]
Two engine pieces make the narrow phase cheap:

```csharp
// current/decomp/KSA/Vehicle.cs L1276-L1281
	public double4x4 GetMatrixAsmb2Ego(double3 bodyPositionEgo)
	{
		doubleQuat asmb2Ego = Asmb2Ego;
		double3 position = bodyPositionEgo - CenterOfMassAsmb.Transform(asmb2Ego);
		return double4x4.CreateFromQuaternion(asmb2Ego) * double4x4.CreateTranslation(position);
	}
```

`Vehicle.GetMatrixAsmb2Ego(double3 bodyPositionEgo)` takes the frame origin as an argument. Passing the mover-relative separation makes
the mover the origin, so the cast runs in a metres-scale frame with no camera and no viewport, and it works while paused.
`Part.RayCastEgo` rejects on the part's bounding sphere, then asks each subpart's `RayCastEgoSubPart`, which casts a
watertight ray against the first `MeshViewModule` mesh:[^ksa-part]

```csharp
// current/decomp/KSA/Part.cs L2534-L2545
	public bool RayCastEgo(ref readonly double4x4 matrixVehicleAsmb2Ego, Ray ray, out double minDistance, out double maxDistance, out double3 nearIntersectionPositionAsmb, out double3 nearIntersectionNormalAsmb, out double3 farIntersectionPositionAsmb, out double3 farIntersectionNormalAsmb, out Part? closestSubPart, out Part? farthestSubPart)
	{
		minDistance = double.MaxValue;
		maxDistance = double.MinValue;
		nearIntersectionPositionAsmb = double3.Zero;
		nearIntersectionNormalAsmb = double3.Zero;
		farIntersectionPositionAsmb = double3.Zero;
		farIntersectionNormalAsmb = double3.Zero;
		closestSubPart = null;
		farthestSubPart = null;
		double3 double5 = double3.Max(double3.Abs(BoundingBoxPartAsmb.Min), double3.Abs(BoundingBoxPartAsmb.Max));
		BoundingSphere3D inSphere = new BoundingSphere3D(PositionEgo(in matrixVehicleAsmb2Ego), double5.Length() * Double3Ex.GetAbsoluteLargestElement(ScaleTotal));
```

## Lifecycle and timing

Main thread only, from the mod's per-frame step (`StarMapAfterGui` or its fallbacks). `Part.RayCastEgo` reads part
transforms and `GetMatrixAsmb2Ego` reads attitude and centre of mass, state `Universe.ApplyVehicleSolvers` has just
finished writing in `Program.PrepareFrame`. After `ExecuteNextVehicleSolvers` the next step runs on workers but
publishes only at the next apply, so main-thread reads in the GUI hook see one finished step. Do not call it from a
worker or from a Harmony patch inside the vehicle step. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md). The inputs must be a
difference at the mover's own epoch; differencing an end-of-frame world sample against a mid-frame mover carries
about 500 m of planet motion at 60 fps.

## Steps

1. Broad phase: closest approach of `separation + closingVelocity * t` over `[0, step]` (the same primitive as
   [approach 1](prevent-tunnelling-of-fast-bodies-1.md)). Contact when the miss distance is within the trigger:

```csharp
// src/KSAGolf/Sim/ContactSweep.cs L29-L37
    public static bool TryContact(double3 separation, double3 closingVelocity,
                                  double stepSeconds, double trigger,
                                  out double timeOfContact, out double missDistance)
    {
        timeOfContact = Vec.TimeOfClosestApproach(separation, closingVelocity, stepSeconds);
        missDistance = Vec.Len(separation + closingVelocity * timeOfContact);

        return missDistance <= trigger;
    }
```

2. Narrow phase through a seam. `TryStrike` calls the sphere with `contactRadius + bodyRadius` and then
   `hull.Judge(body, separation, -closingVelocity * step, ...)`. `Missed` rejects, `Unknown` keeps the sphere's
   verdict, `Struck` replaces the contact time with `fraction * step`:

```csharp
// src/KSAGolf/Sim/ContactSweep.cs L59-L84
    public static bool TryStrike(double3 separation, double3 closingVelocity, double stepSeconds,
                                 double contactRadius, double bodyRadius,
                                 IHullTest? hull, object? body,
                                 out double timeOfContact, out double missDistance, out object? part)
    {
        part = null;
        if (!TryContact(separation, closingVelocity, stepSeconds, contactRadius + bodyRadius,
                        out timeOfContact, out missDistance))
        {
            return false;
        }

        if (hull is null) return true;

        // Relative travel: in a frame riding with the body it stands still for the step, which is
        // what makes a static segment the right query against a mesh sampled once.
        HullVerdict verdict = hull.Judge(body, separation, -closingVelocity * stepSeconds,
                                         out double fraction, out part);

        if (verdict == HullVerdict.Missed) return false;
        if (verdict == HullVerdict.Unknown) return true;

        timeOfContact = Math.Clamp(fraction, 0.0, 1.0) * stepSeconds;
        missDistance = 0.0;
        return true;
    }
```

3. Implement `Judge` for a `Vehicle`. Build the Asmb-to-Ego matrix with the separation as origin, cast one ray from
   the origin along the unit travel direction against every part, keep the nearest, and compare with the step
   length. An origin already inside the hull gives a negative near distance, which is a hit at fraction 0:

```csharp
// src/KSAGolf/Ksa/HullTest.cs L44-L82
            if (craft.Parts is not { } tree) return HullVerdict.Unknown;

            ReadOnlySpan<Part> parts = tree.Parts;

            if (!KsaWorld.HasPickableMesh(parts))
            {
                return AgainstSphere(craft, separation, travel, out fraction);
            }

            double4x4 asmb2Round = craft.GetMatrixAsmb2Ego(separation);
            Ray ray = new() { Origin = default, Direction = travel / length };

            double nearest = double.MaxValue;
            int met = -1;

            for (int i = 0; i < parts.Length; i++)
            {
                if (!parts[i].RayCastEgo(in asmb2Round, ray, out double near, out double far,
                                         out _, out _, out _, out _, out _, out _))
                {
                    continue;
                }

                // The cast is an unbounded ray and reports a negative near hit for an origin
                // already inside the hull, so both ends of the segment have to be imposed here.
                double hit = near >= 0.0 ? near : far >= 0.0 ? 0.0 : double.MaxValue;

                if (hit < nearest)
                {
                    nearest = hit;
                    met = i;
                }
            }

            if (nearest > length) return HullVerdict.Missed;

            fraction = nearest / length;
            part = parts[met];
            return HullVerdict.Struck;
```

4. Fall back to the sphere in fractions of the step when nothing can be cast: a kitten (drawn by the character
   renderer; its single part declares no mesh) or a craft whose mesh has not loaded. `HasPickableMesh` mirrors what
   `RayCastEgo` tests, one level of subparts each with a `MeshViewModule`; an unsized body is floored at 1 m:

```csharp
// src/KSAGolf/Sim/ContactSweep.cs L95-L102
    public static bool TryReachSphere(double3 separation, double3 travel, double radius,
                                      out double fraction)
    {
        // A step is one unit long, so the horizon is 1 and the answer is already a fraction.
        fraction = Vec.TimeOfClosestApproach(separation, -travel, 1.0);

        return Vec.Len(separation - travel * fraction) <= radius;
    }
```

```csharp
// src/KSAGolf/Ksa/HullTest.cs L96-L105
    private static HullVerdict AgainstSphere(Vehicle craft, double3 separation, double3 travel,
                                             out double fraction)
    {
        double radius = craft.MeanRadius;
        if (!double.IsFinite(radius) || radius <= 0.0) radius = UnsizedBodyRadius;

        return ContactSweep.TryReachSphere(separation, travel, radius, out fraction)
                   ? HullVerdict.Struck
                   : HullVerdict.Missed;
    }
```

5. Turn every exception into `Unknown`: answering `Missed` for a craft the engine will not answer for would make it
   quietly untouchable, which is worse than a contact registered early.

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Sim/ContactSweep.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/ContactSweep.cs#L29-L102) | `TryContact`, `TryStrike`, `TryReachSphere` |
| [src/KSAGolf/Sim/IHullTest.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/IHullTest.cs#L5-L48) | `HullVerdict` and the seam |
| [src/KSAGolf/Ksa/HullTest.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/HullTest.cs#L31-L105) | Per-part ray cast in a mover-centred frame |
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L1371-L1386) | `HasPickableMesh` |
| [tests/KSAGolf.Tests/ContactSweepTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/ContactSweepTests.cs) | A step is not stepped over; wide passes miss; short steps have not arrived |

## Variations and gotchas

- No caller: `TryStrike` and `HullTest.Shared` are not referenced by the shipping mod at the pinned commit
  f3e6ab72; the repo describes them as the contact with craft for longer, airborne shots.
- `KSA.Ray` normalises only in its primary constructor; the hull test uses an object initializer with an already unit
  direction. Copying the pattern with an unnormalised direction scales every distance.
- Kittens have no castable geometry, so the sphere decides.
- KSArmory uses the same two-phase shape for shells, with the sphere test as broad phase; see approach 1.
- Built against 2026.9.22.5482; `RayCastEgo`, `RayCastEgoSubPart` and `GetMatrixAsmb2Ego` are unchanged to 2026.10.10.5554.

## See also

- [Overview and comparison](overview.md)
- [Analytic closest approach](prevent-tunnelling-of-fast-bodies-1.md), [swept shapes](prevent-tunnelling-of-fast-bodies-3.md)
- Mod feature: [KSAGolf swept hull contact](../../../../mods/ksagolf/features/swept-hull-contact.md)
- [Ray cast against part meshes and terrain](../ray-cast-against-vessel-meshes-and-terrain/overview.md)

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
