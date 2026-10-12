---
type: Game Feature
title: Find the closest approach to a target along a trajectory
description: Locate the time and distance of closest approach to a target along the flight plan or a straight-line prediction, classify what is coming close, and write the result into the stock gauge digits
category: flight-and-orbits
group: map-view
topic: find-closest-approach-along-the-flight-plan
demonstrated_by:
  - mods/compendium/features/root-closest-approach-search
  - mods/ksarmory/features/cpa-threat-classification
lifecycle: [frame:gauges, frame:draw-ui]
game_types: [KSA.FlightPlan, KSA.PatchedConic, KSA.Orbit, KSA.Burn, KSA.IOrbiter, KSA.Program, KSA.DigitRoller]
tags: [closest-approach, flight-plan, encounter, cpa, gauge, digit-roller, target, minimization]
---
The stock TGT gauge shows a closest-approach distance and time for the vehicle's target, but only for targets the patched-conic solver already tracks as encounters, and only along orbits. Mods need the same answer for targets the solver ignores (a navigation-only body, a craft seconds away) and want to rank many contacts by how soon they arrive. Two mods show the two regimes: an orbital search along `Vehicle.FlightPlan`, and straight-line relative motion for nearby craft.

## Engine background

- **Flight plan.** `Vehicle.FlightPlan` is the coast trajectory, `FlightPlan.Patches` a `List<PatchedConic>` with `Orbit`, `StartTime`, `EndTime`; planned burns carry their own `Burn.FlightPlan`.[^ksa-flightplan] [current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L68) [current/decomp/KSA/Burn.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Burn.cs#L46)
- **Stock encounters.** `FlightPlan.TryFindNextClosestApproach(IOrbiter, UniverseTime)` returns the first `Encounter` (`ClosestDistance`, `GameTime`) any patch already found for that target.[^ksa-patchedconic] [current/decomp/KSA/PatchedConic.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PatchedConic.cs#L178-L189)
- **Evaluating an orbit at any time.** `Orbit.GetStateVectorsAt`, `GetNextPeriapsisTime`, `IsBound`, `Period`, with `IParentBody.GetCci2Cce()` and `IOrbiter.GetPositionEcl(UniverseTime)` to place it in the ecliptic.[^ksa-orbit] [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L2171-L2195)
- **Gauge digits.** `Program.UpdateGauges` (private static) writes the public static `DigitRoller`s `TgtRelVelocityRoller`, `TgtRelDistanceRoller`, `TgtClosestDistanceRoller` and `TgtClosestTimeRoller` once per frame after `ImGui.Render` (`frame:gauges`).[^ksa-program] [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2760-L2822) [current/decomp/KSA/DigitRoller.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DigitRoller.cs#L39)
- **Timing.** Both approaches read main-thread state installed by `ApplyVehicleSolvers` in this frame's `PrepareFrame`. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Sample patches uniformly and log-spaced, golden-section refine, cache per vehicle](find-closest-approach-along-the-flight-plan-1.md) | Compendium | numeric minimization, gauge override | `frame:gauges` | Works for any target over ten years of coast; about a thousand samples per patch on the main thread, throttled by a cache that only helps on rails |
| [Closest point of approach from relative motion](find-closest-approach-along-the-flight-plan-2.md) | KSArmory | frame-invariant signatures | `frame:draw-ui` | A few multiplications per contact, headless-testable; straight line only, so seconds-to-minutes horizons |

## See also

- [Understand KSA coordinate frames and epochs](../../flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md)
- [Aim a transfer at a flyby periapsis](../../transfers/aim-a-transfer-at-a-flyby-periapsis/overview.md)
- [Set and clear the vehicle target](../../autopilot/set-and-clear-the-vehicle-target/overview.md)
- [Run an expensive solve without stalling the frame](../../guidance/run-an-expensive-solve-without-stalling-the-frame/overview.md)

[^ksa-flightplan]: KSA.FlightPlan (decompiled, build 2026.10.10.5554)
[^ksa-patchedconic]: KSA.PatchedConic (decompiled, build 2026.10.10.5554)
[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
