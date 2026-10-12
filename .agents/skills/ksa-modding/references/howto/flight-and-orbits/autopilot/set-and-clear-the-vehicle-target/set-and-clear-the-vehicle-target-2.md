---
type: How-To
title: Make fixed stars and barycenters targetable with weak tables keyed by Vehicle and BurnPlan
description: Keep a navigation-only target for bodies that are not IOrbiter in ConditionalWeakTables keyed by the Vehicle and by the BurnPlan the solver's flight computer copy shares, queue UI requests, and drain them in an ApplyInputEvents prefix
category: flight-and-orbits
group: autopilot
topic: set-and-clear-the-vehicle-target
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/compendium/features/root-navigation-targets]
techniques: [conditional-weak-table, harmony-prefix, command-queue]
hooks:
  - harmony-prefix KSA.InputEvents.ApplyInputEvents
  - harmony-prefix KSA.Vehicle.SetTarget
  - harmony-prefix KSA.Universe.UnsetTargetCommand
lifecycle: [frame:draw-ui, frame:input, event:target-changed, event:starmap-unload]
game_types:
  - KSA.Vehicle
  - KSA.FixedStar
  - KSA.Barycenter
  - KSA.IIndependentRoot
  - KSA.IOrbiter
  - KSA.BurnPlan
  - KSA.FlightComputer
  - KSA.Universe
  - KSA.InputEvents
tags: [target, fixed-star, barycenter, conditional-weak-table, navigation-target, session-only]
---

Outcome: selecting a fixed star or a root barycenter makes the navball show a target marker, the TGT reference frame work, the flight computer track toward or away, and the TGT gauges show distance and speed. It never becomes the game's real target, has no orbit, is not saved, and is replaced as soon as the player picks a native target.
Compendium implements it.[Navigation-only root targets](../../../../mods/compendium/features/root-navigation-targets.md) Prerequisites: StarMap and Harmony.

## Background: how the game does it

`Vehicle.Target` is typed `IOrbiter?` and `FixedStar` and `Barycenter` do not implement `IOrbiter` (see the [overview](overview.md)). The mod therefore keeps its own state next to the vehicle. `Universe.LoadSystem` fills `Universe.Roots` from `CurrentSystem.All.OfType<IIndependentRoot>()`, which is how the mod decides a body is a loaded root.[^ksa-universe] [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L232-L256)
The solver runs `ComputeControl` on a copy of the vehicle's `FlightComputer`; `FlightComputer.CopyFrom` copies the `BurnPlan` reference, so the copy and the live computer share that one object.[^ksa-flightcomputer] [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L163-L200)

## Lifecycle and timing

| Step | Where | Thread |
|---|---|---|
| Button click, queue | `[StarMapAfterGui]` window | main; ImGui is only valid here and the solver is running, so nothing is written to the vehicle |
| Drain | prefix on `InputEvents.ApplyInputEvents` | main; after the apply, before the dispatch |
| `Vehicle.SetTarget` prefix | wherever the game calls it (usually `ChangeTargetData.Apply`) | main |
| `Universe.UnsetTargetCommand` prefix | the `unsettarget` console command, the Target Track window, other mods | main |
| Unload | `[StarMapUnload]` | main; after the game has exited |

A click in frame N is applied in frame N+1's `PrepareFrame`, and the first solver step that sees the change is the one dispatched in that same `PrepareFrame`. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Define which bodies are supported:** a `FixedStar` or `Barycenter` that is an `IIndependentRoot` and not an `IOrbiter`, and that is reference-equal to an entry of `Universe.Roots` in the current system:

```csharp
// src/RootNavigationTargets.cs L72-L90
public static bool Supports(Astronomical body)
{
    return body is FixedStar or Barycenter && body is IIndependentRoot && body is not IOrbiter;
}

private static bool IsLoadedRoot(Astronomical body)
{
    if (body.System == Universe.CurrentSystem)
    {
        foreach (var root in Universe.Roots)
        {
            if (ReferenceEquals(root, body))
            {
                return true;
            }
        }
    }
    return false;
}
```

2. **Store one state object in two weak tables:** keyed by `Vehicle` for main-thread lookups (UI, navball, gauges), and by `BurnPlan` for the solver threads. A weak table keeps nothing alive, so an entry disappears with its vehicle:

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

3. **Let the native target win:** `GetTarget(vehicle)` returns the root only while `vehicle.Target == null` and the body is still a loaded root.
4. **UI only queues** (`QueueSelection(vehicle, body)`, or `null` to clear, and `QueueTracking`); drain at the input point in a prefix (see [Drive the flight computer and attitude modes from a mod](../drive-the-flight-computer-and-attitude-modes-from-a-mod/overview.md), approach 8).
5. **Select:** drop any native target with `vehicle.SetTarget(null)`, then register a fresh state in both tables; **clear:** write `null` into the shared state with `Volatile.Write`, remove both entries and stop root tracking:

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

6. **Clear on native target changes.** A prefix on `Vehicle.SetTarget` repeats the game's guard and clears the root target; it stops tracking only when the new target is null. A prefix on `Universe.UnsetTargetCommand` queues a clear and skips the original, because the original returns early when `vehicle.Target == null`:

```csharp
// src/RootTargetPatches.cs L17-L38
[HarmonyPatch(typeof(Vehicle), nameof(Vehicle.SetTarget))]
[HarmonyPrefix]
private static void ChangingNativeTarget(Vehicle __instance, IOrbiter? target, Part? targetPart)
{
    if (target != __instance && targetPart?.Tree?.OwningVehicle != __instance)
    {
        RootNavigationTargets.Clear(__instance, stopTracking: target == null);
    }
}

[HarmonyPatch(typeof(Universe), nameof(Universe.UnsetTargetCommand))]
[HarmonyPrefix]
private static bool UnsetRootTarget(string celestialId)
{
    if (Universe.CurrentSystem?.Get(celestialId) is Vehicle vehicle && RootNavigationTargets.GetTarget(vehicle) != null)
    {
        RootNavigationTargets.QueueSelection(vehicle, null);
        Console.WriteLine($"Compendium: navigation target clear requested for '{celestialId}'.");
        return false;
    }
    return true;
}
```

7. **Make the rest of the game see it.** The navball marker, TGT frame and flight-computer tracking are separate patches; see [Synthesize a NavigationTarget for an object that has no orbit](../synthesize-a-navigation-target-for-the-flight-computer/overview.md).
8. **Unload:** clear the queue, caches and every vehicle's state in `[StarMapUnload]`. Under StarMap `[StarMapUnload]` runs after the game's `Main` returned and after StarMap's id-less `Harmony.UnpatchAll()` (see the lifecycle reference), so this is tidiness for a loader that can unload a mod from a running game, not a step that protects a live frame.

## Complete example

| File | Role |
|---|---|
| [src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L11-L36) | shared state, weak tables, request queue |
| [src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L38-L70) | drain requests at the input point |
| [src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L72-L106) | eligibility, loaded check, `GetTarget` |
| [src/RootTargetPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetPatches.cs#L10-L38) | `ApplyInputEvents`, `SetTarget` and `UnsetTargetCommand` prefixes |

## Variations and gotchas

- **Key worker-visible state by a shared reference** (`BurnPlan`), not the flight computer, which differs between vehicle and worker.
- **Switching from one root to another** goes through `SetTarget(null)` and so clears the old root with tracking stopped; Toward/Away must be selected again.
- **Session only.** The state is not saved, and a native target replaces it.
- **Do not clear from a worker thread**: all table writes except the volatile body write are main-thread.

## See also

- [Overview](overview.md); [native target selection](set-and-clear-the-vehicle-target-1.md)
- [Navigation-only root targets](../../../../mods/compendium/features/root-navigation-targets.md)
- [Drive the flight computer and attitude modes from a mod](../drive-the-flight-computer-and-attitude-modes-from-a-mod/overview.md)

[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
