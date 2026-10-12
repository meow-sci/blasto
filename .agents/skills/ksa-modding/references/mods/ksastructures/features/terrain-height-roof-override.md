---
type: Mod Feature
title: Terrain height answered as roof, ramp or floor
description: Postfixes the camera-frame terrain height query so wheels, cameras and placement see the garage roof, the ramp slabs or the pit floor depending on who is asking, while Bepu terrain collision keeps the real pit
mod: ksastructures
category: physics
techniques: [harmony-postfix, harmony-prefix, harmony-finalizer, custom-physics]
hooks:
  - harmony-postfix KSA.Celestial.GetTerrainHeightFromDirCce
  - harmony-prefix KSA.Vehicle.PrepareWorker
  - harmony-finalizer KSA.Vehicle.PrepareWorker
  - harmony-prefix KSA.Vehicle.TeleportToLocation
  - harmony-finalizer KSA.Vehicle.TeleportToLocation
lifecycle: [frame:dispatch-solvers, frame:viewports, frame:celestials, event:teleport]
game_types:
  - KSA.Celestial
  - KSA.Vehicle
  - KSA.KittenEva
  - KSA.Camera
  - KSA.TerrainPatch
  - KSA.Universe
  - KSA.StaticObjectTemplate
  - KSA.InputEvents
tags: [terrain-height, wheels, camera-clamp, teleport, thread-static, harmony, launch-pad]
---

A garage roof that is really a hole in the terrain has a problem: anything that asks the terrain
for its height over the roof is told "the pit floor, five metres down". KSA's own Bepu collision is
fine with that (the roof is a collider), but cameras clamp to the terrain height, vehicles placed by
latitude and longitude stand on it, and mod wheels (such as the sibling KSACars mod's) spring
against it.[^mod-claude] KSAStructures answers that question differently depending on who is asking:
the roof to whoever stands above it, a ramp slab on the ramps, the right turn of the deep spiral
road, and the floor to whoever is down in the garage.

## How it works

1. **Patch the camera-frame query, not the fixed-frame one.** `Celestial.GetTerrainHeightFromDirCce`
   transforms its direction into the body-fixed frame and calls `GetTerrainHeightFromDirCcf`.[^ksa-celestial]
   The Bepu terrain patches sample `GetTerrainHeightFromDirCcf` directly, so a postfix on the `Cce`
   overload changes what cameras, placement, altitude readouts and wheel code see without touching
   physics collision, which needs the real pit.[^ksa-terrainpatch][^mod-terrain]
2. **Only over the site, only where dug.** The postfix returns early unless the body is Earth's
   template, the direction is within 260 m of the tower landmark (a cheap dot-product test), and the
   terrain there is at least 5 cm below the tower's ground height (`dug >= 0.05`). The tower's ground
   height is cached per `Celestial` instance.[^mod-terrain]
3. **Ramps are slabs to everybody.** Inside a ramp's footprint the answer is the slab's top,
   interpolated along the ramp (`RampTop`).[^mod-terrain]
4. **Who is asking.** A `[ThreadStatic] Vehicle? _stepping` is set by a `Priority.First` prefix on
   `Vehicle.PrepareWorker(SimStep)` and cleared by a `Priority.Last` finalizer. During that window the
   asker is the vehicle being prepared, and anything a wheel mod computes from its own prefix on
   `PrepareWorker` is answered for that craft. Outside it the asker is the main camera.[^mod-terrain]
   Kittens (`_stepping is KittenEva`) are skipped: they walk on colliders.
5. **Roof or floor.** An asker above `roof + clear` gets the roof (ground + 0.08 m). `clear` is
   0.55 m for the camera, half a metre more than the engine's own camera clamp
   (`Camera.ClampCamera` calls `TryGetSurfaceClampPositionEcl(0.5)` from `Camera.OnFrame`), so a camera
   rising through the roof in the lift is not thrown upward; for a craft it is -0.3 m.[^ksa-camera]
   Where the pit is over 5.2 m deep and the asker is within 1.5 m of the floor, the answer is the floor.[^mod-terrain]
6. **The deep spiral is several floors over one pit.** Over the deep shaft and hall the terrain is a
   single 54 m pit under the cover, eight turns of road and the hall. `OfficeDeep.SurfaceUnder(east,
   north, askerUp)` returns the highest surface not above the asker: the cover, the first turn's level
   on that side of the core, each lower turn `Turn = 8` m further down, or the hall floor.[^mod-deep]
   It lives in `Sim/` and is unit-tested headlessly.[^mod-deep-tests]
7. **Placement on the roof by lending `SurfaceHeight`.** `Vehicle.TeleportToLocation` computes the
   start state immediately through `GetInitialKinematicStateForLocation`: the highest
   `GetTerrainHeightFromDirCcf` under the four corners of the craft's box plus
   `GetLaunchPadHeightAtDirCcf`, the `GroundOffset + SurfaceHeight` of the first launch pad whose
   footprint holds the point. It then only queues the result into `InputEvents.TeleportInputBuffer`,
   applied at the next frame's `InputEvents.ApplyInputEvents`.[^ksa-vehicle][^ksa-inputevents] The
   mod's prefix sets the tower template's `SurfaceHeight` to the least-dug corner's depth plus 0.08 m
   (`OnTheRoofAt`), returns an `IDisposable` as Harmony `__state`, and its finalizer disposes it,
   putting `SurfaceHeight` back to zero even if the original throws. The furniture spawner uses the
   same lending around its own `TeleportToLocation` call.[^mod-terrain][^mod-furniture]

## Lifecycle and timing

| When | Hook | Thread | What |
|---|---|---|---|
| `frame:dispatch-solvers` | prefix + finalizer on `Vehicle.PrepareWorker` | main | `Universe.ExecuteNextVehicleSolvers` -> `PrepareVehicleWorkers` calls `PrepareWorker` per vehicle on the main thread, then `PrepareFromVehicle` copies the kinematic state for the worker.[^ksa-universe][^ksa-vehicleupdatestate] The mod marks the asker and, in the same prefix, carries lift riders (see [the lift](./lift-moving-landmark.md)) |
| `frame:viewports` | postfix on `GetTerrainHeightFromDirCce` | main | Camera clamp inside `Camera.OnFrame`, from `GameViewport.OnFrame` |
| `frame:celestials` | same postfix | main | `Program.OnFrameCelestials` reads the camera's terrain height and `GetCurrentAltitudeKm`[^ksa-program] |
| any caller | same postfix | caller's | UI readouts, other mods; the cached tower ground height is a plain static, so the postfix assumes main-thread callers |
| `event:teleport` | prefix + finalizer on `Vehicle.TeleportToLocation(Celestial, double, double)` | main | Lend the roof height for the synchronous start-state computation |

`Vehicle.PrepareWorker` is `virtual` and `KittenEva` overrides it, but the override calls
`base.PrepareWorker`, so the prefix on `Vehicle.PrepareWorker` runs for kittens as well.

## Key code

| Path | Role |
|---|---|
| [src/KSAStructures/Ksa/OfficeTerrain.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeTerrain.cs#L218-L264) | `HoldUpWheels`: install the three patches |
| [src/KSAStructures/Ksa/OfficeTerrain.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeTerrain.cs#L266-L296) | Teleport prefix/finalizer and the `PrepareWorker` window |
| [src/KSAStructures/Ksa/OfficeTerrain.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeTerrain.cs#L298-L362) | `AfterTerrainHeight`: the answer |
| [src/KSAStructures/Ksa/OfficeTerrain.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeTerrain.cs#L364-L382) | Ramp table and `RampTop` |
| [src/KSAStructures/Ksa/OfficeTerrain.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeTerrain.cs#L384-L430) | `OnTheRoofAt`, `LeastDug`, the lent `SurfaceHeight` |
| [src/KSAStructures/Sim/OfficeDeep.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Sim/OfficeDeep.cs#L25-L75) | `Covers` and `SurfaceUnder` for the deep spiral |
| [tests/KSAStructures.Tests/OfficeDeepTests.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tests/KSAStructures.Tests/OfficeDeepTests.cs) | Headless tests of the spiral answer |

## Snippets

Installing the patches; the worker window brackets everything that runs inside `PrepareWorker`:

```csharp
// src/KSAStructures/Ksa/OfficeTerrain.cs L245-L257, L287-L296
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
    // ...
    private static void BeforeWorker(Vehicle __instance)
    {
        _stepping = __instance;

        // The engine copies the craft's state for its worker straight after this, so a rider is
        // moved here or the move is overwritten before anything reads it.
        try { Carry(__instance); } catch { }
    }

    private static void AfterWorker() => _stepping = null;
```

The postfix's filters and the ramp answer:

```csharp
// src/KSAStructures/Ksa/OfficeTerrain.cs L304-L336
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

Choosing roof, deep road or floor by who is asking:

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

Lending the pad surface height for the length of a teleport call:

```csharp
// src/KSAStructures/Ksa/OfficeTerrain.cs L388-L399, L421-L430
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
    // ...
    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();
        public void Dispose() { }
    }

    private sealed class Lent(StaticObject tower) : IDisposable
    {
        public void Dispose() => tower.Template.SurfaceHeight = new DistanceReference(GroundSurface);
    }
```

## Game APIs touched

| API | Decomp | Use |
|---|---|---|
| `Celestial.GetTerrainHeightFromDirCce`, `GetTerrainHeightFromDirCcf`, `GetCcf2Cce`, `MeanRadius` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L777-L800) | The patched query and the untouched fixed-frame one |
| `TerrainPatch` height sampling | [current/decomp/KSA/TerrainPatch.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TerrainPatch.cs#L517-L524) | Physics collision reads the `Ccf` overload |
| `Camera.OnFrame`, `ClampCamera`, `TryGetSurfaceClampPositionEcl` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L487-L657) | The 0.5 m camera clamp |
| `Vehicle.PrepareWorker(SimStep)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423) | Main-thread prepare before the worker copy |
| `Universe.PrepareVehicleWorkers` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2155) | Where `PrepareWorker` is called |
| `Vehicle.TeleportToLocation`, `GetInitialKinematicStateForLocation`, `GetLaunchPadHeightAtDirCcf` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L4135-L4232) | Placement height = max corner terrain + pad surface |
| `InputEvents.TeleportInputBuffer` | [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1026) | Teleport applied next frame |
| `Vehicle.BoundingBoxHalfExtentsAsmb`, `GetPositionCce` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L529) | Box corners and asker height |
| `StaticObjectTemplate.SurfaceHeight` | [current/decomp/KSA/StaticObjectTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObjectTemplate.cs#L21-L22) | Lent value |

## Pitfalls and version notes

- **Never patch `GetTerrainHeightFromDirCcf` for this.** It feeds Bepu's terrain patches and
  `ConstraintSim`'s launch-pad placement; changing it would lift the colliders off the pit.
- **The answer depends on the asker, so it is not a pure function.** Code that caches terrain
  heights across askers (or across frames while a camera moves through the roof) can see a jump.
- **`_stepping` assumes `PrepareWorker` runs on the main thread**, which it does in
  2026.10.10.5554 (`Universe.PrepareVehicleWorkers`). If KSA ever parallelizes that loop, the
  `[ThreadStatic]` mark still works but the cached `_groundOf`/`_groundHeight` statics would race.
- **Teleport is deferred.** Lending `SurfaceHeight` only works because the height is computed inside
  the call; the move itself lands a frame later through the input buffer.
- **The postfix must not throw**: it runs inside the camera's per-frame clamp and inside other
  mods' wheel code. It is wrapped in an empty `catch`.

## See also

- [Terrain decal pits](./terrain-decal-pits.md) - the pits being answered for
- [Lift as a moving landmark](./lift-moving-landmark.md) - shares the `PrepareWorker` prefix to carry riders
- [Office furniture as single-part craft](./office-furniture-craft.md) - uses the same `SurfaceHeight` lending
- [Generated deep ramp](./generated-deep-ramp.md) - the layout `SurfaceUnder` restates
- [Priority.First prefixes and Priority.Last finalizers around shared seams](../../../howto/patching/harmony/coexist-with-other-mods-patching-the-same-method/coexist-with-other-mods-patching-the-same-method-1.md) - how-to article demonstrating this feature
- [Mark the asker in a Priority.First prefix, clear it in a Priority.Last finalizer](../../../howto/patching/harmony/run-cleanup-or-recover-from-exceptions-with-a-finalizer/run-cleanup-or-recover-from-exceptions-with-a-finalizer-7.md) - how-to article demonstrating this feature
- [Postfix GetTerrainHeightFromDirCce keyed on who is asking](../../../howto/physics/aero-and-ground/override-terrain-height-for-roofs-and-platforms/override-terrain-height-for-roofs-and-platforms.md) - how-to article demonstrating this feature
- [Lend the pad template's SurfaceHeight around TeleportToLocation](../../../howto/physics/kinematics/place-a-vessel-on-a-surface-at-latitude-and-longitude/place-a-vessel-on-a-surface-at-latitude-and-longitude-3.md) - how-to article demonstrating this feature
- [Identify the vehicle a callback runs for with a ThreadStatic set in a prefix and cleared in a finalizer](../../../howto/physics/solver/write-worker-thread-safe-solver-patches/write-worker-thread-safe-solver-patches-5.md) - how-to article demonstrating this feature

[^mod-terrain]: src/KSAStructures/Ksa/OfficeTerrain.cs
[^mod-deep]: src/KSAStructures/Sim/OfficeDeep.cs
[^mod-furniture]: src/KSAStructures/Ksa/OfficeFurniture.cs
[^mod-claude]: KSAStructures CLAUDE.md (engine facts section)
[^mod-deep-tests]: tests/KSAStructures.Tests/OfficeDeepTests.cs
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-terrainpatch]: KSA.TerrainPatch (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatestate]: KSA.VehicleUpdateState (decompiled, build 2026.10.10.5554)
[^ksa-inputevents]: KSA.InputEvents (decompiled, build 2026.10.10.5554)
