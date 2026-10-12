---
type: How-To
title: Orient a vehicle to a sky direction from a Vehicle.PrepareWorker prefix
description: Point the nose at a star by writing Auto, the EclBody frame and a Custom attitude target with solved Euler angles every physics dispatch, and restore the previous mode and rate command on exit
category: flight-and-orbits
group: autopilot
topic: drive-the-flight-computer-and-attitude-modes-from-a-mod
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ohmystars/features/orient-to-star]
techniques: [flight-computer-target, harmony-prefix]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker, starmap StarMapBeforeGui]
lifecycle: [frame:draw-ui, frame:dispatch-solvers, physics:vehicle-solver]
game_types:
  - KSA.Vehicle
  - KSA.FlightComputer
  - KSA.VehicleReferenceFrameEx
  - KSA.FlightComputerAttitudeTrackTarget
  - KSA.KittenEva
tags: [attitude, custom-target, euler, eclbody, prepare-worker, star, kitten]
---

Outcome: a button that turns the controlled vessel (or an EVA kitten) so its nose points at a chosen star and keeps it there through the stock attitude controller; pressing it again hands control back to the mode the vehicle was in.
OhMyStars implements it.[Orient a vessel to a star](../../../../mods/ohmystars/features/orient-to-star.md) Prerequisites: StarMap and Harmony.

## Background: how the game does it

See the [overview](overview.md) for the fields and the copy round trip. `EclBody` is the ecliptic frame composed with `Vehicle.BODY2UPFRAME`, a diagonal `(1, -1, -1)` rotation, so Euler angles for a direction have to be solved in that frame.[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L271)
In the stock `UpdateAttitudeTarget`, `Custom` turns `CustomAttitudeTarget` into a quaternion with `AttitudeFrame.EulerAnglesToQuaternion` and composes it with the frame orientation, while `None` reads the same field as a body-rate command.[current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1264-L1303)

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| Engage or retarget | the mod's window, `[StarMapBeforeGui]` | `frame:draw-ui` |
| Re-apply or disengage | prefix on `Vehicle.PrepareWorker`, called by `Universe.PrepareVehicleWorkers` from `ExecuteNextVehicleSolvers` | `frame:dispatch-solvers` |
| Steering | stock attitude control on the worker copy | `physics:vehicle-solver` |

The prefix is the last main-thread moment before the worker snapshots the flight computer, and it runs after this frame's input, so a stock button pressed this frame has already cleared the vehicle's entry. The re-apply is needed every dispatch because
`ApplyVehicleSolvers` copies the worker's flight computer back over the live one at the start of every frame, and the step itself changes these fields (auto-burn alignment, `SetNullRot` at the end of a burn). An engage written during `frame:draw-ui` is erased by the next apply and
written again by the prefix, so the first step that steers to the star is the one dispatched in the next frame. `KittenEva.PrepareWorker` overrides the method but calls `base.PrepareWorker`, so the prefix also runs for kittens. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Compute the direction in ECL** (the aligned unit vector for a catalog star; `-vehicle.GetPositionEcl()` normalised for the central star).
2. **Solve Euler angles in `EclBody`.** Negate Y and Z, then `pitch = asin(dy)`, `yaw = atan2(-dz, dx)`, `roll = 0`; write the flight computer fields:

```csharp
// src/OhMyStarsWindow.cs L1253-L1265, L1295-L1298
// In KSA, VehicleReferenceFrame.EclBody is the ecliptic body-reference frame,
// which relates to Ecliptic (CCE) coordinates via BODY2UPFRAME (negating Y and Z).
// For EclBody, Euler angles (roll, yaw, pitch) map to unit forward as:
// forward vector = (cos(pitch)*cos(yaw), sin(pitch), -cos(pitch)*sin(yaw)).
// Since starDirEcl in EclBody coordinates is (starDirEcl.X, -starDirEcl.Y, -starDirEcl.Z),
// we can solve directly for pitch and yaw:
double dx = starDirEcl.X;
double dy = -starDirEcl.Y;
double dz = -starDirEcl.Z;

double pitch = Math.Asin(Math.Clamp(dy, -1.0, 1.0));
double yaw = Math.Atan2(-dz, dx);
double roll = 0.0;
// ...
vehicle.FlightComputer.AttitudeMode = FlightComputerAttitudeMode.Auto;
vehicle.FlightComputer.AttitudeFrame = VehicleReferenceFrame.EclBody;
vehicle.FlightComputer.AttitudeTrackTarget = FlightComputerAttitudeTrackTarget.Custom;
vehicle.FlightComputer.CustomAttitudeTarget = new double3(roll, yaw, pitch);
```

   Order of components is roll, yaw, pitch, matching `ToRollYawPitchRadians` for `EclBody`.
3. **Kittens need another solve.** EVA kittens face +X but use -Z as body up; build a look-at rotation with a -Z up hint and convert with `VehicleReferenceFrame.EclBody.QuaternionToEulerAngles` (see [src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L1267-L1293)).
4. **Re-apply in a `PrepareWorker` prefix**, for every vehicle that has an entry, with no gating on `Program.ControlledVehicle`:

```csharp
// src/StarOrientationObserver.cs L31-L52
[HarmonyPatch(typeof(Vehicle), nameof(Vehicle.PrepareWorker))]
private static class PrepareWorkerPatch {
    static void Prefix(Vehicle __instance) {
        try {
            // A pending disengage runs the rate-hold on this tick in place of the star reapply,
            // so the mode change happens atomically on the mod's own pass with no stale write.
            if(OhMyStarsWindow.IsDisengagePending(__instance)) {
                OhMyStarsWindow.DisengagePending(__instance);
                return;
            }

            // Every vehicle re-applies its own remembered star orientation. Nothing is gated on
            // Program.ControlledVehicle, so switching control neither transfers star orientation
            // to the newly controlled vehicle nor discards the state the previous one was left in.
            if(OhMyStarsWindow.TryGetOrientedStar(__instance, out int hip)) {
                OhMyStarsWindow.ApplyStarOrientation(__instance, hip);
            }
        } catch(Exception ex) {
            DefaultCategory.Log.Warning($"[OhMyStars] PrepareWorker Prefix: {ex}");
        }
    }
}
```

5. **Keep four per-vehicle dictionaries**: the oriented star, the pre-star rate command, a snapshot of the previous attitude mode and vehicles pending disengage.
6. **Toggle off in the prefix.** The button only marks the vehicle pending; the next prefix restores `CustomAttitudeTarget`, clears `AttitudeTarget`, re-enters the previous track target with `TrackTarget` or rate hold with `RateHold`, and restores `AttitudeMode`
   ([src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L1047-L1074), [src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L1162-L1191)). Leaving through stock buttons is handled by [approach 9](drive-the-flight-computer-and-attitude-modes-from-a-mod-9.md).

## Complete example

| File | Role |
|---|---|
| [src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L1193-L1299) | toggle, snapshot, Euler solve, flight computer writes |
| [src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L1047-L1191) | disengage and previous-mode restore |
| [src/StarOrientationObserver.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarOrientationObserver.cs#L31-L52) | per-dispatch re-apply prefix |

## Variations and gotchas

- **Do not write the target from a draw hook only.** It is erased by the next apply; the prefix is what makes it stick.
- **For the Sun the direction changes as the vessel moves**, so the per-step recompute matters.
- **A rate hold reads `CustomAttitudeTarget` as a body rate**, so restore the pre-mod rate command before re-entering rate hold, or the vehicle starts rotating.
- A disengage inside the prefix replaces the re-apply on that tick, so the mode change is atomic with the mod's own pass.

## See also

- [Overview](overview.md); [release patches](drive-the-flight-computer-and-attitude-modes-from-a-mod-9.md); [queued writes](drive-the-flight-computer-and-attitude-modes-from-a-mod-2.md)
- [Orient a vessel to a star](../../../../mods/ohmystars/features/orient-to-star.md)
- [Run a closed-loop autopilot for every vehicle each step and arbitrate control with the player](../run-an-autopilot-for-every-vehicle-each-step/overview.md)

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
