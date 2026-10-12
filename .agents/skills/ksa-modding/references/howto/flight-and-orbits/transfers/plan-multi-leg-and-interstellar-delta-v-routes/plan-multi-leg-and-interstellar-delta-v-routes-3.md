---
type: How-To
title: Price an interstellar leg by reusing the stock burn model, with a golden-section speed search and a rocket-equation fallback
description: Fold the hyperbolic excess through every gravity well, search for the cheapest and the fastest affordable cruise speed, and compute fuel with InterstellarBurnPlan for an antimatter ram drive or the rocket equation otherwise
category: flight-and-orbits
group: transfers
topic: plan-multi-leg-and-interstellar-delta-v-routes
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksa-deltavmap/features/interstellar-routes-and-fuel]
techniques: [stock-model-reuse, golden-section-search]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.InterstellarBurnPlan
  - KSA.InterstellarDrive
  - KSA.InterstellarBurnEstimate
  - KSA.OrbitalTransfers
  - KSA.Vehicle
  - KSA.VehicleProperties
  - KSA.AstronomicalTemplate
tags: [interstellar, antimatter, rocket-equation, oberth, golden-section, fuel, cruise-speed]
---

Outcome: pick a destination in another star system and see departure, coast and capture lines, the cheapest trip at any speed, feasibility and a fuel estimate. For a vessel with an antimatter ram drive the fuel numbers are the stock planner's own; otherwise the rocket equation runs over the staged sequences.
KSA-DeltaVMap implements it.[Interstellar routes and fuel](../../../../mods/ksa-deltavmap/features/interstellar-routes-and-fuel.md) Prerequisite: StarMap for a draw hook.

## Background: how the game does it

The kernel follows the game's interstellar planner: Newtonian, an escape hyperbola with excess speed `vInf`, a straight coast of `distance / vInf` and a capture. Stock `SolveInterstellar` sets the transit time the same way and computes the start speed with `DepartureSpeedForExcess(mu, r, vInf) = sqrt(vInf^2 + 2 mu / r)`.[^ksa-orbitaltransfers]
[current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs#L399-L402) The stock planner only handles a vessel orbiting the source star directly; the mod chains every well from the parking orbit up. `InterstellarDrive.TryGetFrom` returns the rated thrust, jet power, exhaust velocity, minimum throttle and scoop capture area for the active antimatter ram cores; `InterstellarBurnPlan.Burn` and `CoastCollection` integrate a burn and the scoop.[^ksa-interstellardrive]
[current/decomp/KSA/InterstellarDrive.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InterstellarDrive.cs#L7-L37) [^ksa-interstellarburnplan] The speed cap is `OrbitalTransfers.MAX_INTERSTELLAR_SPEED` (0.9 c).[current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs#L228)

## Lifecycle and timing

On the main thread in the map draw. The section never re-lays out the map or re-routes: each frame it brings its figures current with keyed caches (leg geometry per route and periapsis, cheapest speed per park, fastest cruise per budget, strings per view key, drive fuel plan per record equality). See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Chain the wells.** For the departure body, list the levels from the system root down; each step carries the outer body's `Mu`, the inner body's transfer radius, the inner body's own speed (from its own `Orbit.Mu`, so stars of a multiple system move at the right speed) and optionally the sine of the aim direction against that level's orbital plane (`Orbit.GetOrbitNormalCci()`).
2. **Fold the excess inward:** at each level the vessel needs `v = sqrt(excess^2 + 2 mu_outer / r)` at the inner body's radius; subtracting the body's velocity leaves `excess' = sqrt(v^2 + u^2 - 2 u along)`; the last level ends in `max(0, sqrt(excess^2 + 2 mu_park / r_park) - v_park)`:

```csharp
// DeltaVMap/Dv/InterstellarMath.cs L80-L107
// Mirrors OrbitalTransfers.DepartureSpeedForExcess: the speed at radius r that leaves a well
// of gravitational parameter mu with excess speed vInf.
public static double SpeedForExcess(double mu, double radius, double vInf)
{
    return Math.Sqrt(vInf * vInf + 2.0 * mu / radius);
}

// The size of v - u for a vessel velocity v of size vNeeded and a body velocity u of size
// bodySpeed, where along is the component of v along u.
public static double RelativeExcess(double vNeeded, double bodySpeed, double along)
{
    double sq = vNeeded * vNeeded + bodySpeed * bodySpeed - 2.0 * bodySpeed * along;
    return Math.Sqrt(Math.Max(0.0, sq));
}

public static double ExcessAtBody(double outerExcess, in WellStep step)
{
    return ExcessAtBody(outerExcess, in step, AlongSamples, AlongRefineSteps);
}

private static double ExcessAtBody(double outerExcess, in WellStep step, int samples, int refineSteps)
{
    double needed = SpeedForExcess(step.OuterMu, step.Radius, outerExcess);
    double along = step.AimSine is double aimSine && aimSine != 0.0
        ? BestSpeedAlongMotion(step.OuterMu, step.Radius, outerExcess, aimSine, samples, refineSteps)
        : needed;
    return RelativeExcess(needed, step.BodySpeed, along);
}
```

```csharp
// DeltaVMap/Dv/InterstellarMath.cs L198-L207
public static double ChainBurn(double vInf, ReadOnlySpan<WellStep> stepsOuterToInner, in ParkingOrbit park)
{
    if (stepsOuterToInner.IsEmpty && park.AimSine.HasValue)
        return ExcessAtBody(vInf, new WellStep(park.Mu, park.Radius, park.Speed, park.AimSine));

    double excess = vInf;
    for (int i = 0; i < stepsOuterToInner.Length; i++)
        excess = ExcessAtBody(excess, in stepsOuterToInner[i]);
    return Math.Max(0.0, SpeedForExcess(park.Mu, park.Radius, excess) - park.Speed);
}
```

3. **Search the cruise speed.** The total is not monotonic in `vInf`: out of a tilted plane a slow escape bends too much. `CheapestSpeed` does a doubling scan from 1 m/s to the cap and 60 golden-section steps between the neighbours of the best sample; `MaxCruiseSpeed(budget)` is 0 below the cheapest trip, the cap if the cap fits, else bisection on the rising branch.
4. **Compute fuel with the stock model** in the same order the stock planner does: speed-up burn from 0 over at most half the distance, a brake burn on the hydrogen left, the coast collection of the scoop, and a second brake with the scooped hydrogen:

```csharp
// DeltaVMap/Dv/InterstellarFuel.cs L54-L78
{
    public static DriveFuelPlan Plan(in DriveFuelInputs inputs)
    {
        InterstellarDrive drive = inputs.Drive;
        InterstellarBurnEstimate departure = InterstellarBurnPlan.Burn(in drive, inputs.Mass, 0.0, inputs.CruiseSpeed,
            inputs.DepartureAntimatter, inputs.HydrogenAboard, 0.5 * inputs.Distance, inputs.GasDensity);
        double hydrogenLeft = Math.Max(0.0, inputs.HydrogenAboard - departure.CarriedHydrogen);
        double brakeDistance = inputs.Distance - departure.Distance;
        InterstellarBurnEstimate brake = InterstellarBurnPlan.Burn(in drive, departure.EndMass, departure.EndSpeed, inputs.FinalSpeed,
            inputs.BrakeAntimatter, hydrogenLeft, brakeDistance, inputs.GasDensity);

        double coast = departure.EndSpeed > 0.0
            ? Math.Max(0.0, inputs.Distance - departure.Distance - brake.Distance) / departure.EndSpeed
            : 0.0;
        double scooped = InterstellarBurnPlan.CoastCollection(in drive, inputs.GasDensity, departure.EndSpeed, coast,
            inputs.HydrogenSpace + departure.CarriedHydrogen);
        if (scooped > 0.0)
        {
            brake = InterstellarBurnPlan.Burn(in drive, departure.EndMass + scooped, departure.EndSpeed, inputs.FinalSpeed,
                inputs.BrakeAntimatter, hydrogenLeft + scooped, brakeDistance, inputs.GasDensity);
        }
        return new DriveFuelPlan(departure, brake, coast, scooped);
    }

    // Read the vessel's drive and propellant. False when no ACTIVE engine carries an antimatter
```

5. **Without a drive**, walk the staged sequences in firing order with the rocket equation: within a sequence the burned share is `(1 - q^(dv / DeltaV)) / (1 - q)` with `q = (wet - burned) / wet`; past the last stage report the extra propellant the last stage alone would need (a lower bound).
6. **Choose the speed mode** (`Preset`, `Custom` slider, `Max`) as a stored mode, not a speed, so MAX keeps following the budget during a burn; when the budget cannot pay the cheapest trip, resolve MAX to the cheapest speed and say so.

## Complete example

| File | Role |
|---|---|
| [DeltaVMap/Dv/InterstellarMath.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/InterstellarMath.cs) | kernel, chain burns, searches |
| [DeltaVMap/Model/InterstellarLegs.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/InterstellarLegs.cs) | well chains, departure park, vessels in transit |
| [DeltaVMap/Dv/InterstellarFuel.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/InterstellarFuel.cs) | line-for-line copy of the stock fuel plan |

## Variations and gotchas

- **A departure chain is plane-limited only on the level directly under the root**; deeper levels take the free best case and are a lower bound. Arrival chains may spend part of the burn during the coast to rotate into each plane; with zero correction the result equals the plain chain.
- **A vessel in transit on an open orbit** has no departure; the mod reads the excess `sqrt(mu (e - 1) / pe)` and the asymptote direction, and adds a course change `vInf * sqrt(2 - 2 cos(angle))` for onward trips.
- **Copied private logic can drift**: `InterstellarFuel.Plan` mirrors the private `TransferPlanner.PlanInterstellarFuel`, and the rocket-equation share mirrors `TransferPlanner.BurnedShare`; re-check them on game updates.
- A capture at a star uses a periapsis slider (default 1 AU) kept inside half a finite SOI and outside `2 R`.

## See also

- [Overview](overview.md); [route finding](plan-multi-leg-and-interstellar-delta-v-routes-1.md)
- [Interstellar routes and fuel](../../../../mods/ksa-deltavmap/features/interstellar-routes-and-fuel.md), [Open the stock Transfer Planner preselected on an interstellar plan](../../../../mods/ksa-deltavmap/features/stock-transfer-planner-bridge.md)
- [Add plan types to the stock Transfer Planner](../add-plan-type-to-the-transfer-planner/overview.md)

[^ksa-orbitaltransfers]: KSA.OrbitalTransfers (decompiled, build 2026.10.10.5554)
[^ksa-interstellardrive]: KSA.InterstellarDrive (decompiled, build 2026.10.10.5554)
[^ksa-interstellarburnplan]: KSA.InterstellarBurnPlan (decompiled, build 2026.10.10.5554)
