---
type: How-To
title: Create a stock maneuver node with Burn.Create and queue it through InputEvents.BurnUpdateBuffer
description: Build a Burn on the right patched conic and add it to the flight computer's plan by queuing a BurnUpdateData, the same way the stock Transfer Planner does, so it never races the vehicle solver
category: flight-and-orbits
group: autopilot
topic: create-and-read-maneuver-nodes
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/planner-burn-node-creation]
techniques: [input-buffer, harmony-prefix]
hooks: [harmony-prefix KSA.TransferPlanner.DrawPlanWindow]
lifecycle: [frame:draw-ui, frame:input]
game_types:
  - KSA.Burn
  - KSA.InputEvents
  - KSA.FlightPlan
  - KSA.PatchedConic
  - KSA.TransferPlanner
  - KSA.FlightComputer
  - KSA.Vehicle
tags: [maneuver-node, burn-create, input-events, burn-update-buffer, transfer-planner]
---

Outcome: a node that appears in the stock burn plan and burn editor, that the stock Auto button can fly and that is saved in the stock format.
KSA-AdvancedFlightComputer creates one whenever the player presses CREATE in one of its plan types.[Create a stock maneuver node from a computed impulse](../../../../mods/ksa-advancedflightcomputer/features/planner-burn-node-creation.md)
Prerequisites: StarMap; Harmony only to hook a plan window. The node creation itself is plain game API.

## Background: how the game does it

The stock Transfer Planner creates its burn with `Burn.Create` and then does not call `FlightComputer.AddBurn`; it adds an
`InputEvents.BurnUpdateData { Burn, FlightComputer, AddBurn = true }` to `InputEvents.BurnUpdateBuffer`.[^ksa-transferplanner] [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L776-L786)
`ApplyInputEvents` sorts that buffer (deletes first, then adds, then updates) and applies each entry, which calls `FlightComputer.AddBurn`.[^ksa-inputevents] [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L877-L905)
[current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1118-L1120)

## Lifecycle and timing

The player's click is handled during the plan-window draw (`frame:draw-ui`), when this frame's vehicle solver has already been dispatched in
`PrepareFrame`, so the vehicle's `FlightComputer` must not be mutated there. The queued add is applied by `InputEvents.ApplyInputEvents`
in the next `PrepareFrame` (`frame:input`), after the solver results are applied and before the next step is dispatched.[^ksa-program]
[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2167-L2181) The same rule applies to target changes (`InputEvents.ChangeTargetBuffer`). See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Find the patched conic the burn belongs to.** A burn lives on one `PatchedConic` of a flight plan. Look it up with `FlightPlan.TryFindPatch(burnTime)`;
   no patch at that time means no burn. When chaining after another pending burn, use that burn's plan (see
   [approach 3](create-and-read-maneuver-nodes-3.md)); otherwise the vehicle's live plan.
2. **Compute the impulse in the velocity-local frame (VLF).** `OrbitalTransfers.CciToVlf(orbit, time, dvCci)` converts a CCI delta-v.
3. **Build the burn and queue the add:**

```csharp
// AdvancedFlightComputer/Features/MultiPass/MultiPassCommitter.cs L25-L49
// Buffer additions with other input changes. No burn can be created if the flight plan has no patch at the requested time.
public static Burn? QueueAddBurn(
    Vehicle source, UniverseTime burnTime, double3 dvVlf, FlightPlan? chainPlan = null)
{
    // A supplied chain plan anchors the maneuver after another pending burn. Normal pass commits use the live plan because the vehicle has already flown the previous pass and its prediction can be stale.
    PatchedConic? patch = chainPlan?.TryFindPatch(burnTime)
                          ?? source.FlightPlan.TryFindPatch(burnTime);
    if (patch == null)
    {
        return null;
    }

    OrbitPointCce point = patch.Orbit.GetPointAt(burnTime);
    Burn burn = Burn.Create(point, burnTime.Seconds(), dvVlf, patch, source);
    burn.IsGizmoActive = false;

    InputEvents.BurnUpdateBuffer.Add(new InputEvents.BurnUpdateData
    {
        Burn = burn,
        FlightComputer = source.FlightComputer,
        AddBurn = true,
    });

    return burn;
}
```

   `IsGizmoActive = false` stops the new node from grabbing the burn gizmo.
4. **Commit after the footer.** The mod decides the footer state first, draws the CREATE button and calls `QueueAddBurn` only after
   `ConsoleStyle.EndFooter()`, as stock does ([AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.Commit.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.Commit.cs)).
5. **Track the node you created.** Keep the `Burn` so a footer can read NODE CREATED until its time passes or it leaves the plan.
   `Burn.Equals` compares time and delta-v, so plan membership alone does not prove ownership; also check `burn.Vehicle.Id`.
6. **Check the result next frame**: the burn is in `FlightComputer.BurnPlan` only after `ApplyInputEvents` ran.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/MultiPass/MultiPassCommitter.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassCommitter.cs) | `QueueAddBurn`: patch lookup, `Burn.Create`, queued add |
| [AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.Commit.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.Commit.cs) | footer states and commit dispatch |
| [AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.cs) | `CleanupStaleBurn`, commit after `EndFooter` |
| [tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/SetApoapsisTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/SetApoapsisTest.cs) | headless test: the created node reaches the requested apoapsis |

## Variations and gotchas

- **Do not call `FlightComputer.AddBurn` from UI code.** It races the worker that is reading the plan; stock never does.
- **The node is not there until next frame.** Anything that reads `BurnPlan` right after queuing sees the old plan.
- **A burn needs its patch.** After an SOI change, an old `PatchedConic` reference can be stale; look the patch up again.
- With multi-pass armed, CREATE starts a multi-pass execution that commits its first pass through the same call (see
  [Execute planned burns hands-free with the stock Auto burn](../execute-planned-burns-with-the-stock-autopilot/overview.md)).

## See also

- [Overview](overview.md); [direct burn target](create-and-read-maneuver-nodes-2.md); [impulse formulas](create-and-read-maneuver-nodes-3.md)
- [Create a stock maneuver node from a computed impulse](../../../../mods/ksa-advancedflightcomputer/features/planner-burn-node-creation.md)
- [Execute planned burns hands-free with the stock Auto burn](../execute-planned-burns-with-the-stock-autopilot/overview.md)

[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
[^ksa-inputevents]: KSA.InputEvents (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
