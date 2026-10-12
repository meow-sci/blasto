---
type: Game Feature
title: Run a closed-loop autopilot for every vehicle each step and arbitrate control with the player
description: Run a per-vehicle guidance loop on the main thread right before each vehicle's worker snapshot, claim vehicles per mod, detect when the player takes over attitude or burn mode, and release only what you changed
category: flight-and-orbits
group: autopilot
topic: run-an-autopilot-for-every-vehicle-each-step
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/guidance-vehicle-step-loop
  - mods/ksa-advancedflightcomputer/features/boostback-impact-prediction
  - mods/ksarmory/features/fly-another-vehicle
  - mods/ksa-advancedflightcomputer/features/vehicle-control-ownership
lifecycle:
  - frame:dispatch-solvers
  - frame:draw-ui
  - event:save-load
  - event:vehicle-destroyed
  - frame:apply-solvers
  - frame:input
game_types:
  - KSA.Vehicle
  - KSA.Universe
  - KSA.FlightComputer
  - KSA.ManualControlInputs
  - KSA.Program
  - KSA.SequenceList
  - KSA.Decoupler
  - KSA.FlightComputerBurnMode
  - KSA.FlightComputerAttitudeTrackTarget
  - KSA.BurnTarget
tags: [autopilot, prepare-worker, per-vehicle, control-ownership, takeover, claim, booster, harmony-prefix]
---

An autopilot for more than the craft the player is flying has two jobs the stock flight computer does not do for you. It must run once per physics step for every vehicle it owns (including a separated booster while the player rides the upper stage),
at a point where its writes reach the next step, and it must share each craft with the player and with other mods without fighting or clobbering them.

## Engine background

- **The per-vehicle hook.** `Universe.ExecuteNextVehicleSolvers` calls `PrepareVehicleWorkers`, which calls `Vehicle.PrepareWorker(SimStep)` on the main thread for every vehicle, then queues the solver job. `PrepareWorker` applies held throttle keys, clears `EngineOn` while `BurnMode` is Auto,
  and ends with `PrepareFromVehicle`, the snapshot the worker flies from. A prefix is the last main-thread point before that vehicle's snapshot and comes after `ApplyInputEvents` has drained the player's input.[^ksa-universe] [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2155) [^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423)
- **The copy-back.** The worker's flight computer is copied back over the vehicle's at the next `ApplyVehicleSolvers`, so writes from draw hooks are erased (see [Drive the flight computer and attitude modes from a mod](../drive-the-flight-computer-and-attitude-modes-from-a-mod/overview.md)).
- **Who else writes.** Burn mode through `Vehicle.SetEnum` (gauge button, hotkey); attitude through `SetEnum`, `ToggleEnum`, `SetStabilization`, `FlightComputer.TrackTarget`, `RateHold`, `SetNullRot`; stock also writes Manual when a burn is loaded, unloaded or completed.[^ksa-flightcomputer] [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L331-L343) [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1978-L2000)
- **Separation creates a new `Vehicle`.** `Vehicle.Split` copies several fields to it; part instance ids survive, so ids recorded before the split identify the booster after it.[current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1660-L1668)

See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Reach the physics worker through the references FlightComputer.CopyFrom shares](../../../../mods/ksa-advancedflightcomputer/features/flight-computer-worker-copy-channels.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [PrepareWorker prefix loop with takeover checks](run-an-autopilot-for-every-vehicle-each-step-1.md) | KSA-AdvancedFlightComputer | harmony prefix | `frame:dispatch-solvers` | The core pattern; fault counter and menu off switch |
| [Adopt a separated booster](run-an-autopilot-for-every-vehicle-each-step-2.md) | KSA-AdvancedFlightComputer | harmony prefix | `frame:dispatch-solvers` | Hand-over by recorded part ids; retry until the tree settles |
| [Fly a vehicle that is not the controlled one](run-an-autopilot-for-every-vehicle-each-step-3.md) | KSArmory | harmony prefix | `frame:dispatch-solvers` | Throttle by input, staging by sequence list; hand it back |
| [Per-vehicle claims and exact-field takeover checks](run-an-autopilot-for-every-vehicle-each-step-4.md) | KSA-AdvancedFlightComputer | conditional weak table | `frame:apply-solvers`, `frame:dispatch-solvers` | Several mods coexist; restore only what you wrote |

## See also

- [Drive the flight computer and attitude modes from a mod](../drive-the-flight-computer-and-attitude-modes-from-a-mod/overview.md)
- [Execute burns with RCS translation and throttle inputs from code](../execute-burns-with-rcs-and-throttle-inputs/overview.md)
- Related topics: [Fly a closed-loop ascent to a target orbit](../../guidance/fly-closed-loop-ascent-guidance/overview.md), [Fly a boostback burn and glide a booster to its landing site](../../guidance/fly-a-boostback-and-glide-to-a-landing-site/overview.md)

[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
