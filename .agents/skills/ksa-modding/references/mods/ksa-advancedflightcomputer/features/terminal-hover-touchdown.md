---
type: Mod Feature
title: Terminal hover and touchdown with keypad nudges
description: A pilot-in-the-loop final descent on a quadratic sink profile with per-axis PID and gravity feedforward, a coast-and-burn mode for engines whose minimum thrust exceeds weight, terrain height below the craft, and touchdown taken from the game's contact flag
mod: ksa-advancedflightcomputer
category: flight-and-orbits
techniques: [harmony-prefix, imgui-window]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers, frame:draw-ui]
game_types: [KSA.Vehicle, KSA.Celestial, KSA.FlightComputer, KSA.ManualControlInputs]
tags: [landing, hover, pid, touchdown, terrain, keypad, guidance]
---

The last stage of every AFC landing is a terminal hover: the craft nulls its rates, descends on a
smooth sink-rate profile and touches down at a fixed rate, while the player can nudge the velocity
setpoints with the numpad to steer the touchdown point by eye. A craft that cannot hover (its minimum
thrust exceeds its weight) is landed with an upright coast and a timed final burn instead. Abort
semantics are part of the design: an abort in the air must not cut the engine.

## How it works

### Hover

- **Profile.** Vertical setpoint `v = touchdown rate + k (h - h0)^2`, dropping to a constant touchdown
  rate for the last `h0` metres; lateral setpoints start at zero.
- **Control.** A PID per axis on vertical and lateral velocity error produces a thrust command, with
  gravity fed forward on the vertical axis and the integrator clamped at 3 m/s^2
  ([AdvancedFlightComputer/Features/Guidance/Modes/TerminalHover.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/TerminalHover.cs#L1-L40)).
- **Height.** Measured to the terrain directly below the craft (not below the site, because a hover can
  drift), from `Celestial.GetTerrainHeightFromDirCcf` with the position rotated into the body-fixed frame,
  minus the vehicle height so zero means legs on the ground
  ([AdvancedFlightComputer/Features/Guidance/Modes/TerminalHover.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/TerminalHover.cs#L91-L99)).
- **Nudges.** The hover gauge reads `ImGui.IsKeyPressed(ImGuiKey.Keypad8/2/6/4/9/3)` and `Keypad5` to move
  the north/east/up setpoints, plus on-screen buttons; all state (profile, gains, integrators, setpoints)
  is per vehicle ([AdvancedFlightComputer/Features/Guidance/Ui/Gauges/HoverGauge.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/Gauges/HoverGauge.cs#L85-L100)).
- **Refusal.** The hover refuses a craft whose thrust at minimum throttle exceeds its weight (it would
  climb whenever the engine runs); a G-FOLD or 6-DOF descent that would hand over to it flies its own
  plan to touchdown instead.

### Coast and burn for a non-hovering engine

`TerminalBraking` handles an engine that cannot throttle below weight: the engine cuts for an upright
coast, then a timed final burn uses current speed, height, gravity, thrust and minimum pulse, with at most
15 degrees of commanded tilt; one step or minimum pulse may remove at most the sink above the touchdown
speed, and below the estimated touchdown plane minimum thrust holds the sink between touchdown speed and
the leg contact limit ([AdvancedFlightComputer/Features/Guidance/Gfold/TerminalBraking.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Gfold/TerminalBraking.cs),
[AdvancedFlightComputer/Features/Guidance/Modes/TerminalBurn.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/TerminalBurn.cs)). `LandingEngineWait` waits for a
staging that may bring a throttleable liquid engine, without buying a staging action on its way out.

### Touchdown and abort

Touchdown is decided only by the game's contact flag, `vehicle.Situation.HasAnyContact()`
([AdvancedFlightComputer/Features/Guidance/Modes/Landing.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Landing.cs#L129)). An abort during the powered
descent or the hover hands the craft back with the engine as it is, so it does not drop; every other abort
cuts the engine (see [vehicle control ownership](vehicle-control-ownership.md)).

## Lifecycle and timing

The hover step runs in the `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`) as part of the landing
state machine, for whichever vehicle is landing; the keypad is read in the gauge draw (`frame:draw-ui`) and
only changes setpoints that the next step consumes. Using ImGui key state during the draw is fine for
nudges; it is not filtered by the game's own key bindings.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Modes/TerminalHover.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/TerminalHover.cs) | Hover profile, PID, height, refusal |
| [AdvancedFlightComputer/Features/Guidance/Modes/TerminalBurn.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/TerminalBurn.cs) | Coast and final burn mode |
| [AdvancedFlightComputer/Features/Guidance/Gfold/TerminalBraking.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Gfold/TerminalBraking.cs) | Braking law for non-hovering engines (game-independent) |
| [AdvancedFlightComputer/Features/Guidance/Modes/LandingEngineWait.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/LandingEngineWait.cs) | Wait for a throttleable engine |
| [AdvancedFlightComputer/Features/Guidance/Ui/Gauges/HoverGauge.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/Gauges/HoverGauge.cs) | Hover gauge and keypad nudges |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Guidance/GuidanceTerminalBurnTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Guidance/GuidanceTerminalBurnTest.cs) | Headless terminal-burn test |

## Snippets

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/TerminalHover.cs L91-L99
    // Height of the touchdown plane above the terrain DIRECTLY BELOW the vehicle (not the landing site - a hover can drift anywhere), minus the vehicle height so zero means legs on the ground.
    private static double TerminalHeight(Orbit orbit, IParentBody parent, double bodyRadius)
    {
        double3 r = orbit.StateVectors.PositionCci;
        double3 dirCcf = double3.Normalize(r).Transform(parent.GetCci2Ccf());
        double terrain = (parent as Celestial)?.GetTerrainHeightFromDirCcf(dirCcf) ?? 0.0;
        if (!double.IsFinite(terrain))
            terrain = 0.0;
        return r.Length() - (bodyRadius + terrain) - _s.VehicleHeightM;
```

## Game APIs touched

| Member | Use |
|---|---|
| `Celestial.GetTerrainHeightFromDirCcf` ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs)) | Terrain height under the craft |
| `IParentBody.GetCci2Ccf` | Inertial to body-fixed direction |
| `Vehicle.Situation` with `SituationEx.HasAnyContact()` ([current/decomp/KSA/SituationEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SituationEx.cs#L18)) | Touchdown |
| `Vehicle.GetMinThrottle` | Hover feasibility |

## Pitfalls and version notes

- Use the terrain under the vehicle for a hover, not the site's terrain.
- Do not detect touchdown from altitude or speed; the contact flag is what the physics uses.
- Do not cut the engine on an abort in the air.

## See also

- [G-FOLD powered descent](gfold-powered-descent.md)
- [6-DOF SCvx landing MPC](scvx-6dof-landing-mpc.md)
- [Deorbit landing planner](deorbit-landing-planner.md)
- [Guidance panel and overlays](guidance-panel-and-overlays.md)
- [Height of the terrain directly below a craft for hover and touchdown](../../../howto/celestials/terrain/query-terrain-height-and-line-of-sight/query-terrain-height-and-line-of-sight-4.md) - how-to article demonstrating this feature
- [Hover and touch down with a sink-profile velocity controller and terrain height below the craft](../../../howto/flight-and-orbits/guidance/plan-and-fly-a-powered-landing/plan-and-fly-a-powered-landing-3.md) - how-to article demonstrating this feature
- [Hover and touchdown under real engine power with a PID](../../../howto/physics/solver/inject-forces-and-impulses-at-the-physics-safe-point/inject-forces-and-impulses-at-the-physics-safe-point-5.md) - how-to article demonstrating this feature

