---
type: Mod Feature
title: Submit engine-free volumetric exhaust plumes into the game's plume batch
description: Pyro postfixes Vehicle.AddVolumetricExhaustInstances and, for each plume welded to that vessel, drives a mod-owned VolumetricExhaustInstance through UpdateState and VolumetricExhaustRenderer.AddInstance so it renders, ignites, shuts down and reacts to air like a real engine plume
mod: unscience
submod: pyro
category: rendering
techniques: [harmony-postfix, toml-config]
hooks:
  - harmony-postfix KSA.Vehicle.AddVolumetricExhaustInstances
  - "starmap StarMapBeforeGui (via unscience ISubmod.Update: dead-anchor pruning, cycle sampling)"
  - "starmap StarMapAfterGui (via unscience ISubmod.RenderContent: create, tune, presets)"
lifecycle: [frame:pre-render, render:exhaust, frame:draw-ui, event:load]
game_types:
  - KSA.Vehicle
  - KSA.VolumetricExhaustRenderer
  - KSA.VolumetricExhaustInstance
  - KSA.VolumetricExhaustReference
  - KSA.VolumetricExhaustTemplate
  - KSA.Camera
  - KSA.Part
  - KSA.Universe
  - KSA.Program
  - KSA.ExhaustBendTarget
  - KSA.ExhaustAxialFade
  - KSA.ExhaustDiamondFade
tags: [exhaust, plumes, volumetrics, harmony, ego-space, animation, time-warp]
---

Pyro places the game's own volumetric rocket plume anywhere on a vessel without an engine part. The
player picks a vessel, a part and optionally a sub-part, an exhaust template (`EngineALarge`,
`EngineAMed`, `EngineACompact`, `EngineAAuxiliary`, `RCS`, `MmuRcsVac`, or any a content mod adds),
a position and rotation offset in the part's frame, a throttle and nozzle parameters. The plume plays
the template's startup transient, follows the part, bends in wind, changes shape with altitude and
plays the shutdown tail when switched off. Plumes can also cycle on and off on a timer.[^pyro-readme]

Mechanism in one line: a Harmony postfix on `Vehicle.AddVolumetricExhaustInstances(Camera,
VolumetricExhaustRenderer, double)` gets the same renderer, camera and frame delta the game is using
for that vessel's plumes and submits each Pyro plume with `instance.UpdateState(...)` followed by
`renderer.AddInstance(instance, bendTarget, fade, diamondFade)`.

## How it works

**A real instance per plume.** Each `PlumeEntry` owns its instance.[^plume-entry] `PlumeTemplates.CreateInstance(id)` builds
`new VolumetricExhaustReference { Id = id }`, calls `Load()` (which resolves
`VolumetricExhaustTemplate.Get(id)`), and wraps it in `new VolumetricExhaustInstance(reference)`. The
instance owns the game's four-slot startup/shutdown pulse tracker, so Pyro gets stock transients for
free. Changing the template replaces the instance, which restarts the startup
transient.[^plume-templates][^ksa-exhaust-reference][^ksa-exhaust-instance]

**The hook.** `PyroPatches.Apply` binds the three-parameter `AddVolumetricExhaustInstances` overload
explicitly and adds a postfix whose parameters are named exactly like the original (`camera`,
`renderer`, `frameDeltaTime`). The postfix calls `PyroSubmod.Instance.SubmitPlumes(__instance, ...)`,
which returns immediately when there are no plumes or `renderer.Disabled`, computes simulation time
(`Universe.GetElapsedSeconds()`), simulation delta (`frameDeltaTime * Universe.GetSimulationSpeed()`,
the same formula the vessel method uses) and ambient pressure, and submits every plume whose
`Vehicle` is this vessel.[^pyro-patches][^pyro-submod][^ksa-universe]

**Per plume (`PlumeEmitter.Submit`).**[^plume-emitter]

1. Sample the cycle and compute `active = EffectiveEnabled && throttle > 0`.
2. Compute `PlumeData` from the nozzle settings (see
   [isentropic nozzle model](./pyro-isentropic-nozzle-model.md)). While inactive and still fading,
   keep the instance's `LastPlumeData` so the shutdown tail is sized from the last burning state.
3. Position: part-local offset through `Part.MatrixAsmb2VehicleAsmb`, then
   `Vehicle.PosAsmbToBody`, `Body2Cce`, plus `camera.GetPositionEgo(vehicle)`; the axis is part-local
   `-X` (the convention of every stock nozzle) rotated by the Euler offset,[^rotation-helper] then
   `Part.Asmb2VehicleAsmb` and `Body2Cce`. This is the same chain the vessel method applies to
   `RocketNozzleState.FxExhaustLocationVehicleAsmb`, so sub-parts and scaled parts work.[^ksa-part][^ksa-camera]
4. Air state: surface velocity in CCE and atmospheric density at the vessel's altitude, copied from
   `Vehicle.AddVolumetricExhaustInstances`, so plumes fold and bend in atmosphere.[^ksa-vehicle]
5. `instance.UpdateState(simTime, active, simDt, gas, exhaust, coreVelocity, pos, axis, pos, axis,
   ambient, airVelocity, airDensity)`; `LastPlumeData` is written only while active.
6. If `instance.IsLive` (the instance's own decision, true during a burn or a fading transient),
   `renderer.AddInstance(instance, default(ExhaustBendTarget), ExhaustAxialFade.NoFadeOut,
   ExhaustDiamondFade.None)` inside a per-plume [look override](./pyro-scoped-exhaust-look-override.md).

**Auto-removal.** `PyroSubmod.Update` prunes plumes whose vessel is no longer in
`VehicleProvider.GetAllVehicles()` or whose part's top ancestor (`PartParent` chain) is no longer in
`vehicle.Parts.Parts`, so decoupled or destroyed anchors do not leave orphan plumes.[^pyro-submod]

**On/off cycles.** `PlumeCycle` derives its phase from absolute simulation time rather than
accumulating deltas: `phase = (t - start) % (on + off)`. Repeated samples in one frame (the UI update
and each submission) do not advance it, pause freezes it, warp or a long frame jumps straight to the
right phase without looping, and a backwards clock restarts at On. Durations are sanitized to
0.05-3600 s. Manual On/Off and All On/All Off cancel the cycle. `pyro.tests` runs these cases against
the production class without the game.[^plume-cycle][^pyro-tests]

**Presets and saves.** Named presets (template, offsets, throttle, nozzle, look; no anchor, no cycle)
are written as TOML to `<user data>/.unscience/pyro-presets.toml` with Tomlyn. The `pyro` save
participant stores anchor paths, the preset-shaped settings, enabled state and cycle phase, and
recreates the instances after a native load; removed template ids `EngineAVernier` and
`EngineATurbine` are mapped to `EngineAAuxiliary` on every path when the old id is not
available.[^preset-manager][^pyro-persistence][^plume-template-ids]

## Lifecycle and timing

`Program.OnPreRender` (called from `OnFrame` after `OnFrameCelestials`, before `Render`) prepares the
frame's plumes on the main thread:[^ksa-program]

| Step | Line | Pyro |
|---|---|---|
| `_volumetricExhaustRenderer.UpdateFrameData()` | L2376 | Instance count and refraction flag reset; camera pressure sampled |
| `BeginPlumeGrouping()` | L2377 | Stock pending list cleared |
| `vehicle.AddVolumetricExhaustInstances(GetCamera(), renderer, dtPlayer)` per `VehiclesInFrame` | L2382 | **Postfix runs here**, once per vessel in frame; Pyro calls `UpdateState` and `AddInstance` directly |
| `EndPlumeGrouping()` | L2384 | Stock plumes: `UpdateState` on the render worker pool, view culling, `ComputePlumeData`, hierarchy merge, then `AddInstance` |
| `RenderGame` -> `_volumetricExhaustRenderer.Render(...)` | L4891 | All instances drawn in the main view's exhaust pass |

`GetCamera()` here is the main viewport's camera (`PrepareFrame` resets `_frameViewport` to
`MainViewport` at L2204), and the exhaust pass is only recorded for the main view, so plumes, stock
and Pyro alike, do not appear in secondary or crew-portrait viewports. The stock vessel method now
only queues `AddPlume` records; Pyro's direct submissions land in the instance array before the
grouped stock plumes and never enter the plume hierarchy.[^ksa-exhaust-renderer] The authors note
that opening a second grouping frame for Pyro would drop the stock pending entries.[^scope-exhaust]

Pyro's `Update(dt)` (pruning, cycle sampling) runs in `frame:draw-ui`, earlier in the same frame,
so the list is stable during submission. All of it is main-thread; Pyro does not touch the physics
state of the vessel.

## Key code

| Path | Role |
|---|---|
| [pyro.lib/PyroPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PyroPatches.cs) | Explicit-overload postfix install, rollback on failure |
| [pyro.lib/PyroSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PyroSubmod.cs) | Plume list, `SubmitPlumes`, public API, pruning |
| [pyro.lib/PlumeEmitter.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PlumeEmitter.cs) | Per-plume transform, air state, `UpdateState`, `AddInstance` |
| [pyro.lib/PlumeEntry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PlumeEntry.cs) | Anchor, offsets, nozzle settings, instance |
| [pyro.lib/PlumeTemplates.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PlumeTemplates.cs) | Template catalog and instance creation |
| [pyro.lib/PlumeCycle.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PlumeCycle.cs) | Simulation-time on/off cycle |
| [pyro.lib/RotationHelper.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/RotationHelper.cs) | Euler degrees to quaternion |
| [pyro.lib/PlumePresetManager.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PlumePresetManager.cs) | TOML presets |
| [pyro.lib/PyroSubmod.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PyroSubmod.Persistence.cs) | Save participants `pyro` and `pyro.templates` |
| [pyro.tests/Program.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.tests/Program.cs) | Cycle, throttle and id-migration checks |

## Snippets

Where the game prepares plumes each frame:

```csharp
// current/decomp/KSA/Program.cs L2376-L2384
		_volumetricExhaustRenderer.UpdateFrameData();
		_volumetricExhaustRenderer.BeginPlumeGrouping();
		ReadOnlySpan<Vehicle> vehiclesInFrame = VehiclesInFrame;
		for (int i = 0; i < vehiclesInFrame.Length; i++)
		{
			Vehicle vehicle = vehiclesInFrame[i];
			vehicle.AddVolumetricExhaustInstances(GetCamera(), _volumetricExhaustRenderer, dtPlayer);
		}
		_volumetricExhaustRenderer.EndPlumeGrouping();
```

The explicitly bound target and the postfix:

```csharp
// pyro.lib/PyroPatches.cs L19-L98
    private static MethodBase? VehicleTarget() => AccessTools.Method(typeof(Vehicle),
        nameof(Vehicle.AddVolumetricExhaustInstances),
        new[] { typeof(Camera), typeof(VolumetricExhaustRenderer), typeof(double) });
// ...
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

Anchoring and state update for one plume:

```csharp
// pyro.lib/PlumeEmitter.cs L44-L68
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

Warp- and pause-safe cycle phase:

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

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Program.OnPreRender` plume block | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2372-L2384) | Frame position of the hook |
| `Vehicle.AddVolumetricExhaustInstances(Camera, VolumetricExhaustRenderer, double)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L5718-L5754) | Postfix target; reference for air state and transform chain |
| `VolumetricExhaustRenderer.UpdateFrameData` / `BeginPlumeGrouping` / `AddPlume` / `EndPlumeGrouping` | [current/decomp/KSA/VolumetricExhaustRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricExhaustRenderer.cs#L765-L952) | Stock batching around the hook |
| `VolumetricExhaustRenderer.AddInstance(VolumetricExhaustInstance, in ExhaustBendTarget, in ExhaustAxialFade, in ExhaustDiamondFade)` | [current/decomp/KSA/VolumetricExhaustRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricExhaustRenderer.cs#L842-L845) | Direct submission |
| `VolumetricExhaustRenderer.Disabled` | [current/decomp/KSA/VolumetricExhaustRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricExhaustRenderer.cs#L446) | Skip when volumetrics are off |
| `VolumetricExhaustInstance(VolumetricExhaustReference)`, `UpdateState(...)`, `IsLive`, `LastPlumeData` | [current/decomp/KSA/VolumetricExhaustInstance.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricExhaustInstance.cs#L57-L130) | Per-plume transient state |
| `VolumetricExhaustReference.Id` / `Load()` / `Template` | [current/decomp/KSA/VolumetricExhaustReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricExhaustReference.cs#L13-L31) | Template binding |
| `Universe.GetElapsedSeconds()` / `GetElapsedTime()` / `GetSimulationSpeed()` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2311-L2402) | Simulation clock (`_lastSimStep.NextTime`) and warp factor |
| `Part.MatrixAsmb2VehicleAsmb` / `Asmb2VehicleAsmb` / `PartParent` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L724-L745) | Part-local to vessel assembly |
| `Vehicle.PosAsmbToBody` / `Body2Cce` / `GetSurfaceVelocityCci` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1284-L1288) | Body frame, CCE rotation, wind |
| `Camera.GetPositionEgo(IPosition)` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L236) | Camera-relative vessel position |

## Pitfalls and version notes

- Harmony binds postfix parameters by name. If a game update renames `camera`, `renderer` or
  `frameDeltaTime`, `Apply` throws, and Unscience's patcher logs and skips Pyro.[^scope-exhaust]
- The signature lost its viewport argument in 5438, and in 5554 the stock body only queues `AddPlume`
  records that are updated on worker threads in `EndPlumeGrouping`. Pyro keeps its own main-thread
  `UpdateState` path, so its plumes are not view-culled by `MaxPlumeExtent`, not merged with
  neighbouring plumes, and get no bend target from a parent plume.[^ksa-exhaust-renderer]
- Plumes only update while their vessel is in `Program.VehiclesInFrame`, like stock engines.
- Removing a plume or changing its template drops the instance immediately; there is no shutdown
  tail for a removed plume.
- Pyro's ambient pressure is sampled at the camera (`PhysicalAtmosphereReference`), while stock
  plumes use the vessel's `_environment.AtmosphericPressure`; far from the camera the two can differ.
- The README states 5541 as the build compiled against; the APIs above are unchanged in 5554 apart
  from the stock-side grouping internals.

## See also

- [Isentropic nozzle model](./pyro-isentropic-nozzle-model.md)
- [Scoped per-plume look override](./pyro-scoped-exhaust-look-override.md)
- [Exhaust template editor](./pyro-exhaust-template-editor.md)
- [Thug Life textured quad render pass](./thug-life-textured-quad-render-pass.md) (render-pass hook for comparison)
- [Garry's Torch weld presets](./garrys-torch-weld-animation-and-presets.md) (the preset UI pattern Pyro copies)
- [Pyro sub-mod](../submods/pyro.md)
- [Mod overview](../overview.md)
- [Postfix AddVolumetricExhaustInstances with an isentropic nozzle model](../../../howto/rendering/lights-and-particles/draw-exhaust-plumes-without-an-engine/draw-exhaust-plumes-without-an-engine-2.md) - how-to article demonstrating this feature

[^pyro-patches]: pyro.lib/PyroPatches.cs
[^pyro-submod]: pyro.lib/PyroSubmod.cs
[^plume-emitter]: pyro.lib/PlumeEmitter.cs
[^plume-entry]: pyro.lib/PlumeEntry.cs
[^plume-templates]: pyro.lib/PlumeTemplates.cs
[^plume-template-ids]: pyro.lib/PlumeTemplateIds.cs
[^plume-cycle]: pyro.lib/PlumeCycle.cs
[^rotation-helper]: pyro.lib/RotationHelper.cs
[^preset-manager]: pyro.lib/PlumePresetManager.cs
[^pyro-persistence]: pyro.lib/PyroSubmod.Persistence.cs
[^pyro-tests]: pyro.tests/Program.cs
[^pyro-readme]: pyro/README.md
[^scope-exhaust]: scope/exhaust-plumes.md (authors' integration analysis)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-exhaust-renderer]: KSA.VolumetricExhaustRenderer (decompiled, build 2026.10.10.5554)
[^ksa-exhaust-instance]: KSA.VolumetricExhaustInstance (decompiled, build 2026.10.10.5554)
[^ksa-exhaust-reference]: KSA.VolumetricExhaustReference (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
