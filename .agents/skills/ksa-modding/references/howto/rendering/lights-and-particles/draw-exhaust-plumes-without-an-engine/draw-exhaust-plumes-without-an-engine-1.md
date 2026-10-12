---
type: How-To
title: Template-based plume instances with RocketNozzle.ComputePlumeData and mod-made gas numbers
description: Draw KSA's volumetric rocket flame from nozzles that belong to no engine by building VolumetricExhaustInstances from a Core template, shaping them with ComputePlumeData and submitting them from a postfix on Vehicle.AddVolumetricExhaustInstances
category: rendering
group: lights-and-particles
topic: draw-exhaust-plumes-without-an-engine
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksacars/features/exhaust-plumes-without-engine]
techniques: [harmony-postfix, starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded, harmony-postfix KSA.Vehicle.AddVolumetricExhaustInstances]
lifecycle: [startup:all-mods-loaded, frame:pre-render, render:exhaust]
game_types:
  - KSA.Vehicle
  - KSA.Program
  - KSA.VolumetricExhaustRenderer
  - KSA.VolumetricExhaustInstance
  - KSA.VolumetricExhaustReference
  - KSA.VolumetricExhaustTemplate
  - KSA.RocketNozzle
  - KSA.RocketDesign
  - KSA.GasProperties
  - KSA.GasConditions
  - KSA.Camera
tags: [exhaust, plume, volumetric, flame, rocket, nozzle-less]
---
Outcome: a KSA rocket flame, with KSA's own startup flash and shutdown tail, coming out of points on a vessel that have no engine behind them: a flying car's lift, boost and downforce rockets, a prop flame, a pyrotechnic. You build `VolumetricExhaustInstance` objects from a Core exhaust template, shape them each frame from gas numbers you make up for the throttle, and hand them to the volumetric exhaust renderer from a postfix, beside the craft's real engines.

Demonstrated by KSACars ([Volumetric exhaust plumes with no engine behind them](../../../../mods/ksacars/features/exhaust-plumes-without-engine.md)). Prerequisites: StarMap, Harmony. For plume physics that reacts to altitude see [approach 2](draw-exhaust-plumes-without-an-engine-2.md).

## Background: how the game does it

A real engine cannot be kept just for its flame: with no propellant reachable it has no plume, any flow it has pushes the craft, and a `<DeLavalNozzle>` with no `<Rocket>` makes `AddVolumetricExhaustInstances` dereference the nozzle's missing rocket. So the plume has to be built from outside. In `Program.OnPreRender` the game empties the renderer's instance list, opens a plume grouping, asks every vehicle in frame to queue its nozzles and closes the grouping:

```csharp
// current/decomp/KSA/Program.cs L2372-L2391
private FrameAcquireResult OnPreRender(double dtPlayer)
{
	Profiler.MainThread.Begin(Profiler.Tag.OnPreRender);
	Profiler.MainThread.Begin(Profiler.Tag.VolumetricExhaust);
	_volumetricExhaustRenderer.UpdateFrameData();
	_volumetricExhaustRenderer.BeginPlumeGrouping();
	ReadOnlySpan<Vehicle> vehiclesInFrame = VehiclesInFrame;
	for (int i = 0; i < vehiclesInFrame.Length; i++)
	{
		Vehicle vehicle = vehiclesInFrame[i];
		vehicle.AddVolumetricExhaustInstances(GetCamera(), _volumetricExhaustRenderer, dtPlayer);
	}
	_volumetricExhaustRenderer.EndPlumeGrouping();
	ReadOnlySpan<Vehicle> vehiclesInFrame2 = VehiclesInFrame;
	for (int j = 0; j < vehiclesInFrame2.Length; j++)
	{
		Vehicle vehicle2 = vehiclesInFrame2[j];
		vehicle2.UpdateExhaustLights(GetCamera(), MainViewport);
	}
	Profiler.MainThread.End();
```

`Vehicle.AddVolumetricExhaustInstances(Camera, VolumetricExhaustRenderer, double)` walks the vehicle's `RocketNozzle` modules and queues each with `renderer.AddPlume` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L5718-L5754)). `UpdateFrameData` resets the instance count every frame, so an instance added from a StarMap hook is either wiped before the render (`StarMapAfterGui`) or added after it (`StarMapAfterOnFrame`). The public `AddInstance` submits one flame directly, without merging:

```csharp
// current/decomp/KSA/VolumetricExhaustRenderer.cs L842-L845
public ExhaustSubmission AddInstance(VolumetricExhaustInstance volumetricExhaustInstance, in ExhaustBendTarget bendTarget, in ExhaustAxialFade fade, in ExhaustDiamondFade diamondFade)
{
	return AddInstanceCore(volumetricExhaustInstance.EmitterPosition, volumetricExhaustInstance.EmitterAxis, volumetricExhaustInstance, in bendTarget, in fade, in diamondFade, isMerged: false, default(ExhaustNode));
}
```

The engine's own debug tool, `VolumetricExhaustRenderer.UpdateTestInstances` ([current/decomp/KSA/VolumetricExhaustRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricExhaustRenderer.cs#L2831-L2891)), builds instances the same way from a template id, `Load()`, `ComputePlumeData` over invented gas numbers and `UpdateState`, which is what shows the path works without a nozzle.

## Lifecycle and timing

| Moment | Where | Thread |
|---|---|---|
| Patch installed | `[StarMapAllModsLoaded]` | main |
| Instance list emptied | `Program.OnPreRender` then `UpdateFrameData()` (L2376) | main |
| Grouping opened | `BeginPlumeGrouping()` (L2377) | main |
| KSA's nozzles queued, then **your postfix** | per vehicle in frame: `AddVolumetricExhaustInstances(GetCamera(), renderer, dtPlayer)` (L2382) | main |
| KSA's plumes merged and submitted | `EndPlumeGrouping()` (L2384) | main |
| Drawn | `RenderGame`: `_volumetricExhaustRenderer.Render` (L4891) | main (GPU recording) |

`OnPreRender` runs after the GUI pass, `UpdateGauges`, the light pass and `OnFrameCelestials`, right before `Render` (rows 19 to 22 of [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence); RenderGame row 13). The postfix runs once per vehicle in frame, with the camera and renderer the game is using and the player's frame time, so positions in the camera's Ego frame are right for this frame. It only acts for vehicles you own; the dictionary lookup returns for every other craft. Exhaust is recorded for the main view only.

## Steps

1. **Hook the three-argument method** by exact signature and pin it so a game change breaks the build. Never throw from the postfix (it is inside the engine's pre-render pass):

   ```csharp
// src/KSACars/Ksa/FlamesHook.cs L27-L51
public static void Install()
{
    if (Installed) return;

    try
    {
        MethodInfo? target = AccessTools.Method(typeof(Vehicle), nameof(Vehicle.AddVolumetricExhaustInstances),
            [typeof(Camera), typeof(VolumetricExhaustRenderer), typeof(double)]);
        if (target is null)
        {
            Log.Warn("KSA has no Vehicle.AddVolumetricExhaustInstances to hook; a flying car shows no flame");
            return;
        }

        MethodInfo postfix = typeof(FlamesHook).GetMethod(nameof(AfterExhausts),
                                                          BindingFlags.NonPublic | BindingFlags.Static)!;
        _harmony = new Harmony(HarmonyId);
        _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
        Installed = true;
    }
    catch (Exception e)
    {
        Log.Warn($"could not hook the exhaust plumes; a flying car shows no flame ({e.Message})");
    }
}
```

   ```csharp
// src/KSACars/Ksa/FlamesHook.cs L68-L85
// Inside the engine's pre-render pass: nothing here may throw.
private static void AfterExhausts(Vehicle __instance, Camera camera, VolumetricExhaustRenderer renderer, double frameDeltaTime)
{
    try
    {
        Buggies.Flames(__instance, camera, renderer, frameDeltaTime);
    }
    catch (Exception e)
    {
        if (_complained) return;
        _complained = true;
        Log.Error("drawing a car's flames failed", e);
    }
}

// Never called: puts the patched method in this assembly's metadata, and so in docs/KSA-API-SURFACE.md.
private static void PinTheSignature(Vehicle vehicle, Camera camera, VolumetricExhaustRenderer renderer) =>
    vehicle.AddVolumetricExhaustInstances(camera, renderer, 0.0);
```

2. **Make up the gas for the throttle.** The numbers only shape the flame (the push is computed elsewhere). KSACars sizes them like Core's auxiliary engine: exit radius 0.10 m, area ratio 4, chamber pressure `throttle * 30e5` Pa at 3000 K, exit pressure 4 % of chamber at 1700 K, gamma 1.21, R = 681 J/(kg K), exhaust velocity 2500 m/s:

   ```csharp
// src/KSACars/Sim/Lift.cs L49-L55
public static FlameGas Flame(double throttle)
{
    float chamber = (float)(Math.Clamp(throttle, 0.0, 1.0) * 30e5);
    return new FlameGas(ExitRadius: 0.10f, AreaRatio: 4f, ChamberPressure: chamber, ChamberTemperature: 3000f,
                        ExitPressure: chamber * 0.04f, ExitTemperature: 1700f, Gamma: 1.21f,
                        SpecificGasConstant: 681f, ExhaustVelocity: 2500f);
}
```

3. **Gather the environment for this craft**: ambient pressure and air density, the surface velocity as air velocity rotated into CCE (as KSA does for its own nozzles), the simulation delta time (frame time times simulation speed), the craft's camera-relative position and `Body2Cce`. Guard `craft.Parent` for null; the stock method dereferences it:

   ```csharp
// src/KSACars/Ksa/Buggies.cs L243-L262
bool burning = throttle > 0.0;
FlameGas f = Lift.Flame(throttle);
GasProperties gas = new() { Gamma = f.Gamma, SpecificGasConstant = f.SpecificGasConstant };
GasConditions inlet = new() { Pressure = f.ChamberPressure, Temperature = f.ChamberTemperature };
GasConditions exhaust = new() { Pressure = f.ExitPressure, Temperature = f.ExitTemperature };

double3 at = KsaWorld.PositionEcl(craft);
double air = KsaWorld.AirDensityRatioAt(craft, at);
float ambient = (float)(air * 101325.0);
float airDensity = (float)(air * KsaWorld.ReferenceAirDensityKgPerM3);
float3 airVelocity = craft.Parent is { } parent
    ? float3.Pack(craft.GetSurfaceVelocityCci().Transform(parent.GetCci2Cce()))
    : float3.Zero;

double simDt = frameDeltaTime * Universe.GetSimulationSpeed();
double now = Universe.GetElapsedSeconds();
double3 craftEgo = camera.GetPositionEgo(craft);
doubleQuat body2Cce = craft.Body2Cce;
doubleQuat part2Asmb = e.Part.Asmb2VehicleAsmb;
float3 axis = float3.Pack((part2Asmb * direction).Transform(body2Cce));
```

4. **Create one instance per nozzle, lazily**: `new VolumetricExhaustReference { Id = "EngineAAuxiliary" }`, `.Load()` to resolve the template, `new VolumetricExhaustInstance(reference)`. Shape it with the game's own static `RocketNozzle.ComputePlumeData`, pose it, advance its state and submit it. A dying flame keeps the shape it last had, which is what its shutdown tail is drawn from:

   ```csharp
// src/KSACars/Ksa/Buggies.cs L265-L294
for (int i = 0; i < nozzles.Length; i++)
{
    if (flames[i] is not { } flame)
    {
        VolumetricExhaustReference reference = new() { Id = FlameTemplate };
        reference.Load();
        flame = flames[i] = new VolumetricExhaustInstance(reference);
    }
    if (flame.Template is not { } template) continue;

    // A flame going out keeps the shape it last had, which is what its dying is drawn from.
    if (burning)
    {
        flame.LastPlumeData = RocketNozzle.ComputePlumeData(
            in gas, in exhaust, in inlet, inlet.Pressure, f.ExhaustVelocity, ambient, f.ExitRadius,
            f.ExitRadius / MathF.Sqrt(f.AreaRatio), RocketDesign.SolveMachNumberFromAreaRatio(gas, f.AreaRatio),
            RocketNozzle.ComputeMinGasVisibilityDensity(template, f.ExitRadius));
    }

    double3 nozzle = e.Part.PositionVehicleAsmb + (part2Asmb * nozzles[i]);
    float3 where = float3.Pack(craftEgo + craft.PosAsmbToBody(nozzle).Transform(body2Cce));
    flame.UpdateState(now, burning, simDt, in gas, in exhaust, f.ExhaustVelocity, where, axis, where, axis,
                      ambient, airVelocity, airDensity);
    if (!flame.IsLive) continue;

    any = true;
    renderer.AddInstance(flame, default, in ExhaustAxialFade.NoFadeOut, in ExhaustDiamondFade.None);
}

return !burning && !any ? null : flames;
```

5. **Drop the instances once the set is out and burnt down** (`flame.IsLive` false for all), so the next burn starts from cold with a fresh startup transient.

## Complete example

| File | Role |
|---|---|
| [src/KSACars/Ksa/FlamesHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/FlamesHook.cs) | Postfix on `Vehicle.AddVolumetricExhaustInstances`; never throws; pinned |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L211-L295) | `Flames` and `Burn`: template, plume data, pose, state, submit |
| [src/KSACars/Sim/Lift.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/Lift.cs#L44-L55) | `Flame`: the gas a throttle is drawn from |
| [src/KSACars/Sim/BuggyProfile.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/BuggyProfile.cs#L88-L110) | Nozzle points in the part frame |

## Variations and gotchas

- **No merging.** KSA's own nozzles go through `AddPlume` and are merged by the plume hierarchy at `EndPlumeGrouping`; `AddInstance` submits each flame on its own, so neighbouring flames never merge into one plume.
- **Budget.** `AddInstance` returns without drawing when the renderer is `Disabled` (`MaxVolumetricExhausts == 0`) or the instance count is at its maximum; check `renderer.Disabled` first.
- **No exhaust light, no particles, no trail.** KSA's exhaust lights, particle spawn and plume trails iterate the craft's `RocketNozzle` modules, which a nozzle-less craft does not have; add those yourself ([exhaust lights](../add-dynamic-lights-to-the-world/overview.md), [smoke trails, approach 5](draw-exhaust-plumes-without-an-engine-5.md)).
- **Camera-relative `float3`.** Positions are packed in the renderer camera's Ego frame; a different camera or frame puts the flame in the wrong place.
- **Secondary views.** The exhaust pass is recorded for the main view only.
- **Fail soft.** If the patch fails the craft still flies, with no flame.

## See also

- [Overview](overview.md)
- [Approach 2](draw-exhaust-plumes-without-an-engine-2.md), [approach 3](draw-exhaust-plumes-without-an-engine-3.md), [approach 4](draw-exhaust-plumes-without-an-engine-4.md), [approach 5](draw-exhaust-plumes-without-an-engine-5.md)
- [Volumetric exhaust plumes with no engine behind them](../../../../mods/ksacars/features/exhaust-plumes-without-engine.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
