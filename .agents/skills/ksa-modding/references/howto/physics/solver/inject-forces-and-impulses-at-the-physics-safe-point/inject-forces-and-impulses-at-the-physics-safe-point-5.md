---
type: How-To
title: Hover and touchdown under real engine power with a PID
description: Hold a hover and land at a fixed rate by commanding the craft's own engines and attitude from the PrepareWorker prefix with a quadratic sink profile and per-axis PIDs, instead of injecting velocity
category: physics
group: solver
topic: inject-forces-and-impulses-at-the-physics-safe-point
approach: 5
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/terminal-hover-touchdown]
techniques: [harmony-prefix, custom-physics]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers, frame:draw-ui]
game_types: [KSA.Vehicle, KSA.FlightComputer, KSA.Celestial, KSA.Orbit, KSA.SimStep]
tags: [physics, vehicle, hover, landing, pid, guidance, throttle, terrain-height, feedforward]
---

Outcome: a craft nulls its rates, descends on a smooth sink-rate profile and touches down at a fixed rate under
the thrust of its real engines, while the player nudges the setpoints with the numpad. This is the opposite
trade to the other four approaches: it injects no velocity at all, it commands engine throttle and attitude,
so the result is physically honest (it burns propellant, respects minimum throttle and gimbal limits) but needs
a throttleable engine. Prerequisites: StarMap and Harmony. KSA-AdvancedFlightComputer (AFC) demonstrates it as
the last stage of every AFC landing.

## Background: how the game does it

The game flies a craft from its flight computer and manual control inputs. A craft that should hover
needs thrust-to-weight above one at full throttle and below one at minimum throttle: with more thrust at the
minimum it climbs whenever the engine runs, with less at full throttle it cannot hold its weight.[^ksa-vehicle]
The inputs are the manual control inputs (`EngineOn`, `EngineThrottle`) and the flight computer's attitude fields,
copied into the worker snapshot at the end of `Vehicle.PrepareWorker` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423).

The terrain height under the craft comes from `Celestial.GetTerrainHeightFromDirCcf` [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L777-L800)
with the position rotated into the body-fixed frame (CCF). Touchdown is detected from the game's own contact
flag, `Vehicle.Situation.HasAnyContact()` [current/decomp/KSA/SituationEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SituationEx.cs).

## Lifecycle and timing

The hover step runs in the `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`), as part of AFC's landing
state machine, for whichever vehicle is landing; the numpad is read in the gauge draw (`frame:draw-ui`) and only
changes setpoints that the next step consumes. Compared with the injection approaches this writes engine and
attitude commands, not `VelocityPhys`, so the same prefix windows apply (the commands are copied into the
snapshot at the end of `PrepareWorker`) but nothing is lost on rails: the engine firing keeps the craft in the
bubble.

Note the stock rule that `PrepareWorker` clears `_manualControlInputs.EngineOn` while the flight computer's
`BurnMode` is Auto, after the prefix has run. AFC therefore holds the burn mode in Manual while it flies the
craft (see the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md#lifecycle-and-timing)).

## Steps

1. Refuse early when the craft cannot hover. Thrust at minimum throttle above the weight means no rate profile can
   bring it down; AFC checks at engage (or on the first step after staging lights the engine) and reports why:

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

2. Measure height above the terrain directly below the craft, not above the landing site (a hover can drift), from
   the height field in the body-fixed frame, minus the vehicle height so zero means legs on the ground:

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

3. Build the descent-rate setpoint from a quadratic profile, `v = touchdown rate + k (h - h0)^2`, constant
   inside the last `h0` metres, then run one PID per axis (vertical and two lateral) on the velocity error with
   gravity fed forward on the vertical axis and the integrator clamped:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/TerminalHover.cs L141-L162
        // Descent-rate setpoint from the quadratic profile, plus the user's vertical nudge bias.
        // Above _s.TermConstAltM the rate grows with height squared (gentle flare); inside it the rate is constant for touchdown.
        double dh = Math.Max(h - _s.TermConstAltM, 0.0);
        double vDesc = Math.Min(_s.TermTouchdownRate + _s.TermQuadK * dh * dh, _s.TermMaxDescRate);
        double vSetUp = -vDesc + _s.TermSetUp;

        (double3 east, double3 north) = EnuBasis(up);
        double vE = double3.Dot(vSrf, east);
        double vN = double3.Dot(vSrf, north);

        double dt = Math.Clamp(now - _s.TermLastTime, 0.0, 0.25);
        _s.TermLastTime = now;
        if (!_s.TermInit)
        {
            dt = 0.0;
            _s.TermInit = true;
        }

        double g = mu / (r.Length() * r.Length());
        double aUp = g + StepPid(ref _s.TermPidUp, vSetUp - vUp, _s.TermKpV, _s.TermKiV, _s.TermKdV, dt);
        double aE = StepPid(ref _s.TermPidE, _s.TermSetE - vE, _s.TermKpL, _s.TermKiL, _s.TermKdL, dt);
        double aN = StepPid(ref _s.TermPidN, _s.TermSetN - vN, _s.TermKpL, _s.TermKiL, _s.TermKdL, dt);
```

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/TerminalHover.cs L180-L186
    private static double StepPid(ref Pid s, double err, double kp, double ki, double kd, double dt)
    {
        s.I = Math.Clamp(s.I + ki * err * dt, -TermILimit, TermILimit);
        double d = (dt > 1e-6 && kd > 0) ? kd * (err - s.PrevErr) / dt : 0.0;
        s.PrevErr = err;
        return kp * err + s.I + d;
    }
```

4. Turn the acceleration command into a throttle and a direction. The vector `(up, east, north)` gives the thrust
   needed (`|a| * mass`), `CommandForThrust` converts it to a throttle for this engine at this ambient pressure,
   and the direction is clamped to a tilt cone so lateral authority never flips the craft:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/TerminalHover.cs L164-L177
        // Local (x = up) command: throttle from the magnitude, direction clamped to the tilt cone so lateral authority never flips the vehicle over.
        var local = new double3(Math.Max(aUp, 0.0), aE, aN);
        double pressure = KsaEnginePerf.AmbientPressureAt(parent, r.Length() - parent.MeanRadius);
        KsaEnginePerf.ThrustCommand command = KsaEnginePerf.CommandForThrust(
            vehicle, local.Length() * vehicle.TotalMass, pressure);
        _s.GfoldThrottle = command.Throttle;
        _s.GfoldThrustStatus = command.Message;
        double3 dirLocal = ClampToCone(local, _s.TermMaxTiltDeg);
        double3 dir = dirLocal.X * up + dirLocal.Y * east + dirLocal.Z * north;
        if (dir.Length() > 1e-6)
        {
            _s.CommandDir = double3.Normalize(dir);
            _s.HasCommand = true;
        }
```

5. Write the commands where the worker will read them: in the same prefix, AFC's `ApplyAutopilot` writes engine
   state into the manual control inputs (and `CommandAttitude` aims the flight computer at `CommandDir`):

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs L1113-L1125
                ref ManualControlInputs inputs = ref ManualInputs(vehicle);
                if (_s.LandingPhase == LandingPhase.Burn)
                {
                    inputs.EngineOn = true;
                    // Mode 3's throttle command stretches the burn onto the site.
                    inputs.EngineThrottle = (float)_s.Upfg.Throttle;
                }
                else if (_s.LandingPhase is LandingPhase.GfoldDescent or LandingPhase.TerminalHover
                    or LandingPhase.TerminalCoast or LandingPhase.TerminalBrake)
                {
                    inputs.EngineOn = _s.GfoldThrottle > 0.0;
                    inputs.EngineThrottle = (float)_s.GfoldThrottle;
                }
```

6. Decide touchdown from the game's contact flag only (`vehicle.Situation.HasAnyContact()`), never from altitude or
   speed: the height is a terrain sample minus an assumed vehicle height, so it can sit above zero with the legs
   already down. An abort in the air must not cut the engine, or the craft drops.

7. Keep the numpad nudges in the draw: `ImGui.IsKeyPressed(ImGuiKey.Keypad8/2/6/4/9/3)` move the north/east/up
   setpoints and `Keypad5` zeroes them. They only change fields that step 3 reads on the next prefix call.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Modes/TerminalHover.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/TerminalHover.cs) | Hover profile, PID, height, refusal |
| [AdvancedFlightComputer/Features/Guidance/Modes/TerminalBurn.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/TerminalBurn.cs) | Coast and final burn for a non-throttleable engine |
| [AdvancedFlightComputer/Features/Guidance/Ui/Gauges/HoverGauge.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/Gauges/HoverGauge.cs) | Hover gauge and keypad nudges |
| [AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs#L24-L75) | The `PrepareWorker` prefix that runs it |

## Variations and gotchas

- A craft whose minimum thrust exceeds its weight cannot hover. AFC lands it with an upright coast and a timed
  final burn (`TerminalBraking`) of at most 15 degrees of tilt instead.
- Use the terrain under the vehicle for a hover, not the site's terrain.
- Contrast with [approaches 1 and 4](inject-forces-and-impulses-at-the-physics-safe-point-1.md): injection gives
  a craft that hovers with no engine, instantly and with no fuel; engine commanding gives one the player can read
  off the throttle gauge and that runs out of propellant.
- The PID `dt` is clamped to 0.25 s and zeroed on the first step, so a pause or a long warp step cannot spike the
  derivative or integrator; the integrator is clamped at 3 m/s^2.
- The numpad is read from ImGui key state, not the game's key bindings, so it is not filtered by them.
- A kitten on EVA is a vehicle too; guidance skips it.

## See also

- [Overview and comparison](overview.md)
- [Approach 4: velocity and spin deltas gated by hatches](inject-forces-and-impulses-at-the-physics-safe-point-4.md)
- Mod feature: [AFC terminal hover and touchdown](../../../../mods/ksa-advancedflightcomputer/features/terminal-hover-touchdown.md)
- [Mutate vehicles at a physics-safe point](../../solver/mutate-vehicles-at-a-physics-safe-point/overview.md)
- The topic query-terrain-height-and-line-of-sight (another batch) for terrain-height queries

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
