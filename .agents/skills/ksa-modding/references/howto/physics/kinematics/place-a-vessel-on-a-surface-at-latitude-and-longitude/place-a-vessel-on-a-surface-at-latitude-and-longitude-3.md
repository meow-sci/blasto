---
type: How-To
title: Lend the pad template's SurfaceHeight around TeleportToLocation
description: Stand a craft on a roof by temporarily setting a launch-pad static object's SurfaceHeight in a prefix on TeleportToLocation and restoring it in a finalizer, since the start state is computed synchronously from terrain plus pad height
category: physics
group: kinematics
topic: place-a-vessel-on-a-surface-at-latitude-and-longitude
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksastructures/features/terrain-height-roof-override]
techniques: [harmony-prefix, harmony-finalizer]
hooks: [harmony-prefix KSA.Vehicle.TeleportToLocation, harmony-finalizer KSA.Vehicle.TeleportToLocation]
lifecycle: [event:teleport, frame:dispatch-solvers]
game_types: [KSA.Vehicle, KSA.StaticObject, KSA.LocationReference, KSA.Celestial]
tags: [physics, kinematics, teleport, surface, roof, surface-height, launch-pad, ksastructures]
---

Outcome: any craft set down by latitude and longitude over a structure's roof, from this mod, another mod, the launch menu or the
console, stands on the roof rather than the dug ground beneath it. KSAStructures' garage roof is really a hole in the terrain
(the pit floor is five metres down), so the terrain height under it is wrong for a craft that should stand on the roof. Prerequisites:
StarMap and Harmony. The terrain-height half of the problem is a separate topic; this article covers only the placement.

## Background: how the game does it

`TeleportToLocation` computes the start state immediately through `GetInitialKinematicStateForLocation`: the highest
`GetTerrainHeightFromDirCcf` under the four corners of the craft's box plus `GetLaunchPadHeightAtDirCcf`, which is the
`GroundOffset + SurfaceHeight` of the first launch-pad landmark in list order whose footprint holds the point:

```csharp
// current/decomp/KSA/Vehicle.cs L4193-L4215
	private static double GetLaunchPadHeightAtDirCcf(Celestial celestial, double3 dirCcf)
	{
		CelestialTemplate celestialTemplate = celestial.BodyTemplate;
		if (celestialTemplate == null)
		{
			return 0.0;
		}
		foreach (LocationReference location in celestialTemplate.Locations)
		{
			if (!(location is LandmarkReference { IsLaunchPad: not false } landmarkReference))
			{
				continue;
			}
			StaticObject staticObject = landmarkReference.GetStaticObject();
			if (staticObject != null)
			{
				double num = (landmarkReference.ForwardCcf - dirCcf).Length() * celestial.MeanRadius;
				if (num < staticObject.FootprintRadius)
				{
					return staticObject.GroundOffset + staticObject.SurfaceHeight;
				}
			}
		}
```

It then only queues the result into `InputEvents.TeleportInputBuffer`, applied at the next frame's `ApplyInputEvents`. So the
numbers are read synchronously inside the call, and a number that is set before and restored after the call is seen exactly
by this one placement.[^ksa-vehicle]

## Lifecycle and timing

| When | Hook | Thread | What |
|---|---|---|---|
| `event:teleport` | prefix + finalizer on `Vehicle.TeleportToLocation(Celestial, double, double)` | main | Lend the roof height for the synchronous start-state computation |
| `frame:dispatch-solvers` | prefix and finalizer on `Vehicle.PrepareWorker` | main | Marks which craft is asking for terrain height (a separate feature) |

See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Patch `TeleportToLocation(Celestial, double, double)` by signature with a prefix that returns an `IDisposable` as Harmony
   `__state` and a finalizer that disposes it, so the template is restored even when the original throws. Wrap everything so an
   exception in the lending never blocks the placement:

```csharp
// src/KSAStructures/Ksa/OfficeTerrain.cs L266-L285
    private static void BeforeTeleport(Vehicle __instance, Celestial celestial, double lat, double lon, out IDisposable __state)
    {
        __state = Nothing.Instance;
        try
        {
            // Its box as it will stand, +Y east and +Z north.
            float3 half = __instance.BoundingBoxHalfExtentsAsmb;
            __state = OnTheRoofAt(celestial, lat, lon, half.Y, half.Z);
        }
        catch
        {
        }
    }

    private static Exception? AfterTeleport(IDisposable? __state, Exception? __exception)
    {
        try { __state?.Dispose(); } catch { }
        return __exception;
    }

```

2. In `OnTheRoofAt`, decide whether the destination is over the dug site (within 200 m, not on a ramp, ground dug by 0.3 m or more at
   the centre). If so, set the tower template's `SurfaceHeight` to the least-dug corner's depth plus 0.08 m and return a disposable
   that sets it back. The game's own placement stands a craft on the highest ground under the four corners of its box, so the
   height lent is the roof's over that corner:

```csharp
// src/KSAStructures/Ksa/OfficeTerrain.cs L384-L432
    // For as long as the answer is held, a craft set down at this place stands on the roof over the
    // dug ground where there is one, and not on the floor under it. The game's own placement stands
    // a craft on the highest ground under the four corners of its box and adds a launch pad's
    // surface height inside its footprint, so the height lent is the roof's over that corner.
    private static IDisposable OnTheRoofAt(Celestial body, double latitudeDeg, double longitudeDeg, double halfEast, double halfNorth)
    {
        if (_furnishing || _tower?.GetStaticObject() is not { } tower || !ReferenceEquals(body.BodyTemplate, _earth)) return Nothing.Instance;

        double y = ((longitudeDeg - TowerLongitudeDeg) * 97730.0) - 3.0;
        double z = (latitudeDeg - LatitudeDeg) * 110860.0;
        if (Math.Abs(y) > 200.0 || Math.Abs(z) > 200.0 || RampTop(y, z) is not null) return Nothing.Instance;
        if (LeastDug(body, y, z, 0.0, 0.0) < 0.3) return Nothing.Instance;

        tower.Template.SurfaceHeight = new DistanceReference(LeastDug(body, y, z, halfEast, halfNorth) + 0.08);
        return new Lent(tower);
    }

    // How far the ground is dug under the least-dug corner of a box standing at a place in the
    // building's frame: what the game's own placement stands a craft on.
    private static double LeastDug(Celestial body, double y, double z, double halfEast, double halfNorth)
    {
        double ground = body.GetTerrainHeightFromDirCcf(_tower!.ForwardCcf);
        _tower.GetAxesCcf(out _, out double3 east, out double3 north);

        double least = double.MaxValue;
        foreach (double dy in (ReadOnlySpan<double>)[-halfEast, halfEast])
        {
            foreach (double dz in (ReadOnlySpan<double>)[-halfNorth, halfNorth])
            {
                double3 at = (_tower.ForwardCcf * (body.MeanRadius + ground)) + (east * (y + dy + 3.0)) + (north * (z + dz - LandmarkNorth));
                least = Math.Min(least, Math.Max(0.0, ground - body.GetTerrainHeightFromDirCcf(at.Normalized())));
            }
        }

        return least;
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();
        public void Dispose() { }
    }

    private sealed class Lent(StaticObject tower) : IDisposable
    {
        public void Dispose() => tower.Template.SurfaceHeight = new DistanceReference(GroundSurface);
    }
}

```

3. Respect a re-entrant caller. A spawner that sets its own height (approach 4) sets a `_furnishing` flag so this general lending
   stays out of the way.
4. Remember the landmark order: the first landmark whose footprint holds the point gives its height, and the mod appended its
   landmarks after Core's.

## Complete example

| File | Role |
|---|---|
| [src/KSAStructures/Ksa/OfficeTerrain.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeTerrain.cs#L245-L296) | Install the patches; the teleport prefix and finalizer |
| [src/KSAStructures/Ksa/OfficeTerrain.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeTerrain.cs#L384-L432) | `OnTheRoofAt`, `LeastDug`, `Lent` |

## Variations and gotchas

- The template is shared state: the lending is only safe because it is set and restored on the main thread around one synchronous call.
- A craft with a big box standing near the roof's edge takes the highest corner, which `LeastDug` computes for the box as it will stand (+Y east, +Z north).
- This does not make the roof collide: that is the static object's collider; see [add static colliders](../../colliders/add-static-colliders-to-physics-bubbles/add-static-colliders-to-physics-bubbles-3.md).

## See also

- [Overview and comparison](overview.md)
- [Build one-part craft in code and set them on the ground](place-a-vessel-on-a-surface-at-latitude-and-longitude-4.md)
- Mod feature: [KSAStructures terrain height roof override](../../../../mods/ksastructures/features/terrain-height-roof-override.md)
- The topic override-terrain-height-for-roofs-and-platforms (other batch) covers the terrain-height half

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
