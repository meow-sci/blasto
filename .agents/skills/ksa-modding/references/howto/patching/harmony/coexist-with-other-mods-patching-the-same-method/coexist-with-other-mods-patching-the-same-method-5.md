---
type: How-To
title: One worker-side postfix merging every writer
description: Own exactly one postfix on FlightComputer.ComputeControl, run every actuator writer from it in a fixed order, and merge their wake-up and actuator requests into the stock output without clearing or raising what stock set
category: patching
group: harmony
topic: coexist-with-other-mods-patching-the-same-method
approach: 5
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/flight-computer-command-sink]
techniques: [harmony-postfix, ref-arguments]
hooks: [harmony-postfix KSA.FlightComputer.ComputeControl]
lifecycle: [physics:vehicle-solver]
game_types: [KSA.FlightComputer, KSA.FlightComputerOutput, KSA.PhysicsBubble, KSA.VehicleUpdateState]
tags: [harmony, postfix, command-sink, worker-thread, flight-computer, merge]
---

KSA-AdvancedFlightComputer has two features that write actuator commands every control step: the RCS executor fires
thruster pulses and zeroes engine commands, and powered guidance sets gimbal angles. Both must write after stock has
computed its own control, on the same worker, into the same output struct. Instead of two postfixes on the same method,
AFC owns exactly one: `VehicleCommandSink` calls each writer in a fixed order and merges what they ask of the
physics step. One patch, one order, one merge rule, no dependence on how two postfixes happen to be ordered.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by AFC in
[One ComputeControl postfix as an actuator command sink](../../../../mods/ksa-advancedflightcomputer/features/flight-computer-command-sink.md).

## Background: how the game does it

`FlightComputer.ComputeControl` takes its three arguments by reference, starts by clearing the output, and is called by
`PhysicsBubble` in three places on the solver's copy of the flight computer:

```csharp
// current/decomp/KSA/FlightComputer.cs L400-L404
public void ComputeControl(ref readonly FlightComputerNavigation nav, ref readonly ManualControlInputs inputs, ref FlightComputerOutput outputs)
{
	outputs.Clear();
	IsThrottleGLoadLimited = false;
	Ctrl2Asmb = nav.Ctrl2Body;
```

Source: [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L400-L404)[^ksa-flightcomputer]

`PhysicsBubble` reads `AnyActuatorCommanded` and `NextWakeupDeltaTime` from the output: an actuator command or a wake-up
sooner than the step takes the vehicle off rails, and inside a full-physics step the flight computer is re-run at the
later of the next grid point and the earliest requested wake-up ([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1264-L1290)[^ksa-physicsbubble],
[current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1404-L1426)).

## Lifecycle and timing

`ComputeControl` runs on the vehicle solver worker threads (`physics:vehicle-solver`), possibly concurrently for
different vehicles (one worker per physics bubble): per substep segment during full physics, in the pre-step when a
wake is due, and once per segment for a lone vehicle on an analytic segment ([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1277),
[current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1414), [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1794)). The postfix may touch only the `FlightComputer` it is
given (the worker's copy) and the output struct. Main-thread state reaches the writers through per-vehicle command
objects that the main thread publishes before dispatch. See [Run code inside the physics step on worker threads](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md) and
[Worker-side postfix merging commands from the main thread](../patch-methods-that-run-on-background-threads/patch-methods-that-run-on-background-threads-3.md).

## Steps

1. **Attach to a method whose arguments are all by reference.** `ComputeControl(ref readonly ..., ref readonly ...,
   ref ...)` is patched with an explicit `ArgumentType.Ref` for each parameter; the postfix may declare the `ref
   readonly` ones as `in` and takes `ref FlightComputerOutput outputs` to write into stock's struct:

```csharp
// AdvancedFlightComputer/Core/VehicleCommandSink.cs L79-L87
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

Source: [AdvancedFlightComputer/Core/VehicleCommandSink.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/VehicleCommandSink.cs#L79-L87)[^ksa-advancedflightcomputer-vehiclecommandsink]

2. **Keep per-call state on the stack.** Vehicle workers run in parallel, so nothing is shared between calls. A
   `ref struct Receipt` records whether anything was commanded and the earliest wake-up requested:

```csharp
// AdvancedFlightComputer/Core/VehicleCommandSink.cs L20-L40
/// <summary>
/// Records actuator writes and the requested time to the next control step, in seconds.
/// The game's minimum control interval still limits when that step can run.
/// </summary>
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

Source: [AdvancedFlightComputer/Core/VehicleCommandSink.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/VehicleCommandSink.cs#L20-L40)

3. **Call every writer in a fixed order, each isolated.** Each is gated by its enable flag (from
   [One shared tick with per-feature enable flags set from TryApply results](coexist-with-other-mods-patching-the-same-method-4.md)) and wrapped in its own `try`/`catch` with a once-per-exception-type warning, so one failing writer
   cannot stop the other or break the worker:

```csharp
// AdvancedFlightComputer/Core/VehicleCommandSink.cs L42-L71
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
        catch (Exception ex)
        {
            LogHelper.WarnOnce($"command-sink-rcs:{ex.GetType().Name}",
                $"[AFC] RCS command writer failed on the vehicle worker: {ex}");
        }
    }

    if (SharedVehicleHooks.GuidanceEnabled)
    {
        try
        {
            KsaGimbalControl.OnComputeControl(fc, ref outputs, ref receipt);
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce($"command-sink-guidance:{ex.GetType().Name}",
                $"[AFC] Guidance gimbal writer failed on the vehicle worker: {ex}");
        }
    }
```

Source: [AdvancedFlightComputer/Core/VehicleCommandSink.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/VehicleCommandSink.cs#L42-L71)

4. **Merge; never overwrite what stock set.** `AnyActuatorCommanded` is only ever raised, and
   `NextWakeupDeltaTime` is only ever lowered, so a stock request in the same call survives:

```csharp
// AdvancedFlightComputer/Core/VehicleCommandSink.cs L73-L77
    if (receipt.Commanded)
        outputs.AnyActuatorCommanded = true;
    if (receipt.WakeupSec < outputs.NextWakeupDeltaTime)
        outputs.NextWakeupDeltaTime = receipt.WakeupSec;
}
```

Source: [AdvancedFlightComputer/Core/VehicleCommandSink.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/VehicleCommandSink.cs#L73-L77)

5. **Install it as a class patch in the right owner.** The sink's `ApplyPatches` is `CreateClassProcessor(typeof(
   ComputeControlPatch)).Patch()` under the `Core` feature owner:

```csharp
// AdvancedFlightComputer/Core/VehicleCommandSink.cs L15-L18
internal static void ApplyPatches(Harmony harmony)
{
    harmony.CreateClassProcessor(typeof(ComputeControlPatch)).Patch();
}
```

Source: [AdvancedFlightComputer/Core/VehicleCommandSink.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/VehicleCommandSink.cs#L15-L18)

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Core/VehicleCommandSink.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/VehicleCommandSink.cs) | The single postfix, receipt, writers, merge |

## Variations and gotchas

- **`ref readonly` is patched as `ArgumentType.Ref`.** A postfix may declare it `in`.
- **Never reset `AnyActuatorCommanded` to false or raise `NextWakeupDeltaTime`.** Stock may have set them for its own
  reasons in the same call.
- **The worker copy is not private to the step.** `Vehicle.PrepareWorker` fills it from the vehicle with
  `FlightComputer.CopyFrom`, and `UpdateFromTaskResultsUnsynchronized` copies it back at the next
  `ApplyVehicleSolvers`; a main-thread write made after dispatch is lost. `FlightComputerOutput` is not copied
  back; it is consumed by the same step ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2525-L2535)[^ksa-vehicle]).
- **Exceptions on the worker abort the bubble's step.** Keep the writers in `try`/`catch`.
- **One owner for the method.** When other mods also postfix `ComputeControl`, theirs run in priority order around
  this one; the sink's contract is only that its own writers run in one fixed order inside it.

## See also

- [Priority.First prefixes and Priority.Last finalizers around shared seams](coexist-with-other-mods-patching-the-same-method-1.md), [Anchors that one mod can consume (and what the next transpiler then sees)](coexist-with-other-mods-patching-the-same-method-2.md), [Idempotent shared installs with a registration API](coexist-with-other-mods-patching-the-same-method-3.md), [One shared tick with per-feature enable flags set from TryApply results](coexist-with-other-mods-patching-the-same-method-4.md)
- [Coexisting with other mods that patch the same method](overview.md)
- [One ComputeControl postfix as an actuator command sink](../../../../mods/ksa-advancedflightcomputer/features/flight-computer-command-sink.md)
- [Patching game methods that run on worker and pool threads](../patch-methods-that-run-on-background-threads/overview.md)
- Related task: drive-the-flight-computer-and-attitude-modes-from-a-mod

[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^ksa-advancedflightcomputer-vehiclecommandsink]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Core/VehicleCommandSink.cs
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
