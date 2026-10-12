---
type: Game Feature
title: Create, chain and read stock maneuver nodes from code
description: Turn a computed impulse into a stock maneuver node, compute set-apsis, circularize and plane-change burns, place a chain of burns on the orbits earlier burns produce, read back planned burns and draw them on a timeline
category: flight-and-orbits
group: autopilot
topic: create-and-read-maneuver-nodes
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/planner-burn-node-creation
  - mods/gatos/features/solver-phase-command-drain
  - mods/ksa-advancedflightcomputer/features/apse-and-inclination-quick-tools
  - mods/ksa-advancedflightcomputer/features/multi-pass-burn-splitting
  - mods/ksa-public-telemetry/features/mission-timeline-with-planned-burns
lifecycle: [frame:draw-ui, frame:input, frame:apply-solvers, frame:after-on-frame]
game_types:
  - KSA.Burn
  - KSA.BurnPlan
  - KSA.FlightPlan
  - KSA.PatchedConic
  - KSA.InputEvents
  - KSA.TransferPlanner
  - KSA.Orbit
  - KSA.OrbitalTransfers
  - KSA.PatchTransition
  - KSA.FlightComputer
  - KSA.UniverseTime
  - KSA.Vehicle
tags: [maneuver-node, burn, burn-plan, flight-plan, patched-conic, input-events, impulse]
---

A stock maneuver node is a `Burn` in the vehicle's `FlightComputer.BurnPlan`. Everything the stock UI does with a node (the burn
editor, the Auto burn button, the saved game) works on a node a mod adds the same way. This topic shows how to create a node
without racing the vehicle solver, how to compute the common impulses, how to chain nodes on the orbits earlier nodes produce, and how to read the plan back.

## Engine background

| Type | Role |
|---|---|
| `Burn` | One planned impulse: `Time` (`UniverseTime`), `DeltaVVlf` (velocity-local frame), `FlightPlan`, `ImpulseTime`, `HasDeltaV`. Created with `Burn.Create(point, time, deltaVVlf, patch, vehicle)` |
| `BurnPlan` | The list of burns, owned by `FlightComputer.BurnPlan`; `HasActiveBurns`, `BurnCount`, `TryGetBurn(i, out Burn?)`, `FindFinalNontrivialBurn()` |
| `FlightPlan` | The vehicle's predicted trajectory: `Patches` of `PatchedConic`, `TryFindPatch(time)`, `CalculateBurnPatch`, `ComputeCompleteTrajectory` |
| `InputEvents.BurnUpdateBuffer` | Queue of `BurnUpdateData { FlightComputer, Burn, AddBurn, DeleteBurn }` applied in `ApplyInputEvents` |
| `FlightComputer.AddBurn`/`RemoveBurn`/`BurnUpdated` | What `BurnUpdateData.Apply` calls |
| `OrbitalTransfers.CciToVlf`, `DvCciToCircularize` | Frame conversion and the game's own circularize impulse |

Sources: [current/decomp/KSA/Burn.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Burn.cs#L185-L188), [current/decomp/KSA/BurnPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnPlan.cs#L22-L42), [current/decomp/KSA/BurnPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnPlan.cs#L201-L210), [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L877-L905),
[current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L304-L320), [current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs#L892-L920).

**The rule that shapes everything.** The vehicle solver snapshots the `FlightComputer` when it dispatches a step and `CopyFrom`s the
worker's copy back at `ApplyVehicleSolvers`; `BurnPlan` is assigned by reference, so both copies share it, but edits made while
the worker runs race it.[^ksa-flightcomputer] [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L181-L225) Stock edits burn plans only in `ApplyInputEvents`
and the `RaisePendingAlerts` loop, before the nearest-orbit job is queued. A mod therefore either queues through the buffer or writes in the settled window; see
[Mutate simulation state safely relative to the physics step](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md)
and [Reach the physics worker through the references FlightComputer.CopyFrom shares](../../../../mods/ksa-advancedflightcomputer/features/flight-computer-worker-copy-channels.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Burn.Create queued through BurnUpdateBuffer](create-and-read-maneuver-nodes-1.md) | KSA-AdvancedFlightComputer | input buffer | `frame:draw-ui`, `frame:input` | A real stock node, safe from any main-thread hook; applied one frame later |
| [Impulsive burn directly on the flight computer](create-and-read-maneuver-nodes-2.md) | gatOS | command queue | `frame:dispatch-solvers` | No node in the plan; a Priority.First prefix because `CopyFrom` erases earlier writes |
| [Opposite-apse and AN/DN impulse formulas](create-and-read-maneuver-nodes-3.md) | KSA-AdvancedFlightComputer | custom physics | `frame:draw-ui` | The impulses for set-apsis, circularize and plane change, planned after the last pending burn |
| [CalculateBurnPatch pass by pass](create-and-read-maneuver-nodes-4.md) | KSA-AdvancedFlightComputer | custom physics | `frame:draw-ui`, `frame:apply-solvers` | Each later burn placed on the orbit the earlier ones produce |
| [TryGetBurn loop converted to mission time](create-and-read-maneuver-nodes-5.md) | KSA-Public-Telemetry | starmap lifecycle | `frame:after-on-frame` | Read-only; a drawn timeline with label placement |

## See also

- [Drive the flight computer and attitude modes from a mod](../drive-the-flight-computer-and-attitude-modes-from-a-mod/overview.md)
- [Execute planned burns hands-free with the stock Auto burn](../execute-planned-burns-with-the-stock-autopilot/overview.md)
- [Set or clear a vehicle's target, including bodies the game cannot target](../set-and-clear-the-vehicle-target/overview.md)
- [Show mission elapsed time from actual liftoff and pick the right clock under time warp](../../flight-state/compute-mission-elapsed-time-and-detect-liftoff/overview.md)
- Related topics: [Inject UI into the game's own windows and menus, including the orbit right-click menu](../../../ui/menus/inject-ui-into-a-stock-window/overview.md), [Aim a transfer at a flyby periapsis and predict its encounter](../../transfers/aim-a-transfer-at-a-flyby-periapsis/overview.md)

[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
