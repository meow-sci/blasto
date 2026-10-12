---
type: How-To
title: Run a main-thread tick around Universe.ApplyVehicleSolvers
description: Use a prefix and postfix on the static ApplyVehicleSolvers to sample before the apply and act on fresh physics results afterwards, before input is drained and before stock reacts to burn flags
category: flight-and-orbits
group: autopilot
topic: drive-the-flight-computer-and-attitude-modes-from-a-mod
approach: 6
difficulty: intermediate
prerequisites: [harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/post-solver-vehicle-tick]
techniques: [harmony-prefix, harmony-postfix]
hooks: [harmony-prefix KSA.Universe.ApplyVehicleSolvers, harmony-postfix KSA.Universe.ApplyVehicleSolvers]
lifecycle: [frame:apply-solvers]
game_types: [KSA.Universe, KSA.Vehicle, KSA.FlightComputer, KSA.VehicleUpdateTask, KSA.Program]
tags: [apply-vehicle-solvers, tick, state-machine, staging, prepare-frame, sampling]
---

Outcome: one place where per-vehicle state machines (automatic staging, multi-pass scheduling, RCS burn execution) run each frame with fresh physics results, before the player's input is drained and before stock ends burns and raises alerts.
KSA-AdvancedFlightComputer hangs all three off a pair of patches on `Universe.ApplyVehicleSolvers`.[Main-thread vehicle tick right after the solver apply](../../../../mods/ksa-advancedflightcomputer/features/post-solver-vehicle-tick.md) Prerequisite: Harmony.

## Background: how the game does it

`ApplyVehicleSolvers` takes no vehicle argument. `VehicleUpdateTask.ApplyResultsToMainThread` applies each bubble's results in a parallel batch on the vehicle worker pool (`Vehicle.UpdateFromTaskResultsUnsynchronized` runs once per physics bubble on a worker), and `UpdateFromTaskResultsSynchronized` is `AggressiveInlining`, which a Harmony detour on
the callee cannot reliably survive.[^ksa-vehicleupdatetask] [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L226-L259) [^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2546-L2547) A postfix on the static `Universe.ApplyVehicleSolvers` runs once, on the main thread, after all of that.

## Lifecycle and timing

In `Program.PrepareFrame` the order is: wait for the orbit, vehicle and cloth solver jobs, `ApplyOrbitSolvers`, **`ApplyVehicleSolvers`** (prefix and postfix), `ApplyClothSolvers`, `Glfw.PollEvents`, `InputEvents.ApplyInputEvents`, `RefreshVehiclesInFrame`, `FlightComputer.RaisePendingAlerts` per vehicle, auto-warp, and `ExecuteNextVehicleSolvers`.[^ksa-program] [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141-L2222)
At the postfix:

| State | Status |
|---|---|
| Vehicle physics state, flight computer fields, burn flags | fresh from the step that just finished (`CopyFrom` just ran); the workers are joined |
| Player input for this frame | not drained yet; burn mutations queued now are processed in the same frame's drain |
| `AutoBurnCompleted` / `AutoBurnStoppedOutOfPropellant` | set by the worker, not yet consumed by `RaisePendingAlerts`, so a mod can still clear or raise them |
| Next physics step | not dispatched; writes here are copied into it by `Vehicle.PrepareWorker` |
| `BurnPlan` edits | safe: the nearest-orbit job that walks burn plans is queued only after the `RaisePendingAlerts` loop |

This is the safest main-thread window to mutate simulated vehicle state. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Prefix: sample before the apply.** Record per-vehicle engine and burn state before the worker results overwrite it, so the postfix can compare before and after (for example "an engine that was burning is now dry"). Postfix: tick:

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

2. **Tick in a fixed order.** Staging evaluation first, so a staging that keeps an Auto burn alive takes back the worker's out-of-propellant stop before anything reads it; then for every live vehicle the multi-pass state machine (it reads the burn mode before RCS may change it) and the RCS driver:

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

3. **Enumerate the live registry**, `Universe.CurrentSystem.All`, not `Program.VehiclesInFrame`, which is refreshed only after the apply and would be a frame stale; skip `IsDisposed` vehicles (`Dispose(bool)` sets the flag before deregistering).
4. **Raise or clear stock flags here**: the RCS executor raises `AutoBurnCompleted` for its own burns so stock ends them like engine burns (see [Execute planned burns hands-free with the stock Auto burn](../execute-planned-burns-with-the-stock-autopilot/overview.md)).
5. **Gate each driver on its feature flag** so the shared patch stays installed even when one feature fails to load.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Core/SharedVehicleHooks.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SharedVehicleHooks.cs) | patches, tick order, feature flags, dispose and rename hooks |
| [AdvancedFlightComputer/Core/LoadedVehicles.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/LoadedVehicles.cs) | live vehicle enumeration |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Core/SharedVehicleHooksTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Core/SharedVehicleHooksTest.cs) | headless test of the tick and end-of-burn dispatch |

## Variations and gotchas

- **Input is not drained yet.** A burn you queue in the tick through `BurnUpdateBuffer` is applied in the same frame's `ApplyInputEvents`; a first pass queued from a draw hook is applied one frame later.
- **Acting before `RaisePendingAlerts`** is right for raising flags and wrong for deleting finished burns (stock would remove the next one too).
- Do the sampling in the prefix only if you need before/after; otherwise the postfix alone suffices.
- The sampling-before, acting-after pattern is also how automatic staging notices an engine that went dry.

## See also

- [Overview](overview.md); [solver-phase drain](drive-the-flight-computer-and-attitude-modes-from-a-mod-3.md); [PrepareWorker prefix](drive-the-flight-computer-and-attitude-modes-from-a-mod-2.md)
- [Main-thread vehicle tick right after the solver apply](../../../../mods/ksa-advancedflightcomputer/features/post-solver-vehicle-tick.md)
- [Execute planned burns hands-free with the stock Auto burn](../execute-planned-burns-with-the-stock-autopilot/overview.md)

[^ksa-vehicleupdatetask]: KSA.VehicleUpdateTask (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
