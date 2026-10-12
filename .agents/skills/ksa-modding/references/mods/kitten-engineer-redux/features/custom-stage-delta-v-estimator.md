---
type: Mod Feature
title: Custom stage delta-v estimator
description: A self-contained per-stage rocket-equation estimate that walks staging sequences, subtracts decoupled subtrees, sums engine thrust at a chosen ambient pressure and claims each reachable tank once through the resource manager's consumption order
mod: kitten-engineer-redux
category: vehicles-and-parts
techniques: [stage-model-recompute]
hooks: [harmony-postfix KSA.VehicleEditor.OnDrawUi, harmony-postfix KSA.Vehicle.OnDrawUi]
lifecycle: [frame:draw-ui, editor:draw-ui]
game_types:
  - KSA.PartTree
  - KSA.SequenceList
  - KSA.Sequence
  - KSA.Part
  - KSA.ModuleList
  - KSA.Decoupler
  - KSA.InertMass
  - KSA.Tank
  - KSA.Mole
  - KSA.Liquid
  - KSA.Solid
  - KSA.EngineController
  - KSA.RocketControllerData
  - KSA.RocketCore
  - KSA.PlumbedCore
  - KSA.ResourceManager
  - KSA.FlowOrder
  - KSA.Constants
tags: [staging, delta-v, twr, rocket-equation, decoupler, tanks, resource-flow, engines]
---

The "Altitude estimate" mode (and the fallback whenever the game's staging cache is dirty) computes
stage numbers without the game's drain simulation. It is a classic KER-style estimate: walk the
stages in `SequenceList` order (sorted by `Sequence.Number`), drop what the decouplers in each stage release, sum the active engines'
thrust and mass flow at a chosen ambient pressure, find the propellant those engines can reach, and
apply the rocket equation. It is less exact than the native simulator but lets the player sweep the
ambient pressure with an altitude slider, which the native results cannot do.

## How it works

`SequenceAnalyzer.Analyze(parts, totalMass, ambientPressure, surfaceGravity)`
([SequenceAnalyzer.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SequenceAnalyzer.cs#L63-L171)):[^ker-sequenceanalyzer]

1. **Starting mass.** `totalMass` is the editor's wet mass (inert plus propellant mass properties) or
   `Vehicle.TotalMass` in flight; dry mass starts at `ComputeInertMassPropertiesAsmb().Props.Mass`.
2. **Jettison.** For every part in the sequence that has a `Decoupler` module
   (`ModuleList.HasAny<Decoupler>()`), the whole subtree under it (`Part.TreeChildren`, recursively,
   plus each part's `SubParts`) is marked jettisoned by `Part.InstanceId`; its inert mass (sum of
   `InertMass.MassPropertiesAsmb.Props.Mass`) and the substance mass of tanks not already claimed are
   subtracted ([SequenceAnalyzer.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SequenceAnalyzer.cs#L398-L463)).[^ksa-part] [^ksa-inertmass]
3. **Engines.** `EngineController` modules on the sequence's non-jettisoned parts; for an already
   activated sequence only engines with `IsActive` count.
4. **Thrust and flow.** In vacuum it sums `EngineController.VacuumData.ThrustMax.Length()` and
   `MassFlowRateMax`; with pressure it calls `RocketControllerData.ComputeFromCores(engine.Cores, p, 1f)`
   per engine, the same static `EngineController` itself falls back to for non-vacuum or partial
   throttle ([current/decomp/KSA/EngineController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineController.cs#L74)).[^ksa-enginecontroller] [^ksa-rocketcontrollerdata]
5. **Reachable propellant.** For each `PlumbedCore` it takes `core.ResourceManager.ConsumptionOrder`
   (a `FlowOrder<Tank>` of levels) and visits every tank once across the whole vehicle using a
   `HashSet<ulong>` of `ModuleBase.InstanceId`: current mass is `Tank.ComputeSubstanceMass(moleStates)`
   and capacity is the sum over `Tank.Moles` of `Liquid/Solid.ComputeMass(ContainerVolume)`
   ([SequenceAnalyzer.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SequenceAnalyzer.cs#L349-L396), [MassHelpers.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/MassHelpers.cs#L17-L30),
   [FlowHelpers.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlowHelpers.cs)).[^ker-masshelpers] [^ker-flowhelpers] [^ksa-plumbedcore] [^ksa-resourcemanagerbase] [^ksa-floworder] [^ksa-tank] [^ksa-mole]
6. **Rocket equation.** `ve = thrust / flow`, `Isp = ve / Constants.STANDARD_GRAVITY`, burnable fuel
   is capped so at least 1 kg remains, `dv = ve * ln(m0 / m1)`, `burnTime = fuel / flow`,
   `TWR = thrust / (m * g)` at start and end of the burn. Cumulative delta-v and burn time are carried
   to the next stage, whose start mass is this stage's end mass.[^ksa-constants]

Every list and set is a static pooled collection cleared per call, so the per-frame call allocates
only the result records; `Mod.Unload` clears the pools.[^ker-mod]

## Lifecycle and timing

- Called from `frame:draw-ui`: the editor postfix on `VehicleEditor.OnDrawUi` when the player picks
  "Altitude estimate", and the flight HUD postfix on `Vehicle.OnDrawUi` likewise or when native data
  is unavailable.
- In the editor everything it reads is main-thread state. In flight it reads the main-thread
  `Vehicle.Parts` (mole states, module lists) during `frame:draw-ui`, after `frame:apply-solvers` has
  copied the last physics step back, so values are one physics step old but consistent for the frame.
- `PlumbedCore.ResourceManager`'s getter calls `PartTree.EnsureResourceManagersBuilt()`
  ([current/decomp/KSA/PlumbedCore.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlumbedCore.cs#L19-L25)), so the
  first read after an edit may build the resource managers on the UI thread.

## Key code

| Path | Role |
|---|---|
| [SequenceAnalyzer.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SequenceAnalyzer.cs#L63-L171) | `Analyze`: per-stage loop and rocket equation |
| [SequenceAnalyzer.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SequenceAnalyzer.cs#L328-L463) | Engine collection, tank walk, jettisoned subtree mass |
| [MassHelpers.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/MassHelpers.cs) | Inert-mass sum and tank capacity from mole container volume |
| [FlowHelpers.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlowHelpers.cs) | `ResourceManager.ConsumptionOrder` accessor |

## Snippets

```csharp
// SequenceAnalyzer.cs L96-L135
            float totalThrust = 0f;
            float totalFlowRate = 0f;
            if (ambientPressure > 0f)
            {
                foreach (EngineController engine in _pooledEngines)
                {
                    var data = RocketControllerData.ComputeFromCores(engine.Cores.AsSpan(), ambientPressure, 1f);
                    totalThrust += data.ThrustMax.Length();
                    totalFlowRate += data.MassFlowRateMax;
                }
            }
            else
            {
                foreach (EngineController engine in _pooledEngines)
                {
                    totalThrust += engine.VacuumData.ThrustMax.Length();
                    totalFlowRate += engine.VacuumData.MassFlowRateMax;
                }
            }

            if (totalFlowRate < MinMassFlowRate)
                continue;

            float ve = totalThrust / totalFlowRate;
            float isp = (float)(ve / Constants.STANDARD_GRAVITY);

            var (fuelMass, maxFuelMass) = ComputeSequenceFuel(_pooledEngines, _pooledFuelClaimedTankIds, moleStates);
            float fuelFraction = maxFuelMass > 0f ? fuelMass / maxFuelMass : 0f;

            float burnableFuel = fuelMass;
            float maxBurnable = currentMass - MinDryMass;
            if (burnableFuel > maxBurnable)
                burnableFuel = Math.Max(0f, maxBurnable);

            float startMass = currentMass;
            float endMass = currentMass - burnableFuel;
            float dv = burnableFuel > 0f ? ve * MathF.Log(startMass / endMass) : 0f;
            float burnTime = burnableFuel / totalFlowRate;
            float initialTwr = surfaceGravity > 0f ? totalThrust / (startMass * surfaceGravity) : 0f;
            float maxTwr = surfaceGravity > 0f && endMass > 0f ? totalThrust / (endMass * surfaceGravity) : 0f;
```

```csharp
// SequenceAnalyzer.cs L368-L396
    private static (float current, float max) WalkReachableTanks(
        ResourceManager resourceManager, HashSet<ulong> fuelClaimedTankIds, ReadOnlySpan<MoleState> moleStates)
    {
        float current = 0f;
        float max = 0f;
        FlowOrder<Tank> order = FlowHelpers.SelectFlowNodes(resourceManager);
        if (!order.IsValid || order.LevelCount == 0)
            return (0f, 0f);

        for (int i = 0; i < order.LevelCount; i++)
        {
            ReadOnlySpan<Tank> tanks = order[i];
            if (tanks.IsEmpty)
                continue;

            for (int j = 0; j < tanks.Length; j++)
            {
                Tank tank = tanks[j];
                if (tank == null)
                    continue;
                if (!fuelClaimedTankIds.Add(tank.InstanceId))
                    continue;

                current += tank.ComputeSubstanceMass(moleStates);
                max += MassHelpers.ComputeTankMaxMass(tank);
            }
        }
        return (current, max);
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `EngineController.Cores`, `VacuumData`, `IsActive` | [current/decomp/KSA/EngineController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineController.cs#L36-L46) | Engine cores and cached vacuum performance |
| `RocketControllerData.ComputeFromCores(ReadOnlySpan<RocketCore>, float, float)` | [current/decomp/KSA/RocketControllerData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketControllerData.cs#L21) | Thrust and mass flow at an ambient pressure |
| `PlumbedCore.ResourceManager` | [current/decomp/KSA/PlumbedCore.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlumbedCore.cs#L19) | Per-core propellant routing |
| `ResourceManagerBase.ConsumptionOrder` / `FlowOrder<T>.IsValid`, `LevelCount`, indexer | [current/decomp/KSA/ResourceManagerBase.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ResourceManagerBase.cs#L81), [current/decomp/KSA/FlowOrder.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlowOrder.cs#L17-L21) | Tanks a core can draw from, by level |
| `Tank.ComputeSubstanceMass(ReadOnlySpan<MoleState>)`, `Tank.Moles` | [current/decomp/KSA/Tank.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Tank.cs#L579) | Current tank contents |
| `Mole.SubstancePhase`, `ContainerVolume`; `Liquid/Solid.ComputeMass(float)` | [current/decomp/KSA/Mole.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mole.cs#L9-L13), [current/decomp/KSA/Liquid.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Liquid.cs#L27), [current/decomp/KSA/Solid.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Solid.cs#L25) | Tank capacity |
| `Part.InstanceId`, `TreeChildren`, `SubParts`, `Modules` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L574-L680) | Subtree walk and module lookup |
| `ModuleList.Get<T>()`, `HasAny<T>()` | [current/decomp/KSA/ModuleList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleList.cs#L180-L196) | Find decouplers, engines, tanks, inert masses |
| `ModuleBase.InstanceId` | [current/decomp/KSA/ModuleBase.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleBase.cs#L27) | Claim each tank once (`ulong`) |
| `InertMass.MassPropertiesAsmb` | [current/decomp/KSA/InertMass.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InertMass.cs#L9) | Dry mass of jettisoned parts |
| `Constants.STANDARD_GRAVITY` | [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L245) | Isp from exhaust velocity |

## Pitfalls and version notes

- Tanks are claimed first-come across stages, so a tank shared by engines of several stages is
  counted for the earliest stage only; the native simulator handles shared drains properly.
- Jettisoned tank contents are subtracted only for tanks not already claimed as fuel by an earlier
  stage, because claimed propellant is assumed to have been burned in full.
- Only parts in the current sequence that carry a `Decoupler` trigger jettison; anything else that
  detaches (docking ports, fairings handled by other modules) is not modelled.
- Thrust is summed as vector magnitudes per engine, which overstates thrust for canted engines.
- In flight the starting mass is `Vehicle.TotalMass`, so parts that have already separated are gone
  from both the mass and `PartTree.Parts`.

## See also

- [Native stage performance readout](native-stage-performance-readout.md)
- [Editor panel injected into the vehicle editor](editor-panel-injection.md)
- [Suicide-burn calculator](suicide-burn-calculator.md)
- [Find the tanks an engine can draw from by walking ConsumptionOrder levels](../../../howto/vehicles-and-parts/modules-and-resources/add-parts-to-a-live-vessel/add-parts-to-a-live-vessel-3.md) - how-to article demonstrating this feature
- [Use ComputeFromCores or VacuumData inside a stage delta-v estimator](../../../howto/vehicles-and-parts/staging-and-engines/compute-engine-thrust-at-ambient-pressure/compute-engine-thrust-at-ambient-pressure-2.md) - how-to article demonstrating this feature
- [Estimate stage delta-v yourself from tanks and engine cores](../../../howto/vehicles-and-parts/staging-and-engines/read-staged-delta-v-from-the-game/read-staged-delta-v-from-the-game-4.md) - how-to article demonstrating this feature

[^ker-sequenceanalyzer]: SequenceAnalyzer.cs
[^ker-masshelpers]: MassHelpers.cs
[^ker-flowhelpers]: FlowHelpers.cs
[^ker-mod]: Mod.cs
[^ksa-enginecontroller]: KSA.EngineController (decompiled, build 2026.10.10.5554)
[^ksa-rocketcontrollerdata]: KSA.RocketControllerData (decompiled, build 2026.10.10.5554)
[^ksa-plumbedcore]: KSA.PlumbedCore (decompiled, build 2026.10.10.5554)
[^ksa-resourcemanagerbase]: KSA.ResourceManagerBase (decompiled, build 2026.10.10.5554)
[^ksa-floworder]: KSA.FlowOrder (decompiled, build 2026.10.10.5554)
[^ksa-tank]: KSA.Tank (decompiled, build 2026.10.10.5554)
[^ksa-mole]: KSA.Mole (decompiled, build 2026.10.10.5554)
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-inertmass]: KSA.InertMass (decompiled, build 2026.10.10.5554)
[^ksa-constants]: KSA.Constants (decompiled, build 2026.10.10.5554)
