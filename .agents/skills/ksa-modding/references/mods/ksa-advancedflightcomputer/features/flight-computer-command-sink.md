---
type: Mod Feature
title: One ComputeControl postfix as an actuator command sink
description: A single postfix on FlightComputer.ComputeControl runs every mod-owned actuator writer on the vehicle worker and merges their wake and off-rails requests into the stock output
mod: ksa-advancedflightcomputer
category: patching
techniques: [harmony-postfix]
hooks: [harmony-postfix KSA.FlightComputer.ComputeControl]
lifecycle: [physics:vehicle-solver]
game_types:
  - KSA.FlightComputer
  - KSA.FlightComputerOutput
  - KSA.FlightComputerNavigation
  - KSA.ManualControlInputs
  - KSA.PhysicsBubble
tags: [flight-computer, actuators, rcs, gimbal, worker-thread, off-rails]
---

AFC has two features that write actuator commands every control step: the RCS executor fires
thruster pulses and zeroes engine commands, and powered guidance sets engine gimbal angles. Both must
write after stock has computed its own control, on the same worker and in the same output struct the
physics step consumes. Instead of two postfixes on the same method, AFC owns exactly one,
`VehicleCommandSink`, that calls each writer in a fixed order and merges what they ask of the
physics step.

## How it works

1. **Hook.** A postfix on `FlightComputer.ComputeControl(ref readonly FlightComputerNavigation, ref readonly ManualControlInputs, ref FlightComputerOutput)`.
   All three parameters are by-ref, so the `[HarmonyPatch]` passes `ArgumentType.Ref` for each and
   the postfix takes `ref FlightComputerOutput outputs` to write into stock's struct
   ([AdvancedFlightComputer/Core/VehicleCommandSink.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/VehicleCommandSink.cs#L79-L87)).
2. **Receipt.** Each call creates a stack-only `ref struct Receipt` with `Commanded` and the
   smallest requested wake-up interval. Vehicle workers run in parallel (one per physics bubble), so
   nothing is shared between calls ([AdvancedFlightComputer/Core/VehicleCommandSink.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/VehicleCommandSink.cs#L20-L40)).
3. **Writers.** If the RCS feature is enabled, `RcsComputeControlPatch.Command` writes thruster
   `CommandPulseTime`, clears engine `CommandThrottle`/`CommandBurnTime` while an RCS burn runs and
   updates the burn target timing; then, if guidance is enabled, `KsaGimbalControl.OnComputeControl`
   writes gimbal `CommandY`/`CommandZ`. Each writer is in its own try/catch with a once-per-type
   warning, so one failing writer cannot stop the other or break the worker
   ([AdvancedFlightComputer/Core/VehicleCommandSink.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/VehicleCommandSink.cs#L42-L77)).
4. **Merge.** After the writers: `outputs.AnyActuatorCommanded = true` if any writer commanded (never
   cleared, so stock's own flag survives), and `outputs.NextWakeupDeltaTime` lowered to the receipt's
   interval if it is earlier (never raised, so an earlier stock wake request is kept).

What those two fields do in the engine:

- `ComputeControl` starts with `outputs.Clear()`, so every field is rebuilt each control step
  ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L400-L403)).[^ksa-flightcomputer]
- In `PhysicsBubble`, `AnyActuatorCommanded` and a `NextWakeupDeltaTime` shorter than the step take
  the vehicle off rails, and inside a full-physics step the flight computer is re-run at
  `max(next grid point of minFlightComputerDeltaTime, now + min NextWakeupDeltaTime)`
  ([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1264-L1290), [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1405-L1426)).[^ksa-physicsbubble]
  An RCS writer that wants to end a pulse precisely therefore asks for an early wake; the game's
  minimum control interval still bounds how early.

## Lifecycle and timing

`ComputeControl` is called by `PhysicsBubble` on the vehicle solver worker threads: per substep
segment during full physics ([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1405-L1416)), in the pre-step when a wake is due
([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1782-L1798)), and once per segment for a lone vehicle in
`ApplySingleVehicleMotion` ([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1264-L1277)). The postfix therefore runs in
`physics:vehicle-solver`, off the main thread, possibly concurrently for different vehicles. It may
only touch the `FlightComputer` instance it was given (the worker's copy, `UpdateData.NewFlightComputer`)
and the output struct. Main-thread state reaches the writers through per-vehicle command objects
that the main thread publishes before the step is dispatched (see
[RCS burn execution](rcs-burn-execution.md), [guidance attitude and gimbal actuation](guidance-attitude-and-gimbal-actuation.md)
and [flight-computer worker copy channels](flight-computer-worker-copy-channels.md)).

The worker copy is not private to the step: `Vehicle.PrepareWorker` fills it from the vehicle with
`FlightComputer.CopyFrom` before the job is queued, and `Vehicle.UpdateFromTaskResultsUnsynchronized`
copies it back over the vehicle's flight computer with `CopyFrom` at the next `ApplyVehicleSolvers`
([current/decomp/KSA/VehicleUpdateData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateData.cs#L84-L104), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2530)).[^ksa-vehicle] The
`FlightComputerOutput` struct is not copied back; it is consumed by the same step's actuators.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Core/VehicleCommandSink.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/VehicleCommandSink.cs) | The sink: patch, receipt, writer dispatch, merge |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsComputeControlPatch.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsComputeControlPatch.cs) | RCS writer (thruster pulses, engine zeroing, burn timing) |
| [AdvancedFlightComputer/Features/Guidance/Control/GimbalControl.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/GimbalControl.cs) | Guidance gimbal writer |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Core/VehicleCommandSinkTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Core/VehicleCommandSinkTest.cs) | Headless test of dispatch and receipt merging |

## Snippets

```csharp
// AdvancedFlightComputer/Core/VehicleCommandSink.cs L73-L88
        if (receipt.Commanded)
            outputs.AnyActuatorCommanded = true;
        if (receipt.WakeupSec < outputs.NextWakeupDeltaTime)
            outputs.NextWakeupDeltaTime = receipt.WakeupSec;
    }

    [HarmonyPatch(typeof(FlightComputer), nameof(FlightComputer.ComputeControl),
        new Type[] { typeof(FlightComputerNavigation), typeof(ManualControlInputs), typeof(FlightComputerOutput) },
        new ArgumentType[] { ArgumentType.Ref, ArgumentType.Ref, ArgumentType.Ref })]
    private static class ComputeControlPatch
    {
        static void Postfix(FlightComputer __instance, in FlightComputerNavigation nav,
                            ref FlightComputerOutput outputs)
            => Run(__instance, in nav, ref outputs);
    }
}
```

## Game APIs touched

| Member | Use |
|---|---|
| `FlightComputer.ComputeControl` ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L400)) | Hooked control step on the worker |
| `FlightComputerOutput.AnyActuatorCommanded`, `NextWakeupDeltaTime`, `Thrusters`, `Engines`, `Gimbals` ([current/decomp/KSA/FlightComputerOutput.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputerOutput.cs)) | Command and scheduling outputs |
| `PhysicsBubble` control loop ([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs)) | Consumer of the outputs; decides off-rails and re-run time |

## Pitfalls and version notes

- `ref readonly` parameters are patched as `ArgumentType.Ref`; a postfix may declare them `in`.
- Never reset `AnyActuatorCommanded` to false or raise `NextWakeupDeltaTime`: stock may have set
  them for its own reasons in the same call.
- The instance is the worker's flight computer copy, and `CopyFrom` writes every field it copies
  (including `BurnMode`, `AttitudeMode`, `AutoBurnCompleted`) back over the vehicle's flight computer
  at the next `ApplyVehicleSolvers`. That is how stock's own worker-side `BurnMode = Manual` reaches
  the main thread, and it also means a main-thread write made after dispatch is lost. Fields
  `CopyFrom` skips, such as `Ctrl2Asmb`, never come back. AFC writes only the output struct and the
  per-step burn timing from the sink, and changes settings on the main thread before the snapshot.
- Exceptions on the worker abort the bubble's step; keep the writers in try/catch.

## See also

- [Vehicle control ownership](vehicle-control-ownership.md)
- [RCS burn execution](rcs-burn-execution.md)
- [Guidance attitude and gimbal actuation](guidance-attitude-and-gimbal-actuation.md)
- [Post-solver vehicle tick](post-solver-vehicle-tick.md)
- [Flight-computer worker copy channels](flight-computer-worker-copy-channels.md)
- [Write actuator commands from one worker-thread postfix on FlightComputer.ComputeControl](../../../howto/flight-and-orbits/autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/drive-the-flight-computer-and-attitude-modes-from-a-mod-5.md) - how-to article demonstrating this feature
- [Run code inside the physics step on worker threads](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md) - how-to article demonstrating this feature
- [One worker-side postfix merging every writer](../../../howto/patching/harmony/coexist-with-other-mods-patching-the-same-method/coexist-with-other-mods-patching-the-same-method-5.md) - how-to article demonstrating this feature
- [Worker-side postfix merging commands from the main thread](../../../howto/patching/harmony/patch-methods-that-run-on-background-threads/patch-methods-that-run-on-background-threads-3.md) - how-to article demonstrating this feature
- [Request wake or off-rails from the flight computer command sink](../../../howto/physics/solver/keep-a-pushed-craft-off-rails/keep-a-pushed-craft-off-rails-2.md) - how-to article demonstrating this feature
- [Immutable commands published before the solver job is queued](../../../howto/physics/solver/write-worker-thread-safe-solver-patches/write-worker-thread-safe-solver-patches-6.md) - how-to article demonstrating this feature

[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
