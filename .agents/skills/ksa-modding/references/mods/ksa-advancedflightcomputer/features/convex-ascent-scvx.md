---
type: Mod Feature
title: Minimum-propellant ascent planned offline with SCvx and flown against air-relative speed
description: Build a 3-DOF ascent problem from the vehicle on the main thread as plain data, solve it with successive convexification and Clarabel on its own thread, publish an immutable plan, and fly its pitch and azimuth against air-relative speed until UPFG takes over
mod: ksa-advancedflightcomputer
category: flight-and-orbits
techniques: [native-interop, background-thread, harmony-prefix, custom-physics]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers, thread:background, frame:draw-ui]
game_types: [KSA.SequencePerformanceList, KSA.SolidMotor, KSA.Vehicle, KSA.PartTree]
tags: [ascent, scvx, convex-optimization, background-thread, solid-motors, max-q, upfg]
---

As an alternative to the vertical rise and fixed-rate gravity turn of the [UPFG ascent](upfg-ascent-guidance.md),
AFC can compute a minimum-propellant ascent for the actual vehicle (a port of a `launch3dof.py`
reference script solved by SCvx): staging, thrust against back pressure, its own measured drag, the
body's atmosphere and a max-q limit. **Calculate ascent** solves it without launching; **EXECUTE** flies
it open loop until the vehicle is high enough for UPFG to finish the insertion. The pattern that matters
for other mods: gather everything from the game on the main thread as plain data, solve for seconds on a
private thread, publish an immutable result.

## How it works

### Request, build, solve, publish

1. The panel sets a request; the focused vehicle's per-vehicle step (the `PrepareWorker` prefix) picks it
   up, because that is where the part tree can be read without racing the game's own recompute.
2. The step builds the problem as plain data: stage model with each stage's thrust sampled at several
   back pressures from vacuum to sea level (fine enough for KSA's flow-separation knee), the drag table
   sampled off the vehicle (see [drag table measurement](drag-table-measurement.md)), the atmosphere, and
   the target orbit.
3. `AscentPlanJob` (in `KsaAscentPlan.cs`) runs the solve on its own background thread
   (`afc-ascent-scvx`, below-normal priority). It receives a `build(stages, maxQ)` delegate and a
   `publish` delegate that both "must touch nothing of the game": they only close over the data captured
   on the main thread, so the job can rebuild the problem for a retry without going back to the game. It
   publishes an immutable `AscentPlan`; the step polls for it. A fault costs the plan, never the step
   ([AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs#L1-L10),
   [AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/Ascent/KsaAscentPlan.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/Ascent/KsaAscentPlan.cs#L420-L470)).
4. The SCvx loop (`AscentScvx`, `AscentSubproblem` solved by Clarabel) reports each iteration (trust
   region, defect, path violation) for the panel.

### Retries that say why

- The first attempt plans only as many stages as the ideal delta-v says orbit needs; a plan that cannot
  reach orbit adds a stage, one whose last stage is pinned at its shortest burn drops it.
- A max-q the vehicle cannot hold (a KSA stack lifting off at 3-5 g climbs through thick air above the
  script's 35 kPa however steeply it goes) is relaxed once, to a factor above the least peak q the
  steepest seed reached; each retry is labelled in the panel.

### Solid motors as a table

KSA's solid burn model is a chain of piecewise-linear tables with a chamber-pressure fixed-point solve
and a nozzle flow-separation branch; only the nozzle exit sees the air. `AscentSolid` samples the game's
own model once into a table of mass flow and thrust at a few back pressures against the stage's clock,
and pins the stage's duration whenever a solid burns, so time never needs differentiating and the table's
slope jumps cost nothing ([AdvancedFlightComputer/Features/Guidance/Scvx/Ascent/AscentSolid.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Scvx/Ascent/AscentSolid.cs#L1-L20)).

### Flying it

EXECUTE flies pitch and azimuth against **air-relative speed** (`ConvexAscentProfile`), not the plan's
clock, until the vehicle is above the hand-over altitude (or the plan's speed runs out) and UPFG has
converged; the attitude then blends to UPFG's over a short time. A plan is refused if the vehicle has
moved too far or its mass changed too much since the solve, or it is flown too long after the lift-off
instant it was solved for.

## Lifecycle and timing

| Work | Lifecycle | Thread |
|---|---|---|
| Request pick-up, problem build, poll | `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`) | Main |
| SCvx + Clarabel | `thread:background` (`afc-ascent-scvx`) | Mod-owned |
| Plan drawing and EXECUTE | Guidance panel (`frame:draw-ui`) | Main |

The only shared object is the immutable `AscentPlan` handed back by reference.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs) | Mode: request, collect, fly, hand-over |
| [AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/Ascent/KsaAscentPlan.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/Ascent/KsaAscentPlan.cs) | Problem build and solve thread, retries |
| [AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/Ascent/KsaAscentStages.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/Ascent/KsaAscentStages.cs) | Stage and thrust tables from the game |
| [AdvancedFlightComputer/Features/Guidance/Scvx/Ascent/AscentScvx.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Scvx/Ascent/AscentScvx.cs) | SCvx loop |
| [AdvancedFlightComputer/Features/Guidance/Scvx/Ascent/AscentSubproblem.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Scvx/Ascent/AscentSubproblem.cs) | Cone program per iteration (Clarabel) |
| [AdvancedFlightComputer/Features/Guidance/Scvx/Ascent/AscentSolid.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Scvx/Ascent/AscentSolid.cs) | Solid motor tables |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Guidance/GuidanceConvexAscentTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Guidance/GuidanceConvexAscentTest.cs) | Headless test |

## Snippets

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/Ascent/KsaAscentPlan.cs L447-L466
    /// <param name="build">The problem for a number of stages and a max-q, Pa. Must touch nothing of the game.</param>
    /// <param name="available">Stages the vehicle has.</param>
    /// <param name="initial">Stages to try first.</param>
    /// <param name="qMax">The max-q asked for, Pa.</param>
    /// <param name="publish">Wraps the chosen solution, its stage count, the max-q it was planned to, the attempt notes and the wall time in a plan; run on the worker, so it too must touch nothing of the game.</param>
    public AscentPlanJob(Func<int, double, AscentProblem> build, int available, int initial, double qMax,
                         Func<AscentSolution, int, double, string[], double, AscentPlan> publish)
    {
        _build = build;
        _qMax = qMax;
        _available = available;
        _initial = Math.Clamp(initial, 1, available);
        _publish = publish;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "afc-ascent-scvx",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
```

## Game APIs touched

| Member | Use |
|---|---|
| `PartTree.PerformanceSequences` / `SequencePerformance` ([current/decomp/KSA/SequencePerformanceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequencePerformanceList.cs)) | Stage model |
| `SolidMotor` thrust profiles ([current/decomp/KSA/SolidMotor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SolidMotor.cs)) | Solid motor tables |
| Body atmosphere and rotation | Air-relative speed and drag |

## Pitfalls and version notes

- Never read the part tree from the solve thread; build plain data first.
- Do not fly an offline plan by its clock; fly it against a monotonic physical variable (air-relative speed)
  so a slower or faster climb stays on the profile.
- An unattainable max-q makes the problem infeasible and SCvx hides it in virtual control; detect and relax.

## See also

- [UPFG ascent guidance](upfg-ascent-guidance.md)
- [Native conic solver interop](native-conic-solver-interop.md)
- [6-DOF SCvx landing MPC](scvx-6dof-landing-mpc.md)
- [Plan a propellant-optimal ascent with SCvx on a private thread and fly the published plan](../../../howto/flight-and-orbits/guidance/fly-closed-loop-ascent-guidance/fly-closed-loop-ascent-guidance-2.md) - how-to article demonstrating this feature
- [Build plain data on the main thread, solve on a worker, publish an immutable plan](../../../howto/flight-and-orbits/guidance/run-an-expensive-solve-without-stalling-the-frame/run-an-expensive-solve-without-stalling-the-frame-1.md) - how-to article demonstrating this feature

