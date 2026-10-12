---
type: How-To
title: Edit ComputeControl inputs by ref in a prefix and restore them in a finalizer
description: Change the by-ref navigation struct the vehicle worker passes to ComputeControl for exactly one call, and put it back afterwards, so the stock controller sees a value the game did not build
category: flight-and-orbits
group: autopilot
topic: drive-the-flight-computer-and-attitude-modes-from-a-mod
approach: 7
difficulty: advanced
prerequisites: [harmony]
demonstrated_by: [mods/compendium/features/flight-computer-root-attitude-tracking]
techniques: [harmony-prefix, harmony-finalizer]
hooks:
  - harmony-prefix KSA.FlightComputer.ComputeControl
  - harmony-finalizer KSA.FlightComputer.ComputeControl
lifecycle: [physics:vehicle-solver]
game_types:
  - KSA.FlightComputer
  - KSA.FlightComputerNavigation
  - KSA.NavigationTarget
  - KSA.BurnPlan
  - KSA.VehicleUpdateState
tags: [compute-control, by-ref, finalizer, navigation-target, worker-thread, burn-plan]
---

Outcome: inject a value into a by-ref input of `ComputeControl` on the vehicle worker for a single call and leave the game's struct as the game built it. Compendium uses it to give the stock Toward/Away and TGT-frame controller a navigation target the game
would not build.[Flight-computer attitude tracking against a root target](../../../../mods/compendium/features/flight-computer-root-attitude-tracking.md) The full target synthesis is the topic [Synthesize a NavigationTarget for an object that has no orbit](../synthesize-a-navigation-target-for-the-flight-computer/overview.md); this article extracts the timing pattern. Prerequisite: Harmony.

## Background: how the game does it

The vehicle solver rebuilds `vehicleUpdateState.FlightComputerNavigation` before each call (`FlightComputerNavigation.CreateFromAnalyticStates` or `CreateFromPhysicsStates`, passing `vehicle.Target`) and calls `ComputeControl` on the worker's flight computer copy, taking the navigation as
`ref readonly` (passed `in` from the `VehicleUpdateState` field). A Harmony prefix can declare that parameter `ref` and so alias the caller's storage.[^ksa-flightcomputernavigation] [current/decomp/KSA/FlightComputerNavigation.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputerNavigation.cs#L30-L66) [^ksa-flightcomputer] [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L400)
`ComputeControl` is called in three places: `ApplySingleVehicleMotion` for a lone vehicle on an analytic segment, `FullPhysicsPreStep` when the computer's wake-up time is due, and `FullPhysicsStepUntil` at every flight-computer tick inside the substep loop; the tick spacing is at least `1 / MaxFlightComputerRate`, so the prefix runs per flight-computer
tick, not per integration substep.[^ksa-physicsbubble] [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1264-L1277) [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1391-L1414) [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1782-L1797)

## Lifecycle and timing

`physics:vehicle-solver`, on worker threads, between `ExecuteNextVehicleSolvers` in one frame's `PrepareFrame` and the wait before `ApplyVehicleSolvers` in the next. The prefix may read a `ConditionalWeakTable` (thread-safe), shared state through `Volatile.Read`, and values replaced only when a system loads; it writes only the solver's own `nav` struct and
the solver's own flight-computer copy, with no managed allocation and no ImGui or Vulkan. Results reach the vehicle at `frame:apply-solvers`. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and
[Run code inside the physics step on worker threads](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md).

## Steps

1. **Declare the parameter `ref`** in the prefix and carry a flag in `__state` (`out bool`), so the finalizer knows whether to undo:

```csharp
// src/RootTargetPatches.cs L40-L60
[HarmonyPatch(typeof(FlightComputer), nameof(FlightComputer.ComputeControl))]
[HarmonyPrefix]
private static void AddControlTarget(FlightComputer __instance, ref FlightComputerNavigation nav, out bool __state)
{
    __state = false;
    if (nav.Target == null)
    {
        RootNavigationTargets.ApplyControlTarget(__instance, ref nav);
        __state = nav.Target.HasValue;
    }
}

[HarmonyPatch(typeof(FlightComputer), nameof(FlightComputer.ComputeControl))]
[HarmonyFinalizer]
private static void RestoreControlTarget(ref FlightComputerNavigation nav, bool __state)
{
    if (__state)
    {
        nav.Target = null;
    }
}
```

2. **Inject only when the current mode needs it.** Look the state up by `__instance.BurnPlan`, which the solver's copy shares with the live vehicle's computer (the flight computer objects differ), and write `nav.Target`:

```csharp
// src/RootNavigationTargets.cs L189-L207
public static void ApplyControlTarget(FlightComputer computer, ref FlightComputerNavigation nav)
{
    if (nav.Target == null &&
        (computer.AttitudeTrackTarget is FlightComputerAttitudeTrackTarget.Toward or FlightComputerAttitudeTrackTarget.Away ||
         (computer.AttitudeFrame == VehicleReferenceFrame.Dock &&
          computer.AttitudeTrackTarget is FlightComputerAttitudeTrackTarget.None or FlightComputerAttitudeTrackTarget.Custom)) &&
        computerTargets.TryGetValue(computer.BurnPlan, out var state))
    {
        var body = GetLoadedBody(state);
        if (body != null)
        {
            nav.Target = CreateNavigationTarget(body, nav.Parent, nav.Time);
        }
        else
        {
            StopRootTracking(computer);
        }
    }
}
```

3. **Use a finalizer to restore**, not a postfix: a finalizer runs whether or not the original throws, so the `VehicleUpdateState` field is always left as the game built it. In build 2026.10.10.5554 nothing else reads that field after the call (the three `ComputeControl` calls are its only uses), so the restore is defensive.
4. **Stop from the copy.** If the body is no longer valid, clear the mode on the worker's copy (`StopRootTracking`); the change reaches the vehicle at the next copy-back.
5. **Keep main-thread writes in the settled window.** Compendium makes every main-thread flight-computer write inside `ApplyInputEvents` ([approach 8](drive-the-flight-computer-and-attitude-modes-from-a-mod-8.md)); the only worker-side write travels back through the copy-back.

## Complete example

| File | Role |
|---|---|
| [src/RootTargetPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetPatches.cs#L40-L60) | prefix with `__state` and the restoring finalizer |
| [src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L189-L207) | mode check, burn-plan lookup, injection or stop |
| [src/RootTargetFlightComputerPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetFlightComputerPatches.cs#L78-L95) | enable Toward/Away and suppress the no-target tooltip |

## Variations and gotchas

- **A by-ref prefix aliases the caller's storage**; forgetting the restore leaves the injected value in the state for any later reader.
- **Per-tick cost**: the prefix runs on every flight-computer tick of every vehicle; return at the first cheap test.
- **Stock falls back without the injection**: `UpdateAttitudeTarget` calls `SetInertialNullRot` when `AttitudeFrame == Dock` and `nav.Target` is null.
- Another mod with the same prefix pattern on `ComputeControl` composes only if each restores exactly what it changed.

## See also

- [Overview](overview.md); [command sink](drive-the-flight-computer-and-attitude-modes-from-a-mod-5.md); [input-event queue](drive-the-flight-computer-and-attitude-modes-from-a-mod-8.md)
- [Flight-computer attitude tracking against a root target](../../../../mods/compendium/features/flight-computer-root-attitude-tracking.md)
- [Synthesize a NavigationTarget for an object that has no orbit](../synthesize-a-navigation-target-for-the-flight-computer/overview.md)

[^ksa-flightcomputernavigation]: KSA.FlightComputerNavigation (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
