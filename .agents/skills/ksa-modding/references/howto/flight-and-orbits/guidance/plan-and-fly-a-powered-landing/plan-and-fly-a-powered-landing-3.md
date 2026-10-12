---
type: How-To
title: Hover and touch down with a sink-profile velocity controller and terrain height below the craft
description: Land the last stretch with per-axis velocity PID controllers that follow a quadratic sink-rate profile, measure height to the terrain directly below the craft, refuse craft that cannot hover, and detect touchdown from the game's contact flag
category: flight-and-orbits
group: guidance
topic: plan-and-fly-a-powered-landing
approach: 3
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/terminal-hover-touchdown]
techniques: [harmony-prefix, pid-control, custom-physics]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers, frame:draw-ui]
game_types: [KSA.Vehicle, KSA.Celestial, KSA.IParentBody, KSA.Orbit, KSA.FlightComputer]
tags: [landing, hover, touchdown, pid, terrain-height, contact, sink-rate]
---
Outcome: the last stage of a landing. The craft nulls its lateral rate, descends on a smooth sink-rate profile that ends in a constant touchdown rate, lets the pilot nudge the setpoints to steer the touchdown point by eye, and the engine is cut on the frame the game reports contact. AFC uses it as the final stage of every powered landing (G-FOLD, 6-DOF) and it works standalone for any throttleable liquid engine.[Terminal hover and touchdown](../../../../mods/ksa-advancedflightcomputer/features/terminal-hover-touchdown.md)
Prerequisites: StarMap and Harmony. The thrust-to-throttle helper (`KsaEnginePerf.CommandForThrust`) is described in [Compute a suicide-burn start altitude and the thrust it needs](../compute-a-suicide-burn/compute-a-suicide-burn-3.md).

## Background: how the game does it

- **Height under the craft.** `Celestial.GetTerrainHeightFromDirCcf(directionCcf)` gives terrain height above `MeanRadius` for a body-fixed direction; `IParentBody.GetCci2Ccf()` rotates an inertial direction into that frame.[^ksa-celestial] [^ksa-iparentbody] [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L789-L789) The same sample underlies `Vehicle.GetRadarAltitude()`.
- **Contact.** The physics step raises a terrain-contact flag whenever any part touches the terrain or a launch-pad collider; `SituationEx.HasAnyContact(this Situation)` exposes it as `vehicle.Situation.HasAnyContact()`.[^ksa-situationex] [current/decomp/KSA/SituationEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SituationEx.cs#L18-L18)
- **Engine minimum.** `Vehicle.GetMinThrottle()` is the throttle floor; a craft whose thrust at that floor exceeds its weight climbs whenever the engine runs.[^ksa-vehicle]

## Lifecycle and timing

- **Control step:** the `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`) calls the landing state machine for every landing vehicle, on or off screen, so the setpoints, throttle and direction are what the next physics step sees. It is the same site as in [approach 1](plan-and-fly-a-powered-landing-1.md); the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) shows why it is the last settled point.
- **Nudges:** the numpad is read in the gauge draw (`frame:draw-ui`) and only changes setpoints that the next step consumes. ImGui key state is not filtered by the game's key bindings.
- **Touchdown** is tested first in each step, before any powered phase steps, so the engine is cut on the frame contact is reported instead of the controller fighting the ground.

## Steps

1. **Check the craft can hover.** The thrust-to-weight at the minimum throttle must be at most 1, or no sink profile can bring it down. AFC asks this before it lets go of a G-FOLD or 6-DOF descent, because a refused hover claims nothing and would leave the craft released in the air:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/TerminalHover.cs L63-L89
// Thrust-to-weight at the given throttle, at the current mass and local gravity. A hover needs more than one at full throttle and less than one at the minimum, or the engine cannot hold the weight on one side and cannot stop climbing on the other.
private static double TerminalTwrAt(Vehicle vehicle, Orbit orbit, double mu, double throttle)
{
    double rLen = orbit.StateVectors.PositionCci.Length();
    double pressure = KsaEnginePerf.AmbientPressureAt(vehicle.Parent, rLen - vehicle.Parent.MeanRadius);
    double thrust = KsaEnginePerf.ThrustAtThrottle(vehicle, throttle, pressure);
    double g = mu / (rLen * rLen);
    double weight = vehicle.TotalMass * g;
    return weight > 0 ? thrust / weight : 0.0;
}

private static double TerminalTwr(Vehicle vehicle, Orbit orbit, double mu)
    => TerminalTwrAt(vehicle, orbit, mu, 1.0);

private static double TerminalMinThrottleTwr(Vehicle vehicle, Orbit orbit, double mu)
    => TerminalTwrAt(vehicle, orbit, mu, vehicle.GetMinThrottle());

// A craft that out-thrusts its weight at the minimum throttle climbs whenever the engine runs, so no rate profile can bring it down. Checked at the engage when the engine is lit, or on the first step after staging lights it, so a craft that crosses the line while it burns propellant keeps the hover and shows the readout.
private static bool TerminalHoverRefused(Vehicle vehicle, Orbit orbit, double mu)
{
    double minTwr = TerminalMinThrottleTwr(vehicle, orbit, mu);
    if (!(minTwr > 1.0))
        return false;
    _s.LandingStatus = $"Terminal hover refused: the thrust at minimum throttle is {minTwr:F2} times the weight, so the craft climbs whenever the engine runs.";
    GuidanceLog.Info(vehicle, _s.LandingStatus);
    return true;
}
```

2. **Measure height to the terrain directly below the craft**, not below the landing site, because a hover can drift anywhere. Zero means legs on the ground (subtract the vehicle height):

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/TerminalHover.cs L91-L100
// Height of the touchdown plane above the terrain DIRECTLY BELOW the vehicle (not the landing site - a hover can drift anywhere), minus the vehicle height so zero means legs on the ground.
private static double TerminalHeight(Orbit orbit, IParentBody parent, double bodyRadius)
{
    double3 r = orbit.StateVectors.PositionCci;
    double3 dirCcf = double3.Normalize(r).Transform(parent.GetCci2Ccf());
    double terrain = (parent as Celestial)?.GetTerrainHeightFromDirCcf(dirCcf) ?? 0.0;
    if (!double.IsFinite(terrain))
        terrain = 0.0;
    return r.Length() - (bodyRadius + terrain) - _s.VehicleHeightM;
}
```

3. **Pick the sink-rate setpoint from a quadratic profile.** Above the constant-rate zone the rate grows with height squared (a gentle flare); inside it the touchdown rate is constant. The pilot's vertical nudge is a bias:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/TerminalHover.cs L141-L145
// Descent-rate setpoint from the quadratic profile, plus the user's vertical nudge bias.
// Above _s.TermConstAltM the rate grows with height squared (gentle flare); inside it the rate is constant for touchdown.
double dh = Math.Max(h - _s.TermConstAltM, 0.0);
double vDesc = Math.Min(_s.TermTouchdownRate + _s.TermQuadK * dh * dh, _s.TermMaxDescRate);
double vSetUp = -vDesc + _s.TermSetUp;
```

4. **Run a PID per axis on velocity error and feed gravity forward on the vertical axis.** The integrator is clamped (3 m/s^2). Convert the local command to a thrust demand, invert it to a throttle, and clamp the direction to a tilt cone so lateral authority never flips the vehicle:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/TerminalHover.cs L159-L172
double g = mu / (r.Length() * r.Length());
double aUp = g + StepPid(ref _s.TermPidUp, vSetUp - vUp, _s.TermKpV, _s.TermKiV, _s.TermKdV, dt);
double aE = StepPid(ref _s.TermPidE, _s.TermSetE - vE, _s.TermKpL, _s.TermKiL, _s.TermKdL, dt);
double aN = StepPid(ref _s.TermPidN, _s.TermSetN - vN, _s.TermKpL, _s.TermKiL, _s.TermKdL, dt);

// Local (x = up) command: throttle from the magnitude, direction clamped to the tilt cone so lateral authority never flips the vehicle over.
var local = new double3(Math.Max(aUp, 0.0), aE, aN);
double pressure = KsaEnginePerf.AmbientPressureAt(parent, r.Length() - parent.MeanRadius);
KsaEnginePerf.ThrustCommand command = KsaEnginePerf.CommandForThrust(
    vehicle, local.Length() * vehicle.TotalMass, pressure);
_s.GfoldThrottle = command.Throttle;
_s.GfoldThrustStatus = command.Message;
double3 dirLocal = ClampToCone(local, _s.TermMaxTiltDeg);
double3 dir = dirLocal.X * up + dirLocal.Y * east + dirLocal.Z * north;
```

5. **Decide touchdown only from the contact flag**, never from altitude or speed (the altitude estimate is terrain-height sampling minus an assumed vehicle height and can read low). Arm the detector only after the vehicle has been off the ground, because a craft on the pad already reports contact:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/Landing.cs L146-L162
bool contact = HasTouchedDown(vehicle);
if (_s.LandingPhase != _s.TouchdownPrevPhase)
{
    bool continuesDescent = IsPoweredDescentPhase(_s.TouchdownPrevPhase) && IsPoweredDescentPhase(_s.LandingPhase);
    _s.TouchdownPrevPhase = _s.LandingPhase;
    if (!continuesDescent) _s.LandingTouchdownArmed = false;
}
if (!contact)
    _s.LandingTouchdownArmed = true;

if (contact && _s.LandingTouchdownArmed && IsPoweredDescentPhase(_s.LandingPhase))
{
    ResetLandingEngineWait();
    _s.GfoldThrottle = 0.0;
    _s.HasCommand = false;
    _s.LandingPhase = LandingPhase.Done;
    _s.LandingCutPending = true;
```

6. **Read the numpad for nudges** in the gauge draw; the buttons mirror the keys:

```csharp
// AdvancedFlightComputer/Features/Guidance/Ui/Gauges/HoverGauge.cs L86-L98
// Numpad nudges while hovering. Unlikely to clash with game bindings, and the buttons below always work regardless.
if (active)
{
    if (ImGui.IsKeyPressed(ImGuiKey.Keypad8)) _s.TermSetN += _s.TermNudgeStep;
    if (ImGui.IsKeyPressed(ImGuiKey.Keypad2)) _s.TermSetN -= _s.TermNudgeStep;
    if (ImGui.IsKeyPressed(ImGuiKey.Keypad6)) _s.TermSetE += _s.TermNudgeStep;
    if (ImGui.IsKeyPressed(ImGuiKey.Keypad4)) _s.TermSetE -= _s.TermNudgeStep;
    if (ImGui.IsKeyPressed(ImGuiKey.Keypad9)) _s.TermSetUp += _s.TermNudgeStep;
    if (ImGui.IsKeyPressed(ImGuiKey.Keypad3)) _s.TermSetUp -= _s.TermNudgeStep;
    if (ImGui.IsKeyPressed(ImGuiKey.Keypad5, false)) ZeroTerminalSetpoints();
}

// Laid out as the numpad it mirrors, so the keys and the buttons are the same control rather than two things that happen to agree.
```

7. **Fall back for engines that cannot hover.** A craft whose minimum thrust exceeds its weight is landed by an upright coast and a timed final burn (the `LandingPhase.TerminalCoast` and `TerminalBrake` phases run by `StepTerminalBurn`, with at most 15 degrees of commanded tilt), and a landing-engine wait (`LandingEngineWaitStart`, reset by `ResetLandingEngineWait`) waits for a staging that may bring a throttleable liquid engine.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Modes/TerminalHover.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/TerminalHover.cs) | hover profile, PID, height, refusal |
| [AdvancedFlightComputer/Features/Guidance/Modes/TerminalBurn.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/TerminalBurn.cs) | coast and final burn for non-hovering engines |
| [AdvancedFlightComputer/Features/Guidance/Modes/Landing.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Landing.cs#L129-L162) | contact flag and armed touchdown detection |
| [AdvancedFlightComputer/Features/Guidance/Ui/Gauges/HoverGauge.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/Gauges/HoverGauge.cs#L80-L104) | setpoint display and numpad nudges |
| [AdvancedFlightComputer/Features/Guidance/Modes/LandingEngineWait.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/LandingEngineWait.cs) | wait for a throttleable engine after staging |

## Variations and gotchas

- **Use the terrain under the vehicle** for a hover, not the site's terrain.
- **Do not cut the engine on an abort in the air.** An abort during the powered descent or the hover hands the craft back with the engine as it is, so it does not drop; every other abort cuts the engine.
- **Zero demand switches the engine off**; a hover that must throttle below the engine minimum is refused upstream.
- **The altitude test has no cutoff**: the hover keeps flying the profile until something actually touches.
- **Gains are per vehicle**: profile, gains, integrators and setpoints live in per-vehicle state, so several landing craft do not share them.

## See also

- [Overview](overview.md); [G-FOLD](plan-and-fly-a-powered-landing-1.md); [6-DOF MPC](plan-and-fly-a-powered-landing-2.md)
- [Terminal hover and touchdown (mod feature)](../../../../mods/ksa-advancedflightcomputer/features/terminal-hover-touchdown.md)
- [Run an autopilot for every vehicle each step](../../autopilot/run-an-autopilot-for-every-vehicle-each-step/overview.md)
- [Read flight state in a local horizon frame](../../flight-state/read-flight-state-in-a-local-horizon-frame/overview.md)

[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-situationex]: KSA.SituationEx (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
