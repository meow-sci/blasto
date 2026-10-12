---
type: Mod Feature
title: Swept contact against a craft's real hull
description: Analytic swept contact for a fast small body, with a bounding sphere as broad phase and a per-triangle ray cast through Part.RayCastEgo as the narrow phase, falling back to the sphere rather than to a miss
mod: ksagolf
category: physics
techniques: [custom-physics, continuous-collision, ray-cast, headless-tests]
hooks: []
lifecycle: []
game_types: [KSA.Vehicle, KSA.Part, KSA.PartTree, KSA.Ray, KSA.MeshViewModule]
tags: [contact, sweep, tunnelling, broad-phase, narrow-phase, raycast, hull, vehicle, part]
---

A golf ball is 43 mm across and a long drive moves it over a metre per frame at 60 fps, so a
distance measured at the two ends of a step says nothing about the middle. KSAGolf decides contact
with a craft in two phases: an analytic closest-approach sweep against the craft's bounding sphere
rejects almost everything, and only then a hull test casts the step as a ray against the craft's
actual mesh triangles, through the same `Part.RayCastEgo` the engine uses to pick the highlighted part under the cursor (`Vehicle.cs` L2922). A hull
test that cannot answer leaves the sphere's verdict standing; it never turns into "no hit".[^golf-claude]

The split also keeps the maths testable: `Sim/ContactSweep.cs` and the `IHullTest` seam contain no
KSA types and run in the headless test project, and only `Ksa/HullTest.cs` touches the engine.

## How it works

| Piece | Behaviour |
|---|---|
| `ContactSweep.TryContact` | Closest approach of `separation + closingVelocity * t` for `t` in `[0, step]` (`Vec.TimeOfClosestApproach` clamps to the step); contact when the miss distance is within the trigger radius[^golf-contactsweep][^golf-vec] |
| `ContactSweep.TryStrike` | Broad phase with `contactRadius + bodyRadius`; then `hull.Judge(body, separation, -closingVelocity * step)`. `Missed` returns false, `Unknown` returns true (the sphere decided), `Struck` sets the time of contact from the hull's fraction and reports the part met |
| `ContactSweep.TryReachSphere` | The same question in fractions of a step against a body held still, for the hull test's own sphere fallback |
| `IHullTest.Judge` | The seam: given two differences (body minus mover at the mover's epoch, and the mover's travel relative to the body) and an opaque body handle, answer `Unknown`, `Missed` or `Struck` with a fraction and an opaque part[^golf-ihulltest] |
| `HullTest.Judge` | For a `Vehicle`: if no part has a mesh, test its sphere (`MeanRadius`, or 1 m when unsized); else build `GetMatrixAsmb2Ego(separation)`, cast a ray from the origin along `travel`, take the nearest part hit and compare it with the step length; any exception is `Unknown`[^golf-hulltest] |

Two details make the narrow phase cheap and exact:

- **No camera.** `Vehicle.GetMatrixAsmb2Ego(double3 bodyPositionEgo)` takes the frame origin as an
  argument. Passing the mover-relative separation makes the mover the origin, so the cast runs in a
  metres-scale frame centred on the mover. It works paused, with no viewport, and never evaluates
  geometry at ecliptic magnitudes.[^ksa-vehicle]
- **Watertight per-triangle test.** `Part.RayCastEgo` rejects on the part's bounding sphere, then
  asks each subpart's `RayCastEgoSubPart`, which casts `Ray.RaycastWatertight` against the first
  `MeshViewModule`'s `MeshView.PositionCompare` vertices.[^ksa-part][^ksa-ray]
  `KsaWorld.HasPickableMesh` mirrors exactly that requirement (one level of subparts, each needing a
  `MeshViewModule`), so a craft with nothing to cast against goes straight to the sphere instead of
  reporting a miss.[^golf-ksaworld]

Why the sphere alone is not enough: `Vehicle.MeanRadius` is `BoundingSphereRadiusBody`, a bounding
measure; on a rocket it stands metres clear of the skin, so a sphere-only contact registers well
off the hull.[^ksa-vehicle]

## Lifecycle and timing

At this commit nothing in the shipping mod calls `ContactSweep.TryStrike` or `HullTest.Shared`:
the repo describes them as the contacts with craft that longer, airborne shots will use, next to
`GroundTest`.[^golf-claude] They are designed for the mod's own per-frame step, which runs on the
main thread from `[StarMapAfterGui]` (or its fallbacks; see
[Dual-hook frame stepping](dual-hook-frame-stepping.md)).

That placement is what makes the hull query safe. `Part.RayCastEgo` reads part transforms and
`Vehicle.GetMatrixAsmb2Ego` reads the vehicle's attitude and centre of mass, all main-thread vehicle
state that `Universe.ApplyVehicleSolvers` has just written in `Program.PrepareFrame`. After
`ExecuteNextVehicleSolvers` the next step runs on worker threads, but it writes its own state and
publishes it only at the next apply, so main-thread reads in the GUI hook see one consistent,
finished step.[^ksa-program] Do not call the hull test from a worker or a Harmony patch inside the
vehicle step.

Epochs matter for the inputs: the separation must be body minus mover at the mover's own epoch.
A world sample is end-of-frame while a sub-stepping mover is mid-frame, and differencing across that
carries a whole frame of the planet's motion, about 500 m at 60 fps.[^golf-contactsweep][^golf-frames]

## Key code

| Path | Role |
|---|---|
| [src/KSAGolf/Sim/ContactSweep.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/ContactSweep.cs#L29-L102) | Swept contact, two-phase strike, sphere reach in step fractions |
| [src/KSAGolf/Sim/IHullTest.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/IHullTest.cs#L5-L48) | `HullVerdict` (`Unknown`, `Missed`, `Struck`) and the narrow seam |
| [src/KSAGolf/Ksa/HullTest.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/HullTest.cs#L31-L105) | Per-part ray cast in a mover-centred frame, sphere fallback, exception as `Unknown` |
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L1371-L1386) | `HasPickableMesh`: whether any subpart carries a `MeshViewModule` |
| [src/KSAGolf/Sim/Vec.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/Vec.cs#L66-L72) | `TimeOfClosestApproach`, clamped to the step |
| [tests/KSAGolf.Tests/ContactSweepTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/ContactSweepTests.cs) | A step is not stepped over; passing wide is a miss; a step ending short has not arrived |

## Snippets

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

## Game APIs touched

| API | Decomp | Purpose |
|---|---|---|
| `Part.RayCastEgo(ref readonly double4x4, Ray, out ...)` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L2534-L2572) | Bounding-sphere reject, then nearest and farthest subpart hits |
| `Part.RayCastEgoSubPart` / `Part.SubParts` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L2574-L2600) | Watertight cast against the first `MeshViewModule` mesh of each subpart |
| `Vehicle.GetMatrixAsmb2Ego(double3 bodyPositionEgo)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1276-L1281) | Assembly-to-frame matrix with a caller-chosen origin; no camera needed |
| `Vehicle.MeanRadius` / `BoundingSphereRadiusBody` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L527-L535) | `MeanRadius => BoundingSphereRadiusBody`, the broad-phase sphere |
| `Vehicle.Parts` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L605-L611) | The craft's `PartTree` |
| `PartTree.Parts` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L136) | Span of the craft's parts |
| `Ray` / `Ray.RaycastWatertight` | [current/decomp/KSA/Ray.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Ray.cs#L7-L11) | The engine's ray struct; its primary constructor normalises the direction |

## Pitfalls and version notes

- **`Unknown` must not be a miss.** A craft drawn by the character renderer (a kitten), one whose
  mesh has not loaded, or one the engine throws on would otherwise be silently untouchable.[^golf-ihulltest]
- **Kittens have no castable geometry.** Their single part declares no mesh, so `HasPickableMesh`
  is false and the sphere decides; `HullTest` floors an unsized body at 1 m.[^golf-hulltest]
- **Inside the hull.** `RayCastEgo` is an unbounded ray and reports a negative near distance for an
  origin already inside; the mod treats that as a hit at fraction 0 and imposes the far end of the
  segment itself.
- **`new Ray { ... }` skips normalisation.** `KSA.Ray` normalises only in its primary constructor;
  `HullTest` uses an object initializer with an already normalised direction. Copying the pattern
  with an unnormalised direction scales every distance.[^ksa-ray]
- **Main thread only.** The query reads applied vehicle state; see Lifecycle.
- **Not yet wired.** The ball in this commit rolls on the ground and course and does not call these
  types; treat this page as a reusable recipe rather than shipped behaviour.
- **Version.** Built against 2026.9.22.5482. Between that build and 2026.10.10.5554 the `Part.cs`
  diff touches only resource-UI code (`Combustor` renamed to `PlumbedCore`, magnetic scoop info);
  `RayCastEgo`, `RayCastEgoSubPart` and `Vehicle.GetMatrixAsmb2Ego` are unchanged.

## See also

- [Golf ball physics in the body-fixed frame](golf-ball-physics.md)
- [Terrain height queries off the engine's height field](terrain-height-queries.md)
- [Cursor picking and the terrain ray](cursor-picking-and-terrain-ray.md)
- [Headless simulation tests behind a Sim/Ksa boundary](headless-sim-tests.md)
- [Dual-hook frame stepping on simulated time](dual-hook-frame-stepping.md)
- [Sweep against a bounding sphere, then cast the hull triangles](../../../howto/physics/mod-simulation/prevent-tunnelling-of-fast-bodies/prevent-tunnelling-of-fast-bodies-2.md) - how-to article demonstrating this feature
- [Round-centred ego origin and sphere broad phase](../../../howto/physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/ray-cast-against-vessel-meshes-and-terrain-1.md) - how-to article demonstrating this feature
- [Per-triangle cast in a mover-centred frame with no camera](../../../howto/physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/ray-cast-against-vessel-meshes-and-terrain-3.md) - how-to article demonstrating this feature
- [Differences-only inputs and headless tests](../../../howto/physics/mod-simulation/simulate-bodies-outside-the-vehicle-system/simulate-bodies-outside-the-vehicle-system-6.md) - how-to article demonstrating this feature
- [Test ball and contact physics with an epoch bookkeeping layer and missed-frame invariance](../../../howto/testing-and-tooling/offline-tests/run-mod-simulation-in-a-headless-rig/run-mod-simulation-in-a-headless-rig-3.md) - how-to article demonstrating this feature

[^golf-contactsweep]: src/KSAGolf/Sim/ContactSweep.cs
[^golf-vec]: src/KSAGolf/Sim/Vec.cs
[^golf-ihulltest]: src/KSAGolf/Sim/IHullTest.cs
[^golf-hulltest]: src/KSAGolf/Ksa/HullTest.cs
[^golf-ksaworld]: src/KSAGolf/Ksa/KsaWorld.cs
[^golf-claude]: CLAUDE.md (design decisions)
[^golf-frames]: docs/FRAMES-AND-EPOCHS.md
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-ray]: KSA.Ray (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
