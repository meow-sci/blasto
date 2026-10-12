---
type: How-To
title: Prefix with __state and a finalizer that restores an edited input
description: Inject a navigation target into the by-ref struct passed to FlightComputer.ComputeControl on the solver thread for exactly one call, record that in __state, and put the struct back in a finalizer
category: patching
group: harmony
topic: run-cleanup-or-recover-from-exceptions-with-a-finalizer
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/compendium/features/flight-computer-root-attitude-tracking]
techniques: [harmony-prefix, harmony-finalizer, harmony-state]
hooks:
  - harmony-prefix KSA.FlightComputer.ComputeControl
  - harmony-finalizer KSA.FlightComputer.ComputeControl
lifecycle: [physics:vehicle-solver]
game_types:
  - KSA.FlightComputer
  - KSA.FlightComputerNavigation
  - KSA.NavigationTarget
  - KSA.PhysicsBubble
  - KSA.VehicleUpdateState
tags: [harmony, finalizer, prefix, state, flight-computer, worker-thread, ref-struct]
---

Compendium lets the stock Toward and Away attitude buttons track a star or barycenter. The stock flight
computer already knows how; it only needs `nav.Target` to be non-null. The mod supplies that value to the
solver's copy of the flight computer for exactly one `ComputeControl` call, then takes it away again. The
mechanism is the general `__state` rule: an `out` or `ref` `__state` parameter carries data from a prefix to
a postfix or finalizer, here a single `bool` that says "I injected, so put it back".

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by Compendium in
[Flight-computer attitude tracking against a root target](../../../../mods/compendium/features/flight-computer-root-attitude-tracking.md).

## Background: how the game does it

`ComputeControl` takes the navigation struct by `ref readonly`, which is `in` at the call site:

```csharp
// current/decomp/KSA/FlightComputer.cs L400-L404
public void ComputeControl(ref readonly FlightComputerNavigation nav, ref readonly ManualControlInputs inputs, ref FlightComputerOutput outputs)
{
	outputs.Clear();
	IsThrottleGLoadLimited = false;
	Ctrl2Asmb = nav.Ctrl2Body;
```

Source: [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L400-L404)[^ksa-flightcomputer]

The solver rebuilds `vehicleUpdateState.FlightComputerNavigation` before each call, passing `vehicle.Target`,
and passes the *field* by `in` ([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1404-L1415)[^ksa-physicsbubble]). The same method is called from three places
in `PhysicsBubble`: a lone-vehicle analytic segment, the full-physics pre-step, and every flight-computer tick
inside the substep loop ([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1277), [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1414),
[current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1794)). `NavigationTarget` is a struct, `FlightComputerNavigation.Target` a nullable
of it ([current/decomp/KSA/FlightComputerNavigation.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputerNavigation.cs#L5-L21)[^ksa-flightcomputernavigation]).

Inside, `UpdateAttitudeTarget` falls back to inertial null rotation when the attitude frame is `Dock` and
`nav.Target` has no value:

```csharp
// current/decomp/KSA/FlightComputer.cs L1264-L1277
private void UpdateAttitudeTarget(ref readonly FlightComputerNavigation nav)
{
	if (AttitudeFrame == VehicleReferenceFrame.BurnBody && Burn == null)
	{
		SetInertialNullRot();
	}
	if (AttitudeFrame == VehicleReferenceFrame.Dock)
	{
		NavigationTarget? target = nav.Target;
		if (!target.HasValue)
		{
			SetInertialNullRot();
		}
	}
```

Source: [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1264-L1277)

So with no native target, "track toward" does nothing, whatever the GUI buttons were set to.

## Lifecycle and timing

`ComputeControl` runs on vehicle worker threads (`physics:vehicle-solver`), on the solver's *copy* of the
flight computer (`VehicleUpdateState.FlightComputer`), once per flight-computer tick rather than once per
integration substep. These jobs run between `Universe.ExecuteNextVehicleSolvers` in one frame's
`PrepareFrame` and the wait before `ApplyVehicleSolvers` in the next ([Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md), [Run code inside the physics step on worker threads](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md)). The prefix may
therefore read only worker-safe data: a `ConditionalWeakTable`, `Volatile.Read` of shared state and plain
struct math. It must not touch ImGui or Vulkan. Results reach the real vehicle at `frame:apply-solvers`,
when the solver's flight computer is copied back, so a `StopRootTracking` done on the copy becomes the
vehicle's state at the next apply.

## Steps

1. **Declare the argument you want to edit as `ref`.** Harmony lets a prefix take the original's `in`
   argument by `ref`; the write lands in the caller's storage (here the `FlightComputerNavigation` field).
   Name it exactly like the original's parameter (`nav`). Declare `out bool __state` and set it first:

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

2. **Inject only when the mode needs it.** `ApplyControlTarget` acts only when `nav.Target` is empty and the
   computer is tracking Toward or Away, or is in the `Dock` frame holding or following a custom attitude. It
   finds the state through `computer.BurnPlan`, the object that the solver's copy *shares by reference* with
   the live vehicle's computer, and builds a `NavigationTarget` for the root body. If the body is no longer a
   loaded root, it stops tracking on the copy instead:

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

3. **Restore in a finalizer.** The finalizer receives the same `__state`. It sets the target back to `null`
   only when the prefix put one there, so the `VehicleUpdateState` field is left exactly as the game built it,
   whether `ComputeControl` returned normally or threw:

```csharp
// src/RootTargetPatches.cs L52-L60
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

Source: [src/RootTargetPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetPatches.cs#L52-L60)

   The finalizer returns `void`, so it does not swallow an exception from the original. In build
   2026.10.10.5554 nothing else reads that field after the call, so the restore is defensive rather than
   required, but it keeps the contract that the patch leaves no trace.

4. **Make the worker-side lookup thread-safe.** The table is a `ConditionalWeakTable` keyed by the `BurnPlan`
   (see [Look the state up through __instance.BurnPlan in a ComputeControl prefix](../patch-methods-that-run-on-background-threads/patch-methods-that-run-on-background-threads-5.md)), written only on the main thread between apply
   and dispatch.

5. **Verify.** Select a fixed star or barycenter as a navigation-only root target, press Toward: the vehicle
   rotates to point at it. Clear the target: tracking stops. Pressing a button for a body that was unloaded stops
   tracking through the copy-back.

## Complete example

| File | Role |
|---|---|
| [src/RootTargetPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetPatches.cs#L40-L60) | Prefix with `__state` and restoring finalizer |
| [src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L189-L207) | Mode check, burn-plan lookup, injection or stop |
| [src/RootTargetFlightComputerPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetFlightComputerPatches.cs#L78-L95)[^compendium-roottargetflightcomputerpatches] | Enable the Toward and Away buttons for root targets |

## Variations and gotchas

- **`__state` rules.** `__state` is created by the prefix (`out`), then passed by value to the postfix and
  finalizer. Checked against Lib.Harmony 2.4.2: it reaches both, and the finalizer also receives it when the
  original throws.
- **A `ref` edit must be paired with a restore.** Writing through `ref` mutates caller storage. Without the
  finalizer the injected target would persist on the field.
- **Hot path.** `ComputeControl` runs for every vehicle at every tick. The prefix exits after one null check
  unless the mode needs a target.
- **Silent fallback.** If the burn-plan lookup misses (another mod replaces the copy's plan, or a future build stops
  sharing it), stock code drops to inertial null rotation without an error.

## See also

- [Restore what a prefix changed around one game call](run-cleanup-or-recover-from-exceptions-with-a-finalizer-1.md), [Swallow an exception and finish the method's remaining work](run-cleanup-or-recover-from-exceptions-with-a-finalizer-2.md), [Prefix, postfix and finalizer as a transaction around loads](run-cleanup-or-recover-from-exceptions-with-a-finalizer-4.md), [ThreadStatic override set in try/finally around the mod's own submission](run-cleanup-or-recover-from-exceptions-with-a-finalizer-5.md), [Thread-static construction context scoping a transpiler and a prefix](run-cleanup-or-recover-from-exceptions-with-a-finalizer-6.md), [Mark the asker in a Priority.First prefix, clear it in a Priority.Last finalizer](run-cleanup-or-recover-from-exceptions-with-a-finalizer-7.md)
- [Harmony finalizers, cleanup and scoped overrides](overview.md)
- [Flight-computer attitude tracking against a root target](../../../../mods/compendium/features/flight-computer-root-attitude-tracking.md)
- [Patching private and overloaded game methods by name](../../internals/patch-a-private-game-method-by-name/overview.md) (the `ref readonly` argument form)
- Related task: synthesize-a-navigation-target-for-the-flight-computer

[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputernavigation]: KSA.FlightComputerNavigation (decompiled, build 2026.10.10.5554)
[^compendium-roottargetpatches]: Compendium/src/RootTargetPatches.cs
[^compendium-rootnavigationtargets]: Compendium/src/RootNavigationTargets.cs
[^compendium-roottargetflightcomputerpatches]: Compendium/src/RootTargetFlightComputerPatches.cs
