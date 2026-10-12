---
type: Mod Feature
title: Attitude-mode hand-off patches
description: Detect every stock path that changes attitude mode with Harmony prefixes on flight computer, vehicle, kitten and MMU methods so a mod-owned custom target is released and the shared rate command restored
mod: ohmystars
category: patching
techniques: [harmony-prefix, reflection, harmony-target-methods]
hooks:
  - harmony-prefix KSA.Vehicle.PrepareWorker
  - harmony-prefix KSA.FlightComputer.TrackTarget
  - harmony-prefix KSA.FlightComputer.RateHold
  - harmony-prefix KSA.FlightComputer.SetNullRot
  - harmony-prefix KSA.Vehicle.SetStabilization
  - harmony-prefix KSA.Vehicle.SetEnum
  - harmony-prefix KSA.Vehicle.ToggleEnum
  - harmony-prefix KSA.KittenEva.HandleKittenAction
  - harmony-prefix KSA.PhysicsBubble.ApplyMmuAttitudeTarget
  - harmony-prefix KSA.PhysicsBubble.ResetMmuAttitudeTarget
lifecycle: [startup:all-mods-loaded, frame:input, frame:dispatch-solvers, physics:vehicle-solver]
game_types:
  - KSA.FlightComputer
  - KSA.Vehicle
  - KSA.KittenEva
  - KSA.PhysicsBubble
  - KSA.VehicleUpdateState
  - KSA.VehicleUpdateData
  - KSA.InputEvents
  - KSA.GaugeButtonFlightComputer
tags: [harmony, prefix, flight-computer, attitude, kitten, mmu, inlining]
---

Oh My Stars' "Orient to Star" mode is a custom attitude target that the mod re-applies every physics
dispatch, so it must notice the moment the player picks any stock attitude mode, or it would keep
overriding the player. It does that with a ring of Harmony prefixes on every path the decomp shows
can change the attitude mode, and it cleans up the shared `CustomAttitudeTarget` field on the way
out. Together with the save hooks and the star-technique patches these are all of the mod's Harmony
patches.[^oms-starorientationobserver-cs]

## How it works

Every patch class is applied individually with `harmony.CreateClassProcessor(typeof(X)).Patch()`
rather than `PatchAll`, from `[StarMapAllModsLoaded]` on the Harmony id `nanonestor.ohmystars`
([src/StarOrientationObserver.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarOrientationObserver.cs#L9-L19), [src/OhMyStarsEntryPoint.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsEntryPoint.cs#L13-L26)). Each
prefix wraps its body in `try`/`catch` and logs a warning through `DefaultCategory.Log`, so a
failure never breaks the game method.

| Target (verified in decomp) | Kind | Mod class | What it does and why |
|---|---|---|---|
| `Vehicle.PrepareWorker(SimStep)` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423)) | prefix | `PrepareWorkerPatch` | Runs a pending disengage, else re-applies the vehicle's star target before the worker snapshot. See [Orient a vessel to a star](orient-to-star.md) |
| `FlightComputer.TrackTarget(FlightComputerAttitudeTrackTarget)` ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1993-L1998)) | prefix | `TrackTargetPatch` | Any target except `Custom` (prograde, target, burn...) ends star mode |
| `FlightComputer.RateHold(VehicleReferenceFrame)` ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1978-L1983)) | prefix | `RateHoldPatch` | Rate hold ends star mode |
| `FlightComputer.SetNullRot(VehicleReferenceFrame)` ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L331-L337)) | prefix | `SetNullRotPatch` | Null-rotation ends star mode |
| `Vehicle.SetStabilization(bool)` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3618-L3628)) | prefix | `SetStabilizationPatch` | `active == false` sets `AttitudeMode = Manual` directly, bypassing the flight computer methods |
| `Vehicle.SetEnum(Enum?)` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6321-L6407)) | prefix | `SetEnumPatch` | Catch-all for button and key bindings: `FlightComputerAttitudeMode`, `VehicleReferenceFrame`, or a non-`Custom` track target ends star mode |
| `Vehicle.ToggleEnum(Enum?)` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6266-L6319)) | prefix | `ToggleEnumPatch` | Same filter; covers `ToggleAttitudeMode` |
| `KittenEva.HandleKittenAction(KittenEvaAction)` (protected override, [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L218-L238)) | prefix (by name string) | `KittenActionPatch` | `ManualTrim` writes `AttitudeMode` directly; `RateTrim` calls `RateHold` |
| `PhysicsBubble.ApplyMmuAttitudeTarget`, `ResetMmuAttitudeTarget` (private static, [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1891-L1967)) | prefix via `TargetMethods()` + `AccessTools.Method` | `KittenMmuPatch` | When the MMU takes over a kitten's attitude, drop the star target; reads `VehicleUpdateState.ReadOnlyVehicle` from `__args[0]` |
| `UncompressedSave.Load()`, `UncompressedSave.Make(string)` | postfix | `SaveLoadObserver` | Per-save sidecar, see [Save-scoped settings sidecar](save-scoped-settings-sidecar.md) |
| `InstancedStarTechnique.AddInstance(IViewport, float3, byte, byte4, float)` and its constructor | prefix | `StarsEditPatcher` | Separate Harmony id, applied at `[StarMapBeforeMain]`; see [Star instance restyle patch](star-instance-restyle-patch.md) |

**Why so many entry points.** The decomp shows several ways to leave a custom target that never
touch `TrackTarget`/`RateHold`/`SetNullRot`: `Vehicle.SetStabilization(false)` and
`KittenEva.HandleKittenAction(ManualTrim)` assign `AttitudeMode` directly, and `Vehicle.SetEnum`
handles `FlightComputerAttitudeMode` by assigning it too
([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6398-L6401)).[^ksa-vehicle-cs] `IsAttitudeModeChange`
filters the `Enum?` argument to the attitude-related enum types so other bindings (throttle, RCS,
actions) do not end star mode ([src/StarOrientationObserver.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarOrientationObserver.cs#L21-L29)).

**What "end star mode" does.** `OhMyStarsWindow.ClearOrientedStar(FlightComputer)` finds the vehicle
that owns the flight computer, removes its entries, and writes the saved pre-star rate command back
into `CustomAttitudeTarget` and `AttitudeTarget.Zero`
([src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L1076-L1131)).[^oms-ohmystarswindow-cs] This must happen in a
prefix: the stock method is about to switch to rate hold, which reads `CustomAttitudeTarget` as a
body-rate command (`AttitudeTrackTarget == None` branch of `UpdateAttitudeTarget`,
[current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1264-L1284)).[^ksa-flightcomputer-cs]

## Lifecycle and timing

| Patch | Thread and lifecycle |
|---|---|
| `Vehicle.SetEnum`, `ToggleEnum`, `SetStabilization` | Main thread, `frame:input`. Keys (`Vehicle.OnKey`) and navball gauge buttons (`GaugeButtonFlightComputer.OnReleased`, detected during `frame:draw-ui`) only queue `InputEvents` entries; `InputEvents.ApplyInputEvents` in `Program.PrepareFrame` runs them ([current/decomp/KSA/GaugeButtonFlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonFlightComputer.cs#L130-L143), [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L582-L619)),[^ksa-gaugebuttonflightcomputer-cs] [^ksa-inputevents-cs] so a gauge click lands at the next frame's input step |
| `KittenEva.HandleKittenAction` | Main thread, `frame:input`: reached from `SetEnum`/`ToggleEnum` with a `KittenEvaAction` |
| `FlightComputer.TrackTarget`, `RateHold` | Main thread: from `SetEnum`, `SetStabilization(true)` and `HandleKittenAction(RateTrim)` at `frame:input`, and from the mod's own `DisengagePending` inside the `PrepareWorker` prefix at `frame:dispatch-solvers` |
| `FlightComputer.SetNullRot` | Main thread through `RateHold`, and also vehicle solver worker threads (`physics:vehicle-solver`): `FlightComputer.ComputeControl` runs `UpdateBurnTarget`, which calls `SetNullRot(BurnBody)` when an auto burn completes, and `UpdateAttitudeTarget`, which calls `SetInertialNullRot` for a `BurnBody` frame without a burn or a `Dock` frame without a target ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L400-L426), [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1247-L1277)) |
| `Vehicle.PrepareWorker` | Main thread, `frame:dispatch-solvers` (`Universe.PrepareVehicleWorkers`) |
| `ApplyMmuAttitudeTarget` / `ResetMmuAttitudeTarget` | Vehicle solver worker threads, `physics:vehicle-solver`, every substep from `PhysicsBubble.AdvanceKittenLocomotion` ([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1969-L2008)); apply runs while the MMU is translating the controlled kitten, reset once when that stops |
| Install | `startup:all-mods-loaded` |

Input is applied before the dispatch in `Program.PrepareFrame`
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141-L2220)),[^ksa-program-cs] so a key pressed this frame, or a
gauge button clicked during the previous frame's UI, clears the vehicle's entry before `PrepareWorker`
would re-apply it.

**Worker-side calls see a copy.** The vehicle step runs on its own `FlightComputer` copy
(`VehicleUpdateData.NewFlightComputer`, filled by `CopyFrom` in `PrepareFromVehicle` and copied back
over the live one at the next `ApplyVehicleSolvers`)
([current/decomp/KSA/VehicleUpdateData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateData.cs#L84-L100), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2530)).[^ksa-vehicleupdatedata-cs]
When `SetNullRot` runs there, `ClearOrientedStar(FlightComputer)` compares that copy with each
oriented vehicle's live `Vehicle.FlightComputer`, never finds an owner and changes nothing, but it
still enumerates `_orientedVehicles` and `_pendingDisengage` while the main thread may be adding to
them (the window's toggle runs in `frame:draw-ui`, with the step in flight). The MMU prefix goes
further: it resolves the live `Vehicle` through `VehicleUpdateState.ReadOnlyVehicle` and calls
`ClearOrientedStar(Vehicle)`, which removes entries from four plain collections and writes the live
flight computer from the worker thread.

## Key code

| Path | Role |
|---|---|
| [src/StarOrientationObserver.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarOrientationObserver.cs#L9-L176) | All attitude patch classes |
| [src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L1076-L1137) | Clean-up when the game leaves star mode |
| [src/SaveLoadObserver.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SaveLoadObserver.cs#L19-L55) | Save hooks |
| [src/StarsEditPatcher.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditPatcher.cs#L57-L158) | Star technique patches |

## Snippets

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

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `FlightComputer.SetNullRot`, `RateHold`, `TrackTarget` | [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L331-L337) | Patched; all three are `AggressiveInlining` |
| `Vehicle.SetEnum`, `ToggleEnum`, `SetStabilization` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6266-L6407) | Patched UI entry points |
| `KittenEva.HandleKittenAction` | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L218-L238) | Patched kitten trim actions |
| `PhysicsBubble.ApplyMmuAttitudeTarget`, `ResetMmuAttitudeTarget` | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1891-L1967) | Patched MMU takeover |
| `VehicleUpdateState.ReadOnlyVehicle`, `FlightComputer` | [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L14) | Map worker state back to the live `Vehicle` |
| `FlightComputerAttitudeTrackTarget`, `FlightComputerAttitudeMode`, `VehicleReferenceFrame`, `KittenEvaAction` | [current/decomp/KSA/FlightComputerAttitudeTrackTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputerAttitudeTrackTarget.cs), [current/decomp/KSA/FlightComputerAttitudeMode.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputerAttitudeMode.cs), [current/decomp/KSA/VehicleReferenceFrame.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleReferenceFrame.cs), [current/decomp/KSA/KittenEvaAction.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEvaAction.cs) | Enum filters |
| `FlightComputer.ComputeControl`, `UpdateBurnTarget`, `UpdateAttitudeTarget` | [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L400-L426) | Worker-side callers of `SetNullRot` |
| `InputEvents.FlightComputerInputData.Apply`, `GaugeButtonFlightComputer.OnReleased` | [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L582-L619) | Where queued button and key presses reach `SetEnum`/`ToggleEnum` |

## Pitfalls and version notes

- **Inlined targets may be bypassed (unverified in game).** `SetNullRot`, `RateHold` and
  `TrackTarget` are marked `[MethodImpl(MethodImplOptions.AggressiveInlining)]`. Harmony detours a
  method's own entry point; callers into which the JIT inlined the body do not go through it. The
  `SetEnum`/`ToggleEnum`/`HandleKittenAction` prefixes cover the usual UI paths regardless, but
  `Vehicle.SetStabilization(true)` relies on the `RateHold` prefix alone. In this build `KSA.dll`
  carries a `DebuggableAttribute` with `DisableOptimizations` (blob `01 00 07 01 00 00 00 00` in
  `current/dll/KSA.dll`), under which the JIT normally does not inline, so the prefixes most likely
  fire, including the worker-side `SetNullRot` calls; an optimized game build could change that.
- **Two prefixes run on worker threads and touch main-thread state.** `SetNullRot` (worker copy,
  see Lifecycle) enumerates the mod's dictionaries without a lock; a concurrent main-thread write can
  make the enumeration throw, which the prefix's `try`/`catch` turns into a logged warning. The MMU
  prefix mutates the plain `Dictionary` and `HashSet` fields and writes the live
  `Vehicle.FlightComputer` while the main thread may be reading or writing them (the UI runs during
  the physics step). It runs on every substep while the MMU drives the kitten, and
  `ClearOrientedStar(Vehicle)` writes `CustomAttitudeTarget` and `AttitudeTarget` unconditionally,
  even for kittens that were never star-oriented. Those live-field writes are overwritten anyway at the
  next apply, when the worker's copy is copied back; the lasting effect is the removed entry.
- **Auto burns are not detected.** `IsAttitudeModeChange` ignores `FlightComputerBurnMode`, and the
  auto-burn alignment in `ComputeControl` assigns `AttitudeTrackTarget = PositiveDv` directly on the
  worker copy ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L478-L482)), bypassing every patched method. A vessel
  left in star mode keeps its entry, so the `PrepareWorker` prefix re-applies the `Custom` star target
  at every dispatch while the burn logic switches the copy to `PositiveDv` again within the step.
- **Patch-time failures are not contained.** `ApplyPatches` and `OnFullyLoaded` have no
  `try`/`catch`, and StarMap invokes `[StarMapAllModsLoaded]` methods from its `ModLibrary.LoadAll`
  postfix without one ([StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L17-L28)).[^starmap-patches-modlibrarypatches-cs] If
  Harmony throws for a missing target, the rest of `OnFullyLoaded` (renderers, settings, Star Editor)
  never runs and the exception propagates into the `Program` constructor.
- **Comment and code disagree on MMU.** The comment says the star is "parked" and re-engaged when the
  MMU releases, but `DetachOrientedStar` calls `ClearOrientedStar(vehicle)`, which removes the entry;
  the star is dropped, not parked.
- **String targets.** `HandleKittenAction`, `ApplyMmuAttitudeTarget` and `ResetMmuAttitudeTarget`
  are named by string. `TargetMethods` yields nothing if they are renamed, and Harmony may refuse a
  patch class with no targets; the attribute form for `HandleKittenAction` throws at patch time
  instead.
- **No unpatch.** There is no `[StarMapUnload]`; `StarsEditPatcher.Unload` exists but nothing calls
  it. It would not matter: at shutdown `StarMapCore.DeInit` calls the id-less `Harmony.UnpatchAll()`,
  which removes every mod's patches before any `[StarMapUnload]` method runs
  ([StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35)).[^starmap-starmapcore-cs]

## See also

- [Orient a vessel to a star](orient-to-star.md)
- [Star instance restyle patch](star-instance-restyle-patch.md)
- [Save-scoped settings sidecar](save-scoped-settings-sidecar.md)
- [Game loop and mod lifecycle](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Patch every attitude-mode writer so the player can take back control](../../../howto/flight-and-orbits/autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/drive-the-flight-computer-and-attitude-modes-from-a-mod-9.md) - how-to article demonstrating this feature
- [Mutate simulation state safely relative to the physics step](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md) - how-to article demonstrating this feature
- [TargetMethods with per-name AccessTools lookups and the __args array](../../../howto/patching/internals/patch-a-private-game-method-by-name/patch-a-private-game-method-by-name-5.md) - how-to article demonstrating this feature
- [Patch the non-inlined entry points that reach the inlined method too](../../../howto/patching/internals/patch-around-aggressive-inlining/patch-around-aggressive-inlining-2.md) - how-to article demonstrating this feature

[^oms-starorientationobserver-cs]: OhMyStars src/StarOrientationObserver.cs
[^ksa-vehicle-cs]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^oms-ohmystarswindow-cs]: OhMyStars src/OhMyStarsWindow.cs
[^ksa-flightcomputer-cs]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-program-cs]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-gaugebuttonflightcomputer-cs]: KSA.GaugeButtonFlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-inputevents-cs]: KSA.InputEvents (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatedata-cs]: KSA.VehicleUpdateData (decompiled, build 2026.10.10.5554)
[^starmap-patches-modlibrarypatches-cs]: StarMap StarMap.Core/Patches/ModLibraryPatches.cs
[^starmap-starmapcore-cs]: StarMap StarMap.Core/StarMapCore.cs
