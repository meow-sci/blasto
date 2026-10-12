---
type: How-To
title: Hand a retargeted transfer to the stock Create path
description: Return a mod-computed Burn from a same-signature replacement of Burn.Create so the planner's own CREATE button queues it, with a click gate for repeated clicks and a fallback to the stock burn on any failure
category: flight-and-orbits
group: transfers
topic: add-plan-type-to-the-transfer-planner
approach: 4
difficulty: advanced
prerequisites: [harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/flyby-periapsis-targeting]
techniques: [harmony-transpiler]
hooks: [harmony-transpiler KSA.TransferPlanner.DrawPlanWindow]
lifecycle: [frame:draw-ui, frame:input]
game_types: [KSA.TransferPlanner, KSA.Burn, KSA.InputEvents, KSA.Vehicle, KSA.PatchedConic]
tags: [create, burn-create, retarget, interceptor, click-gate, fallback, transfer-planner]
---

Outcome: the planner's own CREATE button creates a burn the mod computed (a flyby departure, or the first pass of a multi-pass transfer) instead of the center-aimed one, and any failure falls back to the stock burn with an alert. KSA-AdvancedFlightComputer does this for flyby targeting.[Aim a transfer at a flyby periapsis instead of the body center](../../../../mods/ksa-advancedflightcomputer/features/flyby-periapsis-targeting.md)
The solve itself is in [Aim a transfer at a flyby periapsis and predict its encounter](../aim-a-transfer-at-a-flyby-periapsis/overview.md). Prerequisite: Harmony.

## Background: how the game does it

Stock's CREATE path calls `Burn.Create(point, time, deltaVVlf, patch, vehicle)` and then queues the returned burn into `InputEvents.BurnUpdateBuffer` (applied at `frame:input` in the next frame; see [Create, chain and read stock maneuver nodes from code](../../autopilot/create-and-read-maneuver-nodes/overview.md)).[^ksa-transferplanner] [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L770-L786) Because the call is a single static call with arguments already on the stack, a replacement with an identical signature can return any `Burn` and stock queues it unchanged.

## Lifecycle and timing

The swapped call runs exactly when stock would create its burn: inside `DrawPlanWindow` on the main thread (`frame:draw-ui`) at the click. Stock then queues the returned burn; the add happens in `ApplyInputEvents` (`frame:input`) the next frame. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Swap the call in the transpiler** with a method of the same signature and carry labels and blocks over (see [approach 2](add-plan-type-to-the-transfer-planner-2.md)).
2. **Gate the click** before the swap runs: the click gate rejects an active execution with an alert so a second click cannot start another one:

```csharp
// AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs L17-L40
public static bool ShouldAllowCreateClick(bool wasClicked)
{
    if (!wasClicked) return false;
    if (!HohmannMultiPassUI.Enabled && !HohmannFlybyUI.Enabled) return true;
    try
    {
        if (StockPlanner.SourceVehicle is not Vehicle source) return true;
        if (!MultiPassRegistry.Has(source.Id)) return true;

        TimedAlert.Create(ActiveExecAlert, Color.Yellow, 5.0);
        DefaultCategory.Log.Warning(
            $"[AFC] HohmannCreateInterceptor: Create click absorbed for " +
            $"vehicle={source.Id} (active multi-pass exec running; cancel it first).");
        return false;
    }
    catch (Exception ex)
    {
        // Keep the stock UI available if the gate fails.
        DefaultCategory.Log.Warning(
            $"[AFC] HohmannCreateInterceptor.ShouldAllowCreateClick: {ex}; allowing click.");
        return true;
    }
}

```

3. **Return the requested burn or fall back.** If neither feature is enabled return stock's `Burn.Create`; if an execution is active, return a separate stock burn, never a reference to the active pass; otherwise create the requested burn:

```csharp
// AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs L42-L70
public static Burn CreateMaybeMultiPass(
    OrbitPointCce point, double time, double3 deltaVVlf,
    PatchedConic patch, Vehicle vehicle)
{
    try
    {
        if (!HohmannMultiPassUI.Enabled && !HohmannFlybyUI.Enabled)
            return Burn.Create(point, time, deltaVVlf, patch, vehicle);

        // A failed gate must return a separate stock burn, never a reference to the active pass.
        if (MultiPassRegistry.Has(vehicle.Id))
        {
            TimedAlert.Create(ActiveExecAlert, Color.Yellow, 5.0);
            DefaultCategory.Log.Warning(
                $"[AFC] HohmannCreateInterceptor: re-click for vehicle={vehicle.Id} " +
                "with active exec reached Burn.Create swap (click gate not active); " +
                "falling back to stock single burn. Duplicate burn may queue.");
            return Burn.Create(point, time, deltaVVlf, patch, vehicle);
        }

        return CreateRequestedBurn(point, time, deltaVVlf, patch, vehicle);
    }
    catch (Exception ex)
    {
        DefaultCategory.Log.Warning(
            $"[AFC] HohmannCreateInterceptor: {ex}; falling back to stock single burn.");
        return Burn.Create(point, time, deltaVVlf, patch, vehicle);
    }
}
```

4. **Prefer the richest request, degrade stepwise:** multi-pass first (and bake a flyby request into the final pass), then a single flyby burn with an alert ("Multi-pass setup failed; firing a single flyby burn instead."), then the stock impact-aimed burn with an alert ("Flyby retarget failed; firing the stock (impact-aimed) burn.").
5. **Catch everything** and fall back to `Burn.Create` with a warning: an exception here would propagate into the draw.
6. **Do not edit the flight computer here:** the draw overlaps the vehicle solver; return the burn and let stock queue it.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs) | Create interception and click gate |
| [AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs#L131-L199) | the swap and the gate injection |
| [AdvancedFlightComputer/Features/Flyby/FlybyTargeting.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Flyby/FlybyTargeting.cs) | the retargeted departure |

## Variations and gotchas

- **A failed gate must return a separate stock burn,** never a reference to the active pass: stock would queue it again.
- **The click gate is a filter on the button's `bool`,** so it must sit immediately after the call to `PrimaryButton`.
- **Duplicate burns can queue** if the gate is not active and the swap is reached on a re-click; the code logs it.
- A custom plan type in its own body ([approach 1](add-plan-type-to-the-transfer-planner-1.md)) does not need this: it can call `QueueAddBurn` itself.

## See also

- [Overview](overview.md); [transpiler injection](add-plan-type-to-the-transfer-planner-2.md)
- [Aim a transfer at a flyby periapsis instead of the body center](../../../../mods/ksa-advancedflightcomputer/features/flyby-periapsis-targeting.md)
- [Aim a transfer at a flyby periapsis and predict its encounter](../aim-a-transfer-at-a-flyby-periapsis/overview.md)

[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
