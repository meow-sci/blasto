---
type: How-To
title: Trim a bus with one translation direction at a time, then go quiet so KSA puts it back on rails
description: Null the velocity error against a propagated burnout solution by pressing translation actions one direction at a time in pulse mode, release payloads along one line, and stop commanding actuators so the craft returns to rails
category: flight-and-orbits
group: autopilot
topic: execute-burns-with-rcs-and-throttle-inputs
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksarmory/features/mirv-bus-trim-and-release]
techniques: [closed-loop-trim, input-injection, harmony-prefix]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:draw-ui, frame:dispatch-solvers, physics:vehicle-solver]
game_types:
  - KSA.Vehicle
  - KSA.FlightComputer
  - KSA.FlightComputerOutput
  - KSA.InputAction
  - KSA.PhysicsBubble
  - KSA.Universe
tags: [rcs, trim, translation, pulse-mode, rails, deploy, bus, closed-loop]
---

Outcome: after a ballistic burn, the bus is pushed back onto the solution the guidance flew to (the decoupler kicks it about a metre per second and the cutoff leaves a frame of upper-stage thrust), then it
releases several payloads one tube at a time and goes quiet so KSA propagates it as a conic. KSArmory's MIRV bus does this with its own RCS.[A MIRV post-boost bus that trims with RCS, re-points between releases and lets its warheads go](../../../../mods/ksarmory/features/mirv-bus-trim-and-release.md) Prerequisites: StarMap and Harmony.

## Background: how the game does it

Translation commands are `ThrusterMapFlags` bits in the vehicle's `_manualControlInputs.ThrusterCommandFlags`, set and cleared by `Vehicle.ProcessInput(InputAction.Translate*, ...)`. KSA decides which nozzles answer to a flag from where they point, so a
direction with no suitable nozzles simply does nothing. `FlightComputer.SetManualThrustMode(Pulse)` turns a held translation key into one pulse of each thruster's `MinimumPulseTime`, at most every 0.15 s, instead of thrust for a whole frame.
Separately, the vehicle step takes a craft off rails whenever any actuator is commanded (`FlightComputerOutput.AnyActuatorCommanded` in `PhysicsBubble`), so a craft that keeps commanding attitude is integrated instead of propagated.[^ksa-physicsbubble]
[current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1439-L1456)

## Lifecycle and timing

- `frame:draw-ui`: the computer decides what to do inside its step (normally from the `[StarMapAfterGui]` postfix), and presses or releases the `Translate*` actions directly: `ProcessInput` sets persistent flag bits that `PrepareWorker` reads on the following frame, with the same one-frame latency as a key press.
- `frame:dispatch-solvers`: pulse mode, attitude holds, quiet and rails are written by a `Vehicle.PrepareWorker` prefix just before the snapshot (flight-computer fields are overwritten by the copy-back otherwise).
- `physics:vehicle-solver`: whether the bus stays on rails is decided inside the worker step, which is why the mod must stop *commanding*, not just stop thrusting.

See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Define the directions** in the control frame (+X nose, +Y right, +Z down) as a flags enum, so the loop can strike off a direction that moves nothing: see `TrimAxes` in [src/KSArmory/Sim/BusTrim.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BusTrim.cs#L10-L19).
2. **Trim against the flown trajectory, never a fresh solve.** Propagate the trajectory the guidance flew to up to now, subtract what the bus is doing, and thrust along the difference until less than one frame of firing is left. A re-solved transfer depends on when it is asked
   (about 20 m/s per second of arrival error on a deorbit) and goes stale while the bus coasts clear.
3. **One direction at a time.** The stop threshold is half a frame of thrust along the direction actually fired; a direction that moves nothing is struck off and the next tried. Fire normally down to the band and pulse from there (a pulse is about sixteen times finer on the shipped bus).
4. **Actuate by key action** and restate every direction every cycle, because `ProcessInput` bits persist until released:

```csharp
// src/KSArmory/Ksa/VehicleCommand.cs L260-L277
public static void DriveTranslation(Vehicle craft, TrimAxes fire)
{
    if (!KsaWorld.IsAlive(craft)) return;

    Hold(InputAction.TranslateForward, TrimAxes.Forward);
    Hold(InputAction.TranslateBackward, TrimAxes.Backward);
    Hold(InputAction.TranslateRight, TrimAxes.Right);
    Hold(InputAction.TranslateLeft, TrimAxes.Left);
    Hold(InputAction.TranslateDown, TrimAxes.Down);
    Hold(InputAction.TranslateUp, TrimAxes.Up);

    void Hold(InputAction action, TrimAxes direction)
        => craft.ProcessInput(action,
                              (fire & direction) != TrimAxes.None
                                  ? GlfwKeyAction.Press
                                  : GlfwKeyAction.Release,
                              default);
}
```

5. **Set pulse mode from the prefix** (assign, do not toggle), before anything else the craft is doing that frame, so a standing translation command is spent as pulses.
6. **Wait for the split, then trim.** After the decoupler fires, record the vehicles that appeared (the dropped stack) and run a clearance check before trimming so the bus does not thrust into what it dropped. Dispose of spent stages with `Universe.DestroyVehicle`: each extra vehicle costs about 2 ms of frame time.
7. **Point each tube before it fires.** Read tube axes in ECL from the launcher, convert to CCI with `parent.GetCce2Cci()`, and let a release sequence turn the bus between releases:

```csharp
// src/KSArmory/Ksa/IcbmComputer.cs L3715-L3729
if (!_sequence.Begun && Config.RepointBetweenReleases
    && !(_tubeSpinSpeed > ReleaseSequence.SteadyToLatchMetresPerSecond))
{
    int found = weapon.TubeAxesEcl(_tubeAxes);
    if (found > 0 && Parent is { } parent)
    {
        doubleQuat cce2Cci = parent.GetCce2Cci();
        for (int i = 0; i < found; i++) _tubeAxes[i] = _tubeAxes[i].Transform(cce2Cci);

        if (_sequence.Begin(_tubeAxes.AsSpan(0, found)))
        {
            Log.Info($"aiming each of {found} tube(s) before it fires");
        }
    }
}
```

8. **Release when the predicted miss is below one correction cycle's holding cost**, measured off the actual trajectory (0.82 m/s on a 500 km shot, 21.79 on 12,900 km).
9. **Go quiet for the coast.** When the correction is finished and nothing is pending, stop commanding attitude (bounded at both ends, with a wider re-acquire threshold than the quiet one) and queue the release for the prefix, which also asserts rails:

```csharp
// src/KSArmory/Sim/CoastQuiet.cs L55-L67
public bool Update(in CoastQuietState now, double quietDeg, double reacquireDeg)
{
    if (!now.Enabled || !now.Coasting || now.Burning || now.Trimming || now.SalvoAway
        || !now.CorrectionFinished || now.InReleaseApproach
        || !double.IsFinite(now.PointingErrorDeg))
    {
        _quiet = false;
        return false;
    }

    _quiet = now.PointingErrorDeg <= (_quiet ? reacquireDeg : quietDeg);
    return _quiet;
}
```

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Sim/BusTrim.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BusTrim.cs) | trim loop, axis selection, give-up rule |
| [src/KSArmory/Ksa/IcbmComputer.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/IcbmComputer.cs#L1721-L1800) | `DriveTrim`: split, clearance, actuation |
| [src/KSArmory/Ksa/IcbmComputer.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/IcbmComputer.cs#L3681-L3760) | `DriveDeployment`: tube axes, release sequence |
| [src/KSArmory/Sim/PostBoostAim.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/PostBoostAim.cs) | release-when rule |
| [src/KSArmory/Sim/CoastQuiet.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/CoastQuiet.cs) | when to stop commanding |
| [src/KSArmory/KSArmoryGameData.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryGameData.xml#L479-L582) | the bus's RCS, tank, feed wiring and control |

## Variations and gotchas

- **Give up rather than hold the warheads**: with no thrusters or a dry tank, release on the trajectory you have.
- **Stopping thrusting is not enough for rails.** Asserting rails (`GetPhysicsStatesMutable().Props.SetOnRails(true)`) after releasing the attitude is the other half; in a Ccf bubble there is no path back to rails otherwise.
- **An integrated craft in a shared bubble picked up about 4 m/s per probe** of non-gravitational push; that is why the quiet state matters.
- A multi-target release walker is built but, at the pinned ref, not yet flown.

## See also

- [Overview](overview.md); [SetEnum-intercepted RCS burn](execute-burns-with-rcs-and-throttle-inputs-1.md); [input injection](execute-burns-with-rcs-and-throttle-inputs-4.md)
- [A MIRV post-boost bus that trims with RCS, re-points between releases and lets its warheads go](../../../../mods/ksarmory/features/mirv-bus-trim-and-release.md)
- [Run a closed-loop autopilot for every vehicle each step and arbitrate control with the player](../run-an-autopilot-for-every-vehicle-each-step/overview.md)

[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
