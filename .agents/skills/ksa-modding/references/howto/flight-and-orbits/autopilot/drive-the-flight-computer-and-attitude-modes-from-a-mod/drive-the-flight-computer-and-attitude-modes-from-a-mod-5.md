---
type: How-To
title: Write actuator commands from one worker-thread postfix on FlightComputer.ComputeControl
description: Own exactly one postfix on ComputeControl that runs every actuator writer in a fixed order on the vehicle worker, with immutable commands and a merged receipt for the physics step
category: flight-and-orbits
group: autopilot
topic: drive-the-flight-computer-and-attitude-modes-from-a-mod
approach: 5
difficulty: advanced
prerequisites: [harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/flight-computer-command-sink]
techniques: [harmony-postfix]
hooks: [harmony-postfix KSA.FlightComputer.ComputeControl]
lifecycle: [physics:vehicle-solver]
game_types: [KSA.FlightComputer, KSA.FlightComputerOutput, KSA.FlightComputerNavigation, KSA.PhysicsBubble]
tags: [compute-control, worker-thread, actuators, rcs, gimbal, receipt, harmony-postfix]
---

Outcome: several mod features (RCS pulses, engine gimbals) write their commands after stock has computed its own control, on the same worker and in the same output struct the physics step consumes, without stacking competing postfixes.
KSA-AdvancedFlightComputer owns exactly one, `VehicleCommandSink`.[One ComputeControl postfix as an actuator command sink](../../../../mods/ksa-advancedflightcomputer/features/flight-computer-command-sink.md) Prerequisite: Harmony.

## Background: how the game does it

`ComputeControl(ref readonly FlightComputerNavigation, ref readonly ManualControlInputs, ref FlightComputerOutput)` starts with `outputs.Clear()`, so every field is rebuilt each control step.[^ksa-flightcomputer] [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L400-L403) In `PhysicsBubble`, `AnyActuatorCommanded` and a
`NextWakeupDeltaTime` shorter than the step take the vehicle off rails, and inside a full-physics step the flight computer is re-run at `max(next grid point of minFlightComputerDeltaTime, now + min NextWakeupDeltaTime)`.[^ksa-physicsbubble] [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1264-L1290) [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1405-L1426)
A writer that wants to end a pulse precisely asks for an early wake; the game's minimum control interval bounds how early. The `FlightComputerOutput` is consumed by the same step's actuators and is not copied back.

## Lifecycle and timing

`ComputeControl` is called by `PhysicsBubble` on the vehicle solver worker threads: per substep segment during full physics, in the pre-step when a wake is due, and once per segment for a lone vehicle in `ApplySingleVehicleMotion`. The postfix therefore runs in `physics:vehicle-solver`, off the main thread, possibly concurrently for different vehicles,
and may touch only the `FlightComputer` instance it was given (the worker's copy, `UpdateData.NewFlightComputer`) and the output struct. Main-thread state reaches the writers through per-vehicle command objects the main thread publishes before the step is dispatched. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Reach the physics worker through the references FlightComputer.CopyFrom shares](../../../../mods/ksa-advancedflightcomputer/features/flight-computer-worker-copy-channels.md).

## Steps

1. **Patch with by-ref parameters.** All three parameters are by-ref, so the attribute passes `ArgumentType.Ref` for each and the postfix takes `ref FlightComputerOutput outputs`:

```csharp
// AdvancedFlightComputer/Core/VehicleCommandSink.cs L42-L52, L73-L88
    internal static void Run(FlightComputer fc, in FlightComputerNavigation nav,
                             ref FlightComputerOutput outputs)
    {
        Receipt receipt = new();

        if (SharedVehicleHooks.RcsEnabled)
        {
            try
            {
                RcsComputeControlPatch.Command(fc, in nav, ref outputs, ref receipt);
            }
// ...
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

2. **Make a stack-only receipt per call** (`ref struct Receipt` with `Commanded` and the smallest requested wake-up interval); vehicle workers run in parallel, one per physics bubble, so nothing is shared between calls.
3. **Call each writer in a fixed order in its own try/catch** with a once-per-type warning, so one failing writer cannot stop another or break the worker: RCS first (it clears engine commands while an RCS burn runs), then guidance gimbals.
4. **Merge the receipt**: set `outputs.AnyActuatorCommanded = true` if any writer commanded (never clear it, so stock's flag survives) and lower `outputs.NextWakeupDeltaTime` (never raise it, so an earlier stock wake request is kept).
5. **Read your commands from immutable records** published by the main thread (see approach 4 and [Execute burns with RCS translation and throttle inputs from code](../execute-burns-with-rcs-and-throttle-inputs/overview.md)), keyed by an object `CopyFrom` shares.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Core/VehicleCommandSink.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/VehicleCommandSink.cs) | the sink: patch, receipt, writer dispatch, merge |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsComputeControlPatch.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsComputeControlPatch.cs) | RCS writer |
| [AdvancedFlightComputer/Features/Guidance/Control/GimbalControl.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/GimbalControl.cs) | guidance gimbal writer |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Core/VehicleCommandSinkTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Core/VehicleCommandSinkTest.cs) | headless test of dispatch and receipt merging |

## Variations and gotchas

- **Never allocate or lock** on the worker; do not touch ImGui or Vulkan.
- **The worker copy is not private to the step**: `PrepareWorker` fills it with `CopyFrom` and the next apply copies it back, so a field you change on the copy reaches the vehicle.
- **Two postfixes are not ordered by anything you control** (same priority across mods); one sink makes the order explicit.
- Do not read `Ctrl2Asmb` from the vehicle's copy; it is set on the worker and never copied back.

## See also

- [Overview](overview.md); [rate feedforward](drive-the-flight-computer-and-attitude-modes-from-a-mod-4.md); [navigation-target finalizer](drive-the-flight-computer-and-attitude-modes-from-a-mod-7.md)
- [One ComputeControl postfix as an actuator command sink](../../../../mods/ksa-advancedflightcomputer/features/flight-computer-command-sink.md)
- [Execute burns with RCS translation and throttle inputs from code](../execute-burns-with-rcs-and-throttle-inputs/overview.md)

[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
