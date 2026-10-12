---
type: Mod Feature
title: Native stage performance readout
description: Reads per-stage delta-v, thrust, Isp and masses from the game's own staging simulator (PartTree.PerformanceSequences), derives burn time and max TWR from its burn phases, and falls back to a custom estimate while the cache is dirty
mod: kitten-engineer-redux
category: vehicles-and-parts
techniques: [game-simulator-reuse]
hooks: [harmony-postfix KSA.VehicleEditor.OnDrawUi, harmony-postfix KSA.Vehicle.OnDrawUi]
lifecycle: [frame:draw-ui, editor:draw-ui]
game_types:
  - KSA.PartTree
  - KSA.SequencePerformanceList
  - KSA.SequencePerformance
  - KSA.SequencePhaseInfo
  - KSA.SequencePropellantInfo
  - KSA.SequenceList
  - KSA.Sequence
  - KSA.EngineController
  - KSA.Part
  - KSA.SequencePerformanceJob
  - KSA.PhysicsBubble
  - KSA.ResourceGroups
  - KSA.GaugeCanvas
tags: [staging, delta-v, twr, burn-time, sequences, sequence-performance, editor, flight]
---

The default "KSA simulation" mode of both the editor panel and the flight HUD does not compute stage
delta-v itself. It reads the results of the game's staging simulator, the same
`SequencePerformanceList` that backs the stock stage display, and only adds what the game does not
store: burn time, a TWR against a chosen body and altitude, a maximum TWR over the burn, the number
of engines, the fuel left in the tanks each stage can reach, and a feed diagnostic string.

## How it works

`SequenceAnalyzer.AnalyzeNative` ([SequenceAnalyzer.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SequenceAnalyzer.cs#L173-L294)):[^ker-sequenceanalyzer]

1. Takes `parts.PerformanceSequences` and, when asked, calls `RecomputeIfDirty()` (editor only).
2. If the list is still dirty, or its `PerformanceSequences` span length differs from
   `SequenceList.Sequences`, it gives up on native data, reports `usedNativeResults = false` and runs
   the custom estimator in vacuum ([Custom stage delta-v estimator](custom-stage-delta-v-estimator.md)).
3. Otherwise it walks the two spans in parallel (index `i` of `PerformanceSequences` belongs to
   sequence `i`, which is how the game's own `FindActiveSequenceDeltaV` pairs them).[^ksa-seqperflist]
4. Per stage it takes `DeltaV`, `Thrust`, `Isp`, `MassFlowRate`, `WetMass`, `InertMass`, `FuelMass`
   and `BurnedFuelMass` from the `SequencePerformance` struct as-is.[^ksa-seqperf]
5. Burn time is the sum of `SequencePhaseInfo.Duration` over `Phases`; if that is zero it falls back
   to `BurnedFuelMass / MassFlowRate`.[^ksa-seqphase]
6. Initial TWR is recomputed as `Thrust / (WetMass * g)` with the mod's own `g` (home body in the
   editor, current parent in flight, at the analysis altitude), falling back to the game's `Twr`
   (which always uses 9.80665 m/s^2) when `g` is zero. Max TWR walks the phases, subtracting
   `MassFlowRate * Duration` from the mass after each phase and taking the largest
   `phase.Thrust / (mass * g)` at the start and end of each phase.
7. Engine count: engines on `AttachedParts` whose `EngineController.Sequence` equals the stage number,
   or the largest `SequencePhaseInfo.ActiveEngineCount`, whichever is larger.
8. Fuel gauges: the mod walks each counted engine's reachable tanks itself (same walk as the custom
   estimator) to get remaining versus capacity, because `SequencePerformance.FuelMass` is the
   simulator's start mass, not a capacity.
9. Feed diagnostics: up to three `SequencePropellantInfo` entries are formatted as
   `ReactantName @ TankPart.DisplayName (ConsumedMass kg)`; a stage with engines but zero delta-v,
   zero burned fuel or no propellants gets a warning string instead
   ([SequenceAnalyzer.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SequenceAnalyzer.cs#L296-L318)).[^ksa-seqprop]

What the game computes for you, for comparison ([current/decomp/KSA/SequencePerformanceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequencePerformanceList.cs#L111-L291)):
each sequence's drain is evaluated at 101325 Pa if its `Sequence.Environment` is `Atmospheric`, else in
vacuum; a drain simulation (`RunDrainSimulation`) burns the stage's propellant and records one
`SequencePhaseInfo` per phase; `Twr` uses `9.80665`; `Isp` is back-derived from the delta-v and mass
ratio except for the stage currently burning in flight, which uses thrust over mass flow. In a flight
recompute (`RecomputeForFlight`) the active stage's reported thrust is taken at the live ambient
pressure, or from the live nozzle states when its engines are burning, and stages numbered below the
active one get no engines.[^ksa-sequence]

## Lifecycle and timing

- Called from the editor postfix (`editor:draw-ui`) with `recomputeIfDirty: true`
  ([EditorPanel.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/EditorPanel.cs#L46-L50)) and from the flight HUD postfix (`frame:draw-ui`)
  with `recomputeIfDirty: false` ([FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L121-L127)).[^ker-editorpanel] [^ker-flighthud]
- Editor: the game itself recomputes a dirty list on the `NearestOrbitAndPerformanceWorker` thread
  through `SequencePerformanceJob`, queued after the draw-UI block of `Program.OnFrame` (only if
  `IsDirty` and the worker is ready) and waited on at the top of the next `PrepareFrame`
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2277-L2284), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2151-L2152)).[^ksa-seqjob] [^ksa-program]
  KER's synchronous `RecomputeIfDirty()` runs on the main thread earlier in the same frame, inside
  `OnDrawUiThreadSafe`, while that worker holds only the nearest-orbit job queued in `PrepareFrame`,
  so no second recompute of the list is in flight; the game's later check then sees a clean list and
  skips its own job. The cost is the full drain simulation on the main thread on every frame where an
  edit dirtied the list. With the panel hidden or in "Altitude estimate" mode the game's job does the
  work as usual.
- Flight: the game recomputes only on a vehicle-solver worker, in
  `PhysicsBubble.RunVehiclePostWorkInner`, for the controlled vehicle, and only while
  `ResourceGroups.IsOpen` (the `Sequence` gauge canvas is enabled) or `Program.EngineControlGaugeOpen`
  (the `EngineControl` gauge canvas is enabled)
  (`RecomputeForFlight`, [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L793-L796),
  [current/decomp/KSA/ResourceGroups.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ResourceGroups.cs#L15)).[^ksa-physicsbubble] [^ksa-resourcegroups]
  Gauge canvases start enabled (`GaugeCanvas._enabled = true`,
  [current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L143)), so in a default flight UI the list is
  recomputed every vehicle step and the draw normally finds it clean.[^ksa-gaugecanvas]
  KER never forces a recompute in flight; it only reads the published array. The game double-buffers
  it: `Recompute` fills `_performanceScratch` and publishes it with `Interlocked.Exchange`, readers get
  it through `Volatile.Read`, and the previously published array only becomes the scratch for the
  next recompute, one vehicle step later. A read from `frame:draw-ui`, while that step's worker may be
  recomputing, therefore sees one complete result set, not a half-written one.
- `SetDirty()` is called by editor edits, sequence list and `Sequence.Environment` changes,
  resource-group edits, tank toggles and reconfiguration (`InputEvents`, `Tank.ConfigureFor`),
  `ResourceManager.RefillAllTanks`/`DepleteAllTanks`, and `PartTree.Deserialize`; a new list also
  starts dirty. Nothing in the propellant consumption path calls it. With both gauge canvases turned
  off, a dirty list stays dirty in flight (KER shows its vacuum estimate) and a clean list keeps the
  numbers of its last recompute, not the live fuel state.[^ksa-sequencelist] [^ksa-parttree]

## Key code

| Path | Role |
|---|---|
| [SequenceAnalyzer.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SequenceAnalyzer.cs#L173-L294) | `AnalyzeNative`: dirty check, phase walk, TWR, fuel and feed diagnostics |
| [SequenceAnalyzer.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SequenceAnalyzer.cs#L9-L43) | `SequenceBurnInfo` and `VehicleBurnAnalysis` records shared with the custom estimator |
| [StageListView.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/StageListView.cs) | Stage table with expandable details and per-stage fuel bars |

## Snippets

```csharp
// SequenceAnalyzer.cs L173-L212
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

        _pooledSequences.Clear();
        _pooledFuelClaimedTankIds.Clear();
        var result = new VehicleBurnAnalysis
        {
            Sequences = _pooledSequences,
            TotalDeltaV = 0f,
            TotalBurnTime = 0f
        };

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

```csharp
// SequenceAnalyzer.cs L246-L261
            float endMass = Math.Max(0f, native.WetMass - native.BurnedFuelMass);
            float initialTwr = native.WetMass > 0f && surfaceGravity > 0f
                ? native.Thrust / (native.WetMass * surfaceGravity)
                : native.Twr;
            float maxTwr = initialTwr;
            if (phases != null && surfaceGravity > 0f)
            {
                float phaseMass = native.WetMass;
                foreach (SequencePhaseInfo phase in phases)
                {
                    if (phaseMass > 0f)
                        maxTwr = Math.Max(maxTwr, phase.Thrust / (phaseMass * surfaceGravity));
                    phaseMass = Math.Max(1f, phaseMass - phase.MassFlowRate * phase.Duration);
                    maxTwr = Math.Max(maxTwr, phase.Thrust / (phaseMass * surfaceGravity));
                }
            }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `PartTree.PerformanceSequences` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L58) | The craft's `SequencePerformanceList` |
| `SequencePerformanceList.IsDirty`, `PerformanceSequences`, `RecomputeIfDirty()` | [current/decomp/KSA/SequencePerformanceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequencePerformanceList.cs#L63-L101) | Cache state, published results, synchronous recompute |
| `SequencePerformance` fields | [current/decomp/KSA/SequencePerformance.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequencePerformance.cs) | `DeltaV`, `Twr`, `Isp`, `Thrust`, `MassFlowRate`, `WetMass`, `InertMass`, `FuelMass`, `BurnedFuelMass`, `AttachedParts`, `Propellants`, `Phases` |
| `SequencePhaseInfo` | [current/decomp/KSA/SequencePhaseInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequencePhaseInfo.cs) | `Duration`, `Thrust`, `MassFlowRate`, `ActiveEngineCount` per burn phase |
| `SequencePropellantInfo` | [current/decomp/KSA/SequencePropellantInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequencePropellantInfo.cs) | `ReactantName`, `TankPart`, `ConsumedMass` for feed diagnostics |
| `SequenceList.Sequences`, `Sequence.Number`, `Sequence.Activated` | [current/decomp/KSA/SequenceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequenceList.cs#L127), [current/decomp/KSA/Sequence.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Sequence.cs#L11-L13) | Stage order and labels |
| `EngineController.Sequence` | [current/decomp/KSA/EngineController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineController.cs#L46) | Match engines to their stage |
| `SequencePerformanceJob`, `PhysicsBubble` | [current/decomp/KSA/SequencePerformanceJob.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequencePerformanceJob.cs), [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L793-L796) | Where the game recomputes on its own |

## Pitfalls and version notes

- The fallback path passes ambient pressure `0`, so a dirty cache in flight silently switches the
  numbers to a vacuum estimate. The HUD labels this "Native cache pending; estimate" and the editor
  "KSA data unavailable; estimate".
- `SequencePerformance.Twr` and the per-sequence `Environment` pressure are fixed by the game (9.80665
  m/s^2, 101325 Pa or vacuum, plus the live pressure for the active stage in flight); the mod's
  altitude slider changes `g` for its TWR columns but not the game's delta-v.
- Index pairing assumes `PerformanceSequences` and `SequenceList.Sequences` stay in the same order;
  the mod guards only on length.
- `AttachedParts`, `Propellants` and `Phases` are reference fields inside the `SequencePerformance`
  structs; each of the two buffers keeps its own lists and clears and refills them when it is next
  used as scratch. Read them in the same frame you fetched the span and do not keep references.

## See also

- [Custom stage delta-v estimator](custom-stage-delta-v-estimator.md)
- [Editor panel injected into the vehicle editor](editor-panel-injection.md)
- [Flight HUD on the controlled vehicle](flight-hud-controlled-vehicle-postfix.md)
- [KSA-AdvancedFlightComputer: per-stage thrust, Isp and burn time from the staging simulator](../../ksa-advancedflightcomputer/features/stage-model-from-staging-simulator.md)
- [KSA-DeltaVMap: vehicle staged delta-v readout](../../ksa-deltavmap/features/vehicle-staged-dv-readout.md)
- [Read PartTree.PerformanceSequences paired by index with SequenceList.Sequences](../../../howto/vehicles-and-parts/staging-and-engines/read-staged-delta-v-from-the-game/read-staged-delta-v-from-the-game-1.md) - how-to article demonstrating this feature

[^ker-sequenceanalyzer]: SequenceAnalyzer.cs
[^ker-editorpanel]: EditorPanel.cs
[^ker-flighthud]: FlightHud.cs
[^ksa-seqperflist]: KSA.SequencePerformanceList (decompiled, build 2026.10.10.5554)
[^ksa-seqperf]: KSA.SequencePerformance (decompiled, build 2026.10.10.5554)
[^ksa-seqphase]: KSA.SequencePhaseInfo (decompiled, build 2026.10.10.5554)
[^ksa-seqprop]: KSA.SequencePropellantInfo (decompiled, build 2026.10.10.5554)
[^ksa-seqjob]: KSA.SequencePerformanceJob (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^ksa-sequence]: KSA.Sequence (decompiled, build 2026.10.10.5554)
[^ksa-sequencelist]: KSA.SequenceList (decompiled, build 2026.10.10.5554)
[^ksa-parttree]: KSA.PartTree (decompiled, build 2026.10.10.5554)
[^ksa-resourcegroups]: KSA.ResourceGroups (decompiled, build 2026.10.10.5554)
[^ksa-gaugecanvas]: KSA.GaugeCanvas (decompiled, build 2026.10.10.5554)
