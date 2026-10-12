---
type: How-To
title: AccessTools static field refs bound once, replay stock's writes
description: Bind the Transfer Planner's private static fields with AccessTools.StaticFieldRefAccess and its private SetTransferInfo as an Action delegate once at class initialisation, then replay the writes stock performs in the order it performs them, with no Harmony patch at all
category: patching
group: internals
topic: call-private-game-members-through-reflection-and-delegates
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-deltavmap/features/stock-transfer-planner-bridge]
techniques: [accesstools-fieldref, method-delegate, reflection]
hooks:
  - starmap StarMapAfterGui
  - reflection KSA.TransferPlanner._sourceBody
  - reflection KSA.TransferPlanner.SetTransferInfo
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.TransferPlanner, KSA.TransferObject, KSA.TransferType]
tags: [reflection, accesstools, fieldref, delegate, private-static, transfer-planner]
---

KSA-DeltaVMap's interstellar panel has an OPEN STOCK TRANSFER PLANNER button that opens the game's own Transfer Planner already set to the Interstellar plan, with the player's vessel as source and the
route's destination selected. The planner's plan type, source, destination and selection are private statics. DeltaVMap reaches them without a Harmony patch: it binds each field once with
`AccessTools.StaticFieldRefAccess<T>`, which yields a `ref T` getter with no per-call reflection, binds the private `SetTransferInfo()` as an `Action`, and then writes them in the same order, with the
same side effects, as one stock frame of combo changes.

Prerequisites: a StarMap mod referencing `0Harmony` (for `AccessTools`). Demonstrated by KSA-DeltaVMap in [Open the stock Transfer Planner preselected on an interstellar plan](../../../../mods/ksa-deltavmap/features/stock-transfer-planner-bridge.md).

## Background: how the game does it

`TransferPlanner` keeps its state in private static fields and a private static method:

```csharp
// current/decomp/KSA/TransferPlanner.cs L63-L67
private static TransferObject _sourceBody;

private static TransferObject _destinationBody;

private static TransferType _transferType = TransferTypes[0];
```

Source: [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L63-L67)[^ksa-transferplanner]

```csharp
// current/decomp/KSA/TransferPlanner.cs L139
private static CancellationTokenSource cts;
```

Source: [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L139)

When the PLAN TYPE or SOURCE combo changes in `DrawPlanWindow`, stock writes several of those fields together (reset `_transferCalculated`, cancel `cts` if a calculation is running, replace
`_destinationBody`, clear `_selectedEntry`, set `_selectedTimeUnit`) and calls `SetTransferInfo()`; the DESTINATION combo of the interstellar plan has its own handler in `DrawInterstellarPlan`.
Anything that selects a plan from outside has to repeat that bookkeeping:

```csharp
// current/decomp/KSA/TransferPlanner.cs L338-L350
TransferType transferType = _transferType;
if (DrawComboRow("PLAN TYPE".AsSpan(), "PlanType".AsSpan(), ref _transferType, CollectionsMarshal.AsSpan(TransferTypes)) && _transferType.GetKey() != transferType.GetKey())
{
	_transferCalculated = false;
	if (_transferBeingCalculated)
	{
		cts.Cancel();
	}
	_destinationBody = new TransferObject(-1);
	_selectedEntry = null;
	_selectedTimeUnit = ((_transferType.GetKey() == "Interstellar") ? _timeUnits[5] : _timeUnits[1]);
	SetTransferInfo();
}
```

Source: [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L338-L350)

```csharp
// current/decomp/KSA/TransferPlanner.cs L379-L387
if (DrawComboRow("SOURCE".AsSpan(), "Source".AsSpan(), ref _sourceBody, list.Slice(0, count)) && _sourceBody.GetKey() != sourceBody2.GetKey())
{
	_transferCalculated = false;
	if (_transferBeingCalculated)
	{
		cts.Cancel();
	}
	SetTransferInfo();
}
```

Source: [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L379-L387)

## Lifecycle and timing

The bridge runs in `[StarMapAfterGui]` (`frame:draw-ui`) on the frame the button is clicked, on the main thread. `OnDrawUiViewports` (where `StarMapAfterGui` hooks) runs before
`OnDrawUiThreadSafe`, which is where `TransferPlanner.DrawPlanWindow` runs when `ShowPlanWindow` is true ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2265-L2273)[^ksa-program]), so the planner draws with the new state later in the same frame. All writes happen in
one synchronous call, so stock never observes a half-written selection. (The mod's source comment says the writes happen after stock drew the window; per the decompiled frame order it is the other way round,
which does not change the outcome.) The planner's expensive solve runs on a `TransferTask` worker with `cts`, which is why the bridge cancels a running calculation exactly as stock does. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Declare the problem list first.** Static initialisers run in textual order, so the list the bindings add to must be declared before them:

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

Source: [DeltaVMap/Core/StockPlannerBridge.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Core/StockPlannerBridge.cs#L42-L55)[^ksa-deltavmap-stockplannerbridge]

2. **Bind each field with a checked helper.** `Bind<T>` requires the field to be static and exactly of type `T` before creating the `FieldRef`; otherwise it records a readable key. A field that a game
   update retypes or makes non-static is reported as missing up front instead of failing in the middle of a write sequence:

```csharp
// DeltaVMap/Core/StockPlannerBridge.cs L248-L261
private static AccessTools.FieldRef<T>? Bind<T>(string name)
{
    try
    {
        FieldInfo? field = AccessTools.DeclaredField(typeof(TransferPlanner), name);
        if (field != null && field.IsStatic && field.FieldType == typeof(T))
            return AccessTools.StaticFieldRefAccess<T>(field);
    }
    catch (Exception)
    {
    }
    Missing.Add($"TransferPlanner.{name} ({typeof(T).Name})");
    return null;
}
```

Source: [DeltaVMap/Core/StockPlannerBridge.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Core/StockPlannerBridge.cs#L248-L261)

3. **Bind the private method as a delegate.** `AccessTools.DeclaredMethod` with an explicit empty parameter list, a check on static and return type, and `CreateDelegate<Action>()`:

```csharp
// DeltaVMap/Core/StockPlannerBridge.cs L263-L276
private static Action? BindSetTransferInfo()
{
    try
    {
        MethodInfo? method = AccessTools.DeclaredMethod(typeof(TransferPlanner), "SetTransferInfo", Type.EmptyTypes);
        if (method != null && method.IsStatic && method.ReturnType == typeof(void))
            return method.CreateDelegate<Action>();
    }
    catch (Exception)
    {
    }
    Missing.Add("TransferPlanner.SetTransferInfo()");
    return null;
}
```

Source: [DeltaVMap/Core/StockPlannerBridge.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Core/StockPlannerBridge.cs#L263-L276)

4. **Replay the writes in stock's order.** The type goes first so the source change's `SetTransferInfo` sees no destination and never builds a Hohmann transfer for the plan type that is about to go. Each
   step returns early when the field already holds that key, so a second click changes nothing. Writing a `FieldRef` is `Ref() = value`:

```csharp
// DeltaVMap/Core/StockPlannerBridge.cs L160-L179
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

Source: [DeltaVMap/Core/StockPlannerBridge.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Core/StockPlannerBridge.cs#L160-L179)

5. **Copy stock's guard conditions too.** Cancel `cts` only while `_transferBeingCalculated` is true and catch `ObjectDisposedException`, because stock disposes the source after clearing the flag:

```csharp
// DeltaVMap/Core/StockPlannerBridge.cs L181-L194
// Stock cancels only while a calculation runs, and disposes the source only after it cleared
// that flag. A source that was disposed all the same has nothing left to cancel.
private static void CancelCalculation()
{
    if (!TransferBeingCalculatedRef!())
        return;
    try
    {
        CancellationRef!()?.Cancel();
    }
    catch (ObjectDisposedException)
    {
    }
}
```

Source: [DeltaVMap/Core/StockPlannerBridge.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Core/StockPlannerBridge.cs#L181-L194)

6. **Degrade safely.** `OpenInterstellar` always sets the public `TransferPlanner.ShowPlanWindow = true` first. If any key is missing it logs once which ones and only opens the window; if a write throws it sets
   `_broken` and from then on only opens the window:

```csharp
// DeltaVMap/Core/StockPlannerBridge.cs L83-L101
internal static void OpenInterstellar(Vehicle vehicle, Astronomical? departureRoot, Astronomical? destinationRoot)
{
    TransferPlanner.ShowPlanWindow = true;
    if (_broken)
        return;
    try
    {
        if (!Preselect(vehicle, departureRoot, destinationRoot))
        {
            LogHelper.WarnOnce("stock-planner-keys",
                $"[DvMap] The stock Transfer Planner opens without a preselected plan. Missing: {string.Join(", ", MissingKeys())}.");
        }
    }
    catch (Exception ex)
    {
        _broken = true;
        LogHelper.WarnOnce("stock-planner-write", $"[DvMap] Could not preselect the stock Transfer Planner, the button only opens it from now on: {ex}");
    }
}
```

Source: [DeltaVMap/Core/StockPlannerBridge.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Core/StockPlannerBridge.cs#L83-L101)

7. **Look list entries up by key, not index.** Other mods add plan types, so the `TransferType` whose `GetKey()` is `"Interstellar"` is searched in the public `TransferPlanner.TransferTypes`, and the `TimeObject`
   with `Unit == TimeUnit.Years` in the private `_timeUnits` ([DeltaVMap/Core/StockPlannerBridge.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Core/StockPlannerBridge.cs#L215-L246)).

## Complete example

| File | Role |
|---|---|
| [DeltaVMap/Core/StockPlannerBridge.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Core/StockPlannerBridge.cs) | Bindings, `Preselect`, state capture and degrade |
| [DeltaVMap/Render/InterstellarSection.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/InterstellarSection.cs)[^ksa-deltavmap-interstellarsection] | The button (hidden in the editor and without a controlled vehicle) |

## Variations and gotchas

- **Stock departs only from the vessel's own star system.** Preselecting a destination for a map rooted elsewhere would make the panel and the planner disagree, so `StockDestination` accepts a destination only
  when the vehicle's own root is the route's departure root.
- **Test it headlessly.** `StockPlannerKeysTest` fails the harness run if any key is missing, captures the planner state, runs the selection and the button path, and restores the state
  ([DeltaVMap.HarnessTests/StockPlannerKeysTest.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap.HarnessTests/StockPlannerKeysTest.cs)[^ksa-deltavmap-stockplannerkeystest]).
- **Static `FieldRef` only for static fields.** For instance fields use `FieldRefAccess<T, F>` ([Typed FieldRefAccess, boxed private enum members and by-ref lookups](call-private-game-members-through-reflection-and-delegates-3.md)).
- **Every private name is a maintenance point.** The collected `Missing` list is what turns a rename into a log line and a degraded button.

## See also

- [Plain GetField on internal registries and a generic holder for instance fields](call-private-game-members-through-reflection-and-delegates-2.md), [Typed FieldRefAccess, boxed private enum members and by-ref lookups](call-private-game-members-through-reflection-and-delegates-3.md), [AccessTools.MethodDelegate with a delegate type that mirrors in and ref modifiers](call-private-game-members-through-reflection-and-delegates-4.md), [Delegates over private editor helpers reused in a replacement drag](call-private-game-members-through-reflection-and-delegates-5.md), [UnsafeAccessor StaticField on the k__BackingField with UnsafeAccessorType](call-private-game-members-through-reflection-and-delegates-6.md), [Copied injector in a consumer mod with the same accessor](call-private-game-members-through-reflection-and-delegates-7.md)
- [Reading, writing and calling private game members](overview.md)
- [Open the stock Transfer Planner preselected on an interstellar plan](../../../../mods/ksa-deltavmap/features/stock-transfer-planner-bridge.md)
- [Validating patch targets and detecting game-update breakage](../../safety/validate-patch-targets-and-detect-game-update-breakage/overview.md)
- Related task: add-plan-type-to-the-transfer-planner

[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-deltavmap-stockplannerbridge]: KSA-DeltaVMap/DeltaVMap/Core/StockPlannerBridge.cs
[^ksa-deltavmap-interstellarsection]: KSA-DeltaVMap/DeltaVMap/Render/InterstellarSection.cs
[^ksa-deltavmap-stockplannerkeystest]: KSA-DeltaVMap/DeltaVMap.HarnessTests/StockPlannerKeysTest.cs
