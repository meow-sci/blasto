---
type: Mod Feature
title: Synthesize plume physics from nozzle parameters with an isentropic model
description: Pyro turns six nozzle values and a throttle into a full PlumeData by solving exit Mach from the area ratio, expanding the chamber gas isentropically and calling the game's PlumeData.Compute, so engine-free plumes react to ambient pressure like stock ones
mod: unscience
submod: pyro
category: rendering
techniques: [custom-physics]
hooks: [harmony-postfix KSA.Vehicle.AddVolumetricExhaustInstances (called per plume from the Pyro postfix)]
lifecycle: [frame:pre-render]
game_types:
  - KSA.PlumeData
  - KSA.GasProperties
  - KSA.GasConditions
  - KSA.RocketDesign
  - KSA.RocketNozzle
  - KSA.DeLavalNozzleConfig
  - KSA.PhysicalAtmosphereReference
  - KSA.VolumetricExhaustTemplate
  - KSA.VolumetricExhaustRenderer
tags: [exhaust, plumes, gas-dynamics, nozzle, atmosphere]
---

A stock plume gets its shape from the engine: chamber conditions from the combustor, nozzle
geometry, and the ambient pressure decide how long the core is, how wide the plume flares at
altitude, and where shock diamonds sit. Pyro has no engine, so it computes the same `PlumeData`
struct from six numbers the player edits per plume (exit radius, throat radius, chamber pressure in
bar, chamber temperature in K, gamma, gas constant) plus a throttle. Because the result goes through
the game's own `PlumeData.Compute`, the renderer cannot tell it apart from engine data.[^pyro-readme][^plume-entry]

Mechanism in one line: area ratio -> exit Mach (`RocketDesign.SolveMachNumberFromAreaRatio`) ->
isentropic exit pressure and temperature -> core velocity and densities -> `PlumeData.Compute(...)`
with the template's visibility threshold and the current ambient pressure.

## How it works

`PlumePhysics.TryCompute(NozzleSettings, VolumetricExhaustTemplate, ambientPa, throttle, out
PlumeData)`:[^plume-physics]

1. **Sanitize.** Every input is clamped to a finite, physical range (exit radius 0.001-20 m, throat
   below the exit radius, gamma 1.05-1.67, gas constant at least 10, temperature at least 50 K).
2. **Throttle as pressure.** KSA's exhaust templates no longer have throttle modifier curves, so
   `SyntheticChamberPressurePa(bar, throttle) = bar * 1e5 * max(throttle, 0.001)`; the floor keeps a
   small pressure during the shutdown path.[^plume-migration-math]
3. **Exit Mach.** `areaRatio = (r_exit / r_throat)^2`, solved for the supersonic Mach number with the
   game's `RocketDesign.SolveMachNumberFromAreaRatio`, clamped to at least 1.[^ksa-rocketdesign]
4. **Isentropic expansion.** With `f = 1 + (gamma - 1) / 2 * M^2`: `p_exit = p_c * f^(-gamma / (gamma - 1))`,
   `T_exit = T_c / f`.
5. **Velocities and densities.** `coreVelocity = M * gas.ComputeSpeedOfSound(T_exit)`;
   `exitDensity = exhaust.ComputeDensity(gas)`; the chamber state's density is passed as the throat
   density.[^ksa-gasproperties][^ksa-gasconditions]
6. **Visibility.** `RocketNozzle.ComputeMinGasVisibilityDensity(template, exitRadius)`, the same
   public formula stock nozzles use, so shared template edits move Pyro and engine plumes
   together.[^ksa-rocketnozzle]
7. **Assemble.** `PlumeData.Compute(gas, exhaust, exitDensity, coreVelocity, exitRadius, p_c,
   ambient, M, threshold, throatRadius, throatDensity)`, then reject the result if any of 23
   checked values is not finite. Using `Compute` (adopted in the 5438 port) means new derived fields are filled by the game, not left at zero.[^scope-exhaust]

**What reacts to altitude.** Inside `PlumeData.Compute` the game clamps ambient pressure, then derives
`NozzlePressureRatio = p_stag / p_amb`, `JetExpansionRatio = p_exit / p_amb`, the expansion radius
`r_exit * sqrt((p_exit / p_amb)^(1 / gamma))`, the Prandtl-Meyer expansion angle, the fully expanded
Mach number and the Mach-disk area ratio.[^ksa-plumedata] As the camera climbs and ambient pressure
falls, the jet goes from over-expanded (pinched, with diamonds) to under-expanded (a wide bell). Pyro
samples ambient pressure at the camera with
`PhysicalAtmosphereReference.GetAtmosphericPressure(camera)`, which returns atmospheres (it multiplies
pascals by 9.869e-6), and converts back with `* 101325`.[^ksa-atmosphere-ref]

**Inactive frames.** When a plume is off but its shutdown transient is still live, the emitter feeds
the instance's previous `LastPlumeData` instead of a fresh low-pressure result, so the tail keeps its
last burning size.[^plume-emitter]

**Compared with the engine path.** Stock data comes from `RocketNozzle.ComputePlumeData(coreConditions,
ambient)`. For a de Laval nozzle that applies an isothermal inlet loss (`FlowEfficiency`) and an
isentropic stagnation loss (`ExpansionEfficiency`), solves exit Mach from the physical
`ExitArea / ThroatArea`, expands isentropically, switches to a separated-flow exit when the exit
pressure falls below `(2/3) p_amb (p_stag / p_amb)^-0.2`, derives exhaust velocity from the stagnation
enthalpy, and passes the template's design Mach and the cached `MinGasVisibilityDensity`.[^ksa-delaval-config][^ksa-delaval]
Pyro's model is the ideal version of that chain: no efficiencies, no flow separation, and the area
ratio comes from the visual exit radius the player sets.

## Lifecycle and timing

`TryCompute` runs once per plume per frame inside `PlumeEmitter.Submit`, from the
`Vehicle.AddVolumetricExhaustInstances` postfix in `Program.OnPreRender` (`frame:pre-render`). It is
pure math on the main thread, allocation-free, and reads only the template and the ambient pressure
sampled once per vessel. The stock path, by contrast, computes `LastPlumeData` on render worker
threads in `EndPlumeGrouping`, and only for plumes that are active and in view.[^ksa-exhaust-renderer]
See [volumetric exhaust plumes](./pyro-volumetric-exhaust-plumes.md#lifecycle-and-timing) for the
frame position.

## Key code

| Path | Role |
|---|---|
| [pyro.lib/PlumePhysics.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PlumePhysics.cs) | The model, finiteness check, ambient pressure |
| [pyro.lib/PlumeMigrationMath.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PlumeMigrationMath.cs) | Throttle clamp and synthetic chamber pressure |
| [pyro.lib/PlumeEntry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PlumeEntry.cs#L6-L23) | `NozzleSettings` defaults (0.72 m exit, 0.103 m throat, 49 bar, 3400 K, gamma 1.2, R 350) |
| [pyro.lib/PlumeEmitter.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PlumeEmitter.cs#L30-L45) | Calls the model, keeps last data while fading |
| [pyro.tests/Program.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.tests/Program.cs#L38-L40) | Managed checks of the throttle-to-pressure mapping[^pyro-tests] |

## Snippets

The model:

```csharp
// pyro.lib/PlumePhysics.cs L23-L61
    public static bool TryCompute(NozzleSettings n, VolumetricExhaustTemplate template, float ambientPressurePa,
        float throttle, out PlumeData plume)
    {
        plume = PlumeData.Zero;

        float exitRadius = Math.Clamp(FiniteOr(n.ExitRadius, 0.72f), 0.001f, 20f);
        float throatRadius = Math.Clamp(FiniteOr(n.ThroatRadius, 0.103f), 0.0005f, exitRadius * 0.999f);
        float gamma = Math.Clamp(FiniteOr(n.Gamma, 1.2f), 1.05f, 1.67f);
        float gasConstant = Math.Max(FiniteOr(n.GasConstant, 350f), 10f);
        float chamberTemperature = Math.Max(FiniteOr(n.ChamberTemperatureK, 3400f), 50f);
        float chamberPressure = PlumeMigrationMath.SyntheticChamberPressurePa(n.ChamberPressureBar, throttle);
        float ambient = FiniteOr(ambientPressurePa, 0f);

        var gas = new GasProperties { Gamma = gamma, SpecificGasConstant = gasConstant };
        float areaRatio = (exitRadius * exitRadius) / (throatRadius * throatRadius);
        float exitMach = RocketDesign.SolveMachNumberFromAreaRatio(gas, areaRatio);
        if (!float.IsFinite(exitMach) || exitMach < MaxExpansionMach) exitMach = MaxExpansionMach;

        float stagnationFactor = 1f + 0.5f * (gamma - 1f) * exitMach * exitMach;
        float exitPressure = chamberPressure * MathF.Pow(stagnationFactor, -gamma / (gamma - 1f));
        float exitTemperature = chamberTemperature / stagnationFactor;
        var exhaust = new GasConditions { Pressure = exitPressure, Temperature = exitTemperature };
        var chamber = new GasConditions { Pressure = chamberPressure, Temperature = chamberTemperature };

        float soundSpeed = gas.ComputeSpeedOfSound(exitTemperature);
        float coreVelocity = exitMach * soundSpeed;
        float exitDensity = exhaust.ComputeDensity(gas);
        float throatDensity = chamber.ComputeDensity(gas);
        float densityThreshold = ComputeMinVisibleDensity(template, exitRadius);
// ...
        plume = PlumeData.Compute(in gas, in exhaust, exitDensity, coreVelocity, exitRadius, chamberPressure,
            ambient, exitMach, densityThreshold, throatRadius, throatDensity);
        return IsFinite(plume);
    }
```

Throttle as synthetic chamber pressure:

```csharp
// pyro.lib/PlumeMigrationMath.cs L16-L20
    public static float SyntheticChamberPressurePa(float chamberPressureBar, float throttle)
    {
        float bar = float.IsFinite(chamberPressureBar) ? Math.Max(chamberPressureBar, 0.01f) : 0.01f;
        return bar * 100000f * Math.Max(ClampThrottle(throttle), MinimumPressureThrottle);
    }
```

The engine-side equivalent in the game:

```csharp
// current/decomp/KSA/RocketNozzle.cs L331-L345
	public PlumeData ComputePlumeData(in RocketCoreConditions coreConditions, float ambientPressure)
	{
		if (coreConditions.Exit.Pressure <= 0f || MinGasVisibilityDensity.IsNaNOrInf())
		{
			return PlumeData.Zero;
		}
		NozzleConditions nozzleConditions = ComputeConditions(coreConditions.Gas, coreConditions.Exit);
		NozzlePerformance nozzlePerformance = ComputePerformance(in nozzleConditions, ambientPressure);
		return ComputePlumeData(in nozzleConditions.Gas, in nozzlePerformance.Exhaust, in nozzleConditions.Inlet, nozzleConditions.Stagnation.Pressure, nozzlePerformance.ActualExhaustVelocity, ambientPressure, FxExitRadius, MathF.Sqrt(ThroatArea / (float)Math.PI), ComputeDesignMach(nozzleConditions.Gas), MinGasVisibilityDensity);
	}

	public static PlumeData ComputePlumeData(in GasProperties gas, in GasConditions exhaust, in GasConditions inlet, float stagnationPressure, float actualExhaustVelocity, float ambientPressure, float nozzleExitRadius, float throatRadius, float designMach, float densityThreshold)
	{
		return PlumeData.Compute(in gas, in exhaust, exhaust.ComputeDensity(gas), actualExhaustVelocity, nozzleExitRadius, stagnationPressure, ambientPressure, designMach, densityThreshold, throatRadius, inlet.ComputeDensity(gas));
	}
```

The flow-separation branch Pyro does not model:

```csharp
// current/decomp/KSA/DeLavalNozzleConfig.cs L23-L39
		float num2 = ExitArea / ThroatArea;
		float machNumber = RocketDesign.SolveMachNumberFromAreaRatio(conditions.Gas, num2);
		float pressureCoefficient = ComputeInverseNozzlePressureRatioFromMach(machNumber, conditions.Gas.Gamma);
		float exhaustArea = ExitArea;
		GasConditions exhaust = conditions.Gas.IsentropicProcessByPressure(conditions.Stagnation, pressureCoefficient);
		float flowSeparationSeverity = 0f;
		float num3 = ambientPressure * (2f / 3f) * MathF.Pow(conditions.Stagnation.Pressure / ambientPressure, -0.2f);
		if (exhaust.Pressure < num3)
		{
			float val = conditions.Gas.GetCriticalPressureRatio() * conditions.Stagnation.Pressure;
			float num4 = Math.Min(num3, val);
			pressureCoefficient = num4 / conditions.Stagnation.Pressure;
			exhaust = conditions.Gas.IsentropicProcessByPressure(conditions.Stagnation, pressureCoefficient);
// ...
		}
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `PlumeData.Compute(...)` / `PlumeData.Zero` | [current/decomp/KSA/PlumeData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlumeData.cs#L51-L121) | Derives every renderer field from the gas state and ambient pressure |
| `RocketDesign.SolveMachNumberFromAreaRatio(GasProperties, double)` | [current/decomp/KSA/RocketDesign.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketDesign.cs#L168) | Exit Mach from area ratio |
| `GasProperties.ComputeSpeedOfSound(float)` | [current/decomp/KSA/GasProperties.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GasProperties.cs#L38) | Exit sound speed |
| `GasConditions.ComputeDensity(GasProperties)` | [current/decomp/KSA/GasConditions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GasConditions.cs#L18) | Exit and chamber densities |
| `RocketNozzle.ComputeMinGasVisibilityDensity(VolumetricExhaustTemplate, double)` | [current/decomp/KSA/RocketNozzle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketNozzle.cs#L254-L262) | Density threshold from template brightness and absorption |
| `RocketNozzle.ComputePlumeData` | [current/decomp/KSA/RocketNozzle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketNozzle.cs#L331-L345) | Reference path for engines |
| `DeLavalNozzleConfig.ComputeConditions` / `ComputePerformance` | [current/decomp/KSA/DeLavalNozzleConfig.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DeLavalNozzleConfig.cs#L16-L76) | Efficiencies and flow separation in the engine path |
| `DeLavalNozzle.ComputeDesignMach` | [current/decomp/KSA/DeLavalNozzle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DeLavalNozzle.cs#L45-L48) | Design Mach from the template area ratio |
| `PhysicalAtmosphereReference.GetAtmosphericPressure(Camera)` | [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L50-L59) | Ambient pressure at the camera, in atmospheres |
| `VolumetricExhaustRenderer.UpdatePendingPlumes` | [current/decomp/KSA/VolumetricExhaustRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricExhaustRenderer.cs#L909-L922) | Where stock `LastPlumeData` is computed |

## Pitfalls and version notes

- Units: KSA works in pascals for gas state but `GetAtmosphericPressure` returns atmospheres; mixing
  them makes plumes absurdly long or short. The user-facing chamber pressure is bar.[^scope-exhaust]
- There is no `RocketNozzle.UpdatePlumeData` in 5554; the engine-side function is
  `RocketNozzle.ComputePlumeData`, and since 5438 `VolumetricExhaustInstance.UpdateState` no longer
  takes `PlumeData`, so the caller owns `LastPlumeData`.
- `PlumeData.Compute` floors the core velocity at 1.05 times the exit sound speed, so very low exit
  Mach settings still produce a supersonic core.[^ksa-plumedata]
- Semantic drift is silent: if the renderer starts reading a new `PlumeData` field that `Compute`
  does not fill, Pyro plumes change shape without any error. The authors re-diff the nozzle path on
  every game update.[^scope-exhaust]
- The model ignores flow separation, so very large area ratios at sea level look over-expanded
  where a real (or stock) nozzle would separate.

## See also

- [Volumetric exhaust plumes](./pyro-volumetric-exhaust-plumes.md)
- [Exhaust template editor](./pyro-exhaust-template-editor.md) (template values feed the visibility threshold)
- [Scoped per-plume look override](./pyro-scoped-exhaust-look-override.md)
- [Pyro sub-mod](../submods/pyro.md)
- [Mod overview](../overview.md)
- [Postfix AddVolumetricExhaustInstances with an isentropic nozzle model](../../../howto/rendering/lights-and-particles/draw-exhaust-plumes-without-an-engine/draw-exhaust-plumes-without-an-engine-2.md) - how-to article demonstrating this feature

[^plume-physics]: pyro.lib/PlumePhysics.cs
[^plume-migration-math]: pyro.lib/PlumeMigrationMath.cs
[^plume-entry]: pyro.lib/PlumeEntry.cs
[^plume-emitter]: pyro.lib/PlumeEmitter.cs
[^pyro-tests]: pyro.tests/Program.cs
[^pyro-readme]: pyro/README.md
[^scope-exhaust]: scope/exhaust-plumes.md (authors' integration analysis)
[^ksa-plumedata]: KSA.PlumeData (decompiled, build 2026.10.10.5554)
[^ksa-rocketnozzle]: KSA.RocketNozzle (decompiled, build 2026.10.10.5554)
[^ksa-delaval-config]: KSA.DeLavalNozzleConfig (decompiled, build 2026.10.10.5554)
[^ksa-delaval]: KSA.DeLavalNozzle (decompiled, build 2026.10.10.5554)
[^ksa-rocketdesign]: KSA.RocketDesign (decompiled, build 2026.10.10.5554)
[^ksa-gasproperties]: KSA.GasProperties (decompiled, build 2026.10.10.5554)
[^ksa-gasconditions]: KSA.GasConditions (decompiled, build 2026.10.10.5554)
[^ksa-atmosphere-ref]: KSA.PhysicalAtmosphereReference (decompiled, build 2026.10.10.5554)
[^ksa-exhaust-renderer]: KSA.VolumetricExhaustRenderer (decompiled, build 2026.10.10.5554)
