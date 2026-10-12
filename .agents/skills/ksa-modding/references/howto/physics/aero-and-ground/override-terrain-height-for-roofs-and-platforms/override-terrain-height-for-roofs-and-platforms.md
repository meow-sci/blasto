---
type: How-To
title: Postfix GetTerrainHeightFromDirCce keyed on who is asking
description: Answer a roof, ramp slab, spiral level or pit floor from the camera-frame terrain height query depending on whether a vehicle being prepared or the camera is asking, without moving any collider
category: physics
group: aero-and-ground
topic: override-terrain-height-for-roofs-and-platforms
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksastructures/features/terrain-height-roof-override]
techniques: [harmony-postfix, harmony-prefix, harmony-finalizer]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-prefix KSA.Vehicle.PrepareWorker
  - harmony-finalizer KSA.Vehicle.PrepareWorker
  - harmony-postfix KSA.Celestial.GetTerrainHeightFromDirCce
  - harmony-prefix KSA.Vehicle.TeleportToLocation
lifecycle: [startup:all-mods-loaded, frame:dispatch-solvers, frame:viewports, frame:celestials]
game_types: [KSA.Celestial, KSA.Vehicle, KSA.Camera, KSA.StaticObject]
tags: [physics, terrain, height, roof, wheels, camera, harmony, postfix, threadstatic]
---

Outcome: over a site you choose, `Celestial.GetTerrainHeightFromDirCce` answers the top of a roof, ramp or platform to a camera
or a wheel mod standing above it, and the pit floor to whoever is down in it, while Bepu collision keeps reading the real
terrain. Prerequisites: StarMap and Harmony. Demonstrated by KSAStructures, whose garage roof is a hole in the terrain with a
collider over it; the sibling KSACars wheel code springs against this height (see
[sample the ground under each wheel](../sample-the-ground-under-each-wheel/overview.md)).

## Background: how the game does it

The terrain height is asked in several frames, and only the camera-frame overload is safe to change. `GetTerrainHeightFromDirCce`
rotates the direction into the body-fixed frame and delegates to `GetTerrainHeightFromDirCcf`, which is what the Bepu terrain
patches and the static-object placement call:[^ksa-celestial]

```csharp
// current/decomp/KSA/Celestial.cs L777-L781
	public double GetTerrainHeightFromDirCce(double3 positionDirCce, bool accurate = true)
	{
		double3 positionDirCcf = positionDirCce.Transform(GetCcf2Cce().Inverse());
		return GetTerrainHeightFromDirCcf(positionDirCcf, accurate);
	}
```

Cameras clamp to the `Cce` answer: `Camera.TryGetSurfaceClampPositionEcl(0.5, ...)`, called from `ClampCamera`, keeps the camera
half a metre above `MeanRadius + GetTerrainHeightFromDirCce(...)`.[^ksa-camera] The vehicle placement, by contrast, reads
`GetTerrainHeightFromDirCcf` under the four corners of the craft's box and then adds a launch pad's surface height, and only
queues the result into `InputEvents.TeleportInputBuffer`, applied at the next frame's `ApplyInputEvents`.[^ksa-vehicle][^ksa-inputevents]

## Lifecycle and timing

- **Install: `startup:all-mods-loaded`.** `Office.Place()` runs from the `[StarMapAllModsLoaded]` method and calls
  `HoldUpWheels()`, which installs the patches below.[^ksas-office]
- **The asker window: `frame:dispatch-solvers`, main thread.** `Universe.ExecuteNextVehicleSolvers` calls the virtual
  `Vehicle.PrepareWorker(SimStep)` on the main thread for every vehicle just before the worker snapshot is taken, and a wheel mod
  does its work from a prefix on that method because nothing written anywhere else survives.[^ksa-universe] The mod brackets
  that call: a `Priority.First` prefix records the vehicle, a `Priority.Last` finalizer clears it. `KittenEva` overrides
  `PrepareWorker` but calls `base.PrepareWorker`, so the bracket also covers kittens (the postfix skips them, since they walk
  on colliders).
- **Cameras and readouts: `frame:viewports`, `frame:celestials`.** The camera clamp is in `Camera.OnFrame`, and
  `Program.OnFrameCelestials` reads the camera's terrain height; both are outside the bracket, so the asker is taken to be the
  main camera.
- **Not on workers.** Nothing physical calls the `Cce` overload on a solver thread in this build. The cached tower height is a
  plain static, so the postfix assumes main-thread callers. See the
  [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and the general
  [ThreadStatic asker pattern](../../solver/write-worker-thread-safe-solver-patches/write-worker-thread-safe-solver-patches-5.md).

## Steps

1. **Resolve the targets and patch with the right priorities.** `PrepareWorker` is looked up by its `SimStep` parameter, the
   terrain query by its `(double3, bool)` parameters. The bracket gets the first and last priority so other `PrepareWorker`
   prefixes (a wheel mod's) run inside it. The teleport patch is optional and degrades quietly:

```csharp
// src/KSAStructures/Ksa/OfficeTerrain.cs L245-L257
            MethodInfo Own(string name) => typeof(Office).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
            var harmony = new Harmony(HarmonyId);
            harmony.Patch(worker, prefix: new HarmonyMethod(Own(nameof(BeforeWorker))) { priority = Priority.First },
                          finalizer: new HarmonyMethod(Own(nameof(AfterWorker))) { priority = Priority.Last });
            harmony.Patch(height, postfix: new HarmonyMethod(Own(nameof(AfterTerrainHeight))));

            // Any craft set down by latitude and longitude over the roof stands on it: a mover in
            // this mod or another, the launch menu, the console.
            if (typeof(Vehicle).GetMethod(nameof(Vehicle.TeleportToLocation), [typeof(Celestial), typeof(double), typeof(double)]) is { } teleport)
            {
                harmony.Patch(teleport, prefix: new HarmonyMethod(Own(nameof(BeforeTeleport))),
                              finalizer: new HarmonyMethod(Own(nameof(AfterTeleport))));
            }
```

   Expect `office: the garage roof holds up wheels that follow the terrain` in the log; on a missing method the mod logs that a
   car on the roof "sits on its belly" and installs nothing.[^ksas-officeterrain]

2. **Mark who is asking.** A `[ThreadStatic]` field holds the vehicle being prepared; the prefix sets it and the finalizer
   clears it (a finalizer runs even if the original throws):

```csharp
// src/KSAStructures/Ksa/OfficeTerrain.cs L219-L223
    // The craft whose worker the engine is preparing, on the thread preparing it. A mod that drives
    // wheels does it from a prefix on that method, because nothing written anywhere else survives,
    // so a terrain height asked for in between is asked on that craft's behalf.
    [ThreadStatic] private static Vehicle? _stepping;
```

```csharp
// src/KSAStructures/Ksa/OfficeTerrain.cs L286-L296
    // Inside the engine's frame loop: nothing here may throw.
    private static void BeforeWorker(Vehicle __instance)
    {
        _stepping = __instance;

        // The engine copies the craft's state for its worker straight after this, so a rider is
        // moved here or the move is overwritten before anything reads it.
        try { Carry(__instance); } catch { }
    }

    private static void AfterWorker() => _stepping = null;
```

3. **Filter cheaply before doing anything.** The postfix runs for every terrain query in the game. Return at once unless the body
   is the one you changed, the direction is within the site's reach (a dot-product test against the site's unit direction,
   no square root), and the terrain there is at least 5 cm below the site's ground height, i.e. the point is really dug. Cache
   the site's ground height per `Celestial` instance:

```csharp
// src/KSAStructures/Ksa/OfficeTerrain.cs L304-L326
    private static void AfterTerrainHeight(Celestial __instance, double3 positionDirCce, ref double __result)
    {
        try
        {
            if (_tower is null || _stepping is KittenEva) return;
            if (!ReferenceEquals(__instance.BodyTemplate, _earth)) return;

            double3 dirCcf = positionDirCce.Transform(__instance.GetCcf2Cce().Inverse());
            // Only over the site itself: within its reach of the tower, as an angle at the body's centre.
            double reach = SiteReachMetres / __instance.MeanRadius;
            if (double3.Dot(dirCcf, _tower.ForwardCcf) < 1.0 - (0.5 * reach * reach)) return;

            if (!ReferenceEquals(_groundOf, __instance))
            {
                _groundHeight = __instance.GetTerrainHeightFromDirCcf(_tower.ForwardCcf);
                _groundOf = __instance;
            }

            double ground = _groundHeight;
            double roof = ground + 0.08;
            double dug = ground - __result;
            if (dug < 0.05) return;
```

   The postfix sees the game's answer in `ref double __result` and only overrides it where it has something better to say.

4. **Convert to the structure's frame and answer slabs for everybody.** Project the direction's offset from the site onto the
   site's east and north axes (`y`, `z` metres), and for a ramp footprint return the slab top, interpolated along the ramp,
   to every asker:

```csharp
// src/KSAStructures/Ksa/OfficeTerrain.cs L327-L336
            _tower.GetAxesCcf(out _, out double3 east, out double3 north);
            double3 from = (dirCcf - _tower.ForwardCcf) * (__instance.MeanRadius + ground);
            double y = double3.Dot(from, east) - 3.0, z = double3.Dot(from, north) + LandmarkNorth;

            // A ramp is its slab to everybody: the terrain under it is only there to be dug.
            if (RampTop(y, z) is { } slab)
            {
                __result = Math.Max(__result, ground + slab);
                return;
            }
```

5. **Choose the level by who is asking.** The asker's height is the craft's own (when inside the bracket) or the main camera's.
   Above the roof by a clearance the asker gets the roof; deep in the pit it gets the floor. The camera needs 0.55 m of clearance,
   half a metre more than the engine's own clamp, otherwise a camera rising through the roof is told the ground jumped up under
   it and is thrown upward; a craft uses -0.3 m. Where several floors share one pit, ask a function that returns the highest
   surface not above the asker:

```csharp
// src/KSAStructures/Ksa/OfficeTerrain.cs L338-L358
            // Who is asking stands above the roof and gets the roof, or under it and gets the floor.
            // A camera only once it is clear of the roof by the half metre its own clamp keeps over
            // the ground: one rising through the roof's height, in the lift, is otherwise told the
            // ground has jumped up under it and is thrown up to meet it.
            double3 asker = _stepping is { } craft ? craft.GetPositionCce()
                          : Program.GetMainCamera() is { } camera ? camera.PositionEcl - __instance.GetPositionEcl()
                          : default;
            double clear = _stepping is null ? 0.55 : -0.3;
            double askerHeight = asker.Length() - __instance.MeanRadius;

            // Over the deep ramp the terrain is one pit under several things, and which of them is
            // the ground depends on how high whoever is asking stands.
            if (OfficeDeep.Covers(y, z))
            {
                __result = ground + OfficeDeep.SurfaceUnder(y, z, askerHeight - ground + (_stepping is null ? 0.0 : 0.3));
                return;
            }

            if (askerHeight >= roof + clear) __result = roof;
            else if (dug > 5.2 && askerHeight > ground + Floor - 1.5) __result = ground + Floor;
        }
```

6. **Never let the postfix throw.** The whole body sits in an empty `catch`; the postfix runs inside the camera clamp and inside
   other mods' wheel code, and a throw there stalls the frame.

7. **Optional: stand a teleported craft on the roof by lending a pad height.** `Vehicle.TeleportToLocation` computes the start
   height synchronously from `GetTerrainHeightFromDirCcf` (which you did not change) plus a launch pad's surface height, then
   queues the move. A prefix sets the site's static-object template `SurfaceHeight` to the roof's depth over the box's
   least-dug corner, returns an `IDisposable` as Harmony `__state`, and the finalizer disposes it, restoring the template value
   even if the original throws:

```csharp
// src/KSAStructures/Ksa/OfficeTerrain.cs L388-L399
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
```

## Complete example

| File | Role |
|---|---|
| [src/KSAStructures/Ksa/OfficeTerrain.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeTerrain.cs#L233-L430) | `HoldUpWheels` (installation), the `PrepareWorker` bracket, `AfterTerrainHeight`, ramps, the teleport lending |
| [src/KSAStructures/Sim/OfficeDeep.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Sim/OfficeDeep.cs#L25-L75) | `Covers` and `SurfaceUnder` for several floors over one pit; plain geometry, unit-testable without the game |
| [src/KSAStructures/Ksa/Office.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Office.cs#L51-L92) | `Place()` calls `HoldUpWheels()` at `StarMapAllModsLoaded` |

## Variations and gotchas

- **Never patch `GetTerrainHeightFromDirCcf` for this.** It feeds the Bepu terrain patches and the static-object placement;
  changing it lifts the colliders off the pit.
- **The answer depends on the asker, so it is not a pure function.** Code that caches terrain heights across askers, or across
  frames while a camera moves through the roof, can see a jump.
- **`_stepping` assumes `PrepareWorker` runs on the main thread**, which it does in build 2026.10.10.5554. If a game update
  parallelized that loop, the `[ThreadStatic]` mark would still be per-thread correct, but the cached `_groundOf` and
  `_groundHeight` statics would race.
- **Teleport is deferred.** Lending `SurfaceHeight` only works because the height is computed inside the call; the move itself
  lands a frame later through the input buffer.
- **Other callers see the roof too.** Altitude readouts, the biome sound controller, the ground track window and mods that read
  the `Cce` overload are all answered by the same postfix, by design.
- **Keep the clearance asymmetric.** A camera needs more clearance than a craft, or it is thrown through the roof as it rises.

## See also

- [Overview](overview.md)
- [Sample terrain and pad surfaces under each wheel](../sample-the-ground-under-each-wheel/overview.md)
- [Identify the vehicle a callback runs for with a ThreadStatic](../../solver/write-worker-thread-safe-solver-patches/write-worker-thread-safe-solver-patches-5.md)
- [Place a vessel on a surface at latitude and longitude](../../kinematics/place-a-vessel-on-a-surface-at-latitude-and-longitude/overview.md)
- Mod feature: [Terrain height roof override](../../../../mods/ksastructures/features/terrain-height-roof-override.md)
- The topic query-terrain-height-and-line-of-sight (other batch) covers asking the height directly

[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-inputevents]: KSA.InputEvents (decompiled, build 2026.10.10.5554)
[^ksas-office]: KSAStructures: src/KSAStructures/Ksa/Office.cs
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksas-officeterrain]: KSAStructures: src/KSAStructures/Ksa/OfficeTerrain.cs
