---
type: How-To
title: Adopt a separated booster and fly a boostback burn from impact predictions
description: Record what a separation will throw overboard, adopt the new vehicle as the booster, fly a settle, turn and receding-horizon boostback burn that moves the predicted impact onto the site, and hand over to the glide
category: flight-and-orbits
group: guidance
topic: fly-a-boostback-and-glide-to-a-landing-site
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/boostback-impact-prediction]
techniques: [harmony-prefix, custom-physics, closed-loop-guidance]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker, harmony-postfix KSA.FlightComputer.UpdateAttitudeTarget]
lifecycle: [frame:dispatch-solvers, physics:vehicle-solver]
game_types:
  - KSA.Vehicle
  - KSA.SequenceList
  - KSA.Decoupler
  - KSA.PartTree
  - KSA.FlightComputer
  - KSA.IParentBody
  - KSA.Orbit
tags: [boostback, booster-recovery, hand-over, stage-separation, receding-horizon, impact-point]
---
Outcome: when a stage with its own control module separates, your mod takes over the new vehicle and flies it back toward a landing site: a settling burn, a flip, the boostback burn with the engine lit only as long as the plan says, then the glide ([approach 2](fly-a-boostback-and-glide-to-a-landing-site-2.md)). The player keeps flying the upper stage. KSA-AdvancedFlightComputer is the demonstration.[Boostback impact prediction](../../../../mods/ksa-advancedflightcomputer/features/boostback-impact-prediction.md)

Prerequisites: StarMap, Harmony, and the impact predictor from [Predict impact points with drag](../predict-impact-point-and-build-a-ballistic-computer/predict-impact-point-and-build-a-ballistic-computer-1.md): the burn is planned by integrating the powered arc and the coast through the measured drag table.

## Background: how the game does it

- The game stages through `SequenceList`; a staged `Decoupler` releases a connection and `Vehicle.Split` turns the tree-child side into a new `Vehicle` with a new id, so a mod cannot keep a handle across the split.[^ksa-vehicle] [^ksa-sequencelist]
- A returnable stage is one whose detached subtree contains a `Control` module; AFC applies the same test stock uses for `Vehicle.IsControllable`.[^ksa-part]
- Engines and attitude are commanded through the stock flight computer, which tracks both an attitude target and its rate ([Drive the flight computer](../../autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/overview.md)).[^ksa-flightcomputer]

## Lifecycle and timing

- **`frame:dispatch-solvers`, main thread.** The whole machine runs in a prefix on `Vehicle.PrepareWorker`, once per vehicle per step, right before the vehicle's physics snapshot: the last settled point before the next step. The prediction and the plan solve are synchronous numerical work on the main thread (2.3 ms for a warm plan solve in a release build, per the author), bounded by step budgets and the re-solve cadence.
- **Adoption happens on the split frame.** That is the frame the part tree is least settled, so the booster is adopted but the burn is engaged on a retry (every 0.5 s, for up to 30 s) until the aero sweep can fit a bounding box.
- **Worker-thread piece.** The `UpdateAttitudeTarget` postfix is `physics:vehicle-solver`: it must not touch ImGui or allocate.
- **Why it is safe.** The prefix reads the state the previous `ApplyVehicleSolvers` committed and writes through the flight computer's command path, which the worker copies at the next snapshot. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Patch `PrepareWorker`.** One prefix covers every vehicle, focused or not:

```csharp
// AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs L26-L27
        harmony.Patch(GameReflection.Vehicle_PrepareWorker!,
            prefix: new HarmonyMethod(typeof(GuidanceFeature), nameof(OnPrepareWorker)));
```

```csharp
// AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs L62-L62
    private static void OnPrepareWorker(Vehicle __instance) => StepVehicle(__instance);
```

   A postfix on the private `FlightComputer.UpdateAttitudeTarget` runs on the vehicle worker after stock builds the attitude target and before it reads the target rate; it writes the slew's rate as feedforward so the flight computer tracks a moving target instead of a stationary one (`KsaAttitudeRate.OnUpdateAttitudeTarget`). It is the only code here that runs off the main thread.

2. **Know which stages can return.** A stage can return only when its separating subtree holds a command pod; one prediction and one velocity Jacobian serve every candidate because only the miss depends on the target:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/ReturnableStages.cs L121-L131
            // THE TEST.
            // A command pod on the side that leaves is what makes the thing that leaves a vehicle rather than debris.
            bool controllable = false;
            for (int c = 0; c < controls.Length && !controllable; c++)
                controllable = _returnDrops.Contains(controls[c].Parent.FullPart);
            if (!controllable)
            {
                // Still a separation, so the NEXT flag has to move past it: a dumb interstage coming off first does not make the booster behind it next.
                first = false;
                continue;
            }
```

3. **Record the separation before it happens.** Just before a staging request that will drop a returnable stage, remember the detached roots' `Part.InstanceId`, the landing target and an expiry (30 s). The detached root is the connector's part that is not a tree child of the other:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs L420-L429
    private static Part DetachedRoot(Decoupler decoupler)
    {
        Part.Connection conn = decoupler.Connector?.Connection;
        if (conn == null)
            return null;
        Part a = conn.Connectors[0].ConnectionPart;
        Part b = conn.Connectors[1].ConnectionPart;
        if (a == null || b == null)
            return null;
        return !a.TreeChildren.Contains(b) ? a : b;
```

4. **Adopt the new vehicle.** The per-vehicle step for an unknown, unfocused vehicle matches its parts against the recorded roots and gives it state, target and a pending boostback:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs L925-L933

        // Use creates state, so reserve it for the focused vehicle and reuse existing state elsewhere.
        bool focused = ReferenceEquals(vehicle, Program.ControlledVehicle);
        if (focused)
            Use(vehicle);
        else if (VehicleAutopilotState.TryGet(vehicle, out VehicleAutopilotState state))
            _s = state;
        else if (!TryAdoptBooster(vehicle))
            return;
```

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs L346-L375
        PartTree tree = vehicle?.Parts;
        if (tree == null)
            return false;

        BoosterHandover mine = null;
        ReadOnlySpan<Part> parts = tree.Parts;
        for (int i = 0; i < _handovers.Count && mine == null; i++)
        {
            for (int j = 0; j < parts.Length; j++)
            {
                if (parts[j].InstanceId != _handovers[i].RootId)
                    continue;
                mine = _handovers[i];
                break;
            }
        }
        if (mine == null)
            return false;

        // Consume only this match so other boosters can still claim their records.
        _handovers.Remove(mine);

        _s = VehicleAutopilotState.For(vehicle);
        _s.SiteLatDeg = mine.SiteLatDeg;
        _s.SiteLonDeg = mine.SiteLonDeg;
        GuidanceLog.Info(vehicle, $"adopted as a separated booster, boostback engages within {HandoverWindowS:F0} s.");

        // NOT engaged here, deliberately. This runs on the frame the split happened, which is the frame the part tree is least settled - and ExecuteBoostback needs an aero surrogate fitted to a bounding box that may not exist yet. It refuses rather than throwing, and refusing here would leave a booster that had been adopted and would never be flown, with the hand-over record already consumed. So the engage is retried from the sweep until it takes.
        _s.HandoverPendingUntil = now + HandoverWindowS;
        return true;
```

5. **Fly the phases.** `BoostbackPhase` is `Idle, Separation, Rotation, Boostback, Glide, Done`. Separation holds attitude at the vehicle's lowest throttle for 2 s to settle propellant; Rotation slews the thrust axis (30 deg/s default) toward the plan's burn direction with the engine lit at minimum throttle because gimbals, not RCS, turn a booster; Boostback burns at full throttle:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/Boostback.cs L742-L772
    private static void ApplyBoostbackActuation(Vehicle vehicle, double now)
    {
        switch (_s.BoostbackPhase)
        {
            case BoostbackPhase.Separation:
                _s.BoostbackThrottle = SettleThrottle(vehicle);
                _s.BoostbackEngineOn = true;
                break;

            case BoostbackPhase.Rotation:
                // LIT THROUGH THE TURN, AND THAT IS THE POINT.
                // A booster's RCS is sized for attitude hold, not for throwing a half-empty first stage through 180 degrees; the gimbals are what turn it, and a gimbal produces torque in proportion to the thrust it is deflecting.
                // Shut the engine down for the flip and the only authority left is the one that cannot do it.
                //
                // At the same minimum throttle the settling burn uses, which is the trade: gimbal torque scales with it, and so does the dV the sweeping thrust axis lays down.
                // That dV is NOT a targeting error - the correction is re-derived from the live state at 10 Hz and the burn does not start until the turn is finished, so what the flip does to the trajectory is simply part of the state the first solve sees.
                // It costs propellant, not accuracy.
                _s.BoostbackThrottle = SettleThrottle(vehicle);
                _s.BoostbackEngineOn = true;
                break;

            case BoostbackPhase.Boostback:
                _s.BoostbackThrottle = 1.0;
                _s.BoostbackEngineOn = true;
                break;

            default:
                _s.BoostbackThrottle = 0.0;
                _s.BoostbackEngineOn = false;
                break;
        }
```

6. **Light only when both the command and the vehicle are aligned,** then take a fresh plan at ignition and size a backstop off it:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/Boostback.cs L421-L440
                // BOTH the command and the vehicle.
                // The command reaching the target only says the slew has finished issuing; the flight computer is still some way behind it, and lighting the engine there points the thrust at the last of the turn instead of at the target.
                double cmdErr = AngleBetween(_s.CommandDir, planDir) * 180.0 / Math.PI;
                double fcErr = vehicle.FlightComputer.ErrorAngles.Length() * 180.0 / Math.PI;
                if (cmdErr <= BoostbackAlignDeg && fcErr <= BoostbackAlignDeg)
                {
                    EnterBoostbackPhase(BoostbackPhase.Boostback, now);

                    // A FRESH PLAN AT THE INSTANT THE ENGINE LIGHTS, off the interval.
                    // The plan in hand is up to two seconds old and its duration is measured from when it was solved, so flying it from here would cut the burn short by however long it has been sitting.
                    // Ignition is precisely the moment worth paying an off-cadence solve for.
                    UpdateBoostbackPlan(vehicle, orbit, parent, now, intervalS: 0.0);
                    planTgo = BoostbackPlanTgo(now);
                    planDir = BoostbackPlanDirection(now);

                    // The backstop, sized off the burn the PLAN says we are about to fly - which is the burn we are about to fly.
                    _s.BoostbackBurnLimit = now + (double.IsFinite(planTgo)
                        ? Math.Max(planTgo * BoostbackBurnLimitFactor, 30.0)
                        : 120.0);
                }
```

7. **Fly the plan, not the impulse.** `ImpactSteering.Correction` gives the smallest impulse now that puts the impact on the site; it is used only to decide whether a burn is worth lighting (a correction under 5 m/s skips the burn) and whether it has finished. What flies is `BoostbackShooter`, which integrates the powered arc and optimises the whole burn; on the author's reference arc the instantaneous correction points 33 degrees below the horizon while the optimised burn points 25 degrees above it for 18 percent less propellant. The plan is re-solved every 2 s from the live state (receding horizon), every 0.2 s inside the last 5 s, and not at all inside the last 1 s, where its steering law is simply evaluated to cutoff.
8. **Cut off on the plan's clock** (or on propellant, an overrun backstop of three times the plan, or the impact being nulled) and enter the glide:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/Boostback.cs L444-L470
            case BoostbackPhase.Boostback:
            {
                // The plan runs out.
                // This is the cutoff, and it is a CLOCK rather than a threshold on a shrinking vector: the shot answered "burn for this long" and the burn is over when it has.
                bool done = _s.BoostbackHasPlan && planTgo <= 0.0;
                bool overrun = now >= _s.BoostbackBurnLimit;

                // Propellant, but not for the first second.
                // The engine has only just been commanded on at this point and the master switch does not reach it until ApplyAutopilot runs at the foot of this same step, so the very frames where the burn begins are also the frames most likely to report nothing lit and nothing fed - and cutting on that would end the burn before it started.
                bool dry = now - _s.BoostbackPhaseStart > 1.0
                        && !vehicle.IsAnyEnginePropellantAvailable();

                // The closed-loop cutoff: the ballistic impact is already ON the site, so continuing would take it back off.
                // Targeting only, not the commanded total - shaping would hold this number up after the miss was nulled and the burn would never end on its own.
                // Disabled once locked, where the whole point is that nothing new is listened to.
                bool nulled = !_s.BoostbackLocked && _s.HasSteer
                           && missDv < BoostbackMinDvMs;

                if (done || nulled || dry || overrun)
                {
                    _s.BoostbackStatus = dry ? "Cutoff - propellant exhausted."
                        : overrun ? "Cutoff - burn exceeded its time limit; check the solution."
                        : $"Cutoff - miss {_s.SteerMissM / 1000.0:F1} km.";
                    EnterBoostbackPhase(BoostbackPhase.Glide, now);
                }
                break;
            }
```

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs) | the `PrepareWorker` and `UpdateAttitudeTarget` patches |
| [AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs) | hand-over record, adoption, retry |
| [AdvancedFlightComputer/Features/Guidance/Modes/Boostback.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Boostback.cs) | phases, plan cadence, cutoff, actuation |
| [AdvancedFlightComputer/Features/Guidance/Modes/ReturnableStages.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/ReturnableStages.cs) | which stages can return, shared Jacobian |
| [AdvancedFlightComputer/Features/Guidance/Numerics/Flight/ImpactSteering.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Numerics/Flight/ImpactSteering.cs) | minimum-norm impact correction |
| [AdvancedFlightComputer/Features/Guidance/Numerics/Flight/PoweredBurn.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Numerics/Flight/PoweredBurn.cs) | powered-arc shooter |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Guidance/GuidanceBoosterHandoverTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Guidance/GuidanceBoosterHandoverTest.cs) | headless hand-over test |

## Variations and gotchas

- **Instantaneous impact-point guidance is a poor law to fly** for a burn that lasts tens of seconds; use it to decide, and optimise the burn as a whole.
- **Hand-over records must outlive the split frame and expire.** Match on a recorded root's `InstanceId`, consume only the matched record so several boosters can claim theirs, and arm the nested separations by distinct roots.
- **Do not engage on the adoption frame.** The aero sweep needs a bounding box of the new vehicle; engaging too early would fit the stack that included the upper stage and give several times the real drag area.
- **Light the engine through the turn.** Shutting it down for the flip leaves only RCS authority, which cannot turn a half-empty stage.
- **Model mismatch, not noise, is why the last second stops listening.** The shot integrates a surrogate aero table, an exponential atmosphere and an engine table; re-solving into the last second chases a bias the solver cannot null.
- **Only single-craft ascent has been flown in the game by the author;** boostback is exercised by harness tests and console checks.

## See also

- [Overview](overview.md); [Zero-effort-miss law with an inverted lift curve](fly-a-boostback-and-glide-to-a-landing-site-2.md)
- [Boostback impact prediction (mod feature)](../../../../mods/ksa-advancedflightcomputer/features/boostback-impact-prediction.md)
- [Predict an impact point with gravity, drag and a measured drag table](../predict-impact-point-and-build-a-ballistic-computer/predict-impact-point-and-build-a-ballistic-computer-1.md)
- [Run an autopilot for every vehicle each step](../../autopilot/run-an-autopilot-for-every-vehicle-each-step/overview.md)
- [Stage a vehicle from code](../../../vehicles-and-parts/staging-and-engines/stage-a-vehicle-from-code/overview.md)

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-sequencelist]: KSA.SequenceList (decompiled, build 2026.10.10.5554)
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
