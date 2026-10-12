---
type: How-To
title: Look the state up through __instance.BurnPlan in a ComputeControl prefix
description: Find a vehicle's side state from a prefix on FlightComputer.ComputeControl by the BurnPlan object that the solver's copy shares with the live flight computer, and use it for exactly one call
category: patching
group: harmony
topic: patch-methods-that-run-on-background-threads
approach: 5
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/compendium/features/flight-computer-root-attitude-tracking]
techniques: [harmony-prefix, conditional-weak-table, background-thread]
hooks: [harmony-prefix KSA.FlightComputer.ComputeControl]
lifecycle: [physics:vehicle-solver, frame:input, frame:apply-solvers]
game_types: [KSA.FlightComputer, KSA.BurnPlan, KSA.FlightComputerNavigation, KSA.PhysicsBubble]
tags: [harmony, prefix, worker-thread, burnplan, shared-reference, flight-computer]
---

A prefix on `FlightComputer.ComputeControl` receives `__instance`, the worker's *copy* of the flight computer. A table
keyed by the live vehicle or its flight computer would never match. Compendium looks its per-vehicle state up by
`__instance.BurnPlan`, the object that the copy shares by reference with the live computer. This short article shows
that lookup in the context of the whole worker-side prefix; the injection and the restore are in
[Prefix with __state and a finalizer that restores an edited input](../run-cleanup-or-recover-from-exceptions-with-a-finalizer/run-cleanup-or-recover-from-exceptions-with-a-finalizer-3.md).

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by Compendium in
[Flight-computer attitude tracking against a root target](../../../../mods/compendium/features/flight-computer-root-attitude-tracking.md).

## Background: how the game does it

`CopyFrom` assigns `BurnPlan` by reference ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L197-L201)[^ksa-flightcomputer]), and
`Vehicle.PrepareWorker` fills the solver's copy from the vehicle just before the step is queued
([current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L296-L299)[^ksa-vehicleupdatestate]). The copy is the instance that `ComputeControl` runs on, in three
places in `PhysicsBubble` ([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1277)[^ksa-physicsbubble], [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1414), [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1794)).
At the next apply the solver's copy is copied back over the vehicle's with `CopyFrom`, so a change made on the copy
(such as stopping tracking) becomes the vehicle's state ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2530)[^ksa-vehicle]).

## Lifecycle and timing

- **Worker side, `physics:vehicle-solver`.** The prefix runs on a vehicle worker per flight-computer tick, not per
  integration substep; the tick spacing is at least `1 / MaxFlightComputerRate` of simulation time. It reads only a
  `ConditionalWeakTable`, a `Volatile.Read` of shared state and plain struct math, and writes only the solver's own
  `nav` struct and its own flight-computer copy.
- **Main side, `frame:input`.** Commands are applied inside `InputEvents.ApplyInputEvents`, after the apply and before
  the next snapshot, through a prefix on that method so the write lands in the window that is copied. See [Mutate simulation state safely relative to the physics step](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).
- Results reach the vehicle at `frame:apply-solvers` through the copy-back.

## Steps

1. **Key a `ConditionalWeakTable` by the `BurnPlan`.** The main thread registers the per-vehicle state against the
   vehicle's `FlightComputer.BurnPlan`; no worker thread writes to the table.

2. **Look up through `__instance.BurnPlan` in the prefix.** The prefix hands the worker's computer to
   `ApplyControlTarget`, which finds the state by `computer.BurnPlan` and only then decides whether to inject:

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

Source: [src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L189-L207)[^compendium-rootnavigationtargets]

3. **Use the lookup for exactly one call.** The target is injected into the by-ref navigation struct, and the finalizer
   removes it (the `__state` flow is the subject of [Prefix with __state and a finalizer that restores an edited input](../run-cleanup-or-recover-from-exceptions-with-a-finalizer/run-cleanup-or-recover-from-exceptions-with-a-finalizer-3.md)):

```csharp
// src/RootTargetPatches.cs L40-L50
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
```

Source: [src/RootTargetPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetPatches.cs#L40-L50)[^compendium-roottargetpatches]

4. **Move main-thread writes to the settled window.** Every main-thread write to the flight computer (here
   `StopRootTracking` in `Clear`, and the queued Toward/Away commands) happens inside `ApplyInputEvents` before the next
   snapshot, via a prefix on that method:

```csharp
// src/RootTargetPatches.cs L10-L15
[HarmonyPatch(typeof(InputEvents), nameof(InputEvents.ApplyInputEvents))]
[HarmonyPrefix]
private static void ApplyRootTargetRequests()
{
    RootNavigationTargets.ApplyRequests();
}
```

Source: [src/RootTargetPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetPatches.cs#L10-L15)

5. **Verify.** With a root target selected and Toward pressed, the vehicle points at the root. Delete the target: the
   next apply carries the stop back from the copy.

## Complete example

| File | Role |
|---|---|
| [src/RootTargetPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetPatches.cs#L40-L60) | Worker-side prefix and restore |
| [src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L189-L207) | `BurnPlan` lookup and injection |
| [src/RootTargetPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetPatches.cs#L10-L15) | Main-thread request drain inside `ApplyInputEvents` |

## Variations and gotchas

- **Silent fallback.** If another mod replaces the copy's plan, or a future build stops sharing it, the lookup misses
  and stock code runs without an error.
- **The shared key must be stable.** Anything the game replaces on re-read (such as `VehicleConfigInfo`; see
  [ConditionalWeakTable keyed by objects the copy shares by reference](patch-methods-that-run-on-background-threads-4.md)) is a poor key; `BurnPlan` is stable for the vehicle.
- **A main-thread write after dispatch is lost.** `ApplyVehicleSolvers` copies the worker's computer over the vehicle's
  unconditionally.
- **Hot path.** One null check unless the mode needs a target, and the table lookup is the only shared-state access.

## See also

- [Patches on planner tasks that run on pooled threads](patch-methods-that-run-on-background-threads-1.md), [Preload in StarMapBeforeMain because shaders compile in parallel on pool threads](patch-methods-that-run-on-background-threads-2.md), [Worker-side postfix merging commands from the main thread](patch-methods-that-run-on-background-threads-3.md), [ConditionalWeakTable keyed by objects the copy shares by reference](patch-methods-that-run-on-background-threads-4.md)
- [Patching game methods that run on worker and pool threads](overview.md)
- [Flight-computer attitude tracking against a root target](../../../../mods/compendium/features/flight-computer-root-attitude-tracking.md)
- Related tasks: drive-the-flight-computer-and-attitude-modes-from-a-mod, mutate-vehicles-at-a-physics-safe-point

[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatestate]: KSA.VehicleUpdateState (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^compendium-rootnavigationtargets]: Compendium/src/RootNavigationTargets.cs
[^compendium-roottargetpatches]: Compendium/src/RootTargetPatches.cs
