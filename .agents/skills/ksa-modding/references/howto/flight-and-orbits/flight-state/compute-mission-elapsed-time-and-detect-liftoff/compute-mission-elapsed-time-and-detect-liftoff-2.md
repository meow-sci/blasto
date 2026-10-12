---
type: How-To
title: Advance simulated objects on Universe.GetLastSimStep, deduplicated on NextTime
description: Integrate mod-simulated objects across exactly the interval the engine just applied, taking each step once and closing the gaps from frames the mod missed, so pause and time warp are right by construction
category: flight-and-orbits
group: flight-state
topic: compute-mission-elapsed-time-and-detect-liftoff
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/consume-applied-sim-step]
techniques: [sim-clock, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Universe, KSA.UniverseTime, KSA.SimStep]
tags: [sim-step, time-warp, pause, step-gate, universe-time, integration]
---

Outcome: a mod-side simulation (rounds, decoys, guidance) that advances exactly as far as the world did: nothing when paused,
a hundred times as far at 100x, and a missed frame is added back. KSArmory's `ConsumeSimStep` does this.[Integrate on the step KSA applied, consumed once](../../../../mods/ksarmory/features/consume-applied-sim-step.md)
Prerequisite: StarMap.

## Background: how the game does it

StarMap hands every hook a player-time delta: wall clock, clamped, running through a pause and blind to warp. The engine
instead publishes the step it just applied: `Universe.GetLastSimStep()` returns `_lastSimStep` (a `SimStep` with
`PreviousTime`, `NextTime`, `DeltaTime`), assigned from `_nextSimStep` inside `ApplyVehicleSolvers`.[^ksa-universe] [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1947-L1948)
[current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2381-L2402) A step is created by `GetJobSimStep(dtPlayer)`, starting at the previous `NextTime`, so
`PreviousTime(k) == NextTime(k-1)`: contiguous, and `DeltaTime = dtPlayer * achievedSpeedFraction * SimulationSpeed` was sized in the
previous frame.[current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2615-L2627) A speed change therefore takes two frames to reach the applied step.
The only discontinuity is a save load, which writes a zero-length step at the save's time into both steps.[current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2455-L2462)

## Lifecycle and timing

The mod calls `ConsumeSimStep` once per frame from its step function, which runs in the `[StarMapAfterGui]` postfix, or with the
HUD hidden in a prefix on `OnFrameCelestials`, or as a last resort in the `OnFrame` postfix. At each point `GetLastSimStep()` is
the step whose results were applied this frame, `GetElapsedTime()` is its `NextTime` and `GetNextSimStep()` is the step now
running on the workers. Reading any vehicle or celestial state there is safe; writing vehicle kinematics or flight-computer fields
is not (the worker owns a snapshot). See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Read the applied step and remember its `NextTime`.** Difference the last two `NextTime`s in `Int128` nanoseconds, because an
   absolute universe time is too large to subtract as a `double`:

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

2. **Gate on `NextTime`.** Return 0 when the same `NextTime` is asked for twice (or the world is paused); return the span after a missed
   frame (a screenshot with `DrawUI` false skips the hook entirely); return `DeltaTime` after a backwards jump:

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

3. **Use the step as the integration gate, not `IsPaused`.** A finite positive step is the only condition for advancing:

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

4. **Clamp per consumer.** Rounds with fuses step by `min(dtSim, faithfulStep)`; time above it is discarded and logged. Rate-limited
   drives take the unclamped step.
5. **Smooth cosmetic eases** with an exponential average of the step: KSA's step alternates with the display's frame pacing (for
   example 8.33 and 25 ms on a 120 Hz panel).
6. **Reset on save load.** Call your reset (`ResetSimStepTracking`) when the world is replaced so the first step of the new world is
   its own `DeltaTime` (zero) rather than a span across years.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L58-L145) | `SimStepSeconds`, `SimClockSeconds`, `ConsumeSimStep`, `Span`, `ResetSimStepTracking` |
| [src/KSArmory/Sim/StepGate.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/StepGate.cs) | once-only consumption keyed on `NextTime` |
| [src/KSArmory/Sim/SimClock.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/SimClock.cs) | `Classify` (Idle, Run, Skipped) |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L707-L810) | consume, overrun report, warp policy, clamp, ordered updates |
| [src/KSArmory/Sim/SmoothedStep.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/SmoothedStep.cs) | smoothed step for cosmetics |

## Variations and gotchas

- **Two-frame delay.** A speed change set this frame reaches the applied step two frames later; see [Set, limit and request simulation speed from a mod](../set-simulation-speed-from-a-mod/overview.md).
- **KSA's `GetAchivedSpeedFraction()` saturates at 1.0** once a frame exceeds a thirtieth of a second; measure simulated seconds
  per wall second yourself if you need the true ratio.
- A step longer than your faithful limit means the world advanced faster than your model can follow; report the overrun.
- Do not use `dtPlayer` for anything simulated: it runs through a pause.

## See also

- [Overview](overview.md); [liftoff clock](compute-mission-elapsed-time-and-detect-liftoff-1.md); [timed playback](compute-mission-elapsed-time-and-detect-liftoff-3.md)
- [Integrate on the step KSA applied, consumed once](../../../../mods/ksarmory/features/consume-applied-sim-step.md)

[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
