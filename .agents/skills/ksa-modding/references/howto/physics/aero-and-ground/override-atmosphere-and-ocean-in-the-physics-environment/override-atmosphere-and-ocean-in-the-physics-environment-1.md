---
type: How-To
title: Prefix the density and pressure functions and move the private boundary height
description: Replace a body's exponential atmosphere with a measured density and pressure profile for drag, engines and everything else that reads it, by prefixing the two lookup functions and rewriting the private boundary height after the template loads
category: physics
group: aero-and-ground
topic: override-atmosphere-and-ocean-in-the-physics-environment
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/realatmospheres/features/measured-flight-atmospheres]
techniques: [harmony-prefix, harmony-postfix, reflection]
hooks:
  - starmap StarMapBeforeMain
  - harmony-postfix KSA.AstronomicalTemplate.OnDataLoad
  - harmony-prefix KSA.PhysicalAtmosphereReference.GetAtmosphericDensityAtAltitude
  - harmony-prefix KSA.PhysicalAtmosphereReference.GetAtmosphericPressureAtAltitude
  - reflection KSA.PhysicalAtmosphereReference._height
lifecycle: [startup:starmap-before-main, startup:asset-load, physics:vehicle-solver]
game_types:
  - KSA.PhysicalAtmosphereReference
  - KSA.AstronomicalTemplate
  - KSA.AtmosphereReference
  - KSA.DistanceReference
  - KSA.PhysicsEnvironment
tags: [physics, atmosphere, density, pressure, drag, harmony, reflection, worker-thread]
---

Outcome: for the bodies you choose, drag, buoyancy, engine back-pressure, plume effects and the atmosphere's edge follow a
density and pressure profile you supply (a table, a formula, a measured curve) instead of KSA's one-exponent model.
Prerequisites: StarMap and Harmony. Demonstrated by RealAtmospheres, which ships measured tables for Earth, Venus, Mars,
Titan, Jupiter, Saturn, Uranus, Neptune, Triton and Pluto and does all KSA access by reflection, so it only references
StarMap.API and Harmony.

## Background: how the game does it

Everything physical about the air goes through two methods on `PhysicalAtmosphereReference`, and the atmosphere ends
where the private `_height` says. The exponential has no cut-off of its own, so the boundary is what limits it:[^ksa-physicalatmospherereference]

```csharp
// current/decomp/KSA/PhysicalAtmosphereReference.cs L43-L48
	public void CalculateBoundaryHeight()
	{
		double val = (0.0 - ScaleHeight.InMeters()) * Math.Log(1E-09 / (double)SeaLevelDensity);
		double val2 = (0.0 - ScaleHeight.InMeters()) * Math.Log(0.0001 / (double)SeaLevelPressure);
		_height = DistanceReference.FromMeters(Math.Max(val, val2));
	}
```

```csharp
// current/decomp/KSA/PhysicalAtmosphereReference.cs L80-L88
	public double GetAtmosphericPressureAtAltitude(double altitudeInMeters)
	{
		return (double)SeaLevelPressure * Math.Exp((0.0 - altitudeInMeters) / ScaleHeight.InMeters());
	}

	public double GetAtmosphericDensityAtAltitude(double altitudeInMeters)
	{
		return (double)SeaLevelDensity * Math.Exp((0.0 - altitudeInMeters) / ScaleHeight.InMeters());
	}
```

The vehicle solver reads both functions in one place, and only below `Physical.Height`:[^ksa-physicsenvironment]

```csharp
// current/decomp/KSA/PhysicsEnvironment.cs L89-L101
		double meanRadius = ClosestParent.MeanRadius;
		double num2 = Math.Max(num - meanRadius, 0.0);
		AtmosphereReference atmosphereReference = ClosestParent.GetAtmosphereReference();
		if (atmosphereReference != null && num2 < (double)atmosphereReference.Physical.Height)
		{
			AtmosphericPressure = (float)atmosphereReference.Physical.GetAtmosphericPressureAtAltitude(num2);
			AtmosphericDensity = (float)atmosphereReference.Physical.GetAtmosphericDensityAtAltitude(num2);
		}
		else
		{
			AtmosphericPressure = 0f;
			AtmosphericDensity = 0f;
		}
```

Because the boundary is also used to size the physics regions (`IParentBody.GetAtmosphereRadius` is mean radius plus
`Physical.Height`; `AtmosphericBody.GetNearSurfaceRadius` adds 1 m), moving it changes where a coasting vehicle is forced
into full physics, not only where drag starts.[^ksa-iparentbody][^ksa-atmosphericbody]

## Lifecycle and timing

- **Install: `startup:starmap-before-main`.** The table is attached while a body's template loads, so the patches must exist
  before `ModLibrary.LoadAll` deserializes the selected system. A patch applied at `StarMapAllModsLoaded` would arrive after
  `AstronomicalTemplate.OnDataLoad` has already run and would never see the template. RealAtmospheres installs from its
  `[StarMapBeforeMain]` method.[^ra-modmain]
- **Attach: `startup:asset-load`.** `AstronomicalTemplate.OnDataLoad` calls `AtmosphereReference?.OnDataLoad(mod)`, which
  computes the stock boundary; the postfix then overwrites it.[^ksa-astronomicaltemplate] For celestial bodies the base
  method is only reached outside preload, because `CelestialTemplate.OnDataLoad` returns early while `mod.Preload` is
  set,[^ksa-celestialtemplate] so the postfix never sees the preload copies. The registry is a `ConditionalWeakTable`, whose
  `AddOrUpdate` and `TryGetValue` are thread-safe, because worker threads read it as soon as the first physics step runs
  (see the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for the loading
  sequence).
- **Lookups: `physics:vehicle-solver`, and the main thread.** `RecomputePositionalValues` is called per substep from
  `PhysicsStates` and from `PhysicsBubble.UpdateFlotationRadius`, on worker threads and for several vehicles at once
  (`Universe.ExecuteNextVehicleSolvers` queues the step after `Vehicle.PrepareWorker` has run per vehicle on the main
  thread).[^ksa-physicsstates][^ksa-physicsbubble] The same functions also answer main-thread callers (audio, exhaust
  effects). The prefixes therefore allocate nothing, take no lock and only read a table that was complete before the
  first physics step.

## Steps

1. **Produce the profile.** RealAtmospheres keeps, per body, altitude nodes (metres above the mean radius), `ln` density,
   `ln` pressure and the boundary altitude where the profile crosses the game's `1e-9 kg/m3` and `1e-4 Pa` floors, generated
   offline into a C# file. Earth's entry starts like this (boundary 194.119 km; 0.20294 is `ln 1.225`):[^ra-generated]

```csharp
// AtmosphereTables.Generated.cs L9-L23
    internal static readonly FlightTable[] Flight =
    {
        new("Earth", 194119.0,
            "1", "1.225", "8.435",
            new double[] {
            0.0, 5000.0, 9000.0, 11000.0, 26000.0, 35000.0, 41000.0, 47000.0,
            52000.0, 59000.0, 66000.0, 72000.0, 79000.0, 85000.0, 93000.0, 100000.0,
            105000.0, 109000.0, 112000.0, 115000.0, 118000.0, 120000.0, 125000.0, 130000.0,
            135000.0, 140000.0, 145000.0, 150000.0, 160000.0, 170000.0, 185000.0, 200000.0,
            220000.0, 240000.0, 265000.0, 295000.0, 335000.0, 385000.0, 440000.0, 500000.0,
            540000.0, 580000.0, 620000.0, 660000.0, 700000.0, 740000.0, 780000.0, 820000.0,
            860000.0, 920000.0, 1000000.0
            },
            new double[] {
            0.20294, -0.30594, -0.76129, -1.00841, -3.37391, -4.77207, -5.66761, -6.50472,
```

   Any source works; the only contract is a pure function of altitude, and a boundary where it is effectively zero.

2. **Make the lookup allocation-free.** Binary-search the node, interpolate linearly in the logarithm and exponentiate; past
   either end the nearest segment continues:[^ra-atmospheretables]

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

3. **Resolve the members by name and refuse on any mismatch.** Harmony binds prefix arguments by name, so check that the
   functions' parameter is still called `altitudeInMeters`. If anything is missing, log and patch nothing: flight stays
   stock. Wrap the patching in `try`/`catch` for the same reason:[^ra-flightatmospheres]

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

4. **Register the table per atmosphere object and move the boundary.** The postfix on `AstronomicalTemplate.OnDataLoad`
   matches the template's `Id`, reaches `AtmosphereReference.Physical`, stores the table in a `ConditionalWeakTable` keyed by
   that `PhysicalAtmosphereReference`, and writes the private `_height` through `DistanceReference.FromMeters`:

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

   Expect `Earth: measured flight atmosphere, boundary 194 km` (and one line per body) in the mod's log.

5. **Answer the lookups; return `true` for every other atmosphere.** The prefix returns `false` and sets `__result` for a
   registered object, and lets the original run otherwise. The airless-body path costs one failed weak-table lookup:

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

6. **Install from `StarMapBeforeMain`, and keep the install failure-tolerant.** RealAtmospheres calls
   `FlightAtmospheres.Install(harmony)` at the end of its `Install()` (ModMain L144), itself called from the
   `[StarMapBeforeMain]` method inside a `try`/`catch` so a failure leaves the game stock.

## Complete example

| File | Role |
|---|---|
| [FlightAtmospheres.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/FlightAtmospheres.cs) | Reflection lookups, shape check, the postfix and both prefixes |
| [AtmosphereTables.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/AtmosphereTables.cs) | `FlightTable` interpolation and `ForBody` |
| [AtmosphereTables.Generated.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/AtmosphereTables.Generated.cs) | The ten generated tables |
| [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L21-L35) | `[StarMapBeforeMain]` entry; line 144 installs the flight atmospheres |

## Variations and gotchas

- **Install early.** A patch added at `StarMapAllModsLoaded` misses the template load and the table is never registered.
- **Matching by `Id`.** Another system that reuses a body id gets the same table; a body renamed by another mod loses it.
- **Speed of sound.** With independent pressure and density profiles `p / rho` varies with altitude, so a mod that derives
  Mach from them (see [KSAero in the aero topic](../replace-the-stock-aerodynamic-force-model/replace-the-stock-aerodynamic-force-model-1.md))
  now sees an altitude-dependent speed of sound instead of a per-body constant.
- **The boundary moves the physics regions.** A measured edge of 996 km above Titan makes freefall inside that radius run
  full physics, with the cost that implies at high time warp.
- **Hot path.** The prefixes run for every vehicle every substep and for airless bodies too; never allocate or lock in them.
- **Fixed thresholds.** The generated boundaries assume the game's `1e-9` and `1e-4` floors; if a game update changes
  `MIN_DENSITY` or `MIN_PRESSURE` they are stale until regenerated.
- **Silent degrade.** A renamed member makes the install log a warning and leave flight stock; only the log says so.
- **Drag only sees it below the game's `Physical.Height`.** `RecomputePositionalValues` zeroes density and pressure above the
  boundary regardless of what the table would return, so the boundary and the table must agree.

## See also

- [Overview and comparison](overview.md)
- [Postfix PhysicsEnvironment.RecomputePositionalValues on the worker](override-atmosphere-and-ocean-in-the-physics-environment-2.md): change the struct instead of the functions
- Mod feature: [Measured flight atmospheres](../../../../mods/realatmospheres/features/measured-flight-atmospheres.md)
- [Write worker-thread-safe solver patches](../../solver/write-worker-thread-safe-solver-patches/write-worker-thread-safe-solver-patches-2.md): read-only tables filled before physics starts
- [Apply air drag, gravity and aiming to a mod-simulated body](../../mod-simulation/apply-drag-and-gravity-to-a-mod-simulated-body/apply-drag-and-gravity-to-a-mod-simulated-body-1.md)

[^ksa-physicalatmospherereference]: KSA.PhysicalAtmosphereReference (decompiled, build 2026.10.10.5554)
[^ksa-physicsenvironment]: KSA.PhysicsEnvironment (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-atmosphericbody]: KSA.AtmosphericBody (decompiled, build 2026.10.10.5554)
[^ra-modmain]: RealAtmospheres: ModMain.cs
[^ksa-astronomicaltemplate]: KSA.AstronomicalTemplate (decompiled, build 2026.10.10.5554)
[^ksa-celestialtemplate]: KSA.CelestialTemplate (decompiled, build 2026.10.10.5554)
[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^ra-generated]: RealAtmospheres: AtmosphereTables.Generated.cs
[^ra-atmospheretables]: RealAtmospheres: AtmosphereTables.cs
[^ra-flightatmospheres]: RealAtmospheres: FlightAtmospheres.cs
