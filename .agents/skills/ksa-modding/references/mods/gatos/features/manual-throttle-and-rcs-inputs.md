---
type: Mod Feature
title: Manual throttle and RCS inputs
description: Setting manual throttle and RCS translate/rotate commands by reflecting into the private Vehicle._manualControlInputs struct, the same field KSA's keyboard handlers write and the solver copies each step
mod: gatos
category: vehicles-and-parts
techniques: [reflection, command-queue]
hooks:
  - reflection KSA.Vehicle._manualControlInputs
  - harmony-prefix KSA.GameViewport.OnFrame
  - starmap StarMapBeforeGui
lifecycle: [frame:viewports, frame:draw-ui, frame:dispatch-solvers]
game_types:
  - KSA.Vehicle
  - KSA.ManualControlInputs
  - KSA.ThrusterMapFlags
  - KSA.FlightComputer
  - KSA.FlightComputerRCSMode
tags: [control, throttle, rcs, reflection, thruster-flags, flight-computer]
---

KSA exposes `Vehicle.GetManualThrottle()` and `Vehicle.GetThrusterFlags()` but no setter for
either. Both values live in a private struct field, `Vehicle._manualControlInputs`, that the
keyboard handlers write and the vehicle solver copies at every dispatch. gatOS sets
`ctl/throttle`, `ctl/translate` and `ctl/rotate` by reflecting into that struct, which makes a
script's input indistinguishable from a held key.

## How it works

`ManualControlInputs` is a struct with public fields `EngineOn`, `EngineThrottle`,
`ThrusterCommandFlags`, `Sprint`, `GrabHeld` ([current/decomp/KSA/ManualControlInputs.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ManualControlInputs.cs)).
Because it is a value type, reflection must box it, set the inner field on the box, and write the
box back ([gatOS.GameMod/Game/Ksa/Actuators/ThrottleActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/ThrottleActuator.cs#L17-L41)):

1. `typeof(Vehicle).GetField("_manualControlInputs", NonPublic | Instance)`, cached in a static.
2. `GetValue(vehicle)` returns a boxed copy.
3. `GetField("EngineThrottle")` (or `ThrusterCommandFlags`) `.SetValue(box, value)`.
4. `ManualInputsField.SetValue(vehicle, box)`.

If either field is missing the actuator returns `Unsupported` (EOPNOTSUPP) instead of throwing.

**Thruster flags.** `ThrusterMapFlags` is a bit set: rotation bits `RollRight`/`RollLeft`,
`PitchUp`/`PitchDown`, `YawRight`/`YawLeft` and translation bits `TranslateForward`/`Backward`,
`Right`/`Left`, `Down`/`Up` ([current/decomp/KSA/ThrusterMapFlags.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ThrusterMapFlags.cs)). `ctl/translate x y z`
encodes body-axis signs as +X forward, +Y right, +Z down and replaces only the translation bits;
`ctl/rotate` encodes +X roll right, +Y pitch up, +Z yaw right and replaces only the rotation bits,
so the two compose ([gatOS.GameMod/Game/Ksa/Actuators/TranslateActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/TranslateActuator.cs#L37-L78),
[gatOS.GameMod/Game/Ksa/Actuators/RotateActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/RotateActuator.cs#L64-L82)). Commands latch until rewritten
with `0 0 0`.

**How the flight computer consumes them.** `FlightComputer.ComputeRcsControl` reads
`inputs.ThrusterCommandFlags`, cancels opposing commands, and zeroes everything when
`RCSMode != Enabled`; an active attitude hold strips the rotation bits (`WithNoRotation()`), so
`ctl/rotate` has authority only in manual attitude mode ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L742-L800)).
gatOS therefore also exposes `ctl/rcs_mode`, written in the solver phase (see
[Solver-phase drain for flight computer setpoints](solver-phase-command-drain.md)).

## Lifecycle and timing

- Written in the Frame-phase drain (main-viewport prefix or `StarMapBeforeGui`), after the
  current step was dispatched.
- `Vehicle.PrepareWorker` passes `_manualControlInputs` by value into
  `_threadWorkerUpdateState.PrepareFromVehicle(...)` at the next `Universe.ExecuteNextVehicleSolvers`,
  so the write takes effect one frame later (`frame:dispatch-solvers`) and is never overwritten by
  the apply step ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423)). That is why these controls do
  not need the solver-phase queue.
- In the same method KSA clamps `EngineThrottle` to `[GetMinThrottle(), 1]`, applies the
  throttle-up/down key rates, and, for the controlled vehicle, calls `ClearHeldPlayerInput()` when
  the window is inactive, ImGui wants the keyboard, or warp exceeds 30x.

## Key code

| Path | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Actuators/ThrottleActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/ThrottleActuator.cs) | boxed-struct reflection write of `EngineThrottle` |
| [gatOS.GameMod/Game/Ksa/Actuators/TranslateActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/TranslateActuator.cs) | translation bits, preserves rotation bits, read-back via `GetThrusterFlags()` |
| [gatOS.GameMod/Game/Ksa/Actuators/RotateActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/RotateActuator.cs) | rotation bits, preserves translation bits |
| [gatOS.SimFs/Commands/TranslateRules.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/TranslateRules.cs) | shared game-free validation (arity, finiteness) |

## Snippets

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

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Vehicle._manualControlInputs` (private) | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L239-L239) | reflection target |
| `ManualControlInputs.EngineThrottle`, `ThrusterCommandFlags` | [current/decomp/KSA/ManualControlInputs.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ManualControlInputs.cs) | fields written |
| `Vehicle.GetManualThrottle()`, `GetThrusterFlags()` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1259-L1262), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6089-L6092) | public read-backs |
| `Vehicle.PrepareWorker`, `ClearHeldPlayerInput()` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6094-L6099) | capture point and the game's own flag clearing |
| `FlightComputer.ComputeRcsControl` | [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L742-L800) | consumer, RCS master gate, rotation stripping |
| `ThrusterMapFlags` | [current/decomp/KSA/ThrusterMapFlags.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ThrusterMapFlags.cs) | bit layout |

## Pitfalls and version notes

- Since rev 5128 KSA clears latched thruster flags on vessel switch (`Program.ControlledVehicle`
  setter), window focus loss (`Program.OnFocus`), camera-mode switch (`GameViewport`), and every
  dispatch of the controlled vehicle while ImGui holds keyboard focus or warp exceeds 30x. A latched `ctl/translate` can
  therefore be dropped by the game; throttle and `EngineOn` are not affected.
- Since rev 5143 `RCSMode != Enabled` zeroes manual flags entirely; check `ctl/rcs_mode` first.
- Reflection on a struct field without writing the box back silently does nothing.
- `ManualControlInputs` and the field name are unchanged in 5554; gatOS rates the anchor High churn
  because it is a private member.

## See also

- [Ignition, staging and module actuators](ignition-staging-and-module-actuators.md)
- [Solver-phase drain for flight computer setpoints](solver-phase-command-drain.md)
- [Game-thread command queue](game-thread-command-queue.md)
- [gatOS overview](../overview.md)
- [Write throttle and RCS flags by reflection into Vehicle._manualControlInputs](../../../howto/flight-and-orbits/autopilot/execute-burns-with-rcs-and-throttle-inputs/execute-burns-with-rcs-and-throttle-inputs-3.md) - how-to article demonstrating this feature
- [Set manual throttle and RCS command flags by writing the boxed manual-control struct](../../../howto/vehicles-and-parts/modules-and-resources/command-engines-throttle-and-rcs-from-code/command-engines-throttle-and-rcs-from-code-2.md) - how-to article demonstrating this feature

