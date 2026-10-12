---
type: How-To
title: Frame-budgeted sweeps of the craft's own geometry for actuator travel limits
description: Measure a turret mount's clear traverse arc and elevation band by ray-casting its barrel and line of fire against the rest of the craft at a grid of poses, coarse to fine, under a per-frame ray and millisecond budget
category: physics
group: mod-simulation
topic: ray-cast-against-vessel-meshes-and-terrain
approach: 6
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/self-collision-travel-map]
techniques: [coarse-to-fine-sampling, ray-cast]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Part, KSA.Vehicle, KSA.Ray]
tags: [physics, ray-cast, self-collision, turret, travel-limits, budget, coarse-to-fine]
---

Outcome: a mount bolted onto an arbitrary craft never swings its barrel through the hull or fires through its own
superstructure, with no hand-authored limits per craft. KSArmory ray-casts the barrel and a line of fire at a grid of
bearings and elevations against the other parts of the same craft, and turns the result into a traverse arc and an
elevation band per bearing. Prerequisites: StarMap. The sweep is spread across frames under a hard budget and starts
only after the craft has kept its shape for a second.

## Background: how the game does it

The engine call is `Part.RayCastEgo` with a ray whose origin is not zero: the barrel segment starts at the trunnion
and ends at the muzzle, both converted to the craft's Ego frame centred on its centre of mass:

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

`craft.GetMatrixAsmb2Ego(double3.Zero)` puts the centre of mass at the origin, and `craft.Asmb2Ego` rotates
assembly-frame offsets into it, so no world position or epoch enters the test. The cast is an unbounded ray; the
segment is imposed in the mod. It is the same call KSA uses for hover highlighting.

## Lifecycle and timing

`TravelSweeps.Step` runs from the mod's once-per-frame step (`StarMapAfterGui` on `Program.OnDrawUiViewports`, or the
`OnFrameCelestials` prefix with the HUD hidden). It is not gated on the simulated step, so a sweep progresses while
the game is paused. It reads applied part transforms (`PositionVehicleAsmb`, `Asmb2VehicleAsmb`), `Asmb2Ego` and
`CenterOfMassAsmb`, all from the state applied in `PrepareFrame`; the map is consumed by the turret on the next
`WeaponSystem.Update`. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Select mounts that need it (training launchers with a cannon) and order the focused craft's first. Give each
   mount a state keyed on `(craft, launcher part, part count)` and wait `SettleFrames = 60` unchanged frames; any
   change restarts it and the mount keeps its old map. Spend at most `RaysPerFrame = 50` rays and
   `MsPerFrame = 1.0` ms per frame across all mounts:

```csharp
// src/KSArmory/Ksa/TravelSweeps.cs L50-L69
    public void Step(IEnumerable<WeaponSystems.Entry> all, Vehicle? focused)
    {
        _order.Clear();
        foreach (WeaponSystems.Entry e in all) if (Sweeps(e.Weapon.Profile)) _order.Add(e);
        _order.Sort((a, b) => (b.Craft == focused).CompareTo(a.Craft == focused));

        _present.Clear();
        foreach (WeaponSystems.Entry e in _order) _present.Add(e.Weapon);
        _gone.Clear();
        foreach (WeaponSystem w in _states.Keys) if (!_present.Contains(w)) _gone.Add(w);
        foreach (WeaponSystem w in _gone) _states.Remove(w);

        _frame.Restart();
        int budget = RaysPerFrame;
        foreach (WeaponSystems.Entry e in _order)
        {
            if (budget <= 0 || _frame.Elapsed.TotalMilliseconds >= MsPerFrame) break;
            budget -= Advance(e.Weapon, budget);
        }
    }
```

2. Size the grid: 72 bearing columns (5 degrees) by `ceil((MaxElev - MinElev) / 5) + 1` elevation rows. Test coarse
   first (every second column and row), then only cells whose four coarse neighbours disagree; the rest inherit the
   agreed answer.
3. One pose is two probes: trunnion to mean muzzle (the barrel), then muzzle onward along the bore for
   `max(30 m, 2 * MeanRadius)` (the line of fire). Each probe is one `Blocked` call:

```csharp
// src/KSArmory/Ksa/TravelSweeps.cs L153-L185
    private static bool Blocked(Vehicle craft, Part launcher, double3 startPart, double3 endPart)
    {
        try
        {
            if (craft.Parts is not { } tree) return false;

            double4x4 asmb2Ego = craft.GetMatrixAsmb2Ego(double3.Zero);
            doubleQuat rotate = craft.Asmb2Ego;
            double3 start = rotate * (launcher.PositionVehicleAsmb + launcher.Asmb2VehicleAsmb * startPart - craft.CenterOfMassAsmb);
            double3 end = rotate * (launcher.PositionVehicleAsmb + launcher.Asmb2VehicleAsmb * endPart - craft.CenterOfMassAsmb);

            double length = Vec.Len(end - start);
            if (!(length > 0.0)) return false;
            Ray ray = new() { Origin = start, Direction = (end - start) / length };

            foreach (Part part in tree.Parts)
            {
                if (part == launcher || IsMount(part)) continue;
                if (!part.RayCastEgo(in asmb2Ego, ray, out double near, out double far,
                                     out _, out _, out _, out _, out _, out _))
                {
                    continue;
                }
                if ((near >= 0.0 && near <= length) || (near < 0.0 && far >= 0.0)) return true;
            }
            return false;
        }
        catch
        {
            // A craft the engine will not answer for blocks nothing, which is the profile's limit.
            return false;
        }
    }
```

   A hit anywhere within the segment, or an origin already inside a hull (`near < 0 <= far`), blocks. Other mounts and
   the launcher itself are excluded. An exception blocks nothing, which leaves the profile's fixed limit.
4. Publish the result as a pure-C# map with a band per bearing. Between two columns `BandAt` returns the narrower
   band, so a gun turning between samples never enters a cell either neighbour found blocked:

```csharp
// src/KSArmory/Sim/TravelMap.cs L53-L67
    public (double Floor, double Ceiling) BandAt(double bearingRad)
    {
        double at = Mod(bearingRad, Math.Tau) / BearingStepRad;
        int a = (int)Math.Floor(at) % Bearings;
        int b = (a + 1) % Bearings;

        bool usableA = _floorRow[a] >= 0, usableB = _floorRow[b] >= 0;
        if (!usableA && !usableB) return (MinElevationRad, MaxElevationRad);

        int floor = Math.Max(usableA ? _floorRow[a] : int.MinValue, usableB ? _floorRow[b] : int.MinValue);
        int ceiling = Math.Min(usableA ? _ceilingRow[a] : int.MaxValue, usableB ? _ceilingRow[b] : int.MaxValue);
        if (ceiling < floor) ceiling = floor;

        return (ElevationOfRow(floor), ElevationOfRow(ceiling));
    }
```

5. Use the map in the actuator: `Arc` replaces the traverse limit and the elevation band is intersected with the
   profile's travel at the current bearing every step; a limited arc is stepped in unwrapped coordinates so the mount
   turns the long way round an obstacle instead of through it.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/TravelSweeps.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/TravelSweeps.cs#L50-L188) | Budgeted scheduler, settle counter, `Blocked`, map hand-off |
| [src/KSArmory/Sim/TravelSweep.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TravelSweep.cs) | Grid sizing, coarse-to-fine order, probe segments |
| [src/KSArmory/Sim/TravelMap.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TravelMap.cs) | Bands per bearing, arc round forward, `BandAt` |
| [src/KSArmory/Sim/Turret.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Turret.cs#L77-L95) | `Map`, `Arc` and arc-aware clamping |
| [tests/KSArmory.Tests/TravelMapTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/TravelMapTests.cs) | Arc, band, coarse-pass and long-way-round tests without the game |

## Variations and gotchas

- Cost is linear in triangles for every part the ray crosses; the budget, not the algorithm, keeps it affordable.
- Untested cells count as clear; a craft that keeps changing shape is swept once, after it stops changing.
- Only primitive 0 of a multi-primitive mesh is cast.
- The map is in the craft's frame, so it stays valid as the craft moves; staging or breakup changes the part count
  and restarts the sweep.

## See also

- [Overview and comparison](overview.md)
- [Round-centred ego origin and sphere broad phase](ray-cast-against-vessel-meshes-and-terrain-1.md)
- Mod feature: [KSArmory self collision travel map](../../../../mods/ksarmory/features/self-collision-travel-map.md)
- The topic define-and-animate-subparts (other batch) covers subpart animation that moves what is swept
