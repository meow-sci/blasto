---
type: Mod Feature
title: Part and module state reads
description: Reading engines, tanks, solid motors, batteries, power, RCS, solar panels, docking ports, decouplers, animations and the part tree through KSA's vehicle-wide module lists and struct-of-arrays state lists
mod: gatos
category: vehicles-and-parts
techniques: [starmap-lifecycle, harmony-prefix, immutable-snapshot, conditional-weak-table-cache]
hooks: [harmony-prefix KSA.GameViewport.OnFrame, starmap StarMapBeforeGui]
lifecycle: [frame:viewports, frame:draw-ui]
game_types:
  - KSA.PartTree
  - KSA.ModuleList
  - KSA.ModuleStateful
  - KSA.EngineController
  - KSA.EngineControllerState
  - KSA.Tank
  - KSA.Mole
  - KSA.SolidMotor
  - KSA.Battery
  - KSA.ThrusterController
  - KSA.SolarPanel
  - KSA.Generator
  - KSA.DockingPort
  - KSA.Decoupler
  - KSA.KeyframeAnimationModule
  - KSA.Part
tags: [telemetry, modules, part-tree, engines, tanks, srb, solar, docking, caching]
---

Per-part data in KSA is not stored on `Part` objects. Each vehicle's `PartTree` (`vehicle.Parts`)
keeps a typed list of module instances per module class and, for stateful modules, a
struct-of-arrays state list. gatOS's readers show the general recipe for reading any module's
live state, plus how to cache structural facts per vehicle so the per-sample cost stays flat.

## How it works

**1. Enumerate modules of one type.** `vehicle.Parts.Modules.Get<T>()` returns every module of
type `T` across the whole vehicle; the index in that span is the stable ordinal gatOS exposes as
`engines/<n>`, `solar/<n>`, `docking/<n>` and so on.

**2. Read per-module state.** Stateful modules derive from
`ModuleStateful<TModule, TState, TGlobal, TFx>`. Their runtime state lives in
`vehicle.Parts.States`; `ModuleStateful<...>.TryGetFrom(vehicle.Parts.States, out var states)`
yields the typed list and `states.GetState(module)` returns a `ref readonly TState`
([gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs#L967-L974)). Some lists are pre-resolved fields
on `PartTree`: `Moles`, `RocketCores`, `Batteries`.[^ksa-parttree]

| `/sim` family | Module list | State and members read |
|---|---|---|
| `engines/<n>` | `Get<EngineController>()` | `IsActive`, `VacuumData.ThrustMax`/`MassFlowRateMax` (Isp derived), `MinimumThrottle`; `EngineControllerState.CommandThrottle`, `IsPropellantAvailable` |
| `tanks/<r>` | `Get<Tank>()` then `tank.Moles` | `Parts.Moles.GetState(mole).Mass`, `mole.GetStoredMass(mole.ContainerVolume)`, `mole.FilledFraction(in state)`, `SubstancePhase.Name` |
| `srb/<n>` | `Parts.RocketCores.Modules` filtered to `SolidMotor` | `Stack.Segments`, grain mole mass via `Moles.States[grain.StatesIdx]`, initial and unburnable grain mass |
| `battery` | `Parts.Batteries` | `GetState(b).Charge`, `MaximumCapacity` |
| `power` | `PowerConsumer`, `SolarPanel`, `Generator` states | instantaneous W |
| `rcs/<n>` | `Get<ThrusterController>()` | `IsActive`, `ThrusterControllerState` |
| `solar/<n>` | `Get<SolarPanel>()` | `SolarPanelState.{Produced,IsOccluded,SunAoA,SunEfficiency}` |
| `docking/<n>` | `Get<DockingPort>()` | `Docked`, `DockedToPart.Id`, `PushoffImpulse` |
| `decouplers/<n>` | `Get<Decoupler>()` | `IsActive` (fired), `IsEnabled` |
| `animations/<n>` | `Get<KeyframeAnimationModule>()` | `TimeGoal`, `Shared.Duration`, state `TimeCurrent`/`DeploymentState` |
| `parts/<n>` | `Parts.Parts` (top level) and `Part.SubParts` | `InstanceId`, `Id`, `DisplayName`, `Template.Id`, `PartParent`, `PositionVehicleAsmb` |

**3. Cache structural facts per vehicle.** Which animation deploys which solar panel or light, and
the part list, only change when the vehicle is edited. `AnimationLinks` and `PartsReader` keep a
`ConditionalWeakTable<Vehicle, Entry>` (collected with the vehicle, no pruning needed) and rebuild
only when a module or part **count** changes or every 10 sim-seconds as a backstop for
count-preserving edits ([gatOS.GameMod/Game/Ksa/Readers/AnimationLinks.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/AnimationLinks.cs#L50-L60),
[gatOS.GameMod/Game/Ksa/Readers/PartsReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/PartsReader.cs#L38-L50)).

**4. Allocate lazily.** The SRB pass allocates nothing until it finds a `SolidMotor`, since most
vessels carry none ([gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs#L559-L570)).

## Lifecycle and timing

Same slot as the flight-state reads: main thread, after `Universe.ApplyVehicleSolvers` has called
`Parts.UpdateFromTaskResults` for each vehicle, so module states are the finished step's values
([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2530-L2531)). The caches are game-thread only (the sampler
is the sole caller), so the weak tables need no locking. The parts list is behind its own gate
(`telemetry_vessel_parts`) because it feeds the weld and IVA anchor pickers rather than flight
programs.

## Key code

| Path | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs#L453-L493) | engines: module list plus state list |
| [gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs#L516-L536) | tanks via moles |
| [gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs#L559-L641) | solid rocket motors and grain segments |
| [gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs#L705-L864) | RCS, solar, generators, lights, docking |
| [gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs#L883-L892) | decouplers |
| [gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs#L932-L974) | animations and the `TryStates` helper |
| [gatOS.GameMod/Game/Ksa/Readers/PartsReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/PartsReader.cs) | cached part and subpart tree |
| [gatOS.GameMod/Game/Ksa/Readers/AnimationLinks.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/AnimationLinks.cs) | cached animation to solar/light links |

## Snippets

```csharp
// gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs L453-L478
    private static List<EngineSnapshot> SampleEngines(Vehicle vehicle, bool detail)
    {
        var modules = vehicle.Parts.Modules.Get<EngineController>();
        var engines = new List<EngineSnapshot>(modules.Length);
        if (detail && modules.Length > 0
            && TryStates<EngineController, EngineControllerState, EngineControllerGlobalState, EmptyStruct>(
                vehicle, out var states))
        {
            for (var i = 0; i < modules.Length; i++)
            {
                var engine = modules[i];
                ref readonly var st = ref states.GetState(engine);
                engines.Add(BuildEngine(engine, i,
                    Sanitize.Finite(st.CommandThrottle), st.IsPropellantAvailable));
            }
        }
        else
        {
            // detail off (or no state list): ThrottleCmd/PropellantAvailable keep the record
            // defaults, exactly like the pre-GP3 core pass.
            for (var i = 0; i < modules.Length; i++)
                engines.Add(BuildEngine(modules[i], i, throttleCmd: 0, propellantAvailable: false));
        }

        return engines;
    }
```

```csharp
// gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs L516-L536
    private static List<TankSnapshot> SampleTanks(Vehicle vehicle)
    {
        var tanks = new List<TankSnapshot>();
        var moleStates = vehicle.Parts.Moles;
        foreach (var tank in vehicle.Parts.Modules.Get<Tank>())
        {
            foreach (var mole in tank.Moles)
            {
                ref readonly var state = ref moleStates.GetState(mole);
                double amount = state.Mass;
                double capacity = mole.GetStoredMass(mole.ContainerVolume);
                tanks.Add(new TankSnapshot(mole.SubstancePhase.Name,
                    Sanitize.Finite(amount), Sanitize.Finite(capacity))
                {
                    Fraction = Math.Clamp(Sanitize.Finite(mole.FilledFraction(in state)), 0, 1),
                });
            }
        }

        return tanks;
    }
```

```csharp
// gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs L967-L974

    /// <summary>The struct-of-arrays state list for a module type, or false when none on this vehicle.</summary>
    private static bool TryStates<TModule, TState, TGlobal, TFx>(
        Vehicle vehicle, out ModuleStateful<TModule, TState, TGlobal, TFx>.StateList states)
        where TModule : ModuleStateful<TModule, TState, TGlobal, TFx>, IDisposable
        where TState : unmanaged
        where TGlobal : unmanaged
        where TFx : struct
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `PartTree.Modules`, `States`, `Moles`, `RocketCores`, `Batteries` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L62-L78) | module and state lists |
| `ModuleStateful<...>.TryGetFrom` | [current/decomp/KSA/ModuleStateful.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleStateful.cs#L840-L850) | typed state list from `Parts.States` |
| `EngineController.VacuumData`, `MinimumThrottle`, `IsActive` | [current/decomp/KSA/EngineController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineController.cs#L38-L44) | engine data |
| `Mole.GetStoredMass`, `ContainerVolume`, `FilledFraction` | [current/decomp/KSA/Mole.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mole.cs#L53-L124) | tank capacity and fill |
| `Part.InstanceId`, `DisplayName`, `PositionVehicleAsmb` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L574-L708) | part tree |

## Pitfalls and version notes

- Rev 4992 introduced solid motors: SRB grain is not a `Tank`, so it is absent from `tanks/` while
  `Vehicle.PropellantMass` includes it; gatOS added `srb/` for that (repo AGENTS.md).
- Content drift moves ordinals: in 5482 several nosecones and interstage bridges gained tanks and
  two bridges lost their decoupler, so `tanks/<n>` and `decouplers/<n>` shift on affected craft.
- `Decoupler` became a multi-instance component module in 5348; stock parts still carry one per part.
- `ModuleStateful.TryGetFrom` returns false when a vehicle has no module of that type; check it
  rather than assuming a state list exists.
- All of these members were found unchanged in the 5554 decomp; gatOS's anchors were last verified
  at 5482.

## See also

- [Vessel flight-state reads](vessel-flight-state-reads.md)
- [Telemetry sampler and snapshot store](telemetry-sampler-snapshot.md)
- [Ignition, staging and module actuators](ignition-staging-and-module-actuators.md)
- [Part light cone control](light-cone-control.md)
- [Welds that pin one vessel to another](welds-vessel-anchoring.md)
- [gatOS overview](../overview.md)
- [Read engines, tanks, solid motors and parts with Modules.Get and TryGetFrom, cached per vehicle](../../../howto/vehicles-and-parts/modules-and-resources/read-live-part-module-state/read-live-part-module-state-1.md) - how-to article demonstrating this feature

[^ksa-parttree]: KSA.PartTree (decompiled, build 2026.10.10.5554)
