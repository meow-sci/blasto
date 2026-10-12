---
type: Mod Feature
title: Create a stock maneuver node from a computed impulse
description: Turn an impulsive delta-v at a time into a stock Burn with Burn.Create on the right flight-plan patch and queue it through InputEvents.BurnUpdateBuffer exactly as the Transfer Planner does, so stock Auto, saves and the burn editor treat it as a normal node
mod: ksa-advancedflightcomputer
category: flight-and-orbits
techniques: [harmony-prefix]
hooks: [harmony-prefix KSA.TransferPlanner.DrawPlanWindow]
lifecycle: [frame:draw-ui, frame:input]
game_types:
  - KSA.Burn
  - KSA.BurnPlan
  - KSA.FlightComputer
  - KSA.FlightPlan
  - KSA.PatchedConic
  - KSA.InputEvents
  - KSA.TransferPlanner
tags: [maneuver-node, burn-plan, input-events, flight-plan, transfer-planner]
---

When the player presses CREATE in an AFC plan type, the result is an ordinary stock maneuver node:
it shows in the burn plan and the burn editor, the stock Auto button flies it, and it is saved in the
stock save format. AFC achieves this by building the node with the same factory and the same queued
add that the stock Transfer Planner uses for its own transfer burns.

## How it works

1. **Find the patch.** A burn belongs to one patched conic of a flight plan. `QueueAddBurn` looks up
   `FlightPlan.TryFindPatch(burnTime)`, on the chained plan of the previous pending burn when there is
   one (see [apse and inclination quick tools](apse-and-inclination-quick-tools.md)), otherwise on the
   vehicle's live flight plan. No patch at that time means no burn
   ([AdvancedFlightComputer/Features/MultiPass/MultiPassCommitter.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassCommitter.cs#L25-L49)).
2. **Build the burn.** `Burn.Create(patch.Orbit.GetPointAt(burnTime), burnTime.Seconds(), dvVlf, patch, vehicle)`
   with the delta-v in the velocity-local frame (VLF) that `OrbitalTransfers.CciToVlf` produced, and
   `IsGizmoActive = false` so the new node does not grab the gizmo.
3. **Queue the add.** Instead of calling `FlightComputer.AddBurn` directly, add
   `InputEvents.BurnUpdateData { Burn, FlightComputer, AddBurn = true }` to
   `InputEvents.BurnUpdateBuffer`. This is exactly what stock `DrawPlanWindow` does after its own
   `Burn.Create` ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L776-L786)).[^ksa-transferplanner] The buffer is sorted
   (deletes, then adds, then updates) and applied in `InputEvents.ApplyInputEvents`, which calls
   `FlightComputer.AddBurn` ([current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L877-L905)).
4. **Commit after the footer.** The window decides the footer state first (`MANEUVER READY`,
   `NODE CREATED`, `MULTI-PASS RUNNING`, `MULTI-PASS PREVIEW FAILED`, `NO MANEUVER`), draws the CREATE
   button, and creates the burn only after `ConsoleStyle.EndFooter()`, as stock does
   ([AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.Commit.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.Commit.cs)).
5. **Track the node.** The created `Burn` is remembered so the footer reads NODE CREATED until its time
   passes or it leaves the plan. Because `Burn.Equals` compares time and delta-v, plan membership alone
   does not prove ownership; AFC also checks `burn.Vehicle.Id` against the selected source.
6. **Multi-pass.** With passes armed, CREATE starts a multi-pass execution instead, which commits its
   first pass through the same `QueueAddBurn` (see [multi-pass execution](multi-pass-execution.md)).

## Lifecycle and timing

CREATE is handled during the plan-window draw (`frame:draw-ui`). The vehicle solver for this frame was
already dispatched in `PrepareFrame`, so the vehicle's `FlightComputer` must not be mutated here; the
queued add is applied by `InputEvents.ApplyInputEvents` in the next `Program.PrepareFrame`
(`frame:input`), after the solver results are applied and before the next step is dispatched
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2167-L2181)).[^ksa-program] The same rule applies to target changes
(`InputEvents.ChangeTargetBuffer`).

Stock's own Hohmann CREATE is not replaced by this path; for flyby and multi-pass it is intercepted at
the call site (see [plan window transpiler injection](plan-window-transpiler-injection.md)).

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/MultiPass/MultiPassCommitter.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassCommitter.cs) | `QueueAddBurn`: patch lookup, `Burn.Create`, queued add |
| [AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.Commit.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.Commit.cs) | Footer states and commit dispatch |
| [AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.cs) | `CleanupStaleBurn`, commit after `EndFooter` |
| [tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/SetApoapsisTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/SetApoapsisTest.cs) | Headless test: the created node reaches the requested apoapsis |

## Snippets

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

## Game APIs touched

| Member | Use |
|---|---|
| `Burn.Create(OrbitPointCce, double, double3, PatchedConic, Vehicle)` ([current/decomp/KSA/Burn.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Burn.cs#L185)) | Node factory |
| `FlightPlan.TryFindPatch(UniverseTime)` ([current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs)), `Orbit.GetPointAt` | Patch and point at burn time |
| `InputEvents.BurnUpdateBuffer`, `BurnUpdateData` ([current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1000)) | Queued add applied in the input drain |
| `FlightComputer.AddBurn(Burn)` ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L2209)) | What the buffer finally calls |

## Pitfalls and version notes

- Delta-v must be in VLF for `Burn.Create`; passing CCI components gives a burn in the wrong
  direction.
- A queued burn does not exist in the plan until next frame; do not look it up by plan membership in
  the same frame.
- For the live plan after a pass has been flown, use `vehicle.FlightPlan`, not a cached chain plan,
  because the prediction moved.

## See also

- [Transfer Planner custom plan types](transfer-planner-custom-plan-types.md)
- [Planner preview trajectories](planner-preview-trajectories.md)
- [Multi-pass execution](multi-pass-execution.md)
- [Create a stock maneuver node with Burn.Create and queue it through InputEvents.BurnUpdateBuffer](../../../howto/flight-and-orbits/autopilot/create-and-read-maneuver-nodes/create-and-read-maneuver-nodes-1.md) - how-to article demonstrating this feature

[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
