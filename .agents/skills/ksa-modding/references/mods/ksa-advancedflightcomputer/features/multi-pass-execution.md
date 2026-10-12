---
type: Mod Feature
title: Multi-pass execution as a per-vehicle state machine on stock Auto burns
description: Fly a split maneuver hands-free by placing one stock burn per pass, advancing when stock ends a pass, re-arming Auto through Vehicle.SetEnum once the next queued burn has materialized, and surviving saves, propellant stops and player edits
mod: ksa-advancedflightcomputer
category: flight-and-orbits
techniques: [harmony-postfix, harmony-prefix, save-sidecar]
hooks:
  - harmony-postfix KSA.Universe.ApplyVehicleSolvers
  - harmony-postfix KSA.FlightComputer.RaisePendingAlerts
lifecycle: [frame:apply-solvers, frame:prepare, frame:input]
game_types: [KSA.FlightComputer, KSA.BurnPlan, KSA.Burn, KSA.Vehicle, KSA.InputEvents, KSA.TransferPlanner]
tags: [multi-pass, state-machine, auto-burn, burn-plan, input-events, persistence]
---

After **Create** with PASSES > 1 and the stock **Auto** burn mode on, each pass fires automatically
(with the main engine or as an RCS burn), the next pass is placed once the previous one finished, and
the plan window shows "Multi-pass active: pass X of N" with a cancel button. Combined with automatic
staging and automatic burn removal, a 5-pass LEO-to-Luna departure runs hands-free. AFC never flies
the passes itself: every pass is an ordinary stock burn, and AFC only decides when to place the next
one and when to arm Auto.

## How it works

### State

`MultiPassExecution` holds the intent, split mode, pass count and index, the live `Burn` of the current
pass (transient) plus its time and delta-v (persisted, to reattach after a load), and flags for
"awaiting materialization", "re-engage Auto on next burn" and "pass armed"
([AdvancedFlightComputer/Features/MultiPass/MultiPassExecution.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassExecution.cs)). `MultiPassRegistry` keys
executions by save id and vehicle id and writes them to `multipass.toml`
(see [save-scoped registries](save-scoped-sidecar-registries.md)).

### Start

`MultiPassController.Start` creates the execution from the selected intent and commits the first pass
with `MultiPassCommitter.TryCommitNext`: `intent.RecomputePass` from the live vehicle, then the same
`Burn.Create` + `InputEvents.BurnUpdateBuffer` path as a single node (see
[planner burn node creation](planner-burn-node-creation.md)).

### Two entry points per frame

| Entry | When | Does |
|---|---|---|
| `PassCompletionPatch.TickVehicle` | [post-solver tick](post-solver-vehicle-tick.md) (`ApplyVehicleSolvers` postfix) | Reconcile after a load, observe arming, track materialization, advance (commit the next pass when none is pending) |
| `PassCompletionPatch.OnStockBurnEnded` | [stock burn end detection](stock-burn-end-detection.md) (`RaisePendingAlerts` postfix) | Completed: remove the pass if stock kept it (last burn) and move to the next index; stopped: handle propellant |

([AdvancedFlightComputer/Features/MultiPass/PassCompletionPatch.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/PassCompletionPatch.cs#L19-L82))

### Materialization and re-arming Auto

A burn queued through `InputEvents.BurnUpdateBuffer` appears in the plan only after the next input
drain. `FlightComputer.AddBurn` loads it only if it is the first executable burn, and `LoadBurn` resets
the burn mode to Manual ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L2209-L2217), [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L346-L353)).[^ksa-flightcomputer]
So after a completed pass AFC sets `ReengageAutoOnNextBurn`, waits until `BurnPlan.TryGetBurn(currentBurn)`
succeeds (up to 4 ticks, then the execution is cancelled as "queue dropped the add"), checks that the
loaded target is this pass, and calls `vehicle.SetEnum(FlightComputerBurnMode.Auto)`. Going through
`SetEnum` (not a field write) is deliberate: it is the path the [RCS executor](rcs-burn-execution.md)
intercepts, so a pass that resolves to RCS starts an RCS burn
([AdvancedFlightComputer/Features/MultiPass/PassCompletionPatch.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/PassCompletionPatch.cs#L160-L210)).

### Edge cases handled

- **Propellant stop.** Stock has already warned. A pass stock kept waits in Manual for the player to
  refuel or stage; a pass stock removed is planned again from the current orbit, also in Manual. With
  no propellant anywhere (engines, unstaged engines, or RCS if the RCS executor can fly the pass), the
  execution ends with an alert.
- **Disengage.** Manual without a stock flag on an armed pass means the player disengaged; the
  execution is kept and one hint is shown. RCS passes run in Manual, so they are not mistaken for a
  stall.
- **Deleted pass.** A pass burn missing from the plan outside the materialization window was deleted
  by the player; the execution is cancelled.
- **Load.** References are reattached by persisted time and delta-v.
- **Planner state.** For a Hohmann execution, stock's global planner fields (`_transferCalculated`,
  `_transferBurn`) are kept in sync only for the matching vehicle and type, so the plan window keeps
  its controls after ignition clears stock's flag.
- **Goal met early.** An intent that is already satisfied finishes successfully instead of counting as
  a planning failure (5 consecutive scheduling failures cancel).

## Lifecycle and timing

| Moment | Lifecycle | Thread |
|---|---|---|
| Tick | `frame:apply-solvers`, after AutoStage and before the RCS driver | Main |
| Burn end | `frame:prepare` (`RaisePendingAlerts` postfix, workers joined, plan editable) | Main |
| Queued add applied | `frame:input`: the same frame's drain for a pass queued from the tick (the tick runs before `InputEvents.ApplyInputEvents`), the next frame's drain for the first pass queued from the planner draw | Main |

Ordering matters: the multi-pass tick reads the burn mode before the RCS driver may change it in the
same tick, and the burn-end handler runs before automatic removal looks at the plan.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/MultiPass/PassCompletionPatch.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/PassCompletionPatch.cs) | State machine |
| [AdvancedFlightComputer/Features/MultiPass/MultiPassExecution.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassExecution.cs) | Execution state |
| [AdvancedFlightComputer/Features/MultiPass/MultiPassController.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassController.cs) | Start, status, cancel |
| [AdvancedFlightComputer/Features/MultiPass/MultiPassCommitter.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassCommitter.cs) | Commit the next pass |
| [AdvancedFlightComputer/Features/MultiPass/MultiPassRegistry.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassRegistry.cs) | Persistence |
| [AdvancedFlightComputer/Features/MultiPass/MultiPassUI.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassUI.cs) | Pass slider, preview and status in AFC plan types |
| [tests/AdvancedFlightComputer.HarnessTests/Features/MultiPass/MultiPassRegistryTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/MultiPass/MultiPassRegistryTest.cs) | Headless registry test |

## Snippets

```csharp
// AdvancedFlightComputer/Features/MultiPass/PassCompletionPatch.cs L173-L189
            // Restore the stock transfer burn after it appears in the plan. DrawPlanWindow may have cleared the earlier reference before ApplyInputEvents added it.
            if (exec.Intent is HohmannTransferIntent)
                KeepStockTransferBurnInSync(exec.CurrentBurn);

            // Carry Auto into the next pass after FlightComputer.LoadBurn has loaded it in Manual.
            // FlightComputer.AddBurn loads the pass only when it is the first executable burn, so with another burn ahead of it the request is dropped rather than arming that burn.
            // RCS can accept this request while the burn mode stays in Manual.
            if (exec.ReengageAutoOnNextBurn)
            {
                exec.ReengageAutoOnNextBurn = false;
                if (IsLoadedPass(fc, exec))
                {
                    vehicle.SetEnum(FlightComputerBurnMode.Auto);
                    // Record the pass only if Auto or RCS accepted the request.
                    if (fc.BurnMode == FlightComputerBurnMode.Auto || RcsIsFlyingPass(vehicle, exec))
                        exec.PassArmed = true;
                }
```

## Game APIs touched

| Member | Use |
|---|---|
| `Vehicle.SetEnum(FlightComputerBurnMode.Auto)` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6321)) | Arm Auto for the next pass through the interceptable path |
| `FlightComputer.AddBurn`, `LoadBurn`, `RemoveBurn` ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L2209)) | How a queued pass becomes the loaded target |
| `BurnPlan.TryGetBurn(Burn)` ([current/decomp/KSA/BurnPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnPlan.cs)) | Materialization check |
| `InputEvents.BurnUpdateBuffer` ([current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1000)) | Queued pass creation |

## Pitfalls and version notes

- Never arm Auto in the same frame you queue a burn: the burn is not loaded yet, and `LoadBurn` resets
  the mode to Manual anyway.
- With another burn ahead of the pass in the plan, `AddBurn` does not load the pass; arming Auto then
  would arm the other burn, so AFC drops the request.
- Same-parent Hohmann executions recompute later passes against the live periapsis time because finite
  burns move it.

## See also

- [Multi-pass burn splitting](multi-pass-burn-splitting.md)
- [Multi-pass Hohmann schedule](multi-pass-hohmann-schedule.md)
- [Automatic staging](automatic-staging.md)
- [Automatic removal of finished burns](auto-remove-finished-burns.md)
- [Run a multi-pass burn sequence as a state machine on stock Auto, persisted per vehicle](../../../howto/flight-and-orbits/autopilot/execute-planned-burns-with-the-stock-autopilot/execute-planned-burns-with-the-stock-autopilot-1.md) - how-to article demonstrating this feature

[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
