---
type: Mod Feature
title: A ballistic computer that flies any player-built rocket to a latitude and longitude
description: A per-craft computer samples the vehicle in the parent body's inertial frame, solves universal-variable Lambert over flight time against a spinning-planet target, steers by velocity-to-be-gained, times cutoff to the frame boundary and checks itself against a flown impact prediction
mod: ksarmory
category: flight-and-orbits
techniques:
  - custom-physics
  - sim-boundary
  - lambert-universal-variables
  - velocity-to-be-gained
  - timed-cutoff
  - flown-prediction
  - harmony-prefix
  - headless-flight-rig
hooks: [starmap StarMapAfterGui (via StepOnce), harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:draw-ui, frame:dispatch-solvers]
game_types:
  - KSA.Vehicle
  - KSA.Celestial
  - KSA.IParentBody
  - KSA.CelestialFrameMath
  - KSA.FlightComputer
  - KSA.ActiveEnginePerformance
  - KSA.StructuralLoad
tags: [icbm, guidance, lambert, autopilot, trajectory, impact-prediction, closed-loop]
---

KSArmory's largest subsystem flies whatever rocket the player built, from the pad to a place they
picked on the map, and lets the warheads go over it. Nothing about the vehicle is declared: each
cycle it reads thrust, mass flow and propellant from the game and stages when there is nothing
left to burn.[^icbm-doc] All decision-making lives in game-free `Sim/` classes flown headlessly by a
test rig; `Ksa/IcbmComputer` contains only two conversions: the world into an `IcbmState`, and the
answer into commands on the vehicle.[^icbm-computer][^flight-rig]

## How it works

### One computer per craft

`IcbmComputers` crews one `IcbmComputer` per craft carrying a part whose profile provides
`WeaponRole.Guidance` (today only the MIRV bus): per craft rather than per launcher, because a craft
has one trajectory and a second computer would be a second autopilot fighting the first.[^icbm-computers]

### The state, in the body's inertial frame

`IcbmComputer.Sample` converts the craft into `Cci` (parent-centred inertial), not the ecliptic:
`(PositionEcl(craft) - Parent.GetPositionEcl()).Transform(Parent.GetCce2Cci())`, and the same for
velocity. Over a half-hour flight the ecliptic carries about 54,000 km (29.8 km/s over 1,800 s; the mod source says "54 million kilometres", which is a factor of 1000 too large) of the planet's travel
through every term; `Cci` removes it exactly.[^icbm-computer] The spin axis is exactly `+Z` in
`Cci` because KSA builds `Ccf` from `Cci` by a rotation about `UnitZ` only
(`CelestialFrameMath.ComputeCcf2Cci`), so the planet's rotation is one angle.[^ksa-framemath][^ksa-celestial]
Engine figures come from `FlightComputer.ActiveEnginePerformanceMax` (`Thrust`, `MassFlowRate`,
`MinThrottle`), `Vehicle.TotalMass`, `PropellantMass`, `IsAnyEnginePropellantAvailable()` and the
airframe's `StructuralLoad.MaxGLoad`.[^icbm-computer][^ksa-flightcomputer][^ksa-vehicle]

### The algorithm

| Piece | Code | Idea |
|---|---|---|
| Target | `AimSite` | Latitude and longitude on a named body, never a position; re-read from the world every cycle and survives a save[^aim-site] |
| Transfer | `Lambert.TrySolve(from, to, flightSeconds, mu)` | Lambert's problem in universal variables, bisection on `psi` within `+-4 pi^2` (48 iterations): one routine covers a depressed hyperbolic shot and a lofted ellipse, and bisection cannot run away near the parabolic point as Newton does[^lambert] |
| Arc | `BallisticArc.TrySolve` / `TryCheapest` | Parameterised by **flight time**: the aim point is carried forward by the planet's spin over that time (`body.CarryCci`) before solving, collapsing a fixed point into one solve; `TryCheapest` minimises the velocity still to gain (coarse scan, then golden section, seeded with the last cycle's time)[^ballistic-arc] |
| Loft and arrival | `Loft`, `MinArrivalAngleDeg` | Loft multiplies the *cheapest* time, carried separately so it does not compound each cycle; the arrival time is latched when closed-loop guidance takes over; a minimum arrival angle bounds the search instead of nudging it[^icbm-doc][^ballistic-arc] |
| Ascent | `AscentProfile` | Straight up, then a pitch programme; closed loop takes over by dynamic pressure, so an airless launch hands over at once[^ascent-profile] |
| Steering | `BurnoutGuidance.TrySteer` | Velocity-to-be-gained: thrust along (required velocity from where the vehicle will be at cutoff) minus (what it will have). Re-solved against the actual state, so errors on the way cost propellant, not accuracy[^burnout-guidance] |
| Cutoff | `IcbmProgram.ShouldCutOff` | Engines stop on frame boundaries, so a threshold on remaining velocity hunts. A countdown is run down each step by the *achieved* throttle and the engines stop when less than half a frame of burning is left; it re-solves every step inside the last 0.75 s and throttles back for the last 2 s[^icbm-program] |
| Coasting | `Kepler` | Closed-form universal-variable propagation for "where will it be at time t", used for burn windows[^kepler] |
| Check | `ImpactPredictor.TryPredict` | Flies the warhead's arc (with drag) against the real height field rather than evaluating the conic, so it can disagree with the solver; `AimCorrection` moves the aim by what the flown arc loses (a point in vacuum vs the ground it hits)[^impact-predictor][^aim-correction] |

### Game-facing pieces

- **Commands** go through `VehicleCommand` (flight computer `Custom` attitude, keyboard actions for
  engines and throttle, `ActivateNextSequence` for staging), with attitude, pulse mode and staging
  queued for the `Vehicle.PrepareWorker` prefix; see the fly-another-vehicle feature.[^icbm-doc]
- **Designation** is an armed tool (`SiteDesignator`), not a button, because a button samples the
  cursor while it is over the panel; a ground ring follows the cursor while armed.[^site-designator]
- **Display**: `IcbmOverlay` draws the flown prediction as world lines and the aim point as a ring
  with time to impact; `UiIcbm` is the operator tab.[^icbm-overlay][^ui-icbm]
- **Measurement**: `WarheadTrace` (off by default) records one warhead per frame beside the
  predictor's re-flight to separate integration drift from discrete events.[^warhead-trace]

## Lifecycle and timing

- **`frame:draw-ui`.** `_icbms.Update(dtSim, dtPlayer, ...)` runs inside `StepSimulation`
  (KSArmoryMod.cs L806), from `[StarMapAfterGui]` via `StepOnce`, on the **simulated** step and
  unclamped: the cutoff is timed against whatever step it is handed, so a long step costs accuracy
  honestly.[^entry] The state it samples is the step KSA applied in this frame's `PrepareFrame`.
- **`frame:dispatch-solvers` (next frame).** Throttle, engine and translation keys reach the
  worker at the next `PrepareWorker`; attitude and staging are written in the `PrepareWorker`
  prefix. So every command acts one frame later, the same latency as a player's key press, which
  is why the cutoff is timed rather than observed.[^icbm-doc]
- The computer keeps running after the bus dies, counting down to the warheads' arrival.[^icbm-computer]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/IcbmComputer.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/IcbmComputer.cs#L3453-L3518) | `Sample`: world to `IcbmState` in `Cci` |
| [src/KSArmory/Ksa/IcbmComputer.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/IcbmComputer.cs#L729-L1130) | `Update`: per-frame driver |
| [src/KSArmory/Sim/IcbmProgram.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/IcbmProgram.cs) | Phases, solve cadence, timed cutoff |
| [src/KSArmory/Sim/Lambert.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Lambert.cs) | Universal-variable Lambert solver |
| [src/KSArmory/Sim/BallisticArc.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BallisticArc.cs#L62-L130) | Flight-time parameterised arc and cheapest search |
| [src/KSArmory/Sim/BurnoutGuidance.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BurnoutGuidance.cs) | Velocity-to-be-gained steering |
| [src/KSArmory/Sim/ImpactPredictor.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ImpactPredictor.cs) | Flown prediction against terrain |
| [tests/KSArmory.Tests/IcbmFlightRig.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/IcbmFlightRig.cs) | Headless rig: unmodelled drag, lagging attitude, staging |

## Snippets

```csharp
// src/KSArmory/Ksa/IcbmComputer.cs L3457-L3471
        Parent = KsaWorld.ParentBody(Craft);
        if (Parent is null) return default;

        double mu = Parent.Mass * GravitationalConstant;

        // The spin axis is exactly +Z in a body's own Cci: KSA builds Ccf from Cci by rotating
        // about UnitZ and nothing else, so there is no obliquity term to carry here. It is the
        // *ecliptic* that sees the tilt.
        Body = new BallisticBody(mu, Parent.MeanRadius, new double3(0, 0, 1), Parent.GetAngularVelocity());

        doubleQuat cce2Cci = Parent.GetCce2Cci();
        double3 positionCci = (KsaWorld.PositionEcl(Craft) - Parent.GetPositionEcl()).Transform(cce2Cci);
        double3 velocityCci = (KsaWorld.VelocityEcl(Craft) - Parent.GetVelocityEcl()).Transform(cce2Cci);
```

```csharp
// src/KSArmory/Sim/IcbmProgram.cs L1581-L1593
    private bool ShouldCutOff(in IcbmState state)
    {
        if (_toGain <= BurnoutGuidance.CutoffMetresPerSecond) return true;

        // Against the throttle the vehicle actually has, never the one that was asked for. A stack
        // whose motors do not throttle, or one still ramping down, would otherwise be cut off on a
        // prediction of how much velocity the last frame adds that is several times too small.
        double achieved = Math.Clamp(state.ThrottleAchieved, 0.0, 1.0);
        if (_countdown <= 0.5 * _lastStep * Math.Max(achieved, 1e-3)) return true;

        double oneStep = state.Booster.AccelerationNow * _lastStep;
        return _lowestToGain < BackstopArmsBelow(state) && _toGain > _lowestToGain + Math.Max(oneStep, 1.0);
    }
```

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

## Game APIs touched

| Member | Use |
|---|---|
| `Celestial.GetCce2Cci()` ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L526)), `GetPositionEcl()`, `GetVelocityEcl()`, `GetAngularVelocity()`, `Mass`, `MeanRadius` | The inertial frame and body constants |
| `CelestialFrameMath.ComputeCcf2Cci` ([current/decomp/KSA/CelestialFrameMath.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialFrameMath.cs#L7)) | Proof that spin is about `+Z` only |
| `FlightComputer.ActiveEnginePerformanceMax` ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L95)) | Thrust, mass flow, minimum throttle |
| `Vehicle.TotalMass`, `PropellantMass` (L571), `IsAnyEnginePropellantAvailable()` (L6685), `StructuralLoad.MaxGLoad` (L547) ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L547)) | Booster state and airframe limit |

## Pitfalls and version notes

- **Work in the parent's `Cci`, not `Ecl`**, for anything lasting minutes.[^icbm-computer]
- **Carry the aim point twice**: by the flight time (planet spin) inside the solver, and by the
  burn time still left; passing an already-carried point applies it twice.[^icbm-doc]
- **Never threshold a cutoff on remaining velocity** at high acceleration; time it to the frame
  boundary using the throttle the vehicle reports.[^icbm-program]
- **A loft multiplier fed back into the next search compounds**: carry the cheapest time
  separately and latch the arrival time when the loop takes over.[^icbm-doc]
- **Predict with different maths than you steer with**, or the prediction can only agree.[^impact-predictor]
- The flight computer's double buffer, the 30x held-key clear and the 0.9 g-load throttle cap are
  documented in the fly-another-vehicle feature.[^icbm-doc]

## See also

- [Fly another vehicle](./fly-another-vehicle.md)
- [PrepareWorker prefix command window](./prepareworker-prefix-command-window.md)
- [MIRV bus trim and release](./mirv-bus-trim-and-release.md)
- [Launcher separation at decoupler](./launcher-separation-at-decoupler.md)
- [Timewarp policy during engagements](./timewarp-policy-during-engagements.md)
- [Depth-painted ground rings](./depth-painted-ground-rings.md)
- [Terrain ground-impact test](./terrain-ground-impact-test.md)
- [Shot-night statistics](./shot-night-statistics.md)
- [Point gravity and atmosphere model](./point-gravity-and-atmosphere-model.md)
- [Build a ballistic computer with a closed-form aim and a flown impact check](../../../howto/flight-and-orbits/guidance/predict-impact-point-and-build-a-ballistic-computer/predict-impact-point-and-build-a-ballistic-computer-3.md) - how-to article demonstrating this feature
- [Solve your own universal-variable Lambert against a spinning-planet target](../../../howto/flight-and-orbits/transfers/aim-a-transfer-at-a-flyby-periapsis/aim-a-transfer-at-a-flyby-periapsis-3.md) - how-to article demonstrating this feature

[^icbm-computer]: src/KSArmory/Ksa/IcbmComputer.cs
[^icbm-computers]: src/KSArmory/Ksa/IcbmComputers.cs
[^icbm-program]: src/KSArmory/Sim/IcbmProgram.cs
[^lambert]: src/KSArmory/Sim/Lambert.cs
[^ballistic-arc]: src/KSArmory/Sim/BallisticArc.cs
[^burnout-guidance]: src/KSArmory/Sim/BurnoutGuidance.cs
[^impact-predictor]: src/KSArmory/Sim/ImpactPredictor.cs
[^aim-correction]: src/KSArmory/Sim/AimCorrection.cs
[^aim-site]: src/KSArmory/Sim/AimSite.cs
[^ascent-profile]: src/KSArmory/Sim/AscentProfile.cs
[^kepler]: src/KSArmory/Sim/Kepler.cs
[^site-designator]: src/KSArmory/Ksa/SiteDesignator.cs
[^icbm-overlay]: src/KSArmory/Ksa/IcbmOverlay.cs
[^ui-icbm]: src/KSArmory/Ksa/Ui/UiIcbm.cs
[^warhead-trace]: src/KSArmory/Ksa/WarheadTrace.cs
[^entry]: src/KSArmory/Ksa/KSArmoryMod.cs
[^icbm-doc]: docs/ICBM-GUIDANCE.md
[^flight-rig]: tests/KSArmory.Tests/IcbmFlightRig.cs
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-framemath]: KSA.CelestialFrameMath (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
