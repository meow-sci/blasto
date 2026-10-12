---
type: How-To
title: Use ComputeFromCores or VacuumData inside a stage delta-v estimator
description: Sum engine thrust and mass flow per stage at vacuum from VacuumData or at pressure from RocketControllerData.ComputeFromCores, and feed them to the rocket equation
category: vehicles-and-parts
group: staging-and-engines
topic: compute-engine-thrust-at-ambient-pressure
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/kitten-engineer-redux/features/custom-stage-delta-v-estimator]
techniques: [stage-model-recompute]
hooks: [harmony-postfix KSA.VehicleEditor.OnDrawUi]
lifecycle: [frame:draw-ui, editor:draw-ui]
game_types: [KSA.EngineController, KSA.RocketControllerData, KSA.RocketCore, KSA.SequenceList, KSA.PartTree]
tags: [thrust, isp, pressure, estimator, rocket-equation]
---
Outcome: pressure-dependent thrust and Isp inside a stage-by-stage burn model, so a slider can sweep ambient pressure and the
numbers change as they would on the pad, at altitude and in vacuum. Kitten Engineer Redux's "Altitude estimate" mode does this
with two calls.

Prerequisite: StarMap. Demonstrated by [kitten-engineer-redux/custom-stage-delta-v-estimator](../../../../mods/kitten-engineer-redux/features/custom-stage-delta-v-estimator.md). The full estimator is
[read-staged-delta-v-from-the-game article 4](../read-staged-delta-v-from-the-game/read-staged-delta-v-from-the-game-4.md);
this article isolates the engine-model step.

## Background: how the game does it

- In vacuum at full throttle `EngineController.VacuumData` is the precomputed answer (`ThrustMax` vector length and
  `MassFlowRateMax`); at any other pressure or throttle the controller itself falls back to
  `RocketControllerData.ComputeFromCores(Cores, pressure, throttle)` [current/decomp/KSA/EngineController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineController.cs#L68-L75)[^ksa-enginecontroller].
- Each nozzle's `ComputePerformance(combustion, ambientPressure)` returns both thrust and mass flow for the given pressure, so
  the effective Isp is the ratio of the two sums.
- `ComputeFromCores` clamps the throttle to each core's `MinimumThrottle` and also exists as a two-pressure overload that
  returns both results from one pass [current/decomp/KSA/RocketControllerData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketControllerData.cs#L21-L64)[^ksa-rocketcontrollerdata].

## Lifecycle and timing

Main-thread UI code (`frame:draw-ui`, `editor:draw-ui`). In the editor everything is main-thread state; in flight the
engine data is the committed result of the last step. No timing hazard, because both calls only read templates and module
instances. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. For each engine in the stage (only active ones for an already-activated stage) pick the call by pressure:

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

2. Turn the sums into `ve` and Isp, using the game's standard gravity constant (9.80665):

```csharp
// SequenceAnalyzer.cs L119-120
            float ve = totalThrust / totalFlowRate;
            float isp = (float)(ve / Constants.STANDARD_GRAVITY);
```

3. Use the pressure that matches where the stage burns. A stage lit at sea level and burning to orbit needs more than one
   pressure; the simple estimator uses one pressure per analysis, so sweep it and compare, or split the stage.
4. If you only need two pressures (for example sea level and vacuum Isp), prefer the two-pressure
   `RocketControllerData.ComputeFromCores(cores, pA, pB, throttle, out dataA, out dataB)` overload, which computes both in
   one pass.
5. Keep the vacuum branch as the default when `ambientPressure <= 0`, so the common case allocates nothing and reads a
   cached value.

## Complete example

- [SequenceAnalyzer.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SequenceAnalyzer.cs#L63-L171)[^kitten-engineer-redux-sequenceanalyzer]

## Variations and gotchas

- **Solid motors.** The estimator treats every engine alike; for stages with solid motors compare with the game's own
  simulator ([Read staged delta-v, thrust and Isp from the game or from your own stage model](../read-staged-delta-v-from-the-game/overview.md) approaches 1 to 3).
- **Throttle other than 1.** Pass the throttle; `ComputeFromCores` clamps to each core's minimum throttle; `VacuumData` is only valid at full
  throttle and 0 Pa.
- **Allocation.** The estimator reuses pooled lists; the engine data structs are values.

## See also

- [Overview](overview.md), [Article 1](compute-engine-thrust-at-ambient-pressure-1.md)
- [kitten-engineer-redux/custom-stage-delta-v-estimator](../../../../mods/kitten-engineer-redux/features/custom-stage-delta-v-estimator.md)
- [Read staged delta-v, thrust and Isp from the game or from your own stage model](../read-staged-delta-v-from-the-game/overview.md)

[^kitten-engineer-redux-sequenceanalyzer]: Kitten-Engineer-Redux: SequenceAnalyzer.cs
[^ksa-enginecontroller]: current/decomp/KSA/EngineController.cs (decompiled, build 2026.10.10.5554)
[^ksa-rocketcontrollerdata]: current/decomp/KSA/RocketControllerData.cs (decompiled, build 2026.10.10.5554)
