---
type: How-To
title: Fly a UPFG ascent using a stage list built from the game's SequencePerformance phases
description: Build the multi-stage vehicle model that Space Shuttle style closed-loop ascent guidance needs from the game's staging simulator, solve it once per guidance second, and steer a phase machine from vertical rise to a frozen terminal attitude
category: flight-and-orbits
group: guidance
topic: fly-closed-loop-ascent-guidance
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/upfg-ascent-guidance]
techniques: [harmony-prefix, custom-physics]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers, frame:draw-ui]
game_types:
  - KSA.PartTree
  - KSA.SequencePerformanceList
  - KSA.SequencePerformance
  - KSA.SolidMotor
  - KSA.Vehicle
  - KSA.FlightComputer
  - KSA.Orbit
  - KSA.PhysicsEnvironment
tags: [ascent, upfg, pegas, staging, sequence-performance, solid-motor, guidance-cycle, launch]
---
Outcome: a launch that flies itself to a target periapsis, apoapsis, inclination and longitude of the ascending node (optionally with a fixed argument of periapsis, a g-limit and a booster reserve): vertical rise, a pitch-over, then closed-loop UPFG (the Space Shuttle's Unified Powered Flight Guidance, ported from PEGAS) until cutoff. The guidance algorithm is game-independent; the game-specific work, and what this article covers, is the vehicle model it needs, built from the game's own staging simulator, and the pacing and phasing of the loop. KSA-AdvancedFlightComputer (AFC) is the reference.[UPFG ascent guidance](../../../../mods/ksa-advancedflightcomputer/features/upfg-ascent-guidance.md)
Prerequisites: StarMap and Harmony. Inclination and LAN are measured in the parent's inertial frame (CCI), where +Z is the parent body's pole, not against the ecliptic; see [Understand KSA coordinate frames and epochs](../../flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md) and, for the launch plane and window, [Build a site frame and compute a launch window](../../transfers/build-a-site-frame-and-compute-a-launch-window/overview.md). For the alternative open-loop trajectory see [approach 2](fly-closed-loop-ascent-guidance-2.md).

## Background: how the game does it

- **The staging simulator.** `PartTree.PerformanceSequences` is a `SequencePerformanceList`, one `SequencePerformance` per stage-menu entry, which drives the delta-v and TWR readout. Each entry has a `WetMass` and a list of `SequencePhaseInfo` `Phases` (`Thrust`, `MassFlowRate`, `Duration`, `DeltaV`, `ActiveEngineCount`); a sequence is split into constant-thrust phases wherever the number of burning engines changes (a solid booster burning out under a core).[^ksa-parttree] [^ksa-sequenceperformancelist] [^ksa-sequenceperformance] [current/decomp/KSA/SequencePerformance.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequencePerformance.cs#L5-L31)
- **Recomputation.** `SequencePerformanceList.RecomputeForFlight(float ambientPressure)` recomputes the list, and `TotalDeltaV` reads the total back. In flight the game only recomputes while the stage or engine-control panel is open, so a mod that wants fresh data drives the refresh itself.
- **Solid motors** keep their grain in `SolidMotor` (`VacuumThrustProfile`, `AtmosphericThrustProfile`), which the simulator paces at the mean thrust and flow of the burn they have left, so a tank walk would see no propellant at all.[^ksa-solidmotor]
- **Pressure.** The drain simulation uses each sequence's own `Environment` (vacuum or sea level), not the pressure passed to `RecomputeForFlight`; that pressure only moves the headline figures of the active sequence.

## Lifecycle and timing

- **Where it runs:** the entire ascent runs inside the per-vehicle `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`, main thread). That site runs after the vehicle solver has been waited on, so `RecomputeForFlight` and the copy-out cannot race the game's own recompute on a worker. `Universe.PrepareVehicleWorkers` is the caller.[^ksa-universe] [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2141-L2149)
- **Cadence:** the stage model refreshes about four times a second on the wall clock (so warp does not multiply it); UPFG solves once per guidance cycle of 1.0 simulated seconds; the phase machine and the attitude command run every step.
- **UI:** the Ascent tab and overlays draw in `frame:draw-ui` and only set requests the next step consumes.
- See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Refresh the staging simulation on a wall-clock timer** and build the UPFG stage list from it. Gate on wall-clock milliseconds, because simulation time elapses instantly under warp:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs L444-L455
private const long StageModelIntervalMs = 250;
private const long HousekeepingRetryMs = 1000;

private static void RefreshStageModel(Vehicle vehicle)
{
    // No cross-vehicle invalidation any more. The snapshot lives on the vehicle it describes, so switching craft reads a different one rather than reading someone else's staging for an interval - which is what the old _stageModelVehicle field existed to prevent.

    // Wall-clock gated, not sim-time gated: under warp sim time elapses instantly and this would run every step.
    long now = GuidanceClock.NowMs;
    if (!_s.StageModelDirty && now - _s.StageModelTick < StageModelIntervalMs)
        return;
    _s.StageModelTick = now;
```

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs L464-L470
// This pressure moves only the headline figures of the active sequence, not the phases Build reads, so Build scales the burning stage itself.
SequencePerformanceList performance = vehicle.Parts?.PerformanceSequences;
if (performance == null || vehicle.Parts.SequenceList == null)
    return;
float ambientPressure = vehicle.PhysicsEnvironment.AtmosphericPressure;
performance.RecomputeForFlight(ambientPressure);
_s.StageModel = KsaVehicleAdapter.Build(vehicle, ambientPressure);
```

2. **Read `Phases` from sequence 0, not the headline thrust and Isp.** Each phase becomes a constant-thrust UPFG stage with `Isp = thrust / (massFlow * g0)`; the running mass is re-seeded from each sequence's `WetMass`, because the game's recompute drains its simulated tanks forward through the list:

```csharp
// AdvancedFlightComputer/Features/Guidance/Upfg/KsaVehicleAdapter.cs L46-L50, L64-L87, L106-L113
ReadOnlySpan<Sequence> sequences = sequenceList.Sequences;
ReadOnlySpan<SequencePerformance> performance = performanceList.PerformanceSequences;
int count = Math.Min(sequences.Length, performance.Length);
if (count == 0)
    return result;
// ...
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
// ...
        mass = burnout;
    }
}

Coalesce(result, stageParts);
// Coalesce can drop the first stage, and the phase parts describe that stage only.
if (burningStage != null && result.Stages.Count > 0 && ReferenceEquals(result.Stages[0], burningStage))
    ApplyAmbientPressure(tree, result, burningStage, burningPhaseParts, burningModelPressure, ambientPressure);
```

3. **Scale only the burning stage to the craft's real ambient pressure.** The adapter computes the ratio of the stage's thrust at the ambient pressure to its thrust at the pressure the drain simulation used (mass flow does not depend on back pressure, so Isp moves with thrust) and applies it to the first stage only:

```csharp
// AdvancedFlightComputer/Features/Guidance/Upfg/KsaVehicleAdapter.cs L299-L306
double ratio = ambientThrust / modelThrust;
if (!double.IsFinite(ratio) || ratio <= 0.0)
    return;

// Mass flow does not depend on back pressure (DeLavalNozzleConfig.ComputePerformance), so Isp moves with thrust and the masses stay.
stage.Thrust *= ratio;
stage.Isp *= ratio;
result.BurningStageThrustRatio = ratio;
```

4. **Pace the solve.** UPFG is a recursive once-per-cycle algorithm (its bias term integrates across cycles); called every physics step it winds up 60 times too fast and feeds wobble to the flight computer. Rebuild the model from the live part tree each cycle, apply the reserve and g-limit to the copy, build the target, and step the solver once:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/Ascent.cs L430-L433, L459-L463, L468
if (now - _s.LastSolveTime >= GuidanceCycle)
{
    // Rebuild from the live part tree every cycle so UPFG always sees current masses and the actual remaining staging sequence. No usable thrust is a normal transient during staging (old engine gone, new one not yet active): hold the last solution and wait rather than stopping. The cycle clock is NOT advanced in that case - the next step retries immediately rather than sitting out a whole cycle on a transient.
    UpfgVehicle live = BuildUpfgVehicle(vehicle);
// ...
        var target = UpfgTarget.FromOrbit(_s.PeKm, _s.ApKm, _s.IncDeg, _s.LanDeg, bodyRadius, mu,
            _s.ArgPeFixed ? _s.ArgPeDeg : double.NaN, parent?.GetAtmosphereRadius() ?? 0.0,
            UpfgTarget.RadToDeg(_s.InsertionSearch.Nu));
        // dt is the interval this solve covers, which is what makes the convergence test rate-independent (see UpfgGuidance.Step).
        double solveDt = firstSolve ? 0.0 : now - _s.LastSolveTime;
// ...
            _s.Upfg.Step(r, v, vehicle.TotalMass, mu, target, _s.UpfgVehicle, 1, solveDt);
```

5. **Run the phase machine every step.** `AscentPhase` is `Vertical, Turn, ClosedLoop, Terminal` (and `Profile` for the convex plan). Vertical holds inertial up, the turn pitches over at a fixed rate toward the launch azimuth until the commanded pitch meets UPFG's, then the closed-loop law steers; at `tgo <= 10 s` the attitude is frozen and the cutoff time is counted down from the solve time (not from now):

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/Ascent.cs L563-L572
case AscentPhase.ClosedLoop:
    if (_s.Upfg.Converged && _s.Upfg.Tgo <= TerminalTgo)
    {
        _s.Phase = AscentPhase.Terminal;
        _s.FrozenDir = steerNow;
        _s.ConvexBlendStart = double.NaN;   // a blend still running ends with the burn
        // tgo is measured from the SOLVE, not from now - the solution can be most of a guidance cycle old by the time this trips, and counting that cycle twice is a whole second of extra burn.
        _s.CutoffTime = _s.LastSolveTime + _s.Upfg.Tgo;
        GuidanceLog.Debug(vehicle, $"terminal phase, attitude frozen at pitch {upfgPitch:F1} deg, cutoff in {_s.CutoffTime - SimNow():F1} s.");
    }
```

6. **Slew, do not jump.** The commanded direction slews toward the solution at a capped rate (5 deg/s) and the implied rate is handed to the flight computer as a feed-forward (see [Drive the flight computer and attitude modes from a mod](../../autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/overview.md)).
7. **Choose the insertion point.** With the argument of periapsis free, `UpfgInsertionSearch` measures where on the target ellipse insertion costs least instead of assuming periapsis; with it fixed, the aimed true anomaly follows the predicted cutoff and is frozen 45 s before cutoff.
8. **Limits, window and staging.** The g-limit and structural load cap come from the engine model ([Compute a suicide-burn start altitude and the thrust it needs](../compute-a-suicide-burn/compute-a-suicide-burn-3.md)); `ChaseOrbit` computes plane reachability, a co-elliptic orbit below a target vehicle and the launch window; staging is requested once per flight for cues the plan knows (cold ignition, the reserve boundary) through the auto-stager.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Modes/Ascent.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Ascent.cs) | phase machine, solve pacing, attitude command |
| [AdvancedFlightComputer/Features/Guidance/Upfg/UpfgGuidance.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Upfg/UpfgGuidance.cs) | UPFG port (game-independent) |
| [AdvancedFlightComputer/Features/Guidance/Upfg/KsaVehicleAdapter.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Upfg/KsaVehicleAdapter.cs) | stage list from `SequencePerformance.Phases` |
| [AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs#L444-L495) | stage model refresh |
| [AdvancedFlightComputer/Features/Guidance/Modes/ChaseOrbit.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/ChaseOrbit.cs) | chase orbit and launch window |

## Variations and gotchas

- **Never combine the headline `Thrust` / `MassFlowRate`** (live throttled values for the active sequence) **with `DeltaV`** (design-condition drain simulation).
- **Run recursive guidance at its design cadence**, not per physics step.
- **A failed refresh keeps the previous snapshot**, which is how AFC survives a staging frame that catches the part tree mid-rebuild; never let the exception escape the prefix, or the attitude command for that step is skipped.
- **A sequence the player set to sea level** is flagged in the UI, because the drain simulation then uses sea-level thrust for it.
- **Dust phases** (under 1 m/s of delta-v or a millisecond of burn) are dropped, but their mass change is carried forward so the stage chain stays continuous.
- The author reports in-game flights only for single-craft ascent from this tree.

## See also

- [Overview](overview.md); [Convex ascent with SCvx](fly-closed-loop-ascent-guidance-2.md)
- [UPFG ascent guidance (mod feature)](../../../../mods/ksa-advancedflightcomputer/features/upfg-ascent-guidance.md)
- [Run an autopilot for every vehicle each step](../../autopilot/run-an-autopilot-for-every-vehicle-each-step/overview.md)
- [Reading staged delta-v, thrust and Isp from the game or from your own stage model](../../../vehicles-and-parts/staging-and-engines/read-staged-delta-v-from-the-game/overview.md)

[^ksa-parttree]: KSA.PartTree (decompiled, build 2026.10.10.5554)
[^ksa-sequenceperformancelist]: KSA.SequencePerformanceList (decompiled, build 2026.10.10.5554)
[^ksa-sequenceperformance]: KSA.SequencePerformance (decompiled, build 2026.10.10.5554)
[^ksa-solidmotor]: KSA.SolidMotor (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
