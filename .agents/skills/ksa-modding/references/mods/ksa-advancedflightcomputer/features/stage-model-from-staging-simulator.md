---
type: Mod Feature
title: Per-stage thrust, Isp and burn time from the game's staging simulator
description: Turn PartTree.PerformanceSequences, the drain simulation behind the stage menu, into constant-thrust stages for guidance by reading its phases, repairing double-registered engines, merging artificial phase splits and rescaling the burning stage to the craft's real back pressure
mod: ksa-advancedflightcomputer
category: vehicles-and-parts
techniques: [game-simulator-reuse]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers]
game_types:
  - KSA.PartTree
  - KSA.SequenceList
  - KSA.Sequence
  - KSA.SequencePerformanceList
  - KSA.SequencePerformance
  - KSA.SequencePhaseInfo
  - KSA.PerformanceEnvironment
  - KSA.Part
  - KSA.RocketNozzle
  - KSA.SolidMotor
tags: [staging, delta-v, isp, sequences, solid-motor, guidance, engines]
---

Any mod that plans a burn across stages needs per-stage thrust, exhaust velocity and masses. Rather than
walk tanks and engines itself, AFC reuses the game's staging simulator, the `SequencePerformanceList`
behind the stage menu's delta-v and TWR readout, and folds its output into the constant-thrust stages
UPFG and the convex ascent planner expect ([AdvancedFlightComputer/Features/Guidance/Upfg/KsaVehicleAdapter.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Upfg/KsaVehicleAdapter.cs#L10-L22)).
The game's simulator already handles what a tank walk gets wrong: solid grain lives in solid-grain segments
rather than tanks, part of it is unburnable, and thrust follows the burning area.

## How it works

1. **Recompute in flight mode.** `PartTree.PerformanceSequences.RecomputeForFlight(ambientPressure)` seeds
   simulated mole masses from the live tanks once and drains them forward through every sequence
   ([current/decomp/KSA/SequencePerformanceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequencePerformanceList.cs#L103-L130)).[^ksa-sequenceperformancelist]
2. **Read every sequence from index 0, phase by phase.** A sequence is split into `SequencePhaseInfo`
   phases (`Thrust`, `MassFlowRate`, `Duration`, `ActiveEngineCount`) wherever the set of burning cores
   changes. Each phase with positive thrust, flow and duration becomes a UPFG Mode-1 stage with
   `Isp = thrust / (flow * g0)`, starting from the sequence's `WetMass`. Spent sequences (below
   `SequenceList.ActiveSequence`) carry no phases and are stepped over. The headline `Thrust` and
   `MassFlowRate` of a sequence are never used, because for the active sequence they are live throttled
   values while its delta-v comes from the design-condition drain.
3. **Repair double registration** (`CorrectDuplicateRegistration`, below).
4. **Coalesce.** Adjacent stages with the same thrust and Isp (within 0.1 %) and no mass discontinuity are
   merged, which is exact because `ve * ln(m0/mid) + ve * ln(mid/m1) = ve * ln(m0/m1)`; it removes the
   extra rows the drain creates when several tanks empty at different instants. Stages under 1 m/s are
   dropped as numerical dust.
5. **Rescale the burning stage to the real back pressure.** The drain uses each sequence's own
   `Environment` (Vacuum = 0 Pa, Atmospheric = 101325 Pa), not the craft's pressure. `ApplyAmbientPressure`
   evaluates the burning phase's nozzles at design conditions at both pressures with
   `RocketNozzle.ComputePerformance` and scales thrust and Isp by the ratio (mass flow does not depend on
   back pressure). With a pressure grid, every stage also gets a thrust-versus-pressure table for the
   convex ascent planner.
6. **Cross-check.** The game's own `TotalDeltaV` from the same recompute is latched next to the model so
   the panel can compare them; a stage-shape signature drives one debug log line per change
   ([AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs#L447-L497)).

### The double-registration repair

`SnapshotSequenceParts` lists a part under every sequence one of its (subtree) sequenced modules belongs
to, and `Recompute` registers the engines of every listed part for each sequence from 0 up to the one it
computes, with no de-duplication across sequences ([current/decomp/KSA/SequencePerformanceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequencePerformanceList.cs#L190-L240),
[current/decomp/KSA/SequencePerformanceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequencePerformanceList.cs#L293-L322)). A part with an engine in one sequence and
another sequenced module (a decoupler) in a later one is therefore registered twice from that later
sequence on: thrust and flow double, propellant does not, so the stock delta-v stays right while thrust
reads double and burn time half. `RegistrationCount` mirrors the game's listing with
`Part.HasSubtreeSequencedModule`; with a uniform count the phase is divided by it and its duration stretched
to keep the burned mass, with mixed counts the liquid parts are re-evaluated once at design conditions and
the solids share the rest, and the repair stands down when it cannot explain the phase.

## Lifecycle and timing

`RefreshStageModel` runs from the guidance step in the `Vehicle.PrepareWorker` prefix
(`frame:dispatch-solvers`, main thread), gated to about four refreshes per second of wall-clock time
(`GuidanceClock`), because under time warp a sim-time gate would fire every step. The game itself recomputes
the controlled vehicle's list on the vehicle worker in `PhysicsBubble.RunVehiclePostWorkInner`, but only
while the `Sequence` gauge canvas (`ResourceGroups.IsOpen` is `Program.SequenceCanvas.Enabled`) or the
`EngineControl` gauge canvas (`Program.EngineControlGaugeOpen`) is enabled ([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L793-L796),
[current/decomp/KSA/ResourceGroups.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ResourceGroups.cs#L15)). Both canvases start enabled
(`GaugeCanvas._enabled = true`, [current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L143)), so with the
default UI that recompute runs on every vehicle step.[^ksa-physicsbubble][^ksa-resourcegroups][^ksa-gaugecanvas]
Calling it from the prefix, before the vehicle's next worker job is queued, keeps AFC's recompute off that
worker's timeline. A refresh that catches the part tree mid-rebuild throws; the exception is logged once and
the previous snapshot stays in use.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Upfg/KsaVehicleAdapter.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Upfg/KsaVehicleAdapter.cs) | Phases to stages, duplicate repair, coalescing, pressure scaling |
| [AdvancedFlightComputer/Features/Guidance/Upfg/UpfgVehicle.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Upfg/UpfgVehicle.cs) | The stage record (`UpfgStage`) |
| [AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs) | `RefreshStageModel`: cadence, recompute, latch, error handling |
| [AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/Ascent/KsaAscentStages.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/Ascent/KsaAscentStages.cs) | Convex-ascent variant that splits solids from liquids per stage |
| [AdvancedFlightComputer/Features/Guidance/GuidanceLog.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/GuidanceLog.cs) | Stage-list description and 5 %-bucket signature |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Guidance/GuidanceDuplicateRegistrationTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Guidance/GuidanceDuplicateRegistrationTest.cs) | Registration count against the game's own per-sequence part lists |

## Snippets

```csharp
// AdvancedFlightComputer/Features/Guidance/Upfg/KsaVehicleAdapter.cs L56-L89
        for (int i = 0; i < count; i++)
        {
            SequencePerformance perf = performance[i];
            List<SequencePhaseInfo> phases = perf.Phases;
            if (phases == null)
                continue;

            // Each sequence's WetMass already accounts for the burns and the jettisoned structure of every sequence before it, so the running mass is re-seeded here rather than carried across the boundary.
            double mass = perf.WetMass;
            for (int j = 0; j < phases.Count; j++)
            {
                SequencePhaseInfo phase = phases[j];
                double thrust = phase.Thrust;
                double massFlow = phase.MassFlowRate;
                double duration = phase.Duration;
                if (!(thrust > 0.0) || !(massFlow > 0.0) || !(duration > 0.0))
                    continue;

                CorrectDuplicateRegistration(tree, sequences, perf, i, j, ref thrust, ref massFlow, ref duration);

                double burnout = mass - massFlow * duration;
                if (duration >= MinPhaseSeconds && mass > 0.0 && burnout > 0.0)
                {
                    var stage = new UpfgStage
                    {
                        Mode = 1,
                        Thrust = thrust,
                        Isp = thrust / (massFlow * G0),
                        MassTotal = mass,
                        MassDry = burnout,
                        GLim = 1e9,
                        Seq = i,
                        Engines = phase.ActiveEngineCount,
                    };
```

```csharp
// AdvancedFlightComputer/Features/Guidance/Upfg/KsaVehicleAdapter.cs L241-L251
    // How many times Recompute registers this part's engines for the sequence at seqIdx: once per sequence up to it that lists the part, which SnapshotSequenceParts decides by the sequenced modules of the part and its sub-parts.
    internal static int RegistrationCount(ReadOnlySpan<Sequence> sequences, int seqIdx, Part part)
    {
        if (part == null || !part.IsSequenceable)
            return 0;
        int count = 0;
        for (int n = 0; n <= seqIdx && n < sequences.Length; n++)
            if (part.HasSubtreeSequencedModule(sequences[n].Number))
                count++;
        return count;
    }
```

## Game APIs touched

| Member | Use |
|---|---|
| `PartTree.PerformanceSequences` ([current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L58)) | The staging simulator instance |
| `SequencePerformanceList.RecomputeForFlight`, `PerformanceSequences`, `TotalDeltaV`, `SnapshotSequenceParts` (private) ([current/decomp/KSA/SequencePerformanceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequencePerformanceList.cs#L65-L110)) | Recompute and read results |
| `SequencePerformance.Phases`, `PhaseEngineParts`, `WetMass` ([current/decomp/KSA/SequencePerformance.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequencePerformance.cs#L17-L33)) | Constant-thrust phases and their engine parts |
| `SequencePhaseInfo` ([current/decomp/KSA/SequencePhaseInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequencePhaseInfo.cs)) | Thrust, flow, duration, engine count |
| `SequenceList.Sequences`, `ActiveSequence`; `Sequence.Number`, `Environment` ([current/decomp/KSA/SequenceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequenceList.cs#L131), [current/decomp/KSA/Sequence.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Sequence.cs#L11-L21)) | Sequence order and per-sequence vacuum/sea-level setting |
| `Part.IsSequenceable`, `HasSubtreeSequencedModule(int)` ([current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L2611)) | Mirror of the game's per-sequence part listing |
| `RocketNozzle.ComputePerformance(in RocketCoreConditions, float)` ([current/decomp/KSA/RocketNozzle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketNozzle.cs#L150)) | Design-condition thrust at a chosen back pressure |

## Pitfalls and version notes

- The README says the mod paces partly burned solid motors at the mean flow of the burn they have left. In
  the decompiled build 2026.10.10.5554 that pacing is in the game itself
  (`SequencePerformanceList.RegisterDrainCore` -> `TryComputeSolidRemainingBurnRate`, reading
  `SolidMotor.VacuumThrustProfile` or `AtmosphericThrustProfile`), and the adapter's own comment says it
  relies on the game for it ([current/decomp/KSA/SequencePerformanceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequencePerformanceList.cs#L425-L450)).
- The duplicate-registration repair depends on a private quirk of `Recompute`; if the game de-duplicates,
  the repair becomes a no-op (counts drop to 1), which is safe.
- A sequence the player set to Atmospheric stays at sea-level figures for later stages;
  `AnyAtmosphericSequence` lets the UI flag it rather than overwrite a saved player setting.
- Thrust and Isp of later stages are design-condition vacuum or sea-level values; only the burning stage
  is corrected to the current pressure (see [thrust at ambient pressure](engine-performance-and-throttle-inversion.md)
  for live figures).

## See also

- [UPFG ascent guidance](upfg-ascent-guidance.md)
- [Minimum-propellant convex ascent](convex-ascent-scvx.md)
- [Thrust at ambient pressure and throttle inversion](engine-performance-and-throttle-inversion.md)
- [Automatic staging](automatic-staging.md)
- [Turn staging phases into constant-thrust stages for guidance](../../../howto/vehicles-and-parts/staging-and-engines/read-staged-delta-v-from-the-game/read-staged-delta-v-from-the-game-3.md) - how-to article demonstrating this feature

[^ksa-sequenceperformancelist]: KSA.SequencePerformanceList (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^ksa-resourcegroups]: KSA.ResourceGroups (decompiled, build 2026.10.10.5554)
[^ksa-gaugecanvas]: KSA.GaugeCanvas (decompiled, build 2026.10.10.5554)
