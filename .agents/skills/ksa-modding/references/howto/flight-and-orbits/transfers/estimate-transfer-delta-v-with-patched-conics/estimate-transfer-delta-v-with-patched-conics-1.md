---
type: How-To
title: Estimate a coplanar Hohmann transfer to a circular altitude from the current orbit
description: Compute departure, arrival and total delta-v and the transfer time for a two-burn Hohmann transfer from the vehicle's radius to a target altitude using vis-viva, as a one-line readout
category: flight-and-orbits
group: transfers
topic: estimate-transfer-delta-v-with-patched-conics
approach: 1
difficulty: beginner
prerequisites: [starmap, harmony]
demonstrated_by: [mods/kitten-engineer-redux/features/orbit-readouts-and-hohmann-estimate]
techniques: [flight-math]
hooks: [harmony-postfix KSA.Vehicle.OnDrawUi]
lifecycle: [frame:draw-ui]
game_types: [KSA.Orbit, KSA.Vehicle, KSA.IParentBody, KSA.Constants]
tags: [hohmann, vis-viva, delta-v, transfer-time, hud, estimate]
---

Outcome: type a target altitude, see departure delta-v, arrival delta-v, total and transfer time. Kitten-Engineer-Redux's planner section does exactly that.[Orbit readouts and Hohmann estimate](../../../../mods/kitten-engineer-redux/features/orbit-readouts-and-hohmann-estimate.md)
Prerequisites: a draw hook (the mod uses a `Vehicle.OnDrawUi` postfix); the math needs no patches.

## Background: how the game does it

The game offers no one-line Hohmann function for a vehicle; its Transfer Planner solves Lambert problems between bodies. The classical two-burn transfer is textbook vis-viva: the transfer ellipse has semi-major axis `a_t = (r1 + r2) / 2`, speeds `sqrt(mu (2/r - 1/a_t))` at both ends and flight time `pi sqrt(a_t^3 / mu)`. The inputs are all available:
`Orbit.StateVectors.PositionCci` (radius from the parent centre), `Orbit.Parent.MeanRadius`, `Orbit.Parent.Mass` and `KSA.Constants.GRAVITATIONAL_CONSTANT` (or `Orbit.Parent.Mu`).[^ksa-orbit] [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1181-L1207) [^ksa-constants] [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L25)

## Lifecycle and timing

The estimate is computed when its HUD section is drawn, inside the `Vehicle.OnDrawUi` postfix (`frame:draw-ui`, main thread), from the state applied this frame. It writes nothing. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Take the current radius and the target radius:** `r1 = |PositionCci|`, `r2 = MeanRadius + targetAltitude`; `mu = G * Parent.Mass` (or `Parent.Mu`).
2. **Refuse what the formula cannot handle:** hyperbolic orbits (`Eccentricity >= 1`), negative targets, missing data; return zero delta-v when `|r2 - r1| < 1 cm`.
3. **Compute the estimate:**

```csharp
// OrbitPlanner.cs L18-L58
public static OrbitTransferEstimate EstimateCircularTransfer(Vehicle vehicle, double targetAltitude)
{
    Orbit orbit = vehicle.Orbit;
    double currentRadius = orbit.StateVectors.PositionCci.Length();
    double currentAltitude = currentRadius - orbit.Parent.MeanRadius;
    double targetRadius = orbit.Parent.MeanRadius + targetAltitude;
    double mu = KSA.Constants.GRAVITATIONAL_CONSTANT * orbit.Parent.Mass;

    if (orbit.Eccentricity >= 1.0)
        return Invalid(currentAltitude, targetAltitude, "Requires a bound starting orbit");
    if (!double.IsFinite(targetAltitude) || targetAltitude < 0.0 || targetRadius <= 0.0)
        return Invalid(currentAltitude, targetAltitude, "Target altitude must be non-negative");
    if (currentRadius <= 0.0 || mu <= 0.0)
        return Invalid(currentAltitude, targetAltitude, "Orbit data unavailable");

    double currentCircularSpeed = Math.Sqrt(mu / currentRadius);
    double targetCircularSpeed = Math.Sqrt(mu / targetRadius);
    if (Math.Abs(targetRadius - currentRadius) < 0.01)
    {
        return new OrbitTransferEstimate(
            true, currentAltitude, targetAltitude, 0.0, 0.0, 0.0, 0.0,
            "Already at target radius; assumes circular, coplanar orbit");
    }

    double transferSemiMajorAxis = (currentRadius + targetRadius) * 0.5;
    double transferSpeedAtDeparture = Math.Sqrt(mu * (2.0 / currentRadius - 1.0 / transferSemiMajorAxis));
    double transferSpeedAtArrival = Math.Sqrt(mu * (2.0 / targetRadius - 1.0 / transferSemiMajorAxis));
    double departureDeltaV = Math.Abs(transferSpeedAtDeparture - currentCircularSpeed);
    double arrivalDeltaV = Math.Abs(targetCircularSpeed - transferSpeedAtArrival);
    double transferTime = Math.PI * Math.Sqrt(Math.Pow(transferSemiMajorAxis, 3.0) / mu);

    return new OrbitTransferEstimate(
        true,
        currentAltitude,
        targetAltitude,
        departureDeltaV,
        arrivalDeltaV,
        departureDeltaV + arrivalDeltaV,
        transferTime,
        "Coplanar Hohmann estimate; assumes a circular starting orbit");
}
```

4. **State the assumptions on screen.** The status string reads "Coplanar Hohmann estimate; assumes a circular starting orbit" and is shown every frame.
5. **Take the target altitude from an `ImGui.InputDouble`** (step 1 km, fast step 10 km) persisted with the HUD settings ([FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L391-L408)).
6. **Do not run it every frame for hidden sections**: the planner runs only when its section is drawn.

## Complete example

| File | Role |
|---|---|
| [OrbitPlanner.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/OrbitPlanner.cs) | Hohmann estimate |
| [FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L291-L419) | orbit, elements, planner and surface sections |
| [OrbitSummary.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/OrbitSummary.cs) | the orbit record used beside it |

## Variations and gotchas

- **The starting orbit is treated as circular** at the current radius even when it is eccentric; the departure burn then corresponds to a burn at this instant, not at an apse. For an eccentric orbit use [Create, chain and read stock maneuver nodes from code](../../autopilot/create-and-read-maneuver-nodes/overview.md) (set-apsis impulses) instead.
- **Not coplanar-aware.** A plane change is separate (see [Plan and total multi-leg and interstellar delta-v routes](../plan-multi-leg-and-interstellar-delta-v-routes/overview.md) for plane-change pricing).
- **`mu` from `Parent.Mass`** equals `Parent.Mu`; use `Orbit.Mu` for orbits around a barycenter only when intended.
- Altitude is above the mean radius, not terrain.

## See also

- [Overview](overview.md); [patched-conic model](estimate-transfer-delta-v-with-patched-conics-2.md)
- [Orbit readouts and Hohmann estimate](../../../../mods/kitten-engineer-redux/features/orbit-readouts-and-hohmann-estimate.md)
- [Read orbit elements, apsides and event countdowns safely](../../flight-state/read-orbit-elements-and-timing/overview.md)

[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
[^ksa-constants]: KSA.Constants (decompiled, build 2026.10.10.5554)
