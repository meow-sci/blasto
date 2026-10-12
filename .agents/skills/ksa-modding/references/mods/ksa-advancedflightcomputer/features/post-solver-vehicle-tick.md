---
type: Mod Feature
title: Main-thread vehicle tick right after the solver apply
description: A prefix and postfix on Universe.ApplyVehicleSolvers give mod state machines one main-thread tick per frame with fresh physics results, before input is drained and before stock reacts to burn flags
mod: ksa-advancedflightcomputer
category: patching
techniques: [harmony-prefix, harmony-postfix]
hooks: [harmony-prefix KSA.Universe.ApplyVehicleSolvers, harmony-postfix KSA.Universe.ApplyVehicleSolvers]
lifecycle: [frame:apply-solvers]
game_types: [KSA.Universe, KSA.VehicleUpdateTask, KSA.Vehicle, KSA.CelestialSystem, KSA.Program]
tags: [frame-order, main-thread, physics-results, state-machine, staging, burns]
---

AFC runs three per-vehicle state machines every frame: automatic staging, the multi-pass pass
scheduler and the RCS burn executor. All three need the physics results of the step that just
finished, and two of them must act before stock reacts to that step (stock ends burns and raises
alerts later in the same frame). AFC hangs all of them off one pair of patches on
`Universe.ApplyVehicleSolvers`.

## How it works

- **Prefix: sample before the apply.** `StagingDetector.Sample()` records per-vehicle engine and
  burn state before the worker results overwrite it, so the postfix can compare before and after
  (for example "an engine that was burning is now dry") ([AdvancedFlightComputer/Core/SharedVehicleHooks.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SharedVehicleHooks.cs#L108-L120)).
- **Postfix: tick.** `TickVehicles(LoadedVehicles.All)` runs, in this order:
  `StagingDetector.Evaluate()` first, so a staging that keeps an Auto burn alive can take back the
  worker's out-of-propellant stop before anything reads it; then for every live `Vehicle`
  `PassCompletionPatch.TickVehicle` (multi-pass reads the burn mode before RCS may change it) and
  `RcsDriverPatch.TickVehicle` ([AdvancedFlightComputer/Core/SharedVehicleHooks.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SharedVehicleHooks.cs#L38-L55)).
- **Which vehicles.** `LoadedVehicles.All` is `Universe.CurrentSystem.All`, the live registry,
  not `Program.VehiclesInFrame`: `PrepareFrame` refreshes that cache only after the solver apply, so
  it would be one frame stale here, and a headless harness never refreshes it. Disposed vehicles are
  skipped with `IsDisposed` because `Vehicle.Dispose(bool)` sets the flag before deregistering
  ([AdvancedFlightComputer/Core/LoadedVehicles.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/LoadedVehicles.cs)).
- **Feature flags.** Each driver runs only if its feature block loaded (see
  [feature-gated patch sets](feature-gated-patch-sets.md)); the shared patch stays installed even
  when one feature fails.

### Why not a per-vehicle hook

`ApplyVehicleSolvers` takes no vehicle argument, so a per-vehicle method would seem a better fit.
The decomp shows why it is not: `VehicleUpdateTask.ApplyResultsToMainThread` applies each bubble's
results in a parallel batch on the vehicle worker pool (`Vehicle.UpdateFromTaskResultsUnsynchronized`
runs once per physics bubble on a worker), and `Vehicle.UpdateFromTaskResultsSynchronized` is
`[MethodImpl(AggressiveInlining)]`, which a Harmony detour on the callee cannot reliably survive
([current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L226-L259), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2546-L2547)).[^ksa-vehicleupdatetask]
A postfix on the static `Universe.ApplyVehicleSolvers` runs once, on the main thread, after all of
that has finished.

## Lifecycle and timing

In `Program.PrepareFrame` the order is: wait for the orbit, vehicle and cloth solver jobs,
`Universe.ApplyOrbitSolvers`, **`Universe.ApplyVehicleSolvers`** (AFC prefix and postfix),
`ApplyClothSolvers`, `Glfw.PollEvents`, `InputEvents.ApplyInputEvents`, `RefreshVehiclesInFrame`,
`FlightComputer.RaisePendingAlerts` per vehicle, auto-warp, and finally `ExecuteNextVehicleSolvers`
dispatches the next step ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141-L2222)).[^ksa-program] At the postfix:

| State | Status |
|---|---|
| Vehicle physics state, `FlightComputer` fields, burn flags | Fresh from the step that just finished: `Vehicle.UpdateFromTaskResultsUnsynchronized` has just copied the worker's flight computer back with `FlightComputer.CopyFrom` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2530)); vehicle, orbit and nearest-orbit workers are joined |
| Player input for this frame | Not drained yet; burn mutations queued now are processed in the same frame's drain |
| `AutoBurnCompleted` / `AutoBurnStoppedOutOfPropellant` | Set by the worker, not yet consumed by `RaisePendingAlerts`, so a mod can still clear or raise them |
| Next physics step | Not dispatched; writes here are copied into the next step's flight computer by `Vehicle.PrepareWorker` |
| `BurnPlan` edits | Safe: the nearest-orbit job, which walks burn plans for cursor hover, is queued only after the `RaisePendingAlerts` loop |

This is the safest main-thread window to mutate simulated vehicle state. The RCS executor uses it to
raise `AutoBurnCompleted` for its own burns so stock ends them like engine burns (see
[stock burn end detection](stock-burn-end-detection.md)).

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Core/SharedVehicleHooks.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SharedVehicleHooks.cs) | Patches, tick order, feature flags, dispose and rename hooks |
| [AdvancedFlightComputer/Core/LoadedVehicles.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/LoadedVehicles.cs) | Live vehicle enumeration and why |
| [AdvancedFlightComputer/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/GameReflection.cs#L204-L216) | Rationale comments on the chosen hook |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Core/SharedVehicleHooksTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Core/SharedVehicleHooksTest.cs) | Headless test of the tick and end-of-burn dispatch |

## Snippets

```csharp
// AdvancedFlightComputer/Core/SharedVehicleHooks.cs L38-L55
    internal static void TickVehicles(ReadOnlySpan<Astronomical> bodies)
    {
        // Staging first, so a staging that keeps an Auto burn alive has taken back the stock out-of-propellant stop before anything else reads it.
        if (AutoStageEnabled)
            StagingDetector.Evaluate();

        foreach (Astronomical body in bodies)
        {
            if (body is not Vehicle vehicle || vehicle.IsDisposed)
                continue;

            // The pass state machine reads the burn mode before the RCS driver can change it.
            if (MultiPassEnabled)
                PassCompletionPatch.TickVehicle(vehicle);
            if (RcsEnabled)
                RcsDriverPatch.TickVehicle(vehicle);
        }
    }
```

```csharp
// AdvancedFlightComputer/Core/SharedVehicleHooks.cs L108-L120
    // Program.PrepareFrame joins the workers before this call, then drains input events, then lets FlightComputer.RaisePendingAlerts end the burns the workers or the RCS executor finished or stopped.
    // The prefix runs before the worker results overwrite the burn state the staging detector compares against.
    [HarmonyPatch(typeof(Universe), nameof(Universe.ApplyVehicleSolvers), new Type[0])]
    private static class ApplySolversPatch
    {
        static void Prefix()
        {
            if (AutoStageEnabled)
                StagingDetector.Sample();
        }

        static void Postfix() => TickVehicles(LoadedVehicles.All);
    }
```

## Game APIs touched

| Member | Use |
|---|---|
| `Universe.ApplyVehicleSolvers()` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1952)) | Hooked; applies the finished vehicle step on the main thread |
| `VehicleUpdateTask.ApplyResultsToMainThread()` ([current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L226)) | Shows the parallel per-bubble apply that rules out per-vehicle hooks |
| `Universe.CurrentSystem.All` ([current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs)) | Live vehicle registry |
| `Vehicle.IsDisposed` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs)) | Skip vehicles being torn down |

## Pitfalls and version notes

- `Program.VehiclesInFrame` is stale at this point; enumerate the system.
- Do not do expensive work for every vehicle every frame here; this is on the main thread before the
  next step can be dispatched, so it directly lengthens the frame.
- The prefix runs before the results are applied: anything read there is the previous step's state
  (which is what the staging detector wants as a baseline).

## See also

- [Stock burn end detection](stock-burn-end-detection.md)
- [Automatic staging](automatic-staging.md)
- [Multi-pass execution](multi-pass-execution.md)
- [RCS burn execution](rcs-burn-execution.md)
- [Guidance vehicle step loop](guidance-vehicle-step-loop.md) (the other per-vehicle main-thread hook, after input)
- [Flight-computer worker copy channels](flight-computer-worker-copy-channels.md)
- [Run a main-thread tick around Universe.ApplyVehicleSolvers](../../../howto/flight-and-orbits/autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/drive-the-flight-computer-and-attitude-modes-from-a-mod-6.md) - how-to article demonstrating this feature
- [Hook any frame point with Harmony patches on Program and Universe](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-2.md) - how-to article demonstrating this feature
- [One shared tick with per-feature enable flags set from TryApply results](../../../howto/patching/harmony/coexist-with-other-mods-patching-the-same-method/coexist-with-other-mods-patching-the-same-method-4.md) - how-to article demonstrating this feature
- [Postfix the single non-inlined outer method instead of a per-vehicle callee](../../../howto/patching/internals/patch-around-aggressive-inlining/patch-around-aggressive-inlining-3.md) - how-to article demonstrating this feature

[^ksa-vehicleupdatetask]: KSA.VehicleUpdateTask (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
