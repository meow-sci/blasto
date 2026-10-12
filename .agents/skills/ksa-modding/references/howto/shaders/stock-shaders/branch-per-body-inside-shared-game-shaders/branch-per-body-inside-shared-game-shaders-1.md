---
type: How-To
title: "Branch per body in shared shaders: measured ln-density tables recognised by radius and an exact scale height"
description: Replace six exponential density sites in four stock atmosphere shaders with one injected function that picks a per-body table by radius and scale height, and generate the tables offline from the same script as the flight model
category: shaders
group: stock-shaders
topic: branch-per-body-inside-shared-game-shaders
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/realatmospheres/features/measured-density-tables-in-shaders
  - mods/realatmospheres/features/measured-atmosphere-generator
techniques: [glsl-shader, xml-patch]
hooks: [starmap StarMapBeforeMain]
lifecycle: [startup:starmap-before-main, startup:asset-load, render:atmosphere]
game_types:
  - KSA.AtmosphereRenderer
  - KSA.Atmosphere.Rendering.ShaderStructs.AtmosphereData
  - KSA.MieScatteringReference
tags: [glsl, per-body, atmosphere, density, lookup-table, code-generation, realatmospheres, python]
---
The stock atmosphere shaders compute density as `exp(-altitude / scaleHeight)` for Rayleigh and Mie scattering. RealAtmospheres replaces that with measured density profiles for ten bodies without adding a shader: it injects one function and rewrites every site that computes the exponential,
and the function recognises a body by its radius and by a scale height only the mod writes into the body's XML. A Python generator emits the tables for the shaders, the flight model and the XML from one run. You need a text-patched shader tree
([Replace or text-patch the game's GLSL without editing game files](../replace-or-patch-game-glsl-without-editing-game-files/overview.md)) and Python for the generator.

## Background: how the game does it

The exponential appears at six places across four files: two in `AtmosphereFunctions.glsl` (the LUT functions), one in `AerialPerspectiveLut.comp`, one in the per-pixel `Atmosphere.comp` and two in `Godrays.glsl`
([current/Content/Core/Shaders/Atmosphere/AtmosphereFunctions.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/AtmosphereFunctions.glsl#L56), [current/Content/Core/Shaders/Atmosphere/Atmosphere.comp](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/Atmosphere.comp#L125)). The shaders get per-body inputs from `AtmosphereData` (`bottomRadius`, scale heights in `.w`, `miePhaseFunctionAsymmetry`).
Because every LUT and the per-pixel pass integrate the same function once it is replaced, transmittance, multiple scattering and the sky stay consistent with each other. The LUTs are kept per body while valid, so the shaders must change before the first LUT is generated; the patch runs at launch.

## Lifecycle and timing

| Step | When | Value |
|---|---|---|
| Inject helper, rewrite sites, in the shadow copy | `[StarMapBeforeMain]` | `startup:starmap-before-main` |
| Shaders compile from the copy | `ModLibrary.LoadAll` | `startup:asset-load` |
| The function runs in the transmittance, multiple-scattering and ambient LUTs (when a body's LUTs are invalid), the sky and aerial LUTs, god rays and the compose pass | `PlanetTransparenciesRenderer` records them per atmospheric body | `render:atmosphere` |

The per-body inputs come from the uniform buffer filled by `FillPlanetAtmosphereData` on the main thread during recording. See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

1. **Inject the helper before the include guard's `#endif` of `AtmosphereData.glsl`** so every shader that includes the atmosphere data sees it. A placeholder inside the helper is replaced by the generated table GLSL; every step is skip-with-log if an anchor is
   missing, and each file is patched on its own:

   ```csharp
   // ShadowBuilder.cs L627-L662
       private static void PatchMarsDensity(string shadersDir)
       {
           // 1) helper into AtmosphereData.glsl (before the include-guard endif)
           string dataPath = Path.Combine(shadersDir, "Atmosphere", "AtmosphereData.glsl");
           if (File.Exists(dataPath))
           {
               string d = ReadLf(dataPath);
               if (!d.Contains(Payloads.MarsDensityMarker))
               {
                   if (d.Contains(Payloads.AtmosphereDataEndif))
                   {
                       d = d.Replace(Payloads.AtmosphereDataEndif,
                           Payloads.MarsDensityHelper.Trim('\n')
                               .Replace("// __RA_DENSITY_TABLES__", AtmosphereTables.VisualDensityGlsl.Trim('\n'))
                           + "\n\n" + Payloads.AtmosphereDataEndif);
                       File.WriteAllText(dataPath, d);
                   }
                   else Log("WARN: AtmosphereData include-guard not found, skipping double-exp density");
               }
           }

           // 2) swap the density sites to call the helper
           int done = 0;
           foreach (var (relFile, stock, patch, count) in Payloads.MarsDensitySites)
           {
               string path = Path.Combine(shadersDir, relFile.Replace('/', Path.DirectorySeparatorChar));
               if (!File.Exists(path)) { Log($"WARN: {relFile} missing, skipping density site"); continue; }
               string s = ReadLf(path);
               if (s.Contains(patch)) { done++; continue; } // already applied
               int n = CountOf(s, stock);
               if (n != count) { Log($"WARN: {relFile} density anchor found {n}x (expected {count}), skipping"); continue; }
               File.WriteAllText(path, s.Replace(stock, patch));
               done++;
           }
           Log($"Mars double-exp density ({done}/{Payloads.MarsDensitySites.Length} files)");
       }
   ```

   Source: [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L627-L662).

2. **Rewrite the sites with exact counts.** Two stock expressions (`DensA` with `atmosphereData.*.w`, `DensB` with local scale-height variables) become calls that also pass the phase asymmetry, `bottomRadius` and `ozoneExtent`:

   ```csharp
   // Payloads.cs L2277-L2289
       public const string DensAStock = "exp(-seaLevelAltitude / vec2(atmosphereData.rayleighCoefficientsAndScaleHeight.w, atmosphereData.mieCoefficientsAndScaleHeight.w))";
       public const string DensAPatch = "AtmosphereDensityFalloff(seaLevelAltitude, atmosphereData.rayleighCoefficientsAndScaleHeight.w, atmosphereData.mieCoefficientsAndScaleHeight.w, atmosphereData.miePhaseFunctionAsymmetry.xyz, atmosphereData.bottomRadius, atmosphereData.ozoneExtent)";
       public const string DensBStock = "exp(-seaLevelAltitude / vec2(rayleighScaleHeight, mieScaleHeight))";
       public const string DensBPatch = "AtmosphereDensityFalloff(seaLevelAltitude, rayleighScaleHeight, mieScaleHeight, atmosphereData.miePhaseFunctionAsymmetry.xyz, atmosphereData.bottomRadius, atmosphereData.ozoneExtent)";

       // (file, stock, patch, expected-count) for the density sites
       public static readonly (string File, string Stock, string Patch, int Count)[] MarsDensitySites =
       {
           ("Atmosphere/AtmosphereFunctions.glsl", DensAStock, DensAPatch, 2),
           ("Atmosphere/AerialPerspectiveLut.comp", DensAStock, DensAPatch, 1),
           ("Atmosphere/Atmosphere.comp", DensBStock, DensBPatch, 1),
           ("Atmosphere/Godrays.glsl", DensBStock, DensBPatch, 2),
       };
   ```

   Source: [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2277-L2289). Altitude and scale heights are in metres in this build; an early version used kilometre constants and squeezed Mars's dust into a 4 m layer.

3. **Recognise the body in GLSL, by radius and by an exact scale height.** The generated `RaDensityTable` returns a table index only if the radius is within 20 km of a known body and the scale height equals the value the mod wrote into that body's XML to within 50 m; otherwise it returns -1 and
   the stock exponential is used. The radius tolerance survives a moved `StartHeight` (a sibling mod lowers it below the terrain floor), and the scale-height test turns the tables off if anyone else edits that atmosphere:

   ```glsl
   // AtmosphereTables.Generated.cs L565-L578 (GLSL inside a C# raw string)
   int RaDensityTable(float bodyRadius, float scaleHeight)
   {
       if (abs(bodyRadius - 6371000.0) < 20000.0 && abs(scaleHeight - 8430.0) < 50.0) return 0;   // Earth
       if (abs(bodyRadius - 6051800.0) < 20000.0 && abs(scaleHeight - 15900.0) < 50.0) return 1;   // Venus
       if (abs(bodyRadius - 3389500.0) < 20000.0 && abs(scaleHeight - 15000.0) < 50.0) return 3;   // Mars
       if (abs(bodyRadius - 2575500.0) < 20000.0 && abs(scaleHeight - 67100.0) < 50.0) return 4;   // Titan
       if (abs(bodyRadius - 1188300.0) < 20000.0 && abs(scaleHeight - 179200.0) < 50.0) return 6;   // Pluto
       if (abs(bodyRadius - 69911000.0) < 20000.0 && abs(scaleHeight - 25000.0) < 50.0) return 8;   // Jupiter
       if (abs(bodyRadius - 58232000.0) < 20000.0 && abs(scaleHeight - 52000.0) < 50.0) return 10;   // Saturn
       if (abs(bodyRadius - 25362000.0) < 20000.0 && abs(scaleHeight - 28000.0) < 50.0) return 12;   // Uranus
       if (abs(bodyRadius - 24622000.0) < 20000.0 && abs(scaleHeight - 21000.0) < 50.0) return 13;   // Neptune
       if (abs(bodyRadius - 1353400.0) < 20000.0 && abs(scaleHeight - 94800.0) < 50.0) return 14;   // Triton
       return -1;
   }
   ```

   Source: [AtmosphereTables.Generated.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/AtmosphereTables.Generated.cs#L565-L578). The matching XML values are written by the same mod, so the keys and the XML stay in step.

4. **Look the table up.** All tables share one `const float raDensityLn[2130]` array of `ln(rho / rho0)` on even altitude steps with per-table offset, count and step arrays; `RaTableDensity` interpolates linearly in the log and continues the last slope past the top:

   ```glsl
   // AtmosphereTables.Generated.cs L590-L596 (GLSL inside a C# raw string)
   float RaTableDensity(int table, float seaLevelAltitude)
   {
       float x = max(seaLevelAltitude, 0.0) / raDensityStep[table];
       int i = min(int(x), raDensityCount[table] - 2);
       int o = raDensityOffset[table] + i;
       return exp(mix(raDensityLn[o], raDensityLn[o + 1], x - float(i)));
   }
   ```

   Source: [AtmosphereTables.Generated.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/AtmosphereTables.Generated.cs#L590-L596).

5. **Keep the stock exponential as the fallback and add per-body layers on top.** The base-density function selects a dust double-exponential for the Mars sentinel (see [Smuggle data into stock shaders through unused uniforms, state bits and sentinel values](../send-extra-data-to-stock-shaders-without-new-uniforms/overview.md)), table lookups for known bodies, and the stock exponential otherwise:

   ```glsl
   // Payloads.cs L2221-L2241 (GLSL inside a C# raw string)
   vec2 RaBaseDensity(float seaLevelAltitude, float rayleighScaleHeight, float mieScaleHeight, vec3 mieAsymmetry, float bodyRadius)
   {
       // compact 4 km + extended 20 km layers, expressed as fractions of the (working)
       // stock mieScaleHeight (=9 km) so this is UNIT-AGNOSTIC - seaLevelAltitude is in
       // METERS in this build, so a literal /4.0 capped dust to a 4-metre layer (bug).
       // Mars's dust is measured from the datum, the mean surface (r = 3389.5 km), not from the
       // sky's bottom at the floor of Hellas: it thickens into the basins and thins over the heights.
       float dustAltitude = seaLevelAltitude + (abs(bodyRadius - 3389500.0) < 20000.0 ? bodyRadius - 3389500.0 : 0.0);
       float mie = mieAsymmetry.x < -0.5
           ? 0.75 * exp(1.0 - exp(dustAltitude / (0.44 * mieScaleHeight))) + 0.25 * exp(1.0 - exp(dustAltitude / (2.2 * mieScaleHeight)))
           : exp(-seaLevelAltitude / mieScaleHeight);
       // Titan's absorber: the haze's shape up to the top of the main haze, then a fast drop
       int mieTable = RaMieDensityTable(bodyRadius, mieScaleHeight);
       if (mieTable >= 0)
           mie = RaTableDensity(mieTable, seaLevelAltitude);
       int rayTable = RaDensityTable(bodyRadius, rayleighScaleHeight);
       float ray = rayTable >= 0
           ? RaTableDensity(rayTable, seaLevelAltitude)
           : exp(-seaLevelAltitude / rayleighScaleHeight);
       return vec2(ray, mie);
   }
   ```

   Source: [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2221-L2241).

6. **Generate everything from one script.** `make_flight_atmospheres.py` emits C# flight tables, XML attribute strings and the GLSL tables from the same profiles, so the renderer and the flight model agree. Each visual table is paired with the body's radius and the scale height written into its XML, which become the GLSL conditions:

   ```python
   # make_flight_atmospheres.py L912-L915
       values, offsets, counts, steps, conds = [], [], [], [], {"rayleigh": [], "mie": []}
       for i, ((body, kind), (step, lnr, (r, h))) in enumerate(VISUAL.items()):
           offsets.append(len(values)); counts.append(len(lnr)); steps.append(step * 1000.0); values += list(lnr)
           conds[kind].append(f"    if (abs(bodyRadius - {r:.1f}) < 20000.0 && abs(scaleHeight - {h:.1f}) < 50.0) return {i};   // {body}")
   ```

   Source: [make_flight_atmospheres.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_flight_atmospheres.py#L912-L915). The output `AtmosphereTables.Generated.cs` is checked in, so building the mod needs no Python. `atmosphere_model.py` is a numpy port of the renderer (transmittance LUT 256 x 64, aerial-perspective path with 96 steps) that reads the generated densities to predict relative changes without
   launching the game ([atmosphere_model.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/atmosphere_model.py#L1-L16)).

## Complete example

| File | Role |
|---|---|
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs) | `PatchMarsDensity` and `PatchRayleighColour` (all-or-nothing per file) |
| [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs) | Helper text, site table, dust profile |
| [AtmosphereTables.Generated.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/AtmosphereTables.Generated.cs) | Generated tables, keys, lookup |
| [make_flight_atmospheres.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_flight_atmospheres.py) | The generator |

## Variations and gotchas

- **Keep the keys in step.** The scale heights the generator emits as GLSL keys are also written into the XML by the mod; regenerating with a changed scale height without rebuilding silently disables a table.
- **Large constant arrays.** A 2130-entry `const float[]` in an include compiles into every shader that includes `AtmosphereData.glsl`; shaderc accepts it but compile time grows.
- **Visual and flight tables are separate.** Shader tables are normalised to the visual bottom and stepped evenly; flight tables are absolute and decimated. Both come from the same run.
- **LUT-only bodies cannot vary horizontally.** The sky-view LUT has no azimuth axis, so density modulation added here is concentric; horizontal structure needs a per-pixel march ([Extend KSA's sky and cloud shaders: per-pixel haze and cloud lighting fixes](../extend-stock-sky-and-cloud-shaders/overview.md)).
- **Titan's colour by altitude** is a further all-or-nothing rewrite (13 Rayleigh occurrences in four files call `RaRayleighCoefficients`); it follows the same count-first pattern.
- **Network on first run.** The generator downloads PDS data; the cache is not in the repository.

## See also

- Siblings: [relief-aware terminator](branch-per-body-inside-shared-game-shaders-2.md), [overview](overview.md)
- [Measured density tables in the atmosphere shaders](../../../../mods/realatmospheres/features/measured-density-tables-in-shaders.md), [Measured-atmosphere code generator](../../../../mods/realatmospheres/features/measured-atmosphere-generator.md)
- [Smuggle data into stock shaders through unused uniforms, state bits and sentinel values](../send-extra-data-to-stock-shaders-without-new-uniforms/overview.md), [Compile-check a mod's GLSL without launching the game](../../glsl-workflow/check-mod-shaders-offline-with-glslang/overview.md)
