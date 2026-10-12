---
type: How-To
title: Build a ballistic computer with a closed-form aim and a flown impact check
description: Solve the aim with Lambert in the parent's inertial frame, steer by velocity to be gained, time the cutoff against the achieved throttle, and check the answer by flying it against the real terrain with drag
category: flight-and-orbits
group: guidance
topic: predict-impact-point-and-build-a-ballistic-computer
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/icbm-ballistic-computer]
techniques: [flown-prediction, flight-math, velocity-to-be-gained]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:draw-ui, frame:dispatch-solvers]
game_types:
  - KSA.Vehicle
  - KSA.Celestial
  - KSA.IParentBody
  - KSA.CelestialFrameMath
  - KSA.FlightComputer
  - KSA.ActiveEnginePerformance
  - KSA.StructuralLoad
tags: [ballistic-computer, lambert, velocity-to-be-gained, cutoff, impact-prediction, cci, rotating-body]
---
Outcome: a computer that reads whatever rocket the player built, solves the ballistic arc to a latitude and longitude on a body, steers the burn so the vehicle reaches the right velocity at cutoff, and checks the result by flying the arc with drag. KSArmory's ICBM computer is the demonstration.[ICBM ballistic computer](../../../../mods/ksarmory/features/icbm-ballistic-computer.md) Nothing about the vehicle is declared: each cycle it reads thrust, mass flow and propellant from the game.

Prerequisites: StarMap. The maths (`Lambert`, `BallisticArc`, `BurnoutGuidance`, `ImpactPredictor`) lives in game-free classes you can copy; only `IcbmComputer.Sample` touches KSA.

## Background: how the game does it

Do the maths in the parent's inertial frame (CCI), not the ecliptic. Over a half-hour flight the ecliptic carries about 54,000 km (29.8 km/s over 1,800 s; the mod source says "54 million kilometres", which is a factor of 1000 too large) of the planet's own travel through every term; CCI removes it exactly.

| Input | Game member |
|---|---|
| Position and velocity in CCI | `(craft Ecl - parent.GetPositionEcl()).Transform(parent.GetCce2Cci())`[^ksa-celestial] |
| Spin | Exactly CCI +Z: `CelestialFrameMath.ComputeCcf2Cci` rotates about `UnitZ` only, so the obliquity lives in the ecliptic, not in CCI[^ksa-celestialframemath] |
| Gravitational parameter | `IParentBody.Mu`, `MeanRadius`, `GetAngularVelocity()`[^ksa-iparentbody] |
| Engine capability | `FlightComputer.ActiveEnginePerformanceMax` (`Thrust`, `MassFlowRate`, `MinThrottle`)[^ksa-flightcomputer] [^ksa-activeengineperformance] |
| Mass, propellant, supply | `Vehicle.TotalMass`, `PropellantMass`, `IsAnyEnginePropellantAvailable()`[^ksa-vehicle] |
| Airframe limit | `Vehicle.StructuralLoad.MaxGLoad`[^ksa-structuralload] |

## Lifecycle and timing

- **`frame:draw-ui`.** The computer's `Update` runs inside the mod's own step from `[StarMapAfterGui]` on the simulated step, unclamped, so the cutoff is timed against whatever step it is handed. The state it samples is the step KSA applied in this frame's `PrepareFrame`.
- **`frame:dispatch-solvers` (next frame).** Throttle, engine and translation keys reach the worker at the next `PrepareWorker`; attitude and staging are written in the `Vehicle.PrepareWorker` prefix. Every command acts one frame later, the same latency as a key press, which is why the cutoff is timed rather than observed. See [Execute burns with RCS and throttle inputs](../../autopilot/execute-burns-with-rcs-and-throttle-inputs/overview.md) and the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Sample the craft into CCI.** The spin axis is a constant `(0, 0, 1)` and the spin rate a scalar:

```csharp
// src/KSArmory/Ksa/IcbmComputer.cs L3457-L3469
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

2. **Read the engines from the flight computer.** One `BoosterPerformance` per cycle from the measured capability, so staging and engine failure are picked up without configuration:

```csharp
// src/KSArmory/Ksa/IcbmComputer.cs L3488-L3492
        FlightComputer computer = Craft.FlightComputer;
        ActiveEnginePerformance engines = computer.ActiveEnginePerformanceMax;

        BoosterPerformance booster = new(engines.Thrust, engines.MassFlowRate,
                                         Craft.TotalMass, Craft.PropellantMass);
```

3. **Solve the transfer with Lambert, parameterised by flight time.** The aim point is carried forward by the planet's spin over that time (`CarryCci`) before solving; a bisection on `psi` covers both a depressed hyperbolic shot and a lofted ellipse:

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

4. **Steer by velocity to be gained.** Thrust along (required velocity at cutoff) minus (velocity the vehicle will have at cutoff unpowered), re-solved against the actual state each pass, so errors on the way cost propellant, not accuracy. The aim is carried to the cutoff instant, not "now":

```csharp
// src/KSArmory/Sim/BurnoutGuidance.cs L124-L127
            cutoffPosition = positionCci
                           + velocityCci * timeToCutoff
                           + gravity * (0.5 * timeToCutoff * timeToCutoff)
                           + thrustDir * booster.ThrustDisplacement(timeToCutoff);
```

```csharp
// src/KSArmory/Sim/BurnoutGuidance.cs L142-L149
            double3 velocityAtCutoffUnpowered = velocityCci + gravity * timeToCutoff;

            // The arc departs at cutoff, not now, so the target has to be carried to the cutoff
            // instant before it is handed over. Without this the whole solve is out by the planet's
            // turn during the remaining burn - forty-odd kilometres a hundred seconds before
            // cutoff. The loop converges anyway, because the term goes to zero as the burn ends,
            // which is exactly why it is easy to leave in and never see.
            double3 aimAtCutoff = body.CarryCci(aimNowCci, timeToCutoff);
```

```csharp
// src/KSArmory/Sim/BurnoutGuidance.cs L181-L187
            flightSeed = arc.CheapestFlightSeconds;

            double3 toGainVector = arc.RequiredVelocityCci - velocityAtCutoffUnpowered;
            toGain = Vec.Len(toGainVector);
            toGainOut = toGainVector;

            double3 next = Vec.Unit(toGainVector);
```

5. **Solve a few times a second, step the countdown every frame.** A countdown is run down by the achieved throttle between solves, and the solve runs every step inside the last 0.75 s:

```csharp
// src/KSArmory/Sim/IcbmProgram.cs L1117-L1126
    private void Resolve(in IcbmState state)
    {
        bool burning = IsBurning;
        if (burning && _lastStep > 0.0) _countdown -= _lastStep * Math.Clamp(state.ThrottleAchieved, 0.0, 1.0);

        bool due = _sinceSolve >= SolveIntervalSeconds
                || _countdown <= SolveEveryStepWithin
                || Arc is null;

        if (!due) return;
```

6. **Time the cutoff; never threshold it.** Engines stop on frame boundaries, so a threshold on remaining velocity hunts. Cut when less than half a frame of burning is left, against the throttle the vehicle reports:

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

7. **Fly the answer as a check.** The closed form puts the arc through a point in vacuum, and a round stops at the ground. `ImpactPredictor` steps the arc with drag against the real height field; the miss is the difference from the target carried to the same instant, projected onto the ground:

```csharp
// src/KSArmory/Sim/IcbmProgram.cs L1043-L1066
    // The arc flown with the warhead's drag from the cutoff it was solved from, against the target at
    // the same instant the solve carried it to: a target at any other epoch is wrong by the ground's
    // turn over the difference, 465 m/s on the equator.
    private static bool TryDragMiss(in IcbmState state, in BurnoutGuidance.Command command, out double3 missCci)
    {
        missCci = default;
        if (!double.IsFinite(command.CarrySeconds)) return false;

        // With the tube's throw, as the warhead leaves: 0.5 m/s along the line is 100-190 m at these ranges,
        // and flown without it every low arc landed that much long.
        if (!ImpactPredictor.TryPredict(state.Body, command.CutoffPositionCci + state.ReleaseOffsetCci,
                                        command.Arc.RequiredVelocityCci + state.ReleaseImpulseCci,
                                        DragStepSeconds, ImpactPredictor.DefaultMaxSeconds, out ImpactPredictor.Impact hit,
                                        drag: new ImpactPredictor.Drag(state.DensityRatioAt!, state.Warhead!)))
        {
            return false;
        }

        double3 target = state.Body.CarryCci(state.AimNowCci, command.CarrySeconds);
        double3 miss = hit.GroundFixedPointCci - target;
        double3 up = Vec.Unit(target);
        missCci = miss - up * Vec.Dot(miss, up);
        return Vec.IsFinite(missCci);
    }
```

8. **Move the aim by what the flown arc loses.** `AimCorrection` takes a fraction of the error each cycle (first gain 0.25, then a measured response clamped to at least one), because it is a feedback loop against a solver that then moves the arc: taking all of it oscillates.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/IcbmComputer.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/IcbmComputer.cs) | `Sample`: world to `IcbmState` in CCI; per-frame `Update` |
| [src/KSArmory/Sim/IcbmProgram.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/IcbmProgram.cs) | phases, solve cadence, timed cutoff, drag-flown miss |
| [src/KSArmory/Sim/Lambert.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Lambert.cs) | universal-variable Lambert solver |
| [src/KSArmory/Sim/BallisticArc.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BallisticArc.cs) | flight-time parameterised arc, cheapest search |
| [src/KSArmory/Sim/BurnoutGuidance.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BurnoutGuidance.cs) | velocity-to-be-gained steering |
| [src/KSArmory/Sim/ImpactPredictor.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ImpactPredictor.cs) | flown prediction against terrain, with drag |
| [src/KSArmory/Sim/AimCorrection.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/AimCorrection.cs) | aim moved by the flown arc's loss |

## Variations and gotchas

- **Work in CCI, not Ecl,** for anything lasting minutes.
- **Carry the aim point twice, once each:** by the flight time (spin) inside the solver, and by the burn time still left; passing an already-carried point applies the rotation twice.
- **Never threshold a cutoff on remaining velocity** at high acceleration.
- **A loft multiplier fed back into the next search compounds:** carry the cheapest time separately and latch the arrival time when closed-loop guidance takes over.
- **Predict with different maths than you steer with,** or the prediction can only agree with itself. A closed orbit whose lowest point clears the mean sphere by 12 km is never flown (`NeverComesDownMargin`), because flying a stable orbit costs the whole horizon.
- **The crossing is biased deep.** The search accepts the first sample below the ground, so it always reports a point past the crossing; `stopOnTheSurface` places the answer on the ground instead (`CrossingToleranceMetres` is 0.25).
- **Drag matters for warheads and not for the bus:** without a `Drag` argument the prediction is a vacuum arc, and on a shallow arrival the difference is tens of kilometres.

## See also

- [Overview](overview.md); [Gravity plus drag with a measured table](predict-impact-point-and-build-a-ballistic-computer-1.md); [Throwaway copy in a ground-carried frame](predict-impact-point-and-build-a-ballistic-computer-2.md)
- [ICBM ballistic computer (mod feature)](../../../../mods/ksarmory/features/icbm-ballistic-computer.md)
- [Aim a transfer at a flyby periapsis](../../transfers/aim-a-transfer-at-a-flyby-periapsis/overview.md)
- [Read staged delta-v from the game](../../../vehicles-and-parts/staging-and-engines/read-staged-delta-v-from-the-game/overview.md)

[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-celestialframemath]: KSA.CelestialFrameMath (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-activeengineperformance]: KSA.ActiveEnginePerformance (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-structuralload]: KSA.StructuralLoad (decompiled, build 2026.10.10.5554)
