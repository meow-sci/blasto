---
type: How-To
title: ConditionalWeakTable keyed by objects the copy shares by reference
description: The vehicle worker flies a copy of the flight computer, so a worker-side lookup keyed by the vehicle's own FlightComputer never matches; key it by the BurnPlan or VehicleConfigInfo that CopyFrom shares, publish before dispatch and write settings only before the snapshot
category: patching
group: harmony
topic: patch-methods-that-run-on-background-threads
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/flight-computer-worker-copy-channels]
techniques: [conditional-weak-table, harmony-postfix, harmony-prefix, background-thread]
hooks:
  - harmony-postfix KSA.FlightComputer.ComputeControl
  - harmony-prefix KSA.Vehicle.PrepareWorker
  - harmony-postfix KSA.Universe.ApplyVehicleSolvers
lifecycle: [frame:apply-solvers, frame:dispatch-solvers, physics:vehicle-solver]
game_types:
  - KSA.FlightComputer
  - KSA.Vehicle
  - KSA.VehicleUpdateState
  - KSA.VehicleUpdateData
  - KSA.BurnPlan
  - KSA.VehicleConfigInfo
  - KSA.Universe
tags: [harmony, conditional-weak-table, worker-copy, flight-computer, thread-safety]
---

The vehicle worker never sees a vehicle's own `FlightComputer`. It flies a copy, and the copy is written back
over the original at the next solver apply. A patch on a worker-side method such as `ComputeControl` receives the
*copy*, so a table keyed by the vehicle's flight-computer instance never matches. KSA-AdvancedFlightComputer
solves this with a `ConditionalWeakTable` keyed by an object the copy shares by reference, and with a second rule:
write flight-computer settings only in the window before the snapshot.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by AFC in
[Reach the physics worker through the references FlightComputer.CopyFrom shares](../../../../mods/ksa-advancedflightcomputer/features/flight-computer-worker-copy-channels.md).

## Background: how the game does it

**Out.** `Vehicle.PrepareWorker` ends with `VehicleUpdateState.PrepareFromVehicle`, which calls
`VehicleUpdateData.Prepare`, which runs `NewFlightComputer.CopyFrom(vehicle.FlightComputer)`:

```csharp
// current/decomp/KSA/VehicleUpdateState.cs L296-L299
public void PrepareFromVehicle(bool useHighFidelityOceanPhysics, ManualControlInputs manualControlInputs)
{
	UpdateData.Prepare(in ReadOnlyVehicle.BubbleOrigin, in ReadOnlyVehicle.KinematicStates, ReadOnlyVehicle.FlightComputer);
	UseHighFidelityOceanPhysics = useHighFidelityOceanPhysics;
```

Source: [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L296-L299)[^ksa-vehicleupdatestate]

```csharp
// current/decomp/KSA/VehicleUpdateData.cs L100
NewFlightComputer.CopyFrom(flightComputer);
```

Source: [current/decomp/KSA/VehicleUpdateData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateData.cs#L100)[^ksa-vehicleupdatedata]

**Back.** At the next `Universe.ApplyVehicleSolvers`, `Vehicle.UpdateFromTaskResultsUnsynchronized` runs
`FlightComputer.CopyFrom(updateData.NewFlightComputer)` unconditionally ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2525-L2531)[^ksa-vehicle]).

**What `CopyFrom` shares.** Value fields are copied; `Burn`, `BurnPlan` and `VehicleConfig` are assigned by
reference, so the vehicle's computer and the worker's copy point at the same objects:

```csharp
// current/decomp/KSA/FlightComputer.cs L181-L183, L197-L201
public void CopyFrom(FlightComputer existing)
{
	BurnMode = existing.BurnMode;
// ...
	Burn = existing.Burn;
	LastThrusterPulseTime = existing.LastThrusterPulseTime;
	LastThrustTime = existing.LastThrustTime;
	BurnPlan = existing.BurnPlan;
	VehicleConfig = existing.VehicleConfig;
```

Source: [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L181-L201)[^ksa-flightcomputer]

## Lifecycle and timing

| Moment | Lifecycle | Thread | Flight-computer state |
|---|---|---|---|
| `ApplyVehicleSolvers` (postfix) | `frame:apply-solvers` | main | Worker copy just written back; writes from here reach the next step |
| `Vehicle.PrepareWorker` (prefix, then the snapshot) | `frame:dispatch-solvers` | main | Last write for the next step; the copy is taken at the end of the method |
| `ComputeControl` and its callees | `physics:vehicle-solver` | vehicle worker | Worker copy; shared `BurnPlan`, `VehicleConfig`, `Burn` |
| UI, StarMap frame hooks, render | `frame:draw-ui` onwards | main | Writes are overwritten at the next apply |

The main thread publishes before `ExecuteNextVehicleSolvers` queues the job, so the publish happens before the worker read.
See [Mutate simulation state safely relative to the physics step](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md) and [Run code inside the physics step on worker threads](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md).

## Steps

1. **Pick the key from `CopyFrom`, not from the vehicle.** A table keyed by the `FlightComputer` the postfix receives
   never matches the vehicle's. The RCS channel keys by `BurnPlan`, the guidance channels by
   `FlightComputer.VehicleConfigInfo`:

| Channel | Key | Published from | Read in |
|---|---|---|---|
| RCS worker command | `BurnPlan` | the post-solver tick (main) | `ComputeControl` postfix |
| Guidance gimbal demand | `VehicleConfigInfo` | `PrepareWorker` prefix (main) | `ComputeControl` postfix |
| Guidance rate feedforward | `VehicleConfigInfo` | `PrepareWorker` prefix (main) | `UpdateAttitudeTarget` postfix |

2. **Publish immutable commands in a `ConditionalWeakTable`.** The RCS command is an immutable record whose only mutable
   member is a consumption receipt; the table is safe for concurrent reads and writes and drops an entry when the plan
   object dies:

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

Source: [AdvancedFlightComputer/Features/RcsTranslation/RcsWorkerCommand.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsWorkerCommand.cs#L40-L59)[^ksa-advancedflightcomputer-rcsworkercommand]

3. **Do the same for per-vehicle state keyed by the config object.** The guidance slot comment states the reason: the
   FlightComputer handed to the postfix is not the live one, and `VehicleConfigInfo` is assigned by reference rather
   than cloned:

```csharp
// AdvancedFlightComputer/Features/Guidance/Control/GimbalControl.cs L63-L82
/// <summary>
/// Per-vehicle state, keyed on the identity the apply side can actually see.
///  The FlightComputer handed to the postfix is NOT the live one - the worker runs on a copy - so reference-comparing the FlightComputer would never match.
/// VehicleConfigInfo is the usable identity because FlightComputer.CopyFrom assigns it by REFERENCE rather than cloning.
///  This replaces a single _target field, and the gain is not only that two vehicles can be driven at once. With one target, a second vehicle engaging silently stole the first one's gimbals, and whether the first still flew depended on the order KSA happened to interleave prepare and compute across vehicles. A lookup cannot be pointed at the wrong vehicle.
/// </summary>
private static readonly ConditionalWeakTable<FlightComputer.VehicleConfigInfo, Slot> Slots = new();

private static Slot SlotFor(Vehicle vehicle)
{
    FlightComputer.VehicleConfigInfo cfg = vehicle?.FlightComputer?.VehicleConfig;
    return cfg == null ? null : Slots.GetOrCreateValue(cfg);
}

/// <summary>This vehicle's diagnostics, or null if it has never been driven.</summary>
public static Slot Diagnostics(Vehicle vehicle)
{
    FlightComputer.VehicleConfigInfo cfg = vehicle?.FlightComputer?.VehicleConfig;
    return cfg != null && Slots.TryGetValue(cfg, out Slot s) ? s : null;
}
```

Source: [AdvancedFlightComputer/Features/Guidance/Control/GimbalControl.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/GimbalControl.cs#L63-L82)[^ksa-advancedflightcomputer-gimbalcontrol]

4. **Write settings only before the snapshot.** Because the copy-back overwrites every copied field, a main-thread
   write survives only if it lands between `ApplyVehicleSolvers` and that vehicle's `PrepareWorker`. AFC uses the
   `ApplyVehicleSolvers` postfix (RCS executor, multi-pass, auto-stage) and the `PrepareWorker` prefix (guidance), and
   queues every UI action as a request for its next prefix. See [Worker-side postfix merging commands from the main thread](patch-methods-that-run-on-background-threads-3.md) for the same window in KSACars.

5. **Mutate shared objects from the worker only for fields the main thread does not touch while the step is in
   flight.** The RCS writer replaces `BurnDuration` and `IgnitionTime` on the shared `fc.Burn` every control step; stock does
   the same kind of worker-side write to that object, so neither side treats it as main-thread-only.

6. **Re-publish against the current key.** The game assigns a new `VehicleConfigInfo` whenever the configuration is
   re-read (split, docking, part-tree modification, load), so publish every step instead of once.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/RcsTranslation/RcsWorkerCommand.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsWorkerCommand.cs) | RCS command record and `ConditionalWeakTable` channel |
| [AdvancedFlightComputer/Features/Guidance/Control/GimbalControl.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/GimbalControl.cs#L20-L90) | Per-vehicle slots keyed by `VehicleConfigInfo` |
| [AdvancedFlightComputer/Core/VehicleCommandSink.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/VehicleCommandSink.cs)[^ksa-advancedflightcomputer-vehiclecommandsink] | The worker-side postfix that reads the channels |

## Variations and gotchas

- **Writing a flight-computer field from a UI or StarMap hook looks like it works for one frame** and is then
  reverted; the bug appears only while the next step's copy is in flight.
- **Do not read `Ctrl2Asmb` from the vehicle's flight computer.** It is set on the worker and never copied back (not
  in `CopyFrom`); read `Vehicle.Ctrl2Body` instead.
- **A burn can outlive its plan entry.** `DeleteInvalidBurns` runs before `CopyFrom` writes the worker's target back,
  so identify the loaded burn by impulse time, not by reference.
- **`ConditionalWeakTable` rather than a dictionary.** Entries disappear with the key, so a destroyed vehicle leaves no
  entry to clean up.

## See also

- [Patches on planner tasks that run on pooled threads](patch-methods-that-run-on-background-threads-1.md), [Preload in StarMapBeforeMain because shaders compile in parallel on pool threads](patch-methods-that-run-on-background-threads-2.md), [Worker-side postfix merging commands from the main thread](patch-methods-that-run-on-background-threads-3.md), [Look the state up through __instance.BurnPlan in a ComputeControl prefix](patch-methods-that-run-on-background-threads-5.md)
- [Patching game methods that run on worker and pool threads](overview.md)
- [Reach the physics worker through the references FlightComputer.CopyFrom shares](../../../../mods/ksa-advancedflightcomputer/features/flight-computer-worker-copy-channels.md)
- [One worker-side postfix merging every writer](../coexist-with-other-mods-patching-the-same-method/coexist-with-other-mods-patching-the-same-method-5.md) for the postfix that reads these channels
- Related tasks: write-worker-thread-safe-solver-patches, drive-the-flight-computer-and-attitude-modes-from-a-mod

[^ksa-vehicleupdatestate]: KSA.VehicleUpdateState (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatedata]: KSA.VehicleUpdateData (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-advancedflightcomputer-rcsworkercommand]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Features/RcsTranslation/RcsWorkerCommand.cs
[^ksa-advancedflightcomputer-gimbalcontrol]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Features/Guidance/Control/GimbalControl.cs
[^ksa-advancedflightcomputer-vehiclecommandsink]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Core/VehicleCommandSink.cs
