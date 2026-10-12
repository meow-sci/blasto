---
type: Game Feature
title: Add plan types to the stock Transfer Planner
description: Extend the stock Transfer Planner window with a custom plan type by registering it, injecting UI and call-site changes into the plan window, opening and preselecting the planner from a mod, and letting it target bodies on hyperbolic orbits
category: flight-and-orbits
group: transfers
topic: add-plan-type-to-the-transfer-planner
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/transfer-planner-custom-plan-types
  - mods/ksa-advancedflightcomputer/features/plan-window-transpiler-injection
  - mods/ksa-deltavmap/features/stock-transfer-planner-bridge
  - mods/ksa-advancedflightcomputer/features/flyby-periapsis-targeting
  - mods/ksa-advancedflightcomputer/features/hyperbolic-transfer-targets
lifecycle: [startup:all-mods-loaded, frame:draw-ui, event:mod-unload, startup:asset-load, thread:background]
game_types:
  - KSA.TransferPlanner
  - KSA.TransferType
  - KSA.ConsoleStyle
  - KSA.ConsoleWidgets
  - KSA.OrbitalTransfers
  - KSA.Burn
  - KSA.RefineBurnTask
  - KSA.TransferTask
  - KSA.PatchedConic
  - KSA.Orbit
tags: [transfer-planner, plan-type, transpiler, reflection, harmony, console-style, hyperbolic, preselect]
---

The stock Transfer Planner window (`TRANSFER PLANNING`, the console-style window with a PLAN TYPE dropdown) is the natural home for any mod that plans maneuvers. There is no extension API, so a mod has five levers: add an entry to the public type list, skip the original draw and draw its own body under the same window identity, rewrite the original draw with a transpiler to inject controls and swap a call,
drive the planner's private state by reflection to open and preselect it, and patch the assumptions that break for bodies the planner never offered.

## Engine background

- **The type list.** `TransferPlanner.TransferTypes` is a public static `List<TransferType>` (`Hohmann`, `Circularize Apoapsis`, `Circularize Periapsis`, `Interstellar`); `TransferType` is a struct `(string key, string name)` with `GetKey()`. The PLAN TYPE combo iterates the list and, on a change of key, clears `_transferCalculated`, the destination and the selected porkchop entry, picks the time unit, cancels a running calculation and calls the private `SetTransferInfo()`.[^ksa-transferplanner]
  [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L53-L67) [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L332-L349) [^ksa-transfertype]
- **The window.** `TransferPlanner.DrawPlanWindow(IGameViewport)` is called from `Program.OnDrawUiThreadSafe` (main thread, `frame:draw-ui`, only with the HUD drawn). It opens `ConsoleStyle.BeginWindow("transfer-planning", "TRANSFER PLANNING", "KSA-TRJ", ref open, size, flags)`, then `BeginBody`, `PushWidgetStyle`, region headers and rows; `ShowPlanWindow` is a public static property whose setter also clears stock's transfer state when set to false.[current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L225-L260) [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L305-L337)
- **Create.** Stock's CREATE path calls `Burn.Create` and adds an `InputEvents.BurnUpdateData` (see [Create, chain and read stock maneuver nodes from code](../../autopilot/create-and-read-maneuver-nodes/overview.md)).
- **Background work.** The porkchop and refine tasks (`TransferTask`, `RefineBurnTask`) run on `ThreadPool` threads and publish into `TransferInfo`; patched methods they call run off the main thread.

See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Skip-original prefix redrawing the window](add-plan-type-to-the-transfer-planner-1.md) | KSA-AdvancedFlightComputer | harmony prefix | `startup:all-mods-loaded`, `frame:draw-ui` | Full control of the body for your own types; must replay stock's resets |
| [Transpiler with validated IL anchors](add-plan-type-to-the-transfer-planner-2.md) | KSA-AdvancedFlightComputer | harmony transpiler | `startup:all-mods-loaded`, `frame:draw-ui` | Inject into stock types without replacing them; fragile against IL changes |
| [AccessTools FieldRef and delegates bound once](add-plan-type-to-the-transfer-planner-3.md) | KSA-DeltaVMap | reflection | `frame:draw-ui` | Open and preselect with no patches; breaks quietly on rename |
| [Hand a retargeted transfer to the stock Create path](add-plan-type-to-the-transfer-planner-4.md) | KSA-AdvancedFlightComputer | harmony transpiler | `frame:draw-ui` | Stock's own button creates your burn; needs a same-signature swap and a click gate |
| [XML patch plus NaN-period patches and a finalizer](add-plan-type-to-the-transfer-planner-5.md) | KSA-AdvancedFlightComputer | harmony finalizer | `startup:asset-load`, `frame:draw-ui`, `thread:background` | Targets hyperbolic bodies; several patched methods run on pool threads |

## See also

- [Aim a transfer at a flyby periapsis and predict its encounter](../aim-a-transfer-at-a-flyby-periapsis/overview.md)
- [Compute phase angles, synodic periods and the time to the next transfer window](../compute-transfer-windows/overview.md)
- [Create, chain and read stock maneuver nodes from code](../../autopilot/create-and-read-maneuver-nodes/overview.md)
- [Plan and total multi-leg and interstellar delta-v routes](../plan-multi-leg-and-interstellar-delta-v-routes/overview.md)
- Related topics: [Inject UI into the game's own windows and menus, including the orbit right-click menu](../../../ui/menus/inject-ui-into-a-stock-window/overview.md)

[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
[^ksa-transfertype]: KSA.TransferType (decompiled, build 2026.10.10.5554)
