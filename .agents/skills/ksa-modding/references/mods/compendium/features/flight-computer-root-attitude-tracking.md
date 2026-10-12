---
type: Mod Feature
title: Flight-computer attitude tracking against a root target
description: A prefix and finalizer on FlightComputer.ComputeControl inject a synthesized NavigationTarget into the solver thread's navigation struct for the duration of one call, so the stock attitude controller runs Toward, Away and TGT-frame rotation hold against a target that has no orbit
mod: compendium
category: flight-and-orbits
techniques: [harmony-prefix, harmony-finalizer, harmony-postfix, harmony-state]
hooks:
  - harmony-prefix KSA.FlightComputer.ComputeControl
  - harmony-finalizer KSA.FlightComputer.ComputeControl
  - harmony-postfix KSA.Vehicle.IsDisabled(FlightComputerAttitudeTrackTarget)
  - harmony-prefix KSA.Vehicle.Hovered(FlightComputerAttitudeTrackTarget)
lifecycle: [physics:vehicle-solver, frame:apply-solvers, frame:input, frame:draw-ui, frame:render]
game_types:
  - KSA.FlightComputer
  - KSA.FlightComputerNavigation
  - KSA.NavigationTarget
  - KSA.PhysicsBubble
  - KSA.VehicleUpdateState
  - KSA.VehicleUpdateData
  - KSA.BurnPlan
  - KSA.Vehicle
  - KSA.InputEvents
  - KSA.GaugeButtonFlightComputer
  - KSA.FlightComputerAttitudeTrackTarget
  - KSA.FlightComputerAttitudeTrackTargetEx
  - KSA.VehicleReferenceFrameEx
tags: [flight-computer, attitude, harmony, finalizer, worker-thread, vehicle-solver, targets]
---

With a fixed star or barycenter selected as a [navigation-only root target](root-navigation-targets.md),
the native TGT panel's Toward and Away buttons and its rotation-hold button work, and Compendium's own
Track Toward / Track Away buttons do the same. Toward keeps the vehicle pointed at the root; rotation
hold in the TGT frame holds attitude relative to the root's fixed orientation, which is an inertial
hold, not continuous pointing.[^cmp-readme-md]

The stock flight computer already implements all of this; it only needs `nav.Target` to be non-null.
Compendium supplies that value to the copy of the flight computer that runs on the vehicle solver
threads, for exactly one call at a time, and puts the struct back afterwards.

## How it works

1. **Enabling the buttons.** The stock `Vehicle.IsDisabled(FlightComputerAttitudeTrackTarget)` disables
   `Toward`, `Away`, `Antivel` and `Align` while `Target == null`
   ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6711-L6733)).[^ksa-vehicle] A postfix sets `__result = !IsControllable` for
   `Toward` and `Away` when a root target exists, and a prefix on the private
   `Vehicle.Hovered(FlightComputerAttitudeTrackTarget)` returns `false` (skips the "no target" tooltip)
   in the same case ([src/RootTargetFlightComputerPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetFlightComputerPatches.cs#L78-L95),
   [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6779-L6808)).[^cmp-roottargetflightcomputerpatches] The TGT reference-frame
   button is enabled by the `IsDisabled(VehicleReferenceFrame)` postfix described in
   [the navball feature](navball-target-marker-and-tgt-frame.md).
2. **Commanding.** A gauge button press adds an `InputEvents.FlightComputerInputData` with the button's
   enum value ([current/decomp/KSA/GaugeButtonFlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonFlightComputer.cs#L130-L143)).[^ksa-gaugebuttonflightcomputer]
   Compendium's own buttons queue the same struct through its request queue
   ([src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L48-L60)).[^cmp-rootnavigationtargets] Applying it calls
   `Vehicle.SetEnum`: a track target goes to `FlightComputer.TrackTarget` and switches the navball to the
   track target's frame (`Dock` for `Toward`/`Away`); a reference frame goes to `FlightComputer.RateHold`,
   which sets `AttitudeFrame` and resets the track target to `None`
   ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6362-L6372), [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1977-L1998),
   [current/decomp/KSA/FlightComputerAttitudeTrackTargetEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputerAttitudeTrackTargetEx.cs#L78-L113)).[^ksa-flightcomputer] [^ksa-flightcomputerattitudetracktargetex]
3. **Why the stock controller needs a target.** Inside `ComputeControl`, `UpdateAttitudeTarget`
   immediately falls back to `SetInertialNullRot` when `AttitudeFrame == Dock` and `nav.Target` is null;
   `Toward` and `Away` derive their attitude from `nav.Target` through `GetTgt2Cci` and
   `GetTgtFrameRatesCci` ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1264-L1303),
   [current/decomp/KSA/FlightComputerAttitudeTrackTargetEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputerAttitudeTrackTargetEx.cs#L23-L47)).
4. **Where `nav` comes from.** The vehicle solver rebuilds `vehicleUpdateState.FlightComputerNavigation`
   before each call with `FlightComputerNavigation.CreateFromAnalyticStates` or
   `CreateFromPhysicsStates`, passing `vehicle.Target`; those call `NavigationTarget.Create`, which returns
   null for a vehicle whose native target is null ([current/decomp/KSA/FlightComputerNavigation.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputerNavigation.cs#L30-L66)).[^ksa-flightcomputernavigation]
   `ComputeControl` takes it as `ref readonly FlightComputerNavigation nav`, passed `in` from the
   `VehicleUpdateState` field ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L400), [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L30)).[^ksa-vehicleupdatestate]
5. **Prefix.** `AddControlTarget(FlightComputer __instance, ref FlightComputerNavigation nav, out bool __state)`
   declares the parameter as `ref`, which aliases the caller's storage. If `nav.Target` is null it calls
   `ApplyControlTarget` and records in `__state` whether a target was injected
   ([src/RootTargetPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetPatches.cs#L40-L50)).[^cmp-roottargetpatches]
6. **Only when the mode needs it.** `ApplyControlTarget` acts when the computer tracks `Toward` or
   `Away`, or is in the `Dock` frame with track target `None` (rotation hold) or `Custom` (a custom
   attitude in the TGT frame). It looks the state up by `__instance.BurnPlan`, the object the solver's
   copy shares with the live vehicle's computer, and writes
   `nav.Target = CreateNavigationTarget(body, nav.Parent, nav.Time)`. If the body is no longer a loaded
   root (or the state was cleared), it calls `StopRootTracking` on the copy instead
   ([src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L189-L207)).
7. **Finalizer.** `RestoreControlTarget(ref FlightComputerNavigation nav, bool __state)` sets
   `nav.Target = null` again when the prefix injected it ([src/RootTargetPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetPatches.cs#L52-L60)). A
   Harmony finalizer runs whether or not the original throws, so the `VehicleUpdateState` field is
   always left as the game built it. In build 2026.10.10.5554 nothing else reads that field (the three
   `ComputeControl` calls are its only uses), so the restore is defensive rather than required.
8. **Rotation hold is inertial.** In `Dock` frame with track target `None`, the target rates are
   `AttitudeFrame.GetFrameRatesCci(in nav)`, which for `Dock` is the target's `DockFrameRatesCci`
   ([current/decomp/KSA/VehicleReferenceFrameEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleReferenceFrameEx.cs#L54-L70)).[^ksa-vehiclereferenceframeex] A root's rates are
   zero and its orientation is identity, so the hold is inertial relative to the root's fixed frame.

## Lifecycle and timing

- **`physics:vehicle-solver`, worker threads.** `ComputeControl` is called by `PhysicsBubble` on the
  solver's copy of the flight computer (`VehicleUpdateState.FlightComputer` is
  `UpdateData.NewFlightComputer`, [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L153)) in three places:
  `ApplySingleVehicleMotion` for a lone vehicle on an analytic segment
  ([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1264-L1277)), `FullPhysicsPreStep` when the computer's wake-up time is
  due at the start of a full-physics step ([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1782-L1797)), and
  `FullPhysicsStepUntil` at every flight-computer tick inside the substep loop
  ([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1391-L1414)).[^ksa-physicsbubble] The tick spacing is at least
  `1 / MaxFlightComputerRate` of simulation time ([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1387-L1388)), so the
  prefix runs per flight-computer tick, not per integration substep. These jobs run between
  `Universe.ExecuteNextVehicleSolvers` in one frame's `PrepareFrame` and the wait before
  `ApplyVehicleSolvers` in the next ([lifecycle reference](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md)).
- **What is safe there.** The prefix reads a `ConditionalWeakTable` (thread-safe), the shared state
  through `Volatile.Read`, the `Universe.Roots` array (replaced only when a system loads), the roots'
  stored positions, and the parent body's position at `nav.Time`. It writes only the solver's own
  `nav` struct and the solver's own flight-computer copy. The game's `NavigationTarget.Create` makes the
  same kind of parent and target position calls on these threads. Everything is struct math with no
  managed allocation, and nothing touches ImGui or Vulkan.
- **Results reach the vehicle at `frame:apply-solvers`.** `Vehicle.UpdateFromTaskResultsUnsynchronized`
  copies the solver's computer back with `FlightComputer.CopyFrom` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2435),
  [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2530)), so a `StopRootTracking` done on the copy becomes the vehicle's state at
  the next apply.
- **Commands at `frame:input`.** `FlightComputerInputBuffer` is applied inside
  `InputEvents.ApplyInputEvents`, after the apply and before the next dispatch snapshots the computer
  ([current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1114-L1140)).[^ksa-inputevents]
- **Buttons at `frame:draw-ui` and `frame:render`.** The `Hovered` prefix runs only in the draw:
  `Program.OnDrawUiThreadSafe` calls `GaugeCanvas.CanvasesOnDrawUi`, and `GaugeCanvas.OnDrawUi`
  dispatches hover and release, which reach `Vehicle.HoveredEnum` and, through
  `GaugeButtonFlightComputer.OnReleased`, `IsDisabled` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3178-L3205),
  [current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L1139-L1161)).[^ksa-program] [^ksa-gaugecanvas] The `IsDisabled` postfix runs a second time later
  in the same frame: the button's disabled look is packed in `GaugeButtonFlightComputer.PackData`
  ([current/decomp/KSA/GaugeButtonFlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonFlightComputer.cs#L156-L173)), which `GaugeBundle.UpdateInstanceBuffer` calls while
  `Program.RenderGame` records the gauge canvases ([current/decomp/KSA/GaugeBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeBundle.cs#L116-L146),
  [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4937-L4940)).[^ksa-gaugebundle] Both calls are on the main thread and only read
  the side table.
- **Agrees with the copy-back.** Because `ApplyVehicleSolvers` copies the worker's computer over the
  vehicle's unconditionally, a main-thread write to `FlightComputer` after dispatch would be lost.
  Compendium makes every main-thread flight-computer write (`StopRootTracking` in `Clear`, the queued
  Toward/Away command) inside `ApplyInputEvents`, before the next snapshot, and the only worker-side
  write (`StopRootTracking` on the copy) travels back through the same copy-back. KSA-AdvancedFlightComputer
  documents the same contract in
  [Reach the physics worker through the references FlightComputer.CopyFrom shares](../../ksa-advancedflightcomputer/features/flight-computer-worker-copy-channels.md).

## Key code

| Path | Role |
|---|---|
| [src/RootTargetPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetPatches.cs#L40-L60) | Prefix with `__state` and the restoring finalizer |
| [src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L189-L207) | Mode check, burn-plan lookup, injection or stop |
| [src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L143-L150) | `StopRootTracking` |
| [src/RootTargetFlightComputerPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetFlightComputerPatches.cs#L78-L95) | Enable Toward/Away and suppress the no-target tooltip |
| [src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L146-L171) | Compendium's Track Toward / Track Away buttons |

## Snippets

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

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `FlightComputer.ComputeControl` | [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L400-L521) | Prefix and finalizer; runs on solver threads |
| `FlightComputer.UpdateAttitudeTarget` (private, called by `ComputeControl`) | [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1264-L1303) | Drops `Dock` to inertial without a target |
| `FlightComputer.TrackTarget`, `RateHold`, `AttitudeFrame`, `AttitudeTrackTarget`, `BurnPlan` | [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1977-L1998) | Modes the injection serves; table key |
| `FlightComputerNavigation` | [current/decomp/KSA/FlightComputerNavigation.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputerNavigation.cs#L5-L66) | Struct whose `Target` is injected and restored |
| `FlightComputerAttitudeTrackTargetEx.GetTarget`, `GetNavballReferenceFrame` | [current/decomp/KSA/FlightComputerAttitudeTrackTargetEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputerAttitudeTrackTargetEx.cs#L23-L113) | Toward/Away attitude from `nav.Target` |
| `PhysicsBubble.ApplySingleVehicleMotion`, `FullPhysicsPreStep`, `FullPhysicsStepUntil` | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1264-L1277) | Call sites and thread |
| `Vehicle.IsDisabled(FlightComputerAttitudeTrackTarget)`, `Vehicle.Hovered(...)` (private) | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6711-L6808) | Postfix and prefix |
| `Vehicle.SetEnum` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6321-L6372) | Where the queued Toward/Away and frame commands land |
| `InputEvents.FlightComputerInputData` | [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L582-L619) | Command route |

## Pitfalls and version notes

- **`ref readonly` patched as `ref`.** Harmony lets a prefix take an `in` parameter by `ref` and write
  through it. The write lands in the caller's field, which is why the finalizer exists; copy this
  pattern only together with a restore.
- **Hot path.** `ComputeControl` runs for every vehicle at every flight-computer tick. The prefix exits
  after one null check unless the mode needs a target, and the table lookup is the only shared-state
  access.
- **Antivel and Align stay disabled.** Only `Toward` and `Away` are enabled for root targets; alignment
  needs a docking port and remains native-target only.[^cmp-readme-md]
- **Silent fallback.** If the burn-plan lookup misses (another mod replaces the copy's plan, or a future
  build stops sharing it), the stock code drops to inertial null rotation without an error.
- **Private targets by name.** `IsDisabled` and `Hovered` are private overloads resolved by name and
  argument type; renames break the patch at load (see the install pitfall in
  [the navball feature](navball-target-marker-and-tgt-frame.md)).

## See also

- [Navigation-only root targets](root-navigation-targets.md)
- [Navball target marker and TGT frame for a target without an orbit](navball-target-marker-and-tgt-frame.md)
- [Closest approach to a root target](root-closest-approach-search.md)
- [Mutate simulation state safely relative to the physics step](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md)
- [Reach the physics worker through the references FlightComputer.CopyFrom shares](../../ksa-advancedflightcomputer/features/flight-computer-worker-copy-channels.md) (KSA-AdvancedFlightComputer)
- [Edit ComputeControl inputs by ref in a prefix and restore them in a finalizer](../../../howto/flight-and-orbits/autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/drive-the-flight-computer-and-attitude-modes-from-a-mod-7.md) - how-to article demonstrating this feature
- [Inject a NavigationTarget into ComputeControl with a prefix, __state and a restoring finalizer](../../../howto/flight-and-orbits/autopilot/synthesize-a-navigation-target-for-the-flight-computer/synthesize-a-navigation-target-for-the-flight-computer-1.md) - how-to article demonstrating this feature
- [Look the state up through __instance.BurnPlan in a ComputeControl prefix](../../../howto/patching/harmony/patch-methods-that-run-on-background-threads/patch-methods-that-run-on-background-threads-5.md) - how-to article demonstrating this feature
- [Prefix with __state and a finalizer that restores an edited input](../../../howto/patching/harmony/run-cleanup-or-recover-from-exceptions-with-a-finalizer/run-cleanup-or-recover-from-exceptions-with-a-finalizer-3.md) - how-to article demonstrating this feature

[^cmp-readme-md]: Compendium: README.md
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^cmp-roottargetflightcomputerpatches]: Compendium: src/RootTargetFlightComputerPatches.cs
[^ksa-gaugebuttonflightcomputer]: KSA.GaugeButtonFlightComputer (decompiled, build 2026.10.10.5554)
[^cmp-rootnavigationtargets]: Compendium: src/RootNavigationTargets.cs
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputerattitudetracktargetex]: KSA.FlightComputerAttitudeTrackTargetEx (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputernavigation]: KSA.FlightComputerNavigation (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatestate]: KSA.VehicleUpdateState (decompiled, build 2026.10.10.5554)
[^cmp-roottargetpatches]: Compendium: src/RootTargetPatches.cs
[^ksa-vehiclereferenceframeex]: KSA.VehicleReferenceFrameEx (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^ksa-inputevents]: KSA.InputEvents (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-gaugecanvas]: KSA.GaugeCanvas (decompiled, build 2026.10.10.5554)
[^ksa-gaugebundle]: KSA.GaugeBundle (decompiled, build 2026.10.10.5554)
