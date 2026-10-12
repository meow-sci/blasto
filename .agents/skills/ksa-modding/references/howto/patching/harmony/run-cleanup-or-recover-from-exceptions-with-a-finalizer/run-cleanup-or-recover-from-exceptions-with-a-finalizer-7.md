---
type: How-To
title: Mark the asker in a Priority.First prefix, clear it in a Priority.Last finalizer
description: Answer one shared terrain height query differently for the vehicle being prepared and for the camera by marking the asker in a Priority.First prefix on Vehicle.PrepareWorker and clearing it in a Priority.Last finalizer, and lend a value across TeleportToLocation the same way
category: patching
group: harmony
topic: run-cleanup-or-recover-from-exceptions-with-a-finalizer
approach: 7
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksastructures/features/terrain-height-roof-override]
techniques: [harmony-prefix, harmony-postfix, harmony-finalizer, thread-static-scope]
hooks:
  - harmony-prefix KSA.Vehicle.PrepareWorker
  - harmony-finalizer KSA.Vehicle.PrepareWorker
  - harmony-postfix KSA.Celestial.GetTerrainHeightFromDirCce
  - harmony-prefix KSA.Vehicle.TeleportToLocation
  - harmony-finalizer KSA.Vehicle.TeleportToLocation
lifecycle: [frame:dispatch-solvers, frame:viewports, frame:celestials]
game_types: [KSA.Vehicle, KSA.Celestial, KSA.Universe, KSA.KittenEva]
tags: [harmony, priority, finalizer, thread-static, terrain, asker-marker]
---

A garage roof that is really a hole in the terrain has an awkward property: the terrain height over it depends on
who is asking. A camera or a craft above the roof should be told "the roof"; a craft inside the garage should be
told "the floor". KSAStructures answers a postfix on `Celestial.GetTerrainHeightFromDirCce` differently
depending on a `[ThreadStatic]` "asker" that it sets in a `Priority.First` prefix on `Vehicle.PrepareWorker` and
clears in a `Priority.Last` finalizer, so the mark brackets everything else any mod does in that method.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by KSAStructures in
[Terrain height answered as roof, ramp or floor](../../../../mods/ksastructures/features/terrain-height-roof-override.md).

## Background: how the game does it

`Celestial.GetTerrainHeightFromDirCce` converts its direction to the body-fixed frame and calls
`GetTerrainHeightFromDirCcf`. The Bepu terrain patches sample the `Ccf` overload directly, so a patch on the `Cce`
overload changes what cameras, placement and wheel code see without changing physics collision, which needs the real
pit:

```csharp
// current/decomp/KSA/Celestial.cs L777-L781
public double GetTerrainHeightFromDirCce(double3 positionDirCce, bool accurate = true)
{
	double3 positionDirCcf = positionDirCce.Transform(GetCcf2Cce().Inverse());
	return GetTerrainHeightFromDirCcf(positionDirCcf, accurate);
}
```

Source: [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L777-L781)[^ksa-celestial]

`Universe.PrepareVehicleWorkers` calls `PrepareWorker` on every vehicle on the main thread, and `PrepareFromVehicle`
then copies the state for the solver:

```csharp
// current/decomp/KSA/Universe.cs L2141-L2149
public static void PrepareVehicleWorkers(SimStep simStep)
{
	_orphanVehicles.Clear();
	LookupCollection<Astronomical>.TypeFilter<Vehicle> enumerator = CurrentSystem.All.OfType<Vehicle>().GetEnumerator();
	while (enumerator.MoveNext())
	{
		Vehicle current = enumerator.Current;
		current.PrepareWorker(simStep);
		VehicleUpdateState vehicleUpdateState = current.TakeUpdateStateIfOrphaned();
```

Source: [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2141-L2149)[^ksa-universe]

`Vehicle.PrepareWorker(SimStep)` is `virtual`; `KittenEva` overrides it and calls `base.PrepareWorker`
([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2398)[^ksa-vehicle], [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L319-L325)[^ksa-kitteneva]), so a prefix on the `Vehicle` method sees kittens too.
Placement by latitude and longitude computes the start state immediately inside `TeleportToLocation` and only queues
the move:

```csharp
// current/decomp/KSA/Vehicle.cs L4219-L4233
public void TeleportToLocation(Celestial celestial, double lat, double lon)
{
	double3 double5 = new double3(_props.BoundingBoxAsmb.HalfWidth, _props.BoundingBoxAsmb.HalfHeight, _props.BoundingBoxAsmb.HalfLength);
	double3 boundsMinAsmb = MassToGeometryAsmb - double5;
	double3 boundsMaxAsmb = MassToGeometryAsmb + double5;
	InitialKinematicState initialKinematicStateForLocation = GetInitialKinematicStateForLocation(celestial, Universe.GetNextSimStep().NextTime, lat, lon, boundsMinAsmb, boundsMaxAsmb, double3.Zero, Orbit.OrbitLineColor);
	InputEvents.TeleportInputData data = new InputEvents.TeleportInputData
	{
		Vehicle = this,
		Orbit = initialKinematicStateForLocation.Orbit,
		Body2Cce = initialKinematicStateForLocation.Body2Cce,
		BodyRates = initialKinematicStateForLocation.BodyRates
	};
	InputEvents.TeleportInputBuffer.Add(data);
}
```

Source: [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L4219-L4233)

## Lifecycle and timing

| When | Hook | Thread | What |
|---|---|---|---|
| `frame:dispatch-solvers` | prefix and finalizer on `Vehicle.PrepareWorker` | main | `Universe.ExecuteNextVehicleSolvers` calls `PrepareVehicleWorkers`; the mod marks the asker |
| `frame:viewports` | postfix on `GetTerrainHeightFromDirCce` | main | Camera clamp inside `Camera.OnFrame` |
| `frame:celestials` | same postfix | main | `Program.OnFrameCelestials` reads the camera's terrain height |
| teleport | prefix and finalizer on `Vehicle.TeleportToLocation` | main | Lend a height for the synchronous start-state computation |

`[ThreadStatic]` is correct either way: if `PrepareWorker` ever runs on several threads, each sees only its own
vehicle. The cached tower ground height is a plain static, which assumes main-thread callers. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and
[Mutate simulation state safely relative to the physics step](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. **Declare the asker as `[ThreadStatic]`.** Outside the `PrepareWorker` window the asker is the main camera:

```csharp
// src/KSAStructures/Ksa/OfficeTerrain.cs L220-L223
// The craft whose worker the engine is preparing, on the thread preparing it. A mod that drives
// wheels does it from a prefix on that method, because nothing written anywhere else survives,
// so a terrain height asked for in between is asked on that craft's behalf.
[ThreadStatic] private static Vehicle? _stepping;
```

Source: [src/KSAStructures/Ksa/OfficeTerrain.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeTerrain.cs#L220-L223)[^ksastructures-officeterrain]

2. **Install a prefix at `Priority.First` and a finalizer at `Priority.Last`.** Other mods (wheel mods, for example)
   do their own work in prefixes on the same method. In Lib.Harmony 2.4.2 both prefixes and finalizers run in
   priority order from `First` to `Last` (checked with three patches registered in scrambled order), so the mark is
   set before every other prefix and cleared after every other finalizer. `GetMethod` with an explicit parameter list
   picks the right overload, and a missing method degrades with a warning:

```csharp
// src/KSAStructures/Ksa/OfficeTerrain.cs L233-L264
private static void HoldUpWheels()
{
    try
    {
        MethodInfo? worker = typeof(Vehicle).GetMethod(nameof(Vehicle.PrepareWorker), [typeof(SimStep)]);
        MethodInfo? height = typeof(Celestial).GetMethod(nameof(Celestial.GetTerrainHeightFromDirCce), [typeof(double3), typeof(bool)]);
        if (worker is null || height is null)
        {
            Log.Warn("office: no terrain height to answer for the roof; a car on it sits on its belly");
            return;
        }

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
        Log.Info("office: the garage roof holds up wheels that follow the terrain");
    }
    catch (Exception e)
    {
        Log.Warn($"office: the garage roof cannot hold up wheels ({e.GetBaseException().Message})");
    }
}
```

Source: [src/KSAStructures/Ksa/OfficeTerrain.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeTerrain.cs#L233-L264)

3. **Write the mark and the clear.** The prefix sets the asker (and, in the same prefix, moves lift riders because the
   engine copies the vehicle state for its worker straight after). The finalizer clears it. A finalizer clears the
   mark even if another mod's prefix or the original throws:

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

Source: [src/KSAStructures/Ksa/OfficeTerrain.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeTerrain.cs#L286-L296)

4. **Answer by asker in the postfix.** Kittens are skipped (they walk on colliders). A craft above the roof gets the roof;
   a camera must be 0.55 m clear of the roof to avoid being thrown upward through it; below it the floor is returned.
   The whole body is wrapped in an empty `catch` because it runs inside the camera's per-frame clamp:

```csharp
// src/KSAStructures/Ksa/OfficeTerrain.cs L304-L317, L342-L358
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
// ...
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

Source: [src/KSAStructures/Ksa/OfficeTerrain.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeTerrain.cs#L304-L358)

5. **Lend a value across a call with `__state`.** `TeleportToLocation` computes the start state from the terrain at
   once. The prefix sets the launch pad's `SurfaceHeight` to the roof depth and returns an `IDisposable` as `__state`;
   the finalizer disposes it, putting the value back even if the original throws:

```csharp
// src/KSAStructures/Ksa/OfficeTerrain.cs L266-L284
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

Source: [src/KSAStructures/Ksa/OfficeTerrain.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeTerrain.cs#L266-L284)

## Complete example

| File | Role |
|---|---|
| [src/KSAStructures/Ksa/OfficeTerrain.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeTerrain.cs#L218-L362) | Marker, patches, height postfix, lending |
| [src/KSAStructures/Ksa/CraftSpawner.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/CraftSpawner.cs#L41)[^ksastructures-craftspawner] | A caller that benefits from the teleport lending |

## Variations and gotchas

- **Never patch `GetTerrainHeightFromDirCcf` for this.** It feeds Bepu's terrain patches; changing it would lift
  the colliders off the pit.
- **The answer is not a pure function.** It depends on the asker, so code that caches terrain heights across askers can
  see a jump.
- **Priority is about bracketing.** The mark must be set before and cleared after every other patch on the same
  method; otherwise a prefix that runs earlier would ask while the mark is unset.
- **Teleport is deferred.** Lending only works because the height is computed inside the call; the move itself
  lands later through `InputEvents.TeleportInputBuffer`.
- **The postfix must not throw.** It runs inside the camera clamp and inside other mods' wheel code.

## See also

- [Restore what a prefix changed around one game call](run-cleanup-or-recover-from-exceptions-with-a-finalizer-1.md), [Swallow an exception and finish the method's remaining work](run-cleanup-or-recover-from-exceptions-with-a-finalizer-2.md), [Prefix with __state and a finalizer that restores an edited input](run-cleanup-or-recover-from-exceptions-with-a-finalizer-3.md), [Prefix, postfix and finalizer as a transaction around loads](run-cleanup-or-recover-from-exceptions-with-a-finalizer-4.md), [ThreadStatic override set in try/finally around the mod's own submission](run-cleanup-or-recover-from-exceptions-with-a-finalizer-5.md), [Thread-static construction context scoping a transpiler and a prefix](run-cleanup-or-recover-from-exceptions-with-a-finalizer-6.md)
- [Harmony finalizers, cleanup and scoped overrides](overview.md)
- [Terrain height answered as roof, ramp or floor](../../../../mods/ksastructures/features/terrain-height-roof-override.md)
- [Coexisting with other mods that patch the same method](../coexist-with-other-mods-patching-the-same-method/overview.md)
- Related task: override-terrain-height-for-roofs-and-platforms

[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-kitteneva]: KSA.KittenEva (decompiled, build 2026.10.10.5554)
[^ksastructures-officeterrain]: KSAStructures/src/KSAStructures/Ksa/OfficeTerrain.cs
[^ksastructures-craftspawner]: KSAStructures/src/KSAStructures/Ksa/CraftSpawner.cs
