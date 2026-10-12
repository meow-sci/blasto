---
type: How-To
title: Drive throttle and translation through Vehicle.ProcessInput, re-applied from the PrepareWorker window
description: Press and release the engine and translation actions on any vehicle with the keyboard's own ProcessInput call, servo the throttle toward a target, and re-apply commands that PrepareWorker clears on the controlled craft
category: flight-and-orbits
group: autopilot
topic: execute-burns-with-rcs-and-throttle-inputs
approach: 4
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksarmory/features/fly-another-vehicle]
techniques: [input-injection, harmony-prefix]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:draw-ui, frame:dispatch-solvers]
game_types: [KSA.Vehicle, KSA.InputAction, KSA.FlightComputer, KSA.ManualControlInputs]
tags: [process-input, throttle, translation, staging, prepare-worker, held-keys]
---

Outcome: light, throttle and translate any vehicle (not just the player's) by the same call the keyboard makes. KSArmory's `VehicleCommand` flies whole rockets this way.[Fly any vehicle from a mod through its flight computer and input actions](../../../../mods/ksarmory/features/fly-another-vehicle.md) Prerequisites: StarMap and Harmony.

## Background: how the game does it

`Vehicle.ProcessInput(InputAction, GlfwKeyAction, GlfwModifier)` sets or clears persistent bits in the vehicle's engine and thruster flags (`_engineFlags` for `MainEngineThrottleUp`/`Down`, `ThrusterCommandFlags` for `Translate*`).[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3536-L3560)
`PrepareWorker` moves the throttle by `0.7 * playerDeltaTime` per frame while a throttle bit is set. For the **controlled** vehicle it first calls `ClearHeldPlayerInput()` when the vehicle is inactive, ImGui wants the keyboard, or simulation speed exceeds 30x, which discards held bits set by a mod too.[current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423)
Staging is `craft.Parts.SequenceList.ActivateNextSequence(craft)`.

## Lifecycle and timing

- **Decisions: `frame:draw-ui`.** The computer decides in its step, normally from `[StarMapAfterGui]`, and calls the input channel directly. `ProcessInput` needs no prefix: the bits persist and `PrepareWorker` reads them on the following frame, the same one-frame latency as a player's key press.
- **Delivery: `frame:dispatch-solvers` of the next frame.** `PrepareVehicleWorkers` calls `Vehicle.PrepareWorker` for every vehicle; a prefix runs there, after this frame's results were applied and before the worker is queued.
- **Effect: the step after that.** The command is integrated by the worker queued in that `PrepareFrame` and applied one frame later, so time a cutoff rather than waiting to observe it.
- **Staging must be in the window**: with the engine-control gauge or staging list open, KSA recomputes the controlled craft's stage performance off the main thread, and a sequence list rebuilt under it throws.

See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Mutate simulation state safely](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. **Throttle is a servo, not an assignment.** The only controls are the two a player holds down. Compare `GetManualThrottle()` with the clamped target and hold the up or down action; return the real value so guidance times its cutoff against the thrust the vehicle actually has:

```csharp
// src/KSArmory/Ksa/VehicleCommand.cs L226-L243
public static double DriveThrottle(Vehicle craft, double wanted, bool throughTheClear = false)
{
    if (!KsaWorld.IsAlive(craft)) return 1.0;

    double have = craft.GetManualThrottle();
    double want = Math.Clamp(wanted, craft.GetMinThrottle(), 1.0);

    bool up = have < want - ThrottleTolerance;
    bool down = have > want + ThrottleTolerance;
    AttitudeHook.ThrottleKey(craft, throughTheClear ? (up ? 1 : down ? -1 : 0) : 0);

    craft.ProcessInput(InputAction.MainEngineThrottleUp,
                       up ? GlfwKeyAction.Press : GlfwKeyAction.Release, default);
    craft.ProcessInput(InputAction.MainEngineThrottleDown,
                       down ? GlfwKeyAction.Press : GlfwKeyAction.Release, default);

    return have;
}
```

2. **Engines on and off** with `ProcessInput(InputAction.MainEngineStartup, Press, default)` and `MainEngineShutdown`.
3. **Translation** presses or releases each of six actions every cycle; the bits persist, so every direction must be restated (see approach 2 for the full method). Which nozzles answer is the vehicle's business.
4. **Stage from the window.** Queue the staging request and execute it in the `PrepareWorker` prefix (`ActivateNextSequence`), not from a draw hook.
5. **Survive the clear.** When the clear is predicted (`DiscardsHeldControls`), the prefix moves the throttle value directly by the step the key would have made, reflecting `Vehicle._manualControlInputs` and writing the boxed struct back:

```csharp
// src/KSArmory/Ksa/AttitudeHook.cs L345-L354
if (Staging.Count > 0 && Staging.Remove(__instance)) VehicleCommand.Stage(__instance);

// The clear runs inside PrepareWorker after this and takes the held keys, never the throttle value, so the
// step the key would have made is made here, at KSA's own 0.7 a second of the player's frame.
if (ThrottleKeys.Count > 0 && ThrottleKeys.TryGetValue(__instance, out int key)
    && KsaWorld.DiscardsHeldControls(__instance, out _))
{
    KsaWorld.TryNudgeThrottle(__instance, key * KsaWorld.PlayerDeltaTime * 0.7);
}

```

6. **Point the vehicle** with the Custom attitude target (see [Drive the flight computer and attitude modes from a mod](../drive-the-flight-computer-and-attitude-modes-from-a-mod/overview.md)) from the same prefix, since flight-computer fields are overwritten by the copy-back elsewhere.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/VehicleCommand.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/VehicleCommand.cs#L52-L110) | `TryAim`, `ReleaseAttitude` |
| [src/KSArmory/Ksa/VehicleCommand.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/VehicleCommand.cs#L215-L312) | engines, throttle, translation, pulse mode, staging |
| [src/KSArmory/Ksa/AttitudeHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/AttitudeHook.cs#L341-L420) | the prefix that replays queued commands |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L5266-L5338) | `TryNudgeThrottle`, `DiscardsHeldControls` |

## Variations and gotchas

- **Held keys on the controlled vehicle** are cleared in the three cases above; other vehicles are not cleared, so the channel is steadier on a non-controlled craft.
- **The burn mode must be Manual**: an Auto burn fights for attitude and zeroes `EngineOn` in `PrepareWorker`.
- **Do not assume `ProcessInput` clamps.** Throttle bounds come from `GetMinThrottle()` in `PrepareWorker`.
- Flying another vehicle also needs the claim and takeover checks of [Run a closed-loop autopilot for every vehicle each step and arbitrate control with the player](../run-an-autopilot-for-every-vehicle-each-step/overview.md).

## See also

- [Overview](overview.md); [reflection write](execute-burns-with-rcs-and-throttle-inputs-3.md); [trim loop](execute-burns-with-rcs-and-throttle-inputs-2.md)
- [Fly any vehicle from a mod through its flight computer and input actions](../../../../mods/ksarmory/features/fly-another-vehicle.md)
- [Run a closed-loop autopilot for every vehicle each step and arbitrate control with the player](../run-an-autopilot-for-every-vehicle-each-step/overview.md)

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
