---
type: Mod Feature
title: Measured flight atmospheres
description: Prefixes on PhysicalAtmosphereReference's density and pressure functions return measured profiles for ten bodies, attached per atmosphere in a ConditionalWeakTable at load, with the boundary height moved to match
mod: realatmospheres
category: physics
techniques: [harmony-prefix, harmony-postfix, reflection, custom-physics]
hooks:
  - starmap StarMapBeforeMain
  - harmony-postfix KSA.AstronomicalTemplate.OnDataLoad
  - harmony-prefix KSA.PhysicalAtmosphereReference.GetAtmosphericDensityAtAltitude
  - harmony-prefix KSA.PhysicalAtmosphereReference.GetAtmosphericPressureAtAltitude
  - reflection KSA.PhysicalAtmosphereReference._height
lifecycle: [startup:asset-load, physics:vehicle-solver, frame:viewports, frame:pre-render, frame:post-render]
game_types:
  - KSA.PhysicalAtmosphereReference
  - KSA.AtmosphereReference
  - KSA.AstronomicalTemplate
  - KSA.CelestialTemplate
  - KSA.DistanceReference
  - KSA.PhysicsEnvironment
  - KSA.PhysicsStates
  - KSA.PhysicsBubble
  - KSA.VehicleUpdateState
  - KSA.Rocket
  - KSA.Vehicle
  - KSA.AtmosphericBody
  - KSA.GameViewport
  - KSA.Program
tags:
  - physics
  - atmosphere
  - drag
  - back-pressure
  - harmony
  - prefix
  - postfix
  - reflection
  - conditional-weak-table
---

KSA's flight model gives every body one isothermal exponential: density and pressure are the
sea-level value times `exp(-h / H)` with one scale height for both
([current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L80-L88)).[^ksa-physicalatmospherereference]
Real Atmospheres replaces those two functions, for Earth, Venus, Mars, Titan, Jupiter, Saturn, Uranus,
Neptune, Triton and Pluto, with tables built offline from probe and reference-atmosphere data, so
drag, engine back-pressure and everything else that reads the atmosphere see measured values, and the
atmosphere ends where the measured profile says it does.[^ra-flightatmospheres]

## How it works

1. **Tables.** `AtmosphereTables.Flight` is generated C#: per body, altitude nodes (m above the mean
   radius; the 1-bar level on the giants), `ln` density and `ln` pressure, the boundary altitude, and
   the 0 km values for the XML ([AtmosphereTables.Generated.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/AtmosphereTables.Generated.cs#L1-L39)).
   `FlightTable.Density`/`Pressure` binary-search the node and interpolate linearly in the logarithm;
   past either end the nearest segment continues ([AtmosphereTables.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/AtmosphereTables.cs#L6-L39)).
   See [Measured-atmosphere code generator](measured-atmosphere-generator.md) for how they are made.
2. **Shape check before patching.** `Install` resolves `AstronomicalTemplate.OnDataLoad`, both
   functions, `AstronomicalTemplate.Id`, the `AtmosphereReference` and `Physical` fields, the private
   `_height`, and `DistanceReference.FromMeters`, and also checks that the functions' parameter is
   still named `altitudeInMeters` (Harmony binds prefix arguments by name). If anything is missing it
   logs and patches nothing ([FlightAtmospheres.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/FlightAtmospheres.cs#L21-L54)).
3. **Attach on load.** A postfix on `AstronomicalTemplate.OnDataLoad` looks up the table by the
   template's `Id`, takes `template.AtmosphereReference.Physical`, registers the table for that object
   in a `ConditionalWeakTable`, and sets `_height` to `DistanceReference.FromMeters(table.BoundaryM)`
   ([FlightAtmospheres.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/FlightAtmospheres.cs#L58-L75)). The stock `OnDataLoad` of
   `AtmosphereReference` has already run inside the method and computed the stock boundary with
   `CalculateBoundaryHeight`; the postfix overwrites it afterwards
   ([current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L35-L48)).
4. **Answer the lookups.** Prefixes on `GetAtmosphericDensityAtAltitude` and
   `GetAtmosphericPressureAtAltitude` return the table's value and skip the original for registered
   objects; any other atmosphere runs stock ([FlightAtmospheres.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/FlightAtmospheres.cs#L77-L91)).
5. **Consistent fallback.** The XML copy also gets each body's `Physical` block set to the table's
   0 km pressure, density and a scale height from the 0 km temperature
   ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L366-L395)), so UI readouts of the XML values agree and, if
   the patch cannot install, the stock exponential at least starts from the measured surface values.

Matching is by template `Id`, so "a same-named body elsewhere gets the table"
([FlightAtmospheres.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/FlightAtmospheres.cs#L7-L13)). The boundaries the generator wrote, in km:
Earth 194, Venus 143, Mars 134, Titan 996, Pluto 634, Jupiter 1020, Saturn 1329, Uranus 2023,
Neptune 1710, Triton 349 ([AtmosphereTables.Generated.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/AtmosphereTables.Generated.cs#L11-L248)).

### What reads the replaced functions

| Reader | What it drives | Decompiled source |
|---|---|---|
| `PhysicsEnvironment.RecomputePositionalValues` | Per-vehicle `AtmosphericPressure` and `AtmosphericDensity`, zeroed above `Physical.Height` | [current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L83-L102) |
| `PhysicsStates.ComputeDerivatives` | Atmospheric drag (`ComputeDrag`) and buoyancy from `Environment.AtmosphericDensity`, per substep; chute forces read the same field | [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1007-L1015) |
| `Rocket.UpdateRockets`, from `VehicleUpdateState.UpdateModules` in the vehicle step | Engine state (back-pressure) from `Environment.AtmosphericPressure` | [current/decomp/KSA/Rocket.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Rocket.cs#L181), [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L381-L398) |
| `PhysicalAtmosphereReference.GetDynamicPressure`, `GetAtmosphericPressure(Camera)` | Main-thread audio: the IVA blend in `GameViewport.OnFrame`, the wind sound and spatial audio in `GameAudio.UpdateAudio` (`PostRender`), volumetric exhaust | [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L50-L78), [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L129-L146), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2362) |
| `Vehicle.UpdatePlumeTrailEmitters`, `AddVolumetricExhaustInstances`; vehicle region | `isInsideAtmosphere` and air density for exhaust effects, submitted from `Program.OnPreRender`; the vehicle region test uses `Physical.Height` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L5705-L5712), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L5725-L5731), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2382-L2399), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3100-L3112) |
| `IParentBody.GetAtmosphereRadius` | Mean radius plus `Physical.Height`, stored as `PhysicsEnvironment.AtmosphereRadius`; `PhysicsBubble.FreefallNeedsFullPhysics` forces full physics for a vehicle inside or entering it | [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L60-L64), [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1123-L1130) |
| `AtmosphericBody.GetNearSurfaceRadius` | Mean radius plus `Physical.Height` plus 1 m, stored as `PhysicsEnvironment.PhysicsRadius`; also read by orbit and patched-conic periapsis tests | [current/decomp/KSA/AtmosphericBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphericBody.cs#L20-L28), [current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L39-L43) |

## Lifecycle and timing

- **`startup:asset-load` (attach).** `OnDataLoad` runs when `XmlLoader.Load<T>` deserializes the
  library and the selected system template in `ModLibrary.LoadAll`, on the main thread
  ([current/decomp/KSA/XmlLoader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlLoader.cs#L16)). For celestial bodies
  the call to the patched base method only happens outside preload, because
  `CelestialTemplate.OnDataLoad` returns early while `mod.Preload` is set
  ([current/decomp/KSA/CelestialTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialTemplate.cs#L50-L54)).
  So the postfix never sees the preload copies of celestial templates, and a template that is dropped
  later simply falls out of the weak table.
- **`physics:vehicle-solver` (lookups).** `RecomputePositionalValues` is called from
  `PhysicsStates` and `PhysicsBubble` while a vehicle is integrated
  ([current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L533),
  [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L2633)), so the
  prefixes run on worker threads, per substep, concurrently. They allocate nothing and only read a
  `ConditionalWeakTable` filled before the first physics step; its `TryGetValue` is thread-safe.
- **Main thread (audio, effects).** The same prefixes answer main-thread callers outside the
  physics step. These are the IVA audio blend in `GameViewport.OnFrame` (`frame:viewports`), the
  exhaust and plume-trail submission in `Program.OnPreRender` (`frame:pre-render`), and the wind and
  spatial audio in `GameAudio.UpdateAudio`, which `PostRender` calls (`frame:post-render`). Flight HUD
  values mostly come from the vehicle's `PhysicsEnvironment` fields, which the worker filled.
- **The boundary moves the physics regions.** `Physical.Height` also sets the atmosphere radius and
  the near-surface (physics) radius of every `AtmosphericBody`. A higher measured boundary therefore
  makes the vehicle step force full physics for freefall from higher up, for example 996 km above
  Titan. The radii are copied into a vehicle's `PhysicsEnvironment` when its parent body is set, long
  after the postfix has run.
- Boundary height is read wherever `Physical.Height` is used; the postfix sets it once and nothing in
  the game recomputes the physical boundary after load (only the visual reference has editor-driven
  recalculation).

## Key code

| Path | Role |
|---|---|
| [FlightAtmospheres.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/FlightAtmospheres.cs#L21-L54) | Reflection lookups, shape check, three patches |
| [FlightAtmospheres.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/FlightAtmospheres.cs#L58-L91) | Postfix (register table, move boundary) and the two prefixes |
| [AtmosphereTables.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/AtmosphereTables.cs#L6-L49) | `FlightTable` log-linear interpolation, `ForBody` |
| [AtmosphereTables.Generated.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/AtmosphereTables.Generated.cs#L9-L268) | The ten generated tables |
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L366-L395) | Write the 0 km values into each `Physical` block |

## Snippets

```csharp
// FlightAtmospheres.cs L58-L91
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

```csharp
// current/decomp/KSA/PhysicalAtmosphereReference.cs L43-L48
	public void CalculateBoundaryHeight()
	{
		double val = (0.0 - ScaleHeight.InMeters()) * Math.Log(1E-09 / (double)SeaLevelDensity);
		double val2 = (0.0 - ScaleHeight.InMeters()) * Math.Log(0.0001 / (double)SeaLevelPressure);
		_height = DistanceReference.FromMeters(Math.Max(val, val2));
	}
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `PhysicalAtmosphereReference.GetAtmosphericDensityAtAltitude`, `GetAtmosphericPressureAtAltitude` | [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L80-L88) | Prefixed |
| `PhysicalAtmosphereReference._height`, `Height`, `MIN_PRESSURE`, `MIN_DENSITY` | [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L8-L23) | Boundary written by reflection; thresholds the generator mirrors |
| `AstronomicalTemplate.OnDataLoad`, `AtmosphereReference` | [current/decomp/KSA/AstronomicalTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AstronomicalTemplate.cs#L131-L163) | Postfixed; where the atmosphere hangs |
| `AtmosphereReference.Physical` | [current/decomp/KSA/AtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereReference.cs#L7-L11) | The object the table is keyed on |
| `DistanceReference.FromMeters` | [current/decomp/KSA/DistanceReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DistanceReference.cs#L306-L309) | Build the new boundary value |
| `PhysicsEnvironment.RecomputePositionalValues` | [current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L83-L102) | Main consumer (drag density, engine pressure) |

## Pitfalls and version notes

- **Hot path.** The prefixes sit on functions every vehicle calls every substep, for every body
  including airless ones (which return `true` after one failed weak-table lookup). Keep such prefixes
  allocation-free.
- **No temperature model.** The game has no temperature in `PhysicsEnvironment`; mods deriving the
  speed of sound from `p / rho` (KSAero does) now get an altitude-dependent value instead of a
  per-body constant, which is closer to reality but differs from what those mods document (see
  [Mach number from the game atmosphere (KSAero)](../../ksaero/features/mach-from-game-atmosphere.md)).
- **Matching by `Id`.** Another system that reuses `Earth` or `Mars` as an id gets these tables too;
  a body renamed by another mod loses them.
- **Fixed thresholds.** The generator reproduces the game's `1e-9 kg/m^3` and `1e-4 Pa` boundary rule
  ([make_flight_atmospheres.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_flight_atmospheres.py#L45),
  [make_flight_atmospheres.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_flight_atmospheres.py#L258-L261)); if a game update changes
  `MIN_DENSITY` or `MIN_PRESSURE`, the generated boundaries are stale until regenerated.
- **Silent degrade.** If a member is renamed, flight stays stock and only the log says so.

## See also

- [Measured-atmosphere code generator](measured-atmosphere-generator.md)
- [Measured density tables in the atmosphere shaders](measured-density-tables-in-shaders.md), the same profiles on the rendering side
- [Per-body atmosphere optics in the system XML](atmosphere-optics-in-system-xml.md)
- [Reflection-only mod build](reflection-only-mod-build.md)
- [Python generator emitting checked-in C# tables for flight and shaders](../../../howto/celestials/atmospheres-and-rings/generate-measured-atmosphere-tables-offline/generate-measured-atmosphere-tables-offline.md) - how-to article demonstrating this feature
- [Prefix the density and pressure functions and move the private boundary height](../../../howto/physics/aero-and-ground/override-atmosphere-and-ocean-in-the-physics-environment/override-atmosphere-and-ocean-in-the-physics-environment-1.md) - how-to article demonstrating this feature
- [Read-only tables filled on the main thread before physics starts](../../../howto/physics/solver/write-worker-thread-safe-solver-patches/write-worker-thread-safe-solver-patches-2.md) - how-to article demonstrating this feature

[^ksa-physicalatmospherereference]: KSA.PhysicalAtmosphereReference (decompiled, build 2026.10.10.5554)
[^ra-flightatmospheres]: FlightAtmospheres.cs
