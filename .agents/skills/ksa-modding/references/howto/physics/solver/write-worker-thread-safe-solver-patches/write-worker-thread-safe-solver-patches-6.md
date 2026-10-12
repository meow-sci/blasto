---
type: How-To
title: Immutable commands published before the solver job is queued
description: Run every mod actuator writer from one postfix on FlightComputer.ComputeControl, feed each from an immutable command published on the main thread before dispatch, and merge their wake and off-rails requests into the stock output
category: physics
group: solver
topic: write-worker-thread-safe-solver-patches
approach: 6
difficulty: advanced
prerequisites: [harmony]
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/flight-computer-command-sink
  - mods/ksa-advancedflightcomputer/features/flight-computer-worker-copy-channels
techniques: [harmony-postfix, harmony-prefix, reflection]
hooks: [harmony-postfix KSA.FlightComputer.ComputeControl, harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [physics:vehicle-solver, frame:dispatch-solvers, frame:apply-solvers]
game_types:
  - KSA.FlightComputer
  - KSA.FlightComputerOutput
  - KSA.PhysicsBubble
  - KSA.Vehicle
  - KSA.VehicleUpdateData
  - KSA.BurnPlan
tags: [physics, worker-thread, flight-computer, actuators, immutable, command-sink, conditionalweaktable]
---

Outcome: several features of one mod (RCS translation pulses, engine gimbal demand) command actuators from a
single worker-thread hook, each using a per-vehicle command the main thread prepared, without locks and
without two postfixes fighting over the same output struct. Prerequisite: Harmony. KSA-AdvancedFlightComputer
(AFC) demonstrates it.

## Background: how the game does it

The vehicle worker does not fly the `Vehicle`'s own flight computer: `Vehicle.PrepareWorker` ends with
`PrepareFromVehicle`, which runs `NewFlightComputer.CopyFrom(flightComputer)` in
`VehicleUpdateData.Prepare` [current/decomp/KSA/VehicleUpdateData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateData.cs#L84-L104), and the worker's `ComputeControl` receives
that copy. At the next `Universe.ApplyVehicleSolvers`, `Vehicle.UpdateFromTaskResultsUnsynchronized` copies it
back over the live one [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2435-L2544).[^ksa-vehicleupdatedata]

`FlightComputer.ComputeControl` [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L400-L420) starts with `outputs.Clear()`, so the
`FlightComputerOutput` is rebuilt each control step. In `PhysicsBubble`, `AnyActuatorCommanded` and a
`NextWakeupDeltaTime` shorter than the step take the vehicle off rails, and inside a full-physics step the
flight computer is re-run at the earlier of the next grid point and `now + NextWakeupDeltaTime`.[^ksa-physicsbubble]

`FlightComputer.CopyFrom` copies value fields by value but assigns `BurnPlan`, `VehicleConfig` and the loaded
`Burn` by reference, so the vehicle's flight computer and the worker's copy point at the same objects. That is
the key to the whole pattern.

## Lifecycle and timing

| Moment | Lifecycle | Thread | Flight-computer state |
|---|---|---|---|
| `ApplyVehicleSolvers` (postfix) | `frame:apply-solvers` | main | Worker copy just written back; writes from here reach the next step |
| `Vehicle.PrepareWorker` (prefix, then snapshot) | `frame:dispatch-solvers` | main | Last write for the next step; the copy is taken at the end of the method |
| `ComputeControl` and its callees | `physics:vehicle-solver` | worker | Worker copy; shared `BurnPlan`, `VehicleConfig`, `Burn` |
| UI, StarMap frame hooks, render | `frame:draw-ui` onwards | main | Writes are overwritten at the next apply |

The main thread publishes a command before `ExecuteNextVehicleSolvers` queues the job, so the publish
happens-before the worker's read. Commands are immutable records replaced by reference, so a reader never
needs a lock. See the [settled-window reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md) and the
[worker-thread approach](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md).

## Steps

1. Publish a command per vehicle from the main thread in the window before the snapshot. The command is an
   immutable record (`init`-only members) with one deliberate exception, a consumption receipt written through
   `Volatile`:

```csharp
// AdvancedFlightComputer/Features/RcsTranslation/RcsWorkerCommand.cs L7-L18
/// <summary>The driver publishes one command payload for the worker each tick while execution is active. The payload is immutable. Only its consumption receipt can change.</summary>
internal sealed class RcsWorkerCommand
{
    private int _consumed;
    internal bool WasConsumed => Volatile.Read(ref _consumed) != 0;
    internal void MarkConsumed() => Volatile.Write(ref _consumed, 1);

    /// <summary>The driver has taken over and translation pulses should fire once the ignition time and attitude gate allow.</summary>
    public required bool Active { get; init; }

    /// <summary>Construct the ignition time on the main thread because UniverseTime rejects NaN. A conversion failure inside the worker would interrupt the physics step.</summary>
    public UniverseTime IgnitionTime { get; init; }
```

   Anything that can throw (here `UniverseTime` construction) is done on the main thread, not on the worker.

2. Key the lookup on an object the copy shares by reference. A table keyed by the vehicle's `FlightComputer`
   never matches on the worker, because the worker holds a copy. `BurnPlan` and `VehicleConfigInfo` are shared:

```csharp
// AdvancedFlightComputer/Features/RcsTranslation/RcsWorkerCommand.cs L40-L59
/// <summary>FlightComputer.CopyFrom shares BurnPlan between the vehicle and worker flight computers. Use that shared reference as the command key because the flight computer instances differ.</summary>
internal static class RcsCommandChannel
{
    private static readonly ConditionalWeakTable<BurnPlan, RcsWorkerCommand> _commands = new();

    public static void Publish(BurnPlan plan, RcsWorkerCommand command)
        => _commands.AddOrUpdate(plan, command);

    public static void Clear(BurnPlan plan)
    {
        // Avoid taking the table write lock when this vehicle has no published command.
        if (_commands.TryGetValue(plan, out _))
            _commands.Remove(plan);
    }

    public static bool TryGet(BurnPlan plan, out RcsWorkerCommand command)
        => _commands.TryGetValue(plan, out command!);

    public static void Reset() => _commands.Clear();
}
```

3. Keep scratch buffers per vehicle, not per feature. Two guided vehicles run at once on two workers, so one
   shared array would interleave allocations. AFC's gimbal slot holds the volatile command plus the scratch
   that only the worker servicing that vehicle touches:

```csharp
// AdvancedFlightComputer/Features/Guidance/Control/GimbalControl.cs L46-L69
    public sealed class Slot
    {
        public volatile Command Cmd = Command.Off;

        // Scratch, reused so the allocation stays allocation-free on the hot path.
        // Touched only by the job thread servicing THIS vehicle.
        internal double[] Commands = [];
        internal double[] Thrusts = [];
        internal GimbalController[] Gimbals = [];

        /// <summary>Gimbals actually written on the last worker pass - 0 means the override is not reaching anything.</summary>
        public int AppliedCount;

        /// <summary>Diagnostics from the last Lsq allocation, for the UI readout.</summary>
        public TvcAllocationResult LastAllocation;
    }

    /// <summary>
    /// Per-vehicle state, keyed on the identity the apply side can actually see.
    ///  The FlightComputer handed to the postfix is NOT the live one - the worker runs on a copy - so reference-comparing the FlightComputer would never match.
    /// VehicleConfigInfo is the usable identity because FlightComputer.CopyFrom assigns it by REFERENCE rather than cloning.
    ///  This replaces a single _target field, and the gain is not only that two vehicles can be driven at once. With one target, a second vehicle engaging silently stole the first one's gimbals, and whether the first still flew depended on the order KSA happened to interleave prepare and compute across vehicles. A lookup cannot be pointed at the wrong vehicle.
    /// </summary>
    private static readonly ConditionalWeakTable<FlightComputer.VehicleConfigInfo, Slot> Slots = new();
```

4. Own exactly one postfix on `ComputeControl` and let it call each writer in a fixed order. All three
   parameters are by-ref, so the `[HarmonyPatch]` states `ArgumentType.Ref` for each and the postfix may take
   them as `in` or `ref`:

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

5. Give each call its own stack-only receipt, run each writer in its own `try/catch` with a once-per-type
   warning, and merge at the end. Never clear `AnyActuatorCommanded` and never raise `NextWakeupDeltaTime`,
   because stock may have set them in the same call:

```csharp
// AdvancedFlightComputer/Core/VehicleCommandSink.cs L24-L77
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

        if (receipt.Commanded)
            outputs.AnyActuatorCommanded = true;
        if (receipt.WakeupSec < outputs.NextWakeupDeltaTime)
            outputs.NextWakeupDeltaTime = receipt.WakeupSec;
    }
```

6. Write flight-computer settings only before the snapshot. Because `CopyFrom` overwrites every copied field at
   apply, AFC changes settings in exactly two places: a postfix on `ApplyVehicleSolvers` and a prefix on
   `Vehicle.PrepareWorker`. Guidance runs its per-vehicle step in the prefix, defers every panel action to it, and
   skips kittens:

```csharp
// AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs L61-L68
    // Runs on the main thread inside Universe.PrepareVehicleWorkers for every vehicle. See the handle in GameReflection for why this is the site.
    private static void OnPrepareWorker(Vehicle __instance) => StepVehicle(__instance);

    private static void StepVehicle(Vehicle vehicle)
    {
        // The flag outlives a patch that failed to unpatch, and a kitten on EVA is a vehicle too.
        if (!SharedVehicleHooks.GuidanceEnabled || vehicle is KittenEva)
            return;
```

7. Request off-rails or a wake through the receipt rather than patching the physics bubble. Setting
   `AnyActuatorCommanded` takes a railed vehicle off rails in the next decision, and a small `WakeupSec`
   schedules an earlier control step (bounded by the game's minimum control interval). The sibling topic
   [keeping a pushed craft off rails](../../solver/keep-a-pushed-craft-off-rails/overview.md) compares this with
   patching `PhysicsBubble.KittenWantsWake`.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Core/VehicleCommandSink.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/VehicleCommandSink.cs) | The sink: patch, receipt, writer dispatch, merge |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsWorkerCommand.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsWorkerCommand.cs) | `BurnPlan`-keyed command channel |
| [AdvancedFlightComputer/Features/Guidance/Control/GimbalControl.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/GimbalControl.cs#L40-L100) | `VehicleConfigInfo`-keyed gimbal slots |
| [AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs#L24-L75) | `PrepareWorker` prefix and the attitude-target postfix |

## Variations and gotchas

- The worker copy is not private to the step: its fields are written back at apply, so stock's own worker-side
  writes (`BurnMode = Manual`, `AutoBurnCompleted`) reach the main thread that way, and a main-thread write
  made after dispatch is lost. Fields `CopyFrom` skips (such as `Ctrl2Asmb`) never come back; read the control
  frame from the vehicle instead.
- `fc.Burn` can outlive its plan entry, because `DeleteInvalidBurns` runs before `CopyFrom` writes the worker's
  target back; identify the loaded burn by impulse time, not by reference.
- The shared `BurnTarget` is the one place the worker mutates shared state (AFC writes timing into it every
  control step, as stock does); the UI reads it during the draw, so neither side treats it as main-thread-only.
- An exception on the worker aborts the bubble's step; keep every writer inside its own `try/catch`.
- `ref readonly` parameters are patched as `ArgumentType.Ref`; a postfix may declare them `in`.
- The `FlightComputerOutput` struct is consumed by the same step's actuators and is not copied back.
- `ConditionalWeakTable` takes a write lock on `AddOrUpdate` and `Remove`: publish from the main thread only.

## See also

- [Overview and comparison](overview.md)
- [Approach 4: volatile snapshot handoff](write-worker-thread-safe-solver-patches-4.md)
- Mod features: [command sink](../../../../mods/ksa-advancedflightcomputer/features/flight-computer-command-sink.md), [worker copy channels](../../../../mods/ksa-advancedflightcomputer/features/flight-computer-worker-copy-channels.md)
- [Mutate vehicles at a physics-safe point](../../solver/mutate-vehicles-at-a-physics-safe-point/overview.md)
- [Keep a craft under full physics while your mod pushes it](../../solver/keep-a-pushed-craft-off-rails/overview.md)

[^ksa-vehicleupdatedata]: KSA.VehicleUpdateData (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
