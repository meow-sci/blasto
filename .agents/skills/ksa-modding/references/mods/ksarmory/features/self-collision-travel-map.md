---
type: Mod Feature
title: Gun travel limits swept from the craft's own geometry with part ray casts
description: Each gun mount's traverse arc and per-bearing elevation band are discovered by ray-casting its barrel and line of fire against the other parts of its craft, on a budget of 50 rays and 1 ms a frame once the craft has held its shape for 60 frames
mod: ksarmory
category: vehicles-and-parts
techniques: [part-ray-cast, frame-budgeted-work, coarse-to-fine-sampling, sim-ksa-split]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials]
lifecycle: [frame:draw-ui, frame:celestials]
game_types: [KSA.Part, KSA.Vehicle, KSA.PartTree, KSA.Ray, KSA.MeshViewModule]
tags: [turret, interlock, ray-cast, self-collision, travel-limit, frame-budget, part-picking]
---

A gun mount bolted onto an arbitrary craft must not swing its barrels through the hull or fire
through its own superstructure. KSArmory does not hand-author those limits per craft: it measures
them. For every training mount with a cannon (Phalanx, Mk 42, M197, the Pantsir's guns) it ray-casts
the barrel and a line of fire, at a grid of bearings and elevations, against the other parts of the
same craft using the engine's own part-picking ray cast, and turns the result into a traverse arc
plus a band of elevation per bearing that the turret drive clamps to.[^ksarmory-travelsweeps]

The work is spread across frames under a hard budget, starts only after the craft has kept its
shape for a second, and until it finishes the mount keeps its profile's fixed limits.

## How it works

| Stage | What happens |
|---|---|
| Select mounts | `Sweeps(profile) => profile.Trains && profile.HasCannon`; the focused craft's mounts are ordered first[^ksarmory-travelsweeps] |
| Wait for a stable shape | Per mount, state keyed on `(craft, launcher part, part count)`; any change resets it and the mount keeps its old map; the sweep starts after `SettleFrames = 60` unchanged frames |
| Size the grid | `TravelSweep.For(profile)`: 72 bearing columns (5 degrees) by `ceil((MaxElev - MinElev) / 5) + 1` elevation rows[^ksarmory-travelsweep] |
| Order the tests | Coarse pass: every 2nd column and every 2nd row (plus the top row). Fine pass: only cells whose four surrounding coarse cells disagree; the rest inherit the agreed answer |
| One pose = two probes | `TravelSweep.Probes`: trunnion to the mean muzzle (the barrel itself), then muzzle onward along the bore for `LineOfFireFor(MeanRadius) = max(30 m, 2 * radius)` |
| One probe = one ray per part | `Blocked`: segment to an ego-frame `Ray`, then `Part.RayCastEgo` against every part except the launcher itself and every other mount (training launchers and optic heads) |
| Budget | At most `RaysPerFrame = 50` rays and `MsPerFrame = 1.0` ms per frame across all mounts, measured with a `Stopwatch` |
| Publish | When the sweep is done: `TravelSweep.Result` builds a `TravelMap` (untested cells count as clear), assigned to `system.Turret.Map`, and one INFO log line summarizes arc, worst floor, tests, rays and milliseconds |

**Reading the map** (`TravelMap`, pure C#):[^ksarmory-travelmap]

- Per bearing column, the band is the contiguous clear run of rows that contains the rest elevation
  (or the clear row nearest it); a column with nothing clear is unusable.
- `Arc` is the run of usable columns contiguous with forward (column 0), or null for a whole turn.
- `BandAt(bearing)` interpolates nothing: between two columns it returns the narrower band, so a gun
  turning between samples never enters a cell either neighbour found blocked.

**Using the map** (`Turret`): once `Map` is set, `Arc` replaces `TraverseLimitRad`, and
`ElevationBandAt` intersects the profile's travel and forward depression floor with
`map.BandAt`. `Turret.Update` re-clamps elevation against the band at the *current* bearing every
step, and a limited arc is stepped in unwrapped coordinates so the mount turns the long way round
an obstacle instead of through it.[^ksarmory-turret] That is why the Phalanx profile can keep a full
traverse: "TravelMap keeps it off whatever craft it is on".[^ksarmory-arsenal]

**The ray cast it relies on.** `Part.RayCastEgo` (Part.cs L2534-L2572) first rejects the ray
against a bounding sphere from `BoundingBoxPartAsmb` and `ScaleTotal`, then asks each subpart's
`RayCastEgoSubPart` (L2574-L2609), which tests the subpart's `MeshViewModule` mesh (the
`<MeshView>` mesh in asset XML) with `Ray.RaycastWatertight` per triangle and returns near and far
hit distances along the ray.[^ksa-part][^ksa-meshviewmodule][^ksa-ray] It is the same call KSA
uses to highlight the part under the cursor in flight (`Vehicle.UpdateHighlight`, Vehicle.cs
L2903-L2926).[^ksa-vehicle]

## Lifecycle and timing

| Point | Detail |
|---|---|
| Hook | `TravelSweeps.Step` is called from `KSArmoryMod.StepSimulation` (KSArmoryMod.cs L704), inside `StepOnce`, so it runs in the `[StarMapAfterGui]` postfix on `OnDrawUiViewports` (Program.cs L2268) or, with the HUD hidden, in the prefix on `OnFrameCelestials` (L2320)[^ksarmory-mod][^ksa-program] |
| Not clock-gated | It runs before the simulated-step gate, so a sweep progresses while the game is paused |
| What it reads | Applied part transforms (`PositionVehicleAsmb`, `Asmb2VehicleAsmb`), `Vehicle.Asmb2Ego`, `CenterOfMassAsmb` and `GetMatrixAsmb2Ego(double3.Zero)`, all from the vehicle state applied in `PrepareFrame` (`ApplyVehicleSolvers`) |
| Why it is safe off the critical path | Everything is in one frame the engine built: both the ray and the matrix put the centre of mass at the origin, so no world position or epoch enters the test |
| Result consumed | Next `WeaponSystem.Update` -> `Turret.Update`, which also runs in `StepSimulation` (clock-gated) |

The 60-frame settle is counted per mount on frames where `Advance` is reached; a staging event or
break-up changes the part count and restarts it, so a craft losing parts over several frames is
swept once, after it stops changing.

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/TravelSweeps.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/TravelSweeps.cs#L50-L149) | Budgeted scheduler, settle counter, map hand-off |
| [src/KSArmory/Ksa/TravelSweeps.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/TravelSweeps.cs#L153-L188) | `Blocked`: one probe against the craft's parts; `IsMount` exclusion |
| [src/KSArmory/Sim/TravelSweep.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TravelSweep.cs) | Grid sizing, coarse-to-fine order, probe segments |
| [src/KSArmory/Sim/TravelMap.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TravelMap.cs) | Bands per bearing, arc round forward, `BandAt` |
| [src/KSArmory/Sim/Turret.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Turret.cs#L77-L95) | `Map`, `Arc` and arc-aware clamping |
| [src/KSArmory/Sim/TubeGeometry.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TubeGeometry.cs#L184-L186) | `GunPose`, shared by the probes and the drawn gun |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L1841-L1845) | `MeanRadius` with a 5 m fallback |
| [tests/KSArmory.Tests/TravelMapTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/TravelMapTests.cs) | Arc, band, coarse-pass and long-way-round tests without the game |

## Snippets

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

## Game APIs touched

| API | Decompiled source | Used for |
|---|---|---|
| `Part.RayCastEgo` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L2534-L2572) | Near/far hit distances of a ray against one part's subpart meshes |
| `Part.RayCastEgoSubPart` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L2574-L2609) | Engine side: per-subpart watertight triangle test on the `MeshView` mesh |
| `KSA.Ray` (`Origin`, `Direction`) | [current/decomp/KSA/Ray.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Ray.cs#L7-L11) | The probe ray, built with an object initializer and a pre-normalized direction |
| `MeshViewModule.MeshView` | [current/decomp/KSA/MeshViewModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshViewModule.cs) | Engine side: the mesh the ray is tested against |
| `Vehicle.GetMatrixAsmb2Ego(double3)`, `Vehicle.Asmb2Ego`, `Vehicle.CenterOfMassAsmb` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1276-L1281) | One consistent craft frame with the centre of mass at the origin |
| `Vehicle.MeanRadius` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L535) | Craft size for the line-of-fire length (`BoundingSphereRadiusBody`) |
| `PartTree.Parts`, `Part.Id` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L136) | Iterate full parts; skip other mounts by registry lookup on `Id` |
| `Part.PositionVehicleAsmb`, `Part.Asmb2VehicleAsmb` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L708-L738) | Launcher-frame probe segment into the vehicle frame |

## Pitfalls and version notes

- **Other mounts are excluded entirely**, base and all: their barrels move, and wherever they
  pointed during the sweep would stay blocked forever. Two guns on one craft can therefore still
  sweep through each other.[^ksarmory-travelsweeps]
- **Coarse-to-fine can miss small things.** A cell surrounded by four agreeing coarse cells is never
  tested, so an obstacle narrower than the coarse spacing (10 degrees) in both directions can be
  missed; CLAUDE.md says the same.[^ksarmory-travelsweep][^ksarmory-claude-md]
- **Failure means "no limit from the map".** Any exception in `Blocked` returns false (clear), and
  an untested cell counts as clear, so the profile's own limits are the fallback.
- **Only meshes with a `<MeshView>` are hit.** `RayCastEgoSubPart` returns false for a subpart with
  no `MeshViewModule` (Part.cs L2582-L2586), so a part without view meshes is invisible to the sweep
  as it is to cursor picking.[^ksa-part]
- **The line-of-fire length uses the bounding-sphere radius.** The code comment calls the input a
  "half-diagonal", but `Vehicle.MeanRadius` is `BoundingSphereRadiusBody` in 5554 (Vehicle.cs L535);
  `max(30 m, 2r)` still reaches past the far end from any mount.[^ksa-vehicle]
- **`Ray` initializer.** `KSA.Ray` is a primary-constructor struct whose constructor normalizes the
  direction (Ray.cs L7-L11); the mod's `new() { ... }` bypasses that, so it divides by the length
  itself.[^ksa-ray]

## See also

- [Articulated turrets by rewriting subpart transforms](./articulated-mount-subparts.md): the drive that clamps to this map
- [Weapon registry and profiles](./weapon-registry-and-profiles.md): `Trains`, `HasCannon` and the travel limits it starts from
- [Hull triangle hit test](./hull-triangle-hit-test.md): the same `RayCastEgo` used for round impacts
- [Cursor ray world click tools](./cursor-ray-world-click-tools.md): `RayCastEgo` for picking
- [One simulation step per frame](./dual-hook-simulation-step.md): where `StepSimulation` runs
- [Frame-budgeted sweeps of the craft's own geometry for actuator travel limits](../../../howto/physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/ray-cast-against-vessel-meshes-and-terrain-6.md) - how-to article demonstrating this feature

[^ksarmory-travelsweeps]: src/KSArmory/Ksa/TravelSweeps.cs
[^ksarmory-travelsweep]: src/KSArmory/Sim/TravelSweep.cs
[^ksarmory-travelmap]: src/KSArmory/Sim/TravelMap.cs
[^ksarmory-turret]: src/KSArmory/Sim/Turret.cs
[^ksarmory-arsenal]: src/KSArmory/Sim/Arsenal.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-claude-md]: CLAUDE.md (author notes)
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-ray]: KSA.Ray (decompiled, build 2026.10.10.5554)
[^ksa-meshviewmodule]: KSA.MeshViewModule (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
