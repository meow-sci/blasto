---
type: How-To
title: Round-centred ego origin and sphere broad phase
description: Cast a path segment against a craft's part meshes with no camera by passing the mover-to-craft separation as the ego origin, after a bounding-sphere broad phase
category: physics
group: mod-simulation
topic: ray-cast-against-vessel-meshes-and-terrain
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/hull-triangle-hit-test, mods/ksagolf/features/swept-hull-contact]
techniques: [engine-raycast, continuous-collision]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Part, KSA.Vehicle, KSA.Ray, KSA.MeshReference]
tags: [physics, ray-cast, hull, broad-phase, narrow-phase, no-camera, differences]
---

Outcome: you can ask whether a segment of travel meets any part mesh of any craft, in metres, paused or not, with no
camera or viewport, and learn where along the segment it does. KSArmory's `HullTest` uses this for shells (a craft's
bounding sphere stands metres clear of a rocket's skin, so a fuse judged on it fires on shells that visibly missed);
KSAGolf contains the same call, described in [approach 3](ray-cast-against-vessel-meshes-and-terrain-3.md), but
nothing calls it at the pinned commit; KSArmory is the shipping caller.
Prerequisites: StarMap.

## Background: how the game does it

The trick is the frame. `Vehicle.GetMatrixAsmb2Ego(double3 bodyPositionEgo)` takes the position of the craft in
"ego" space as an argument rather than reading a camera:

```csharp
// current/decomp/KSA/Vehicle.cs L1276-L1281
	public double4x4 GetMatrixAsmb2Ego(double3 bodyPositionEgo)
	{
		doubleQuat asmb2Ego = Asmb2Ego;
		double3 position = bodyPositionEgo - CenterOfMassAsmb.Transform(asmb2Ego);
		return double4x4.CreateFromQuaternion(asmb2Ego) * double4x4.CreateTranslation(position);
	}
```

Pass the **round-relative separation** as that position and the ray starts at the origin: every number involved is
metres, not ecliptic positions of 1.5e11 m. `Part.RayCastEgo` rejects on a part bounding sphere, then calls
`RayCastEgoSubPart` per subpart, which tests the mesh bounding sphere and then runs `Ray.RaycastWatertight` over
every triangle of the subpart's first `MeshViewModule` mesh. The mesh is `PositionCompare`, a de-indexed copy of the
first glTF primitive, and there is no acceleration structure, so cost is linear in triangles for every subpart whose
sphere the ray crosses:[^ksa-part]

```csharp
// current/decomp/KSA/Part.cs L2587-L2608
		MeshReference meshView = span[0].MeshView;
		double absoluteLargestElement = Double3Ex.GetAbsoluteLargestElement(ScaleTotal);
		double3 inPosition = PositionEgo(in matrixVehicleAsmb2Ego);
		BoundingSphere3D inSphere = new BoundingSphere3D(inPosition, meshView.BoundingSphereRadius * absoluteLargestElement);
		if (!ray.Raycast(inSphere, out var _, out var _))
		{
			return false;
		}
		double3[] positionCompare = meshView.PositionCompare;
		double4x4 vertexOffset = MatrixAsmb2Ego(in matrixVehicleAsmb2Ego);
		if (!ray.RaycastWatertight(positionCompare, in vertexOffset, out var nearT, out var nearClosestVertexIndex, out var farT, out var farClosestVertexIndex))
		{
			return false;
		}
		minDistance = nearT;
		maxDistance = farT;
		double4x4.Invert(vertexOffset, out var result);
		nearIntersectionPositionAsmb = (ray.Origin + ray.Direction * nearT).Transform(result);
		nearIntersectionNormalAsmb = double3.Unpack(in meshView.HostMesh.GetVertexSpan<float3>(MeshAttribute.Normal)[meshView.HostMesh.IndexBuffer.ToSpan<int>()[nearClosestVertexIndex]]);
		farIntersectionPositionAsmb = (ray.Origin + ray.Direction * farT).Transform(result);
		farIntersectionNormalAsmb = double3.Unpack(in meshView.HostMesh.GetVertexSpan<float3>(MeshAttribute.Normal)[meshView.HostMesh.IndexBuffer.ToSpan<int>()[farClosestVertexIndex]]);
		return true;
```

The same call drives the engine's hover highlight (`Vehicle.UpdateHighlight`), so a mod using it sees the same
geometry the player sees.

## Lifecycle and timing

Called from the round step in the mod's once-per-frame hook, `StarMapAfterGui` or its fallbacks. The part tree, part
transforms and `Vehicle.Asmb2Ego` are main-thread state settled by `Universe.ApplyVehicleSolvers` earlier in the
frame, and the worker dispatched afterwards steps its own copy, so a read-only cast is safe while it runs. Because
`GetMatrixAsmb2Ego(double3)` does not read a camera the test works paused, with the HUD hidden and with no
viewport. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Define a narrow seam that takes differences, not positions: `Judge(body, separation, travel, out fraction)`, with
   `separation` the body centre minus the mover at the mover's epoch and `travel` the mover's displacement over the
   step relative to the body. The ecliptic carrier of about 29.8 km/s cannot leak into a geometry query. Return
   `Unknown`, `Missed` or `Struck`:

```csharp
// src/KSArmory/Sim/ContactSweep.cs L52-L94
    public static bool TryStrike(double3 separation, double3 closingVelocity, double stepSeconds,
                                 double fuseRadius, double bodyRadius,
                                 IHullTest? hull, object? body,
                                 out double timeOfContact, out double missDistance)
    {
        if (!TryContact(separation, closingVelocity, stepSeconds, fuseRadius + bodyRadius,
                        out timeOfContact, out missDistance))
        {
            return false;
        }

        if (hull is null) return true;

        // Relative travel: in a frame riding with the body it stands still for the step, which is
        // what makes a static segment the right query against a mesh sampled once.
        HullVerdict verdict = hull.Judge(body, separation, -closingVelocity * stepSeconds,
                                         out double fraction);

        if (verdict == HullVerdict.Missed) return false;
        if (verdict == HullVerdict.Unknown) return true;

        timeOfContact = Math.Clamp(fraction, 0.0, 1.0) * stepSeconds;
        missDistance = 0.0;
        return true;
    }

    /// <summary>
    /// Whether a round's step reaches a sphere, and where along the step it does.
    ///
    /// <para>What a hull test falls back on for a body with no geometry to cast against. Separate
    /// from <see cref="TryContact"/> because it answers in <em>fractions of the step</em> against a
    /// body held still for it, which is the frame a hull test works in.</para>
    /// </summary>
    /// <param name="separation">Body centre minus round position, at the round's own epoch.</param>
    /// <param name="travel">The round's displacement across the step, relative to the body.</param>
    public static bool TryReachSphere(double3 separation, double3 travel, double radius,
                                      out double fraction)
    {
        // A step is one unit long, so the horizon is 1 and the answer is already a fraction.
        fraction = Vec.TimeOfClosestApproach(separation, -travel, 1.0);

        return Vec.Len(separation - travel * fraction) <= radius;
    }
```

2. Implement it for a `Vehicle`. Fall back to a sphere when nothing can be cast (a kitten, or a craft whose mesh has
   not loaded), build the matrix at the separation, cast from the origin along the unit travel for every part, and
   impose the segment by hand because the cast is an unbounded ray:

```csharp
// src/KSArmory/Ksa/HullTest.cs L41-L76
        try
        {
            if (craft.Parts is not { } tree) return HullVerdict.Unknown;

            ReadOnlySpan<Part> parts = tree.Parts;

            if (!KsaWorld.HasPickableMesh(parts))
            {
                return AgainstSphere(craft, separation, travel, out fraction);
            }

            double4x4 asmb2Round = craft.GetMatrixAsmb2Ego(separation);
            Ray ray = new() { Origin = default, Direction = travel / length };

            double nearest = double.MaxValue;

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

                if (hit < nearest) nearest = hit;
            }

            if (nearest > length) return HullVerdict.Missed;

            fraction = nearest / length;
            return HullVerdict.Struck;
        }
```

   `near >= 0` is a hit at `near`; `near < 0 <= far` means the mover starts inside the hull, a hit at 0; anything
   beyond the segment length is `Missed`.
3. Guard it with a check that mirrors what the engine cast can see (one level of subparts, each with a
   `MeshViewModule`), so a craft with nothing to cast is not reported as a miss:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L4430-L4446
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

4. Ask only after the broad phase passes. KSArmory's `HullStrikeTests.TheSphereRejectsBeforeTheHullIsAsked` pins the
   order, because a burst of 150 shells asking about every craft 600 times a frame is not affordable.
5. Treat every exception as `Unknown`. A craft the engine will not answer for must not become silently bulletproof.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/HullTest.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/HullTest.cs) | The engine-backed test |
| [src/KSArmory/Sim/IHullTest.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/IHullTest.cs) | KSA-free seam and the three-valued verdict |
| [src/KSArmory/Sim/ContactSweep.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ContactSweep.cs#L52-L94) | Sphere broad phase, then hull; sphere fallback in step fractions |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L4430-L4446) | `HasPickableMesh` |
| [tests/KSArmory.Tests/HullStrikeTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/HullStrikeTests.cs) | Beside-the-hull, on-the-hull, ordering and invariance tests |

## Variations and gotchas

- Only primitive 0 of a multi-primitive mesh is cast, by the engine and by every mod that uses `RayCastEgo`.
- The cast treats the body as still and the mover as moving at the relative velocity; the craft's own rotation during
  the sub-step is ignored.
- A negative near distance for an origin inside the hull is the engine's behaviour, not a bug.
- KSArmory reuses the cast for an optical head's line of sight (`KsaWorld.TryRayCraftHit`), for placing a bullet hole
  on the subpart a shell struck, and for mount self-collision sweeps (see [approach 6](ray-cast-against-vessel-meshes-and-terrain-6.md)).
- In KSArmory, guided `Interceptor` rounds are proximity-fused and never use the hull test.

## See also

- [Overview and comparison](overview.md)
- [Approach 3: the same call in KSAGolf](ray-cast-against-vessel-meshes-and-terrain-3.md)
- [Sweep against a bounding sphere, then cast the hull triangles](../prevent-tunnelling-of-fast-bodies/prevent-tunnelling-of-fast-bodies-2.md)
- Mod feature: [KSArmory hull triangle hit test](../../../../mods/ksarmory/features/hull-triangle-hit-test.md)

[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
