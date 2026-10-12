---
type: Mod Feature
title: Measured-atmosphere code generator
description: Offline Python that builds measured density and pressure profiles for ten bodies, applies KSA's boundary thresholds and emits one C# file of flight tables, XML values and GLSL tables, plus a numpy renderer port
mod: realatmospheres
category: testing-and-tooling
techniques: [code-generation, custom-physics, offline-renderer-model]
hooks: []
lifecycle: []
game_types: [KSA.PhysicalAtmosphereReference]
tags: [tooling, python, code-generation, numpy, atmosphere, nasa-pds, hydrostatic, mie, renderer-model]
---

`AtmosphereTables.Generated.cs` is the single source of every measured number the mod uses at
runtime: the flight tables, the visual density tables compiled into the shaders, and several XML
attribute strings. It is produced by `make_flight_atmospheres.py` and checked in, so building the mod
needs no Python. Two helper scripts close the loop between physics and what KSA actually draws.[^ra-make-flight-atmospheres]

## How it works

### `make_flight_atmospheres.py`

- **Per-body profiles.** One function per body returns altitude, density, pressure, temperature,
  molecular weight and gravity, from: the US Standard Atmosphere 1976 (Earth, via `pyatmos`), VIRA
  temperatures integrated hydrostatically (Venus), the mean of Mars Pathfinder and Phoenix entry
  profiles, Huygens HASI (Titan), Galileo probe ASI (Jupiter), radio-occultation and CIRS/UVIS
  temperature-pressure nodes for Saturn, Uranus and Neptune, Voyager 2 (Triton) and New Horizons (Pluto)
  ([make_flight_atmospheres.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_flight_atmospheres.py#L1-L36)). Downloads are cached in
  `.cache/` ([make_flight_atmospheres.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_flight_atmospheres.py#L48-L55)).
- **Hydrostatic integration.** `integrate_z` integrates pressure on an altitude grid from `T(z)` and
  `mu(z)`; `integrate_p` integrates altitude on a log-pressure grid for the giants, with gravity
  updated as altitude grows and `z = 0` at 1 bar ([make_flight_atmospheres.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_flight_atmospheres.py#L62-L83)).
- **KSA's boundary rule.** `boundary` returns the higher of the altitudes where density falls to
  `1e-9 kg/m^3` and pressure to `1e-4 Pa`, the same thresholds `PhysicalAtmosphereReference` uses
  ([make_flight_atmospheres.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_flight_atmospheres.py#L45),
  [make_flight_atmospheres.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_flight_atmospheres.py#L258-L261),
  [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L8-L10)).[^ksa-physicalatmospherereference]
- **Compact tables.** `decimate` keeps the fewest nodes that reproduce `ln rho` and `ln p` within 0.01
  by linear interpolation (binary search for the longest good segment), which is exactly how
  `FlightTable` interpolates at runtime ([make_flight_atmospheres.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_flight_atmospheres.py#L264-L285)).
  `add` also prints a report comparing the stock exponential with the measured profile at several
  altitudes ([make_flight_atmospheres.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_flight_atmospheres.py#L288-L301)).
- **Visual tables and shader keys.** `visual` resamples `ln(rho / rho0)` on even steps from the
  visual bottom; each table is paired with the body's radius and the scale height written into its XML,
  which become the GLSL `RaDensityTable` conditions ([make_flight_atmospheres.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_flight_atmospheres.py#L304-L312),
  [make_flight_atmospheres.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_flight_atmospheres.py#L912-L915)).
- **Optics.** Separate sections derive Titan's haze shroud from Tomasko et al. optical depth and a
  fitted single-scattering albedo, Pluto's air and haze, Venus's lower haze, Mars's CO2 Rayleigh and
  dust strength, Triton's haze by Mie theory, Earth's ozone cross-sections weighted over each colour
  channel with solar irradiance, the giants' H2/He Rayleigh from refractivity and composition (with a
  `GIANT_RAYLEIGH` switch between measured and eye-tuned), and the giants' stratospheric hazes
  ([make_flight_atmospheres.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_flight_atmospheres.py#L315-L866)).
- **Emit.** `main` runs everything and writes the C# (`FlightTable[] Flight`, `internal const string`
  attribute values, and the `VisualDensityGlsl` raw string) with LF line endings
  ([make_flight_atmospheres.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_flight_atmospheres.py#L872-L1028)).

### Supporting scripts

| Script | Role |
|---|---|
| [mie.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/mie.py) | BHMIE (Bohren and Huffman) single-sphere Mie scattering, shared by the generator (Triton's haze) and the Mars dust scripts |
| [atmosphere_model.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/atmosphere_model.py#L1-L16) | A numpy port of KSA's atmosphere renderer (transmittance LUT 256 x 64 with 50 steps, multiple-scattering LUT, aerial-perspective path with 96 steps, the Sun at 9, the Hable tone curve) that reads its density from the generated file, for predicting relative changes without launching the game |
| [fit_titan_colour.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/fit_titan_colour.py#L1-L17) | Calibrates `atmosphere_model.py` per channel on a screenshot, then fits Titan's haze single-scattering albedo so the disc takes Titan's measured colour; the printed `TITAN_HAZE_SSA` is pasted back into the generator |

## Lifecycle and timing

None in the game. The generator runs on the developer's machine; its output compiles into the DLL.
The runtime consumers are covered in [Measured flight atmospheres](measured-flight-atmospheres.md) and
[Measured density tables in the atmosphere shaders](measured-density-tables-in-shaders.md).

## Key code

| Path | Role |
|---|---|
| [make_flight_atmospheres.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_flight_atmospheres.py#L90-L250) | Per-body profile functions |
| [make_flight_atmospheres.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_flight_atmospheres.py#L252-L312) | Boundary, decimation, flight and visual products |
| [make_flight_atmospheres.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_flight_atmospheres.py#L872-L1028) | Code emission |
| [AtmosphereTables.Generated.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/AtmosphereTables.Generated.cs#L1-L10) | Output header |
| [atmosphere_model.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/atmosphere_model.py#L89-L242) | `Atmosphere` class: the renderer port |

## Snippets

```python
# make_flight_atmospheres.py L258-L261
def boundary(z, rho, p):
    zr = np.interp(math.log(KSA_MIN_DENSITY), np.log(rho)[::-1], z[::-1]) if rho.min() < KSA_MIN_DENSITY else z.max()
    zp = np.interp(math.log(KSA_MIN_PRESSURE), np.log(p)[::-1], z[::-1]) if p.min() < KSA_MIN_PRESSURE else z.max()
    return max(zr, zp)
```

```python
# make_flight_atmospheres.py L62-L68
def integrate_z(z_km, t_of_z, mu_of_z, g_of_z, p0):
    """Hydrostatic pressure on an altitude grid (km) from temperature and molecular weight (g/mol)."""
    t, mu, g = t_of_z(z_km), mu_of_z(z_km), g_of_z(z_km)
    dlnp = -(mu * 1e-3) * g / (RGAS * t) * 1e3                   # per km
    lnp = np.concatenate([[0.0], np.cumsum(0.5 * (dlnp[1:] + dlnp[:-1]) * np.diff(z_km))])
    p = p0 * np.exp(lnp)
    return p, p * mu * 1e-3 / (RGAS * t)
```

```python
# make_flight_atmospheres.py L912-L915
    values, offsets, counts, steps, conds = [], [], [], [], {"rayleigh": [], "mie": []}
    for i, ((body, kind), (step, lnr, (r, h))) in enumerate(VISUAL.items()):
        offsets.append(len(values)); counts.append(len(lnr)); steps.append(step * 1000.0); values += list(lnr)
        conds[kind].append(f"    if (abs(bodyRadius - {r:.1f}) < 20000.0 && abs(scaleHeight - {h:.1f}) < 50.0) return {i};   // {body}")
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `PhysicalAtmosphereReference.MIN_PRESSURE`, `MIN_DENSITY` | [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L8-L10) | Boundary thresholds mirrored offline |
| Stock atmosphere LUT shaders | [current/Content/Core/Shaders/Atmosphere/TransmittanceLut.comp](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/TransmittanceLut.comp) | What `atmosphere_model.py` ports |

## Pitfalls and version notes

- **Generated values are pinned to KSA's shaders.** The renderer port copies LUT sizes and iteration
  counts from build 2026.9.x; if the game changes them, `atmosphere_model.py` predictions drift until
  updated.
- **Network on first run.** The generator downloads PDS files; the cache is not in the repository
  (`.gitignore` lists `.cache/`).
- **Keep the keys in step.** The scale heights the generator emits as GLSL keys are also written into
  the XML by `Payloads.cs`; regenerating with a changed scale height without rebuilding the mod would
  silently disable a table.
- **`__pycache__` is checked in** for a few scripts despite the ignore rule; it is not used by the mod.

## See also

- [Measured flight atmospheres](measured-flight-atmospheres.md)
- [Measured density tables in the atmosphere shaders](measured-density-tables-in-shaders.md)
- [Per-body atmosphere optics in the system XML](atmosphere-optics-in-system-xml.md)
- [Texture and phase asset scripts](texture-and-phase-asset-scripts.md)
- [Python generator emitting checked-in C# tables for flight and shaders](../../../howto/celestials/atmospheres-and-rings/generate-measured-atmosphere-tables-offline/generate-measured-atmosphere-tables-offline.md) - how-to article demonstrating this feature
- [Branch per body in shared shaders: measured ln-density tables recognised by radius and an exact scale height](../../../howto/shaders/stock-shaders/branch-per-body-inside-shared-game-shaders/branch-per-body-inside-shared-game-shaders-1.md) - how-to article demonstrating this feature

[^ra-make-flight-atmospheres]: make_flight_atmospheres.py
[^ksa-physicalatmospherereference]: KSA.PhysicalAtmosphereReference (decompiled, build 2026.10.10.5554)
