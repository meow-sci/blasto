---
type: Mod Feature
title: Orient a vessel to a star
description: Point a vessel or kitten at a star by writing a Custom EclBody attitude target to the stock flight computer and re-applying it in a Vehicle.PrepareWorker prefix just before the physics snapshot
mod: ohmystars
category: flight-and-orbits
techniques: [harmony-prefix, flight-computer-target]
hooks: [starmap StarMapBeforeGui, harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:draw-ui, frame:dispatch-solvers, physics:vehicle-solver]
game_types:
  - KSA.Vehicle
  - KSA.FlightComputer
  - KSA.VehicleReferenceFrameEx
  - KSA.KittenEva
  - KSA.VehicleUpdateState
  - KSA.VehicleUpdateData
  - KSA.Universe
tags: [flight-computer, attitude, harmony, prefix, orientation, kitten, eva]
---

"Orient to Star" turns the controlled vessel (or EVA kitten) so its nose points at the selected
star and keeps it there through the stock flight computer. It is not a new controller: the mod
writes a custom attitude target in the game's inertial `EclBody` frame and lets the stock attitude
control system fly it. Pressing the button again hands control back to whatever mode the vehicle
was in before, including the player's own rate command.[^oms-ohmystarswindow-cs]

## How it works

1. **Direction.** For a catalog star the target is the aligned ecliptic unit vector; for the
   central star it is `-vehicle.GetPositionEcl()` normalised, because the system's star sits at the
   ecliptic origin ([src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L1228-L1251)).
2. **Into `EclBody` Euler angles.** KSA's `EclBody` frame is the ecliptic frame composed with
   `Vehicle.BODY2UPFRAME`, a diagonal `(1, -1, -1)` rotation
   ([current/decomp/KSA/VehicleReferenceFrameEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleReferenceFrameEx.cs#L75-L79),
   [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L271)).[^ksa-vehiclereferenceframeex-cs] [^ksa-vehicle-cs]
   The mod negates Y and Z, then solves `pitch = asin(dy)`, `yaw = atan2(-dz, dx)`, `roll = 0`
   ([src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L1253-L1265)).
3. **Kittens.** EVA kittens face +X but use -Z as body up, so for `KittenEva` the mod builds a
   look-at rotation with a -Z up hint and converts it with the game's own
   `VehicleReferenceFrame.EclBody.QuaternionToEulerAngles`
   ([src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L1267-L1293),
   [current/decomp/KSA/VehicleReferenceFrameEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleReferenceFrameEx.cs#L439-L458)).
4. **Write the flight computer.** `AttitudeMode = Auto`, `AttitudeFrame = EclBody`,
   `AttitudeTrackTarget = Custom`, `CustomAttitudeTarget = (roll, yaw, pitch)`
   ([src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L1295-L1298)). In the stock
   `FlightComputer.UpdateAttitudeTarget`, `Custom` turns `CustomAttitudeTarget` into a quaternion
   with `AttitudeFrame.EulerAnglesToQuaternion` and composes it with the frame's orientation, while
   `None` (rate hold) reads the same field as a body-rate command
   ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1264-L1300)).[^ksa-flightcomputer-cs]
5. **Re-apply every physics dispatch.** A Harmony prefix on `Vehicle.PrepareWorker` calls
   `ApplyStarOrientation` for every vehicle that has an entry in the mod's `_orientedVehicles`
   dictionary ([src/StarOrientationObserver.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarOrientationObserver.cs#L31-L52)).[^oms-starorientationobserver-cs]
   `PrepareWorker` ends with `_threadWorkerUpdateState.PrepareFromVehicle(...)`, which copies the
   live `FlightComputer` into the worker's `NewFlightComputer`
   ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423),
   [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L296-L300),
   [current/decomp/KSA/VehicleUpdateData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateData.cs#L84-L100)),[^ksa-vehicleupdatestate-cs] so the
   values written in the prefix are exactly what the next physics step steers to. For the Sun the
   direction changes as the vessel moves, so the per-step recompute matters.
6. **Per-vehicle state.** Four dictionaries keyed by `Vehicle` hold the oriented star, the pre-star
   rate command, a snapshot of the previous attitude mode, and vehicles pending disengage
   ([src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L84-L112)). Nothing is gated on `Program.ControlledVehicle`, so a
   vessel you switch away from keeps pointing at its star.
7. **Toggle off.** Pressing the button again only marks the vehicle as pending; the next
   `PrepareWorker` prefix runs `DisengagePending`, which restores `CustomAttitudeTarget`, clears
   `AttitudeTarget`, re-enters the previous track target with `TrackTarget` or rate hold with
   `RateHold`, and restores `AttitudeMode` ([src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L1047-L1074),
   [src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L1162-L1191)). Leaving through stock buttons is handled by the
   patches in [Attitude-mode hand-off patches](attitude-mode-handoff-patches.md).

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| Engage or retarget | Button in the Oh My Stars window, `[StarMapBeforeGui]` | `frame:draw-ui` |
| Re-apply / disengage | Prefix on `Vehicle.PrepareWorker`, called by `Universe.PrepareVehicleWorkers` from `ExecuteNextVehicleSolvers` | `frame:dispatch-solvers` |
| Steering | Stock attitude control on the worker copy | `physics:vehicle-solver` |

`Program.PrepareFrame` applies the previous step's results, polls input (stock attitude buttons fire
here), then calls `Universe.ExecuteNextVehicleSolvers`, which runs `PrepareVehicleWorkers` on the
main thread before queuing the solver job
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141-L2220),
[current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2154)).[^ksa-program-cs] [^ksa-universe-cs] The prefix
is therefore the last main-thread moment before the worker snapshots the flight computer, and it
runs after this frame's input, so a stock button pressed this frame has already cleared the
vehicle's entry. `KittenEva.PrepareWorker` overrides the method but calls `base.PrepareWorker`, so
the prefix also runs for kittens ([current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L319-L325)).[^ksa-kitteneva-cs]

The re-apply is needed every dispatch because the live flight computer does not keep the mod's
fields on its own: `Universe.ApplyVehicleSolvers` copies the worker's `NewFlightComputer` back over
it (`Vehicle.UpdateFromTaskResultsUnsynchronized` -> `FlightComputer.CopyFrom`, run as a parallel
apply job while the main thread waits) at the start of every frame
([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2530), [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1952)).
The step can change these fields itself (auto-burn alignment to `PositiveDv`, `SetNullRot` when an
auto burn ends, the MMU's own `Custom` target in `EnuBody`), and whatever the copy holds afterwards is
what the apply writes back, so without the prefix the star target would be lost the first time the
step changed it.

An engage from the UI writes the live flight computer during `frame:draw-ui`, after this frame's
dispatch. The step in flight does not see it (it steers its own copy), the next frame's apply
overwrites it with that copy, and the `PrepareWorker` prefix writes it again just before the next
snapshot, so the first step that steers to the star is the one dispatched in the next frame.

## Key code

| Path | Role |
|---|---|
| [src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L1193-L1299) | Toggle, snapshot, Euler solve, flight computer writes |
| [src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L1047-L1191) | Disengage and previous-mode restore |
| [src/StarOrientationObserver.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarOrientationObserver.cs#L31-L52) | Per-dispatch re-apply prefix |

## Snippets

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

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `Vehicle.PrepareWorker(SimStep)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423) | Harmony prefix target |
| `FlightComputer.AttitudeMode`, `AttitudeFrame`, `AttitudeTrackTarget`, `CustomAttitudeTarget`, `AttitudeTarget` | [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L34-L52) | Custom target |
| `FlightComputer.UpdateAttitudeTarget` | [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1264-L1300) | How `Custom` and `None` read the field |
| `FlightComputer.TrackTarget`, `RateHold` | [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1978-L1998) | Restore previous mode |
| `VehicleReferenceFrameEx.QuaternionToEulerAngles` | [current/decomp/KSA/VehicleReferenceFrameEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleReferenceFrameEx.cs#L439-L458) | Kitten Euler angles |
| `Vehicle.BODY2UPFRAME` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L271) | `EclBody` axis convention |
| `Vehicle.GetPositionEcl()`, `NavBallData` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L954) | Sun direction, rate-hold frame fallback |
| `Universe.PrepareVehicleWorkers` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2141-L2154) | Caller of `PrepareWorker` |

## Pitfalls and version notes

- **Where a write to `vehicle.FlightComputer` sticks.** Only a write between the apply and the
  `PrepareFromVehicle` copy reaches the next step; that is why the prefix is the place. A write after
  the dispatch (the UI engage) is not racy, because the worker steers its own copy, but it is thrown
  away by the next apply. A write from a worker thread is racy; the mod's MMU prefix does exactly
  that (see [Attitude-mode hand-off patches](attitude-mode-handoff-patches.md)).
- **Auto burns fight star mode.** Switching to an auto burn is not treated as leaving star mode, so
  the prefix keeps re-applying the star target while the burn logic retargets the worker copy (see
  the hand-off patches page).
- **Sol is resolved live here, but not everywhere.** Orientation and the pointer use the central
  body's real position for Sol; the [Navball star marker](navball-star-marker.md) does not.
- **`CustomAttitudeTarget` is shared.** Rate hold reads the same field as a rate command, so leaving
  star mode without restoring it spins the vehicle at whatever the Euler angles were (about
  1 rad/s per the mod's comments). Any mod using `Custom` targets needs the same clean-up.
- **Dictionaries keyed by `Vehicle`.** They are never cleared on save load (`SaveLoadObserver.Reset`
  exists but nothing calls it), so stale entries for old vehicle instances linger.
- **Roll is fixed to the ecliptic.** `roll = 0` in `EclBody` pins roll relative to the ecliptic
  frame, not to the local horizon.

## See also

- [Attitude-mode hand-off patches](attitude-mode-handoff-patches.md)
- [Navball star marker](navball-star-marker.md)
- [AT-HYG star catalog and star information](hyg-star-catalog.md)
- [Game loop and mod lifecycle](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Orient a vehicle to a sky direction from a Vehicle.PrepareWorker prefix](../../../howto/flight-and-orbits/autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/drive-the-flight-computer-and-attitude-modes-from-a-mod-1.md) - how-to article demonstrating this feature

[^oms-ohmystarswindow-cs]: OhMyStars src/OhMyStarsWindow.cs
[^ksa-vehiclereferenceframeex-cs]: KSA.VehicleReferenceFrameEx (decompiled, build 2026.10.10.5554)
[^ksa-vehicle-cs]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer-cs]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^oms-starorientationobserver-cs]: OhMyStars src/StarOrientationObserver.cs
[^ksa-vehicleupdatestate-cs]: KSA.VehicleUpdateState (decompiled, build 2026.10.10.5554)
[^ksa-program-cs]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-universe-cs]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-kitteneva-cs]: KSA.KittenEva (decompiled, build 2026.10.10.5554)
