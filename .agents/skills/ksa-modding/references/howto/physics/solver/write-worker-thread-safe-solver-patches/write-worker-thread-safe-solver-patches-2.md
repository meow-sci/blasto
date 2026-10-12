---
type: How-To
title: Read-only tables filled on the main thread before physics starts
description: Give worker-thread prefixes and postfixes lock-free data by filling a ConditionalWeakTable at load time, or by swapping a whole array through a volatile field when the data changes at runtime
category: physics
group: solver
topic: write-worker-thread-safe-solver-patches
approach: 2
difficulty: intermediate
prerequisites: [harmony]
demonstrated_by:
  - mods/realatmospheres/features/measured-flight-atmospheres
  - mods/ksastructures/features/dry-volume-below-sea-level
techniques: [reflection, harmony-prefix, harmony-postfix]
hooks:
  - harmony-postfix KSA.AstronomicalTemplate.OnDataLoad
  - harmony-prefix KSA.PhysicalAtmosphereReference.GetAtmosphericDensityAtAltitude
  - harmony-postfix KSA.PhysicsEnvironment.RecomputePositionalValues
lifecycle: [startup:asset-load, physics:vehicle-solver, frame:dispatch-solvers]
game_types:
  - KSA.PhysicalAtmosphereReference
  - KSA.AstronomicalTemplate
  - KSA.PhysicsEnvironment
  - KSA.PhysicsBubble
  - KSA.VehicleUpdateTask
tags: [physics, worker-thread, conditionalweaktable, volatile, atmosphere, immutable-data, reflection]
---

Outcome: patches that run on vehicle worker threads, per substep, read mod data with no lock and no
allocation, because the data is built on the main thread before the first physics step (or replaced
wholesale) and never mutated in place. Prerequisite: Harmony. RealAtmospheres fills tables when the game
loads its body templates; KSAStructures keeps a small array of volumes that it swaps through a `volatile`
field when a new volume is defined.

## Background: how the game does it

Atmosphere queries are made by the physics step. `PhysicsEnvironment.RecomputePositionalValues`
[current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L83-L102) fills each vehicle's `AtmosphericPressure` and `AtmosphericDensity`
from the parent body's `PhysicalAtmosphereReference`, and it is called from `PhysicsStates` and
`PhysicsBubble` while a vehicle is integrated.[^ksa-physicsenvironment] The density and pressure functions are
simple exponentials over one scale height [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L80-L88). They are also
called from the main thread by audio and exhaust code, so a prefix on them answers both worlds.

```csharp
// current/decomp/KSA/PhysicalAtmosphereReference.cs L43-L48
	public void CalculateBoundaryHeight()
	{
		double val = (0.0 - ScaleHeight.InMeters()) * Math.Log(1E-09 / (double)SeaLevelDensity);
		double val2 = (0.0 - ScaleHeight.InMeters()) * Math.Log(0.0001 / (double)SeaLevelPressure);
		_height = DistanceReference.FromMeters(Math.Max(val, val2));
	}
```

## Lifecycle and timing

| Moment | Thread | What happens |
|---|---|---|
| `startup:asset-load` (`ModLibrary.LoadAll`, `XmlLoader.Load<T>` calls `OnDataLoad`) | main | A postfix on `AstronomicalTemplate.OnDataLoad` registers each body's table. It sees only non-preload templates, because `CelestialTemplate.OnDataLoad` returns early while `mod.Preload` is set |
| First physics step onwards | vehicle workers | Prefixes on the two lookup functions read the table; run per substep, concurrently, for every body including airless ones |
| Main-thread audio and exhaust | main | The same prefixes answer outside the step |
| Runtime changes (KSAStructures volumes) | main, once per definition | A new array is swapped into a `volatile` field; workers see the old or the new array, never a half-edited one |

The read side relies on two facts: `ConditionalWeakTable.TryGetValue` is safe for concurrent readers, and a
published object reference is atomic. Everything the worker touches is built before the reference is
published.

## Steps

1. Resolve every game member by reflection and verify its shape before patching anything. RealAtmospheres
   looks types up by name, checks that the functions' parameter is still called `altitudeInMeters` (Harmony
   binds prefix arguments by name) and logs and installs nothing if one piece is missing:

```csharp
// FlightAtmospheres.cs L21-L54
    public static void Install(Harmony harmony)
    {
        Type? template = AccessTools.TypeByName("KSA.AstronomicalTemplate");
        Type? atmosphere = AccessTools.TypeByName("KSA.AtmosphereReference");
        Type? physical = AccessTools.TypeByName("KSA.PhysicalAtmosphereReference");
        Type? distance = AccessTools.TypeByName("KSA.DistanceReference");
        MethodInfo? loaded = template == null ? null : AccessTools.DeclaredMethod(template, "OnDataLoad");
        MethodInfo? density = physical == null ? null : AccessTools.Method(physical, "GetAtmosphericDensityAtAltitude", new[] { typeof(double) });
        MethodInfo? pressure = physical == null ? null : AccessTools.Method(physical, "GetAtmosphericPressureAtAltitude", new[] { typeof(double) });
        _id = template == null ? null : AccessTools.Property(template, "Id");
        _atmosphere = template == null ? null : AccessTools.Field(template, "AtmosphereReference");
        _physical = atmosphere == null ? null : AccessTools.Field(atmosphere, "Physical");
        _height = physical == null ? null : AccessTools.Field(physical, "_height");
        _fromMeters = distance == null ? null : AccessTools.Method(distance, "FromMeters", new[] { typeof(double) });

        if (loaded == null || density == null || pressure == null || _id == null || _atmosphere == null
            || _physical == null || _height == null || _fromMeters == null
            || density.GetParameters()[0].Name != "altitudeInMeters" || pressure.GetParameters()[0].Name != "altitudeInMeters")
        {
            ShadowBuilder.Log("WARN: the game's flight atmosphere has changed; measured flight atmospheres not installed");
            return;
        }
        try
        {
            harmony.Patch(loaded, postfix: new HarmonyMethod(typeof(FlightAtmospheres), nameof(TemplateLoadedPostfix)));
            harmony.Patch(density, prefix: new HarmonyMethod(typeof(FlightAtmospheres), nameof(DensityPrefix)));
            harmony.Patch(pressure, prefix: new HarmonyMethod(typeof(FlightAtmospheres), nameof(PressurePrefix)));
            ShadowBuilder.Log($"measured flight atmospheres ready for {AtmosphereTables.Flight.Length} bodies");
        }
        catch (Exception e)
        {
            ShadowBuilder.Log($"WARN: could not patch the flight atmosphere ({e.GetType().Name}); flight stays stock");
        }
    }
```

2. Key a `ConditionalWeakTable` on the game's own reference object (here the body's
   `PhysicalAtmosphereReference`) and fill it from a postfix that runs while the game loads, on the main
   thread. The table drops an entry when the game object dies, so a reloaded system leaks nothing:

```csharp
// FlightAtmospheres.cs L58-L75
    public static void TemplateLoadedPostfix(object __instance)
    {
        try
        {
            FlightTable? table = AtmosphereTables.ForBody(_id!.GetValue(__instance) as string);
            object? atmosphere = table == null ? null : _atmosphere!.GetValue(__instance);
            object? physical = atmosphere == null ? null : _physical!.GetValue(atmosphere);
            if (physical == null)
                return;
            Registered.AddOrUpdate(physical, table!);
            _height!.SetValue(physical, _fromMeters!.Invoke(null, new object[] { table!.BoundaryM }));
            ShadowBuilder.Log($"{table.Body}: measured flight atmosphere, boundary {table.BoundaryM / 1000.0:F0} km");
        }
        catch (Exception e)
        {
            ShadowBuilder.Log($"WARN: flight atmosphere not attached ({e.GetType().Name})");
        }
    }
```

3. Answer on the worker with an allocation-free prefix that returns `false` to skip the original only for
   registered objects. Any other atmosphere takes the `return true` branch after one failed lookup:

```csharp
// FlightAtmospheres.cs L77-L91
    public static bool DensityPrefix(object __instance, double altitudeInMeters, ref double __result)
    {
        if (!Registered.TryGetValue(__instance, out FlightTable? table))
            return true;
        __result = table.Density(altitudeInMeters);
        return false;
    }

    public static bool PressurePrefix(object __instance, double altitudeInMeters, ref double __result)
    {
        if (!Registered.TryGetValue(__instance, out FlightTable? table))
            return true;
        __result = table.Pressure(altitudeInMeters);
        return false;
    }
```

   The table itself is immutable: binary search over node arrays and linear interpolation in the logarithm.

```csharp
// AtmosphereTables.cs L26-L38
    public double Density(double altitudeM) => Math.Exp(Interpolate(_lnDensity, altitudeM));

    public double Pressure(double altitudeM) => Math.Exp(Interpolate(_lnPressure, altitudeM));

    private double Interpolate(double[] values, double altitudeM)
    {
        int i = Array.BinarySearch(_altitude, altitudeM);
        if (i >= 0)
            return values[i];
        int a = Math.Clamp(~i - 1, 0, _altitude.Length - 2);
        double f = (altitudeM - _altitude[a]) / (_altitude[a + 1] - _altitude[a]);
        return values[a] + f * (values[a + 1] - values[a]);
    }
```

4. When the data changes at runtime, never edit in place. KSAStructures keeps its volumes in a `volatile`
   array and replaces the whole array on every change. The main-thread `Define` builds a new array; the
   worker only ever reads the reference once per call:

```csharp
// src/KSAStructures/Ksa/OfficeDry.cs L26-L39
    private static volatile Volume[] _volumes = [];
    private static volatile bool _mainCameraDry;

    public static bool Defined => _volumes.Length > 0;

    /// <summary>Adds a column about a direction from the body's centre, between two radii.</summary>
    public static void Define(Celestial body, double3 axisCcf, double radiusMetres, double minRadius, double maxRadius)
    {
        _volumes = [.. _volumes, new Volume
        {
            Body = body, AxisCcf = axisCcf.Normalized(), RadiusMetres = radiusMetres, MinRadius = minRadius, MaxRadius = maxRadius,
        }];
        Log.Info($"office: dry volume defined, {maxRadius - minRadius:F0} m deep and {radiusMetres * 2.0:F0} m across");
    }
```

5. Write the worker side as a postfix that allocates nothing, takes no lock and swallows every exception.
   `PhysicsEnvironment` is a struct, so the postfix takes `ref PhysicsEnvironment __instance`:

```csharp
// src/KSAStructures/Ksa/OfficeDry.cs L90-L123
    private static bool Inside(IParentBody body, double3 positionCcf)
    {
        Volume[] volumes = _volumes;
        if (volumes.Length == 0) return false;

        double radius = positionCcf.Length();
        foreach (Volume volume in volumes)
        {
            if (!ReferenceEquals(body, volume.Body) || radius < volume.MinRadius || radius > volume.MaxRadius) continue;

            double along = double3.Dot(positionCcf, volume.AxisCcf);
            if ((radius * radius) - (along * along) <= volume.RadiusMetres * volume.RadiusMetres && along > 0.0) return true;
        }

        return false;
    }

    // Worker threads, several times a sub-step: nothing here may allocate or throw. A craft already
    // floating is placed at the ocean's radius by the engine, so it is left its ocean.
    private static void AfterPositional(ref PhysicsEnvironment __instance, double3 positionClosestParentCcf, in VehicleProperties props)
    {
        try
        {
            if (__instance.OceanRadius <= 0.0 || props.Situation == Situation.Floating) return;
            if (!Inside(__instance.ClosestParent, positionClosestParentCcf)) return;

            __instance.OceanRadius = 0.0;
            __instance.OceanVolume = 0.0;
            __instance.OceanSurfaceArea = 0.0;
        }
        catch
        {
        }
    }
```

   The `Floating` skip matters: a floating craft is positioned from `OceanRadius` by `UpdateFlotationRadius`, so
   a zeroed ocean would place it at the planet's centre.

6. Install from `[StarMapAllModsLoaded]` for the postfix and prefixes. In the log, look for the install line
   (`measured flight atmospheres ready for N bodies`); a changed game shows `WARN: ... not installed` and the
   game stays stock.

## Complete example

| Mod | Files |
|---|---|
| RealAtmospheres | [FlightAtmospheres.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/FlightAtmospheres.cs#L14-L91), [AtmosphereTables.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/AtmosphereTables.cs#L6-L49) |
| KSAStructures | [src/KSAStructures/Ksa/OfficeDry.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeDry.cs#L26-L145) |

## Variations and gotchas

- Fill the table from a load-time postfix, not lazily on the worker: a lazy build races other workers and
  allocates inside the step.
- `ConditionalWeakTable` takes a write lock on `AddOrUpdate` and `Remove`; only do those on the main thread.
  AFC avoids even a `TryGetValue` + `Remove` pair when the key is absent for the same reason.
- Matching is by template `Id`, so a body that another mod renames loses its table, and a same-named body in
  another system gets one.
- A boundary height set by reflection on `_height` is read wherever `Physical.Height` is used, including the
  physics radius that forces full physics for freefall from higher up (996 km above Titan).
- These prefixes sit on functions every vehicle calls every substep for every body; keep them cheap on the
  miss path.
- Renderer prefixes that read the same data (`OceanRenderer.Render`) are on the main thread and may read the
  same `volatile` array; only the workers force the lock-free design.
- Silent degrade: if the game renames a member the mod stays stock and only the log says so.

## See also

- [Overview and comparison](overview.md)
- [Approach 1: pure arguments only](write-worker-thread-safe-solver-patches-1.md), [approach 3: concurrent registries](write-worker-thread-safe-solver-patches-3.md)
- Mod features: [RealAtmospheres measured flight atmospheres](../../../../mods/realatmospheres/features/measured-flight-atmospheres.md), [KSAStructures dry volume below sea level](../../../../mods/ksastructures/features/dry-volume-below-sea-level.md)
- The topic override-atmosphere-and-ocean-in-the-physics-environment (another batch) for the atmosphere side of this

[^ksa-physicsenvironment]: KSA.PhysicsEnvironment (decompiled, build 2026.10.10.5554)
