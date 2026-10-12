---
type: Game Feature
title: Synthesize a NavigationTarget for an object that has no orbit
description: Build the NavigationTarget struct the navball, flight computer and Toward/Away modes read for a side target, and make the solver thread and the navball see it only while it is needed
category: flight-and-orbits
group: autopilot
topic: synthesize-a-navigation-target-for-the-flight-computer
demonstrated_by:
  - mods/compendium/features/flight-computer-root-attitude-tracking
  - mods/compendium/features/navball-target-marker-and-tgt-frame
lifecycle: [physics:vehicle-solver, frame:apply-solvers, frame:input, frame:draw-ui]
game_types:
  - KSA.NavigationTarget
  - KSA.FlightComputer
  - KSA.FlightComputerNavigation
  - KSA.NavBallData
  - KSA.Vehicle
  - KSA.FlightComputerAttitudeTrackTarget
tags: [navigation-target, flight-computer, navball, tgt-frame, toward, away, finalizer, transpiler]
---

Everything that depends on the vehicle's target (navball marker, TGT navball mode, TGT reference frame, Toward/Away attitude, target gauges) is computed from one value type, `NavigationTarget`, which the game can only build for an `IOrbiter` that has an orbit. To make an object without one
(a fixed star, a waypoint, a barycenter) behave like a target, a mod has to supply that struct in the two places it is consumed: on the vehicle solver thread for the flight computer, and on the main thread for the navball and gauges.

## Engine background

- `NavigationTarget` is a struct with `Dock2Cci`, `DockFrameRatesCci`, `PositionCci` and `VelocityCci`. `NavigationTarget.Create(IOrbiter? target, Part? targetPart, IParentBody myParent, UniverseTime time)` returns null for no target, reads `target.Orbit.GetStateVectorsAt(time)`, and mirrors a branch for a target in another sphere of influence:
  ECL position and velocity differences rotated by `myParent.GetCce2Cci()`.[^ksa-navigationtarget] [current/decomp/KSA/NavigationTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/NavigationTarget.cs#L1-L90)
- **Flight computer.** The solver rebuilds `FlightComputerNavigation` (with a `Target` field) before each `ComputeControl` call. `UpdateAttitudeTarget` falls back to `SetInertialNullRot` when `AttitudeFrame == Dock` and `nav.Target` is null; `Toward` and `Away` derive their attitude from `nav.Target`.[^ksa-flightcomputer] [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1264-L1303)
- **Navball.** `Vehicle.UpdateNavBallMarkers` calls `NavigationTarget.Create` once to place the target marker; `UpdateNavballData` calls it in the `Dock` frame case, where a null result throws.[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2760-L2801)
- **Presence checks.** The target gauge panel, the `Dock` navball-mode button and the TGT reference-frame button are gated by `Vehicle.Target == null` tests in `GaugeCanvas.IsContextVisible`, `GaugeButtonNavBallMode.IsDisabled/OnReleased`, `Vehicle.UpdateNavballData` and `Vehicle.IsDisabled(VehicleReferenceFrame)`.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Prefix with `__state` and a restoring finalizer on ComputeControl](synthesize-a-navigation-target-for-the-flight-computer-1.md) | Compendium | harmony finalizer | `physics:vehicle-solver` | For the flight computer: one call at a time, the game's struct is put back |
| [Transpile the NavigationTarget.Create call sites](synthesize-a-navigation-target-for-the-flight-computer-2.md) | Compendium | harmony transpiler | `frame:apply-solvers`, `frame:draw-ui` | For the navball and gauges: exact, but fragile against IL changes |

Both use the same synthesized struct, built by `RootNavigationTargets.CreateNavigationTarget`. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## See also

- [Set or clear a vehicle's target, including bodies the game cannot target](../set-and-clear-the-vehicle-target/overview.md)
- [Drive the flight computer and attitude modes from a mod](../drive-the-flight-computer-and-attitude-modes-from-a-mod/overview.md)
- [Understand KSA coordinate frames and epochs (ECL, CCE, CCI, CCF, ego, body)](../../flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md)

[^ksa-navigationtarget]: KSA.NavigationTarget (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
