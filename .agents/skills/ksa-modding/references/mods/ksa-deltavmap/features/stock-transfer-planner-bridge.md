---
type: Mod Feature
title: Open the stock Transfer Planner preselected on an interstellar plan
description: Binds the private static fields and SetTransferInfo of KSA.TransferPlanner once with AccessTools FieldRef and delegates, then replays the exact writes the stock PLAN TYPE, SOURCE and DESTINATION combos make, degrading to just opening the window when a key is missing
mod: ksa-deltavmap
category: patching
techniques: [reflection, accesstools-fieldref]
hooks:
  - starmap StarMapAfterGui
  - reflection KSA.TransferPlanner._transferType
  - reflection KSA.TransferPlanner._sourceBody
  - reflection KSA.TransferPlanner._destinationBody
  - reflection KSA.TransferPlanner._selectedEntry
  - reflection KSA.TransferPlanner._selectedTimeUnit
  - reflection KSA.TransferPlanner._timeUnits
  - reflection KSA.TransferPlanner._transferCalculated
  - reflection KSA.TransferPlanner._transferBeingCalculated
  - reflection KSA.TransferPlanner._interstellarDepartureFailed
  - reflection KSA.TransferPlanner.cts
  - reflection KSA.TransferPlanner.SetTransferInfo
lifecycle: [frame:draw-ui]
game_types:
  - KSA.TransferPlanner
  - KSA.TransferType
  - KSA.TransferObject
  - KSA.TimeObject
  - KSA.OrbitalTransfers
  - KSA.Universe
  - KSA.IIndependentRoot
  - KSA.Vehicle
tags: [patching, reflection, accesstools, fieldref, transfer-planner, interstellar, interop-with-stock-ui]
---

The interstellar route panel has an **OPEN STOCK TRANSFER PLANNER** button. It opens the game's
own Transfer Planner already set to the Interstellar plan, with the player's vessel as source and
the route's destination star system selected, so the player can hand the trip to the stock
planner and its burn tooling. Mechanism: no Harmony patch at all. `StockPlannerBridge` binds the
planner's private static fields with `AccessTools.StaticFieldRefAccess<T>` and its private
`SetTransferInfo()` as an `Action` delegate once, at class initialization, and writes them in the
same order and with the same side effects as one stock frame of combo changes.[^dvmap-bridge]

(The mod's `Patches/` folder contains only the menu-bar postfix; this feature is pure reflection.)

## How it works

**Binding.** `Bind<T>(name)` looks up `AccessTools.DeclaredField(typeof(TransferPlanner), name)`,
requires it to be static and of exactly type `T`, and returns
`AccessTools.StaticFieldRefAccess<T>(field)`, a `ref T` getter with no per-call reflection. A
failed lookup appends a readable key such as `"TransferPlanner._sourceBody (TransferObject)"` to a
`Missing` list declared before the bindings (static initializers run in order). `SetTransferInfo`
is bound with `AccessTools.DeclaredMethod(..., Type.EmptyTypes)` and
`method.CreateDelegate<Action>()`. Two list entries are also looked up by value at call time: the
`TransferType` whose `GetKey()` is `"Interstellar"` in the public `TransferPlanner.TransferTypes`
(searched by key, not index, because other mods add plan types) and the `TimeObject` with
`Unit == TimeUnit.Years` in the private `_timeUnits`.

All of these exist in build 2026.10.10.5554: `_sourceBody`, `_destinationBody`, `_transferType`
(`TransferObject` / `TransferType`), `_selectedEntry` (`OrbitalTransfers.PorkChopEntry?`),
`_selectedTimeUnit`, `_timeUnits`, `_transferCalculated`, `_transferBeingCalculated`,
`_interstellarDepartureFailed`, `cts` (`CancellationTokenSource`) and `private static void
SetTransferInfo()`.[^ksa-transferplanner]

**Replaying stock.** `Preselect(vehicle, departureRoot, destinationRoot)` runs three steps, each
a copy of the code stock runs when the matching combo changes in `TransferPlanner.DrawPlanWindow`
/ `DrawInterstellarPlan`:

| Step | Stock equivalent | Writes |
|---|---|---|
| `SelectType` | PLAN TYPE combo change | `_transferType`, `_transferCalculated = false`, cancel `cts` if calculating, `_destinationBody = new TransferObject(-1)`, `_selectedEntry = null`, `_selectedTimeUnit = Years`, `SetTransferInfo()` |
| `SelectSource` | SOURCE combo change | `_sourceBody = new TransferObject(vehicle)`, `_transferCalculated = false`, cancel, `SetTransferInfo()` |
| `SelectDestination` | DESTINATION combo change in `DrawInterstellarPlan` | `_destinationBody = new TransferObject(root)`, `_transferCalculated = false`, `_interstellarDepartureFailed = false` |

Each step returns early when the field already holds that key, so a second click changes nothing.
The type goes first so that the source change's `SetTransferInfo` sees no destination and never
builds a Hohmann transfer for the plan type that is about to go. With an interstellar destination
`SetTransferInfo` only clears the transfer info, because a system root (`FixedStar` /
`Barycenter`) is not an `IOrbiter` and the planner's `Destination` property
(`_destinationBody.Body as IOrbiter`) is null.

**Which destination stock will accept.** `StockDestination` returns the destination root only if
the vehicle's own root (`Universe.NearestRoot(vehicle.GetPositionEcl())`) is the route's departure
root and the destination is a different entry of `Universe.Roots`. That mirrors
`TransferPlanner.PopulateWithStars`, which lists every root except the one nearest the source,
so the panel never shows one route while stock plans another. Otherwise only the plan type is set
and the hint says "Pick the destination there".

**Degrading safely.** `OpenInterstellar` always sets the public `TransferPlanner.ShowPlanWindow =
true` first. If any key is missing it logs once which ones and only opens the window; if a write
throws it sets `_broken` and from then on only opens the window. `IsAvailable` is cheap enough to
drive the hint text every frame. The button itself lives in
`InterstellarSection.DrawStockPlannerButton` (`ConsoleWidgets.Button`), hidden in the editor and
without a controlled vehicle.[^dvmap-interstellarsection]

**Tested headlessly.** `StockPlannerKeysTest` fails the harness run if any key is missing, then
captures the planner state (`Capture()` into a `PlannerState` record struct), runs the selection
and the full button path, checks the result and `Restore`s the original state.[^dvmap-plannerkeystest]

## Lifecycle and timing

- Hook: `[StarMapAfterGui]` (postfix on `Program.OnDrawUiViewports`), main thread,
  `frame:draw-ui`, on the frame the button is clicked inside the map window.
- The planner's state is plain static fields that stock reads and writes only from the main
  thread UI draw; its expensive solve runs on a `TransferTask` worker with `cts`, which is why
  the bridge cancels a running calculation exactly as stock does (only while
  `_transferBeingCalculated`, tolerating an already disposed source).
- Frame order in build 2026.10.10.5554: AfterGui fires before `Program.OnDrawUiThreadSafe`, which
  is where `TransferPlanner.DrawPlanWindow` runs when `ShowPlanWindow` is true.[^ksa-program] So
  the planner draws with the new state later in the same frame; because all writes happen in one
  synchronous call, stock never observes a half-written selection. (The mod's comment says the
  writes happen after stock drew the window for the frame; per the decompiled frame order it is
  the other way round, which does not change the outcome.)
- When the window appears, stock itself resets `_sourceBody` to the controlled vehicle on
  `ImGui.IsWindowAppearing()` and calls `SetTransferInfo` again; the bridge's writes agree with
  that.

## Key code

| Path | Role |
|---|---|
| [DeltaVMap/Core/StockPlannerBridge.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Core/StockPlannerBridge.cs) | Bindings, missing-key report, preselect steps, capture/restore |
| [DeltaVMap/Render/InterstellarSection.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/InterstellarSection.cs#L459-L481) | The button and its hint text |
| [DeltaVMap.HarnessTests/StockPlannerKeysTest.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap.HarnessTests/StockPlannerKeysTest.cs) | Headless check that every key resolves and the writes round-trip |

## Snippets

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

```csharp
// DeltaVMap/Core/StockPlannerBridge.cs L248-L276
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

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `TransferPlanner.ShowPlanWindow` (public static property) | [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs) | Opens the planner window |
| `TransferPlanner.TransferTypes` (public static list) | same | Find the "Interstellar" plan type by key |
| Private statics `_transferType`, `_sourceBody`, `_destinationBody`, `_selectedEntry`, `_selectedTimeUnit`, `_timeUnits`, `_transferCalculated`, `_transferBeingCalculated`, `_interstellarDepartureFailed`, `cts`; private `SetTransferInfo()` | same | The planner's selection state, written as stock writes it |
| `TransferPlanner.PopulateWithStars` | same | Not called; the destination rule `StockDestination` mirrors |
| `TransferObject(Astronomical)`, `TransferObject(int)`, `GetKey()` | [current/decomp/KSA/TransferObject.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferObject.cs) | Combo entries; `new TransferObject(-1)` is the "N/A" entry |
| `Universe.NearestRoot`, `Universe.Roots` | (KSA.Universe) | Which roots stock offers as destinations |

## Pitfalls and version notes

- Every private name here is a maintenance point. The mod collects the missing ones into a
  readable list, logs it once, keeps the button working as "open only", and has a harness test
  that fails on a game update that renames or retypes any of them.
- Check `field.IsStatic` and `field.FieldType == typeof(T)` before creating a `FieldRef`, so a
  field that a game update retypes or makes non-static is reported as missing up front instead
  of failing somewhere in the middle of a write sequence.
- Do not assume list indices in `TransferTypes` (other mods add plan types) or `_timeUnits`; look
  entries up by key or unit.
- Cancel `cts` only while `_transferBeingCalculated` is true and catch
  `ObjectDisposedException`, because stock disposes the source after clearing the flag.
- Stock departs only from the vessel's own star system; preselecting a destination for a map
  rooted elsewhere would make the panel and the planner disagree.

## See also

- [Interstellar routes and fuel](./interstellar-routes-and-fuel.md)
- [Headless harness tests](./headless-harness-tests.md)
- [Map-mode transfer overlay](./map-mode-transfer-overlay.md)
- [Overview](../overview.md)
- [Open and preselect the stock Transfer Planner with AccessTools FieldRefs and delegates bound once](../../../howto/flight-and-orbits/transfers/add-plan-type-to-the-transfer-planner/add-plan-type-to-the-transfer-planner-3.md) - how-to article demonstrating this feature
- [AccessTools static field refs bound once, replay stock's writes](../../../howto/patching/internals/call-private-game-members-through-reflection-and-delegates/call-private-game-members-through-reflection-and-delegates-1.md) - how-to article demonstrating this feature

[^dvmap-bridge]: DeltaVMap/Core/StockPlannerBridge.cs
[^dvmap-interstellarsection]: DeltaVMap/Render/InterstellarSection.cs
[^dvmap-plannerkeystest]: DeltaVMap.HarnessTests/StockPlannerKeysTest.cs
[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
