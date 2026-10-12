---
type: Mod Feature
title: Navigation-only root targets
description: Lets a vehicle target a fixed star or root barycenter, which the game cannot target, by keeping a session-only side table keyed by the vehicle and its burn plan and applying UI requests from a prefix on InputEvents.ApplyInputEvents
mod: compendium
category: flight-and-orbits
techniques: [harmony-prefix, conditional-weak-table, command-queue]
hooks:
  - starmap StarMapAfterGui
  - harmony-prefix KSA.InputEvents.ApplyInputEvents
  - harmony-prefix KSA.Vehicle.SetTarget
  - harmony-prefix KSA.Universe.UnsetTargetCommand
  - starmap StarMapUnload
lifecycle: [frame:draw-ui, frame:input, event:target-changed, event:starmap-unload]
game_types:
  - KSA.Vehicle
  - KSA.FlightComputer
  - KSA.BurnPlan
  - KSA.InputEvents
  - KSA.Universe
  - KSA.FixedStar
  - KSA.Barycenter
  - KSA.IIndependentRoot
  - KSA.IOrbiter
  - KSA.NavBallData
tags:
  - targets
  - navigation
  - harmony
  - flight-computer
  - conditional-weak-table
  - input-events
  - stars
  - barycenter
---

In the Stars category a fixed star (a system's central star that does not orbit anything) or a root
barycenter gets a "Select ... as Target" button. The selected root then behaves like a target for
navigation: the navball shows a target marker, the TGT navball mode and TGT reference frame work, the
flight computer can track toward or away from it, and the TGT gauges show distance, speed and an
estimated closest approach. It never becomes the game's real target, has no orbit, is not saved, and
is replaced as soon as the player picks a native target.[^cmp-readme-md]

The game cannot do this by itself because its target is `Vehicle.Target`, typed `IOrbiter?`, and
`FixedStar` and `Barycenter` do not implement `IOrbiter`
([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L577-L579), [current/decomp/KSA/FixedStar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedStar.cs#L7),
[current/decomp/KSA/Barycenter.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Barycenter.cs#L7)).[^ksa-vehicle] [^ksa-fixedstar] [^ksa-barycenter] This page covers the
state and the selection path; the patches that make the navball, flight computer and gauges see the
target are separate features (see See also).

## How it works

1. **Which bodies.** `RootNavigationTargets.Supports` accepts a body that is a `FixedStar` or a
   `Barycenter`, is an `IIndependentRoot` and is not an `IOrbiter`; `IsLoadedRoot` additionally requires
   that it belongs to `Universe.CurrentSystem` and is reference-equal to an entry of `Universe.Roots`
   ([src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L72-L90)).[^cmp-rootnavigationtargets] `Universe.LoadSystem`
   fills that array from `CurrentSystem.All.OfType<IIndependentRoot>()`
   ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L232-L256)).[^ksa-universe]
2. **Two weak tables, one state object.** A `TargetState` holding the body is stored twice: in a
   `ConditionalWeakTable<Vehicle, TargetState>` for main-thread lookups (UI, navball, gauges) and in a
   `ConditionalWeakTable<BurnPlan, TargetState>` for the flight computer on the solver threads
   ([src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L11-L22)). The burn plan is the key because the solver runs
   `ComputeControl` on a copy of the vehicle's `FlightComputer` (`VehicleUpdateData.NewFlightComputer`),
   and `FlightComputer.CopyFrom` copies the `BurnPlan` reference, so the copy and the live computer share
   that one object ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L163-L200),
   [current/decomp/KSA/VehicleUpdateData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateData.cs#L72), [current/decomp/KSA/VehicleUpdateData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateData.cs#L84-L100)).[^ksa-flightcomputer]
   [^ksa-vehicleupdatedata] Weak tables keep nothing alive, so an entry disappears with its vehicle.
3. **Native target wins.** `GetTarget(vehicle)` returns the root only while `vehicle.Target == null`,
   and only while the body is still a loaded root ([src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L92-L106)).
4. **UI only queues.** `DrawTargetControls` calls `QueueSelection(vehicle, body)` or
   `QueueSelection(vehicle, null)` to clear, and `QueueTracking` for the Track Toward / Track Away
   buttons; the requests go into a `ConcurrentQueue` ([src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L83-L172),
   [src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L24-L36)).[^cmp-cdmhelpers]
5. **Apply at the input point.** A Harmony prefix on `InputEvents.ApplyInputEvents` drains the queue
   ([src/RootTargetPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetPatches.cs#L10-L15)).[^cmp-roottargetpatches] `ApplyRequests` drops requests
   whose vehicle is disposed or whose body is no longer loaded; a tracking request additionally needs
   the root to still be that vehicle's target and the vehicle to be controllable, and is then turned into
   a stock `InputEvents.FlightComputerInputData` with the `Toward` or `Away` enum value; selection calls
   `Select`, a null body calls `Clear` ([src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L38-L70)).
6. **Select.** Throws unless the patches are installed and the body is supported and loaded, calls
   `vehicle.SetTarget(null)` to drop any native target, then registers a fresh `TargetState` in both
   tables and logs that the target is session-only ([src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L108-L122)).
   That `SetTarget(null)` passes through Compendium's own `SetTarget` prefix (step 8) with a null
   target, so switching from one root to another first clears the old root with tracking stopped:
   Toward/Away or TGT-frame hold drops to inertial hold and has to be selected again.
7. **Clear.** Writes `null` into the shared state with `Volatile.Write` (so a solver thread holding the
   same object stops seeing the body), removes both entries and, unless told not to, stops root
   tracking: if the flight computer is in the `Dock` (TGT) frame or tracking `Toward`/`Away` it calls
   `FlightComputer.SetInertialNullRot`, and a navball left in `Dock` is switched to `EclBody`
   ([src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L124-L150)). `SetInertialNullRot` is
   `SetNullRot(VehicleReferenceFrame.EclBody)`, which also resets the track target to `None`
   ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L331-L343)).
8. **Native target changes clear it.** A prefix on `Vehicle.SetTarget(IOrbiter? target, Part? targetPart)`
   repeats the game's own guard (`target != this && targetPart?.Tree.OwningVehicle != this`) and clears
   the root target. It stops tracking only when the new target is null; when the player switches to a
   native target, an active Toward/Away keeps running against the new target
   ([src/RootTargetPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetPatches.cs#L17-L25), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2189-L2203)).
9. **Clearing through the game's command.** `Universe.UnsetTargetCommand(string celestialId)` (the
   `unsettarget` terminal action, also called by the game's Target Track window) returns early when
   `vehicle.Target == null`, so it could never clear a root target. A prefix intercepts it for a vehicle
   that has a root target, queues a clear and returns `false` to skip the original
   ([src/RootTargetPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetPatches.cs#L27-L38), [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L522-L587),
   [current/decomp/KSA/TargetTrackWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TargetTrackWindow.cs#L310-L317)).[^ksa-targettrackwindow]
10. **Unload.** `[StarMapUnload]` calls `Patcher.Unload`, which runs `RootNavigationTargets.Unload`
    (clear queue, clear closest-approach cache, clear every vehicle, mark patches as not installed)
    before unpatching ([src/Patcher.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Patcher.cs#L23-L31), [src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L152-L163)).[^cmp-patcher]

## Lifecycle and timing

| Step | Where it runs | Thread | Why there |
|---|---|---|---|
| Button click, `QueueSelection` / `QueueTracking` | `[StarMapAfterGui]` window | main | ImGui is only valid here; the vehicle solver is already running, so nothing is written to the vehicle |
| `ApplyRequests` | prefix on `InputEvents.ApplyInputEvents`, called once per frame from `Program.PrepareFrame` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2181)) | main | After `Universe.ApplyVehicleSolvers` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2167)) and before `Universe.ExecuteNextVehicleSolvers` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2220)): the solver is idle and its last results are applied, and the next dispatch snapshots the flight computer with `CopyFrom`, so `SetInertialNullRot` and the navball frame change stick |
| Queued `FlightComputerInputData` | `FlightComputerInputBuffer.ApplyAll` later in the same `ApplyInputEvents` call | main | Being a prefix, `ApplyRequests` runs before the method body applies `ChangeTargetBuffer` and `FlightComputerInputBuffer` ([current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1114-L1140)) |
| `Vehicle.SetTarget` prefix | wherever the game calls `SetTarget`; the usual path is `ChangeTargetData.Apply` inside `ApplyInputEvents` ([current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L780-L800)) | main | Any native target change, including one queued in the same frame as a root selection, clears the root afterwards |
| `Universe.UnsetTargetCommand` prefix | wherever it is called: the `unsettarget` console command, the game's Target Track window ([current/decomp/KSA/TargetTrackWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TargetTrackWindow.cs#L310-L317)), other mods' UI | main | Only queues a clear, so calling it from UI code is as safe as Compendium's own buttons |
| `StarMapUnload` | `ModLoader.Dispose`, after `StarMapCore.DeInit` has already called the id-less `UnpatchAll()` ([StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35), [StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L143-L151)) | main | Runs once, after the game's entry point returns ([StarMap/GameSurveyer.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/GameSurveyer.cs#L52-L61)) |

A click in frame N is applied in `PrepareFrame` of frame N+1, and the first solver step that sees the
change is the one dispatched in that same `PrepareFrame`.[^ksa-program] [^ksa-inputevents] The
solver-side readers use only thread-safe structures: `ConditionalWeakTable` lookups and a
`Volatile.Read` of `TargetState.Body`. The general rule this follows is described in
[Mutate simulation state safely relative to the physics step](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Key code

| Path | Role |
|---|---|
| [src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L11-L36) | Shared state, weak tables, request queue |
| [src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L38-L70) | Drain requests at the input point |
| [src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L72-L106) | Eligibility, loaded check, `GetTarget` |
| [src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L108-L163) | `Select`, `Clear`, stop tracking, unload |
| [src/RootTargetPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetPatches.cs#L10-L38) | `ApplyInputEvents`, `SetTarget` and `UnsetTargetCommand` prefixes |
| [src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L83-L172) | Target buttons and Track Toward / Away |

## Snippets

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

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `Vehicle.Target`, `Vehicle.SetTarget` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2189-L2203) | The native target; prefix clears the root target on any change |
| `Vehicle.FlightComputer`, `Vehicle.IsControllable`, `Vehicle.IsDisposed` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L468-L474), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L589), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L618) | Burn plan key, request validation |
| `Vehicle.NavBallData`, `Vehicle.SetNavBallFrame` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L5618-L5621) | Leave the TGT navball mode when the root is cleared |
| `FlightComputer.BurnPlan`, `FlightComputer.CopyFrom` | [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L163-L200) | Shared reference that identifies the solver's copy |
| `FlightComputer.AttitudeFrame`, `AttitudeTrackTarget`, `SetInertialNullRot` | [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L331-L343) | Stop TGT hold and Toward/Away |
| `InputEvents.ApplyInputEvents`, `FlightComputerInputBuffer`, `FlightComputerInputData` | [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L582-L619) | Apply point, and the stock route for Toward/Away commands |
| `Universe.Roots`, `Universe.CurrentSystem`, `Universe.LoadSystem` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L106-L114) | Loaded root check |
| `Universe.UnsetTargetCommand` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L522-L587) | Skipped and replaced for root targets |
| `FixedStar`, `Barycenter`, `IIndependentRoot`, `IOrbiter` | [current/decomp/KSA/IIndependentRoot.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IIndependentRoot.cs#L7-L16) | Eligibility |

## Pitfalls and version notes

- **Session only.** Nothing is serialized, and the tables are keyed by object identity, so loading a
  save (new `Vehicle` objects) loses every root target.[^cmp-readme-md]
- **A throw would reach the game loop.** `Select` throws `InvalidOperationException` when the patches
  are not installed; it is called from the `ApplyInputEvents` prefix, so an exception there would
  propagate into `Program.PrepareFrame`. The UI disables the button when `PatchesInstalled` is false and
  `ApplyRequests` re-checks the loaded state first, which keeps it unreachable in practice.
- **Burn-plan identity is an assumption about the game.** `FlightComputer.BurnPlan` has a private setter
  that only the constructor and `CopyFrom` assign in build 2026.10.10.5554. If a later build gave the
  solver copy its own plan, the worker lookup would miss silently and the flight computer would fall back
  to inertial hold (see [Flight-computer attitude tracking against a root target](flight-computer-root-attitude-tracking.md)).
- **Skipping `UnsetTargetCommand`.** The prefix returns `false` for vehicles with a root target, which
  also hides the original from any other mod's postfix logic that expects the method body to run.
- **One-frame lag on the navball.** The selection lands in `ApplyInputEvents`, after this frame's
  `ApplyVehicleSolvers` has already refreshed the navball, so the TGT marker appears on the next
  frame (see [the navball feature](navball-target-marker-and-tgt-frame.md)).
- **Unload is shutdown only.** StarMap calls `[StarMapUnload]` after the game has exited and after it
  has already unpatched everything, so `Patcher.Unload`'s own `UnpatchAll(_harmony.Id)` is a no-op.

## See also

- [Navball target marker and TGT frame for a target without an orbit](navball-target-marker-and-tgt-frame.md)
- [Flight-computer attitude tracking against a root target](flight-computer-root-attitude-tracking.md)
- [Closest approach to a root target](root-closest-approach-search.md)
- [Target orbiting bodies through the game's settarget command](native-target-selection.md)
- [Game loop and mod lifecycle](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Queue vehicle changes in your own ConcurrentQueue and drain it in an ApplyInputEvents prefix](../../../howto/flight-and-orbits/autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/drive-the-flight-computer-and-attitude-modes-from-a-mod-8.md) - how-to article demonstrating this feature
- [Make fixed stars and barycenters targetable with weak tables keyed by Vehicle and BurnPlan](../../../howto/flight-and-orbits/autopilot/set-and-clear-the-vehicle-target/set-and-clear-the-vehicle-target-2.md) - how-to article demonstrating this feature

[^cmp-readme-md]: Compendium: README.md
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-fixedstar]: KSA.FixedStar (decompiled, build 2026.10.10.5554)
[^ksa-barycenter]: KSA.Barycenter (decompiled, build 2026.10.10.5554)
[^cmp-rootnavigationtargets]: Compendium: src/RootNavigationTargets.cs
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatedata]: KSA.VehicleUpdateData (decompiled, build 2026.10.10.5554)
[^cmp-cdmhelpers]: Compendium: src/CdmHelpers.cs
[^cmp-roottargetpatches]: Compendium: src/RootTargetPatches.cs
[^ksa-targettrackwindow]: KSA.TargetTrackWindow (decompiled, build 2026.10.10.5554)
[^cmp-patcher]: Compendium: src/Patcher.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-inputevents]: KSA.InputEvents (decompiled, build 2026.10.10.5554)
