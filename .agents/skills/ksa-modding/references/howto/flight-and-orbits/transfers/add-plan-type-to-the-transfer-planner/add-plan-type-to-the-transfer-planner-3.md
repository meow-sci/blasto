---
type: How-To
title: Open and preselect the stock Transfer Planner with AccessTools FieldRefs and delegates bound once
description: Bind the planner's private static fields and SetTransferInfo once, replay stock's combo-change side effects in order, and degrade to just opening the window when a key goes missing
category: flight-and-orbits
group: transfers
topic: add-plan-type-to-the-transfer-planner
approach: 3
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-deltavmap/features/stock-transfer-planner-bridge]
techniques: [reflection]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.TransferPlanner, KSA.TransferType, KSA.Universe, KSA.Vehicle]
tags: [reflection, field-ref, access-tools, preselect, transfer-planner, interstellar, bridge]
---

Outcome: a button in a mod's window that opens the game's Transfer Planner already set to the Interstellar plan, with the vessel as source and the route's destination star system selected. KSA-DeltaVMap does it with no Harmony patch at all: `StockPlannerBridge` binds the planner's private static fields and private `SetTransferInfo()` once and writes them in the same order as one stock frame of combo changes.[Open the stock Transfer Planner preselected on an interstellar plan](../../../../mods/ksa-deltavmap/features/stock-transfer-planner-bridge.md)
Prerequisites: StarMap and Harmony (for `AccessTools` only).

## Background: how the game does it

The fields exist in build 2026.10.10.5554: `_sourceBody`, `_destinationBody`, `_transferType` (`TransferObject`/`TransferType`), `_selectedEntry` (`OrbitalTransfers.PorkChopEntry?`), `_selectedTimeUnit`, `_timeUnits`, `_transferCalculated`, `_transferBeingCalculated`, `_interstellarDepartureFailed`, `cts` (`CancellationTokenSource`) and `private static void SetTransferInfo()`; `ShowPlanWindow` is public.[^ksa-transferplanner] [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L53-L67)
[current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L225-L260) The planner's state is plain static fields that stock reads and writes only from the main-thread UI draw; its expensive solve runs on a `TransferTask` worker with `cts`.

## Lifecycle and timing

The hook is `[StarMapAfterGui]` (`frame:draw-ui`, main thread) on the frame the button is clicked. In build 2026.10.10.5554 `AfterGui` fires before `Program.OnDrawUiThreadSafe`, which is where `TransferPlanner.DrawPlanWindow` runs when `ShowPlanWindow` is true, so the planner draws with the new state later in the same frame; since all writes happen in one synchronous call, stock never observes a half-written selection.
(The mod's comment says the writes happen after stock drew; the decompiled frame order is the other way round, which does not change the outcome.) When the window appears, stock resets `_sourceBody` to the controlled vehicle on `ImGui.IsWindowAppearing()` and calls `SetTransferInfo` again; the bridge's writes agree with that. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Bind once, at class initialization.** `Bind<T>(name)` requires a static field of exactly type `T` and returns `AccessTools.StaticFieldRefAccess<T>`; a failed lookup appends a readable key to a `Missing` list declared before the bindings (static initializers run in order). Bind `SetTransferInfo` with `AccessTools.DeclaredMethod(..., Type.EmptyTypes)` and `CreateDelegate<Action>()`:

```csharp
// DeltaVMap/Core/StockPlannerBridge.cs L42-L55
// Declared first, because the bindings below add to it while the class initializes.
private static readonly List<string> Missing = new();

private static readonly AccessTools.FieldRef<TransferType>? TransferTypeRef = Bind<TransferType>("_transferType");
private static readonly AccessTools.FieldRef<TransferObject>? SourceBodyRef = Bind<TransferObject>("_sourceBody");
private static readonly AccessTools.FieldRef<TransferObject>? DestinationBodyRef = Bind<TransferObject>("_destinationBody");
private static readonly AccessTools.FieldRef<OrbitalTransfers.PorkChopEntry?>? SelectedEntryRef = Bind<OrbitalTransfers.PorkChopEntry?>("_selectedEntry");
private static readonly AccessTools.FieldRef<TimeObject>? SelectedTimeUnitRef = Bind<TimeObject>("_selectedTimeUnit");
private static readonly AccessTools.FieldRef<List<TimeObject>>? TimeUnitsRef = Bind<List<TimeObject>>("_timeUnits");
private static readonly AccessTools.FieldRef<bool>? TransferCalculatedRef = Bind<bool>("_transferCalculated");
private static readonly AccessTools.FieldRef<bool>? TransferBeingCalculatedRef = Bind<bool>("_transferBeingCalculated");
private static readonly AccessTools.FieldRef<bool>? InterstellarDepartureFailedRef = Bind<bool>("_interstellarDepartureFailed");
private static readonly AccessTools.FieldRef<CancellationTokenSource?>? CancellationRef = Bind<CancellationTokenSource?>("cts");
private static readonly Action? SetTransferInfo = BindSetTransferInfo();
```

2. **Look up list entries by value at call time:** the `TransferType` whose `GetKey()` is `"Interstellar"` in the public `TransferTypes` (by key, not index, because other mods add plan types) and the `TimeObject` with `Unit == TimeUnit.Years` in `_timeUnits`.
3. **Replay stock in order.** `SelectType` first (so the source change's `SetTransferInfo` sees no destination and never builds a Hohmann transfer for the plan type that is about to go), then `SelectSource`, then `SelectDestination`; each returns early when the field already holds that key so a second click changes nothing:

```csharp
// DeltaVMap/Core/StockPlannerBridge.cs L159-L179
private static void SelectType(TransferType interstellar, TimeObject years)
{
    if (TransferTypeRef!().GetKey() == InterstellarKey)
        return;
    TransferTypeRef!() = interstellar;
    TransferCalculatedRef!() = false;
    CancelCalculation();
    DestinationBodyRef!() = new TransferObject(-1);
    SelectedEntryRef!() = null;
    SelectedTimeUnitRef!() = years;
    SetTransferInfo!();
}

private static void SelectDestination(Astronomical? destination)
{
    if (destination == null || DestinationBodyRef!().GetKey() == destination.Id)
        return;
    DestinationBodyRef!() = new TransferObject(destination);
    TransferCalculatedRef!() = false;
    InterstellarDepartureFailedRef!() = false;
}
```

4. **Accept only destinations stock would accept.** The destination root is valid only if the vehicle's own root (`Universe.NearestRoot(vehicle.GetPositionEcl())`) is the departure root and the destination is a different entry of `Universe.Roots` (mirrors `TransferPlanner.PopulateWithStars`); otherwise set only the plan type and tell the player to pick the destination there.
5. **Always open the window** with the public `ShowPlanWindow = true` first. If any key is missing, log which ones once and only open it; if a write throws, set a broken flag and from then on only open it.
6. **Cancel like stock:** cancel `cts` only while `_transferBeingCalculated`, tolerating an already disposed source.
7. **Test it:** a harness test fails if any key is missing, captures the planner state, runs the selection and the button path, checks the result and restores the original state.

## Complete example

| File | Role |
|---|---|
| [DeltaVMap/Core/StockPlannerBridge.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Core/StockPlannerBridge.cs) | bindings, missing-key report, preselect steps, capture/restore |
| [DeltaVMap/Render/InterstellarSection.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/InterstellarSection.cs#L459-L481) | the button and its hint text |
| [DeltaVMap.HarnessTests/StockPlannerKeysTest.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap.HarnessTests/StockPlannerKeysTest.cs) | headless check that every key resolves and the writes round-trip |

## Variations and gotchas

- **With an interstellar destination `SetTransferInfo` only clears the transfer info,** because a system root (`FixedStar`/`Barycenter`) is not an `IOrbiter` and the planner's `Destination` property is null.
- **Renames fail quietly:** bind by name, report the missing keys, and keep a test that resolves them.
- **Do not write while a calculation runs** without cancelling it the way stock does.
- `FieldRef` access has no per-call reflection cost, unlike `FieldInfo.SetValue`.

## See also

- [Overview](overview.md); [skip-original prefix](add-plan-type-to-the-transfer-planner-1.md)
- [Open the stock Transfer Planner preselected on an interstellar plan](../../../../mods/ksa-deltavmap/features/stock-transfer-planner-bridge.md)
- [Plan and total multi-leg and interstellar delta-v routes](../plan-multi-leg-and-interstellar-delta-v-routes/overview.md)

[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
