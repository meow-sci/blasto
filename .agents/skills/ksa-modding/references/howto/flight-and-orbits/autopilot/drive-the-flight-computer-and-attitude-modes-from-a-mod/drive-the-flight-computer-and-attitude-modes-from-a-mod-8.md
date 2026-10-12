---
type: How-To
title: Queue vehicle changes in your own ConcurrentQueue and drain it in an ApplyInputEvents prefix
description: Let the UI only enqueue requests and apply them from a prefix on InputEvents.ApplyInputEvents, at the stock input point, before the stock buffers are consumed and before the next snapshot
category: flight-and-orbits
group: autopilot
topic: drive-the-flight-computer-and-attitude-modes-from-a-mod
approach: 8
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/compendium/features/root-navigation-targets]
techniques: [command-queue, harmony-prefix]
hooks: [harmony-prefix KSA.InputEvents.ApplyInputEvents, starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:input]
game_types: [KSA.InputEvents, KSA.FlightComputer, KSA.Vehicle, KSA.BurnPlan, KSA.Program]
tags:
  - command-queue
  - input-events
  - apply-input-events
  - concurrent-queue
  - flight-computer
  - navigation-target
---

Outcome: UI code never writes to a vehicle; it enqueues a request, and the request is performed at the one point in the frame where stock itself performs the same changes. Compendium does this for its root navigation targets and Toward/Away tracking.[Navigation-only root targets](../../../../mods/compendium/features/root-navigation-targets.md)
Prerequisites: StarMap and Harmony.

## Background: how the game does it

`InputEvents.ApplyInputEvents` is called once per frame from `Program.PrepareFrame`, after `Universe.ApplyVehicleSolvers` and before `Universe.ExecuteNextVehicleSolvers`. It applies the stock buffers in a fixed order: the burn update buffer, vehicle creation, sim-speed, `ChangeTargetBuffer`, control-part changes, `VehicleInputBuffer`, `AutoWarpBuffer`, `FlightComputerInputBuffer`,
resources, staging and sequence changes.[^ksa-inputevents] [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1114-L1140) The solver is idle and its results applied, and the next dispatch snapshots the flight computer with `CopyFrom`, so a flight-computer change made here sticks. A `FlightComputerInputData` calls `Vehicle.SetEnum`: a track target goes to
`FlightComputer.TrackTarget` and switches the navball; a reference frame goes to `RateHold`.[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6362-L6372)

## Lifecycle and timing

| Step | Where | Thread |
|---|---|---|
| Button click, enqueue | `[StarMapAfterGui]` window | main; ImGui is valid here, the vehicle solver is already running, so nothing is written to the vehicle |
| Drain | prefix on `InputEvents.ApplyInputEvents` (`frame:input`) | main; after the apply, before the dispatch |
| Stock buffers | later in the same `ApplyInputEvents` call | main; as a prefix, the drain runs before them |

A click in frame N is applied in `PrepareFrame` of frame N+1, and the first solver step that sees the change is the one dispatched in that same `PrepareFrame`. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Mutate simulation state safely](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. **Define a request record and a queue.** The weak tables and queue are static:

```csharp
// src/RootNavigationTargets.cs L11-L22
private sealed class TargetState(Astronomical body)
{
    public Astronomical? Body = body;
}

private static readonly ConditionalWeakTable<Vehicle, TargetState> vehicleTargets = new();
// The simulation's FlightComputer copies share their original BurnPlan.
private static readonly ConditionalWeakTable<BurnPlan, TargetState> computerTargets = new();
private readonly record struct TargetRequest(Vehicle Vehicle, Astronomical? Body, FlightComputerAttitudeTrackTarget? Tracking);
private static readonly ConcurrentQueue<TargetRequest> requests = new();

public static bool PatchesInstalled { get; set; }
```

2. **Enqueue from the UI** (`QueueSelection(vehicle, body)`, `QueueSelection(vehicle, null)` to clear, `QueueTracking`); nothing else happens in the draw.
3. **Drain at the input point** with a prefix on `InputEvents.ApplyInputEvents`. Drop requests whose vehicle is disposed or whose body is no longer loaded; a tracking request additionally needs the root to still be the vehicle's target and the vehicle controllable, and becomes a stock
   `InputEvents.FlightComputerInputData` with the `Toward` or `Away` enum value ([src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L38-L70), [src/RootTargetPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetPatches.cs#L10-L15)).
4. **Perform the change with the game's own calls** (`SetInertialNullRot`, `SetNavBallFrame`, `SetTarget(null)`), so the result is indistinguishable from a stock click:

```csharp
// src/RootNavigationTargets.cs L108-L141
public static void Select(Vehicle vehicle, Astronomical body)
{
    if (!PatchesInstalled || !Supports(body) || !IsLoadedRoot(body))
    {
        throw new InvalidOperationException($"Compendium cannot select root navigation target '{body.Id}'.");
    }

    vehicle.SetTarget(null);
    var state = new TargetState(body);
    vehicleTargets.Remove(vehicle);
    vehicleTargets.Add(vehicle, state);
    computerTargets.Remove(vehicle.FlightComputer.BurnPlan);
    computerTargets.Add(vehicle.FlightComputer.BurnPlan, state);
    Console.WriteLine($"Compendium: navigation target '{body.Id}' selected for '{vehicle.Id}' (session only).");
}

public static void Clear(Vehicle vehicle, bool stopTracking = true)
{
    if (!vehicleTargets.TryGetValue(vehicle, out var state))
    {
        return;
    }
    Volatile.Write(ref state.Body, null);
    vehicleTargets.Remove(vehicle);
    computerTargets.Remove(vehicle.FlightComputer.BurnPlan);
    if (stopTracking)
    {
        StopRootTracking(vehicle.FlightComputer);
        if (vehicle.NavBallData.Frame == VehicleReferenceFrame.Dock)
        {
            vehicle.SetNavBallFrame(VehicleReferenceFrame.EclBody);
        }
    }
}
```

5. **Hook the native paths that must clear your state.** A prefix on `Vehicle.SetTarget` repeats the game's own guard and clears the root target; a prefix on `Universe.UnsetTargetCommand` queues a clear and skips the original for vehicles that have a root target:

```csharp
// src/RootTargetPatches.cs L17-L25
[HarmonyPatch(typeof(Vehicle), nameof(Vehicle.SetTarget))]
[HarmonyPrefix]
private static void ChangingNativeTarget(Vehicle __instance, IOrbiter? target, Part? targetPart)
{
    if (target != __instance && targetPart?.Tree?.OwningVehicle != __instance)
    {
        RootNavigationTargets.Clear(__instance, stopTracking: target == null);
    }
}
```

6. **Clean up on unload**: clear the queue and every vehicle's state in `[StarMapUnload]`. Under StarMap `[StarMapUnload]` runs after the game's `Main` returned and after StarMap's id-less `Harmony.UnpatchAll()` (see the lifecycle reference), so this is tidiness for a loader that can unload a mod from a running game, not a step that protects a live frame.

## Complete example

| File | Role |
|---|---|
| [src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L11-L36) | shared state, weak tables, request queue |
| [src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L38-L70) | drain requests at the input point |
| [src/RootTargetPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetPatches.cs#L10-L38) | `ApplyInputEvents`, `SetTarget` and `UnsetTargetCommand` prefixes |
| [src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L83-L172) | target buttons and Track Toward / Away |

## Variations and gotchas

- **A prefix drains before the stock buffers**; a native target change queued in the same frame clears your root afterwards through the `SetTarget` prefix.
- **Key worker-side state by `BurnPlan`**, not the flight computer, because the solver's copy shares only that reference (see approach 7).
- **`Clear` writes `null` with `Volatile.Write`** so a solver thread holding the shared object stops seeing the body.
- Selection is session-only: the state is not saved and is replaced by any native target.

## See also

- [Overview](overview.md); [finalizer injection](drive-the-flight-computer-and-attitude-modes-from-a-mod-7.md); [set and clear the target](../set-and-clear-the-vehicle-target/overview.md)
- [Navigation-only root targets](../../../../mods/compendium/features/root-navigation-targets.md)
- [Set or clear a vehicle's target, including bodies the game cannot target](../set-and-clear-the-vehicle-target/overview.md)

[^ksa-inputevents]: KSA.InputEvents (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
