---
type: How-To
title: Claim vehicles per mod, hold burn mode and compare exact fields to detect a takeover
description: Keep one claimant per Vehicle in a ConditionalWeakTable, hold the stock burn mode in Manual and give Auto back only for the same loaded burn, and snapshot and compare the exact flight-computer fields to notice the player taking over
category: flight-and-orbits
group: autopilot
topic: run-an-autopilot-for-every-vehicle-each-step
approach: 4
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/vehicle-control-ownership]
techniques: [conditional-weak-table]
hooks: [harmony-postfix KSA.Universe.ApplyVehicleSolvers, harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:apply-solvers, frame:dispatch-solvers, frame:input]
game_types:
  - KSA.Vehicle
  - KSA.FlightComputer
  - KSA.BurnTarget
  - KSA.FlightComputerBurnMode
  - KSA.FlightComputerAttitudeTrackTarget
tags: [ownership, claim, takeover, burn-mode, attitude, conditional-weak-table, release]
---

Outcome: a controller that flies a vehicle stops the moment the player (or another mod) takes over, then puts back exactly what it changed and nothing else. KSA-AdvancedFlightComputer writes the rules down and implements them with three pieces: a claim table, a burn-mode hold, and command snapshots compared every step.[Vehicle control ownership, takeover detection and hand-back](../../../../mods/ksa-advancedflightcomputer/features/vehicle-control-ownership.md)
Prerequisite: StarMap.

## Background: how the game does it

The relevant stock behaviours: stock Auto takes the attitude near ignition; `Vehicle.PrepareWorker` clears the engine master switch every frame while `BurnMode` is Auto; stock also writes Manual when a burn is loaded, unloaded or completed; `FlightComputer.UpdateAttitudeTarget` reads `CustomAttitudeTarget` as a rotation rate under `None` and as Euler angles in `AttitudeFrame` under `Custom`,
while built-in targets derive their own frame; and `Vehicle.SetEnum(FlightComputerBurnMode)` sets the navball frame (BurnBody for Auto, the region's frame otherwise) and refuses without control.[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2412-L2415) [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6375-L6386) [^ksa-flightcomputer] [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1264-L1303)

## Lifecycle and timing

All ownership state is main-thread only. The RCS executor evaluates it in the post-solver tick (`frame:apply-solvers`), guidance in its `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`, after the input drain). Both come before the worker snapshot, so a player click drained this frame is visible to the comparison (guidance) or on the next frame (RCS), and a restored value
is what the next physics step uses. A claim, hold or restore written from a draw hook would be undone one frame later by the copy-back, and a comparison made there would see values the worker is about to replace. Stock's buttons and hotkeys reach `SetEnum` through `InputEvents.FlightComputerInputBuffer`, drained in the same window.[^ksa-inputevents] [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L592-L605) See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **One claimant per `Vehicle` object**, so a renamed vehicle keeps its claim and a disposed one does not leak. The holder may claim again, a different claimant is refused, and release removes only the caller's own claim:

```csharp
// AdvancedFlightComputer/Core/VehicleControlOwnership.cs L36-L61
internal static bool Holds(Vehicle vehicle, ControlClaimant claimant)
    => claimant != ControlClaimant.None && HolderOf(vehicle) == claimant;

/// <summary>Allows the current holder to claim again, but refuses a different claimant.</summary>
internal static bool TryClaim(Vehicle vehicle, ControlClaimant claimant, out ControlClaimant holder)
{
    holder = HolderOf(vehicle);
    if (vehicle == null || claimant == ControlClaimant.None)
        return false;
    if (holder == claimant)
        return true;
    if (holder != ControlClaimant.None)
        return false;

    Claim claim = _claims.GetOrCreateValue(vehicle);
    claim.Claimant = claimant;
    claim.VehicleId = vehicle.Id;
    holder = claimant;
    return true;
}

/// <summary>The id the vehicle had when its claim was taken, or null when it is unclaimed.</summary>
internal static string? ClaimedId(Vehicle vehicle)
    => vehicle != null && _claims.TryGetValue(vehicle, out Claim? claim) ? claim.VehicleId : null;

/// <summary>Follows a rename, so a claim taken under the old id stays releasable.</summary>
```

   Acquire before the first write; release after cleanup succeeded. Claims are main-thread only and never saved.
2. **Hold Manual, give Auto back.** `HoldManual` selects Manual and reports whether it replaced an Auto. `GiveBackAuto` restores it only when the mode still reads Manual, the loaded `BurnTarget` is still the one the hold saw, the vehicle is controllable and the burn would not stop at once for lack of propellant:

```csharp
// AdvancedFlightComputer/Core/StockBurnMode.cs L26-L41
internal static bool GiveBackAuto(Vehicle vehicle, BurnTarget? heldTarget, bool takesStopBack = false)
{
    FlightComputer fc = vehicle.FlightComputer;
    if (fc.BurnMode != FlightComputerBurnMode.Manual || heldTarget == null || !ReferenceEquals(fc.Burn, heldTarget)
        || !vehicle.IsControllable)
        return false;
    if (!takesStopBack && WouldStopAtOnce(vehicle, heldTarget))
    {
        TimedAlert.CreateWarning(ReferenceEquals(vehicle, Program.ControlledVehicle)
            ? LStrings.AlertAutoBurnStopped.Format()
            : LStrings.AlertAutoBurnStoppedVehicle.Format<string>(vehicle.Id));
        return false;
    }
    Write(vehicle, FlightComputerBurnMode.Auto);
    return true;
}
```

   Mirror what `SetEnum(FlightComputerBurnMode)` does without calling it if you intercept `SetEnum` yourself.
3. **Snapshot and compare the exact fields the flight computer reads.** Mode and track target always count; frame and coordinates count only under `None` and `Custom`:

```csharp
// AdvancedFlightComputer/Features/RcsTranslation/RcsExecution.cs L338-L350
/// <summary>
/// Compare frame and coordinates for None and Custom, where FlightComputer.UpdateAttitudeTarget uses both.
/// Built-in targets derive their frame, so only mode and tracker are compared for them.
/// </summary>
internal readonly bool Matches(FlightComputer fc)
{
    if (fc.AttitudeMode != Mode || fc.AttitudeTrackTarget != TrackTarget)
        return false;
    if (TrackTarget is not (FlightComputerAttitudeTrackTarget.None
        or FlightComputerAttitudeTrackTarget.Custom))
        return true;
    return fc.AttitudeFrame == Frame && fc.CustomAttitudeTarget.Equals(CustomTarget);
}
```

4. **Treat a mismatch as a takeover**: yield (an Align drops to Hold, no attitude gate, nothing to hand back) and record `attitude_yielded`, saved so a load cannot take the attitude back. Guidance uses the same idea in `AttitudeOwnership`: capture the original fields before the first write, record the last written snapshot, and on release restore frame, tracking and coordinates as one group only if they still match the last command.
5. **Clear leftovers on release**: custom coordinates left under `None` would remain a standing rotation-rate command.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Core/VehicleControlOwnership.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/VehicleControlOwnership.cs) | claim table |
| [AdvancedFlightComputer/Core/StockBurnMode.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/StockBurnMode.cs) | burn-mode hold and give-back |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsExecution.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsExecution.cs#L329-L351) | `RcsAttitudeCommand` snapshot and `Matches` |
| [AdvancedFlightComputer/Features/Guidance/Control/AttitudeOwnership.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/AttitudeOwnership.cs) | guidance's capture, compare and conditional restore |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Core/VehicleControlOwnershipTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Core/VehicleControlOwnershipTest.cs) | headless claim tests |

## Variations and gotchas

| Rule | Reason |
|---|---|
| Restore a field only if it still holds the value you wrote | anything else is the player's choice now |
| Give Auto back only for the same loaded burn target | stock also writes Manual when a burn is loaded, unloaded or completed |
| Never give Auto back to a burn that would stop for lack of propellant | it would stop on the next worker step |
| A takeover moves the attitude only; an engine cut already decided still happens | avoid leaving an engine burning or cutting one mid-air by accident |

- **A rename must update the claim's id** (`NoteRename`) so a claim taken under the old id stays releasable.
- **Compare with `Equals` on the exact `double3`**, not a tolerance, if the controller writes the value verbatim.

## See also

- [Overview](overview.md); [step loop](run-an-autopilot-for-every-vehicle-each-step-1.md); [flying another vehicle](run-an-autopilot-for-every-vehicle-each-step-3.md)
- [Vehicle control ownership, takeover detection and hand-back](../../../../mods/ksa-advancedflightcomputer/features/vehicle-control-ownership.md)
- [Drive the flight computer and attitude modes from a mod](../drive-the-flight-computer-and-attitude-modes-from-a-mod/overview.md)

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-inputevents]: KSA.InputEvents (decompiled, build 2026.10.10.5554)
