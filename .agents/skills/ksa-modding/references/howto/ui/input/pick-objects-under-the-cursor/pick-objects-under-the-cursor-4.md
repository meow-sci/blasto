---
type: How-To
title: "Pick objects under the cursor: clearance-stepped terrain march with bisection, then a buffered TeleportToLocation"
description: Find the terrain point under the cursor by walking the ray out from the eye and bisecting the first crossing, then place a craft there once per action with Vehicle.TeleportToLocation
category: ui
group: input
topic: pick-objects-under-the-cursor
approach: 4
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/ksagolf/features/cursor-picking-and-terrain-ray
  - mods/ksacars/features/craft-mover-mouse-pick-place
techniques: [imgui-drawlist, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:input]
game_types: [KSA.Celestial, KSA.CelestialSystem, KSA.Vehicle, KSA.InputEvents, KSA.Universe, KSA.Camera]
tags: [picking, terrain, raycast, height-field, bisection, teleport, placement]
---
The engine has no CPU ray-versus-terrain routine; the only thing a mod can ask is the height of the ground in a direction
from a body's centre. KSAGolf and KSACars turn that into "the first place this ray goes under the ground": walk the ray out
from the eye in steps as long as its clearance above the ground, then bisect the crossing. The same code feeds a craft
mover that sets a vessel down at the picked latitude and longitude once per click. The sections below are the march and the
teleport. Demonstrated by KSAGolf (`Sim/TerrainRay`) and KSACars (the same tool).

## Background: how the game does it

- `Celestial.GetTerrainHeightFromDirCce(dirFromCentre, accurate)` returns metres of terrain above the mean radius for a unit
  direction in body-centred ecliptic axes. `accurate: false` is a bilinear sample (the physics path); `true` adds bicubic
  filtering and the procedural modifier chain.[current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L777-L781)[^ksa-celestial][current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L789-L800)[^ksa-celestial]
- `Celestial.GetLatitudeFromCce` and `GetLongitudeFromCce` turn a body-centred point into the arguments of
  `Vehicle.TeleportToLocation`.[current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L691-L732)[^ksa-celestial]
- `Vehicle.TeleportToLocation(celestial, lat, lon)` builds the craft's initial kinematic state from its bounding box
  (`GetInitialKinematicStateForLocation`, which also adds a launch pad's height inside its footprint) and **queues** a
  `TeleportInputData` on `InputEvents.TeleportInputBuffer`.[current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L4219-L4233)[^ksa-vehicle][current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L4135-L4192)[^ksa-vehicle]
- `InputEvents.ApplyInputEvents` applies the buffer in `PrepareFrame`: `TeleportInputData.Apply` clears the burn plan, calls
  `Vehicle.Teleport(orbit, body2Cce, bodyRates)` and resets the transfer planner.[current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L407-L423)[^ksa-inputevents]

## Lifecycle and timing

The pick runs on the main thread in `[StarMapAfterGui]` (`frame:draw-ui`), after the panel so a click on a window is the
window's. The teleport is queued there and applied on the **next** frame at `frame:input` (`InputEvents.ApplyInputEvents`,
between `Apply*Solvers` and `ExecuteNext*Solvers`), the one main-thread window where physics results are settled. Queuing is
what makes calling from the GUI hook safe: the mod never mutates vehicle state while solver jobs run. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Wrap the height query so the march is testable and cannot throw into the frame:

```csharp
// src/KSAGolf/Ksa/TerrainHeights.cs L14-L34
internal sealed class TerrainHeights(Celestial body, bool accurate = false) : ITerrainHeights
{
    private readonly Celestial _body = body;
    private readonly bool _accurate = accurate;

    public bool TryHeight(double3 dirFromCentre, out double metres)
    {
        metres = 0.0;

        try
        {
            metres = _body.GetTerrainHeightFromDirCce(dirFromCentre, accurate: _accurate);

            return double.IsFinite(metres);
        }
        catch
        {
            return false;
        }
    }
}
```

2. Walk the ray. Only the stretch of the ray below the body's highest terrain is walked (closed form, `TerrainMask.TryBandBelow`).
   Each step is as long as the ray's clearance above the ground at that point (terrain rising no faster than 1:1 cannot get above it
   in between), clamped to 1..1000 m and never shorter than 0.5% of the range walked; at most 512 samples:

```csharp
// src/KSAGolf/Sim/TerrainRay.cs L40-L43,L56-L93
public static bool TryFirstHit(double3 origin, double3 direction, double maxRange, double3 centre,
                               double meanRadius, double maxTerrainHeight, ITerrainHeights heights,
                               out double range)
{
// ...
    double3 dir = Vec.Unit(direction);
    if (!Vec.IsFinite(dir) || Vec.Len2(dir) < 0.5) return false;

    double top = double.IsFinite(maxTerrainHeight) ? Math.Max(0.0, maxTerrainHeight) : 0.0;
    if (!TerrainMask.TryBandBelow(origin, origin + (dir * maxRange), centre, meanRadius + top,
                                  out double from, out double to))
    {
        return false;
    }

    double t = from * maxRange;
    double end = to * maxRange;
    double clearAt = double.NaN;

    for (int i = 0; i < MaxSamples && t <= end; i++)
    {
        double floor = Math.Max(MinStepMetres, t * MinStepFraction);

        if (!TryClearance(origin, dir, t, centre, meanRadius, heights, out double clearance))
        {
            t += floor;
            continue;
        }

        if (clearance <= 0.0)
        {
            if (double.IsNaN(clearAt)) return false;

            range = Refine(origin, dir, clearAt, t, centre, meanRadius, heights);
            return true;
        }

        clearAt = t;
        t += Math.Clamp(Math.Max(clearance / MaxSlope, floor), MinStepMetres, MaxStepMetres);
    }

    return false;
}
```

3. Bisect from the last clear sample to the first underground one until the crossing is known to 0.5 m, using the clearance above
   the mean radius plus height:

```csharp
// src/KSAGolf/Sim/TerrainRay.cs L97-L131
private static double Refine(double3 origin, double3 dir, double above, double below, double3 centre,
                             double meanRadius, ITerrainHeights heights)
{
    for (int i = 0; i < 40 && below - above > ToleranceMetres; i++)
    {
        double mid = 0.5 * (above + below);

        if (TryClearance(origin, dir, mid, centre, meanRadius, heights, out double clearance) && clearance > 0.0)
        {
            above = mid;
        }
        else
        {
            below = mid;
        }
    }

    return 0.5 * (above + below);
}

// How far the point this far along the ray stands above the ground under it.
private static bool TryClearance(double3 origin, double3 dir, double t, double3 centre, double meanRadius,
                                 ITerrainHeights heights, out double clearance)
{
    clearance = 0.0;

    double3 radial = origin + (dir * t) - centre;
    double radius = Vec.Len(radial);
    if (!(radius > 0.0)) return false;

    if (!heights.TryHeight(radial / radius, out double height) || !double.IsFinite(height)) return false;

    clearance = radius - (meanRadius + height);
    return true;
}
```

   Walking out from the eye is the point. Hitting the mean sphere and refining by the height under that hit samples ground behind a
   hill seen side-on and lands the pick on the terrain beyond what the pointer is on. The march answers false when the first sample is
   already underground (an eye inside a hill points at nothing nameable).
4. Try every body and keep the nearest hit, so pointing at a moon on the horizon means the moon. The upper bound for the band is
   `MaxTerrainRadius - MeanRadius` plus headroom for the procedural modifiers:

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L457-L504
public static bool TryGroundAlong(double3 eye, double3 direction, out double3 groundEcl, out Celestial? nearest)
{
    groundEcl = default;
    nearest = null;

    try
    {
        if (Universe.CurrentSystem is not { } system) return false;

        double nearestRange = double.MaxValue;

        for (int i = 0; i < system.Count; i++)
        {
            if (system.GetIndex(i) is not Celestial body) continue;

            double3 centre = body.GetPositionEcl();
            double top = MaxTerrainHeightMetres(body);

            // Far enough to cross the whole body. Only the part below its highest ground is
            // walked, and the walk stops at the first place it is under it.
            double reach = 2.0 * (Vec.Len(eye - centre) + body.MeanRadius + top);

            // Terrain only. A launch pad is 8 m of pedestal 40 m across, and adding it here
            // models it as an 8 m thicker planet: at 5 km the resolved point moves 2.8 km, and
            // sweeping the cursor over the pad edge swings the bearing from the mount through
            // 168 degrees between one pixel and the next. Where a structure's surface is has
            // no answer in this engine -- see docs/BLOCKED-ON-KSA.md.
            if (!TerrainRay.TryFirstHit(eye, direction, reach, centre, body.MeanRadius, top,
                                        new TerrainHeights(body, accurate: true), out double range)
                || range >= nearestRange)
            {
                continue;
            }

            nearest = body;
            nearestRange = range;
        }

        if (nearest is null) return false;

        groundEcl = eye + (Vec.Unit(direction) * nearestRange);
        return true;
    }
    catch
    {
        return false;
    }
}
```

5. Name the point: latitude, longitude and body id from the body-centred offset:

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L428-L452
public static bool TryCursorGroundPoint(out double3 groundEcl,
                                        out double latitudeDeg, out double longitudeDeg,
                                        out string bodyName)
{
    groundEcl = default;
    latitudeDeg = 0.0;
    longitudeDeg = 0.0;
    bodyName = string.Empty;

    if (!TryCursorRayEcl(out double3 eye, out double3 direction)) return false;
    if (!TryGroundAlong(eye, direction, out groundEcl, out Celestial? nearest)) return false;

    try
    {
        double3 cce = groundEcl - nearest!.GetPositionEcl();
        latitudeDeg = nearest.GetLatitudeFromCce(cce);
        longitudeDeg = nearest.GetLongitudeFromCce(cce);
        bodyName = nearest.Id ?? string.Empty;
        return true;
    }
    catch
    {
        return false;
    }
}
```

## Teleport a craft to the picked latitude and longitude

Both mods put the pick-and-place tool on top of the march. Place **once per action**:

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L534-L560
public static bool TryPlaceOnSurface(Vehicle craft, string bodyName,
                                     double latitudeDeg, double longitudeDeg)
{
    if (!IsAlive(craft)) return false;
    if (!double.IsFinite(latitudeDeg) || !double.IsFinite(longitudeDeg)) return false;

    try
    {
        if (Universe.CurrentSystem is not { } system) return false;

        for (int i = 0; i < system.Count; i++)
        {
            if (system.GetIndex(i) is not Celestial body) continue;
            if (body.Id != bodyName) continue;

            craft.TeleportToLocation(body, latitudeDeg, longitudeDeg);
            return true;
        }

        return false;
    }
    catch (Exception e)
    {
        Log.Warn($"could not place {DisplayName(craft)}: {e.Message}");
        return false;
    }
}
```

- **A craft under the cursor answers with its own footing**, not with the ray, because a ray through a vehicle meets the ground
  behind it; clicking the held craft, or within 12 m of it, leaves it alone (placement re-derives the height from latitude,
  longitude and the surface rule, which on a launch pad is metres below where the craft stands):

```csharp
// src/KSAGolf/Ksa/CraftMover.cs L153-L189
private void PutDown()
{
    if (_held is not { } craft) return;

    // Pointing at the held craft means "leave it". Placing re-derives a position from the
    // latitude and longitude and the surface rule, which is not where the craft is: on a
    // launch pad it is metres below, so a click that should change nothing drops it.
    if (ReferenceEquals(_hovered, craft))
    {
        Log.Info($"left {KsaWorld.DisplayName(craft)} where it was");
        _held = null;
        return;
    }

    if (!TryTarget(out double3 target, out double lat, out double lon, out string body))
    {
        Log.Info("nothing under the cursor to set it down on");
        return;
    }

    // Or near enough to it. The pick radius is generous, so the cursor can be a little off the
    // craft and still mean the same thing.
    if (Vec.Len(target - KsaWorld.PositionEcl(craft)) < StayPut)
    {
        Log.Info($"left {KsaWorld.DisplayName(craft)} where it was");
        _held = null;
        return;
    }

    if (KsaWorld.TryPlaceOnSurface(craft, body, lat, lon))
    {
        Log.Info($"placed {KsaWorld.DisplayName(craft)} on {body} at "
                 + $"{lat:F4}, {lon:F4}");
    }

    _held = null;
}
```

- **Do not teleport every frame.** Each call rebuilds the craft's orbit and velocity; the mover deliberately does not drag the craft.
- **Know what the call does** (from KSACars' notes, checked against `Vehicle.GetInitialKinematicStateForLocation`): over water it
  rests the hull on the seabed so a buoyant craft then rises; it adds a launch pad's height inside the pad footprint
  (`GetLaunchPadHeightAtDirCcf`); and it sets the attitude from `ComputeBody2Cce`, so you cannot choose a heading.

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Sim/TerrainRay.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/TerrainRay.cs) | `TryFirstHit`, `Refine`, `TryClearance` |
| [src/KSAGolf/Sim/TerrainMask.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/TerrainMask.cs) | `TryBandBelow` |
| [src/KSAGolf/Ksa/TerrainHeights.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/TerrainHeights.cs) | Height-query wrapper |
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L457-L504) | `TryGroundAlong` |
| [src/KSAGolf/Ksa/CraftMover.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CraftMover.cs#L153-L189) | `PutDown` |
| [src/KSACars/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.cs#L359-L390) | KSACars' `TryPlaceOnSurface` |
| [tests/KSACars.Tests/TerrainRayTests.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tests/KSACars.Tests/TerrainRayTests.cs) | A hill in front is hit, not the ground behind it; cost bound |

## Variations and gotchas

- **Terrain only, on purpose.** Adding a launch pad's height to the cursor ground models it as a thicker planet and makes the
  resolved point jump kilometres at the pad's edge; there is no engine query for a structure's surface.
- **`accurate: true` matters for picking.** The coarse query skips the procedural modifiers and levelling decals.
- **Sky and eye-in-hill name nothing.** Handle the false.
- **Seabed placement and repeated teleports.** KSACars' notes record that a grounded craft set down again and again can be destroyed.
- **Cost.** The sample cap bounds an ordinary pick to a few hundred height lookups; the repo's test asserts it.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of pick-objects-under-the-cursor](overview.md)
- [Approach 1: screen-space nearest](pick-objects-under-the-cursor-1.md)
- [Approach 2: Part.RayCastEgo hull test plus terrain march](pick-objects-under-the-cursor-2.md)
- [Approach 5: Ray.RaycastWatertight plus terrain bisection](pick-objects-under-the-cursor-5.md)
- [Ray cast against part meshes and terrain, from the cursor or any origin](../../../physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/overview.md)
- Mod features: [Cursor picking and terrain ray](../../../../mods/ksagolf/features/cursor-picking-and-terrain-ray.md),
  [Craft mover mouse pick and place](../../../../mods/ksacars/features/craft-mover-mouse-pick-place.md)

[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-inputevents]: KSA.InputEvents (decompiled, build 2026.10.10.5554)
