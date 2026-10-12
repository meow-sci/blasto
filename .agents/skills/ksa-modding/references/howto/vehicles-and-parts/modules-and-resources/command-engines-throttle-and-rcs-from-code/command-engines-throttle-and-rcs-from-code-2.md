---
type: How-To
title: Set manual throttle and RCS command flags by writing the boxed manual-control struct
description: Reach Vehicle._manualControlInputs by reflection, box it, set EngineThrottle or ThrusterCommandFlags and write it back, so a script's input is indistinguishable from a held key
category: vehicles-and-parts
group: modules-and-resources
topic: command-engines-throttle-and-rcs-from-code
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/manual-throttle-and-rcs-inputs]
techniques: [reflection]
hooks: [reflection KSA.Vehicle._manualControlInputs]
lifecycle: [frame:viewports, frame:dispatch-solvers, physics:vehicle-solver]
game_types: [KSA.Vehicle, KSA.ManualControlInputs, KSA.ThrusterMapFlags, KSA.FlightComputer]
tags: [throttle, rcs, reflection, manual-control, thruster-flags]
---
Outcome: you set the manual throttle and the translation or rotation command flags of any vehicle from code. KSA
exposes `GetManualThrottle()` and `GetThrusterFlags()` but no setter for either; both live in a private struct the
keyboard handlers write and the vehicle solver copies at every dispatch. gatOS sets `ctl/throttle`, `ctl/translate` and
`ctl/rotate` by reflecting into it.

Prerequisite: StarMap and reflection. Demonstrated by [gatos/manual-throttle-and-rcs-inputs](../../../../mods/gatos/features/manual-throttle-and-rcs-inputs.md). KSArmory's
alternative, `ProcessInput` with throttle-up and -down actions, is in [Take control of another vehicle: switch the controlled craft or autopilot it from a mod](../../vehicles/take-control-of-another-vehicle/overview.md) approach 2.

## Background: how the game does it

- `ManualControlInputs` is a struct with public fields `EngineOn`, `EngineThrottle`, `ThrusterCommandFlags`,
  `Sprint`, `GrabHeld` [current/decomp/KSA/ManualControlInputs.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ManualControlInputs.cs#L1-L18)[^ksa-manualcontrolinputs], held in the private field `Vehicle._manualControlInputs`.
- `ThrusterMapFlags` bits: `RollRight`, `RollLeft`, `PitchUp`, `PitchDown`, `YawRight`, `YawLeft`, `TranslateForward`,
  `TranslateBackward`, `TranslateRight`, `TranslateLeft`, `TranslateDown`, `TranslateUp` [current/decomp/KSA/ThrusterMapFlags.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ThrusterMapFlags.cs#L1-L21)[^ksa-thrustermapflags].
- `FlightComputer.ComputeRcsControl` reads the flags each solver step, cancels opposing commands, zeroes everything
  when `RCSMode != Enabled`, and when an attitude hold is active removes the rotation bits (`WithNoRotation()`), so a
  rotate command has authority only in manual attitude [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L742-L819)[^ksa-flightcomputer].
- `Vehicle.PrepareWorker` clamps `EngineThrottle` to `[GetMinThrottle(), 1]`, applies throttle-key rates, and for the
  controlled vehicle calls `ClearHeldPlayerInput()` when the window is inactive, ImGui wants the keyboard, or warp exceeds
  30x [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423)[^ksa-vehicle].

## Lifecycle and timing

Write from the main thread at any hook after the step was dispatched. `PrepareWorker` passes the struct by value into the
worker snapshot at the next `ExecuteNextVehicleSolvers`, so the write takes effect one frame later and is never
overwritten by the apply step (the `ManualControlInputs` are not part of the worker's result). That is why these
controls do not need a solver-window queue (compare [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../../vehicles/change-vehicles-without-racing-the-solver/overview.md) article 5). See
[game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. Resolve the private field once and cache it; return "unsupported" instead of throwing when it is gone:

```csharp
// gatOS.GameMod/Game/Ksa/Actuators/ThrottleActuator.cs L17-18
    private static readonly FieldInfo? ManualInputsField =
        typeof(Vehicle).GetField("_manualControlInputs", BindingFlags.Instance | BindingFlags.NonPublic);
```

2. **Box, mutate, write back.** The struct is a value type; reflection returns a boxed copy, so set the field on the
   box and write the box back:

```csharp
// gatOS.GameMod/Game/Ksa/Actuators/ThrottleActuator.cs L23-41
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

3. For RCS, replace **only** the translation (or only the rotation) bits and encode body-axis signs: +X forward, +Y
   right, +Z down; rotation +X roll right, +Y pitch up, +Z yaw right. The two compose:

```csharp
// gatOS.GameMod/Game/Ksa/Actuators/TranslateActuator.cs L37-61
    internal static CommandResult SetTranslation(Vehicle vehicle, IReadOnlyList<double> axes)
    {
        // Re-validate through the shared game-free rule: the HTTP/MQTT command paths reach here
        // without the 9p control-file parse, so arity/finiteness are not pre-checked on that route.
        if (TranslateRules.Validate(axes) is { } error)
            return new CommandResult(CommandOutcome.Invalid, error);
        if (ManualInputsField is null)
            return new CommandResult(CommandOutcome.Unsupported, "manual control inputs field not found in this build");

        // _manualControlInputs is a struct: box it, mutate the field, write the box back.
        var inputs = ManualInputsField.GetValue(vehicle);
        if (inputs is null)
            return new CommandResult(CommandOutcome.Fault, "manual control inputs unavailable");

        var flagsField = inputs.GetType().GetField("ThrusterCommandFlags",
            BindingFlags.Instance | BindingFlags.Public);
        if (flagsField is null)
            return new CommandResult(CommandOutcome.Unsupported, "ThrusterCommandFlags field not found in this build");

        // Replace only the translation bits; any manual rotation bits stay untouched.
        var current = (ThrusterMapFlags)flagsField.GetValue(inputs)!;
        flagsField.SetValue(inputs, (current & ~AllTranslation) | Encode(axes));
        ManualInputsField.SetValue(vehicle, inputs);
        return CommandResult.Ok;
    }
```

```csharp
// gatOS.GameMod/Game/Ksa/Actuators/TranslateActuator.cs L68-78
    private static ThrusterMapFlags Encode(IReadOnlyList<double> axes)
    {
        var flags = ThrusterMapFlags.None;
        flags |= axes[0] > 0 ? ThrusterMapFlags.TranslateForward
            : axes[0] < 0 ? ThrusterMapFlags.TranslateBackward : ThrusterMapFlags.None;
        flags |= axes[1] > 0 ? ThrusterMapFlags.TranslateRight
            : axes[1] < 0 ? ThrusterMapFlags.TranslateLeft : ThrusterMapFlags.None;
        flags |= axes[2] > 0 ? ThrusterMapFlags.TranslateDown
            : axes[2] < 0 ? ThrusterMapFlags.TranslateUp : ThrusterMapFlags.None;
        return flags;
    }
```

4. Commands **latch** until rewritten; write `0 0 0` to stop.
5. To see the throttle on stock gauges, the same write moves the displayed throttle (`GetManualThrottle()` reads the
   field).
6. Remember `RCSMode` and the attitude hold gate the flags; set `RCSMode` in the solver window if you need it
   (gatOS `ctl/rcs_mode`).

## Complete example

- [gatOS.GameMod/Game/Ksa/Actuators/ThrottleActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/ThrottleActuator.cs)[^gatos-throttleactuator], [gatOS.GameMod/Game/Ksa/Actuators/TranslateActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/TranslateActuator.cs)[^gatos-translateactuator], [gatOS.GameMod/Game/Ksa/Actuators/RotateActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/RotateActuator.cs)[^gatos-rotateactuator]

## Variations and gotchas

- **Held-input clear.** For the controlled vehicle `PrepareWorker` calls `ClearHeldPlayerInput()` under the conditions
  above; that clears flags and `_engineFlags`, so a latched translate on the *player's* craft can vanish at 30x warp.
- **Auto burn.** While `FlightComputer.BurnMode == Auto`, `PrepareWorker` clears `EngineOn`; hold Manual to command
  engines yourself.
- **Reflection risk.** One private field name; the actuator reports `Unsupported` if KSA renames it.
- **Throttle floor.** Values below `GetMinThrottle()` are clamped up at the next `PrepareWorker`.

## See also

- [Overview](overview.md), [Article 1](command-engines-throttle-and-rcs-from-code-1.md), [Article 4](command-engines-throttle-and-rcs-from-code-4.md)
- [gatos/manual-throttle-and-rcs-inputs](../../../../mods/gatos/features/manual-throttle-and-rcs-inputs.md)
- [Take control of another vehicle: switch the controlled craft or autopilot it from a mod](../../vehicles/take-control-of-another-vehicle/overview.md), [Ignite engines, activate the next stage, fire a decoupler or undock from code](../../staging-and-engines/stage-a-vehicle-from-code/overview.md)

[^ksa-manualcontrolinputs]: current/decomp/KSA/ManualControlInputs.cs (decompiled, build 2026.10.10.5554)
[^gatos-throttleactuator]: gatOS: gatOS.GameMod/Game/Ksa/Actuators/ThrottleActuator.cs
[^ksa-flightcomputer]: current/decomp/KSA/FlightComputer.cs (decompiled, build 2026.10.10.5554)
[^gatos-rotateactuator]: gatOS: gatOS.GameMod/Game/Ksa/Actuators/RotateActuator.cs
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^ksa-thrustermapflags]: current/decomp/KSA/ThrusterMapFlags.cs (decompiled, build 2026.10.10.5554)
[^gatos-translateactuator]: gatOS: gatOS.GameMod/Game/Ksa/Actuators/TranslateActuator.cs
