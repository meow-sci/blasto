---
type: How-To
title: Find the tanks an engine can draw from by walking ConsumptionOrder levels
description: Read PlumbedCore.ResourceManager.ConsumptionOrder, a FlowOrder of tank levels, to sum reachable propellant per engine for a custom stage or delta-v model
category: vehicles-and-parts
group: modules-and-resources
topic: add-parts-to-a-live-vessel
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/kitten-engineer-redux/features/custom-stage-delta-v-estimator]
techniques: [stage-model-recompute]
hooks: [harmony-postfix KSA.VehicleEditor.OnDrawUi, harmony-postfix KSA.Vehicle.OnDrawUi]
lifecycle: [frame:draw-ui, editor:draw-ui]
game_types:
  - KSA.PlumbedCore
  - KSA.ResourceManager
  - KSA.FlowOrder
  - KSA.Tank
  - KSA.EngineController
  - KSA.RocketCore
  - KSA.PartTree
tags: [resource-graph, consumption-order, tanks, delta-v, flow-order]
---
Outcome: for each engine you can answer "which tanks can feed it, nearest first, and how much propellant is that?". Kitten
Engineer Redux uses it in its own stage model; it is also the verification half of
[article 2](add-parts-to-a-live-vessel-2.md) (a count of zero means the engine is not wired).

Prerequisites: StarMap. Demonstrated by [kitten-engineer-redux/custom-stage-delta-v-estimator](../../../../mods/kitten-engineer-redux/features/custom-stage-delta-v-estimator.md). The delta-v maths built
on it is [Read staged delta-v, thrust and Isp from the game or from your own stage model](../../staging-and-engines/read-staged-delta-v-from-the-game/overview.md) approach 4.

## Background: how the game does it

- A `PlumbedCore` (a `RocketCore` with a feed) has a `ResourceManager`; `ResourceManager.ConsumptionOrder` is a
  `FlowOrder<Tank>` whose `LevelCount` levels list the tanks the core can draw from, in the order the flow rule
  consumes them; `IsValid` is false until built; `SameStageOnly` records whether the rule filters by stage
  [current/decomp/KSA/PlumbedCore.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlumbedCore.cs#L60-L100)[^ksa-plumbedcore] [current/decomp/KSA/FlowOrder.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlowOrder.cs#L15-L40)[^ksa-floworder].
- The getter on `PlumbedCore.ResourceManager` calls `PartTree.EnsureResourceManagersBuilt()`, so the first read after an
  edit may build the managers on the calling thread (the UI thread in practice).
- Tank mass: `Tank.ComputeSubstanceMass(moleStates)`; capacity: the sum over `Tank.Moles` of the substance phase's
  `ComputeMass(ContainerVolume)` (liquid or solid) [current/decomp/KSA/Tank.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Tank.cs#L78-L81)[^ksa-tank] [current/decomp/KSA/Mole.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mole.cs#L53-L60)[^ksa-mole].

## Lifecycle and timing

Called from `frame:draw-ui`: from a postfix on `VehicleEditor.OnDrawUi` in the editor (`editor:draw-ui`) and a postfix on
`Vehicle.OnDrawUi` in flight. In the editor everything is main-thread state. In flight it reads the main-thread
`Vehicle.Parts` after `frame:apply-solvers` copied the last step back, so values are one physics step old but consistent for
the frame. In flight `PrepareFrame` has already run `PartTree.FlushDirtyDerived()` and `FlushDirtyResourceManagers()` before this frame's dispatch (`Program.cs` L2218-L2219), so on a tree that nothing has dirtied since, the `ResourceManager` getter only reads. If a hook of your own or another mod edited the tree after the dispatch (staged, built parts), the first read from the draw would rebuild the managers on the UI thread while the vehicle-solver worker may be using the tree: skip the read for that frame. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. **Get the order** for a core, guarding the type and the validity:

```csharp
// FlowHelpers.cs L5-8
internal static class FlowHelpers
{
    public static FlowOrder<Tank> SelectFlowNodes(ResourceManager rm) => rm.ConsumptionOrder;
}
```

2. **Visit each tank once across the whole vehicle** with a `HashSet<ulong>` of `ModuleBase.InstanceId`, so a tank shared by
   several engines or cores is counted once, summing current and maximum mass:

```csharp
// SequenceAnalyzer.cs L368-396
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

3. **Do it per core of each active engine** (`engine.Cores` that are `PlumbedCore { ResourceManager: { } }`):

```csharp
// SequenceAnalyzer.cs L349-366
    private static (float current, float max) ComputeSequenceFuel(
        List<EngineController> engines, HashSet<ulong> fuelClaimedTankIds, ReadOnlySpan<MoleState> moleStates)
    {
        float totalCurrent = 0f;
        float totalMax = 0f;
        foreach (EngineController engine in engines)
        {
            foreach (RocketCore core in engine.Cores)
            {
                if (core is not PlumbedCore { ResourceManager: { } resourceManager })
                    continue;
                var (current, max) = WalkReachableTanks(resourceManager, fuelClaimedTankIds, moleStates);
                totalCurrent += current;
                totalMax += max;
            }
        }
        return (totalCurrent, totalMax);
    }
```

4. Use the result to bound the burn: burnable fuel is capped so at least a kilogram remains, `dv = ve * ln(m0/m1)` with
   `ve = thrust / flow`.
5. If a core reports zero tanks, check article 2: the engine is not wired, or is in a different stage than its tanks.

## Complete example

- [SequenceAnalyzer.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SequenceAnalyzer.cs#L349-L396)[^kitten-engineer-redux-sequenceanalyzer] and [FlowHelpers.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlowHelpers.cs)[^kitten-engineer-redux-flowhelpers]
- [MassHelpers.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/MassHelpers.cs)[^kitten-engineer-redux-masshelpers] - tank capacity

## Variations and gotchas

- **Level order is the flow rule.** Nearest-to-furthest consumption means levels are drained in order; do not assume the
  flat sum is the usable amount for a given burn without considering `SameStageOnly`.
- **Pooled collections.** KER uses static pooled lists and sets cleared per call so the per-frame cost allocates only result
  records; clear them on unload.
- **The native model exists.** Where the game's own staging simulation is fresh, prefer it ([Read staged delta-v, thrust and Isp from the game or from your own stage model](../../staging-and-engines/read-staged-delta-v-from-the-game/overview.md)
  approach 1); this walk is the fallback and the way to sweep ambient pressure.
- **Updates.** `ConsumptionOrder` and `FlowOrder` are public; their level layout can change with new flow rules.

## See also

- [Overview](overview.md), [Article 1](add-parts-to-a-live-vessel-1.md), [Article 2](add-parts-to-a-live-vessel-2.md)
- [kitten-engineer-redux/custom-stage-delta-v-estimator](../../../../mods/kitten-engineer-redux/features/custom-stage-delta-v-estimator.md)
- [Read staged delta-v, thrust and Isp from the game or from your own stage model](../../staging-and-engines/read-staged-delta-v-from-the-game/overview.md), [Read live part module state: engines, tanks, propellant, batteries, panels, docking ports](../read-live-part-module-state/overview.md)

[^ksa-tank]: current/decomp/KSA/Tank.cs (decompiled, build 2026.10.10.5554)
[^kitten-engineer-redux-flowhelpers]: Kitten-Engineer-Redux: FlowHelpers.cs
[^kitten-engineer-redux-sequenceanalyzer]: Kitten-Engineer-Redux: SequenceAnalyzer.cs
[^kitten-engineer-redux-masshelpers]: Kitten-Engineer-Redux: MassHelpers.cs
[^ksa-plumbedcore]: current/decomp/KSA/PlumbedCore.cs (decompiled, build 2026.10.10.5554)
[^ksa-floworder]: current/decomp/KSA/FlowOrder.cs (decompiled, build 2026.10.10.5554)
[^ksa-mole]: current/decomp/KSA/Mole.cs (decompiled, build 2026.10.10.5554)
