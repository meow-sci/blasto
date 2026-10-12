---
type: Mod Feature
title: Holed tanks that drain through the engine's mole states and throw a painted stream
description: A shell hole under a tank's liquid level drains it at an orifice rate through Mole.ConsumeStored in a Vehicle.PrepareWorker prefix, with the bookkeeping KSA's DepleteConsumables does, and big leaks throw a stream painted before bloom
mod: ksarmory
category: vehicles-and-parts
techniques:
  - harmony-prefix
  - reflection
  - glsl-shader
  - vulkan-buffer
  - post-processing
  - double-buffer-aware-writes
  - module-state-write
  - ref-readonly-mutation
  - tile-bitmask-gather
hooks:
  - starmap StarMapAfterGui
  - harmony-prefix KSA.Program.OnFrameCelestials
  - harmony-prefix KSA.Vehicle.PrepareWorker
  - harmony-prefix KSA.SunbloomRenderer.Render
  - reflection KSA.Tank._template
  - reflection Core.KSADeviceContextEx.Allocator
lifecycle: [frame:draw-ui, frame:celestials, frame:dispatch-solvers, render:bloom]
game_types:
  - KSA.Tank
  - KSA.Mole
  - KSA.MoleState
  - KSA.MoleGlobalState
  - KSA.Liquid
  - KSA.ModuleStateful
  - KSA.PartTree
  - KSA.ResourceManager
  - KSA.SequencePerformanceList
  - KSA.Vehicle
  - KSA.VehicleProperties
  - KSA.FlightComputer
  - KSA.VehicleUpdateData
  - KSA.Rocket
  - KSA.AsmbTankTemplate
  - KSA.CylindricalTankTemplate
  - KSA.ConicalTankTemplate
  - KSA.SphericalTankTemplate
  - KSA.FxDeformation
  - KSA.Deformation.Dent
  - KSA.Part
  - KSA.Camera
  - KSA.Rendering.ComputePipelineWrapper
  - KSA.SunbloomRenderer
tags:
  - resources
  - tanks
  - propellant
  - leaks
  - damage
  - mass-properties
  - double-buffer
  - compute-shader
  - storage-buffer
  - liquid-stream
---

A shell that holes a tank in KSArmory makes it leak. Every tenth of a simulated second the mod finds
where the liquid stands in each holed tank (settled against the acceleration the craft feels, or
floating in a coast), works out what runs out of every hole under that level, and takes that mass
off the tank through KSA's own propellant states, so the craft gets lighter, its engines run dry
sooner and the staging readouts follow. The biggest leaks also throw a visible stream: parcels of
liquid flown ballistically from each hole and painted as a thin, beaded rope into the HDR scene
image before bloom.[^ksarmory-leaks]

The non-obvious part is where the mass is written. KSA's tank contents are `MoleState` structs
that the vehicle worker copies on write and copies back at the next apply, so a write from a
StarMap hook can be lost. The mod queues the debt and pays it from its Harmony prefix on
`Vehicle.PrepareWorker`, then repeats the bookkeeping `Vehicle.DepleteConsumables` does after
emptying tanks (KSA's console `empty` command, `Universe.Empty`, reaches it through `InputEvents`): fill fraction, the `ValuesUpdated` flag, stage performance, mass properties and the
flight computer's copy of the craft.[^ksarmory-attitudehook][^ksa-resourcemanager][^ksa-vehicle]

## How it works

### 1. Which holes leak

Leaks are not started by an event. They come from the shell holes the decal feature already keeps.
`WeaponSystem` calls `BulletHoles.Strike(holed, round)` for any round whose `StruckBody` is a
vehicle, which ray-casts the hull and stores a `Hole(SubPart, Local, CameFrom, Core, Scorch, Seed)`
in the struck sub-part's frame (at most 400, oldest dropped).[^ksarmory-weaponsystem][^ksarmory-bulletholes]
Each measuring pass of `Leaks` groups those holes by tank:[^ksarmory-leaks][^ksarmory-ksaworld]

| Step | Detail |
|---|---|
| Hole to tank | `KsaWorld.TryTankOf(hole.SubPart)`: the first `Tank` module in `part.FullPart.Modules.Get<Tank>()`, else in any of `FullPart.SubParts` |
| Tank to craft | `part.FullPart.Tree.OwningVehicle`, skipped if not alive |
| Tank shape | The private `Tank._template` (an `AsmbTankTemplate`) read by reflection and matched as `SphericalTankTemplate`, `CylindricalTankTemplate` or `ConicalTankTemplate` (radius, length, `DomeHeightFraction`, `WallThickness`, `LocationAsmb`, `GetPaf2Asmb()`), turned into a `TankShape` (a cone with a semi-ellipsoid dome at each end) and fitted to the part's `BoundingBoxPartAsmb`. If the field is missing or of another type, the mod logs once and uses the cylinder filling the part's box.[^ksa-tank][^ksa-cylindricaltanktemplate] |
| Inside the tank? | The hole, moved into the tank's frame, must be inside the shape grown by `SkinShare` (3.5%). A hole in a fairing or raceway on the same part leaks nothing |
| Cache | Shape and point cloud per tank in a `ConditionalWeakTable<Tank, Form>`, so a destroyed tank's entry goes with it |

### 2. Level and flow (`Sim/TankLeak.cs`, no game types)

The physics is game-free and covered by headless tests.[^ksarmory-tankleak]


| Quantity | How |
|---|---|
| Liquid held | Sum of `states[mole.StatesIdx].Mass` over the tank's `Moles` whose `Mole.Liquid` has `StorageDensity > 0`; volume = mass / density. Gas moles are ignored[^ksa-liquid] |
| Felt acceleration | `KsaWorld.FeltAccelerationAsmb`: `v.Asmb2Ego.Inverse() * v.AccelerationBody.Transform(v.Body2Cce)` (what an accelerometer reads), then lagged with a 1.5 s time constant (`TankLeak.Settle`) so an RCS pulse does not slosh the level |
| Floating | Felt acceleration below 0.05 m/s² |
| Surface height | 256 Halton-sequence points spread through the tank's inner shape, projected on "up" and sorted; the surface is where the fill fraction of them lies below. This handles tilted cylinders with domed ends, which no closed form does |
| Jet speed | `sqrt(2 P / rho + 2 a h)` with a fixed ullage `P = 20 kPa` (KSA models no tank pressure); zero for a hole above a settled surface; `sqrt(2 P / rho)` when floating |
| Mass flow | `0.61 x rho x pi r^2 x v`, `r` = the hole's `Core` radius; when floating, times the fill fraction (how often the liquid covers the hole) |

### 3. Owing and paying

- `MeasureTank` takes `min(flow x step, mass)` and `Owe` splits it over the tank's liquid moles by
  mass share into a per-craft `Dictionary<Mole, float>`, capped at what each mole holds, so a craft
  that misses passes never owes more than it has.
- `Leaks.Apply(craft)`, called from the `PrepareWorker` prefix, removes the craft's debts and, for
  each mole, gets `moles.GetModuleAndAllMutableStatesForInitialization(mole)` (a ref to the live
  state) and calls `Module.ConsumeStored(ref State, kg)`, which subtracts the mass and clamps at
  zero.[^ksa-modulestateful][^ksa-mole]
- Then the same three calls as KSA's private `ResourceManager.OnContentsReplaced`
  (`RecomputeTotalFillFraction`, `Moles` global `ValuesUpdated = true`,
  `PerformanceSequences.SetDirty()`) and the two `Vehicle.DepleteConsumables` makes after it
  (`RecomputeMassProperties` on the vehicle's properties, `FlightComputer.ReadUpdatedVehicleConfiguration`).[^ksa-resourcemanager][^ksa-vehicle]
- `Vehicle.Props` is a `ref readonly VehicleProperties` (a struct) returning `ref _props`; the mod
  uses `Unsafe.AsRef(in craft.Props)` to recompute it in place, where `DepleteConsumables` writes
  `_props` directly.[^ksa-vehicle][^ksa-vehicleproperties]
- `ValuesUpdated` matters: `Rocket`'s module update returns early unless a rocket is firing, the
  flag is set or its other change tests pass (Rocket.cs L90), and `Mole.UpdateModules` clears the
  flag again (Mole.cs L48).[^ksa-rocket][^ksa-moleglobalstate]
- A one-time self-check: the first pass after a debt compares the tank's mass with the expected
  `mass - taken` and logs whether "the write held".

### 4. The stream (`Sim/LeakStream.cs`)

Up to `MostStreams = 16` holes, biggest flow first, each own a `LeakJet`; every other hole still
drains but draws nothing.[^ksarmory-leakstream]

| Aspect | Detail |
|---|---|
| Frame | Positions relative to the craft's drawn centre of mass, in axes that do not turn with it; a free parcel's acceleration there is the felt acceleration reversed (`VehicleAsmbDirectionToEcl(craft, FeltAccelerationAsmb(craft))`, negated) |
| Emission | One parcel every `2 s / 31`, followed for 2 s, integrated with the trapezoid rule each step. When the leak stops the tail keeps falling and the jet is dropped once empty |
| Exit point | The hole, through `KsaWorld.TryDrawnPartPointEcl` (the sub-part's `MatrixAsmb2Ego` under `craft.GetMatrixAsmb2Ego(mainCamera)`), moved back along the shell's path by 0.05 m plus how far KSA's dents pushed the skin in there (the dent list from `PartTree.Dents.EnsureBuilt`, each a `(1 - t^2)^2` bump, re-read about once a second)[^ksa-fxdeformation] |
| Exit velocity | Outward along the shell's incoming direction at the jet speed |
| Thickness | Exit radius `r x sqrt(0.61)`; each point thins as `1 / sqrt(v)`; whole for 30 diameters, then drops |

### 5. Drawing it (`CloudPass.Streams`, `KSArmoryLeak.comp`)

The pass infrastructure (hook, pipeline wrapper, reflected allocator, hazard barriers) is described
in [Compute pass before bloom](./compute-pass-before-bloom.md); the leak-specific parts are:[^ksarmory-cloudpass]

1. **Buffer.** One host-visible, coherent storage buffer of 64-byte `StreamSegment`s (`Head`,
   `Tail`: pixel x, y, device depth, metres along; `Look`: half-widths in pixels, metres it runs
   whole, seed; `Flow`: seconds out at each end, jet radius), sized
   `frames in flight x 4 views x 16 x 31` segments. A view's slice is
   `(frameIndex % frames) x 4 + per-frame view counter`.
2. **Projection on the CPU.** Each stream point is made camera-relative (`- camera.PositionEcl`) and
   projected once with `Camera.EgoToClipDouble`; its pixel width comes from projecting a
   perpendicular offset of the point's radius. Segments are clipped to the near plane, and the
   union of their pixel bounds becomes the dispatch rectangle.[^ksa-camera]
3. **Dispatch.** A 20-byte push (`First`, `Count`, `OriginX`, `OriginY`, `Clock`) and
   `ceil(w/16) x ceil(h/16)` groups over that rectangle only, after a `Hazard` barrier when anything
   was painted before it.
4. **Shader.** Each 16x16 workgroup first gathers the segments whose padded bounds touch its tile
   into shared memory (up to 256), then each pixel finds the nearest segment centreline, rejects it
   behind scene depth (reverse-Z, `z < sceneZ x 0.9995`), and keys beads, sway and drops on
   `Clock - secondsOut`, the time that liquid left the hole, so the pattern travels with the liquid.
   The result darkens and tints the existing pixel towards the edges and adds a flickering glint,
   with a 1-pixel minimum half-width so a far stream stays a thread.[^ksarmory-leak-comp]

`Leaks.Clock` is simulated seconds wrapped at 600, so the pattern freezes in a pause and slows in
slow motion.

## Lifecycle and timing

| Moment | Where (build 5554) | What happens |
|---|---|---|
| A shell hits | Inside `StepSimulation`: `WeaponSystem.UpdateRounds` -> `Detonate`, WeaponSystem.cs L3873 | `BulletHoles.Strike` stores the hole[^ksarmory-weaponsystem] |
| `frame:draw-ui` (or `frame:celestials` with the HUD hidden) | `StepOnce` -> `StepSimulation` tail, `Leaks.Update(_lastSimStep)` (KSArmoryMod.cs L945); `Leaks.Enabled = _config.TankLeaks` in the AfterGui body (L539) | Every 0.1 s of sim time: measure, owe. Every frame: fly and place streams[^ksarmory-mod] |
| `frame:dispatch-solvers` of the next frame | `Universe.ExecuteNextVehicleSolvers` -> `Vehicle.PrepareWorker` (Vehicle.cs L2396); the mod's prefix calls `Leaks.Apply(__instance)` (AttitudeHook.cs L355-L357) | Debt paid into the live mole states before the worker snapshot |
| `render:bloom` | Harmony prefix on `SunbloomRenderer.Render` -> `CloudPass.Record` -> `Streams` (CloudPass.cs L550; L411 when the cloud pass is off) | Streams painted after the ground marks, rings and hull holes and before the sky effects, clouds, tracers and flash |

Why the prefix (the window itself is described in
[PrepareWorker prefix command window](./prepareworker-prefix-command-window.md)):

- `UpdateData.Prepare` ends with `Updaters.Prepare()` (VehicleUpdateData.cs L103), which copies the
  live mole global state into the worker (`_newGlobalState = ...GlobalState`, ModuleStateful.cs
  L785).[^ksa-vehicleupdatedata][^ksa-modulestateful]
- The worker's states are copy-on-write: the first `GetNewStates()` (ModuleStateful.cs L685)
  copies the live mole states into its own buffer. At the next apply,
  `Vehicle.UpdateFromTaskResultsUnsynchronized` calls `Parts.UpdateFromTaskResults` (Vehicle.cs
  L2531) -> `UpdateBulkStates` (ModuleStateful.cs L383-L412), which always copies the global state
  back and copies the new states over the live ones whenever the worker wrote any, for example when
  an engine drew propellant that step.[^ksa-vehicle][^ksa-parttree]
- So a mass taken off from a StarMap hook, while the worker runs, races its copy and is overwritten
  whenever the worker wrote any mole state that step, and `ValuesUpdated` is always overwritten. Written in the prefix, both are in
  the live state the worker copies from. The mass recompute is likewise done before the step
  integrates.
- Measuring is on the simulation clock, ten times a second, because every payment costs a
  mass-properties recompute and a flight-computer configuration rebuild on that craft.
- Streams are placed in the step, not in the render hook, because they need the main camera's
  `GetMatrixAsmb2Ego` for the craft as drawn this frame, and they integrate on `_lastSimStep`.

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/Leaks.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Leaks.cs#L126-L180) | `Update`, `Clear`, `Apply` (the engine writes) |
| [src/KSArmory/Ksa/Leaks.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Leaks.cs#L182-L343) | `Measure`, `MeasureTank`: group holes, level, flow, self-check |
| [src/KSArmory/Ksa/Leaks.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Leaks.cs#L345-L449) | Dent inset, `FormOf` (tank shape), `Owe` |
| [src/KSArmory/Ksa/Leaks.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Leaks.cs#L459-L550) | `PlaceStreams`, `Fly`, `Outward` |
| [src/KSArmory/Sim/TankLeak.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TankLeak.cs) | `TankShape`, Halton fill points, surface height, settling, flow and jet speed |
| [src/KSArmory/Sim/LeakStream.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/LeakStream.cs) | Stream constants and `LeakJet` (parcels, drawing points) |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L2786-L2903) | `FeltAccelerationAsmb`, `TryTankShape` (reflects `Tank._template`), `TryTankOf` |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L3168-L3188) | `DentsOn`: KSA's dents on a craft |
| [src/KSArmory/Ksa/AttitudeHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/AttitudeHook.cs#L355-L357) | The call from the `PrepareWorker` prefix |
| [src/KSArmory/Ksa/BulletHoles.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BulletHoles.cs#L52-L73) | `Strike`: where the holes come from |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L1350-L1575) | `StreamSegment`, `StreamPush`, `Streams`, `RecordStreams`, `BuildStreamBuffer`, `BuildStreams` |
| [src/KSArmory/Shaders/KSArmoryLeak.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryLeak.comp) | Tile gather, depth test, beads, sway, drops, tube shading |
| [src/KSArmory/Sim/Config.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Config.cs#L183-L188) | `TankLeaks` setting (default on)[^ksarmory-config] |
| [tests/KSArmory.Tests/TankLeakTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/TankLeakTests.cs), [tests/KSArmory.Tests/LeakStreamTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/LeakStreamTests.cs) | Headless tests of the game-free halves[^ksarmory-tankleak-tests][^ksarmory-leakstream-tests] |

## Snippets

```csharp
// src/KSArmory/Ksa/Leaks.cs L158-L180
    /// <summary>
    /// Takes what a craft's tanks owe off them. Called from <see cref="AttitudeHook"/>'s prefix, the
    /// one window where tank state can be written without the worker copying over it.
    /// </summary>
    public static void Apply(Vehicle craft)
    {
        if (_owed.Count == 0 || !_owed.Remove(craft, out Dictionary<Mole, float>? owed)) return;

        PartTree parts = craft.Parts;
        var moles = parts.Moles;

        foreach ((Mole mole, float kg) in owed)
        {
            var held = moles.GetModuleAndAllMutableStatesForInitialization(mole);
            held.Module.ConsumeStored(ref held.State, kg);
        }

        parts.RecomputeTotalFillFraction();
        moles.GetMutableGlobalStateForInitialization().ValuesUpdated = true;
        parts.PerformanceSequences.SetDirty();
        Unsafe.AsRef(in craft.Props).RecomputeMassProperties(parts.SubstanceStores, parts.Moles.States);
        craft.FlightComputer.ReadUpdatedVehicleConfiguration(craft);
    }
```

```csharp
// current/decomp/KSA/ResourceManager.cs L468-L487
	public static void DepleteAllTanks(PartTree parts)
	{
		if (ModuleStateful<Mole, MoleState, MoleGlobalState, EmptyStruct>.TryGetFrom(parts.States, out ModuleStateful<Mole, MoleState, MoleGlobalState, EmptyStruct>.StateList typeStates))
		{
			ModuleList.ModuleEnumerator<ISubstanceStore> enumerator = parts.Modules.GetUsing<ISubstanceStore>().GetEnumerator();
			while (enumerator.MoveNext())
			{
				ISubstanceStore current = enumerator.Current;
				current.DepleteAll(typeStates);
			}
		}
		OnContentsReplaced(parts);
	}

	private static void OnContentsReplaced(PartTree parts)
	{
		parts.RecomputeTotalFillFraction();
		parts.Moles.GetMutableGlobalStateForInitialization().ValuesUpdated = true;
		parts.PerformanceSequences.SetDirty();
	}
```

```csharp
// src/KSArmory/Sim/TankLeak.cs L233-L256
    public static double FlowKgPerSecond(double depth, double feltAcceleration, double density, double holeRadius,
                                         double fill)
    {
        if (!(density > 0.0) || !(holeRadius > 0.0) || !(fill > 0.0)) return 0.0;

        double speed = JetSpeed(depth, feltAcceleration, density);
        double wetted = Weightless(feltAcceleration) ? Math.Min(fill, 1.0) : 1.0;
        return DischargeCoefficient * density * Math.PI * holeRadius * holeRadius * speed * wetted;
    }

    /// <summary>
    /// How fast the liquid leaves a hole, in m/s — Bernoulli, <c>√(2·P/ρ + 2·a·h)</c>: the tank's
    /// pressure, and the weight of the liquid over the hole while it is settled; nothing from a hole
    /// above a settled surface.
    /// </summary>
    public static double JetSpeed(double depth, double feltAcceleration, double density)
    {
        if (!(density > 0.0)) return 0.0;

        double pressure = 2.0 * UllagePascals / density;
        if (Weightless(feltAcceleration)) return Math.Sqrt(pressure);

        return depth > 0.0 ? Math.Sqrt(pressure + (2.0 * feltAcceleration * depth)) : 0.0;
    }
```

```glsl
// src/KSArmory/Shaders/KSArmoryLeak.comp L108-L140
        Segment s = segments[first + tileList[k]];

        vec2 a = s.Head.xy;
        vec2 ab = s.Tail.xy - a;
        float len2 = dot(ab, ab);
        float t = len2 > 1.0e-6 ? clamp(dot(p - a, ab) / len2, 0.0, 1.0) : 0.0;

        // When the liquid here left the hole: its own clock, which every pattern is a function of.
        float out1 = mix(s.Flow.x, s.Flow.y, t);
        float left = pc.Clock - out1;
        float seed = s.Look.w;
        float along = mix(s.Head.w, s.Tail.w, t);
        float whole = s.Look.z;
        float grown = clamp(along / max(whole, 1.0e-3), 0.0, 1.0);

        // Beads: the jet swelling and narrowing, more as it runs.
        float bead = 1.0 + (0.12 + 0.18 * grown) * sin(6.2831853 * BeadsPerSecond * left + seed)
                         + 0.15 * (Noise(left * 7.0 + seed) - 0.5);
        float trueHalf = mix(s.Look.x, s.Look.y, t) * bead;
        float halfW = max(trueHalf, LeastHalfWidth);

        // Sway: the stream wandering from side to side, from nothing at the hole.
        vec2 across = len2 > 1.0e-6 ? vec2(-ab.y, ab.x) / sqrt(len2) : vec2(0.0);
        float sway = halfW * 1.4 * grown * (sin(6.2831853 * SwaysPerSecond * left + seed * 3.1)
                                            + 0.5 * (Noise(left * 3.0 + seed * 1.7) - 0.5));
        vec2 centreline = a + ab * t + across * sway;
        float lateral = dot(p - centreline, across);
        float d = length(p - centreline);
        if (d > halfW + 1.5) continue;

        // Depth varies linearly across the screen along a line, so it can be interpolated in pixels.
        float z = mix(s.Head.z, s.Tail.z, t);
        if (z < sceneZ * 0.9995) continue;
```

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `Mole.ConsumeStored(ref MoleState, float)`, `Mole.Liquid`, `StatesIdx`, `UpdateModules` | [current/decomp/KSA/Mole.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mole.cs#L89-L103) | Subtract the owed mass from one mole state, clamped at zero (L89); `ValuesUpdated` cleared per step (L48) |
| `StateList.GetModuleAndAllMutableStatesForInitialization`, `GetMutableGlobalStateForInitialization`, `States`; `StateUpdater.Prepare`, `GetNewStates`; `StateList.UpdateBulkStates` | [current/decomp/KSA/ModuleStateful.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleStateful.cs#L473-L481) | Live state refs (L473, L479); worker snapshot (L777-L786), copy-on-write (L685), copy-back (L383) |
| `MoleGlobalState.ValuesUpdated` | [current/decomp/KSA/MoleGlobalState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MoleGlobalState.cs#L7) | Forces the rocket propellant update ([current/decomp/KSA/Rocket.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Rocket.cs#L90) L90) |
| `PartTree.Moles`, `RecomputeTotalFillFraction`, `PerformanceSequences`, `SubstanceStores`, `Dents`, `UpdateFromTaskResults` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L1044-L1047) | Mole list (L70), fill fraction (L1044), dents (L54), copy-back (L1195) |
| `ResourceManager.DepleteAllTanks`, `OnContentsReplaced` (private) | [current/decomp/KSA/ResourceManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ResourceManager.cs#L468-L487) | The bookkeeping the mod repeats |
| `SequencePerformanceList.SetDirty` | [current/decomp/KSA/SequencePerformanceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequencePerformanceList.cs#L74) | Stage delta-v readouts recompute[^ksa-sequenceperformancelist] |
| `Vehicle.DepleteConsumables`, `Props` (`ref readonly`), `PrepareWorker`, `UpdateFromTaskResultsUnsynchronized`, `AccelerationBody`, `Body2Cce`, `Asmb2Ego`, `GetMatrixAsmb2Ego(Camera)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3208-L3213) | The engine's own drain path (L3208), props (L541), prefix target (L2396), apply (L2531), felt acceleration (L573) |
| `VehicleProperties.RecomputeMassProperties` | [current/decomp/KSA/VehicleProperties.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleProperties.cs#L99-L107) | Total and propellant mass, inverse inertia, bounding sphere |
| `FlightComputer.ReadUpdatedVehicleConfiguration` | [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L311-L328) | Rebuilds the computer's mass, centre of mass, gimbal and thruster lists[^ksa-flightcomputer] |
| `VehicleUpdateData.Prepare` | [current/decomp/KSA/VehicleUpdateData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateData.cs#L84-L104) | `Updaters.Prepare()` at L103 |
| `Tank.Moles`, `StorageVolume`, `_template` (private) | [current/decomp/KSA/Tank.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Tank.cs#L78-L82) | Contents, capacity, shape template |
| `Liquid.StorageDensity` | [current/decomp/KSA/Liquid.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Liquid.cs#L10) | Volume from mass |
| `CylindricalTankTemplate.Length`, `OuterRadius`, `DomeHeightFraction`, `WallThickness` (and the conical and spherical siblings) | [current/decomp/KSA/CylindricalTankTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CylindricalTankTemplate.cs#L9-L18) | Tank shape for the level |
| `FxDeformation.VehicleDents.EnsureBuilt`, `Dents`; `KSA.Deformation.Dent` | [current/decomp/KSA/FxDeformation.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FxDeformation.cs#L77) | How far the skin is pushed in at a hole |
| `Camera.EgoToClipDouble`, `EgoToEcl` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L277) | Projecting stream points (L277); drawn hole position (L223) |

## Pitfalls and version notes

- **Write in the prefix, not a hook.** Mole states are copy-on-write in the worker and copied back
  at apply, and the global state is always copied back, so a hook write can vanish. The mod's
  self-check log ("the write held" / "did NOT hold") exists to catch a KSA change here.
- **Do all of the bookkeeping.** `ConsumeStored` alone changes the number but not the craft's mass,
  fill fraction, stage readouts or the flight computer's mass model. KSA's own
  `DepleteConsumables` shows the full list; the five calls are copied from it.
- **`Unsafe.AsRef` on a `ref readonly` property.** It works because `Props` returns `ref _props`.
  If KSA ever changes it to return by value, `in craft.Props` binds to a temporary and the recompute
  silently does nothing.
- **Reflection on `Tank._template`.** If it moves, every tank is taken as the cylinder filling its
  part's box (one warning). Holes in a spherical tank's corners then count as inside.
- **No tank pressure in KSA.** The 0.2 bar ullage is the mod's choice: low enough that depth and
  pull still matter, high enough that a coasting craft leaks at all.
- **Cost scales with leaking craft.** Each payment allocates a new `VehicleConfigInfo` and walks the
  gimbals and thrusters, which is why measuring runs at 10 Hz, not per frame.
- **Streams are placed for the main camera.** `TryDrawnPartPointEcl` and `TryDrawnCentreEcl` use
  `Program.GetMainCamera()`; a camera window reuses the same Ecl points, although `KsaWorld` notes
  that other cameras can draw a craft metres away from where the main one does.
- **Caps.** 16 streams (biggest flow first), 31 segments each, 256 segments gathered per tile,
  4 views a frame before buffer slices wrap. Holes are capped at 400 by the decal feature, and
  turning bullet holes off also ends every leak (`Update` clears when `BulletHoles.Count == 0`).
- **Draw gating.** With the cloud pass on, `Streams` runs only after the `NearbyCelestial` and
  weather-shadow-buffer checks in `CloudPass.Record`; with it at zero it runs from the plain path.
  Either way it is painted under the clouds and before tracers.
- **Saves.** `Leaks.Clear()` runs from the mod's save-load detection, dropping streams and unpaid
  debts.[^ksarmory-mod]

## See also

- [Bullet-hole decals](./bullet-hole-decals.md)
- [Compute pass before bloom](./compute-pass-before-bloom.md)
- [Per-part store mass](./per-part-store-mass.md)
- [Compute shell tracers](./compute-shell-tracers.md)
- [Blast damage and part health](./blast-damage-and-part-health.md)
- [Detect save load with OnGameLoaded](./detect-save-load-ongameloaded.md)
- [Read a tank's shape and liquid level](../../../howto/vehicles-and-parts/modules-and-resources/read-live-part-module-state/read-live-part-module-state-2.md) - how-to article demonstrating this feature
- [Drain a tank through its mole states and recompute mass](../../../howto/vehicles-and-parts/modules-and-resources/refill-or-drain-resources/refill-or-drain-resources-3.md) - how-to article demonstrating this feature
- [Write mole (tank) state where the worker will not overwrite it](../../../howto/vehicles-and-parts/vehicles/change-vehicles-without-racing-the-solver/change-vehicles-without-racing-the-solver-6.md) - how-to article demonstrating this feature

[^ksarmory-leaks]: src/KSArmory/Ksa/Leaks.cs
[^ksarmory-tankleak]: src/KSArmory/Sim/TankLeak.cs
[^ksarmory-leakstream]: src/KSArmory/Sim/LeakStream.cs
[^ksarmory-leak-comp]: src/KSArmory/Shaders/KSArmoryLeak.comp
[^ksarmory-cloudpass]: src/KSArmory/Ksa/CloudPass.cs
[^ksarmory-attitudehook]: src/KSArmory/Ksa/AttitudeHook.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-bulletholes]: src/KSArmory/Ksa/BulletHoles.cs
[^ksarmory-weaponsystem]: src/KSArmory/Ksa/WeaponSystem.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-config]: src/KSArmory/Sim/Config.cs
[^ksarmory-tankleak-tests]: tests/KSArmory.Tests/TankLeakTests.cs
[^ksarmory-leakstream-tests]: tests/KSArmory.Tests/LeakStreamTests.cs
[^ksa-mole]: KSA.Mole (decompiled, build 2026.10.10.5554)
[^ksa-modulestateful]: KSA.ModuleStateful (decompiled, build 2026.10.10.5554)
[^ksa-moleglobalstate]: KSA.MoleGlobalState (decompiled, build 2026.10.10.5554)
[^ksa-rocket]: KSA.Rocket (decompiled, build 2026.10.10.5554)
[^ksa-parttree]: KSA.PartTree (decompiled, build 2026.10.10.5554)
[^ksa-resourcemanager]: KSA.ResourceManager (decompiled, build 2026.10.10.5554)
[^ksa-sequenceperformancelist]: KSA.SequencePerformanceList (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-vehicleproperties]: KSA.VehicleProperties (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatedata]: KSA.VehicleUpdateData (decompiled, build 2026.10.10.5554)
[^ksa-tank]: KSA.Tank (decompiled, build 2026.10.10.5554)
[^ksa-liquid]: KSA.Liquid (decompiled, build 2026.10.10.5554)
[^ksa-cylindricaltanktemplate]: KSA.CylindricalTankTemplate (decompiled, build 2026.10.10.5554)
[^ksa-fxdeformation]: KSA.FxDeformation (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
