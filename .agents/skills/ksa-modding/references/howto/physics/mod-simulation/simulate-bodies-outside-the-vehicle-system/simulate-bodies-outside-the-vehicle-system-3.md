---
type: How-To
title: Deduplicate GetLastSimStep on NextTime and widen to the span since the last boundary
description: Take the simulation step the engine just applied exactly once per frame, return zero when asked twice or when paused, and add back frames the mod never ran
category: physics
group: mod-simulation
topic: simulate-bodies-outside-the-vehicle-system
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/consume-applied-sim-step]
techniques: [sim-clock, custom-physics]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Universe, KSA.SimStep, KSA.UniverseTime]
tags: [physics, sim-clock, simulated-time, sim-step, pause, timewarp, stepgate]
---

Outcome: your mod advances its own objects by exactly the simulated seconds the world advanced, no more and no
less: zero while paused, a hundred times longer at 100x warp, and still correct when a frame went by without your
hook running. Prerequisites: StarMap (any per-frame hook). KSArmory shows the pattern; KSAGolf uses the same
`ConsumeSimStep` shape for a single object ([approach 2](simulate-bodies-outside-the-vehicle-system-2.md)).

## Background: how the game does it

StarMap hands every frame hook a player-time delta: wall clock, clamped, running through a pause and blind to
timewarp. It is the wrong clock for anything that lives in the simulated world. The engine keeps two
`SimStep` values (`PreviousTime`, `NextTime`, `DeltaTime`):[^ksa-simstep]

| Accessor | Meaning |
|---|---|
| `Universe.GetLastSimStep()` | The step whose results `ApplyVehicleSolvers` applied at the start of this frame |
| `Universe.GetNextSimStep()` | The step now running on the worker threads |
| `Universe.GetElapsedTime()` | `_lastSimStep.NextTime`: the epoch of every vehicle and celestial sample you can read this frame |

`ApplyVehicleSolvers` promotes the in-flight step with `_lastSimStep = _nextSimStep`, and `GetJobSimStep` starts
every new step at the previous `NextTime`, so consecutive steps are contiguous. A step is sized in the previous frame as
`dtPlayer * achievedSpeedFraction * SimulationSpeed`.[^ksa-universe]

```csharp
// current/decomp/KSA/Universe.cs L1947-L1950
			_vehicleUpdateTask.ApplyResultsToMainThread();
			_lastSimStep = _nextSimStep;
			_lastPlayerDeltaTime = _nextPlayerDeltaTime;
			CurrentSystem.UpdatePerFrameData();
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

Consequences a mod must respect:

- `GetLastSimStep()` reports the last step, not "the step since you last asked". Reading it twice in one frame
  returns the same step, so integrating both reads moves your object twice as far as the world moved.
- A frame in which your hook did not run (HUD hidden, screenshot, `PrepareFrame` returned `Exit`) still advanced
  the world, but `GetLastSimStep()` only remembers the most recent step.
- A speed change written in frame k is used to size the step in frame k+1 and applied in frame k+2.
- `Universe.IsPaused()` is `_simulationSpeed == 0.0`, a statement about the setting, not about the step that is
  about to land. On the frame the speed drops to zero the step already on the workers is still applied.

## Lifecycle and timing

Call the consume function once per frame from a hook that runs after `PrepareFrame` has applied the solvers:
`StarMapAfterGui` (`frame:draw-ui`), a prefix on `Program.OnFrameCelestials` for hidden-HUD frames
(`frame:celestials`), or `StarMapAfterOnFrame` (`frame:after-on-frame`) as a last resort. All three see the same
`GetLastSimStep()`; see the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
for the order. Reading a step is always safe because nothing writes `_lastSimStep` outside `ApplyVehicleSolvers`
and `Universe.DeserializeSave` (a save load writes a zero-length step at the save's time into both fields, so
reset your tracking when a game loads).

## Steps

1. Gate on the step's own end time, not on a clock difference. `StepGate<T>` returns the step the first time
   a `NextTime` is seen and zero on any repeat; it needs only `IEquatable`, so it is testable without `UniverseTime`:

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

2. Feed it from the engine. The span between two `NextTime` values is differenced in `Int128` nanoseconds because
   an absolute universe time is far too large to subtract as a `double`:

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

3. Why the span wins. KSA advances its clock by the step rounded to whole nanoseconds and places every celestial
   there, while `DeltaTime` is the unrounded step. `StepGate` takes the span when it is within 1 ns of the step or
   longer, which also closes frames your hook missed; a span clearly shorter (the clock jumped backwards after a
   save load) is rejected in favour of `DeltaTime`.
4. Use the result as the only integration clock, gated on the step and never on `IsPaused`. KSArmory consumes once,
   keeps the value for later passes in the same hook (cameras cannot ask again), and only then decides how to
   integrate:

```csharp
// src/KSArmory/Ksa/KSArmoryMod.cs L722-L754
            double dtSim;
            using (_budget.Measure("consume")) dtSim = KsaWorld.ConsumeSimStep();

            // Kept for the cameras, which run later in this same hook. The step is consumed
            // exactly once per frame, so they cannot ask for it themselves.
            _lastSimStep = double.IsFinite(dtSim) && dtSim > 0.0 ? dtSim : 0.0;

            // No step reported, no step taken - never substitute an estimate. The engine reports
            // nothing exactly when it advanced nothing, so an estimate would integrate the round
            // across an interval the world did not move over, and the whole of that lands in the
            // drawn offset. Skipping costs one frame of round motion and nothing accumulates.
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

            // Clamped, and the clamp discards time: the frame that overran cannot be un-run,
            // and the policy above only takes effect from the next one. What it stops is the
            // *next* thousand frames doing the same thing silently.
            if (double.IsFinite(dtSim) && dtSim > 0.0)
            {
                double faithful;
                using (_budget.Measure("faithful")) faithful = FaithfulStepInFlight();

                double step = Math.Min(dtSim, faithful);
```

5. Clamp what you integrate, not what you consume. `SimClock.Classify` reports `Skipped` for a step above
   `Interceptor.MaxFaithfulStep` (0.005 s x 64 = 0.32 s) so the mod can log an overrun, and rounds step by
   `min(dtSim, FaithfulStepInFlight())` (see [approach 1](simulate-bodies-outside-the-vehicle-system-1.md)). Time above the clamp is discarded
   and logged rather than integrated badly.
6. Reset tracking when the world is replaced. `ResetSimStepTracking` clears the gate and the remembered boundary so
   the first step of the new world is taken as its own (zero) `DeltaTime` rather than as a span across years:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L141-L145
    public static void ResetSimStepTracking()
    {
        _stepGate.Reset();
        _consumedThrough = null;
    }
```

7. Optional: measure simulated seconds per wall second (KSArmory's `SolverLoad` at debug log level). KSA's own
   `GetAchivedSpeedFraction()` saturates at 1.0 because its numerator is `0.9 x min(dtPlayer, 1/MinTargetFrameRate)`
   over the solver tick, so it reads about 1.0 while the world falls behind the wall clock.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L58-L145) | `SimStepSeconds`, `ConsumeSimStep`, `Span`, `ResetSimStepTracking` |
| [src/KSArmory/Sim/StepGate.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/StepGate.cs) | The once-only gate keyed on `NextTime`, with span widening |
| [src/KSArmory/Sim/SimClock.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/SimClock.cs) | `Classify` into Idle, Run and Skipped |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L707-L810) | Consume, overrun report, warp policy, clamp, ordered updates |

## Variations and gotchas

- Never integrate `dtPlayer * SimulationSpeed`: it omits `_achievedSpeedFraction`, which drops the instant the
  vehicle solver overruns and recovers through a 0.9/0.1 filter, and it misses the nanosecond rounding.
- For cosmetic eases (camera blends, turret spin) a smoothed step is better than the raw one, because the step
  alternates with display frame pacing. KSArmory keeps an exponential average (weight 0.15) for those and never
  for physics.
- A mod that skips a frame entirely loses nothing: the next consume returns the whole span. A mod that consumes in
  two hooks of the same frame gets the step once and zero the second time, which is why KSArmory saves it in a
  field.
- The pause check inside `SimClock.Classify` is belt and braces; the step is already zero when the world stands still.

## See also

- [Overview and comparison](overview.md)
- [Approach 2](simulate-bodies-outside-the-vehicle-system-2.md) (single-object version), [approach 4](simulate-bodies-outside-the-vehicle-system-4.md) (which instant a sample belongs to)
- Mod feature: [KSArmory consume applied sim step](../../../../mods/ksarmory/features/consume-applied-sim-step.md)
- [Mutate vehicles at a physics-safe point](../../solver/mutate-vehicles-at-a-physics-safe-point/overview.md) for the timing of writes, as opposed to reads

[^ksa-simstep]: KSA.SimStep (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
