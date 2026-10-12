---
type: How-To
title: Read PartTree.PerformanceSequences paired by index with SequenceList.Sequences
description: Take stage delta-v, thrust, Isp, masses and phases from the game's own staging simulator and add burn time, TWR, engine counts and reachable fuel, knowing when the list is fresh
category: vehicles-and-parts
group: staging-and-engines
topic: read-staged-delta-v-from-the-game
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/kitten-engineer-redux/features/native-stage-performance-readout]
techniques: [game-simulator-reuse]
hooks: [harmony-postfix KSA.VehicleEditor.OnDrawUi, harmony-postfix KSA.Vehicle.OnDrawUi]
lifecycle: [frame:draw-ui, editor:draw-ui]
game_types:
  - KSA.PartTree
  - KSA.SequencePerformanceList
  - KSA.SequencePerformance
  - KSA.SequenceList
  - KSA.SequencePhaseInfo
  - KSA.EngineController
tags: [delta-v, staging, performance, readout, freshness]
---
Outcome: a panel shows each stage's delta-v, Isp, thrust, TWR, burn time and engine count straight from the numbers the
stock staging display uses, adding only what the game does not store (burn time, TWR against a chosen body and altitude,
max TWR over the burn, engine count, fuel left in reachable tanks). Kitten Engineer Redux's "KSA simulation" mode does this.
It is the cheapest approach and the one that keeps matching the game across updates, but it is only as fresh as the game's
own recompute.

Prerequisites: StarMap, a UI hook (here Harmony postfixes on `VehicleEditor.OnDrawUi` and `Vehicle.OnDrawUi`). Demonstrated by
[kitten-engineer-redux/native-stage-performance-readout](../../../../mods/kitten-engineer-redux/features/native-stage-performance-readout.md).

## Background: how the game does it

See the [overview](overview.md) for the structure and who recomputes. The pairing rule: index `i` of
`PerformanceSequences` belongs to `SequenceList.Sequences[i]`, which is how the game's own `FindActiveSequenceDeltaV` walks
them [current/decomp/KSA/SequencePerformanceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequencePerformanceList.cs#L80-L93)[^ksa-sequenceperformancelist]. `IsDirty` is true until a recompute has run after any
`SetDirty()` (editor edits, sequence or environment changes, resource-group edits, tank toggles, `RefillAllTanks`,
`DepleteAllTanks`, `PartTree.Deserialize`); nothing in the propellant consumption path calls it, so a clean list keeps the
numbers of its last recompute rather than the live fuel state.

## Lifecycle and timing

| Context | Who keeps it fresh | Your read |
|---|---|---|
| editor | the game queues `SequencePerformanceJob` on the nearest-orbit worker after the UI draw; it is waited on at the top of the next `PrepareFrame` | `editor:draw-ui`: call `RecomputeIfDirty()` yourself (the synchronous cost is a full drain simulation on the main thread on every frame an edit dirtied the list); the game's later check then finds it clean and skips its job |
| flight | a vehicle worker, in `RunVehiclePostWorkInner`, for the controlled vehicle only, while the `Sequence` or `EngineControl` gauge canvas is enabled | `frame:draw-ui`: read the published array without forcing a recompute; a read sees one complete result set because the game double-buffers and publishes with `Interlocked.Exchange` |

With both gauge canvases turned off a dirty list stays dirty in flight; fall back to approach 4 (the mod shows its vacuum
estimate). See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. **Get the list and, in the editor only, recompute if dirty.** If it is still dirty or its length differs from the
   sequence count, give up on native data and fall back to your own estimator (article 4):

```csharp
// SequenceAnalyzer.cs L173-188
    public static VehicleBurnAnalysis AnalyzeNative(
        PartTree parts, float surfaceGravity, bool recomputeIfDirty, out bool usedNativeResults)
    {
        SequencePerformanceList performanceList = parts.PerformanceSequences;
        if (recomputeIfDirty)
            performanceList.RecomputeIfDirty();

        ReadOnlySpan<Sequence> sequences = parts.SequenceList.Sequences;
        ReadOnlySpan<SequencePerformance> performance = performanceList.PerformanceSequences;
        if (performanceList.IsDirty || performance.Length != sequences.Length)
        {
            usedNativeResults = false;
            VehicleMassSummary mass = MassAnalyzer.Analyze(parts);
            return Analyze(parts, mass.WetMass, 0f, surfaceGravity);
        }

```

2. **Walk the two spans in parallel.** Take `DeltaV`, `Thrust`, `Isp`, `MassFlowRate`, `WetMass`, `InertMass`, `FuelMass`
   and `BurnedFuelMass` as they are. Burn time is the sum of phase durations, falling back to
   `BurnedFuelMass / MassFlowRate`:

```csharp
// SequenceAnalyzer.cs L198-213
        ReadOnlySpan<MoleState> moleStates = parts.Moles.States;
        usedNativeResults = true;
        for (int i = 0; i < sequences.Length; i++)
        {
            Sequence sequence = sequences[i];
            SequencePerformance native = performance[i];
            List<SequencePhaseInfo>? phases = native.Phases;
            float burnTime = 0f;
            if (phases != null)
            {
                foreach (SequencePhaseInfo phase in phases)
                    burnTime += phase.Duration;
            }
            if (burnTime <= 0f && native.MassFlowRate > 0f)
                burnTime = native.BurnedFuelMass / native.MassFlowRate;

```

3. **Engine count**: engines on `AttachedParts` whose `EngineController.Sequence` equals the stage number, or the largest
   `SequencePhaseInfo.ActiveEngineCount`, whichever is larger:

```csharp
// SequenceAnalyzer.cs L214-237
            int engineCount = 0;
            _pooledEngines.Clear();
            HashSet<Part>? attachedParts = native.AttachedParts;
            if (attachedParts != null)
            {
                foreach (Part part in attachedParts)
                {
                    Span<EngineController> engines = part.Modules.Get<EngineController>();
                    for (int engineIndex = 0; engineIndex < engines.Length; engineIndex++)
                    {
                        if (engines[engineIndex].Sequence == sequence.Number)
                        {
                            engineCount++;
                            _pooledEngines.Add(engines[engineIndex]);
                        }
                    }
                }
            }
            if (phases != null)
            {
                foreach (SequencePhaseInfo phase in phases)
                    engineCount = Math.Max(engineCount, phase.ActiveEngineCount);
            }

```

4. **Recompute TWR yourself** if you want a body and altitude: `Thrust / (WetMass * g)`; the game's `Twr` always uses
   9.80665. Max TWR walks the phases subtracting `MassFlowRate * Duration` after each.
5. **Fuel gauges**: `SequencePerformance.FuelMass` is the simulator's start mass, not a capacity, so walk the reachable tanks
   of each counted engine for remaining versus capacity ([Create and attach parts to a live vessel and feed runtime engines from tanks](../../modules-and-resources/add-parts-to-a-live-vessel/overview.md) article 3).
6. **Feed diagnostics**: format up to three `SequencePropellantInfo` entries; a stage with engines but zero delta-v, zero
   burned fuel or no propellants gets a warning string instead.

## Complete example

- [SequenceAnalyzer.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SequenceAnalyzer.cs#L173-L294)[^kitten-engineer-redux-sequenceanalyzer] - `AnalyzeNative`

## Variations and gotchas

- **Do not retain the spans.** The published array is double-buffered: the previously published array becomes the scratch for
  the next recompute, one vehicle step later, so copy the numbers you need (approach 2 does).
- **Do not call `SetDirty()` just to refresh:** it marks the craft as having unsaved changes.
- **A stage below `ActiveSequence` has no engines** in flight mode; show it as spent.
- **Updates.** All public: `PerformanceSequences`, `IsDirty`, `RecomputeIfDirty`, the struct fields. The fields' meaning
  (for example `Twr` at 9.80665) is the part to re-check.

## See also

- [Overview](overview.md), [Article 2](read-staged-delta-v-from-the-game-2.md), [Article 4](read-staged-delta-v-from-the-game-4.md)
- [kitten-engineer-redux/native-stage-performance-readout](../../../../mods/kitten-engineer-redux/features/native-stage-performance-readout.md)
- [Compute engine thrust, Isp and mass flow at ambient pressure and invert for throttle](../compute-engine-thrust-at-ambient-pressure/overview.md)

[^kitten-engineer-redux-sequenceanalyzer]: Kitten-Engineer-Redux: SequenceAnalyzer.cs
[^ksa-sequenceperformancelist]: current/decomp/KSA/SequencePerformanceList.cs (decompiled, build 2026.10.10.5554)
