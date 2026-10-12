---
type: How-To
title: Move labels and begin-exception markers onto the inserted instruction
description: Insert a call before an anchor so jumps to the anchor reach it and it stays inside the same try region, by moving labels and begin-exception markers, plus a click gate that filters a stack bool
category: patching
group: transpilers
topic: insert-calls-into-a-game-method-at-an-il-anchor
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/plan-window-transpiler-injection]
techniques: [harmony-transpiler, il-labels, il-exception-blocks]
hooks: [harmony-transpiler KSA.TransferPlanner.DrawPlanWindow]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.TransferPlanner]
tags: [harmony, transpiler, il, insert, labels, exception-blocks, branch-target]
---

A CodeInstruction carries more than an opcode and an operand: `labels` are jump targets that land on the instruction, and `blocks` are exception-region markers. When you insert a call
*before* an anchor, you must decide who owns those. Insert naively and a jump to the anchor lands after your call (your call is skipped on that path), and if the anchor is the first
instruction of a `try`, your call lands outside the try. KSA-AdvancedFlightComputer moves the anchor's labels and begin-exception markers onto the inserted instruction and keeps end markers
on the anchor, so the inserted call is reached by every path that reached the anchor and stays inside the same region. This article demonstrates the effect and shows the helper, plus
a related insertion that filters the bool a button call leaves on the stack.

Prerequisites: a StarMap mod with `0Harmony` and the recipe from [Retarget the operand of one call (same signature)](../write-a-harmony-transpiler/write-a-harmony-transpiler-1.md). Demonstrated by AFC in
[Inject UI and swap calls inside the stock plan window with anchored transpilers](../../../../mods/ksa-advancedflightcomputer/features/plan-window-transpiler-injection.md).

## Background: what the IL looks like

In `TransferPlanner.DrawPlanWindow` (build 2026.10.10.5554), the `PopWidgetStyle` call is a branch target: when no transfer is calculated, `brfalse IL_1436` jumps straight to it.
`ConsoleStyle.PopWidgetStyle()` is the anchor of AFC's fallback insertion:

```text
// KSA.dll IL via ilspycmd -il: KSA.TransferPlanner::DrawPlanWindow (parameter lists shortened)
IL_0e0f: ldloc.s 104
IL_0e11: brfalse IL_1436
```

Source: [current/dll/KSA.dll](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/dll/KSA.dll)[^ksa-dll]

```text
// KSA.dll IL via ilspycmd -il: KSA.TransferPlanner::DrawPlanWindow (parameter lists shortened)
IL_1435: nop

IL_1436: call void KSA.ConsoleStyle::PopWidgetStyle(...)
IL_143b: nop
```

Source: [current/dll/KSA.dll](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/dll/KSA.dll)

The method has no exception blocks, but the helper is general, and the effect is easy to reproduce. A local test against Lib.Harmony 2.4.2 patched a method whose second statement is a call to
`Anchor()` that a `brfalse` can jump to, inserting a call to `Inserted()` before the anchor:

```text
// observed output of a local test against Lib.Harmony 2.4.2 (not part of any mod repository)
labels left on the anchor, Plain(false):
    Anchor()
labels moved to the inserted call, Moved(false):
    Inserted()
    Anchor()
```

and a second method whose anchor is the first instruction of a `try`/`finally`, with an inserted call that throws:

```text
// observed output of a local test against Lib.Harmony 2.4.2 (not part of any mod repository)
exception-block marker left on the anchor, InTry(), inserted call throws:
    Inserted()
    caller saw: inserted call threw
exception-block marker moved to the inserted call, InTry(), inserted call throws:
    Inserted()
    finally
    caller saw: inserted call threw
```

In the first case the begin-try marker stayed on the anchor, so the inserted call ran before the try began and the `finally` did not run. In the second the marker moved, the call is inside the
try, and the `finally` ran. (The original IL carried `BeginExceptionBlock` on the anchor call itself.)

## Lifecycle and timing

The transpiler runs once when the PlanWindow block is patched at `[StarMapAllModsLoaded]` (`startup:all-mods-loaded`) and again whenever Harmony re-patches the method. The inserted calls run inside
`DrawPlanWindow` from `Program.OnDrawUiThreadSafe` on the main thread (`frame:draw-ui`), inside the open console window with the widget style pushed. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Write the helper that hands entry markers to the new instruction.** Labels move wholesale. Begin and middle exception markers move; end markers stay on the anchor because they apply *after* an
   instruction, so the inserted call remains inside the region:

```csharp
// AdvancedFlightComputer/Core/TranspilerInsertion.cs L7-L19
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
```

Source: [AdvancedFlightComputer/Core/TranspilerInsertion.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/TranspilerInsertion.cs#L7-L19)[^ksa-advancedflightcomputer-transpilerinsertion]

2. **Use it at every insertion.** Create the instruction, move the markers, insert at the anchor's index:

```csharp
// AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs L71-L73
var injected = new CodeInstruction(OpCodes.Call, injectTarget);
TranspilerInsertion.MoveEntryMarkers(code[index], injected);
code.Insert(index, injected);
```

Source: [AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs#L71-L73)[^ksa-advancedflightcomputer-planwindowpatchpipeline]

   The alternative `CodeInstruction.MoveLabelsFrom(anchor)` and `MoveBlocksFrom(anchor)` (used by other mods) move all labels and all blocks, which differs for end markers.

3. **Filter a value that is already on the stack.** After the `PrimaryButton` call the button's bool result is on the stack; inserting `call ShouldAllowCreateClick(bool) -> bool` right after it
   filters the click without touching anything else (the call is stack-neutral: `bool` in, `bool` out):

```csharp
// AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs L165-L169
bool gateInjected = createButtonIndex >= 0 && clickGate != null;
if (gateInjected)
    code.Insert(createButtonIndex + 1, new CodeInstruction(OpCodes.Call, clickGate));

ReportCreateInterception(burnCreateReplaced, gateInjected, gapTooLarge);
```

Source: [AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs#L165-L169)

   The gate returns `false` when a multi-pass execution is already active, so a second click does not queue a duplicate:

```csharp
// AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs L17-L39
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

Source: [AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs#L17-L39)[^ksa-advancedflightcomputer-hohmanncreateinterceptor]

4. **Guard once-per-frame fallbacks.** The fallback insertion before `PopWidgetStyle` is also reached on frames where the first anchor was reached; `DrawInline` remembers
   `ImGui.GetFrameCount()` and returns on a second call in the same frame.

5. **Test with the original IL.** `PatchProcessor.GetOriginalInstructions` plus the transpiler; assert the inserted call's index relative to the anchor and, for exception regions, the
   markers ([Insert a call before a validated anchor](../write-a-harmony-transpiler/write-a-harmony-transpiler-2.md)).

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Core/TranspilerInsertion.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/TranspilerInsertion.cs) | `MoveEntryMarkers` |
| [AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs#L43-L129) | Two insertion passes using it |
| [AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs#L17-L39) | The click gate |

## Variations and gotchas

- **Deliberately leave labels on the anchor** when a jump should skip your addition ([Leave labels on the anchor so a jump skips the addition](insert-calls-into-a-game-method-at-an-il-anchor-2.md)).
- **Inserting at the first instruction of a `try` without moving the begin marker puts your call outside the region** (shown above).
- **Insert at descending indices** when inserting several; earlier anchors do not move ([Anchor on a UTF-8 label literal in the method](../identify-a-call-by-its-argument-il/identify-a-call-by-its-argument-il-3.md)).
- **A call you insert must be stack-neutral** (or consume and produce exactly what you intend), or the method becomes invalid.

## See also

- [Leave labels on the anchor so a jump skips the addition](insert-calls-into-a-game-method-at-an-il-anchor-2.md), [Snapshot state before a stock call, restore it in a paired prefix](insert-calls-into-a-game-method-at-an-il-anchor-3.md), [Insert before the first and after the last instance of a pass boundary](insert-calls-into-a-game-method-at-an-il-anchor-4.md)
- [Inserting calls into a game method at an IL anchor](overview.md)
- [Inject UI and swap calls inside the stock plan window with anchored transpilers](../../../../mods/ksa-advancedflightcomputer/features/plan-window-transpiler-injection.md)
- [Insert a call before a validated anchor](../write-a-harmony-transpiler/write-a-harmony-transpiler-2.md)
- Related topic: inject-ui-into-a-stock-window

[^ksa-dll]: KSA.dll (build 2026.10.10.5554), IL read with ilspycmd
[^ksa-advancedflightcomputer-transpilerinsertion]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Core/TranspilerInsertion.cs
[^ksa-advancedflightcomputer-planwindowpatchpipeline]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs
[^ksa-advancedflightcomputer-hohmanncreateinterceptor]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs
