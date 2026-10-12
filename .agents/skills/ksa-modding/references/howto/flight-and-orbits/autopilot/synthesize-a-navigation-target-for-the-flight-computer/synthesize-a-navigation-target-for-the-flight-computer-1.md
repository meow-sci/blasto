---
type: How-To
title: Inject a NavigationTarget into ComputeControl with a prefix, __state and a restoring finalizer
description: Give the stock Toward, Away and Dock-frame hold logic a navigation target the game would not build, on the solver's flight-computer copy, for one call, and put the struct back afterwards
category: flight-and-orbits
group: autopilot
topic: synthesize-a-navigation-target-for-the-flight-computer
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/compendium/features/flight-computer-root-attitude-tracking]
techniques: [harmony-prefix, harmony-finalizer, conditional-weak-table]
hooks:
  - harmony-prefix KSA.FlightComputer.ComputeControl
  - harmony-finalizer KSA.FlightComputer.ComputeControl
lifecycle: [physics:vehicle-solver, frame:apply-solvers, frame:input]
game_types:
  - KSA.FlightComputer
  - KSA.FlightComputerNavigation
  - KSA.NavigationTarget
  - KSA.BurnPlan
  - KSA.Vehicle
  - KSA.VehicleUpdateState
tags: [navigation-target, compute-control, finalizer, toward, away, dock-frame, worker-thread]
---

Outcome: with a fixed star or barycenter selected as a navigation-only target, the native TGT panel's Toward and Away buttons and its rotation hold work, because the stock controller finally sees a non-null `nav.Target`. The stock flight computer already implements all of this; Compendium supplies
the value to the solver's copy, for exactly one call at a time.[Flight-computer attitude tracking against a root target](../../../../mods/compendium/features/flight-computer-root-attitude-tracking.md) Prerequisites: StarMap and Harmony.

## Background: how the game does it

See the [overview](overview.md). Buttons: `Vehicle.IsDisabled(FlightComputerAttitudeTrackTarget)` disables `Toward`, `Away`, `Antivel` and `Align` while `Target == null`.[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6711-L6733)
A gauge press adds an `InputEvents.FlightComputerInputData` which calls `Vehicle.SetEnum`: a track target goes to `FlightComputer.TrackTarget` and the navball switches to the track target's frame (`Dock` for `Toward`/`Away`); a reference frame goes to `RateHold`.[^ksa-gaugebuttonflightcomputer] [current/decomp/KSA/GaugeButtonFlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonFlightComputer.cs#L130-L143)
[current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6362-L6372) Where `nav` comes from: the solver rebuilds `FlightComputerNavigation` before each call with `CreateFromAnalyticStates` or `CreateFromPhysicsStates`, passing `vehicle.Target`, and those call `NavigationTarget.Create`, which returns null when the target is null.[^ksa-flightcomputernavigation] [current/decomp/KSA/FlightComputerNavigation.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputerNavigation.cs#L30-L66)
`ComputeControl` takes `ref readonly FlightComputerNavigation nav` passed `in` from the `VehicleUpdateState` field, so a prefix declaring the parameter `ref` aliases the caller's storage.[^ksa-vehicleupdatestate] [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L30)

## Lifecycle and timing

- **`physics:vehicle-solver`.** `ComputeControl` runs on the solver's copy of the flight computer (`VehicleUpdateState.FlightComputer` is `UpdateData.NewFlightComputer`) in three places in `PhysicsBubble` (`ApplySingleVehicleMotion`, `FullPhysicsPreStep` when a wake is due, `FullPhysicsStepUntil` at every flight-computer tick), at least `1 / MaxFlightComputerRate` of simulation time apart, so the prefix runs per tick, not per substep.[^ksa-physicsbubble] [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1264-L1277) [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1391-L1414)
- **Safe there:** a `ConditionalWeakTable` lookup, `Volatile.Read` of shared state, the `Universe.Roots` array, stored positions and the parent body's position at `nav.Time`; writes only to the solver's own `nav` and flight-computer copy; no managed allocation, no ImGui.
- **Results reach the vehicle at `frame:apply-solvers`** via `FlightComputer.CopyFrom`, so a `StopRootTracking` done on the copy becomes the vehicle's state at the next apply. Commands (`FlightComputerInputBuffer`) apply at `frame:input`, inside `ApplyInputEvents`.

See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Enable the buttons.** A postfix on `Vehicle.IsDisabled(FlightComputerAttitudeTrackTarget)` sets `__result = !IsControllable` for `Toward` and `Away` when a root target exists; a prefix on the private `Vehicle.Hovered(FlightComputerAttitudeTrackTarget)` returns false to skip the "no target" tooltip:

```csharp
// src/RootTargetFlightComputerPatches.cs L78-L95
[HarmonyPatch(typeof(Vehicle), "IsDisabled", new[] { typeof(FlightComputerAttitudeTrackTarget) })]
[HarmonyPostfix]
private static void EnableRootTracking(Vehicle __instance, FlightComputerAttitudeTrackTarget trackTarget, ref bool __result)
{
    if (trackTarget is FlightComputerAttitudeTrackTarget.Toward or FlightComputerAttitudeTrackTarget.Away &&
        RootNavigationTargets.GetTarget(__instance) != null)
    {
        __result = !__instance.IsControllable;
    }
}

[HarmonyPatch(typeof(Vehicle), "Hovered", new[] { typeof(FlightComputerAttitudeTrackTarget) })]
[HarmonyPrefix]
private static bool RootTrackingTooltip(Vehicle __instance, FlightComputerAttitudeTrackTarget trackTarget)
{
    return !__instance.IsControllable || RootNavigationTargets.GetTarget(__instance) == null ||
        trackTarget is not (FlightComputerAttitudeTrackTarget.Toward or FlightComputerAttitudeTrackTarget.Away);
}
```

2. **Prefix with `ref` and `out bool __state`;** inject only when `nav.Target` is null:

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

3. **Inject only when the mode needs it:** `Toward`/`Away`, or the `Dock` frame with track target `None` (rotation hold) or `Custom`. Look the state up by `__instance.BurnPlan`, the object the solver's copy shares with the live computer:

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

4. **Synthesize the struct** by mirroring the game's other-SOI branch: position and velocity are the root's ECL values minus the vehicle parent's (at `time` when the parent is an `IOrbiter`), rotated by `parent.GetCce2Cci()`; `Dock2Cci` from `VehicleReferenceFrameEx.GetDock2Cci(bodyFixed2Ecl ∘ cce2Cci, Identity)`; frame rates from the body rates in the same frame:

```csharp
// src/RootNavigationTargets.cs L165-L179
public static NavigationTarget CreateNavigationTarget(Astronomical body, IParentBody parent, UniverseTime time)
{
    double3 parentPosition = parent is IOrbiter positionOrbiter
        ? positionOrbiter.GetPositionEcl(time) : parent.GetPositionEcl();
    double3 parentVelocity = parent is IOrbiter velocityOrbiter
        ? velocityOrbiter.GetVelocityEcl(time) : parent.GetVelocityEcl();
    doubleQuat ecl2Cci = parent.GetCce2Cci();
    return new NavigationTarget
    {
        PositionCci = (body.GetPositionEcl() - parentPosition).Transform(ecl2Cci),
        VelocityCci = (body.GetVelocityEcl() - parentVelocity).Transform(ecl2Cci),
        Dock2Cci = VehicleReferenceFrameEx.GetDock2Cci(doubleQuat.Concatenate(body.GetBodyFixed2Ecl(), ecl2Cci), doubleQuat.Identity),
        DockFrameRatesCci = body.GetBodyRates().Transform(doubleQuat.Concatenate(body.GetBodyFixed2Ecl(), ecl2Cci))
    };
}
```

   For both root types the game returns an identity body orientation, zero body rates and zero velocity, so the TGT frame is fixed ECL axes and the relative velocity is just the vehicle's own.
5. **Restore in a finalizer** (`nav.Target = null` when the prefix injected). A finalizer runs whether or not the original throws.
6. **Rotation hold is inertial.** In `Dock` with track target `None`, the target rates are `AttitudeFrame.GetFrameRatesCci(in nav)`, which for `Dock` is the target's `DockFrameRatesCci`; a root's rates are zero and its orientation is identity, so the hold is inertial relative to the root's fixed frame, not continuous pointing.

## Complete example

| File | Role |
|---|---|
| [src/RootTargetPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetPatches.cs#L40-L60) | prefix with `__state` and the restoring finalizer |
| [src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L189-L207) | mode check, burn-plan lookup, injection or stop |
| [src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L143-L150) | `StopRootTracking` |
| [src/RootTargetFlightComputerPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetFlightComputerPatches.cs#L78-L95) | enable Toward/Away and suppress the tooltip |
| [src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L146-L171) | Compendium's Track Toward / Track Away buttons |

## Variations and gotchas

- **Key by a shared reference**, never by the flight computer instance: the copy differs.
- **The restore is defensive in build 5554**: nothing else reads the state field, but another mod might.
- **Stock falls back to inertial null-rotation** when `Dock` has no target; if your state vanished (vehicle gone), call `StopRootTracking` on the copy.
- Tracking, rotation hold and the navball marker are separate mechanisms; the marker needs [approach 2](synthesize-a-navigation-target-for-the-flight-computer-2.md).

## See also

- [Overview](overview.md); [navball transpilers](synthesize-a-navigation-target-for-the-flight-computer-2.md)
- [Flight-computer attitude tracking against a root target](../../../../mods/compendium/features/flight-computer-root-attitude-tracking.md)
- [Set or clear a vehicle's target, including bodies the game cannot target](../set-and-clear-the-vehicle-target/overview.md)

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-gaugebuttonflightcomputer]: KSA.GaugeButtonFlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputernavigation]: KSA.FlightComputerNavigation (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatestate]: KSA.VehicleUpdateState (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
