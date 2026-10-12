---
type: How-To
title: Turn staging phases into constant-thrust stages for guidance
description: Recompute the game's stage model from the PrepareWorker prefix, convert each phase to a constant-thrust stage, coalesce artificial splits, rescale for ambient pressure and repair double-registered engines
category: vehicles-and-parts
group: staging-and-engines
topic: read-staged-delta-v-from-the-game
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/stage-model-from-staging-simulator]
techniques: [game-simulator-reuse, harmony-prefix]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers, frame:draw-ui]
game_types:
  - KSA.PartTree
  - KSA.SequencePerformanceList
  - KSA.SequencePerformance
  - KSA.SequencePhaseInfo
  - KSA.SequenceList
  - KSA.RocketNozzle
  - KSA.Vehicle
tags: [guidance, stage-model, phases, coalesce, ambient-pressure, upfg]
---
Outcome: per-stage thrust, exhaust velocity and masses suitable for a guidance algorithm (UPFG, a convex ascent planner),
taken from the game's staging simulator instead of a hand-written tank and engine walk. KSA-AdvancedFlightComputer (AFC)
reuses `SequencePerformanceList` and folds its output into constant-thrust stages. The game's simulator already handles what a
tank walk gets wrong: solid grain lives in solid-grain segments rather than tanks, part of it is unburnable, and thrust follows
the burning area.

Prerequisites: StarMap and Harmony; guidance that consumes stages. Demonstrated by
[ksa-advancedflightcomputer/stage-model-from-staging-simulator](../../../../mods/ksa-advancedflightcomputer/features/stage-model-from-staging-simulator.md).

## Background: how the game does it

See the [overview](overview.md). Facts specific to this recipe:

- A sequence is split into `SequencePhaseInfo` phases wherever the set of burning cores changes; each phase has `Thrust`,
  `MassFlowRate`, `Duration` and `ActiveEngineCount` [current/decomp/KSA/SequencePhaseInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequencePhaseInfo.cs#L1-L14)[^ksa-sequencephaseinfo]. Spent sequences (below
  `SequenceList.ActiveSequence`) carry no phases.
- The headline `Thrust` / `MassFlowRate` of a sequence are live throttled values for the active sequence while its delta-v
  comes from the design-condition drain, so use the phases.
- Each sequence's `WetMass` already accounts for the burns and jettisoned structure of every earlier sequence, so re-seed
  the running mass from it per sequence.
- The drain uses each sequence's own `Environment` (0 or 101325 Pa), not the craft's pressure, so the burning stage needs its
  own rescale to the real back pressure.
- `SequenceList.ActivateNextSequence`-time and `Recompute` registration: `SnapshotSequenceParts` lists a part under every
  sequence one of its sequenced modules belongs to, and `Recompute` registers the engines of every listed part for each
  sequence up to the one it computes, with no de-duplication across sequences [current/decomp/KSA/SequencePerformanceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequencePerformanceList.cs#L105-L160)[^ksa-sequenceperformancelist]. A
  part with an engine in one sequence and a decoupler in a later one is registered twice from the later sequence on.

## Lifecycle and timing

`RefreshStageModel` runs from the guidance step in the `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`, main thread),
gated to about four refreshes per second of **wall-clock** time (a sim-time gate would fire every step under time warp). The
game's own recompute for the controlled vehicle runs on the vehicle worker while the gauge canvases are enabled; calling
yours from the prefix, before the next worker job is queued, keeps it off that worker's timeline and after
`JobSystems.VehicleSolver.Wait()`. A refresh that catches the part tree mid-rebuild throws; log once and keep the previous
snapshot. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md) and [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../../vehicles/change-vehicles-without-racing-the-solver/overview.md) article 5.

## Steps

1. **Recompute in flight mode** with the live ambient pressure, then build your model and latch the game's own total beside
   it for a sanity check:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs L447-480
    private static void RefreshStageModel(Vehicle vehicle)
    {
        // No cross-vehicle invalidation any more. The snapshot lives on the vehicle it describes, so switching craft reads a different one rather than reading someone else's staging for an interval - which is what the old _stageModelVehicle field existed to prevent.

        // Wall-clock gated, not sim-time gated: under warp sim time elapses instantly and this would run every step.
        long now = GuidanceClock.NowMs;
        if (!_s.StageModelDirty && now - _s.StageModelTick < StageModelIntervalMs)
            return;
        _s.StageModelTick = now;
        _s.StageModelDirty = false;
#if DEBUG
        using var _perf = new PerfTracker.Scope("Guidance.RefreshStageModel");
#endif

        // A staging frame can catch the part tree mid-rebuild. Losing one refresh is harmless, because the previous snapshot stays valid, but letting it escape would skip the attitude command for that step, which is not. The first failure retries on the next step, and a repeat waits for the normal interval, so a refresh that keeps failing does not throw on every step.
        try
        {
            // This pressure moves only the headline figures of the active sequence, not the phases Build reads, so Build scales the burning stage itself.
            SequencePerformanceList performance = vehicle.Parts?.PerformanceSequences;
            if (performance == null || vehicle.Parts.SequenceList == null)
                return;
            float ambientPressure = vehicle.PhysicsEnvironment.AtmosphericPressure;
            performance.RecomputeForFlight(ambientPressure);
            _s.StageModel = KsaVehicleAdapter.Build(vehicle, ambientPressure);
            // The game's own total for the same recompute, latched for the panel to check our stage list against. Both are "from here on" - its simulated mole masses are re-seeded from the live tanks every recompute - so a disagreement means the adapter is reading the sequence list wrongly, which is exactly the failure that is invisible in a plausible-looking stage table. TotalDeltaV is a Volatile.Read of a float, so the draw thread can have this even though the Lists behind it can tear.
            _s.StageModelKsaDv = performance.TotalDeltaV;

            // One line when the staging changes shape, not one per refresh.
            if (GuidanceLog.Enabled)
            {
                string signature = GuidanceLog.StageSignature(_s.StageModel);
                if (signature != _s.StageModelSignature)
                {
                    _s.StageModelSignature = signature;
```

2. **Read every sequence from index 0, phase by phase.** Skip phases with non-positive thrust, flow or duration; a phase
   becomes a constant-thrust stage with `Isp = thrust / (flow * g0)` starting from the running mass; the burnout mass seeds
   the next phase:

```csharp
// AdvancedFlightComputer/Features/Guidance/Upfg/KsaVehicleAdapter.cs L51-95

        UpfgStage burningStage = null;
        HashSet<Part> burningPhaseParts = null;
        double burningModelPressure = 0.0;

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
                    if (pressureGrid != null)
                        SampleThrustVsPressure(tree, stage, PhaseParts(perf, j), ModelPressure(sequences[i].Environment), pressureGrid);
                    HashSet<Part> parts = PhaseParts(perf, j);
                    if (parts != null)
                        foreach (Part part in parts)
                            stage.Throttleable &= FollowsThrottleOnly(part);
```

3. **Coalesce artificial splits.** The drain steps from one tank emptying to the next, so a stack with several tanks that do
   not run dry at the same instant produces several rows for one real stage. Two adjacent stages with the same thrust and
   exhaust velocity and no mass discontinuity are the same stage, and merging is exact because
   `ve * ln(m0/mid) + ve * ln(mid/m1) = ve * ln(m0/m1)`. Then drop stages worth under 1 m/s as numerical dust:

```csharp
// AdvancedFlightComputer/Features/Guidance/Upfg/KsaVehicleAdapter.cs L122-157
    private static void Coalesce(UpfgVehicle vehicle, Dictionary<UpfgStage, HashSet<Part>> stageParts = null)
    {
        List<UpfgStage> stages = vehicle.Stages;
        for (int i = stages.Count - 2; i >= 0; i--)
        {
            UpfgStage a = stages[i], b = stages[i + 1];
            if (!Close(a.Thrust, b.Thrust) || !Close(a.Isp, b.Isp))
                continue;
            // Mass continuity: anything jettisoned between the two is a real stage boundary however alike the two burns look. The tolerance is loose enough to absorb the float arithmetic the game's masses arrive in (10 kg on a 100 t stack) and far tighter than any real separation.
            if (Math.Abs(a.MassDry - b.MassTotal) > 1e-4 * Math.Max(a.MassTotal, 1.0))
                continue;

            a.MassDry = b.MassDry;
            a.Engines = Math.Max(a.Engines, b.Engines);
            a.Throttleable &= b.Throttleable;
            if (stageParts != null && stageParts.TryGetValue(b, out HashSet<Part> merged))
            {
                if (stageParts.TryGetValue(a, out HashSet<Part> into))
                    into.UnionWith(merged);
                stageParts.Remove(b);
            }
            stages.RemoveAt(i + 1);
        }

        // Whatever survives that and still carries no useful dV is numerical dust from the drain simulation's fixed iteration budget. Dropping it is safe: UPFG reads each stage's own four numbers and reconciles stage 0 against the live mass, so nothing downstream depends on the chain being gapless.
        for (int i = stages.Count - 1; i >= 0; i--)
        {
            UpfgStage s = stages[i];
            if (!(s.MassTotal > 0.0) || !(s.MassDry > 0.0) || s.MassDry >= s.MassTotal
                || s.Isp * G0 * Math.Log(s.MassTotal / s.MassDry) < MinStageDv)
            {
                stageParts?.Remove(s);
                stages.RemoveAt(i);
            }
        }
    }
```

4. **Rescale the burning stage to the real back pressure.** Evaluate the burning phase's nozzles at design conditions at the
   model pressure and at the ambient pressure with `RocketNozzle.ComputePerformance`, scale thrust and Isp by the ratio (mass
   flow does not depend on back pressure):

```csharp
// AdvancedFlightComputer/Features/Guidance/Upfg/KsaVehicleAdapter.cs L285-307
    private static void ApplyAmbientPressure(PartTree tree, UpfgVehicle result, UpfgStage stage,
                                             HashSet<Part> phaseParts, double modelPressure, double ambientPressure)
    {
        if (phaseParts == null || phaseParts.Count == 0
            || !double.IsFinite(ambientPressure) || ambientPressure < 0.0)
            return;
        if (Math.Abs(ambientPressure - modelPressure) <= 1.0)
            return;   // not worth two engine sums

        double ambientThrust = PhaseThrustAtPressure(tree, phaseParts, ambientPressure);
        double modelThrust = PhaseThrustAtPressure(tree, phaseParts, modelPressure);
        if (!(ambientThrust > 0.0) || !(modelThrust > 0.0))
            return;

        double ratio = ambientThrust / modelThrust;
        if (!double.IsFinite(ratio) || ratio <= 0.0)
            return;

        // Mass flow does not depend on back pressure (DeLavalNozzleConfig.ComputePerformance), so Isp moves with thrust and the masses stay.
        stage.Thrust *= ratio;
        stage.Isp *= ratio;
        result.BurningStageThrustRatio = ratio;
    }
```

5. **Repair double registration** (`CorrectDuplicateRegistration`): count how many times the game registered each engine part
   the way it lists them (`Part.HasSubtreeSequencedModule`); with a uniform count divide the phase by it and stretch its
   duration to keep the burned mass; with mixed counts re-evaluate the liquid parts once at design conditions and let the
   solids share the rest; stand down when the solids of one phase carry different counts. Thrust and flow double but
   propellant does not, so stock delta-v is right while thrust reads double and burn time half, the two figures guidance steers
   on.
6. **Cross-check** your total against `performance.TotalDeltaV` from the same recompute; a disagreement means the adapter is
   reading the list wrongly, the failure that is invisible in a plausible-looking stage table.

## Complete example

- [AdvancedFlightComputer/Features/Guidance/Upfg/KsaVehicleAdapter.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Upfg/KsaVehicleAdapter.cs)[^ksa-advancedflightcomputer-ksavehicleadapter]
- [AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs#L447-L497)[^ksa-advancedflightcomputer-autopilot]

## Variations and gotchas

- **Why not the headline figures.** They are live and throttled for the active sequence.
- **Pressure grid.** For a planner that needs thrust versus pressure, sample `PhaseThrustAtPressure` at each grid pressure as a
  ratio to the model-pressure thrust so the figure agrees with the stage's own thrust exactly.
- **Warn on sea-level sequences.** A sequence the player set to atmospheric drains at 101325 Pa; report it so the UI can flag
  a model that disagrees with the vacuum plan.
- **Updates.** The adapter's `Phases` reading and the `Recompute` registration behaviour are the two things to re-verify per
  build.

## See also

- [Overview](overview.md), [Article 1](read-staged-delta-v-from-the-game-1.md), [Article 4](read-staged-delta-v-from-the-game-4.md)
- [ksa-advancedflightcomputer/stage-model-from-staging-simulator](../../../../mods/ksa-advancedflightcomputer/features/stage-model-from-staging-simulator.md)
- [Compute engine thrust, Isp and mass flow at ambient pressure and invert for throttle](../compute-engine-thrust-at-ambient-pressure/overview.md), [Ignite engines, activate the next stage, fire a decoupler or undock from code](../stage-a-vehicle-from-code/overview.md)

[^ksa-sequenceperformancelist]: current/decomp/KSA/SequencePerformanceList.cs (decompiled, build 2026.10.10.5554)
[^ksa-advancedflightcomputer-ksavehicleadapter]: KSA-AdvancedFlightComputer: AdvancedFlightComputer/Features/Guidance/Upfg/KsaVehicleAdapter.cs
[^ksa-sequencephaseinfo]: current/decomp/KSA/SequencePhaseInfo.cs (decompiled, build 2026.10.10.5554)
[^ksa-advancedflightcomputer-autopilot]: KSA-AdvancedFlightComputer: AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs
