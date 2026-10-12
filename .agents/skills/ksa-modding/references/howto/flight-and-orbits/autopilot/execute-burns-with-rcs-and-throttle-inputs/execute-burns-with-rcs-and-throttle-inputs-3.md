---
type: How-To
title: Write throttle and RCS flags by reflection into Vehicle._manualControlInputs
description: Set the main-engine throttle and the thruster command flags by boxing the private manual control struct, mutating it and writing it back, so a script's input looks like a held key
category: flight-and-orbits
group: autopilot
topic: execute-burns-with-rcs-and-throttle-inputs
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/manual-throttle-and-rcs-inputs]
techniques: [reflection]
hooks: [reflection KSA.Vehicle._manualControlInputs]
lifecycle: [frame:viewports, frame:dispatch-solvers]
game_types: [KSA.Vehicle, KSA.ManualControlInputs, KSA.ThrusterMapFlags, KSA.FlightComputer]
tags: [reflection, throttle, rcs, thruster-flags, manual-control-inputs, boxed-struct]
---

Outcome: set an exact throttle fraction and RCS translation and rotation flags on a vehicle from mod code. KSA exposes `Vehicle.GetManualThrottle()` and `Vehicle.GetThrusterFlags()` but no setter; gatOS sets `ctl/throttle`, `ctl/translate` and `ctl/rotate` by
reflecting into the private struct.[Manual throttle and RCS inputs](../../../../mods/gatos/features/manual-throttle-and-rcs-inputs.md) Prerequisite: StarMap (any main-thread hook).

## Background: how the game does it

`ManualControlInputs` is a struct with public fields `EngineOn`, `EngineThrottle`, `ThrusterCommandFlags`, `Sprint` and `GrabHeld`, held in the private `Vehicle._manualControlInputs`.[^ksa-manualcontrolinputs]
`Vehicle.PrepareWorker` passes it by value into `_threadWorkerUpdateState.PrepareFromVehicle(...)` at the next `ExecuteNextVehicleSolvers`, clamps `EngineThrottle` to `[GetMinThrottle(), 1]` and applies the throttle-up and throttle-down key rates.[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423)
`ThrusterMapFlags` has rotation bits (`RollRight`, `RollLeft`, `PitchUp`, `PitchDown`, `YawRight`, `YawLeft`) and translation bits (`TranslateForward`, `TranslateBackward`, `TranslateRight`, `TranslateLeft`, `TranslateDown`, `TranslateUp`).[^ksa-thrustermapflags] `FlightComputer.ComputeRcsControl` reads
the flags, cancels opposing commands and zeroes everything when `RCSMode != Enabled`; an active attitude hold strips the rotation bits.[^ksa-flightcomputer] [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L742-L800)

## Lifecycle and timing

gatOS writes in its frame-phase drain (the main-viewport `GameViewport.OnFrame` prefix, or `StarMapBeforeGui`), after the current step was dispatched. The write takes effect one frame later at `frame:dispatch-solvers`, and is never overwritten by the apply step because
`_manualControlInputs` is not copied back from the worker; that is why these controls do not need the solver-phase queue that flight-computer fields do (see [Mutate simulation state safely](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md)). For the controlled vehicle,
`PrepareWorker` calls `ClearHeldPlayerInput()` when the window is inactive, ImGui wants the keyboard, or warp exceeds 30x; the clear takes the held key state, not an already-written throttle value, but a written command flag can be cleared.

## Steps

1. **Cache the field handle once**, null-checked: if the private field is missing in a future build return an `Unsupported` result instead of throwing.
2. **Box, mutate, write back.** The struct is a value type; mutate the inner field on the box and set the box back:

```csharp
// gatOS.GameMod/Game/Ksa/Actuators/ThrottleActuator.cs L17-L41
private static readonly FieldInfo? ManualInputsField =
    typeof(Vehicle).GetField("_manualControlInputs", BindingFlags.Instance | BindingFlags.NonPublic);

[KsaAnchor("Vehicle._manualControlInputs.EngineThrottle (private; set by reflection)",
    SourceFile = "KSA/Vehicle.cs", Verified = "2026-06-12", Risk = ChurnRisk.High,
    Notes = "No public throttle setter; GetManualThrottle() reads this same field.")]
internal static CommandResult Set(Vehicle vehicle, double fraction)
{
    if (ManualInputsField is null)
        return new CommandResult(CommandOutcome.Unsupported, "manual throttle field not found in this build");

    // _manualControlInputs is a struct: box it, mutate the field, write the box back.
    var inputs = ManualInputsField.GetValue(vehicle);
    if (inputs is null)
        return new CommandResult(CommandOutcome.Fault, "manual control inputs unavailable");

    var throttleField = inputs.GetType().GetField("EngineThrottle",
        BindingFlags.Instance | BindingFlags.Public);
    if (throttleField is null)
        return new CommandResult(CommandOutcome.Unsupported, "EngineThrottle field not found in this build");

    throttleField.SetValue(inputs, (float)Math.Clamp(fraction, 0, 1));
    ManualInputsField.SetValue(vehicle, inputs);
    return CommandResult.Ok;
}
```

3. **Replace only your bits** for thruster flags so translation and rotation compose:

```csharp
// gatOS.GameMod/Game/Ksa/Actuators/TranslateActuator.cs L51-L60
var flagsField = inputs.GetType().GetField("ThrusterCommandFlags",
    BindingFlags.Instance | BindingFlags.Public);
if (flagsField is null)
    return new CommandResult(CommandOutcome.Unsupported, "ThrusterCommandFlags field not found in this build");

// Replace only the translation bits; any manual rotation bits stay untouched.
var current = (ThrusterMapFlags)flagsField.GetValue(inputs)!;
flagsField.SetValue(inputs, (current & ~AllTranslation) | Encode(axes));
ManualInputsField.SetValue(vehicle, inputs);
return CommandResult.Ok;
```

   Encoding: `ctl/translate x y z` is +X forward, +Y right, +Z down; `ctl/rotate` is +X roll right, +Y pitch up, +Z yaw right. Commands latch until rewritten with `0 0 0`.
4. **Enable RCS** through the flight computer's RCS mode (written in the solver phase, see [Create, chain and read stock maneuver nodes from code](../create-and-read-maneuver-nodes/overview.md) approach 2); with the RCS master disabled all manual thruster flags are zeroed.
5. **Use manual attitude** for rotation commands: an active attitude hold removes the rotation bits, so `ctl/rotate` only has authority in manual attitude mode.
6. **Read back** with `Vehicle.GetThrusterFlags()` and `GetManualThrottle()`.

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Actuators/ThrottleActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/ThrottleActuator.cs) | boxed-struct reflection write of `EngineThrottle` |
| [gatOS.GameMod/Game/Ksa/Actuators/TranslateActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/TranslateActuator.cs) | translation bits, preserves rotation bits |
| [gatOS.GameMod/Game/Ksa/Actuators/RotateActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/RotateActuator.cs) | rotation bits, preserves translation bits |
| [gatOS.SimFs/Commands/TranslateRules.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/TranslateRules.cs) | shared game-free validation |

## Variations and gotchas

- **High churn risk.** The field is private; pin it with a validated handle and degrade to `Unsupported`.
- **Throttle clamp.** `PrepareWorker` clamps to `[GetMinThrottle(), 1]`, so a requested 0 on an engine with a minimum throttle reads back higher.
- **`EngineOn` is zeroed while `BurnMode` is Auto**, so manual throttle commands do nothing during an Auto burn.
- **Latency is one frame.** Read back next frame, not in the same call.

## See also

- [Overview](overview.md); [ProcessInput injection](execute-burns-with-rcs-and-throttle-inputs-4.md); [SetEnum-intercepted RCS](execute-burns-with-rcs-and-throttle-inputs-1.md)
- [Manual throttle and RCS inputs](../../../../mods/gatos/features/manual-throttle-and-rcs-inputs.md)

[^ksa-manualcontrolinputs]: KSA.ManualControlInputs (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-thrustermapflags]: KSA.ThrusterMapFlags (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
