---
type: Mod Feature
title: Closest approach to a root target
description: A Program.UpdateGauges postfix fills the TGT readouts for a root target, estimating closest approach by sampling and golden-section refinement over 10 game years of coast flight-plan patches, refreshed at most once per real second
mod: compendium
category: flight-and-orbits
techniques: [harmony-postfix, numeric-minimization, conditional-weak-table, throttled-cache]
hooks: [harmony-postfix KSA.Program.UpdateGauges]
lifecycle: [frame:gauges]
game_types:
  - KSA.Program
  - KSA.DigitRoller
  - KSA.FlightPlan
  - KSA.PatchedConic
  - KSA.Orbit
  - KSA.Vehicle
  - KSA.Burn
  - KSA.Universe
  - KSA.IOrbiter
  - KSA.IParentBody
tags: [gauges, closest-approach, flight-plan, harmony, optimization, sampling, targets]
---

When the controlled vehicle has a [navigation-only root target](root-navigation-targets.md), the
native TGT gauge digits show the distance and relative speed to the root and an estimated closest
distance and time to it. The estimate covers the next 10 game years of the vehicle's coast trajectory
(planned burns excluded), samples and refines local minima, measures center to center, and can report
a minimum at the current time or at the end of the window, so it is not necessarily a flyby. With no
coast patches in the window the closest-approach digits stay disabled.[^cmp-readme-md]

## How it works

1. **What the game does.** The private `Program.UpdateGauges(Vehicle vehicle, double dt)` disables the
   TGT relative velocity and distance rollers when `vehicle.Target == null`, and asks
   `vehicle.FlightPlan.TryFindNextClosestApproach(vehicle.Target, now)` for the closest-approach rollers
   ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2802-L2822)).[^ksa-program] That lookup only reads the encounters each
   `PatchedConic` has already found for an `IOrbiter` (matched by hash), which a root never has
   ([current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L854-L868), [current/decomp/KSA/PatchedConic.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PatchedConic.cs#L178-L189)).[^ksa-flightplan] [^ksa-patchedconic]
2. **Postfix overrides the rollers.** `RootTargetReadouts(Vehicle vehicle)` runs after the stock body
   (Harmony binds `vehicle` by parameter name). With a root target it re-enables the relative distance
   and speed rollers and packs `|root - vehicle|` of ECL positions and velocities with the same unit mask
   the game uses, then fills the closest-approach rollers from `RootClosestApproach.Get` or disables
   them ([src/RootTargetFlightComputerPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetFlightComputerPatches.cs#L97-L120)).[^cmp-roottargetflightcomputerpatches]
   The rollers are public statics on `Program` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L382-L388)).
3. **Throttled per-vehicle cache.** A `ConditionalWeakTable<Vehicle, Cache>` keeps the last result. It is
   reused while the root and the `FlightPlan` object are the same, simulation time has not gone
   backwards or advanced 60 s, the cached approach time has not passed, and less than one real second
   (`Stopwatch`) has elapsed; otherwise it recomputes ([src/RootClosestApproach.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootClosestApproach.cs#L14-L45)).[^cmp-rootclosestapproach]
   Comparing the plan by reference works because the game installs a recomputed trajectory as a new
   object in `Vehicle.SetFlightPlanUnsynchronized` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L649-L668)).[^ksa-vehicle]
4. **Search window per patch.** For each patch of `vehicle.FlightPlan.Patches`, the interval is
   clipped to `[max(now, StartTime), min(now + 10 years, EndTime)]`. When the patch orbits the root
   directly and is bound with a finite period, one period is enough. The distance function evaluates
   the patch orbit at time `t` (`Orbit.GetStateVectorsAt(t)`), rotates it out of the parent's CCI frame,
   adds the parent's ECL position at `t`, and subtracts the root's position. For direct orbits the next
   periapsis inside the interval is also considered. The smallest distance wins, the earliest on ties
   ([src/RootClosestApproach.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootClosestApproach.cs#L47-L107), [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L2171),
   [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L2190)).[^ksa-orbit]
5. **Minimizer.** `FindMinimum` samples the interval at both ends plus 511 uniform and 511
   logarithmically spaced interior offsets (`Samples = 512` divisions, dense near now), merged in a
   `SortedSet`. Each sample that is a strict local minimum of its
   neighbors, plus the first and last intervals, is refined with a 48-iteration golden-section search;
   non-finite distances throw ([src/RootClosestApproach.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootClosestApproach.cs#L109-L206)).
6. **Coast only.** `Vehicle.FlightPlan` is the vehicle's coast trajectory; each planned burn carries its
   own `Burn.FlightPlan` that `BurnPlan` uses separately ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L466),
   [current/decomp/KSA/Burn.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Burn.cs#L46), [current/decomp/KSA/BurnPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnPlan.cs#L136-L145)).[^ksa-burn] [^ksa-burnplan] Compendium only
   reads the former, hence "excluding planned burns".

## Lifecycle and timing

- **`frame:gauges`, main thread.** `Program.OnFrame` calls `UpdateGauges(ControlledVehicle, dtPlayer)`
  after `ImGui.Render` and only when the game is loaded and a vehicle is controlled
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2286-L2300)). The postfix therefore runs once per frame for the controlled
  vehicle only, even though the cache is per vehicle.
- **State it reads.** `vehicle.FlightPlan` and the vehicle's ECL position were installed at
  `ApplyVehicleSolvers` earlier in the frame and are not replaced until the next apply; the solver step
  that is running meanwhile works on its own copies. Orbits and parent positions are evaluated
  analytically at the sample time, which is read-only. `Universe.GetElapsedTime()` gives the settled
  simulation time ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2399)).[^ksa-universe]
- **Why here.** The rollers are packed for display right here, so overriding them in a postfix is the
  last write before `GaugeData.AnimateAll`, with no need to touch the gauge definitions.
- **Cost.** A recompute evaluates about a thousand samples per patch plus the refinements, all on the
  main thread; the one-second throttle bounds that to once per real second while the plan and target are
  unchanged.

## Key code

| Path | Role |
|---|---|
| [src/RootTargetFlightComputerPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetFlightComputerPatches.cs#L97-L120) | `UpdateGauges` postfix that fills the TGT rollers |
| [src/RootClosestApproach.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootClosestApproach.cs#L27-L45) | Cache and refresh throttle |
| [src/RootClosestApproach.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootClosestApproach.cs#L47-L107) | Per-patch window, distance function, periapsis candidate |
| [src/RootClosestApproach.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootClosestApproach.cs#L109-L206) | Sampling plus golden-section minimizer |

## Snippets

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

```csharp
// src/RootClosestApproach.cs L51-L83
foreach (var patch in plan.Patches)
{
    UniverseTime start = UniverseTime.Max(now, patch.StartTime);
    UniverseTime end = UniverseTime.Min(limit, patch.EndTime);
    if (end < start)
    {
        continue;
    }

    double duration = (end - start).Seconds();
    // For a direct orbit of this root, one revolution contains every distance.
    if (ReferenceEquals(patch.Orbit.Parent, root) && patch.Orbit.IsBound() &&
        double.IsFinite(patch.Orbit.Period) && patch.Orbit.Period > 0)
    {
        duration = Math.Min(duration, patch.Orbit.Period);
    }
    double DistanceSquared(double offset)
    {
        UniverseTime time = start + offset;
        var orbit = patch.Orbit;
        var parent = orbit.Parent;
        double3 parentPosition = parent is IOrbiter orbiter
            ? orbiter.GetPositionEcl(time) : parent.GetPositionEcl();
        double3 position = parentPosition +
            orbit.GetStateVectorsAt(time).PositionCci.Transform(parent.GetCci2Cce());
        return (position - root.GetPositionEcl()).LengthSquared();
    }

    var minimum = FindMinimum(DistanceSquared, duration);
    if (minimum.HasValue)
    {
        Consider(new RootApproach(Math.Sqrt(minimum.Value.SquaredDistance), start + minimum.Value.Offset));
    }
```

```csharp
// src/RootClosestApproach.cs L171-L204
// Combine uniform coverage with logarithmic near-term coverage.
var times = new SortedSet<double> { 0, duration };
double logRange = Math.Log(1 + duration);
for (int i = 1; i < Samples; i++)
{
    times.Add(duration * i / Samples);
    times.Add(Math.Exp(logRange * i / Samples) - 1);
}
foreach (double time in times)
{
    if (time == 0)
    {
        continue;
    }
    double value = distanceSquared(time);
    Evaluate(time, value);
    if (previousTime == 0)
    {
        Refine(0, time);
    }
    if (previousTime > 0 && previousValue <= beforeValue && previousValue <= value &&
        (previousValue < beforeValue || previousValue < value))
    {
        Refine(beforeTime, time);
    }
    if (time == duration)
    {
        Refine(previousTime, time);
    }
    beforeTime = previousTime;
    beforeValue = previousValue;
    previousTime = time;
    previousValue = value;
}
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `Program.UpdateGauges` (private static) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2760-L2840) | Postfix target |
| `Program.TgtRelVelocityRoller`, `TgtRelDistanceRoller`, `TgtClosestDistanceRoller`, `TgtClosestTimeRoller` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L382-L388) | Gauge digits |
| `DigitRoller.IsDisabled`, `PackTarget`, `SetTimeTarget` | [current/decomp/KSA/DigitRoller.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DigitRoller.cs#L39) | Writing the readouts |
| `Vehicle.FlightPlan`, `FlightPlan.Patches` | [current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L68) | Coast trajectory |
| `PatchedConic.Orbit`, `StartTime`, `EndTime` | [current/decomp/KSA/PatchedConic.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PatchedConic.cs#L64-L72) | Patch interval |
| `Orbit.Parent`, `Period`, `IsBound`, `GetStateVectorsAt`, `GetNextPeriapsisTime` | [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1209-L1213) | Distance function and periapsis candidate |
| `IOrbiter.GetPositionEcl(UniverseTime)`, `IParentBody.GetCci2Cce` | [current/decomp/KSA/IOrbiter.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IOrbiter.cs#L42), [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L51) | Parent position at sample time |
| `Universe.GetElapsedTime` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2399) | Now |

## Pitfalls and version notes

- **Estimate, not an encounter.** Sampling can miss short encounters, distances are center to center,
  and a minimum at the window boundary is reported as is.[^cmp-readme-md]
- **Up to one second stale.** On an unchanged plan the readout refreshes at most once per real second,
  sooner when simulation time advances 60 s or the cached approach time passes.
- **The throttle only helps on rails.** A full-physics step rebuilds the vehicle's orbit
  (`PhysicsBubble.PopulateAnalyticStatesFromKinematicStates` calls `VehicleUpdateState.SetCurrentOrbit`,
  which creates a new `FlightPlan`), and the next apply installs that object
  ([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L2902-L2916), [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L565-L580),
  [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2461-L2464)).[^ksa-physicsbubble] [^ksa-vehicleupdatestate] While the vehicle is in full physics
  (thrusting, in atmosphere, in a multi-vehicle bubble) the plan identity changes almost every frame,
  the cache misses and the full search runs on the main thread each frame.
- **A throw would reach `OnFrame`.** `FindMinimum` throws on a non-finite distance; inside a postfix on
  `UpdateGauges` that exception would propagate into `Program.OnFrame`. A defensive copy would catch and
  disable the rollers instead.
- **Main-thread cost scales with patches.** Long multi-patch plans multiply the sample count; the
  per-patch cap to one period only applies to orbits of the root itself.
- **Signature binding.** The postfix declares only `Vehicle vehicle`; a rename of that parameter in a
  future build makes Harmony reject the patch at load.

## See also

- [Navigation-only root targets](root-navigation-targets.md)
- [Navball target marker and TGT frame for a target without an orbit](navball-target-marker-and-tgt-frame.md)
- [Flight-computer attitude tracking against a root target](flight-computer-root-attitude-tracking.md)
- [Rate-limit a solve on the wall clock and cache results per object with real and simulated time limits](../../../howto/flight-and-orbits/guidance/run-an-expensive-solve-without-stalling-the-frame/run-an-expensive-solve-without-stalling-the-frame-5.md) - how-to article demonstrating this feature
- [Search the flight plan for the closest approach with uniform and log-spaced samples and golden-section refinement, then write it into the stock TGT gauge](../../../howto/flight-and-orbits/map-view/find-closest-approach-along-the-flight-plan/find-closest-approach-along-the-flight-plan-1.md) - how-to article demonstrating this feature

[^cmp-readme-md]: Compendium: README.md
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-flightplan]: KSA.FlightPlan (decompiled, build 2026.10.10.5554)
[^ksa-patchedconic]: KSA.PatchedConic (decompiled, build 2026.10.10.5554)
[^cmp-roottargetflightcomputerpatches]: Compendium: src/RootTargetFlightComputerPatches.cs
[^cmp-rootclosestapproach]: Compendium: src/RootClosestApproach.cs
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
[^ksa-burn]: KSA.Burn (decompiled, build 2026.10.10.5554)
[^ksa-burnplan]: KSA.BurnPlan (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatestate]: KSA.VehicleUpdateState (decompiled, build 2026.10.10.5554)
