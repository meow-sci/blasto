---
type: Mod Feature
title: Per-triangle hull hit test with the engine's part ray cast
description: A shell's step is tested against the target craft's real part meshes with KSA's own watertight Part.RayCastEgo, cast in a metres-scale frame centred on the round, after a bounding-sphere broad phase
mod: ksarmory
category: physics
techniques: [custom-physics, engine-raycast, sim-boundary]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Part, KSA.Ray, KSA.Vehicle, KSA.PartTree, KSA.MeshReference, KSA.MeshViewModule]
tags: [hit-test, ray-cast, mesh, collision, projectiles, broad-phase, narrow-phase]
---

KSArmory's unguided rounds have to touch what they hit. A craft's bounding sphere is built for
orbital clearance margins and stands metres clear of a rocket's skin, so a contact fuse judged on
it fires on shells that visibly missed. Instead, once a shell's sub-step passes the sphere test,
`HullTest` asks the engine's own per-triangle part ray cast whether the step's segment meets any
part mesh, and where along the step it does.[^ksarmory-hulltest][^ksarmory-ihulltest]

The trick worth copying is the frame: the cast is set up with the craft's assembly matrix placed
at the **round-relative separation**, so the ray starts at the origin and every number involved is
metres, not ecliptic positions of 1.5e11 m, and no camera or viewport is needed.

## How it works

**The seam** (`Sim/IHullTest.cs`) is deliberately narrow: `Judge(body, separation, travel, out fraction)`
takes an opaque handle and two **differences** (body centre minus round, both at the round's epoch;
the round's displacement over the step relative to the body). It returns `Unknown`, `Missed` or
`Struck` with a fraction of the step. Positions never cross it, so the ~29.8 km/s ecliptic carrier
cannot leak into a geometry query.[^ksarmory-ihulltest]

**The implementation** (`Ksa/HullTest.cs`, one stateless shared instance):

1. Not a `Vehicle`, non-finite inputs, or zero travel: `Unknown` (the sphere's verdict stands).
2. `craft.Parts.Parts`; if no subpart carries a `MeshViewModule` (`KsaWorld.HasPickableMesh`, which
   mirrors what the engine cast can see), fall back to a sphere of `craft.MeanRadius` (or 1 m for an
   unsized craft such as a kitten) via `ContactSweep.TryReachSphere`.
3. `double4x4 asmb2Round = craft.GetMatrixAsmb2Ego(separation)`: the engine's assembly-to-ego
   matrix, but with the round as the "ego" origin.
4. A `Ray` from the origin along `travel / |travel|`.
5. For each part, `Part.RayCastEgo(in asmb2Round, ray, out near, out far, ...)`. The cast is an
   unbounded ray, so the segment is imposed by hand: `near >= 0` is a hit at `near`; `near < 0 <= far`
   means the round starts inside the hull (hit at 0); otherwise no hit.
6. Nearest hit beyond `|travel|` is `Missed`; otherwise `Struck` with `fraction = nearest / |travel|`.
7. Any exception is `Unknown`, never `Missed`: a craft the engine will not answer for must not
   become silently bulletproof.[^ksarmory-hulltest]

**Where it is used.** `WeaponSystem.UpdateRounds` assigns `HullTest.Shared` to every `Slug` each
frame; `Slug.Step` calls `ContactSweep.TryStrike` for the designated target and every contact; the
hull is only asked after the analytic closest-approach sphere test passes, and a `Struck` verdict
replaces the contact time with `fraction * h` and the miss distance with 0. Guided `Interceptor`s
are proximity-fused and never use it.[^ksarmory-weaponsystem][^ksarmory-contactsweep][^ksarmory-slug]

**What the engine does inside `Part.RayCastEgo`** (Part.cs L2534-L2572):

| Stage | Code | Cost |
|---|---|---|
| Part bounding sphere | `ray.Raycast(BoundingSphere3D(...))` with a radius of the bounding box's length times the scale | One sphere test per part |
| Each subpart | `RayCastEgoSubPart` (L2574): first `MeshViewModule`, mesh bounding sphere | One sphere test per subpart |
| Triangles | `ray.RaycastWatertight(meshView.PositionCompare, in vertexOffset, ...)` (Ray.cs L207) | Loops every triangle (`i += 3`), transforming three vertices by the matrix each time |
| Result | near/far `t`, intersection points and normals in assembly space, closest subpart | |

`PositionCompare` is a de-indexed `double3[]` copy of the mesh's first glTF primitive built at
load (MeshReference.cs L93-L103). There is no BVH: cost is linear in triangle count for every
subpart whose bounding sphere the ray crosses.[^ksa-part][^ksa-ray][^ksa-meshreference]

**Same call the engine picks with.** `Vehicle.UpdateHighlight` casts the cursor ray through
`Part.RayCastEgo` against every part to drive `PartPicker` (Vehicle.cs L2917-L2925), and the
vehicle editor uses it for placement (VehicleEditor.cs L2558 onward). KSArmory reuses it for the
optical head's line of sight (`KsaWorld.TryRayCraftHit`), for placing a bullet hole on the sub-part
a shell struck (`KsaWorld.TryHullHit`), and for its mount self-collision sweeps (`TravelSweeps`).[^ksa-vehicle][^ksa-vehicleeditor][^ksarmory-ksaworld][^ksarmory-travelsweeps]

## Lifecycle and timing

- Called from `Slug.Step`, i.e. inside the once-per-frame round step that runs from
  `[StarMapAfterGui]` (postfix on `Program.OnDrawUiViewports`, Program.cs L2268), the
  `Program.OnFrameCelestials` prefix (call at L2320) when the UI is hidden, or `[StarMapAfterOnFrame]`
  as the fallback.[^ksa-program]
- The part tree, part transforms and `Vehicle.Asmb2Ego` (L502, which returns `Body2Cce`) it reads
  are main-thread state settled by `Universe.ApplyVehicleSolvers` (Universe.cs L1922-L1951) earlier
  in the same frame; the vehicle worker dispatched at Program.cs L2220 is meanwhile stepping its own
  copy. The cast only reads, which is why it can run while that worker is in
  flight.[^ksa-universe][^ksa-vehicle]
- `GetMatrixAsmb2Ego(double3)` (L1276) takes the frame origin as an argument rather than reading a
  camera, so the test works paused, with the UI hidden, and in frames with no viewport of
  interest.[^ksa-vehicle]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/HullTest.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/HullTest.cs) | The engine-backed test |
| [src/KSArmory/Sim/IHullTest.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/IHullTest.cs) | KSA-free seam and the three-valued verdict |
| [src/KSArmory/Sim/ContactSweep.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ContactSweep.cs#L52-L94) | Sphere broad phase, then hull; sphere fallback in step fractions |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L4430-L4446) | `HasPickableMesh`, mirroring what the cast can see |
| [tests/KSArmory.Tests/HullStrikeTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/HullStrikeTests.cs) | Beside-the-hull, on-the-hull, ordering and invariance tests |

## Snippets

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

```csharp
// current/decomp/KSA/Part.cs L2587-L2600
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
```

## Game APIs touched

| API | Decompiled file | Used for |
|---|---|---|
| `Part.RayCastEgo(ref readonly double4x4, Ray, out ...)` (L2534) | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs) | Per-part, per-subpart, per-triangle cast |
| `Part.RayCastEgoSubPart` (L2574), `Part.PositionEgo` / `MatrixAsmb2Ego` (L1203, L1213) | same | Subpart mesh bounds and vertex transform |
| `Ray` (struct, L7), `Ray.RaycastWatertight(Span<double3>, ref readonly double4x4, ...)` (L207) | [current/decomp/KSA/Ray.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Ray.cs) | The triangle loop |
| `MeshReference.PositionCompare` (L55) | [current/decomp/KSA/MeshReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshReference.cs) | De-indexed positions of primitive 0 |
| `Vehicle.GetMatrixAsmb2Ego(double3)` (L1276), `Vehicle.Asmb2Ego` (L502), `Vehicle.Parts` (L605), `Vehicle.MeanRadius` (L535) | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs) | Round-centred assembly matrix; part list; sphere fallback |
| `PartTree.Parts` (L136) | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs) | Span of the craft's parts |

## Pitfalls and version notes

- **Never treat "cannot tell" as a miss.** Every failure path returns `Unknown`, which leaves the
  sphere's (generous) verdict standing; the opposite choice makes craft quietly invulnerable.[^ksarmory-ihulltest]
- **The ray is unbounded and starts inside sometimes.** Clamp to the segment yourself and treat a
  negative near/positive far pair as "already inside" (hit at 0).
- **Only primitive 0.** `PositionCompare` is `PositionsCompare[0]`, so a mesh split into several glTF
  primitives is only cast against its first one. This is engine behaviour shared with part picking,
  not something the mod adds.[^ksa-meshreference]
- **Cost is linear in triangles.** No acceleration structure; the mod keeps it affordable by asking
  only after the sphere test passes (`HullStrikeTests.TheSphereRejectsBeforeTheHullIsAsked` pins the
  order), because, in the test's words, a round asking about every craft every sub-step "is a burst of
  150 shells doing it 600 times a frame".[^ksarmory-hullstriketests]
- **A kinetic round's proximity fuse becomes contact-only** against meshed craft, because a `Missed`
  verdict vetoes the sphere (see [analytic sub-frame fuses](./analytic-subframe-fuses.md)).
- **Relative travel, static mesh.** The query treats the body as still for the step and the round as
  moving with the relative velocity; the craft's own rotation during the sub-step is ignored.

## See also

- [Analytic sub-frame fuses](./analytic-subframe-fuses.md)
- [Mod-simulated rounds](./mod-simulated-rounds.md)
- [Cursor-ray world click tools](./cursor-ray-world-click-tools.md)
- [Self-collision travel map](./self-collision-travel-map.md)
- [Bullet hole decals](./bullet-hole-decals.md)
- [Blast damage and part health](./blast-damage-and-part-health.md)
- [Round-centred ego origin and sphere broad phase](../../../howto/physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/ray-cast-against-vessel-meshes-and-terrain-1.md) - how-to article demonstrating this feature

[^ksarmory-hulltest]: src/KSArmory/Ksa/HullTest.cs
[^ksarmory-ihulltest]: src/KSArmory/Sim/IHullTest.cs
[^ksarmory-weaponsystem]: src/KSArmory/Ksa/WeaponSystem.cs
[^ksarmory-contactsweep]: src/KSArmory/Sim/ContactSweep.cs
[^ksarmory-slug]: src/KSArmory/Sim/Slug.cs
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-ray]: KSA.Ray (decompiled, build 2026.10.10.5554)
[^ksa-meshreference]: KSA.MeshReference (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-vehicleeditor]: KSA.VehicleEditor (decompiled, build 2026.10.10.5554)
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-travelsweeps]: src/KSArmory/Ksa/TravelSweeps.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksarmory-hullstriketests]: tests/KSArmory.Tests/HullStrikeTests.cs
