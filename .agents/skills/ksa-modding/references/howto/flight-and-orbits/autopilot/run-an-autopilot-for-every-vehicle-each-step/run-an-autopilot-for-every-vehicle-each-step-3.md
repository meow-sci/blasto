---
type: How-To
title: Fly a vehicle that is not the controlled one, then hand it back
description: Command another craft's throttle through Vehicle.ProcessInput and its staging through the sequence list, point it with a Custom target, and release it so the player gets back what they had
category: flight-and-orbits
group: autopilot
topic: run-an-autopilot-for-every-vehicle-each-step
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksarmory/features/fly-another-vehicle]
techniques: [harmony-prefix, input-injection]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:draw-ui, frame:dispatch-solvers]
game_types: [KSA.Vehicle, KSA.FlightComputer, KSA.SequenceList, KSA.InputAction, KSA.FlightComputerBurnMode]
tags: [autopilot, other-vehicle, process-input, staging, release, ballistic]
---

Outcome: a mod-side computer flies a whole rocket the player built: points it, lights and throttles its engines, fires its thrusters, stages it and later hands it back. All of KSArmory's ballistic computer's commands live in one static class, `VehicleCommand`.[Fly any vehicle from a mod through its flight computer and input actions](../../../../mods/ksarmory/features/fly-another-vehicle.md)
The mechanics of each channel are in [Execute burns with RCS translation and throttle inputs from code](../execute-burns-with-rcs-and-throttle-inputs/overview.md) (approach 4) and [Drive the flight computer and attitude modes from a mod](../drive-the-flight-computer-and-attitude-modes-from-a-mod/overview.md) (approach 2); this article is about the arbitration. Prerequisites: StarMap and Harmony.

## Background: how the game does it

Two kinds of channel exist and need different windows. `Vehicle.ProcessInput(InputAction, ...)` sets persistent bits that `PrepareWorker` reads on the following frame, so it can be called from a hook with a player's latency. Flight-computer fields (attitude frame, track target, custom target, mode, pulse mode, burn mode) and staging
(`craft.Parts.SequenceList.ActivateNextSequence(craft)`) must be written between the apply and the snapshot, from the `PrepareWorker` prefix.[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3536-L3560) For the **controlled** vehicle, `PrepareWorker` first clears held input when the vehicle is inactive, ImGui wants the keyboard or simulation speed exceeds 30x.[current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423)

## Lifecycle and timing

- **Decisions: `frame:draw-ui`.** The computer decides in its simulation step (normally from `[StarMapAfterGui]`) and calls the input channel directly, queueing attitude, pulse and staging for the prefix.
- **Delivery: `frame:dispatch-solvers` of the next frame.** The prefix replays the queue after this frame's results were applied and before the worker is queued.
- **Effect: the step after that**, applied one frame later; time a cutoff rather than waiting to observe it.

See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Take a claim on the craft** (see [approach 4](run-an-autopilot-for-every-vehicle-each-step-4.md)) so another controller does not steer it; do not gate on `Program.ControlledVehicle`.
2. **Force the burn mode to Manual** when you take the attitude: an Auto burn fights for it and runs its own throttle. The attitude write sets Auto attitude, EclBody frame, Custom track target, the Euler angles and a Strict profile, and `BurnMode = Manual`.
3. **Light and throttle** with `ProcessInput(MainEngineStartup/Shutdown, Press, default)` and a throttle servo that holds `MainEngineThrottleUp`/`Down` toward the target (see [Execute burns with RCS translation and throttle inputs from code](../execute-burns-with-rcs-and-throttle-inputs/overview.md)).
4. **Stage from the window:** `craft.Parts.SequenceList.ActivateNextSequence(craft)`, queued to the prefix.
5. **Let go:** one call returns the craft to whoever was flying it:

```csharp
// src/KSArmory/Ksa/VehicleCommand.cs L100-L109

/// <summary>Give the vehicle back to whoever was flying it.</summary>
public static void ReleaseAttitude(Vehicle craft)
{
    if (!KsaWorld.IsAlive(craft)) return;

    FlightComputer computer = craft.FlightComputer;
    computer.AttitudeMode = FlightComputerAttitudeMode.Manual;
    computer.AttitudeTrackTarget = FlightComputerAttitudeTrackTarget.None;
    computer.CustomAttitudeTarget = double3.Zero;
```

   Also clear the pulse mode back to `Direct`, release the throttle key bits, and, for a coasting craft, assert rails after releasing the attitude.
6. **Dispose of what you drop.** Stages shed by a separation can be removed with `Universe.DestroyVehicle` (end mission) because each extra vehicle costs frame time.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/VehicleCommand.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/VehicleCommand.cs#L52-L110) | `TryAim`, `ReleaseAttitude` |
| [src/KSArmory/Ksa/VehicleCommand.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/VehicleCommand.cs#L215-L312) | engines, throttle, translation, pulse mode, staging |
| [src/KSArmory/Ksa/AttitudeHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/AttitudeHook.cs#L341-L420) | the prefix that replays queued commands |
| [src/KSArmory/Ksa/IcbmComputer.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/IcbmComputer.cs#L965-L1130) | the caller: point, throttle, ignite, stage |

## Variations and gotchas

- **The roll reference** the engine uses for a planet-direction target reverses when the nose points straight up or down and spins a rising rocket; carry your own across frames.
- **Held keys on the controlled craft are cleared** in three cases, so command another craft or use the throttle nudge path.
- **Staging while the engine-control or sequence gauge is open** races the worker's performance recompute if done from a hook.
- The keyboard-style channel leaves the player's own bindings in place; two controllers writing the same bits is a fight, hence the claim.

## See also

- [Overview](overview.md); [step loop](run-an-autopilot-for-every-vehicle-each-step-1.md); [claims](run-an-autopilot-for-every-vehicle-each-step-4.md)
- [Fly any vehicle from a mod through its flight computer and input actions](../../../../mods/ksarmory/features/fly-another-vehicle.md)
- [Execute burns with RCS translation and throttle inputs from code](../execute-burns-with-rcs-and-throttle-inputs/overview.md)

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
