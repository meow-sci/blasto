---
type: How-To
title: Patch every attitude-mode writer so the player can take back control
description: Release your Custom target whenever any stock path changes the attitude mode (SetEnum, ToggleEnum, SetStabilization, TrackTarget, RateHold, SetNullRot, kitten actions) and when the kitten MMU takes over, without breaking the game method
category: flight-and-orbits
group: autopilot
topic: drive-the-flight-computer-and-attitude-modes-from-a-mod
approach: 9
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ohmystars/features/attitude-mode-handoff-patches]
techniques: [harmony-prefix, reflection]
hooks:
  - harmony-prefix KSA.Vehicle.SetEnum
  - harmony-prefix KSA.Vehicle.ToggleEnum
  - harmony-prefix KSA.Vehicle.SetStabilization
  - harmony-prefix KSA.FlightComputer.TrackTarget
  - harmony-prefix KSA.FlightComputer.RateHold
  - harmony-prefix KSA.FlightComputer.SetNullRot
lifecycle: [frame:input, physics:vehicle-solver, startup:all-mods-loaded]
game_types: [KSA.Vehicle, KSA.FlightComputer, KSA.KittenEva, KSA.PhysicsBubble, KSA.VehicleUpdateState]
tags: [attitude-mode, handoff, ownership, set-enum, toggle-enum, kitten-eva, mmu, harmony]
---

Outcome: a custom attitude target that the mod re-applies every physics dispatch stops fighting the player the moment the player picks any stock attitude mode. OhMyStars installs a ring of Harmony prefixes on every path the decomp shows can change the attitude mode.[Attitude-mode hand-off patches](../../../../mods/ohmystars/features/attitude-mode-handoff-patches.md)
Prerequisites: StarMap and Harmony.

## Background: how the game does it

The ways out of a custom target are more than `TrackTarget`, `RateHold` and `SetNullRot`: `Vehicle.SetStabilization(false)` assigns `AttitudeMode = Manual` directly, `KittenEva.HandleKittenAction(ManualTrim)` writes `AttitudeMode` directly, `Vehicle.SetEnum` handles `FlightComputerAttitudeMode` by assigning it,
and the kitten MMU rewrites the target every step.[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3618-L3628) [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6321-L6407) [^ksa-flightcomputer] [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1978-L2000) [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L331-L337) The stock method being patched is about to switch to rate hold, which reads `CustomAttitudeTarget` as a body-rate command (the `None` branch of `UpdateAttitudeTarget`), so the target must be cleaned in a prefix.

| Target | Kind | Effect |
|---|---|---|
| `Vehicle.PrepareWorker(SimStep)` | prefix | runs a pending disengage, else re-applies the star target ([approach 1](drive-the-flight-computer-and-attitude-modes-from-a-mod-1.md)) |
| `FlightComputer.TrackTarget`, `RateHold`, `SetNullRot` | prefix | any non-Custom target, rate hold or null-rotation ends the mode |
| `Vehicle.SetStabilization(bool)` | prefix | `active == false` assigns `Manual` directly |
| `Vehicle.SetEnum(Enum?)`, `ToggleEnum(Enum?)` | prefix | catch-all for buttons and key bindings, filtered to attitude enums |
| `KittenEva.HandleKittenAction(KittenEvaAction)` | prefix (by name; protected) | `ManualTrim` writes the mode directly; `RateTrim` calls `RateHold` |
| `PhysicsBubble.ApplyMmuAttitudeTarget`, `ResetMmuAttitudeTarget` (private static) | prefix via `TargetMethods()` | park the star target while the MMU drives |

## Lifecycle and timing

| Patch | Thread and lifecycle |
|---|---|
| `SetEnum`, `ToggleEnum`, `SetStabilization` | main thread, `frame:input`: keys and navball gauge buttons only queue `InputEvents` entries and `ApplyInputEvents` runs them, so a gauge click lands at the next frame's input step |
| `TrackTarget`, `RateHold` | main thread: from `SetEnum`, `SetStabilization(true)`, kitten actions at `frame:input`, and from the mod's own disengage in the `PrepareWorker` prefix |
| `SetNullRot` | main thread through `RateHold`, and also vehicle worker threads: `ComputeControl` runs `UpdateBurnTarget`, which calls `SetNullRot(BurnBody)` when an auto burn completes, and `UpdateAttitudeTarget` calls `SetInertialNullRot` for `BurnBody` without a burn or `Dock` without a target |
| `ApplyMmuAttitudeTarget` / `ResetMmuAttitudeTarget` | worker threads, every substep while the MMU translates the controlled kitten |
| Install | `startup:all-mods-loaded` |

Input is applied before the dispatch in `PrepareFrame`, so a key pressed this frame clears the vehicle's entry before `PrepareWorker` would re-apply it. Worker-side calls see a copy: the flight computer on the worker is `NewFlightComputer`, so comparing it with the live `Vehicle.FlightComputer` never finds an owner; the MMU prefix instead resolves the live vehicle through
`VehicleUpdateState.ReadOnlyVehicle`. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Apply patch classes individually** (`harmony.CreateClassProcessor(typeof(X)).Patch()`) from `[StarMapAllModsLoaded]`, not `PatchAll`, and wrap every prefix body in `try`/`catch` with a warning so a failure never breaks the game method.
2. **Filter the `Enum?` argument** to the attitude-related enum types (`FlightComputerAttitudeMode`, `VehicleReferenceFrame`, non-`Custom` track target) so throttle, RCS and action bindings do not end the mode:

```csharp
// src/StarOrientationObserver.cs L152-L163
[HarmonyPatch(typeof(Vehicle), nameof(Vehicle.SetEnum))]
private static class SetEnumPatch {
    static void Prefix(Vehicle __instance, Enum? enumValue) {
        try {
            if(IsAttitudeModeChange(enumValue)) {
                OhMyStarsWindow.ClearOrientedStar(__instance.FlightComputer);
            }
        } catch(Exception ex) {
            DefaultCategory.Log.Warning($"[OhMyStars] SetEnum Prefix: {ex}");
        }
    }
}
```

3. **End the mode in the prefix**: remove the vehicle's entries and write the saved pre-mod rate command back into `CustomAttitudeTarget` and `AttitudeTarget.Zero` before stock switches to rate hold.
4. **Patch private methods with `TargetMethods()` and `AccessTools.Method`**, returning only the methods that exist; the prefix reads `__args[0]` when the type is not nameable:

```csharp
// src/StarOrientationObserver.cs L84-L115
// While a kitten's MMU is driving the flight computer (ApplyMmuAttitudeTarget runs every physics
// step and stomps any mod-set Custom target), the kitten's star orientation is parked via
// DetachOrientedStar, which keeps it remembered but stops the mod from fighting the MMU.
// When the MMU releases (ResetMmuAttitudeTarget), PrepareWorker re-engages the star. ReadOnlyVehicle
// returns the live Vehicle, so the patches below use it as the dictionary key; the exact instance
// type does not matter because the key is only ever compared by reference. All three involved
// members are non-public, hence the string method names.
[HarmonyPatch]
private static class KittenMmuPatch {
    private static IEnumerable<MethodBase> TargetMethods() {
        MethodBase? apply = AccessTools.Method(typeof(PhysicsBubble), "ApplyMmuAttitudeTarget");
        if(apply != null)
            yield return apply;

        MethodBase? reset = AccessTools.Method(typeof(PhysicsBubble), "ResetMmuAttitudeTarget");
        if(reset != null)
            yield return reset;
    }

    private static bool Prefix(object[] __args) {
        try {
            Vehicle? vehicle = (__args?[0] as VehicleUpdateState)?.ReadOnlyVehicle;
            if(vehicle != null) {
                OhMyStarsWindow.DetachOrientedStar(vehicle);
            }
        } catch(Exception ex) {
            DefaultCategory.Log.Warning($"[OhMyStars] KittenMmu Prefix: {ex}");
        }

        return true;
    }
}
```

5. **Park, do not discard, when the MMU takes over** (`DetachOrientedStar`): the orientation stays remembered and `PrepareWorker` re-engages it when the MMU releases.

## Complete example

| File | Role |
|---|---|
| [src/StarOrientationObserver.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarOrientationObserver.cs#L9-L176) | all attitude patch classes |
| [src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L1076-L1137) | clean-up when the game leaves star mode |

## Variations and gotchas

- **AggressiveInlining.** `TrackTarget`, `RateHold` and `SetNullRot` are marked `AggressiveInlining`; a Harmony detour on an inlined callee may not fire for every caller, which is why the catch-all `SetEnum` and `ToggleEnum` prefixes exist.
- **Worker-thread callers touch your dictionaries.** `SetNullRot` from the worker enumerates the mod's per-vehicle collections while the main thread may be adding to them; guard them, or have the worker-side path do nothing.
- **Do not gate on `Program.ControlledVehicle`**: a vessel you switch away from keeps its target.
- A new KSA build can add another writer; re-check the list when `AttitudeMode` assignment sites change.

## See also

- [Overview](overview.md); [orient to star](drive-the-flight-computer-and-attitude-modes-from-a-mod-1.md)
- [Attitude-mode hand-off patches](../../../../mods/ohmystars/features/attitude-mode-handoff-patches.md)
- [Run a closed-loop autopilot for every vehicle each step and arbitrate control with the player](../run-an-autopilot-for-every-vehicle-each-step/overview.md)

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
