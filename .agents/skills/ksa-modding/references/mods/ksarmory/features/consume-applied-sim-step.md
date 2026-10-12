---
type: Mod Feature
title: Integrate on the step KSA applied, consumed once
description: The mod advances its own simulation by Universe.GetLastSimStep, deduplicated on the step's NextTime and widened to the span since the last step it integrated, never by the player-time delta StarMap passes
mod: ksarmory
category: physics
techniques: [starmap-lifecycle, custom-physics, step-gate, sim-clock]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Universe, KSA.SimStep, KSA.UniverseTime, KSA.JobSystems, KSA.App, KSA.GameSettings]
tags: [simulation-time, sim-step, timewarp, pause, determinism, universe-time, profiling]
---

StarMap hands every frame hook a player-time delta: wall clock, clamped, running through a pause
and blind to timewarp. KSArmory ignores it for anything simulated. Each frame it asks KSA for the
step the engine just applied to the world (`Universe.GetLastSimStep()`), takes it exactly once, and
integrates rounds, decoys, directors and guidance across that interval and nothing else. Paused,
the step is zero and nothing moves; at 100x it is a hundred times longer; a frame the mod missed is
added back from the step boundaries.[^ksarmory-ksaworld]

## How it works

`KsaWorld.ConsumeSimStep()` reads `Universe.GetLastSimStep()` and feeds its `NextTime` and
`DeltaTime` to a `StepGate<UniverseTime>`:[^ksarmory-stepgate]

| Case | `StepGate.Consume` returns |
|---|---|
| `NextTime` equals the last one integrated through | `0` (asking twice in a frame, or a paused world whose clock did not move) |
| First call after `Reset()` | `DeltaTime` |
| New `NextTime`, span since the last one `> DeltaTime - 1 ns` | the span (closes frames the mod never ran, and takes KSA's nanosecond-rounded clock) |
| New `NextTime`, span shorter (clock jumped backwards, e.g. a save load) | `DeltaTime` |

The span is `(to.Nanoseconds - from.Nanoseconds) / 1e9` on `Int128` nanoseconds, because an
absolute universe time is too large to subtract as a `double`. `LastLongerOfStepAndSpan` records
the alternative for a measurement trace only.

`StepSimulation` then uses the result in a fixed order:[^ksarmory-mod]

1. `_lastSimStep = dtSim` (or 0), kept for cameras, clouds, flash, sound and leaks later in the pass.
2. `SimClock.Classify(dtSim, IsPaused)` only to detect `Skipped` (step longer than
   `Interceptor.MaxFaithfulStep = 0.005 s x 64 = 0.32 s`), which goes to `ReportOverrun`.[^ksarmory-simclock][^ksarmory-interceptor]
3. `ApplyWarpPolicy(dtSim)` (see [timewarp policy](./timewarp-policy-during-engagements.md)).
4. **The integration gate is the step itself**: `if (double.IsFinite(dtSim) && dtSim > 0.0)`,
   not `IsPaused`. Rounds and decoys are stepped by `min(dtSim, FaithfulStepInFlight())`, the
   shortest `MaxFaithfulStepSeconds` of any round in the air; time above it is discarded and logged.[^ksarmory-weaponsystems]
5. Optical directors and ballistic computers take `dtSim` unclamped: a rate-limited drive or a timed
   cutoff has no fuse to step over, and clamping only long frames would inject a judder that tracks
   the display's frame pacing.
6. Drawing-only work (`SyncRoundBodies`) runs outside the gate on every frame.

Two helpers round it out:

- `SmoothedStep.Next(step)`: an exponential average (weight 0.15) of the step for cosmetic eases
  only (director drives, chase-camera blend, turret spin), because KSA's step alternates with the
  display's frame pacing (8.33/25 ms on a 120 Hz panel). Zero in, zero out.[^ksarmory-smoothedstep]
- `SolverLoad`: at `Debug` log level, sums simulated seconds against a wall `Stopwatch` and reports
  "N.NNx real time" every 10 s beside KSA's own `GetAchivedSpeedFraction()` and the vehicle solver's
  worst tick, because KSA's fraction saturates at 1.000 once a frame exceeds a thirtieth.[^ksarmory-solverload]

## Lifecycle and timing

`ConsumeSimStep` is called once per frame from `StepSimulation`, inside `StepOnce`, which runs in the
`[StarMapAfterGui]` postfix (Program.cs L2268), or with the HUD hidden in the prefix on
`OnFrameCelestials` (L2320), or as a last resort in the `OnFrame` postfix (see
[dual-hook simulation step](./dual-hook-simulation-step.md)). At every one of those points:[^ksa-program][^ksa-universe]

| Value | Meaning there | Where KSA set it |
|---|---|---|
| `Universe.GetLastSimStep()` | The step whose results were applied this frame | `_lastSimStep = _nextSimStep` in `ApplyVehicleSolvers` (Universe.cs L1948), called at Program.cs L2167 |
| `Universe.GetElapsedTime()` | `_lastSimStep.NextTime` (Universe.cs L2399-L2402): the epoch of every vehicle and celestial sample read in the hook | same |
| `Universe.GetNextSimStep()` | The step now running on the workers | `_nextSimStep = simStep` in `ExecuteNextVehicleSolvers` (Universe.cs L2135), Program.cs L2220 |
| `DeltaTime` of the applied step | `dtPlayer x _achievedSpeedFraction x SimulationSpeed`, sized in the **previous** frame's `PrepareFrame` | `GetJobSimStep` (Universe.cs L2615-L2627), Program.cs L2216 |

Contiguity: `GetJobSimStep` starts every step at `_lastSimStep.NextTime`, so
`PreviousTime(k) == NextTime(k-1)`, and `NextTime` is `PreviousTime + DeltaTime` rounded to the
nanosecond (`UniverseTime` operator `+`, UniverseTime.cs L162-L165, rounding at L71-L88). That is why
the span between two `NextTime`s is exact across any number of missed frames.[^ksa-universetime]
`dtPlayer` is the wall delta clamped to `1 / MinTargetFrameRate` (App.cs L52-L53; default 30 fps,
GameSettings.cs L1045), so at 1x a step is at most 33.3 ms.[^ksa-app][^ksa-gamesettings]

The only discontinuity is a save load: `Universe.DeserializeSave` writes a zero-length step at the
save's time into both `_lastSimStep` and `_nextSimStep` (Universe.cs L2455-L2462). The mod calls
`KsaWorld.ResetSimStepTracking()` from `ForgetTheWorld` before anything consumes (see
[save-load detection](./detect-save-load-ongameloaded.md)) and from `Unload`, so the first step of the
new world is taken as its own `DeltaTime` (zero) rather than as a span across years.

Safe here: reading any vehicle or celestial state (it is all at `GetElapsedTime()`), integrating
the mod's own objects. Not safe: writing vehicle kinematics or flight-computer fields (the worker
owns a snapshot until next frame; see [the PrepareWorker window](./prepareworker-prefix-command-window.md)).

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L58-L145) | `SimStepSeconds`, `SimClockSeconds`, `ConsumeSimStep`, `Span`, `ResetSimStepTracking` |
| [src/KSArmory/Sim/StepGate.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/StepGate.cs) | Once-only consumption keyed on `NextTime`, span widening, `Reset` |
| [src/KSArmory/Sim/SimClock.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/SimClock.cs) | `Classify` (Idle/Run/Skipped) and `Viewing` (player time while unpaused) |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L707-L810) | Consume, overrun report, warp policy, clamp, ordered updates |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L824-L863) | Debug-only `SolverLoad` and `FrameBudget` sampling |
| [src/KSArmory/Sim/SmoothedStep.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/SmoothedStep.cs) | Smoothed step for cosmetic eases |
| [src/KSArmory/Sim/SolverLoad.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/SolverLoad.cs) | Simulated seconds per wall second |

## Snippets

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L98-L118
    public static double ConsumeSimStep()
    {
        SimStep step = Universe.GetLastSimStep();

        if (_consumedThrough is { } from && !step.NextTime.Equals(from))
        {
            LastLongerOfStepAndSpan = Math.Max(step.DeltaTime, Span(from, step.NextTime));
        }

        _consumedThrough = step.NextTime;

        // The span, not just the step: a frame in which this mod's hook never ran still advanced
        // the world, and GetLastSimStep reports only the most recent one. Taking a screenshot is
        // exactly that case -- ScreenshotCapture sets Program.DrawUI false and KSA guards the call
        // this mod postfixes with it, so the hook is not called at all. Differenced in Int128
        // nanoseconds because absolute universe time is far too large to subtract as double.
        return _stepGate.Consume(step.NextTime, step.DeltaTime, Span);
    }

    private static double Span(UniverseTime from, UniverseTime to)
        => (double)(to.Nanoseconds - from.Nanoseconds) / 1e9;
```

```csharp
// src/KSArmory/Sim/StepGate.cs L60-L78
    public double Consume(T nextTime, double delta, Func<T, T, double>? spanSeconds)
    {
        if (_hasIntegrated && nextTime.Equals(_integratedThrough)) return 0.0;

        double taken = delta;

        // A span shorter than the step by more than rounding means the clock disagrees with the
        // step it just handed over -- a save loaded mid-session jumps it backwards -- and the step
        // is then the thing that moved the world.
        if (_hasIntegrated && spanSeconds is not null)
        {
            double span = spanSeconds(_integratedThrough, nextTime);
            if (double.IsFinite(span) && span > taken - RoundingSeconds) taken = span;
        }

        _integratedThrough = nextTime;
        _hasIntegrated = true;
        return taken;
    }
```

```csharp
// src/KSArmory/Ksa/KSArmoryMod.cs L722-L754
            double dtSim;
            using (_budget.Measure("consume")) dtSim = KsaWorld.ConsumeSimStep();

            // Kept for the cameras, which run later in this same hook. The step is consumed
            // exactly once per frame, so they cannot ask for it themselves.
            _lastSimStep = double.IsFinite(dtSim) && dtSim > 0.0 ? dtSim : 0.0;

            // ...
            bool skipped;
            using (_budget.Measure("clock"))
            {
                skipped = SimClock.Classify(dtSim, KsaWorld.IsPaused, out _) == SimClock.State.Skipped;
            }

            if (skipped)
            {
                ReportOverrun(dtSim);
            }

            using (_budget.Measure("warp")) ApplyWarpPolicy(dtSim);

            // ...
            if (double.IsFinite(dtSim) && dtSim > 0.0)
            {
                double faithful;
                using (_budget.Measure("faithful")) faithful = FaithfulStepInFlight();

                double step = Math.Min(dtSim, faithful);
```

```csharp
// current/decomp/KSA/Universe.cs L2615-L2627
	public static SimStep GetJobSimStep(double dtPlayer)
	{
		double num = _achievedSpeedFraction * GetSimulationSpeed();
		UniverseTime nextTime = _lastSimStep.NextTime;
		double num2 = dtPlayer * num;
		UniverseTime nextTime2 = nextTime + num2;
		return new SimStep
		{
			PreviousTime = nextTime,
			NextTime = nextTime2,
			DeltaTime = num2
		};
	}
```

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `Universe.GetLastSimStep`, `GetNextSimStep`, `GetElapsedTime` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2380-L2402) | The applied step, the in-flight step, the sample epoch |
| `Universe.ApplyVehicleSolvers` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1952) | Promotes `_nextSimStep`; updates `_achievedSpeedFraction` |
| `Universe.GetJobSimStep` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2615-L2627) | How each step is sized and chained |
| `Universe.DeserializeSave` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2430-L2478) | The one clock rewrite |
| `Universe.IsPaused`, `GetAchivedSpeedFraction` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1799-L1802) | `speed == 0.0`; solver-keeping-up diagnostic (L2316-L2319) |
| `SimStep` (`PreviousTime`, `NextTime`, `DeltaTime`) | [current/decomp/KSA/SimStep.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SimStep.cs) | The interval type[^ksa-simstep] |
| `UniverseTime.Nanoseconds`, `operator +` | [current/decomp/KSA/UniverseTime.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UniverseTime.cs#L28) | `Int128` clock, nanosecond rounding |
| `JobScheduler.GetMaxLastTickTime` (`JobSystems.VehicleSolver`) | [current/decomp/Brutal.Concurrency.Jobs/JobScheduler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Concurrency.Jobs/JobScheduler.cs#L114) | Worst recent vehicle-solver tick, for `SolverLoad`[^ksa-jobscheduler] |

## Pitfalls and version notes

- **Gate on the step, never on `IsPaused`.** `IsPaused()` is `_simulationSpeed == 0.0` (Universe.cs
  L1799-L1802), a statement about the setting. On the frame the speed goes to zero the step already
  in the workers is still applied next frame; a flag-gated mod skips it while the platform moves.
- **Never re-read the step.** `GetLastSimStep()` reports the last step, not one since you asked;
  integrating it twice compounds in every integrated position.
- **A speed change shows up in the applied step two frames later**: written in frame k's hook, used
  by `GetJobSimStep` in k+1 (L2216), applied in k+2 (L1948).
- **`dtPlayer x SimulationSpeed` is not the step.** It omits `_achievedSpeedFraction`, which drops
  instantly when the vehicle solver overruns and recovers by a 0.9/0.1 filter (Universe.cs L1939-L1946),
  and it misses the nanosecond rounding.
- **`GetAchivedSpeedFraction` saturates.** Its numerator is `0.9 x min(dtPlayer, 1/MinTargetFrameRate)`
  over the solver tick (L1926-L1938), so it reads ~1.0 while the world falls behind the wall clock.
- **Docs out of date:** `docs/KSA-FRAME-ORDER.md` §6 and §10 say nothing resets `StepGate` on a save
  load. The code now does (`ForgetTheWorld`, KSArmoryMod.cs L399).[^ksarmory-frame-order] Its §2
  (two fields of one struct, contiguity, two-frame speed delay, `_lastSimStep` written only in
  `ApplyVehicleSolvers` and `DeserializeSave`) still holds on 5554 (Universe.cs L1948, L2456).

## See also

- [Dual-hook simulation step](./dual-hook-simulation-step.md)
- [Ecliptic frames and epochs](./ecliptic-frames-and-epochs.md)
- [Timewarp policy during engagements](./timewarp-policy-during-engagements.md)
- [Detect a save load via OnGameLoaded](./detect-save-load-ongameloaded.md)
- [Mod-simulated rounds](./mod-simulated-rounds.md)
- [Analytic sub-frame fuses](./analytic-subframe-fuses.md)
- [Sim boundary and headless tests](./sim-boundary-headless-tests.md)
- [Advance simulated objects on Universe.GetLastSimStep, deduplicated on NextTime](../../../howto/flight-and-orbits/flight-state/compute-mission-elapsed-time-and-detect-liftoff/compute-mission-elapsed-time-and-detect-liftoff-2.md) - how-to article demonstrating this feature
- [Mutate simulation state safely relative to the physics step](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md) - how-to article demonstrating this feature
- [Deduplicate GetLastSimStep on NextTime and widen to the span since the last boundary](../../../howto/physics/mod-simulation/simulate-bodies-outside-the-vehicle-system/simulate-bodies-outside-the-vehicle-system-3.md) - how-to article demonstrating this feature
- [Share one driver seam between the game and the headless rig and test with varying steps](../../../howto/testing-and-tooling/offline-tests/run-mod-simulation-in-a-headless-rig/run-mod-simulation-in-a-headless-rig-2.md) - how-to article demonstrating this feature

[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-stepgate]: src/KSArmory/Sim/StepGate.cs
[^ksarmory-simclock]: src/KSArmory/Sim/SimClock.cs
[^ksarmory-smoothedstep]: src/KSArmory/Sim/SmoothedStep.cs
[^ksarmory-solverload]: src/KSArmory/Sim/SolverLoad.cs
[^ksarmory-interceptor]: src/KSArmory/Sim/Interceptor.cs
[^ksarmory-weaponsystems]: src/KSArmory/Ksa/WeaponSystems.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-frame-order]: docs/KSA-FRAME-ORDER.md
[^ksa-universe]: KSA.Universe (decompiled)
[^ksa-program]: KSA.Program (decompiled)
[^ksa-app]: KSA.App (decompiled)
[^ksa-simstep]: KSA.SimStep (decompiled)
[^ksa-universetime]: KSA.UniverseTime (decompiled)
[^ksa-gamesettings]: KSA.GameSettings (decompiled)
[^ksa-jobscheduler]: Brutal.Concurrency.Jobs.JobScheduler (decompiled)
