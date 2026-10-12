---
type: How-To
title: Rate-limit a solve on the wall clock and cache results per object with real and simulated time limits
description: Keep an expensive per-frame computation affordable with a wall-clock interval that time warp cannot defeat, a long back-off for unanswerable questions, and a per-object cache invalidated by identity, simulated time and elapsed real time
category: flight-and-orbits
group: guidance
topic: run-an-expensive-solve-without-stalling-the-frame
approach: 5
difficulty: beginner
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksarmory/features/guided-bomb-tail-kit-and-sight
  - mods/compendium/features/root-closest-approach-search
techniques: [rate-limited-solve, conditional-weak-table, throttled-cache]
hooks: [starmap StarMapAfterGui, harmony-postfix KSA.Program.UpdateGauges]
lifecycle: [frame:draw-ui, frame:gauges]
game_types: [KSA.Vehicle, KSA.Program, KSA.FlightPlan, KSA.Universe]
tags: [rate-limit, wall-clock, stopwatch, cache, conditional-weak-table, time-warp, back-off, throttle]
---
Outcome: a computation that is too expensive for every frame (a 2048-step flown trajectory, a thousand-sample minimum search) runs only as often as it is worth, and the cheap path in between is a cache read. Two mods show the two halves: KSArmory re-solves its bombsight on a wall-clock interval with a long back-off for unanswerable cases, and Compendium caches a closest-approach search per vehicle and invalidates it by identity, simulated time and real time. Both keep the work on the main thread, so they suit a computation of a few milliseconds that you cannot afford sixty times a second.[Guided bomb sight](../../../../mods/ksarmory/features/guided-bomb-tail-kit-and-sight.md) [Root closest-approach search](../../../../mods/compendium/features/root-closest-approach-search.md)
Prerequisites: StarMap and Harmony (Compendium patches `Program.UpdateGauges`; KSArmory draws from `StarMapAfterGui`).

## Background: how the game does it

Time warp is the trap. Simulated time can advance by hours between two frames, so a limiter written in simulation seconds stops limiting exactly when a frame is the most expensive. `Universe.GetElapsedTime()` is the simulated clock;[^ksa-universe] a `Stopwatch` is the real one. Use the real clock for a computation budget and the simulated clock only for what the answer depends on (is the cached approach still in the future, did time jump).

`Program.UpdateGauges(Vehicle vehicle, double dt)` is private and runs once per frame after `ImGui.Render` for the controlled vehicle only, so a postfix there is a natural single call site for a per-vehicle cache.[^ksa-program] [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2286-L2300)

## Lifecycle and timing

| Where | Lifecycle | What |
|---|---|---|
| KSArmory sight | `frame:draw-ui` (`StarMapAfterGui`), after the round step | Rate-limited solve, then draw from the cached offsets |
| Compendium readouts | `frame:gauges` (`Program.UpdateGauges` postfix) | Cache lookup per frame, recompute at most once a real second |

Both run on the main thread, so the cap on the work is the rate. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Rate-limit on a real-time stopwatch.** The comment states the reason; the check is the first statement of the update:

```csharp
// src/KSArmory/Ksa/BombSightOverlay.cs L75-L96
// Wall clock, not simulated: a solve is a computation budget rather than physics, so under
// timewarp a simulated interval passes every frame and a rate limit built on one stops
// limiting. Same trap IcbmState.PlayerStepSeconds documents.
private const double SolveIntervalSeconds = 0.2;

// How long to wait before asking again once the store has been found unsightable. Long, but
// not never: an aircraft that climbs out of range comes back down.
private const double UnreachableIntervalSeconds = 2.0;

private readonly System.Diagnostics.Stopwatch _sinceSolve = System.Diagnostics.Stopwatch.StartNew();
private bool _unreachable;

public void Update(WeaponSystem system, double dtSim)
{
    if (system.Platform is not { } platform || system.Launcher is null) return;

    // Held between solves, so what was drawn last time keeps being drawn.
    double wanted = _unreachable ? UnreachableIntervalSeconds : SolveIntervalSeconds;

    if (_sinceSolve.Elapsed.TotalSeconds < wanted) return;

    _sinceSolve.Restart();
```

2. **Back off hard for a question that cannot be answered.** A store released minutes above its target never reaches the ground inside the sight's horizon, so after a failed solve the interval becomes two seconds, and the previous good answer is kept for display on a failure:

```csharp
// src/KSArmory/Ksa/BombSightOverlay.cs L111-L129
// Into a scratch list, so a failed solve leaves the last good one on screen. A sight that
// blanks for a frame reads as broken, and the answer it had a quarter of a second ago is
// still very nearly right.
bool ok = Fly(system, platform, releaseEcl, releaseVelocity, _next, out double3 impact);

// A store that cannot reach the ground inside BombSight.MaxSteps has no pipper, and it will
// still have none a frame later -- so back off rather than re-flying 2048 steps at the
// solve rate for a shot that can never answer.
//
// The ballistic case is why this is needed. The gate above asks whether the round is
// *powered*, and a Mk 21 reentry vehicle is not, so it passes -- but the sight's horizon is
// MaxSteps x IntegrationStep, about 102 s, and a warhead released minutes above its target
// falls for far longer. Such a store can never have a pipper.
if (!ok)
{
    _unreachable = true;
    Clear();
    return;
}
```

3. **Store the answer relative to the object it was solved against** (offsets from the platform sample, not world points), so a quarter-second-old answer translates with the craft instead of drifting; see [Predict impact points with drag and build a ballistic computer](../predict-impact-point-and-build-a-ballistic-computer/predict-impact-point-and-build-a-ballistic-computer-2.md).
4. **Key a cache on the object that owns it.** A `ConditionalWeakTable<Vehicle, Cache>` lets the entry die with the vehicle and needs no cleanup hook:

```csharp
// src/RootClosestApproach.cs L14-L25
private sealed class Cache
{
    public Astronomical? Root;
    public FlightPlan? Plan;
    public long Updated;
    public UniverseTime Now;
    public RootApproach? Approach;
}

private static readonly ConditionalWeakTable<Vehicle, Cache> cache = new();

public static void Clear() => cache.Clear();
```

5. **Invalidate by identity, simulated time and real time.** The cached result is reused while the root and the `FlightPlan` object are the same (the game installs a recomputed trajectory as a new object), simulated time has not gone backwards or advanced 60 s, the cached approach time has not passed, and less than one real second has elapsed:

```csharp
// src/RootClosestApproach.cs L27-L45
public static RootApproach? Get(Vehicle vehicle, Astronomical root, UniverseTime now)
{
    var entry = cache.GetOrCreateValue(vehicle);
    if (entry.Root == root && entry.Plan == vehicle.FlightPlan && now >= entry.Now &&
        (now - entry.Now).Seconds() < 60 &&
        (!entry.Approach.HasValue || entry.Approach.Value.Time >= now) &&
        Stopwatch.GetElapsedTime(entry.Updated).TotalSeconds < 1)
    {
        return entry.Approach;
    }

    entry.Root = root;
    entry.Plan = vehicle.FlightPlan;
    entry.Now = now;
    entry.Updated = Stopwatch.GetTimestamp();
    entry.Approach = null;
    entry.Approach = Calculate(vehicle.FlightPlan, root, now);
    return entry.Approach;
}
```

6. **Call it from the one place that needs the value.** The `UpdateGauges` postfix reads the cache and writes the gauge rollers; the expensive part runs at most once per second per vehicle:

```csharp
// src/RootTargetFlightComputerPatches.cs L97-L120
[HarmonyPatch(typeof(Program), "UpdateGauges")]
[HarmonyPostfix]
private static void RootTargetReadouts(Vehicle vehicle)
{
    var root = RootNavigationTargets.GetTarget(vehicle);
    if (root == null)
    {
        return;
    }
    const UnitMask units = UnitMask.Millimeter | UnitMask.Centimeter | UnitMask.Meter |
        UnitMask.Kilometer | UnitMask.Gigameter | UnitMask.AU | UnitMask.LightYear;
    Program.TgtRelDistanceRoller.IsDisabled = false;
    Program.TgtRelDistanceRoller.PackTarget((root.GetPositionEcl() - vehicle.GetPositionEcl()).Length(), 1, units);
    Program.TgtRelVelocityRoller.IsDisabled = false;
    Program.TgtRelVelocityRoller.PackTarget((root.GetVelocityEcl() - vehicle.GetVelocityEcl()).Length(), 1, units);
    var approach = RootClosestApproach.Get(vehicle, root, Universe.GetElapsedTime());
    Program.TgtClosestDistanceRoller.IsDisabled = !approach.HasValue;
    Program.TgtClosestTimeRoller.IsDisabled = !approach.HasValue;
    if (approach.HasValue)
    {
        Program.TgtClosestDistanceRoller.PackTarget(approach.Value.Distance, 1, units);
        Program.TgtClosestTimeRoller.SetTimeTarget(Math.Max(0, (approach.Value.Time - Universe.GetElapsedTime()).Seconds()));
    }
}
```

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/BombSightOverlay.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BombSightOverlay.cs#L65-L153) | rate limit, unreachable back-off, scratch list for failed solves |
| [src/RootClosestApproach.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootClosestApproach.cs#L14-L45) | per-vehicle cache and refresh rule |
| [src/RootTargetFlightComputerPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetFlightComputerPatches.cs#L97-L120) | the postfix that consumes it |

## Variations and gotchas

- **Rate in wall clock, invalidate in both clocks.** A limiter in simulation seconds stops limiting under warp; a cache that ignores simulated time serves stale numbers after a time jump.
- **Solve into a scratch list** so a failed solve leaves the last good answer on screen; a readout that blanks for a frame reads as broken.
- **Rate by weapons system / vehicle**, not globally: there is one sight per weapons system, so an unrated solve is 2048 steps times however many craft are armed.
- **The one-second throttle bounds the cost, not the first call**: a recompute evaluates about a thousand samples per patch plus refinements on the main thread; see [Find the closest approach to a target along a trajectory](../../map-view/find-closest-approach-along-the-flight-plan/overview.md).
- **Not a substitute for a worker.** If one run of the computation exceeds a few milliseconds, use [approach 1](run-an-expensive-solve-without-stalling-the-frame-1.md) or [approach 4](run-an-expensive-solve-without-stalling-the-frame-4.md).

## See also

- [Overview](overview.md); [Iterator time-slicing](run-an-expensive-solve-without-stalling-the-frame-4.md)
- [Guided bomb sight (mod feature)](../../../../mods/ksarmory/features/guided-bomb-tail-kit-and-sight.md); [Root closest-approach search (mod feature)](../../../../mods/compendium/features/root-closest-approach-search.md)
- [Measure what a mod costs per frame, throttle expensive work and warm up hot code](../../../testing-and-tooling/in-game-testing/measure-and-budget-mod-performance/overview.md)

[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
