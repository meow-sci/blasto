---
type: How-To
title: Differences-only inputs and headless tests
description: Keep a mod's physics maths free of game types and large coordinates by passing the mover-to-target difference, linking the engine-free folder into a test project that references no KSA assembly, and putting the engine behind narrow seams
category: physics
group: mod-simulation
topic: simulate-bodies-outside-the-vehicle-system
approach: 6
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/swept-hull-contact]
techniques: [headless-tests, custom-physics]
hooks: [starmap StarMapAfterGui, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:after-on-frame]
game_types: [KSA.Vehicle, KSA.Part, KSA.Ray]
tags: [physics, headless-tests, xunit, seam, differences, coordinates, contact-sweep, hull]
---

Outcome: the part of your simulation that decides contact is plain C# over `double3` values, runs in an xunit
project on any machine with no KSA installed, and is fed only small numbers. KSAGolf splits its repository into
`Sim/` (no KSA types, tested) and `Ksa/` (everything that touches the engine), and the swept contact test against a
craft is the cleanest example of the boundary. Prerequisites: StarMap for the host mod; the headless project needs
only `Brutal.Core.Numerics.dll` and `Brutal.Core.Common.dll`.

## Background: how the game does it

KSA positions are huge. A craft's ecliptic position near Earth is about 1.5e11 m, so subtracting two such values
in `double` leaves roughly 3e-5 m of resolution, and any frame-mismatched difference carries the planet's 29.8 km/s
of orbital motion (see [approach 4](simulate-bodies-outside-the-vehicle-system-4.md)). The engine's own ray cast
against part meshes takes a matrix with a caller-chosen origin, so the query can run at metre scale:

```csharp
// current/decomp/KSA/Vehicle.cs L1276-L1281
	public double4x4 GetMatrixAsmb2Ego(double3 bodyPositionEgo)
	{
		doubleQuat asmb2Ego = Asmb2Ego;
		double3 position = bodyPositionEgo - CenterOfMassAsmb.Transform(asmb2Ego);
		return double4x4.CreateFromQuaternion(asmb2Ego) * double4x4.CreateTranslation(position);
	}
```

`Part.RayCastEgo(ref readonly double4x4, Ray, ...)` rejects on the part's bounding sphere, then casts watertight
triangles of each subpart's first `MeshViewModule` mesh.[^ksa-part] Pass the mover-to-vessel difference as the
frame origin and the cast needs no camera and no large coordinates; it works while paused and with no viewport.
`Vehicle.MeanRadius` is the bounding sphere radius, not a hull, so on a rocket a sphere-only test registers contact
metres from the skin.

## Lifecycle and timing

The pattern runs in the mod's per-frame step on the main thread, from `[StarMapAfterGui]` or its fallbacks (see
[approach 3](simulate-bodies-outside-the-vehicle-system-3.md)), where `Part.RayCastEgo` and
`Vehicle.GetMatrixAsmb2Ego` read part transforms and attitude that `ApplyVehicleSolvers` has just finished writing.
Do not call the hull test from a worker or from a Harmony patch inside the vehicle step. The maths in `Sim/` is
thread-agnostic; only the engine seam is main-thread. Timing detail is in the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

KSAGolf's swept-hull test (`ContactSweep.TryStrike`, `HullTest.Shared`) has no caller at the pinned commit: the
shipping ball rolls on the ground and course and never calls it. Treat this article as a reusable recipe for the
boundary, not as shipped behaviour.

## Steps

1. Split the code by what it touches. A test project that references no KSA assembly links the whole `Sim/` folder
   with a wildcard, so the moment a file there reaches for a KSA type the test build fails:

```
// tests/KSAGolf.Tests/KSAGolf.Tests.csproj L19-L30
  <!--
    Everything under src/KSAGolf/Sim is free of KSA types by construction, so the whole
    folder is linked in and exercised headlessly on any platform.

    The wildcard is the point. This project references no KSA assembly, so the moment a file
    under Sim/ reaches for one, *the test build fails* - the boundary enforces itself instead
    of relying on someone remembering a rule. It also means a new simulation file is tested the
    moment it exists, with no build plumbing to forget.
  -->
  <ItemGroup>
    <Compile Include="..\..\src\KSAGolf\Sim\**\*.cs" Link="Linked\%(RecursiveDir)%(Filename)%(Extension)" />
  </ItemGroup>
```

2. Define the maths over differences. The contact rule asks whether something moving runs into a body over one
   step. It takes `separation` (body minus mover at the mover's epoch) and `closingVelocity`, never absolute
   positions, and clamps the closest approach to the step so a step that ends short has not arrived:

```csharp
// src/KSAGolf/Sim/Vec.cs L66-L72
    public static double TimeOfClosestApproach(double3 r, double3 v, double horizon)
    {
        double vv = Len2(v);
        if (vv < 1e-12) return 0.0;
        double t = -Dot(r, v) / vv;
        return Math.Clamp(t, 0.0, horizon);
    }
```

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

3. Put the engine behind a one-method seam whose answer includes "I cannot tell". `Unknown` falls back to the
   broad-phase sphere; it must never be read as a miss, or a kitten (no part geometry) or a craft whose mesh has not
   loaded becomes untouchable:

```csharp
// src/KSAGolf/Sim/IHullTest.cs L5-L48
/// <summary>What a hull test could establish about a moving body's step.</summary>
internal enum HullVerdict
{
    /// <summary>
    /// No geometry to ask. The caller keeps whatever the bounding sphere decided.
    ///
    /// <para>Never treated as a miss. A craft the test cannot resolve — one drawn by the character
    /// renderer rather than by parts, one whose mesh has not loaded, one the engine threw on —
    /// would otherwise become silently untouchable, which is worse than a contact registered wide
    /// and far harder to notice.</para>
    /// </summary>
    Unknown,

    /// <summary>The mover passes the body without touching it.</summary>
    Missed,

    /// <summary>The mover meets the surface, at a known fraction of its step.</summary>
    Struck,
}

/// <summary>
/// Whether a moving body actually meets a craft's surface, as opposed to entering the sphere that
/// contains it.
///
/// <para>A craft's bounding sphere is the half-diagonal of its bounding box — tens of metres for a
/// rocket, and built for orbital clearance margins rather than for its skin. A contact tested
/// against it registers at a distance nobody watching would call a hit.</para>
///
/// <para>The implementation lives in <c>Ksa/</c>, because only the engine knows where a hull is.
/// This seam is deliberately narrow: it is handed two <em>differences</em> and an opaque handle,
/// never an absolute position, so the ecliptic carrier cannot leak through it and there is no
/// frame-bearing subtraction at a call site no test reaches. See docs/FRAMES-AND-EPOCHS.md.</para>
/// </summary>
internal interface IHullTest
{
    /// <param name="body">Opaque handle to the body, never dereferenced here.</param>
    /// <param name="separation">Body centre minus mover position, both at the mover's own epoch.</param>
    /// <param name="travel">The mover's displacement across the step <em>relative to the body</em>,
    /// so the body stands still for the query and a static segment is the right question.</param>
    /// <param name="fraction">Where along <paramref name="travel"/> the surface was met, 0 to 1.</param>
    /// <param name="part">Opaque handle to the piece of the body that was met first, or null when the
    /// answer came from anything coarser than the body's own pieces.</param>
    HullVerdict Judge(object? body, double3 separation, double3 travel, out double fraction, out object? part);
}
```

4. Two-phase strike: sphere broad phase with `contactRadius + bodyRadius`, then the hull seam with the mover's
   travel relative to the body (in a frame riding with the body it stands still for the step, so a static segment is
   the right query against a mesh sampled once):

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

5. Implement the seam in `Ksa/`. Build the Asmb-to-Ego matrix with the separation as origin, cast one ray from the
   origin along the travel direction against every part, keep the nearest hit, and treat an exception as `Unknown`:

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

   `RayCastEgo` is an unbounded ray and reports a negative near distance for an origin already inside the hull, so
   the segment's both ends are imposed in the mod: inside the hull is a hit at fraction 0. `KSA.Ray` normalises only
   in its primary constructor, so with an object initializer pass an already unit direction.
6. Guard the cast with a check that mirrors what the engine tests: `HasPickableMesh` requires one level of
   subparts each carrying a `MeshViewModule`, so a craft with nothing to cast goes to the sphere rather than being
   reported as a miss:

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

7. Write the tests against the seam. They state the physics, not the code:

```csharp
// tests/KSAGolf.Tests/ContactSweepTests.cs L16-L28
    [Fact]
    public void AStepIsNotSteppedOver()
    {
        // Ends 500 m short and 500 m past, and dead centre in between.
        double3 separation = new(500, 0, 0);
        double3 closing = new(-1000, 0, 0);

        Assert.True(Vec.Len(separation) > 5.0);
        Assert.True(ContactSweep.TryContact(separation, closing, 1.0, 5.0,
                                            out double when, out double miss));
        Assert.Equal(0.5, when, 6);
        Assert.Equal(0.0, miss, 6);
    }
```

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Sim/ContactSweep.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/ContactSweep.cs#L29-L102) | Swept contact, two-phase strike, sphere reach in step fractions |
| [src/KSAGolf/Sim/IHullTest.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/IHullTest.cs) | `HullVerdict` and the seam |
| [src/KSAGolf/Ksa/HullTest.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/HullTest.cs#L31-L105) | Ray cast in a mover-centred frame, sphere fallback |
| [tests/KSAGolf.Tests/KSAGolf.Tests.csproj](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/KSAGolf.Tests.csproj) | The test project that links `Sim/` and references no KSA assembly |
| [tests/KSAGolf.Tests/ContactSweepTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/ContactSweepTests.cs) | Tests for the contact rule |

## Variations and gotchas

- The same boundary is used in KSArmory (`Sim/` with one driver shared by the game and a headless rig, see
  [approach 1](simulate-bodies-outside-the-vehicle-system-1.md)) and KSACars (`ITerrainHeights`).
- Feed epochs consistently: the separation must be body minus mover at the mover's own epoch. Differencing an
  end-of-frame world sample against a mid-frame mover carries a whole frame of planet motion, about 500 m at 60 fps.
- Built against an older game build than the pin; between 2026.9.22.5482 and 2026.10.10.5554 the `Part.cs` diff
  touches only resource-UI code, and `RayCastEgo` and `Vehicle.GetMatrixAsmb2Ego` are unchanged.
- Kittens carry no mesh, so the sphere decides and an unsized body is floored at 1 m.
- Seams widen with the mod: do not let `object?` opaque handles leak game types into `Sim/`.

## See also

- [Overview and comparison](overview.md)
- [Sweep against a bounding sphere, then cast the hull triangles](../../mod-simulation/prevent-tunnelling-of-fast-bodies/prevent-tunnelling-of-fast-bodies-2.md)
- [Per-triangle cast in a mover-centred frame](../../mod-simulation/ray-cast-against-vessel-meshes-and-terrain/ray-cast-against-vessel-meshes-and-terrain-3.md)
- Mod feature: [KSAGolf swept hull contact](../../../../mods/ksagolf/features/swept-hull-contact.md)
- The topic headless-test-harness-for-game-mods (other batch) covers test harnesses in general

[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
