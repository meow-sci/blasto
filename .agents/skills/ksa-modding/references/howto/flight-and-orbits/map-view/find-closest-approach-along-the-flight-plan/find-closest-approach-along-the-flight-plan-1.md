---
type: How-To
title: Search the flight plan for the closest approach with uniform and log-spaced samples and golden-section refinement, then write it into the stock TGT gauge
description: Evaluate the distance to a target along every coast patch of Vehicle.FlightPlan, sample uniformly and log-spaced, refine local minima with golden-section search, cache the result per vehicle, and override the stock TGT closest-approach rollers from a Program.UpdateGauges postfix
category: flight-and-orbits
group: map-view
topic: find-closest-approach-along-the-flight-plan
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/compendium/features/root-closest-approach-search]
techniques: [numeric-minimization, harmony-postfix, throttled-cache]
hooks: [harmony-postfix KSA.Program.UpdateGauges]
lifecycle: [frame:gauges]
game_types:
  - KSA.FlightPlan
  - KSA.PatchedConic
  - KSA.Orbit
  - KSA.IOrbiter
  - KSA.IParentBody
  - KSA.Program
  - KSA.DigitRoller
  - KSA.Universe
tags:
  - closest-approach
  - flight-plan
  - patched-conic
  - golden-section
  - minimization
  - gauge
  - digit-roller
  - target
---
Outcome: the stock TGT gauge shows a closest-approach distance and time for a target the game's own encounter search does not cover. Compendium does this for navigation-only "root" targets (bodies without an orbit the patched-conic solver tracks): it samples the vehicle's coast trajectory over ten game years, refines every local minimum, caches the answer per vehicle and writes it into the stock digit rollers.[Root closest-approach search](../../../../mods/compendium/features/root-closest-approach-search.md) The same search works for any target you can evaluate at an arbitrary time.

Prerequisites: StarMap and Harmony (Compendium calls `PatchAll` on its assembly); a target you can position at time `t` (an `Astronomical` here).

## Background: how the game does it

- **Stock lookup.** `Program.UpdateGauges(Vehicle vehicle, double dt)` is private static. For the TGT rollers it asks `vehicle.FlightPlan.TryFindNextClosestApproach(vehicle.Target, now)`, which only returns encounters each `PatchedConic` has already found for that `IOrbiter`; a target the solver never tracked gets nothing:[^ksa-program] [^ksa-flightplan]

```csharp
// current/decomp/KSA/Program.cs L2814-L2822
		Encounter? encounter = ((vehicle.Target != null) ? vehicle.FlightPlan.TryFindNextClosestApproach(vehicle.Target, Universe.GetElapsedTime()) : ((Encounter?)null));
		TgtClosestDistanceRoller.IsDisabled = !encounter.HasValue;
		TgtClosestTimeRoller.IsDisabled = !encounter.HasValue;
		if (encounter.HasValue)
		{
			double closestDistance = encounter.Value.ClosestDistance;
			TgtClosestDistanceRoller.PackTarget(closestDistance, 1, UnitMask.Millimeter | UnitMask.Centimeter | UnitMask.Meter | UnitMask.Kilometer | UnitMask.Gigameter | UnitMask.AU | UnitMask.LightYear);
			TgtClosestTimeRoller.SetTimeTarget(encounter.Value.GameTime.Seconds() - Universe.GetElapsedSeconds());
		}
```

- **The rollers** `TgtRelVelocityRoller`, `TgtRelDistanceRoller`, `TgtClosestDistanceRoller` and `TgtClosestTimeRoller` are public static `DigitRoller`s on `Program`, with `IsDisabled`, `PackTarget(value, decimals, UnitMask)` and `SetTimeTarget(seconds)`.[^ksa-digitroller] [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L382-L388) [current/decomp/KSA/DigitRoller.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DigitRoller.cs#L282-L296)
- **The trajectory.** `Vehicle.FlightPlan.Patches` is a `List<PatchedConic>`; each has `Orbit`, `StartTime` and `EndTime`. Planned burns are not in it: each `Burn` carries its own plan.[^ksa-patchedconic] [current/decomp/KSA/PatchedConic.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PatchedConic.cs#L64-L72)
- **Evaluating a patch at any time.** `Orbit.GetStateVectorsAt(UniverseTime)` returns CCI state relative to `Orbit.Parent`; `IParentBody.GetCci2Cce()` rotates it to the ecliptic-aligned frame and `IOrbiter.GetPositionEcl(UniverseTime)` places the parent at that time. `Orbit.GetNextPeriapsisTime`, `IsBound()` and `Period` bound the search.[^ksa-orbit] [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L2171-L2195) [current/decomp/KSA/IOrbiter.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IOrbiter.cs#L42)

## Lifecycle and timing

`Program.OnFrame` calls `UpdateGauges(ControlledVehicle, dtPlayer)` after `ImGui.Render`, only when the game is loaded and a vehicle is controlled, so the postfix runs once per frame for the controlled vehicle (`frame:gauges`, main thread).[^ksa-program] [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2286-L2300) The flight plan and positions it reads were installed by `ApplyVehicleSolvers` earlier in the frame; orbit evaluation at a sample time is a pure read. Overriding the rollers here is the last write before the gauges animate. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Clip each patch to the search window and build a distance function.** For a patch that orbits the target directly and is bound, one period contains every distance. The distance is evaluated in the ecliptic at the sample time, and the next periapsis inside the window is added as an extra candidate:

```csharp
// src/RootClosestApproach.cs L47-L83
        internal static RootApproach? Calculate(FlightPlan plan, Astronomical root, UniverseTime now)
        {
            RootApproach? best = null;
            UniverseTime limit = now + HorizonSeconds;
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

2. **Sample uniformly and log-spaced.** 511 uniform and 511 logarithmic interior offsets (`Samples = 512`) are merged in a `SortedSet`, so coverage is dense near now and even over ten years. Every strict local minimum, plus the first and last intervals, is refined:

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

3. **Refine with golden-section search.** 48 iterations shrink each bracket by the golden ratio; a non-finite distance throws rather than reporting a bogus minimum:

```csharp
// src/RootClosestApproach.cs L140-L170
            void Refine(double left, double right)
            {
                const double ratio = 0.6180339887498949;
                double a = right - ratio * (right - left);
                double b = left + ratio * (right - left);
                double fa = distanceSquared(a);
                double fb = distanceSquared(b);
                for (int iteration = 0; iteration < 48; iteration++)
                {
                    Evaluate(a, fa);
                    Evaluate(b, fb);
                    if (fa <= fb)
                    {
                        right = b;
                        b = a;
                        fb = fa;
                        a = right - ratio * (right - left);
                        fa = distanceSquared(a);
                    }
                    else
                    {
                        left = a;
                        a = b;
                        fa = fb;
                        b = left + ratio * (right - left);
                        fb = distanceSquared(b);
                    }
                }
                Evaluate(a, fa);
                Evaluate(b, fb);
            }
```

4. **Cache per vehicle.** A `ConditionalWeakTable<Vehicle, Cache>` reuses the answer while the target and the `FlightPlan` object are unchanged, simulated time has not gone backwards or advanced 60 s, the cached approach is still in the future, and less than one real second has passed. The code and the reasoning are in [Run an expensive solve without stalling the frame, approach 5](../../guidance/run-an-expensive-solve-without-stalling-the-frame/run-an-expensive-solve-without-stalling-the-frame-5.md).

### Override the stock gauge readouts

5. **Postfix `Program.UpdateGauges` and overwrite the rollers.** Harmony binds the private static method by name and the `vehicle` parameter by name. Re-enable the rollers the stock body disabled, pack values with the same unit mask the game uses, and disable the closest-approach pair when there is no answer:

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

6. **Expect** the TGT distance, relative speed, closest distance and time-to-closest digits to fill while the root target is selected, and the last two to stay dark when no coast patch falls inside the window.

## Complete example

| File | Role |
|---|---|
| [src/RootClosestApproach.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootClosestApproach.cs) | Window per patch, distance function, sampling, golden-section, cache |
| [src/RootTargetFlightComputerPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetFlightComputerPatches.cs#L97-L120) | `UpdateGauges` postfix |
| [src/Patcher.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Patcher.cs) | `PatchAll` on the mod assembly |

## Variations and gotchas

- **An estimate, not an encounter.** Sampling can miss a short flyby, distances are centre to centre, and a minimum at a window boundary (now, or ten years out) is reported as is.
- **Coast only.** `Vehicle.FlightPlan` excludes planned burns; search `Burn.FlightPlan` too if you want post-burn approaches.
- **The cache only helps on rails.** In full physics the game installs a new `FlightPlan` object almost every frame, the identity check misses and the full search runs each frame on the main thread.
- **A throw reaches `OnFrame`.** `FindMinimum` throws on a non-finite distance inside a postfix on `UpdateGauges`; wrap it and disable the rollers instead.
- **Parameter-name binding.** The postfix declares `Vehicle vehicle`; a renamed parameter in a future build makes Harmony reject the patch at load.

## See also

- [Overview](overview.md); [Closest point of approach from relative motion](find-closest-approach-along-the-flight-plan-2.md)
- [Set and clear the vehicle target](../../autopilot/set-and-clear-the-vehicle-target/overview.md)
- [Aim a transfer at a flyby periapsis](../../transfers/aim-a-transfer-at-a-flyby-periapsis/overview.md)
- [Root closest-approach search (mod feature)](../../../../mods/compendium/features/root-closest-approach-search.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-flightplan]: KSA.FlightPlan (decompiled, build 2026.10.10.5554)
[^ksa-digitroller]: KSA.DigitRoller (decompiled, build 2026.10.10.5554)
[^ksa-patchedconic]: KSA.PatchedConic (decompiled, build 2026.10.10.5554)
[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
