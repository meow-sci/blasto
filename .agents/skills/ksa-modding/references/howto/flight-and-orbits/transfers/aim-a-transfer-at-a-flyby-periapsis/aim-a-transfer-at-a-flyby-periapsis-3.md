---
type: How-To
title: Solve your own universal-variable Lambert against a spinning-planet target
description: Solve over flight time in the parent body's inertial frame, carry the surface aim point forward by the planet's rotation, and search for the cheapest flight time with a coarse scan and golden section
category: flight-and-orbits
group: transfers
topic: aim-a-transfer-at-a-flyby-periapsis
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/icbm-ballistic-computer]
techniques: [lambert-universal-variables]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:draw-ui, frame:dispatch-solvers]
game_types: [KSA.Orbit, KSA.IParentBody, KSA.Vehicle, KSA.Celestial, KSA.CelestialFrameMath]
tags: [lambert, universal-variables, ballistic, flight-time, spinning-planet, cci, intercept]
---

Outcome: the velocity to be gained for a shot from where a craft is to a latitude and longitude on a rotating body, with the aim point moved by the planet's spin over the flight time. KSArmory's ballistic computer uses it to fly whatever rocket the player built to a chosen place, and the same solver aims a transfer at a point rather than a centre.[A ballistic computer that flies any player-built rocket to a latitude and longitude](../../../../mods/ksarmory/features/icbm-ballistic-computer.md) Prerequisite: StarMap.

## Background: how the game does it

The game's `OrbitalTransfers.SuperiorLambert` is tied to the planner. A mod that needs Lambert from arbitrary points writes its own and works in the **parent body's inertial frame (CCI)**, not the ecliptic: over a half-hour flight the ecliptic carries about 54,000 km (29.8 km/s over 1,800 s; the mod source says "54 million kilometres", which is a factor of 1000 too large) of the planet's travel through every term, which CCI removes exactly. The spin axis is exactly +Z in CCI because KSA builds CCF from CCI by a rotation about `UnitZ` only (`CelestialFrameMath.ComputeCcf2Cci`), so the planet's rotation is one angle:
`(PositionEcl - Parent.GetPositionEcl()).Transform(Parent.GetCce2Cci())` converts a world sample.[^ksa-celestialframemath] [current/decomp/KSA/CelestialFrameMath.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialFrameMath.cs#L7-L10) [^ksa-celestial] [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L526-L575) Engine figures come from `FlightComputer.ActiveEnginePerformanceMax` and `Vehicle.TotalMass`/`PropellantMass`.

## Lifecycle and timing

- `frame:draw-ui`: the computer's update runs inside the mod's simulation step, from `[StarMapAfterGui]`, on the simulated step and unclamped, sampling the state the engine applied in this frame's `PrepareFrame`.
- `frame:dispatch-solvers` (next frame): throttle, engine and translation keys reach the worker at the next `PrepareWorker`; attitude and staging are written in a `PrepareWorker` prefix. Every command acts one frame later, which is why the cutoff is timed rather than observed (see [Execute burns with RCS translation and throttle inputs from code](../../autopilot/execute-burns-with-rcs-and-throttle-inputs/overview.md)).

See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Express everything in CCI:** craft position and velocity, the aim site as latitude and longitude on a named body (never a position; re-read from the world each cycle), the planet's `Mu`.
2. **Parameterise by flight time.** Carry the aim point forward by the planet's rotation over that time before solving, which collapses a moving target into one solve:

```csharp
// src/KSArmory/Sim/BallisticArc.cs L62-L82
public static bool TrySolve(BallisticBody body, double3 fromCci, double3 aimNowCci,
                            double flightSeconds, out Solution solution, bool longWay = false)
{
    solution = default;

    if (!body.IsUsable) return false;
    if (!(flightSeconds >= MinFlightSeconds)) return false;

    double3 arrivalCci = body.CarryCci(aimNowCci, flightSeconds);

    if (!Lambert.TrySolve(fromCci, arrivalCci, flightSeconds, body.Mu, out Lambert.Transfer t, longWay))
    {
        return false;
    }

    Extremes(body.Mu, fromCci, t.DepartureVelocityCci, arrivalCci, out double apogee, out double lowest);

    solution = new Solution(t.DepartureVelocityCci, t.ArrivalVelocityCci, arrivalCci,
                            flightSeconds, flightSeconds, apogee, lowest);
    return true;
}
```

3. **Solve Lambert in universal variables with bisection** on `psi` within `+-4 pi^2` (48 iterations); one routine covers a depressed hyperbolic shot and a lofted ellipse, and bisection cannot run away near the parabolic point as Newton does. Reject non-finite inputs, non-positive flight time, and the antipodal shot where the transfer plane is undetermined:

```csharp
// src/KSArmory/Sim/Lambert.cs L42-L66
public static bool TrySolve(double3 fromCci, double3 toCci, double flightSeconds, double mu,
                            out Transfer transfer, bool longWay = false)
{
    transfer = default;

    if (!Vec.IsFinite(fromCci) || !Vec.IsFinite(toCci)) return false;
    if (!(flightSeconds > 0.0) || !double.IsFinite(flightSeconds)) return false;
    if (!(mu > 0.0)) return false;

    double r1 = fromCci.Length();
    double r2 = toCci.Length();
    if (!(r1 > 0.0) || !(r2 > 0.0)) return false;

    double cosTransfer = Math.Clamp(Vec.Dot(fromCci, toCci) / (r1 * r2), -1.0, 1.0);

    // A is zero for a transfer angle of exactly zero or exactly half a turn. The second is the
    // antipodal shot, where the two points and the centre are collinear and no plane is
    // determined: every azimuth is an equally valid answer, so there is no solution to return.
    double a = (longWay ? -1.0 : 1.0) * Math.Sqrt(r1 * r2 * (1.0 + cosTransfer));
    if (Math.Abs(a) < 1e-9) return false;

    double psiLow = PsiLower;
    double psiUp = PsiUpper;
    double psi = 0.0;
    double y = 0.0;
```

   The velocities come from the Lagrange coefficients:

```csharp
// src/KSArmory/Sim/Lambert.cs L93-L104
double f = 1.0 - y / r1;
double g = a * Math.Sqrt(y / mu);
double gDot = 1.0 - y / r2;
if (Math.Abs(g) < 1e-9) return false;

double3 v1 = (toCci - fromCci * f) / g;
double3 v2 = (toCci * gDot - fromCci) / g;

if (!Vec.IsFinite(v1) || !Vec.IsFinite(v2)) return false;

transfer = new Transfer(v1, v2);
return true;
```

4. **Search the cheapest flight time:** objective is the length of the velocity still to gain, scanned coarsely then refined by golden section inside the best bracket (the cost curve has a second minimum at the long-way-round arc), seeded with the last cycle's time. A loft multiplies the cheapest time, carried separately so it does not compound each cycle; a minimum arrival angle bounds the search instead of nudging it.
5. **Steer by velocity-to-be-gained:** thrust along (required velocity from where the vehicle will be at cutoff) minus (what it will have); re-solved against the actual state, so errors on the way cost propellant, not accuracy.
6. **Time the cutoff** with a countdown run down by the achieved throttle (engines stop on frame boundaries, so a threshold on remaining velocity hunts), re-solve every step inside the last 0.75 s and throttle back for the last 2 s.
7. **Check the answer** with an impact predictor that flies the arc (with drag) against the real height field and moves the aim by what the flown arc loses.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Sim/Lambert.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Lambert.cs) | universal-variable Lambert solver |
| [src/KSArmory/Sim/BallisticArc.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BallisticArc.cs#L62-L130) | flight-time parameterised arc and cheapest search |
| [src/KSArmory/Sim/IcbmProgram.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/IcbmProgram.cs) | phases, solve cadence, timed cutoff |
| [src/KSArmory/Ksa/IcbmComputer.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/IcbmComputer.cs#L3453-L3518) | `Sample`: world to state in CCI |

## Variations and gotchas

- **Do not use ECL for long arcs;** the common motion of the planet is a huge constant in every term.
- **The long way round** costs enormously more; accept it only when asked.
- **The conic and the flown arc can disagree** (drag, terrain), so correct the aim with the flown result.
- All decision-making lives in game-free classes flown headlessly by a test rig; only two conversions touch KSA (world into state, answer into commands).

## See also

- [Overview](overview.md); [impact-parameter offset](aim-a-transfer-at-a-flyby-periapsis-1.md)
- [A ballistic computer that flies any player-built rocket to a latitude and longitude](../../../../mods/ksarmory/features/icbm-ballistic-computer.md)
- [Build a local site frame and compute a launch window into a target plane](../build-a-site-frame-and-compute-a-launch-window/overview.md)
- Related topics: [Predict impact points with drag and build a ballistic computer](../../guidance/predict-impact-point-and-build-a-ballistic-computer/overview.md)

[^ksa-celestialframemath]: KSA.CelestialFrameMath (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
