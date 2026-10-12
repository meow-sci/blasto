---
type: How-To
title: Estimate stage delta-v yourself from tanks and engine cores
description: Walk sequences in order, drop what decouplers release, sum engine thrust and flow at a chosen ambient pressure, find reachable propellant and apply the rocket equation
category: vehicles-and-parts
group: staging-and-engines
topic: read-staged-delta-v-from-the-game
approach: 4
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/kitten-engineer-redux/features/custom-stage-delta-v-estimator]
techniques: [stage-model-recompute]
hooks: [harmony-postfix KSA.VehicleEditor.OnDrawUi, harmony-postfix KSA.Vehicle.OnDrawUi]
lifecycle: [frame:draw-ui, editor:draw-ui]
game_types:
  - KSA.PartTree
  - KSA.SequenceList
  - KSA.EngineController
  - KSA.RocketControllerData
  - KSA.Decoupler
  - KSA.PlumbedCore
  - KSA.Tank
tags: [delta-v, rocket-equation, estimator, ambient-pressure, stage-model]
---
Outcome: a KER-style stage estimate with no dependency on the game's drain simulation, so you can sweep ambient pressure
with an altitude slider (which the native results cannot do) and still show numbers when the native list is dirty. It is less
exact than the native simulator. Kitten Engineer Redux uses it for "Altitude estimate" mode and as the fallback.

Prerequisites: StarMap. Demonstrated by [kitten-engineer-redux/custom-stage-delta-v-estimator](../../../../mods/kitten-engineer-redux/features/custom-stage-delta-v-estimator.md). The reachable-propellant walk
is [add-parts-to-a-live-vessel article 3](../../modules-and-resources/add-parts-to-a-live-vessel/add-parts-to-a-live-vessel-3.md).

## Background: how the game does it

- Stages are `SequenceList.Sequences` (sorted by `Sequence.Number`); each `Sequence` has `Parts`, `Activated` and a number
  [current/decomp/KSA/SequenceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequenceList.cs#L127-L131)[^ksa-sequencelist].
- A part with a `Decoupler` module releases the subtree under it (`Part.TreeChildren` recursively plus each part's
  `SubParts`); inert mass is the sum of `InertMass.MassPropertiesAsmb.Props.Mass`.
- `EngineController.VacuumData` (`RocketControllerData`) is the full-throttle, 0 Pa figure; at any other pressure or throttle
  `RocketControllerData.ComputeFromCores(cores, pressure, throttle)` is what the controller itself falls back to
  [current/decomp/KSA/EngineController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineController.cs#L68-L75)[^ksa-enginecontroller].
- Reachable propellant: `PlumbedCore.ResourceManager.ConsumptionOrder` (a `FlowOrder<Tank>`)
  [current/decomp/KSA/PlumbedCore.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlumbedCore.cs#L60-L100)[^ksa-plumbedcore].

## Lifecycle and timing

Called from `frame:draw-ui` by an editor postfix on `VehicleEditor.OnDrawUi` and a flight HUD postfix on `Vehicle.OnDrawUi`. In
the editor everything it reads is main-thread state. In flight it reads the main-thread `Vehicle.Parts` after
`frame:apply-solvers` has copied the last step back, so values are one physics step old but consistent for the frame. The first
read of a `PlumbedCore.ResourceManager` after an edit may build the managers on the UI thread. In flight `PrepareFrame` has already run `PartTree.FlushDirtyDerived()` and `FlushDirtyResourceManagers()` before this frame's dispatch (`Program.cs` L2218-L2219), so on a tree that nothing has dirtied since, the `ResourceManager` getter only reads. If a hook of your own or another mod edited the tree after the dispatch (staged, built parts), the first read from the draw would rebuild the managers on the UI thread while the vehicle-solver worker may be using the tree: skip the read for that frame. See
[game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. **Start masses**: `totalMass` is the editor's wet mass or `Vehicle.TotalMass` in flight; dry mass starts at
   `ComputeInertMassPropertiesAsmb().Props.Mass`. For each non-empty sequence, subtract what its decouplers jettison:

```csharp
// SequenceAnalyzer.cs L81-91
        for (int si = 0; si < sequences.Length; si++)
        {
            Sequence sequence = sequences[si];
            if (sequence.Parts.IsEmpty)
                continue;

            (float jettisonedMass, float jettisonedDryMass) = ComputeJettisonedMass(
                sequence, moleStates, _pooledJettisonedPartIds, _pooledFuelClaimedTankIds);
            currentMass -= jettisonedMass;
            currentDryMass = Math.Max(0f, currentDryMass - jettisonedDryMass);

```

2. **Collect the sequence's engines** on non-jettisoned parts (only active ones if the sequence is already activated), then
   **sum thrust and flow** in vacuum from `VacuumData`, or at pressure from `ComputeFromCores(engine.Cores, p, 1f)`:

```csharp
// SequenceAnalyzer.cs L97-119
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
```

3. **Find the fuel the engines can reach** with `ConsumptionOrder`, visiting each tank once vehicle-wide (shared tanks are
   counted once).
4. **Rocket equation.** `ve = thrust / flow`, `Isp = ve / g0`, burnable fuel capped so at least 1 kg remains,
   `dv = ve * ln(m0/m1)`, `burnTime = fuel / flow`, TWR at start and end of the burn; carry the end mass to the next stage:

```csharp
// SequenceAnalyzer.cs L119-143
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
            float cumulativeDeltaV = result.TotalDeltaV + dv;

            result.Sequences.Add(new SequenceBurnInfo
            {
                SequenceNumber = sequence.Number,
                IsActivated = sequence.Activated,
                DeltaV = dv,
                CumulativeDeltaV = cumulativeDeltaV,
```

5. **Pool your collections** (static lists and sets cleared per call) so the per-frame call allocates only the result records;
   clear the pools on unload.

## Complete example

- [SequenceAnalyzer.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SequenceAnalyzer.cs#L63-L171)[^kitten-engineer-redux-sequenceanalyzer] - `Analyze`
- [SequenceAnalyzer.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SequenceAnalyzer.cs#L349-L396) - reachable fuel

## Variations and gotchas

- **Sequences are not strictly sequential in the game's model.** Cutoff groups, cross-stage fuel attribution and solid residue
  are things the native simulator handles and this estimate does not; expect small disagreements, and say which mode is shown.
- **Solid motors.** Their propellant is a `SolidGrainSegment`, not a `Tank`, so a tank walk misses it (use
  `Vehicle.PropellantMass` or approach 1/3 for SRB stages).
- **Use it as a fallback, not a replacement.** KER shows native numbers when the list is clean, and the estimate when it is
  not.

## See also

- [Overview](overview.md), [Article 1](read-staged-delta-v-from-the-game-1.md), [Article 3](read-staged-delta-v-from-the-game-3.md)
- [kitten-engineer-redux/custom-stage-delta-v-estimator](../../../../mods/kitten-engineer-redux/features/custom-stage-delta-v-estimator.md)
- [Compute engine thrust, Isp and mass flow at ambient pressure and invert for throttle](../compute-engine-thrust-at-ambient-pressure/overview.md), [Create and attach parts to a live vessel and feed runtime engines from tanks](../../modules-and-resources/add-parts-to-a-live-vessel/overview.md)

[^kitten-engineer-redux-sequenceanalyzer]: Kitten-Engineer-Redux: SequenceAnalyzer.cs
[^ksa-enginecontroller]: current/decomp/KSA/EngineController.cs (decompiled, build 2026.10.10.5554)
[^ksa-sequencelist]: current/decomp/KSA/SequenceList.cs (decompiled, build 2026.10.10.5554)
[^ksa-plumbedcore]: current/decomp/KSA/PlumbedCore.cs (decompiled, build 2026.10.10.5554)
