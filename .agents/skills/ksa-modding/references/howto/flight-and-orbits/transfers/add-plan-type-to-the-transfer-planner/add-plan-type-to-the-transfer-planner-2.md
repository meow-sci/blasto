---
type: How-To
title: Inject UI and swap a call in DrawPlanWindow with a transpiler that validates its IL anchors
description: Rewrite the stock plan window in anchored passes (insert a call before the transfer controls, a fallback before the style pop, swap Burn.Create and gate the CREATE click), moving labels and exception blocks correctly and failing soft when anchors are missing
category: flight-and-orbits
group: transfers
topic: add-plan-type-to-the-transfer-planner
approach: 2
difficulty: advanced
prerequisites: [harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/plan-window-transpiler-injection]
techniques: [harmony-transpiler]
hooks: [harmony-transpiler KSA.TransferPlanner.DrawPlanWindow]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.TransferPlanner, KSA.Burn, KSA.ConsoleStyle, KSA.ConsoleWidgets]
tags: [transpiler, il, anchors, labels, exception-blocks, plan-window, injection]
---

Outcome: flyby and multi-pass controls appear inside the stock Transfer Planning window next to stock's own transfer controls, and the stock CREATE button creates the mod's burn, without replacing the window. KSA-AdvancedFlightComputer rewrites `TransferPlanner.DrawPlanWindow` in three anchored steps.[Inject UI and swap calls inside the stock plan window with anchored transpilers](../../../../mods/ksa-advancedflightcomputer/features/plan-window-transpiler-injection.md)
Prerequisite: Harmony.

## Background: how the game does it

`DrawPlanWindow` is a long method that calls the private `DrawCorrectionTransfer()` for the correction controls, `ConsoleStyle.PopWidgetStyle()` every frame the body is drawn, `ConsoleWidgets.PrimaryButton` for CREATE, and `Burn.Create(OrbitPointCce, double, double3, PatchedConic, Vehicle)` for the burn; stock then queues the returned burn.[^ksa-transferplanner] [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L305-L337) [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L1834)
[current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L770-L786) Inserting before an anchor that is a branch target lets the jump skip the new call, and inserting at the first instruction of a `try` leaves the call outside the exception region, so labels and begin markers must move with it.

## Lifecycle and timing

The transpiler runs once when the patch block is applied at `[StarMapAllModsLoaded]` (and again whenever Harmony re-patches the method). The injected calls run where stock's code runs: inside `DrawPlanWindow`, from `Program.OnDrawUiThreadSafe` on the main thread (`frame:draw-ui`), inside the open console window with the widget style pushed.
The swapped create runs exactly when stock would create its burn, and stock then queues the returned burn into `InputEvents.BurnUpdateBuffer` as usual. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Chain passes over the instruction list** in one pipeline:
   - insert `call HohmannMultiPassUI.DrawInline()` before the first `call TransferPlanner.DrawCorrectionTransfer()` (resolved with `AccessTools.Method` since it is private), so the section renders above stock's correction controls;
   - insert the same call before the first `call ConsoleStyle.PopWidgetStyle()` as a fallback; the injected method remembers `ImGui.GetFrameCount()` and returns on a second call in the same frame, so the first injection wins;
   - swap the `Burn.Create` call operand for a static method with the identical signature (the arguments on the stack stay valid), carrying labels and blocks over, and insert a click-gate call right after the nearest `ConsoleWidgets.PrimaryButton` (within 160 instructions) that filters the button's `bool` result:

```csharp
// AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs L152-L167
int burnCreateIndex = code.FindIndex(instruction => instruction.Calls(burnCreate));
int createButtonIndex = FindCreateButton(
    code, burnCreateIndex, clickGate, out int gapTooLarge);

bool burnCreateReplaced = burnCreateIndex >= 0;
if (burnCreateReplaced)
{
    var swap = new CodeInstruction(OpCodes.Call, replacement);
    swap.labels.AddRange(code[burnCreateIndex].labels);
    swap.blocks.AddRange(code[burnCreateIndex].blocks);
    code[burnCreateIndex] = swap;
}

bool gateInjected = createButtonIndex >= 0 && clickGate != null;
if (gateInjected)
    code.Insert(createButtonIndex + 1, new CodeInstruction(OpCodes.Call, clickGate));
```

2. **Move entry markers.** Move the anchor's labels and its begin-exception markers to the inserted instruction while keeping end markers on the anchor, so the inserted call stays inside the same region:

```csharp
// AdvancedFlightComputer/Core/TranspilerInsertion.cs L5-L20
internal static class TranspilerInsertion
{
    internal static void MoveEntryMarkers(CodeInstruction anchor, CodeInstruction first)
    {
        first.labels.AddRange(anchor.labels);
        anchor.labels.Clear();

        // Begin markers apply before an instruction, while end markers apply after it. Keep end markers on the anchor so the inserted call stays inside its exception region.
        foreach (ExceptionBlock block in anchor.blocks)
        {
            if (block.blockType != ExceptionBlockType.EndExceptionBlock)
                first.blocks.Add(block);
        }
        anchor.blocks.RemoveAll(block => block.blockType != ExceptionBlockType.EndExceptionBlock);
    }
}
```

   (The mod's settings page does the opposite on purpose: leaving labels on the anchor so a jump to it skips the sections.)
3. **Fail soft.** Each pass returns the list unchanged and warns once when its anchor is missing; presence probes (`HasCalculatedControlsAnchor`, `HasCreateAnchor`, `HasFallbackControlsAnchor`) decide at load whether the UI feature is enabled at all. Warn if stock moves `PopWidgetStyle` before `DrawCorrectionTransfer`.
4. **Log success once** (`DebugOnce`), because Harmony re-runs transpilers whenever another patch is added to or removed from the method.
5. **Test headlessly:** run the transpiler over `PatchProcessor.GetOriginalInstructions(DrawPlanWindow)` and assert both insertions, the swap and the gate positions ([tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/ManeuverTranspilerTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/ManeuverTranspilerTest.cs)).

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs) | the three passes and anchor probes |
| [AdvancedFlightComputer/Core/TranspilerInsertion.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/TranspilerInsertion.cs) | label and exception-marker hand-over |
| [AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs) | same-signature `Burn.Create` replacement and click gate |
| [AdvancedFlightComputer/Features/MultiPass/HohmannMultiPassUI.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/HohmannMultiPassUI.cs) | `DrawInline` with its once-per-frame guard |

## Variations and gotchas

- **The injected call must be stack-neutral** at the insertion point.
- **Two anchors** make the feature survive a stock reorder; the once-per-frame guard stops a double draw.
- **Another mod's transpiler** on the same method is composed by Harmony; keep your passes independent of instruction indices.
- Prefer a skip-original prefix ([approach 1](add-plan-type-to-the-transfer-planner-1.md)) when you replace the whole body; use a transpiler when you extend stock's types.

## See also

- [Overview](overview.md); [skip-original prefix](add-plan-type-to-the-transfer-planner-1.md); [create hand-off](add-plan-type-to-the-transfer-planner-4.md)
- [Inject UI and swap calls inside the stock plan window with anchored transpilers](../../../../mods/ksa-advancedflightcomputer/features/plan-window-transpiler-injection.md)
- Related topics: [Inject UI into the game's own windows and menus, including the orbit right-click menu](../../../ui/menus/inject-ui-into-a-stock-window/overview.md)

[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
