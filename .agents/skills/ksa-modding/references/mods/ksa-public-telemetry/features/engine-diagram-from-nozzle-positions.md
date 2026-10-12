---
type: Mod Feature
title: Engine diagram from real nozzle positions
description: Builds a top-down dot diagram of the active stage by enumerating engine controllers and rocket cores from the part tree and projecting each nozzle into vehicle assembly space, ringed by a propellant arc summed from every tank
mod: ksa-public-telemetry
category: vehicles-and-parts
techniques: [imgui-drawlist, part-tree-modules, starmap-lifecycle]
hooks: [starmap StarMapAfterOnFrame, starmap StarMapAfterGui, harmony-postfix KSA.Program.DrawFps]
lifecycle: [frame:after-on-frame, frame:draw-ui]
game_types:
  - KSA.PartTree
  - KSA.EngineController
  - KSA.EngineControllerState
  - KSA.RocketCore
  - KSA.RocketCoreState
  - KSA.Rocket
  - KSA.RocketNozzle
  - KSA.Part
  - KSA.SequenceList
  - KSA.ModuleStateful
  - KSA.Tank
  - KSA.Mole
  - KSA.MoleState
tags: [engines, nozzles, staging, part-tree, propellant, diagram, imgui]
---

The bottom-right gauge is a top-down view of the current stage's engines: one dot per engine, placed
where the nozzles actually sit on the vehicle, white when burning and dark grey when off, armed or
starved, with a 270 degree arc around it showing how much propellant is left.[^kpt-readme] A Falcon-9
style ring of nine, a single vacuum engine, or a side-booster cluster each come out right without any
per-vehicle configuration because the layout is computed from the part tree every frame.

## How it works

Sampling ([Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L375-L512)):

1. **Get the controllers with their states.**
   `ModuleStateful<EngineController, EngineControllerState, EngineControllerGlobalState, EmptyStruct>.TryGetFrom(parts.States, out var controllerStates)`
   returns the typed state list for engine controllers; `controllerStates.ModulesAndStates` enumerates
   `(Module, ref State)` pairs without allocating ([current/decomp/KSA/ModuleStateful.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleStateful.cs#L268-L295),
   [current/decomp/KSA/ModuleStateful.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleStateful.cs#L840)).[^ksa-modulestateful]
2. **Pick the stage to draw.** `parts.SequenceList.ActiveSequence` is the current staging sequence
   ([current/decomp/KSA/SequenceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequenceList.cs#L131)). An engine belongs on the diagram when
   `controller.Sequence <= activeSequence || controller.IsActive`. If nothing qualifies (for example
   before the first engine stage), `DiagramSequencePicker` advances to the nearest sequence ahead that
   has engines, so the pad view already shows the first stage
   ([Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L375-L416)).
3. **Cores of each controller.** `EngineController.Cores` is a `RocketCore[]`
   ([current/decomp/KSA/EngineController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineController.cs#L36-L46)); `parts.RocketCores.GetModulesAndStates(controller.Cores.AsSpan())`
   yields each core with its `RocketCoreState`.[^ksa-enginecontroller]
4. **Skip verniers.** For each controller the sampler finds the largest nozzle exit radius
   (`core.Rocket.Nozzles[i].FxExitRadius`) and drops cores whose largest nozzle is under 40 % of it, so
   small steering or vernier chambers do not clutter the diagram
   ([Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L453-L476), [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L558-L573)).
5. **Per-engine data.** Throttle, chamber pressure and temperature (`Conditions.Core`), mass flow,
   `GetActualThrustTime()`, and a status: `Inactive` (controller off), `Starved`
   (`!IsPropellantAvailable`), `Armed` (throttle 0), `Throttled` or `Nominal` (throttle >= 0.95)
   ([Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L478-L497), [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L514-L534),
   [current/decomp/KSA/RocketCoreState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketCoreState.cs#L9-L19)).[^ksa-rocketcorestate]
6. **Project the nozzles.** For each nozzle of the core, `GetNozzleCentroid` takes
   `nozzle.LocationAsmb` (nozzle position in its part's assembly space), transforms it by
   `float4x4.Pack(nozzle.Parent.MatrixAsmb2VehicleAsmb)` into vehicle assembly space, and averages the
   `Y` and `Z` components; `X` is discarded ([Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L575-L613)). This is
   the same transform the game applies in `RocketNozzle.ResetState` to get
   `ThrustLocationVehicleAsmb`, the ungimballed thrust point ([current/decomp/KSA/RocketNozzle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketNozzle.cs#L278-L289)).[^ksa-rocketnozzle]
   `UpdateState` uses the same matrix but first rotates the location about the gimbal pivot, so the
   mod's dots ignore gimbal deflection ([current/decomp/KSA/RocketNozzle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketNozzle.cs#L291-L329)). Dropping X treats the
   vehicle's X axis as the stack axis. The game's stock engines exhaust along -X of their own assembly
   (`<ExhaustDirection X="-1" Y="0" Z="0" />` on the large vacuum engine,
   [current/Content/Core/CorePropulsionAGameData.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/CorePropulsionAGameData.xml#L17-L24)), so for engines stacked along the root
   part's axis X is the thrust axis and Y/Z the cross-section.[^ksa-corepropulsiona-xml] Which way the
   cross-section faces on screen is not fixed by anything the mod reads, hence the "Diagram rotation"
   slider.
7. **Normalise.** One engine sits at the centre. Otherwise positions are re-centred on their mean and
   divided by the largest radius, so the outermost engine lands on the unit circle. Coincident engines
   (multi-core parts) are fanned onto a small ring around their shared point, and if every engine
   collapsed onto one point the set is laid out on a 0.7 radius circle
   ([Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L615-L750)). Dot size is the exit radius relative to the largest,
   clamped to 0.25..1 ([Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L536-L556)).
8. **Propellant fraction.** `SamplePropellants` walks `parts.Tanks.Modules`, and for each `Mole` in a
   tank reads the live mass from `parts.Moles.States[mole.StatesIdx].Mass` and the capacity from
   `mole.GetStoredMass(mole.ContainerVolume)`, summing both over the whole vehicle
   ([Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L752-L796), [current/decomp/KSA/Tank.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Tank.cs#L80),
   [current/decomp/KSA/Mole.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mole.cs#L13-L55), [current/decomp/KSA/MoleState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MoleState.cs#L5)).[^ksa-mole]

Drawing ([Rendering/EngineClusterPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/EngineClusterPanel.cs#L86-L166), [Rendering/EngineClusterPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/EngineClusterPanel.cs#L285-L412)):

- Plot radius is chosen so the outermost dot plus its own radius fits inside the plate; base dot radius
  comes from half the minimum pairwise separation times `NeighbourFillFraction`, so dense clusters get
  smaller dots.
- Each dot is an `AddCircleFilled`, plus a faint 1.1x halo when burning. The rotation slider is applied
  at draw time; diagram +Y maps to screen up.
- The propellant arc is two `Gfx.Arc` strokes (track and fill) from -135 to +135 degrees, with the fill
  exponentially smoothed.

## Lifecycle and timing

- **Sampling in `frame:after-on-frame`** (`[StarMapAfterOnFrame]`). Module states (`RocketCoreState`,
  `EngineControllerState`, `MoleState`) are main-thread copies the game refreshes when it applies the
  vehicle solver results in `PrepareFrame` (`Parts.UpdateFromTaskResults`,
  [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2529-L2531)), so reading them after the frame gives the values applied this
  frame, while the step dispatched later in `PrepareFrame` runs on the workers with its own
  copies.[^ksa-vehicle]
- **Drawing in `frame:draw-ui`** on the next frame from the snapshot.
- **Why rebuild every frame.** Staging, decoupling and engine shutdown change the set; recomputing a
  few dozen transforms per frame is cheaper than tracking events, and the panel animates count changes
  itself.

## Key code

| Path | Role |
|---|---|
| [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L418-L512) | Controller and core enumeration, filtering, status |
| [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L575-L613) | Nozzle centroid in vehicle assembly space |
| [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L615-L750) | Normalisation and overlap fan-out |
| [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L752-L796) | Propellant fraction from tank moles |
| [Rendering/EngineClusterPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/EngineClusterPanel.cs#L285-L374) | Dot placement and drawing |

## Snippets

```csharp
// Telemetry/TelemetrySampler.cs L426-L433, L461-L491
        if (!ModuleStateful<EngineController, EngineControllerState, EngineControllerGlobalState, EmptyStruct>
                .TryGetFrom(parts.States, out var controllerStates))
        {
            return;
        }

        int activeSequence = EffectiveSequence(parts, controllerStates);
        snapshot.PartCount = parts.Count;
// ...
            var coreEnumerator = parts.RocketCores
                .GetModulesAndStates(controller.Cores.AsSpan())
                .GetEnumerator();

            while (coreEnumerator.MoveNext())
            {
                var core = coreEnumerator.Current;
                ref readonly RocketCoreState coreState = ref core.State;

                controllerThrottle = MathF.Max(controllerThrottle, coreState.Throttle);

                float exitRadius = GetMaxExitRadius(core.Module);
                if (largestExit > 0f && exitRadius < mainEngineThreshold)
                {
                    continue;
                }

                EngineSample sample = default;
                sample.ExitRadius = exitRadius;
                sample.Sequence = controller.Sequence;
                sample.Throttle = coreState.Throttle;
                sample.ChamberPressure = coreState.Conditions.Core.Pressure;
                sample.ChamberTemperature = coreState.Conditions.Core.Temperature;
                sample.MassFlowRate = coreState.MassFlowRate;
                sample.ThrustTimeRemaining = coreState.GetActualThrustTime();
                sample.Status = ClassifyEngine(controllerActive, in coreState);

                GetNozzleCentroid(core.Module, out float x, out float y);
                sample.DiagramX = x;
                sample.DiagramY = y;

```

```csharp
// Telemetry/TelemetrySampler.cs L589-L612
        for (int i = 0; i < nozzles.Length; i++)
        {
            RocketNozzle nozzle = nozzles[i];
            Part? part = nozzle.Parent;
            if (part is null)
            {
                continue;
            }

            float4x4 asmb2Vehicle = float4x4.Pack(part.MatrixAsmb2VehicleAsmb);
            float3 vehicleLocation = nozzle.LocationAsmb.Transform(asmb2Vehicle);

            sumY += vehicleLocation.Y;
            sumZ += vehicleLocation.Z;
            count++;
        }

        if (count == 0)
        {
            return;
        }

        x = sumY / count;
        y = sumZ / count;
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `PartTree.States`, `RocketCores`, `Tanks`, `Moles`, `SequenceList`, `Count` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L56-L74), [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L134) | Module and state lists of the vehicle |
| `ModuleStateful<...>.TryGetFrom`, `StateList.ModulesAndStates`, `GetModulesAndStates`, `States` | [current/decomp/KSA/ModuleStateful.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleStateful.cs#L268-L295), [current/decomp/KSA/ModuleStateful.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleStateful.cs#L840) | Typed module-state access |
| `SequenceList.ActiveSequence` | [current/decomp/KSA/SequenceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequenceList.cs#L131) | Current stage |
| `EngineController.Cores`, `IsActive`, `Sequence` | [current/decomp/KSA/EngineController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineController.cs#L36-L46) | Engines of a controller and their stage |
| `RocketCore.Rocket`, `Rocket.Nozzles` | [current/decomp/KSA/RocketCore.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketCore.cs#L16), [current/decomp/KSA/Rocket.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Rocket.cs#L12) | Nozzles of a core |
| `RocketNozzle.LocationAsmb`, `FxExitRadius`, `Parent` | [current/decomp/KSA/RocketNozzle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketNozzle.cs#L24), [current/decomp/KSA/RocketNozzle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketNozzle.cs#L72), [current/decomp/KSA/ModuleBase.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleBase.cs#L31) | Nozzle position, size and owning part |
| `Part.MatrixAsmb2VehicleAsmb` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L740-L752) | Part assembly to vehicle assembly transform |
| `RocketCoreState.Throttle`, `IsPropellantAvailable`, `MassFlowRate`, `Conditions`, `GetActualThrustTime` | [current/decomp/KSA/RocketCoreState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketCoreState.cs#L9-L60) | Engine status |
| `Tank.Moles`, `Mole.StatesIdx`, `ContainerVolume`, `GetStoredMass`, `MoleState.Mass` | [current/decomp/KSA/Tank.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Tank.cs#L80), [current/decomp/KSA/Mole.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mole.cs#L13-L55), [current/decomp/KSA/MoleState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MoleState.cs#L5) | Propellant mass and capacity |

## Pitfalls and version notes

- **Generic arity.** The `ModuleStateful<EngineController, EngineControllerState, EngineControllerGlobalState, EmptyStruct>`
  spelling must match the game's declaration exactly ([current/decomp/KSA/EngineController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineController.cs#L7)); a change in
  the type parameters breaks compilation, not runtime.
- **Whole-vehicle propellant.** The arc sums every tank (the code comment says so on purpose), so drop
  tanks and upper stages all count; it is not a per-stage gauge. Capacity includes every substance a
  tank holds, oxidiser and fuel alike.
- **Vernier filter is a heuristic** (40 % of the largest exit radius per controller); engines with
  genuinely different nozzle sizes on one controller can disappear.
- **Axis assumption.** If the engines of a stage thrust along the vehicle's Y or Z axis (a vehicle
  built sideways from its root part), their Y/Z positions are no longer a cross-section: engines in
  a row along that axis are spread along a line, and engines that coincide in Y/Z collapse to one
  point, which the overlap fan-out or the 0.7 circle fallback then spreads out.

## See also

- [Live flight telemetry sampling](live-flight-telemetry-sampling.md)
- [Staged intro and outro animation](intro-outro-animation.md)
- [In-flight event detection and callouts](flight-event-detection-and-callouts.md)
- [Draw an engine diagram from nozzle positions and live engine state](../../../howto/vehicles-and-parts/part-geometry/draw-an-engine-diagram-from-nozzle-positions/draw-an-engine-diagram-from-nozzle-positions.md) - how-to article demonstrating this feature

[^kpt-readme]: README.md
[^ksa-modulestateful]: KSA.ModuleStateful (decompiled, build 2026.10.10.5554)
[^ksa-enginecontroller]: KSA.EngineController (decompiled, build 2026.10.10.5554)
[^ksa-rocketcorestate]: KSA.RocketCoreState (decompiled, build 2026.10.10.5554)
[^ksa-rocketnozzle]: KSA.RocketNozzle (decompiled, build 2026.10.10.5554)
[^ksa-mole]: KSA.Mole (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-corepropulsiona-xml]: Content/Core/CorePropulsionAGameData.xml (build 2026.10.10.5554)
