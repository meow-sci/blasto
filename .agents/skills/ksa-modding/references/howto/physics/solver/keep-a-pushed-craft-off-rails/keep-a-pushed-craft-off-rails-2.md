---
type: How-To
title: Request wake or off-rails from the flight computer command sink
description: Let a mod actuator writer on FlightComputer.ComputeControl set AnyActuatorCommanded and an earlier wake-up time in the stock output, so the physics bubble takes the craft off rails and re-runs the flight computer on time
category: physics
group: solver
topic: keep-a-pushed-craft-off-rails
approach: 2
difficulty: advanced
prerequisites: [harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/flight-computer-command-sink]
techniques: [harmony-postfix]
hooks: [harmony-postfix KSA.FlightComputer.ComputeControl]
lifecycle: [physics:vehicle-solver, frame:dispatch-solvers]
game_types: [KSA.FlightComputer, KSA.FlightComputerOutput, KSA.PhysicsBubble]
tags: [physics, rails, flight-computer, wake, actuators, command-sink, harmony, postfix]
---

Outcome: a mod that commands actuators (RCS pulses, gimbal angles) takes a railed craft off rails and gets its
control step re-run exactly when it asks, without patching the physics bubble. Prerequisite: Harmony.
KSA-AdvancedFlightComputer (AFC) merges the requests of all its writers into the stock `FlightComputerOutput`.

## Background: how the game does it

The bubble runs the flight computer even for a railed craft, and reads its output to decide whether the craft
should stay on rails. In `ApplySingleVehicleMotion` [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1264-L1292):

```csharp
// current/decomp/KSA/PhysicsBubble.cs L1278-L1288
		if (flightComputerOutput.NextWakeupDeltaTime < window.DeltaTime)
		{
			situation = situation.WithOnRails(isOnRails: false);
		}
		if (vehicleState.FlightComputerOutput.AnyActuatorCommanded)
		{
			situation = situation.WithOnRails(isOnRails: false);
		}
		if (vehicleState.AnyActuatorActive())
		{
			situation = situation.WithOnRails(isOnRails: false);
		}
```

Inside a full-physics step the flight computer is re-run at the later of the next grid point of the minimum
flight-computer interval and `now + min(NextWakeupDeltaTime)` [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1403-L1432). And
`FlightComputer.ComputeControl` starts with `outputs.Clear()` [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L400-L403), so every
field is rebuilt each control step, which is why a mod must write them again each time.

## Lifecycle and timing

`ComputeControl` runs on the vehicle solver worker threads: per substep segment during full physics, in the
pre-step when a wake is due, and once per segment for a lone vehicle in `ApplySingleVehicleMotion`. The postfix
therefore runs in `physics:vehicle-solver`, off the main thread, possibly concurrently for different vehicles. It
may only touch the `FlightComputer` it was given (the worker's copy) and the output struct. Main-thread state
reaches the writers through per-vehicle immutable commands published before dispatch (see
[approach 6 of the worker-thread patterns](../../solver/write-worker-thread-safe-solver-patches/write-worker-thread-safe-solver-patches-6.md)).

| Request | Effect |
|---|---|
| `AnyActuatorCommanded = true` | A railed craft leaves rails; in full physics it skips the rails else-chain |
| `NextWakeupDeltaTime` lowered | The craft leaves rails if the value is shorter than the window; in full physics the control step is re-run at that time, bounded below by the game's minimum control interval |

## Steps

1. Own exactly one postfix on `FlightComputer.ComputeControl`. All three parameters are `ref`/`ref readonly`, so the
   patch states `ArgumentType.Ref` for each:

```csharp
// AdvancedFlightComputer/Core/VehicleCommandSink.cs L79-L88
    [HarmonyPatch(typeof(FlightComputer), nameof(FlightComputer.ComputeControl),
        new Type[] { typeof(FlightComputerNavigation), typeof(ManualControlInputs), typeof(FlightComputerOutput) },
        new ArgumentType[] { ArgumentType.Ref, ArgumentType.Ref, ArgumentType.Ref })]
    private static class ComputeControlPatch
    {
        static void Postfix(FlightComputer __instance, in FlightComputerNavigation nav,
                            ref FlightComputerOutput outputs)
            => Run(__instance, in nav, ref outputs);
    }
```

2. Have each writer record its request in a stack-only receipt instead of writing the output flags itself. `Command()`
   says "I wrote an actuator"; `Wake(seconds)` says "run me again in this long", keeping the smallest request:

```csharp
// AdvancedFlightComputer/Core/VehicleCommandSink.cs L24-L40
    internal ref struct Receipt
    {
        internal bool Commanded;
        internal double WakeupSec = double.PositiveInfinity;

        public Receipt()
        {
        }

        internal void Command() => Commanded = true;

        internal void Wake(double seconds)
        {
            if (seconds < WakeupSec)
                WakeupSec = seconds;
        }
    }
```

3. Merge into the stock output. Never clear `AnyActuatorCommanded` and never raise `NextWakeupDeltaTime`: stock may
   have set them in the same call. After merging, the bubble sees an actuator commanded and takes the craft off
   rails on this decision:

```csharp
// AdvancedFlightComputer/Core/VehicleCommandSink.cs L73-L77
        if (receipt.Commanded)
            outputs.AnyActuatorCommanded = true;
        if (receipt.WakeupSec < outputs.NextWakeupDeltaTime)
            outputs.NextWakeupDeltaTime = receipt.WakeupSec;
    }
```

4. Keep each writer in its own `try/catch` with a once-per-type warning, so one failing writer cannot stop the
   others or abort the bubble's step.

5. Ask for an early wake to end a pulse precisely: an RCS writer that wants a pulse to stop at a given instant
   passes that interval to `Wake`; the game's minimum control interval still bounds how early.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Core/VehicleCommandSink.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/VehicleCommandSink.cs) | The sink: patch, receipt, writer dispatch, merge |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsComputeControlPatch.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsComputeControlPatch.cs) | RCS writer: thruster pulses, engine zeroing, burn timing |
| [AdvancedFlightComputer/Features/Guidance/Control/GimbalControl.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/GimbalControl.cs) | Guidance gimbal writer |

## Variations and gotchas

- This only works for features that actually write actuators. A mod that applies a velocity delta (a blast, a
  scripted push) has no actuator to command, and setting `AnyActuatorCommanded` for no real actuator is a
  hack that other code may read; use [approach 1](keep-a-pushed-craft-off-rails-1.md) instead.
- The request must be present on every `ComputeControl` call while the push lasts, because `outputs.Clear()`
  runs at the start of each.
- The instance given to the postfix is the worker's copy of the flight computer; main-thread writes to the real
  one are overwritten at the next apply (see [approach 6](../../solver/write-worker-thread-safe-solver-patches/write-worker-thread-safe-solver-patches-6.md)).
- Exceptions on the worker abort the bubble's step.
- A burn in progress holds the craft off rails only as long as the engines or actuators are active; check the
  situation after the burn ends if you rely on it.

## See also

- [Overview and comparison](overview.md)
- [Approach 1: the wake-check postfix](keep-a-pushed-craft-off-rails-1.md)
- Mod feature: [AFC flight computer command sink](../../../../mods/ksa-advancedflightcomputer/features/flight-computer-command-sink.md)
- [Write worker-thread-safe patches inside the vehicle solver](../../solver/write-worker-thread-safe-solver-patches/overview.md)
