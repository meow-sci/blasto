---
type: How-To
title: Plan a propellant-optimal ascent with SCvx on a private thread and fly the published plan
description: Gather the stage model, drag table, atmosphere and solid-motor tables on the main thread, solve a minimum-propellant ascent for seconds on a background thread, publish an immutable plan and fly it against air-relative speed until UPFG takes over
category: flight-and-orbits
group: guidance
topic: fly-closed-loop-ascent-guidance
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/convex-ascent-scvx]
techniques: [background-thread, custom-physics, harmony-prefix, native-interop]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers, thread:background, frame:draw-ui]
game_types:
  - KSA.SequencePerformanceList
  - KSA.SolidMotor
  - KSA.Vehicle
  - KSA.PartTree
  - KSA.IParentBody
  - KSA.PhysicsEnvironment
tags:
  - ascent
  - scvx
  - convex-optimization
  - background-thread
  - immutable-plan
  - max-q
  - air-relative-velocity
  - solid-motor
---
Outcome: a minimum-propellant ascent computed for the actual vehicle (staging, thrust against back pressure, measured drag, the body's atmosphere and a max-q limit) instead of the fixed vertical rise and gravity turn of [approach 1](fly-closed-loop-ascent-guidance-1.md). A "Calculate" request solves without launching; execute flies the plan open loop until the vehicle is high enough for UPFG to finish the insertion. The pattern that matters for other mods: gather everything from the game on the main thread as plain data, solve for seconds on a private thread, publish an immutable result. KSA-AdvancedFlightComputer (AFC) is the reference.[Minimum-propellant convex ascent](../../../../mods/ksa-advancedflightcomputer/features/convex-ascent-scvx.md)
Prerequisites: StarMap and Harmony; the SCvx/Clarabel solver is native and treated as a black box here.

## Background: how the game does it

- **Stage model.** `PartTree.PerformanceSequences` / `SequencePerformanceList.RecomputeForFlight` give the phase list, as in approach 1; thrust against back pressure is sampled by evaluating the engine model at a grid of pressures from vacuum to sea level, fine enough for KSA's nozzle flow-separation knee.[^ksa-parttree] [^ksa-sequenceperformancelist]
- **Solid motors.** A `SolidMotor` has a `Stack` of grain `Segments` and its own `UpdateState` and `ConsumePropellant`; AFC steps a copy of the grain through the same functions about 600 times (midpoint rule) to tabulate mass flow and thrust at several back pressures against the stage clock.[^ksa-solidmotor]
- **The rotating body.** `IParentBody.GetAngularVelocity()` is the spin about CCI +Z; air-relative velocity is inertial velocity minus omega cross r. [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L78-L78)

## Lifecycle and timing

| Work | Lifecycle | Thread |
|---|---|---|
| Request pick-up, problem build, poll for the result | `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`) | Main |
| SCvx + Clarabel | `thread:background` (`afc-ascent-scvx`, below-normal priority) | Mod-owned |
| Plan drawing and EXECUTE | guidance panel (`frame:draw-ui`) | Main |

The panel only sets a request; the vehicle's step picks it up because the part tree can be read there without racing the game's own recompute. The only shared object afterwards is the immutable plan handed back by reference. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Run an expensive solve without stalling the frame](../run-an-expensive-solve-without-stalling-the-frame/overview.md).

## Steps

1. **Pick up the request in the vehicle step, build plain data.** Copy everything the solver needs: body constants, a drag table measured off the active aero model (see [Predict impact points with drag and build a ballistic computer](../predict-impact-point-and-build-a-ballistic-computer/overview.md)), the atmosphere, the staging model with thrust sampled over a pressure grid, and per-solid-motor tables:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs L226-L254
if (!KsaAeroSweep.TryBuild(vehicle, parent, SimNow(), out KsaAeroSweep.Result aero, out string aeroError))
{
    error = "no drag model (" + aeroError + ")";
    return false;
}
ExponentialAtmosphere atmosphere = aero.Atmosphere;
AscentAtmosphere air = atmosphere != null
    ? new KsaAscentAtmosphere(atmosphere, aero.Table)
    : VacuumAscentAtmosphere.Instance;

double[] grid = null;
if (atmosphere != null)
{
    grid = new double[ThrustPressureSamples];
    for (int g = 0; g < grid.Length; g++)
        grid[g] = atmosphere.SeaLevelPressure * g / (grid.Length - 1);
}

// The staging model, as RefreshStageModel builds it, with thrust sampled across back pressure, and each stage's engines so its solids can be told from its liquids.
SequencePerformanceList performance = vehicle.Parts?.PerformanceSequences;
if (performance == null || vehicle.Parts.SequenceList == null)
{
    error = "the vehicle has no staging sequence";
    return false;
}
float ambient = vehicle.PhysicsEnvironment.AtmosphericPressure;
performance.RecomputeForFlight(ambient);
var stageParts = new Dictionary<UpfgStage, HashSet<Part>>();
UpfgVehicle model = KsaVehicleAdapter.Build(vehicle, ambient, grid, stageParts);
```

2. **Sample each solid motor from the game's own model**, not from the headline numbers, and stamp the table against the stage clock:

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/Ascent/KsaAscentStages.cs L154-L168, L173-L174, L181, L224-L230
internal static AscentSolidMotor SimulateBurn(PartTree tree, SolidMotor motor, double[] grid)
{
    if (tree?.Moles == null || tree.RocketNozzles == null || tree.RocketCores == null
        || !motor.Stack.IsValid || motor.Rocket?.Nozzles == null || motor.Stack.Segments.Length == 0)
        return null;

    SolidGrainSegment[] segments = motor.Stack.Segments;
    ReadOnlySpan<MoleState> moles = tree.Moles.States;
    var mass = new double[segments.Length];
    for (int s = 0; s < segments.Length; s++)
        mass[s] = segments[s].Grain != null ? moles[segments[s].Grain.StatesIdx].Mass : 0.0;

    RocketCoreState live = tree.RocketCores.States[motor.StatesIdx];
    bool lit = live.Throttle > 0f;
    float warm = lit && live.Conditions.Core.Pressure > 0f ? live.Conditions.Core.Pressure : motor.MaxChamberPressure;
// ...
    // The game's own reckoning of the full grain's burn, to size the step; the grain over the ignition flow if it has none.
    double estimate = motor.VacuumThrustProfile.TotalBurnSeconds;
// ...
    double dt = Math.Clamp(estimate / 600.0, 1e-3, 0.5);
// ...
    return new AscentSolidMotor
    {
        Name = motor.TemplateId,
        Time = time.ToArray(),
        MassFlow = flows.ToArray(),
        Thrust = thrust.ToArray(),
    };
```

3. **Capture everything the result records before dispatch.** The publisher delegate runs on the worker and must not touch the game, so ids, names, the launch pad position and the target orbit are copied into locals first:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs L375-L382
// Everything the plan records about what it was for, captured now: the publisher runs on the worker and must not touch the game.
string vehicleId = vehicle.Id;
string bodyName = parent.Id;
double solvedAt = launchAt;
double3 startCcf = rNow.Transform(parent.GetCci2Ccf());
double peKm = _s.PeKm, apKm = _s.ApKm, incDeg = _s.IncDeg, lanDeg = _s.LanDeg;
double qMaxKpa = qMax / 1000.0;
double insAltKm = (rIns - bodyRadius) / 1000.0;
```

4. **Start the job with `build` and `publish` delegates that close over data only.** The thread rebuilds the problem for retries without going back to the game:

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

5. **Retry with a reason, never silently.** The first attempt plans only as many stages as the ideal delta-v needs; a plan that cannot reach orbit adds a stage, one whose last stage is pinned at its shortest burn drops it. A max-q the vehicle cannot hold (a KSA stack lifting off at 3 to 5 g climbs through thick air above a conventional 35 kPa limit however steeply it goes) is relaxed once, to 1.15 times the least peak q the steepest seed reached, and each retry is labelled. Unattainable constraints make SCvx hide infeasibility in virtual control, so detect and relax them:

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/Ascent/KsaAscentPlan.cs L537-L552
// Every seed climbed through more than the limit: re-plan the same stages, once, to a limit the vehicle can hold.
if (!relaxed && double.IsFinite(sol.SeedLeastMaxQ) && sol.SeedLeastMaxQ > 0.98 * qMax)
{
    double held = Math.Ceiling(QRelaxFactor * sol.SeedLeastMaxQ / 5000.0) * 5000.0;
    lock (_gate)
    {
        _notes.Add($"did not converge at max q {qMax / 1000.0:F0} kPa, which is out of reach at full throttle - the steepest climb reaches "
                 + $"{sol.SeedLeastMaxQ / 1000.0:F0} kPa - so retrying with looser constraints, max q {held / 1000.0:F0} kPa");
        _retry = $"Did not converge: max q {qMax / 1000.0:F0} kPa is out of this vehicle's reach at full throttle (its steepest climb reaches "
               + $"{sol.SeedLeastMaxQ / 1000.0:F0} kPa). Retrying with looser constraints: max q {held / 1000.0:F0} kPa.";
    }
    qMax = held;
    relaxed = true;
    tried.Remove(stages);
    continue;
}
```

6. **Poll on later steps.** A fault costs the plan, never the step:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs L61-L68
private static void StepAscentPlannerCore(Vehicle vehicle, Orbit orbit, IParentBody parent)
{
    AscentPlanJob job = _s.AscentPlanJob;
    if (job != null && job.IsDone)
    {
        _s.AscentPlanJob = null;
        AscentPlan plan = job.Result;
        string failure;
```

7. **Lift-off state.** On the pad the air-relative velocity is zero and the angle of attack is meaningless, so the plan starts the vehicle rising at 10 m/s relative to the ground; a launch armed for a window advances the pad position by the body's spin until lift-off:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs L316-L324
// Lift-off. On the pad the air-relative velocity is zero, and the script starts its vehicle rising at 10 m/s, clear of the singularity that makes the angle of attack meaningless at rest.
double3 rNow = orbit.StateVectors.PositionCci;
double3 r = rNow;
double3 v = orbit.StateVectors.VelocityCci;
double3 spin = new double3(0, 0, omega);
double3 up = double3.Normalize(r);
bool atRest = (v - double3.Cross(spin, r)).Length() < 10.0;
if (atRest)
    v = double3.Cross(spin, r) + 10.0 * up;
```

8. **Fly the plan against air-relative speed, not its clock.** A slower or faster climb stays on the profile when the independent variable is monotonic and physical:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs L515-L517
/// <summary>Air-relative speed: what the profile is indexed by.</summary>
private static double AirSpeed(double3 r, double3 v, IParentBody parent)
    => (v - double3.Cross(new double3(0, 0, parent.GetAngularVelocity()), r)).Length();
```

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs L533-L544
private static double3 ConvexProfileDirection(double3 r, double3 v, double mass, IParentBody parent)
{
    ConvexAscentProfile profile = _s.FlyingPlan?.Profile;
    if (profile == null)
        return double3.Normalize(r);
    double now = SimNow();
    double dt = double.IsFinite(_s.ConvexLastTime) ? Math.Max(0.0, now - _s.ConvexLastTime) : 0.0;
    _s.ConvexLastTime = now;
    _s.ConvexPlanTime = profile.Advance(_s.ConvexPlanTime, AirSpeed(r, v, parent), mass, dt,
        ref _s.ConvexStage, ref _s.ConvexStageSpeed, ref _s.ConvexFastest);
    return profile.Direction(r, _s.ConvexPlanTime);
}
```

9. **Hand over to UPFG.** Fly the profile until the vehicle is above the hand-over altitude (or the plan's speed runs out) and UPFG has converged, then blend the attitude to UPFG's over a short time (`ConvexBlendWeight`, see approach 1). A plan is refused if the vehicle has moved too far, its mass changed too much since the solve, or it is flown too long after the lift-off instant it was solved for.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs) | request, collect, fly, hand-over |
| [AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/Ascent/KsaAscentPlan.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/Ascent/KsaAscentPlan.cs) | problem build, solve thread, retries |
| [AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/Ascent/KsaAscentStages.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/Ascent/KsaAscentStages.cs) | stage and thrust tables from the game |
| [AdvancedFlightComputer/Features/Guidance/Scvx/Ascent/AscentScvx.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Scvx/Ascent/AscentScvx.cs) | SCvx loop |
| [AdvancedFlightComputer/Features/Guidance/Scvx/Ascent/AscentSubproblem.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Scvx/Ascent/AscentSubproblem.cs) | cone program per iteration |

## Variations and gotchas

- **Never read the part tree from the solve thread.** Build plain data first; the job's delegates must touch nothing of the game.
- **Do not fly an offline plan by its clock.** Fly it against air-relative speed.
- **The solve takes seconds.** The panel shows live progress (`Progress`, `Retry`, `Notes`) read under the job's lock.
- **Cancel and dispose** both cancel the token; a cancelled job finishes with no plan.
- **Compared with UPFG:** SCvx is open loop and needs a drag table and an atmosphere; UPFG is closed loop and cheap. AFC therefore flies SCvx first and UPFG for the insertion.

## See also

- [Overview](overview.md); [UPFG with a stage list](fly-closed-loop-ascent-guidance-1.md)
- [Minimum-propellant convex ascent (mod feature)](../../../../mods/ksa-advancedflightcomputer/features/convex-ascent-scvx.md)
- [Run an expensive solve without stalling the frame](../run-an-expensive-solve-without-stalling-the-frame/overview.md)
- [Build a site frame and compute a launch window](../../transfers/build-a-site-frame-and-compute-a-launch-window/overview.md)

[^ksa-parttree]: KSA.PartTree (decompiled, build 2026.10.10.5554)
[^ksa-sequenceperformancelist]: KSA.SequencePerformanceList (decompiled, build 2026.10.10.5554)
[^ksa-solidmotor]: KSA.SolidMotor (decompiled, build 2026.10.10.5554)
