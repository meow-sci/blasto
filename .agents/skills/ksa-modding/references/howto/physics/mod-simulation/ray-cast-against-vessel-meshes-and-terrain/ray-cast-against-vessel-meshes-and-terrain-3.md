---
type: How-To
title: Per-triangle cast in a mover-centred frame with no camera
description: The narrow phase for a small mover hitting a craft, using the engine's mesh cast with the mover-to-craft separation as the frame origin and the segment imposed on the unbounded ray
category: physics
group: mod-simulation
topic: ray-cast-against-vessel-meshes-and-terrain
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/swept-hull-contact]
techniques: [ray-cast, continuous-collision]
hooks: [starmap StarMapAfterGui, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:after-on-frame]
game_types: [KSA.Part, KSA.Vehicle, KSA.Ray]
tags: [physics, ray-cast, hull, golf, mover-centred, kitten-fallback]
---

Outcome: the same engine call as [approach 1](ray-cast-against-vessel-meshes-and-terrain-1.md), set up the way KSAGolf
sets it up for a 43 mm ball: separation and travel in, `Unknown`/`Missed`/`Struck` plus a step fraction out.
KSAGolf's `ContactSweep.TryStrike` and `HullTest.Shared` have no caller at the pinned commit (f3e6ab72): the shipping
ball rolls on the ground and course only. Treat this as a tested recipe, not shipped behaviour. The test seam and the
two-phase strike are walked through in
[sweep against a bounding sphere, then cast the hull triangles](../prevent-tunnelling-of-fast-bodies/prevent-tunnelling-of-fast-bodies-2.md);
this article keeps to the cast itself. Prerequisites: StarMap.

## Background: how the game does it

`Part.RayCastEgo(ref readonly double4x4 matrixVehicleAsmb2Ego, Ray ray, out minDistance, out maxDistance, out ...)`
is an unbounded ray test; `Vehicle.GetMatrixAsmb2Ego(double3)` supplies the matrix with the frame origin of your
choice:

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

`KSA.Ray` normalises its direction only in its primary constructor; building one with an object initializer and an
unnormalised direction scales every distance, so KSAGolf passes an already unit direction.[^ksa-ray]

## Lifecycle and timing

Main thread, from the mod's per-frame step: `Part.RayCastEgo` and `GetMatrixAsmb2Ego` read part transforms and
attitude that `Universe.ApplyVehicleSolvers` just wrote, and the next vehicle step runs on workers against its own
snapshot. Do not call it from a worker or from a Harmony patch inside the vehicle step. The separation must be body
minus mover at the mover's own epoch; differencing an end-of-frame sample against a mid-frame mover carries a whole
frame of planet motion, about 500 m at 60 fps. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Reject cheaply: not a `Vehicle`, non-finite inputs or zero travel are `Unknown`, so the sphere's verdict stands.
2. Pick the geometry that exists. If no subpart carries a `MeshViewModule` (kittens are drawn by the character renderer
   and their single part declares no mesh), test a sphere instead, in fractions of the step:

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

3. Otherwise build the matrix with the separation as origin and cast from the origin along the unit travel for every
   part. The cast is unbounded and reports a negative near distance for an origin already inside the hull, so impose
   both ends of the segment: inside is a hit at 0, beyond the segment is a miss:

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

4. Gate step 3 with `HasPickableMesh`, which mirrors what the cast can see:

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L1371-L1386
    // Whether a ray could ever hit this craft. Mirrors what RayCastEgo actually tests -- one level
    // of SubParts, each needing a MeshViewModule -- so the answer cannot disagree with the cast.
    internal static bool HasPickableMesh(ReadOnlySpan<Part> parts)
    {
        for (int i = 0; i < parts.Length; i++)
        {
            ReadOnlySpan<Part> subs = parts[i].SubParts;

            for (int j = 0; j < subs.Length; j++)
            {
                if (subs[j].Modules.HasAny<MeshViewModule>()) return true;
            }
        }

        return false;
    }
```

5. Report `fraction = nearest / length` and the part met (an opaque handle), and treat an exception as `Unknown`.

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Ksa/HullTest.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/HullTest.cs#L31-L105) | Per-part ray cast in a mover-centred frame, sphere fallback |
| [src/KSAGolf/Sim/ContactSweep.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/ContactSweep.cs#L29-L102) | The broad phase and the seam caller |
| [tests/KSAGolf.Tests/ContactSweepTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/ContactSweepTests.cs) | Headless tests of the contact rule |

## Variations and gotchas

- No caller at the pinned commit; do not describe the golf ball as colliding with craft.
- Floor an unsized body at 1 m: core's kitten is a 0.4 m capsule 0.59 m from its origin, and `KsaWorld.MeanRadius`
  floors at 5 m, which as a contact radius nothing can miss.
- Built against 2026.9.22.5482; `RayCastEgo`, `RayCastEgoSubPart` and `GetMatrixAsmb2Ego` are unchanged to
  2026.10.10.5554.

## See also

- [Overview and comparison](overview.md)
- [Approach 1: KSArmory's version](ray-cast-against-vessel-meshes-and-terrain-1.md)
- [Differences-only inputs and headless tests](../simulate-bodies-outside-the-vehicle-system/simulate-bodies-outside-the-vehicle-system-6.md)
- Mod feature: [KSAGolf swept hull contact](../../../../mods/ksagolf/features/swept-hull-contact.md)

[^ksa-ray]: KSA.Ray (decompiled, build 2026.10.10.5554)
