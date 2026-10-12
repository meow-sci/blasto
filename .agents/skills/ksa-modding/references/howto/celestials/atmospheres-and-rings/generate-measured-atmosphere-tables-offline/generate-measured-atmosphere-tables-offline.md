---
type: How-To
title: Python generator emitting checked-in C# tables for flight and shaders
description: Integrate per-body reference profiles hydrostatically, apply KSA's boundary thresholds, decimate to the nodes that reproduce ln density and ln pressure within tolerance, and emit one generated C# file consumed by the flight patch, the XML and the shader tables
category: celestials
group: atmospheres-and-rings
topic: generate-measured-atmosphere-tables-offline
difficulty: advanced
prerequisites: [python, numpy]
demonstrated_by:
  - mods/realatmospheres/features/measured-atmosphere-generator
  - mods/realatmospheres/features/measured-flight-atmospheres
techniques: [xml-asset, harmony-prefix]
hooks:
  - harmony-postfix KSA.AstronomicalTemplate.OnDataLoad
  - harmony-prefix KSA.PhysicalAtmosphereReference.GetAtmosphericDensityAtAltitude
  - harmony-prefix KSA.PhysicalAtmosphereReference.GetAtmosphericPressureAtAltitude
lifecycle: [startup:asset-load, physics:vehicle-solver]
game_types: [KSA.PhysicalAtmosphereReference, KSA.AstronomicalTemplate, KSA.AtmosphereReference]
tags: [atmosphere, tables, generator, decimation, hydrostatic, offline]
---
Outcome: measured atmospheres for ten bodies compiled into a mod without runtime downloads: `AtmosphereTables.Generated.cs` is the single source for the flight tables, the visual density tables compiled into patched shaders, and several XML attribute strings. Demonstrated by Real Atmospheres ([Measured-atmosphere generator](../../../../mods/realatmospheres/features/measured-atmosphere-generator.md), [Measured flight atmospheres](../../../../mods/realatmospheres/features/measured-flight-atmospheres.md)). Building the mod needs no Python; only regenerating does.

## Background: how the game does it

The stock lookups are `SeaLevel * exp(-h / ScaleHeight)` ([current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L80-L88)) with a boundary computed once at load from two thresholds ([current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L42-L47)). A measured model replaces the functions, so the generator must reproduce the same boundary rule or `Physical.Height`, the atmosphere radius and the physics regions would disagree with the table. Interpolating linearly in the logarithm is exactly what the runtime table does, so decimation can use the same rule.

## Lifecycle and timing

The generator runs on the developer's machine. At runtime: a Harmony postfix on `AstronomicalTemplate.OnDataLoad` (`startup:asset-load`: `Mod.LoadSystems` deserialises each system template on the main thread inside `ModLibrary.LoadAll`, and `SystemTemplate.OnDataLoad` then calls every body's `OnDataLoad`) registers the table for the template's `Id` and overwrites the stock boundary; prefixes on the two lookup functions answer from the table on worker threads per substep (`physics:vehicle-solver`; main-thread readers such as `PhysicalAtmosphereReference.GetAtmosphericPressure(Camera)` for UI and audio get the same answers), allocation-free, reading a `ConditionalWeakTable` filled before the first physics step. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md), [write worker-thread-safe solver patches](../../../physics/solver/write-worker-thread-safe-solver-patches/overview.md) and, for the other half of the mod, [define atmospheres in system XML](../define-atmospheres-and-cloud-layers-in-system-xml/overview.md).

## Steps

1. **Write one profile function per body** returning altitude, density, pressure, temperature, molecular weight and gravity from a reference: the US Standard Atmosphere 1976 (Earth), VIRA (Venus), Mars Pathfinder and Phoenix entry profiles, Huygens HASI (Titan), Galileo probe (Jupiter), radio-occultation and CIRS/UVIS nodes (Saturn, Uranus, Neptune), Voyager 2 (Triton) and New Horizons (Pluto). Cache downloads in an ignored `.cache/` folder.

2. **Integrate hydrostatically.** On an altitude grid pressure follows from `T(z)` and `mu(z)`; for the giants integrate altitude on a log-pressure grid with gravity updated as altitude grows and `z = 0` at 1 bar:

```py
# make_flight_atmospheres.py L62-L68
def integrate_z(z_km, t_of_z, mu_of_z, g_of_z, p0):
    """Hydrostatic pressure on an altitude grid (km) from temperature and molecular weight (g/mol)."""
    t, mu, g = t_of_z(z_km), mu_of_z(z_km), g_of_z(z_km)
    dlnp = -(mu * 1e-3) * g / (RGAS * t) * 1e3                   # per km
    lnp = np.concatenate([[0.0], np.cumsum(0.5 * (dlnp[1:] + dlnp[:-1]) * np.diff(z_km))])
    p = p0 * np.exp(lnp)
    return p, p * mu * 1e-3 / (RGAS * t)
```

3. **Apply KSA's boundary rule.** Mirror the game's two thresholds exactly:

```py
# make_flight_atmospheres.py L258-L261
def boundary(z, rho, p):
    zr = np.interp(math.log(KSA_MIN_DENSITY), np.log(rho)[::-1], z[::-1]) if rho.min() < KSA_MIN_DENSITY else z.max()
    zp = np.interp(math.log(KSA_MIN_PRESSURE), np.log(p)[::-1], z[::-1]) if p.min() < KSA_MIN_PRESSURE else z.max()
    return max(zr, zp)
```

4. **Decimate to what the runtime reproduces.** Keep the fewest nodes that reproduce `ln rho` and `ln p` within 0.01 by linear interpolation, found by binary search for the longest good segment:

```py
# make_flight_atmospheres.py L264-L285
def decimate(z, rho, p, max_err=0.01):
    """Keep the fewest nodes that reproduce ln(rho) and ln(p) to within max_err by linear interpolation."""
    lr, lp = np.log(rho), np.log(p)
    keep = [0]
    i = 0
    while i < len(z) - 1:
        j = len(z) - 1
        lo, hi = i + 1, len(z) - 1
        best = i + 1
        while lo <= hi:                                          # binary search the longest good segment
            m = (lo + hi) // 2
            seg = slice(i, m + 1)
            f = (z[seg] - z[i]) / max(z[m] - z[i], 1e-12)
            er = np.max(np.abs(lr[seg] - (lr[i] + f * (lr[m] - lr[i]))))
            ep = np.max(np.abs(lp[seg] - (lp[i] + f * (lp[m] - lp[i]))))
            if max(er, ep) <= max_err:
                best, lo = m, m + 1
            else:
                hi = m - 1
        keep.append(best)
        i = best
    return np.array(keep)
```

5. **Emit source, not data files.** `main` writes `FlightTable[] Flight` (altitude nodes, `ln` density, `ln` pressure, boundary, and the 0 km values for the XML), `internal const string` attribute values and the `VisualDensityGlsl` raw string, with LF line endings. Visual tables resample `ln(rho / rho0)` on even steps and are paired with the body's radius and the scale height written into the XML, which become the GLSL conditions that select a table (a key, so the table is skipped if anyone changes that scale height):

```py
# make_flight_atmospheres.py L912-L915
values, offsets, counts, steps, conds = [], [], [], [], {"rayleigh": [], "mie": []}
for i, ((body, kind), (step, lnr, (r, h))) in enumerate(VISUAL.items()):
    offsets.append(len(values)); counts.append(len(lnr)); steps.append(step * 1000.0); values += list(lnr)
    conds[kind].append(f"    if (abs(bodyRadius - {r:.1f}) < 20000.0 && abs(scaleHeight - {h:.1f}) < 50.0) return {i};   // {body}")
```

6. **Interpolate the same way at runtime** (binary search, linear in the logarithm, nearest segment extended past either end):

```csharp
// AtmosphereTables.cs L26-L39
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
}
```

7. **Attach by template `Id` and answer the lookups.** The postfix registers the table and overwrites the boundary the stock `OnDataLoad` just computed; the prefixes return the table's value and skip the original only for registered objects:

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

   Before patching, check the shape of every target (`AstronomicalTemplate.OnDataLoad`, both lookups, the `AtmosphereReference` and `Physical` fields, the private `_height`, `DistanceReference.FromMeters`, and that the lookups' parameter is still named `altitudeInMeters`, because Harmony binds prefix arguments by name) and patch nothing if anything is missing.

8. **Keep XML and runtime consistent.** The edited XML also gets each body's `Physical` block set to the table's 0 km values, so UI readouts agree and, if the patch cannot install, the stock exponential at least starts from the measured surface.

## Fitting parameters against the renderer (optional)

Two helpers close the loop between physics and what KSA draws: `atmosphere_model.py`, a numpy port of KSA's atmosphere renderer (transmittance LUT 256 x 64 with 50 steps, a multiple-scattering LUT, a 96-step aerial-perspective path) that reads its density from the generated file, and `fit_titan_colour.py`, which calibrates the port per channel on a screenshot and then fits Titan's haze single-scattering albedo so the disc takes Titan's measured colour (the printed `TITAN_HAZE_SSA` is pasted back into the generator). They predict relative changes without launching the game, but copy LUT sizes and step counts from a specific game build, so they drift when the game changes them.

## Complete example

| File | Role |
|---|---|
| [make_flight_atmospheres.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_flight_atmospheres.py#L90-L312) | Profiles, boundary, decimation, flight and visual products |
| [make_flight_atmospheres.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_flight_atmospheres.py#L872-L1028) | Code emission |
| [AtmosphereTables.Generated.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/AtmosphereTables.Generated.cs#L1-L10) | Output header |
| [FlightAtmospheres.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/FlightAtmospheres.cs#L21-L91) | Shape check and the three patches |
| [atmosphere_model.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/atmosphere_model.py#L89-L242) | The renderer port |
| [fit_titan_colour.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/fit_titan_colour.py) | Calibrates the renderer port on a screenshot |

## Variations and gotchas

- **Generated values are pinned to KSA's shaders.** The renderer port copies LUT sizes from a specific build.
- **Network on first run.** The generator downloads PDS files; the cache is not in the repository.
- **Keep the keys in step.** The scale heights emitted as GLSL keys are also written into the XML; regenerating with a changed scale height without rebuilding the mod silently disables a table.
- **Matching is by template `Id`**, so a same-named body elsewhere gets the table.
- **The boundary moves physics regions.** A boundary of 996 km above Titan forces full physics for freefall from there.

## See also

- [Overview](overview.md)
- [Measured-atmosphere generator](../../../../mods/realatmospheres/features/measured-atmosphere-generator.md), [Measured flight atmospheres](../../../../mods/realatmospheres/features/measured-flight-atmospheres.md)
- [Define atmospheres and cloud layers in system XML](../../../celestials/atmospheres-and-rings/define-atmospheres-and-cloud-layers-in-system-xml/overview.md), [Override atmosphere and ocean in the physics environment](../../../physics/aero-and-ground/override-atmosphere-and-ocean-in-the-physics-environment/overview.md)
