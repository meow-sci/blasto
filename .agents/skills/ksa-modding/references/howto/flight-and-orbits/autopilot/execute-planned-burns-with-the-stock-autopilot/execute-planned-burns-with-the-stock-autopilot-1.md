---
type: How-To
title: Run a multi-pass burn sequence as a state machine on stock Auto, persisted per vehicle
description: Commit one pass as an ordinary stock node, re-arm Auto after the previous pass loaded in Manual, advance on stock burn-end events and persist the execution so a reload resumes
category: flight-and-orbits
group: autopilot
topic: execute-planned-burns-with-the-stock-autopilot
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/multi-pass-execution]
techniques: [save-sidecar, harmony-postfix, state-machine]
hooks:
  - harmony-postfix KSA.Universe.ApplyVehicleSolvers
  - harmony-postfix KSA.FlightComputer.RaisePendingAlerts
lifecycle: [frame:apply-solvers, frame:prepare, frame:input]
game_types:
  - KSA.FlightComputer
  - KSA.BurnPlan
  - KSA.Burn
  - KSA.Vehicle
  - KSA.InputEvents
  - KSA.FlightComputerBurnMode
  - KSA.Universe
tags: [auto-burn, multi-pass, state-machine, sidecar, re-arm, burn-plan]
---

Outcome: after the player presses Create with several passes and the stock **Auto** mode on, each pass fires automatically, the next is placed once the
previous finished, and the sequence survives a reload. KSA-AdvancedFlightComputer implements it.[Multi-pass execution as a per-vehicle state machine on stock Auto burns](../../../../mods/ksa-advancedflightcomputer/features/multi-pass-execution.md)
The mod never flies a pass itself: every pass is an ordinary stock burn, and it only decides when to place the next one and when to arm Auto.
Prerequisites: StarMap and Harmony.

## Background: how the game does it

See the [overview](overview.md) for burn modes and `RaisePendingAlerts`. Two engine facts drive the design. A burn queued through
`InputEvents.BurnUpdateBuffer` appears in the plan only after the next input drain; and `FlightComputer.AddBurn` loads it only if it is the first
executable burn, resetting `BurnMode` to Manual. So Auto has to be re-selected after the pass is loaded.[^ksa-flightcomputer] [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L2209-L2217)
[current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L346-L353)

## Lifecycle and timing

| Moment | Lifecycle | Thread |
|---|---|---|
| Tick (postfix on `Universe.ApplyVehicleSolvers`) | `frame:apply-solvers`, after automatic staging, before an RCS driver | main |
| Burn end (postfix on `RaisePendingAlerts`) | `frame:prepare`, workers joined, plan editable | main |
| Queued add applied | `frame:input`: the same frame's drain for a pass queued from the tick (the tick runs before `ApplyInputEvents`), the next frame's drain for a first pass queued from a draw | main |

Ordering matters: the tick reads the burn mode before the RCS driver may change it, and the burn-end handler runs before automatic removal looks at the plan. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Keep an execution record per vehicle**: the intent (the goal, not the UI input), split mode, pass count and index, the live `Burn` of the current pass
   (transient) with its time and delta-v (persisted, to reattach after a load), and flags for "awaiting materialization", "re-engage Auto on next burn" and "pass armed".
2. **Commit the first pass** by recomputing the maneuver from the live vehicle and queuing it as in [Create, chain and read stock maneuver nodes from code](../create-and-read-maneuver-nodes/overview.md) (`QueueAddBurn`).
3. **Two entry points per frame.** The post-solver tick reconciles after a load, observes arming, tracks materialization and, when no pass is pending, commits the next; the
   burn-end handler marks completion:

```csharp
// AdvancedFlightComputer/Features/MultiPass/PassCompletionPatch.cs L19-L82
// Runs after FlightComputer.RaisePendingAlerts with the workers joined, so the plan can be changed directly.
// keptByStock says whether FlightComputer.EndAutoBurn left the burn in the plan, which it does only for the last planned burn.
internal static void OnStockBurnEnded(Vehicle vehicle, Burn burn, bool completed, bool keptByStock)
{
    if (!MultiPassRegistry.TryGet(vehicle.Id, out MultiPassExecution? exec)
        || !ReferenceEquals(exec.CurrentBurn, burn))
        return;

    try
    {
        if (completed)
        {
            if (MultiPassDebug.Enabled)
                DefaultCategory.Log.Debug(
                    $"[AFC] MultiPass: vehicle='{vehicle.Id}' pass {exec.PassIndex + 1}/{exec.PassCountTotal} " +
                    $"completed, {(keptByStock ? "kept" : "removed")} by stock.");
            CompletePass(vehicle.Id, exec, vehicle.FlightComputer);
        }
        else
        {
            StopPass(vehicle, exec, keptByStock);
        }
    }
    catch (Exception ex)
    {
        DefaultCategory.Log.Error(
            $"[AFC] MultiPass: vehicle={vehicle.Id} stock burn end threw, cancelling execution: {ex}");
        CancelExecution(vehicle.Id, reason: null);
    }
}

internal static void TickVehicle(Vehicle vehicle)
{
    if (!MultiPassRegistry.TryGet(vehicle.Id, out var exec))
        return;

    FlightComputer fc = vehicle.FlightComputer;

    // Keep active execution controls available after ignition clears the stock calculated flag.
    if (exec.Intent is HohmannTransferIntent
        && IsPlanWindowOnVehicleHohmann(vehicle))
        KeepStockTransferCalculatedInSync();

    // Execution changes remain in memory until the game is saved.
    try
    {
        ReconcileResult reconcile = ReconcileAfterLoad(vehicle.Id, exec, fc);
        if (reconcile != ReconcileResult.Proceed)
            return;

        ObserveArming(vehicle, exec, fc);

        if (UpdateMaterializationTracking(vehicle, exec, fc) == MaterializationResult.Cancelled)
            return;

        AdvanceExecution(vehicle, exec, fc);
    }
    catch (Exception ex)
    {
        DefaultCategory.Log.Error(
            $"[AFC] MultiPass: vehicle={vehicle.Id} tick threw, cancelling execution: {ex}");
        CancelExecution(vehicle.Id, reason: null);
    }
}
```

4. **Re-arm Auto after the pass loads.** Wait until the burn is in the plan (up to 4 ticks, then cancel as "queue dropped the add"), check that the loaded target is this
   pass, and call `SetEnum` rather than writing the field, because other mods intercept that path:

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

5. **Persist by save and vehicle.** `MultiPassRegistry` writes executions to `multipass.toml` keyed by save id and vehicle id (see
   [Save-scoped TOML registries beside the mod](../../../../mods/ksa-advancedflightcomputer/features/save-scoped-sidecar-registries.md)); on load, references are reattached by persisted time and delta-v.
6. **Handle the edges**: a propellant stop (a pass stock kept waits in Manual; a pass stock removed is planned again from the current orbit; with no propellant anywhere the
   execution ends with an alert), a disengaged Auto (keep the execution, hint once), a deleted pass (cancel), and an early-satisfied goal (finish successfully).
7. **Cancel after repeated failures**: five consecutive scheduling failures cancel the execution.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/MultiPass/PassCompletionPatch.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/PassCompletionPatch.cs) | state machine |
| [AdvancedFlightComputer/Features/MultiPass/MultiPassExecution.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassExecution.cs) | execution state |
| [AdvancedFlightComputer/Features/MultiPass/MultiPassController.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassController.cs) | start, status, cancel |
| [AdvancedFlightComputer/Features/MultiPass/MultiPassCommitter.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassCommitter.cs) | commit the next pass |
| [AdvancedFlightComputer/Features/MultiPass/MultiPassRegistry.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassRegistry.cs) | persistence |

## Variations and gotchas

- **Do not re-arm in the same call that queues the add**: the burn is not in the plan until `ApplyInputEvents` ran.
- **Another burn ahead of the pass** means `AddBurn` does not load it; the re-arm request is dropped rather than arming that other burn.
- **RCS passes run in Manual** (the RCS executor flies them); do not mistake that for a stall; see [Execute burns with RCS translation and throttle inputs from code](../execute-burns-with-rcs-and-throttle-inputs/overview.md).
- Stock's planner fields (`_transferCalculated`, `_transferBurn`) are kept in sync for a Hohmann execution so the plan window keeps its controls after ignition.

## See also

- [Overview](overview.md); [burn-end detection](execute-planned-burns-with-the-stock-autopilot-4.md); [removing finished burns](execute-planned-burns-with-the-stock-autopilot-5.md)
- [Multi-pass execution as a per-vehicle state machine on stock Auto burns](../../../../mods/ksa-advancedflightcomputer/features/multi-pass-execution.md)
- [Create, chain and read stock maneuver nodes from code](../create-and-read-maneuver-nodes/overview.md)

[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
