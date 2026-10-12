---
type: Mod Feature
title: Volumetric exhaust plumes with no engine behind them
description: Draw KSA's own volumetric rocket plumes at arbitrary nozzles by building VolumetricExhaustInstance objects from a Core template and submitting them from a Harmony postfix on Vehicle.AddVolumetricExhaustInstances, with gas numbers the mod chooses
mod: ksacars
category: rendering
techniques: [harmony-postfix]
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
  - KSA.ExhaustAxialFade
  - KSA.ExhaustDiamondFade
  - KSA.ExhaustBendTarget
  - KSA.Camera
tags: [rendering, exhaust, plume, volumetric, harmony, postfix, rockets, vfx]
---

When the Eldorado's lift, boost or downforce rockets fire, a KSA rocket flame comes out of each
nozzle, with KSA's own startup flash and shutdown tail. There is no engine part behind it: the mod
builds `VolumetricExhaustInstance` objects from a Core exhaust template, sets their plume shape each
frame from gas numbers it makes up for the throttle, and hands them to KSA's volumetric exhaust
renderer from a postfix on `Vehicle.AddVolumetricExhaustInstances`, beside the craft's real
engines.[^ksacars-buggies][^ksacars-flameshook] The repo reports the plume as seen in game.[^ksacars-notes]

A real engine cannot be kept just for its flame: with no propellant reachable it has no plume, and
any flow it has pushes the craft. A `<DeLavalNozzle>` with no `<Rocket>` is worse:
`AddVolumetricExhaustInstances` dereferences the nozzle's missing rocket.[^ksacars-notes][^ksa-vehicle]

## How it works

`Buggies.Flames(craft, camera, renderer, frameDeltaTime)` calls `Burn` three times, one per set
of nozzles from `BuggyProfile` (`RocketNozzles` pointing down the car's -X, `BoostNozzles` along -Y,
`DownNozzles` along +X), each at one throttle: the lift throttle once the irises are open, 1.0 while
boosting, 0.35 while the downforce rockets fire.[^ksacars-buggies][^ksacars-profile] For each set:

1. **Gas.** `Lift.Flame(throttle)` returns a `FlameGas` sized like Core's auxiliary engine: exit
   radius 0.10 m, area ratio 4, chamber pressure `throttle * 30e5` Pa at 3000 K, exit pressure 4%
   of chamber at 1700 K, gamma 1.21, R = 681 J/(kg K), exhaust velocity 2500 m/s. This only shapes
   the flame; the push is computed elsewhere.[^ksacars-lift] It becomes `GasProperties` and two
   `GasConditions` (chamber inlet and exit).
2. **Environment.** Ambient pressure and air density are scaled from the mod's sea-level ratio at
   the craft; air velocity is the craft's surface velocity rotated into CCE, as KSA does for its
   own nozzles.
3. **Instances.** One per nozzle, created lazily:
   `new VolumetricExhaustReference { Id = "EngineAAuxiliary" }`, `.Load()` (which resolves the
   `VolumetricExhaustTemplate`), then `new VolumetricExhaustInstance(reference)`.[^ksa-exhaustreference][^ksa-exhaust-assets]
4. **Shape.** While burning: `flame.LastPlumeData = RocketNozzle.ComputePlumeData(gas, exhaust,
   inlet, inlet.Pressure, exhaustVelocity, ambient, exitRadius, exitRadius / sqrt(areaRatio),
   RocketDesign.SolveMachNumberFromAreaRatio(gas, areaRatio),
   RocketNozzle.ComputeMinGasVisibilityDensity(template, exitRadius))`. A dying flame keeps its
   last shape.[^ksa-rocketnozzle][^ksa-rocketdesign]
5. **Pose.** The nozzle point (part frame -> vehicle assembly -> body -> CCE) is added to the
   craft's camera-relative position, packed as `float3`; the axis is the set's direction turned the
   same way.
6. **State.** `flame.UpdateState(now, burning, simDt, gas, exhaust, exhaustVelocity, where, axis,
   where, axis, ambient, airVelocity, airDensity)` runs KSA's startup and shutdown transients;
   `simDt` is the frame time times the simulation speed.[^ksa-exhaustinstance]
7. **Submit.** If `flame.IsLive`, `renderer.AddInstance(flame, default, ExhaustAxialFade.NoFadeOut,
   ExhaustDiamondFade.None)`.
8. When the set is out and every instance has burnt down, the array is dropped so the next burn
   starts from cold (with a fresh startup transient).

The engine's own `VolumetricExhaustRenderer.UpdateTestInstances` (its exhaust debug tool) builds
instances the same way, from a template id, `Load()`, `ComputePlumeData` over invented gas numbers
and `UpdateState`, which is what shows the path works without a nozzle. It submits them through its
private plume hierarchy (`_plumeHierarchy.AddSource` inside its own grouping), where the mod uses
the public `AddInstance`.[^ksa-exhaustrenderer]

## Lifecycle and timing

| Moment | Where | Thread |
|---|---|---|
| Patch installed | `[StarMapAllModsLoaded]` -> `FlamesHook.Install` | main |
| Instance list emptied | `Program.OnPreRender` -> `_volumetricExhaustRenderer.UpdateFrameData()` (Program.cs L2376) | main |
| Grouping opened | `BeginPlumeGrouping()` (L2377) | main |
| KSA's nozzles queued, then **postfix** | per vehicle in frame: `vehicle.AddVolumetricExhaustInstances(GetCamera(), _volumetricExhaustRenderer, dtPlayer)` (L2382) | main |
| KSA's plumes merged and submitted | `EndPlumeGrouping()` (L2384) | main |
| Drawn | `Program.Render` -> `RenderGame`: `_volumetricExhaustRenderer.Render` (Program.cs L4891) | main (GPU recording) |

`OnPreRender` runs after the GUI pass, `UpdateGauges`, the light pass and `OnFrameCelestials`, and
right before `Render`.[^ksa-program] `UpdateFrameData` resets `_instanceCount` to zero every frame,
so an instance added from a StarMap hook is either wiped before the render (`StarMapAfterGui`) or
added after it (`StarMapAfterOnFrame`).[^ksa-exhaustrenderer][^ksacars-flameshook] The postfix runs
inside the window once per vehicle in frame, with the camera and renderer KSA is using and the
player's frame time, so positions in the camera's Ego frame are right for this frame. The postfix
only acts for vehicles that are cars; the dictionary lookup returns for every other craft.

Reading `Entry.Rockets`, `Boosting` and `Pressed` here is safe: they are plain fields written in
the `Vehicle.PrepareWorker` prefix earlier in the same frame on the same thread.[^ksacars-buggies]

## Key code

| Path | Role |
|---|---|
| [src/KSACars/Ksa/FlamesHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/FlamesHook.cs) | Postfix on `Vehicle.AddVolumetricExhaustInstances(Camera, VolumetricExhaustRenderer, double)`; never throws; pinned |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L211-L295) | `Flames` and `Burn`: template, plume data, pose, state, submit |
| [src/KSACars/Sim/Lift.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/Lift.cs#L44-L55) | `Flame`: the gas a throttle is drawn from |
| [src/KSACars/Sim/BuggyProfile.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/BuggyProfile.cs#L88-L110) | `RocketNozzles`, `BoostNozzles`, `DownNozzles` (part-frame points) |
| [tests/KSACars.Tests/LiftTests.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tests/KSACars.Tests/LiftTests.cs#L33-L39) | The flame grows with throttle and is out without it |

## Snippets

```csharp
// src/KSACars/Ksa/Buggies.cs L264-L294
        bool any = false;
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

## Game APIs touched

| API | Decompiled source | Used for |
|---|---|---|
| `Vehicle.AddVolumetricExhaustInstances(Camera, VolumetricExhaustRenderer, double)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L5718-L5754) | Patch target; KSA's version queues each nozzle with `AddPlume` |
| `Program.OnPreRender` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2372-L2391) | `UpdateFrameData`, grouping, the per-vehicle loop |
| `VolumetricExhaustRenderer.UpdateFrameData` / `AddInstance(VolumetricExhaustInstance, in ExhaustBendTarget, in ExhaustAxialFade, in ExhaustDiamondFade)` / `Disabled` | [current/decomp/KSA/VolumetricExhaustRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricExhaustRenderer.cs#L765-L845) | Per-frame reset and direct, unmerged submission; `Disabled` is at L446 |
| `VolumetricExhaustRenderer.UpdateTestInstances` | [current/decomp/KSA/VolumetricExhaustRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricExhaustRenderer.cs#L2831-L2891) | KSA's own engine-less plume path (debug tool), the model for this feature |
| `VolumetricExhaustReference.Load()` | [current/decomp/KSA/VolumetricExhaustReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricExhaustReference.cs#L28-L31) | Resolves a template by id |
| `VolumetricExhaustInstance(VolumetricExhaustReference)`, `LastPlumeData`, `Template`, `UpdateState`, `IsLive` | [current/decomp/KSA/VolumetricExhaustInstance.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricExhaustInstance.cs#L57-L170) | The plume object, its shape and transients |
| `RocketNozzle.ComputePlumeData`, `ComputeMinGasVisibilityDensity` | [current/decomp/KSA/RocketNozzle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketNozzle.cs#L254-L342) | Static plume maths from gas conditions |
| `RocketDesign.SolveMachNumberFromAreaRatio` | [current/decomp/KSA/RocketDesign.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketDesign.cs#L168) | Design Mach number for the area ratio |
| `ExhaustAxialFade.NoFadeOut`, `ExhaustDiamondFade.None` | [current/decomp/KSA/ExhaustAxialFade.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ExhaustAxialFade.cs) | No axial or shock-diamond fade |
| `GameSettings.Graphics.MaxVolumetricExhausts` | [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L328) | Instance budget; 0 disables every plume |
| `<VolumetricExhaustTemplate Id="EngineAAuxiliary">` | [current/Content/Core/ExhaustAssets.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/ExhaustAssets.xml#L1331) | The Core template the flames use |

## Pitfalls and version notes

- **No merging.** KSA's own nozzles go through `AddPlume` and are merged by the plume hierarchy at
  `EndPlumeGrouping`; `AddInstance` submits each flame on its own, so neighbouring car flames never
  merge into one plume.[^ksa-exhaustrenderer][^ksacars-notes]
- **Budget and settings.** `AddInstance` returns without drawing when the renderer is `Disabled`
  (`MaxVolumetricExhausts == 0`) or the instance count is at its maximum; `Flames` checks
  `renderer.Disabled` first.[^ksa-exhaustrenderer][^ksa-gamesettings]
- **No exhaust light, no particles, no trail.** KSA's `UpdateExhaustLights`, `SpawnParticles` and
  plume trails iterate the craft's `RocketNozzle` modules, which a car does not have.[^ksa-program]
- **Camera-relative `float3`.** Positions are packed in the renderer camera's Ego frame; computing
  them from a different camera or frame puts the flame in the wrong place.
- **`Parent` can be null.** The mod checks `craft.Parent` before computing air velocity; KSA's
  method dereferences it unconditionally.[^ksa-vehicle]
- **A public patch, pinned.** `FlamesHook.PinTheSignature` puts the target in the mod's API surface
  report so a signature change is a build error; if the patch fails the car still flies, with no
  flame.[^ksacars-flameshook]

## See also

- [Rocket lift, hover, boost and downforce](./rocket-lift-hover-boost-downforce.md) (the push the flames show)
- [Rocket roar from KSA's stock engine sound](./rocket-sound-without-engine.md)
- [Posing subparts at runtime](./pose-subparts-at-runtime.md) (the irises the flames fire through)
- [KSACars overview](../overview.md)
- [Template-based plume instances with RocketNozzle.ComputePlumeData and mod-made gas numbers](../../../howto/rendering/lights-and-particles/draw-exhaust-plumes-without-an-engine/draw-exhaust-plumes-without-an-engine-1.md) - how-to article demonstrating this feature

[^ksacars-flameshook]: src/KSACars/Ksa/FlamesHook.cs
[^ksacars-buggies]: src/KSACars/Ksa/Buggies.cs
[^ksacars-lift]: src/KSACars/Sim/Lift.cs
[^ksacars-profile]: src/KSACars/Sim/BuggyProfile.cs
[^ksacars-notes]: docs/KSA-MODDING-NOTES.md, "An exhaust plume without an engine"
[^ksa-program]: KSA.Program (decompiled)
[^ksa-vehicle]: KSA.Vehicle (decompiled)
[^ksa-exhaustrenderer]: KSA.VolumetricExhaustRenderer (decompiled)
[^ksa-exhaustinstance]: KSA.VolumetricExhaustInstance (decompiled)
[^ksa-exhaustreference]: KSA.VolumetricExhaustReference (decompiled)
[^ksa-rocketnozzle]: KSA.RocketNozzle (decompiled)
[^ksa-rocketdesign]: KSA.RocketDesign (decompiled)
[^ksa-gamesettings]: KSA.GameSettings (decompiled)
[^ksa-exhaust-assets]: Core/ExhaustAssets.xml
