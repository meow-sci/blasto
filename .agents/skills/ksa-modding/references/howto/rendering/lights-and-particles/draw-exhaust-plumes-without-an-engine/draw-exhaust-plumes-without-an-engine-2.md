---
type: How-To
title: Postfix AddVolumetricExhaustInstances with an isentropic nozzle model
description: Place KSA's volumetric plume anywhere on a vessel and make it react to ambient pressure and throttle by computing PlumeData from nozzle parameters with an isentropic expansion and the game's own PlumeData.Compute
category: rendering
group: lights-and-particles
topic: draw-exhaust-plumes-without-an-engine
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/unscience/features/pyro-volumetric-exhaust-plumes
  - mods/unscience/features/pyro-isentropic-nozzle-model
techniques: [harmony-postfix]
hooks: [harmony-postfix KSA.Vehicle.AddVolumetricExhaustInstances]
lifecycle: [frame:pre-render, render:exhaust, frame:draw-ui]
game_types:
  - KSA.Vehicle
  - KSA.VolumetricExhaustRenderer
  - KSA.VolumetricExhaustInstance
  - KSA.PlumeData
  - KSA.GasProperties
  - KSA.GasConditions
  - KSA.RocketDesign
  - KSA.RocketNozzle
  - KSA.PhysicalAtmosphereReference
  - KSA.Camera
  - KSA.Part
tags: [exhaust, plume, volumetric, isentropic, nozzle-model, altitude, throttle]
---
Outcome: a plume you can place on any part (with a position and rotation offset in the part's frame), choose a stock exhaust template for, and tune with six nozzle numbers. It plays the template's startup transient, follows the part, bends in wind, changes shape with altitude (pinched with shock diamonds near the ground, a wide bell in vacuum) and plays the shutdown tail when switched off.

Demonstrated by unscience's Pyro ([Submit engine-free volumetric exhaust plumes into the game's plume batch](../../../../mods/unscience/features/pyro-volumetric-exhaust-plumes.md), [Synthesize plume physics from nozzle parameters with an isentropic model](../../../../mods/unscience/features/pyro-isentropic-nozzle-model.md)). Prerequisites: StarMap, Harmony. Same hook as [approach 1](draw-exhaust-plumes-without-an-engine-1.md); this adds the physics.

## Background: how the game does it

A stock plume gets its shape from the engine: chamber conditions from the combustor, nozzle geometry, and the ambient pressure decide the core length, how wide it flares and where shock diamonds sit. Pyro has no engine, so it computes the same `PlumeData` struct. The game's reference path is `RocketNozzle.ComputePlumeData`:

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

Whatever fills `PlumeData` goes through `PlumeData.Compute(...)`, which clamps ambient pressure and derives the nozzle pressure ratio, jet expansion ratio, expansion radius, Prandtl-Meyer angle, fully expanded Mach and Mach-disk area ratio ([current/decomp/KSA/PlumeData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlumeData.cs#L80-L121)). Using `Compute` rather than assigning fields means new derived fields are filled by the game, not left at zero. Note: this build has no `RocketNozzle.UpdatePlumeData`; the engine-side function is `ComputePlumeData`, and `VolumetricExhaustInstance.UpdateState` no longer takes `PlumeData`, so the caller owns `LastPlumeData` ([current/decomp/KSA/VolumetricExhaustInstance.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricExhaustInstance.cs#L57)).

## Lifecycle and timing

`Program.OnPreRender` (`frame:pre-render`) prepares the frame's plumes on the main thread: `UpdateFrameData` (L2376), `BeginPlumeGrouping` (L2377), `vehicle.AddVolumetricExhaustInstances` per vessel in frame (L2382, **your postfix runs here**), `EndPlumeGrouping` (L2384), and later `RenderGame` draws all instances (`render:exhaust`, row 13 of the [RenderGame table](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence)). `GetCamera()` here is the main viewport's camera, and the exhaust pass is only recorded for the main view, so plumes (stock and yours) do not appear in secondary or crew-portrait viewports. In this build the stock vessel method only queues `AddPlume` records that are updated on worker threads in `EndPlumeGrouping`; your direct submissions land in the instance array before the grouped stock plumes and never enter the plume hierarchy (so they are not merged with neighbouring plumes, not view-culled by `MaxPlumeExtent` and get no bend target). Opening a second grouping frame for your plumes would drop the stock pending entries. Pruning and UI run earlier in `frame:draw-ui`, so the list is stable during submission.

## Steps

1. **Bind the three-parameter overload explicitly** and name the postfix parameters exactly like the original (`camera`, `renderer`, `frameDeltaTime`); a rename makes `Apply` throw and the feature skip:

   ```csharp
// pyro.lib/PyroPatches.cs L19-L21
private static MethodBase? VehicleTarget() => AccessTools.Method(typeof(Vehicle),
    nameof(Vehicle.AddVolumetricExhaustInstances),
    new[] { typeof(Camera), typeof(VolumetricExhaustRenderer), typeof(double) });
```

   ```csharp
// pyro.lib/PyroPatches.cs L87-L98
private static void AfterAddVolumetricExhaustInstances(Vehicle __instance, Camera camera,
    VolumetricExhaustRenderer renderer, double frameDeltaTime)
{
    try
    {
        PyroSubmod.Instance?.SubmitPlumes(__instance, camera, renderer, frameDeltaTime);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"pyro: error submitting plumes: {ex.Message}");
    }
}
```

2. **One real instance per plume**, created from a template id: `new VolumetricExhaustReference { Id = id }`, `Load()`, `new VolumetricExhaustInstance(reference)`. The instance owns the game's four-slot startup and shutdown pulse tracker, so you get stock transients for free. Changing the template replaces the instance, which restarts the startup transient:

   ```csharp
// pyro.lib/PlumeTemplates.cs L65-L73
/// <summary>Creates a fresh game-side instance bound to the template. Returns null if the id is unknown.</summary>
public static VolumetricExhaustInstance? CreateInstance(string templateId)
{
    string resolvedId = NormalizeId(templateId);
    var reference = new VolumetricExhaustReference { Id = resolvedId };
    reference.Load();
    if (reference.Template == null) return null;
    return new VolumetricExhaustInstance(reference);
}
```

3. **Compute `PlumeData` from six nozzle numbers**: exit radius, throat radius, chamber pressure in bar, chamber temperature, gamma and gas constant, plus throttle. The chain is area ratio, then exit Mach (`RocketDesign.SolveMachNumberFromAreaRatio`), then isentropic exit pressure and temperature, core velocity and densities, then `PlumeData.Compute` with the template's visibility threshold and the current ambient pressure. Inputs are clamped to physical ranges first:

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
    if (!float.IsFinite(soundSpeed) || !float.IsFinite(coreVelocity) || !float.IsFinite(exitDensity)
        || !float.IsFinite(throatDensity) || !float.IsFinite(densityThreshold) || exitDensity <= 0f)
        return false;

    // PlumeData.Compute owns the derived mass flow, expansion ratios, core Mach and apparent speed. Using
    // it avoids silently dropping fields when KSA extends this struct again.
    plume = PlumeData.Compute(in gas, in exhaust, exitDensity, coreVelocity, exitRadius, chamberPressure,
        ambient, exitMach, densityThreshold, throatRadius, throatDensity);
    return IsFinite(plume);
}
```

4. **Throttle becomes chamber pressure.** KSA's exhaust templates no longer have throttle modifier curves, so a synthetic pressure is used; the floor keeps a small pressure during the shutdown path:

   ```csharp
// pyro.lib/PlumeMigrationMath.cs L16-L20
public static float SyntheticChamberPressurePa(float chamberPressureBar, float throttle)
{
    float bar = float.IsFinite(chamberPressureBar) ? Math.Max(chamberPressureBar, 0.01f) : 0.01f;
    return bar * 100000f * Math.Max(ClampThrottle(throttle), MinimumPressureThrottle);
}
```

5. **Ambient pressure** comes from the camera. `PhysicalAtmosphereReference.GetAtmosphericPressure(camera)` returns atmospheres, so convert with `* 101325` (mixing units makes plumes absurdly long or short):

   ```csharp
// pyro.lib/PlumePhysics.cs L69-L75
/// <summary>Ambient pressure at the camera in pascals (KSA's helper returns atmospheres).</summary>
public static float AmbientPressurePa(Camera camera)
{
    const double paPerAtm = 101325.0;
    try { return (float)(PhysicalAtmosphereReference.GetAtmosphericPressure(camera) * paPerAtm); }
    catch { return 0f; }
}
```

6. **Submit one plume.** Position goes part-local offset, through `Part.MatrixAsmb2VehicleAsmb`, `Vehicle.PosAsmbToBody` and `Body2Cce`, plus `camera.GetPositionEgo(vehicle)`; the axis is part-local `-X` (every stock nozzle's convention) rotated by the Euler offset. While inactive and still fading, feed the instance's previous `LastPlumeData` so the tail keeps its last burning size. Then `UpdateState` and, if `IsLive`, `AddInstance`:

   ```csharp
// pyro.lib/PlumeEmitter.cs L30-L68
plume.Cycle.Update(simulationTime);
float throttle = PlumeMigrationMath.ClampThrottle(plume.Throttle);
bool active = plume.EffectiveEnabled && throttle > 0f;
bool hasCurrentData = PlumePhysics.TryCompute(plume.Nozzle, template, ambientPressurePa, throttle,
    out var plumeData);
if (!hasCurrentData && active)
{
    plume.LastError = "Nozzle settings produce a non-finite plume; adjust radius/pressure.";
    return false;
}

// An inactive instance may be midway through its shutdown pulse. KSA's new UpdateState no longer
// accepts PlumeData and deliberately leaves LastPlumeData untouched while inactive, so the renderer
// continues to size the tail from the last active state.
if (!hasCurrentData || (!active && instance.LastPlumeData.Exhaust.Pressure > 0f))
    plumeData = instance.LastPlumeData;
plume.LastError = null;

doubleQuat offsetRotation = RotationHelper.FromEulerDegrees(plume.Rotation);
double3 axisPart = double3.Unpack(in BaseAxis).Transform(offsetRotation);
double3 positionPart = double3.Unpack(in plume.Position);
double3 positionVehicleAsmb = positionPart.Transform(plume.Part.MatrixAsmb2VehicleAsmb);
double3 axisVehicleAsmb = axisPart.Transform(plume.Part.Asmb2VehicleAsmb);

// Part-local → vehicle assembly → body → camera-ego, matching Vehicle.AddVolumetricExhaustInstances.
double3 positionEgo = camera.GetPositionEgo(plume.Vehicle)
                      + plume.Vehicle.PosAsmbToBody(positionVehicleAsmb).Transform(plume.Vehicle.Body2Cce);
double3 axisWorld = axisVehicleAsmb.NormalizeOrZero().Transform(plume.Vehicle.Body2Cce);
float3 emitterPosition = float3.Pack(in positionEgo);
float3 emitterAxis = float3.Pack(in axisWorld);
ComputeAirState(plume.Vehicle, out float3 airVelocity, out float airDensity);

GasProperties gas = plumeData.Gas;
GasConditions exhaust = plumeData.Exhaust;
if (active) instance.LastPlumeData = plumeData;
instance.UpdateState(simulationTime, active, simulationDeltaTime, in gas, in exhaust,
    plumeData.CoreVelocity, emitterPosition, emitterAxis, emitterPosition, emitterAxis,
    ambientPressurePa, airVelocity, airDensity);
if (!instance.IsLive) return true;
```

   ```csharp
// pyro.lib/PlumeEmitter.cs L86-L95
IDisposable lookScope = PyroPatches.BeginLookOverride(absorption, refractionScale, refractionFallback);
try
{
    renderer.AddInstance(instance, in bendTarget, in fade, in diamondFade);
}
finally
{
    lookScope.Dispose();
}
return true;
```

7. **Optional: cycle on and off from simulation time**, not accumulated deltas, so pause freezes it and warp or a long frame jumps to the right phase:

   ```csharp
// pyro.lib/PlumeCycle.cs L39-L49
public void Update(double simulationTime)
{
    if (!Running || !double.IsFinite(simulationTime)) return;
    Sanitize();
    if (simulationTime < _lastTime) { Restart(simulationTime); return; }
    _lastTime = simulationTime;
    // Modulo handles a long frame or time warp without an unbounded transition loop.
    double phase = (simulationTime - _startTime) % ((double)OnSeconds + OffSeconds);
    IsOn = phase < OnSeconds;
    RemainingSeconds = IsOn ? OnSeconds - phase : OnSeconds + (double)OffSeconds - phase;
}
```

## Complete example

| File | Role |
|---|---|
| [pyro.lib/PyroPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PyroPatches.cs) | Explicit-overload postfix install, rollback on failure |
| [pyro.lib/PlumeEmitter.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PlumeEmitter.cs) | Per-plume transform, air state, `UpdateState`, `AddInstance` |
| [pyro.lib/PlumePhysics.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PlumePhysics.cs) | The isentropic model, finiteness check, ambient pressure |
| [pyro.lib/PlumeMigrationMath.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PlumeMigrationMath.cs) | Throttle clamp and synthetic chamber pressure |
| [pyro.lib/PlumeCycle.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PlumeCycle.cs) | Simulation-time on/off cycle |
| [pyro.lib/PlumeTemplates.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PlumeTemplates.cs) | Template catalog and instance creation |

## Variations and gotchas

- **Altitude reacts through `PlumeData.Compute`.** As ambient pressure falls the jet goes from over-expanded (pinched, with diamonds) to under-expanded (a wide bell). Pyro's model is the ideal chain: no flow separation (the engine path switches to a separated-flow exit below `(2/3) p_amb (p_stag/p_amb)^-0.2`) and no efficiencies, so very large area ratios at sea level look over-expanded.
- **Semantic drift is silent.** If the renderer starts reading a new `PlumeData` field that `Compute` does not fill, plumes change shape without error.
- **Ambient pressure source.** Yours is sampled at the camera; stock plumes use the vessel's own environment pressure, so far from the camera the two can differ.
- **Only vessels in frame.** Plumes only update while their vessel is in `Program.VehiclesInFrame`, like stock engines.
- **Removing a plume** drops the instance immediately; there is no shutdown tail for a removed plume.
- **Per-plume look.** Pyro also scopes a prefix on the final `AddInstance(ExhaustInstance, int)` to override absorption and refraction for one submission; shared template edits (approach 3) are global.

## See also

- [Overview](overview.md)
- [Approach 1](draw-exhaust-plumes-without-an-engine-1.md), [approach 3](draw-exhaust-plumes-without-an-engine-3.md)
- [Submit engine-free volumetric exhaust plumes into the game's plume batch](../../../../mods/unscience/features/pyro-volumetric-exhaust-plumes.md), [Synthesize plume physics from nozzle parameters with an isentropic model](../../../../mods/unscience/features/pyro-isentropic-nozzle-model.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
