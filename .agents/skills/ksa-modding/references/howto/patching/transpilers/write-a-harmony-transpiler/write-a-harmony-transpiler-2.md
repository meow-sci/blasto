---
type: How-To
title: Insert a call before a validated anchor
description: Add a draw call into the stock Transfer Planning window by inserting it before a validated anchor call in a pipeline of small passes, each returning the list unchanged and warning once when its anchor is missing, and unit test the rewrite over the original IL
category: patching
group: transpilers
topic: write-a-harmony-transpiler
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/plan-window-transpiler-injection]
techniques: [harmony-transpiler, il-anchor, headless-test]
hooks: [starmap StarMapAllModsLoaded, harmony-transpiler KSA.TransferPlanner.DrawPlanWindow]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.TransferPlanner, KSA.ConsoleStyle, KSA.Burn]
tags: [harmony, transpiler, il, insert, anchor, pipeline, unit-test]
---

KSA-AdvancedFlightComputer renders its flyby and multi-pass controls inside the stock Transfer Planning window, without replacing the
window. A transpiler on `TransferPlanner.DrawPlanWindow` finds a validated anchor call in the IL and inserts a call to the mod's own draw
method before it. This article shows the second common shape in this topic: insertion at an anchor, built as a pipeline of small passes
that each fail soft, and tested over the original IL without launching the game.

Prerequisites: a StarMap mod with `0Harmony` and the vocabulary from [Retarget the operand of one call (same signature)](write-a-harmony-transpiler-1.md). Demonstrated by AFC in
[Inject UI and swap calls inside the stock plan window with anchored transpilers](../../../../mods/ksa-advancedflightcomputer/features/plan-window-transpiler-injection.md).

## Background: how the game does it

`DrawPlanWindow` draws the console-style window body. The block that holds the transfer controls, including the call to
`DrawCorrectionTransfer()`, runs only when `_transferCalculated && _transferInfo != null`; otherwise control jumps straight to
`ConsoleStyle.PopWidgetStyle()` at the end of the body, followed by the footer and the CREATE button:

```csharp
// current/decomp/KSA/TransferPlanner.cs L616-L626
IL_0df8:
if (_transferCalculated && _transferInfo != null)
{
	if (_transferBeingRefined && (_refinementWorker == null || _refinementWorker.IsResultsReady))
	{
		_transferBeingRefined = false;
	}
	flag = true;
	DrawCorrectionTransfer();
	if (_transferInfo.BestDvTransferIndex.X != -1)
	{
```

Source: [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L616-L626)[^ksa-transferplanner]

```csharp
// current/decomp/KSA/TransferPlanner.cs L730-L733
IL_1436:
ConsoleStyle.PopWidgetStyle();
ConsoleStyle.EndBody();
ConsoleStyle.BeginFooter();
```

Source: [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L730-L733)

The matching IL in build 2026.10.10.5554. `DrawCorrectionTransfer` (IL_0e46) is reached only through the `if`; the `brfalse` at IL_0e11 jumps
to IL_1436, the `PopWidgetStyle` call, so that instruction is a **branch target** and carries a label (the blank line before it in the
listing). The method has no `.try` blocks:

```text
// KSA.dll IL via ilspycmd -il: KSA.TransferPlanner::DrawPlanWindow (parameter lists shortened)
IL_0e0f: ldloc.s 104
IL_0e11: brfalse IL_1436
```

Source: [current/dll/KSA.dll](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/dll/KSA.dll)[^ksa-dll]

```text
// KSA.dll IL via ilspycmd -il: KSA.TransferPlanner::DrawPlanWindow (parameter lists shortened)
IL_0e44: ldc.i4.1
IL_0e45: stloc.3
IL_0e46: call void KSA.TransferPlanner::DrawCorrectionTransfer(...)
IL_0e4b: nop
```

Source: [current/dll/KSA.dll](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/dll/KSA.dll)

```text
// KSA.dll IL via ilspycmd -il: KSA.TransferPlanner::DrawPlanWindow (parameter lists shortened)
IL_1435: nop

IL_1436: call void KSA.ConsoleStyle::PopWidgetStyle(...)
IL_143b: nop
```

Source: [current/dll/KSA.dll](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/dll/KSA.dll)

That is why AFC has two anchors: when no transfer is calculated the first anchor is never reached, and the fallback at the branch-target
`PopWidgetStyle` is.

## Lifecycle and timing

The transpiler runs once when the PlanWindow block is patched at `[StarMapAllModsLoaded]` (`startup:all-mods-loaded`), and again whenever
Harmony re-patches the method. The injected call runs where stock's code runs: inside `DrawPlanWindow`, from `Program.OnDrawUiThreadSafe` on
the main thread (`frame:draw-ui`), inside the open console window with the widget style pushed. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Resolve the anchor and the injected method.** The anchor `TransferPlanner.DrawCorrectionTransfer` is private, resolved with
   `AccessTools.Method` and an empty parameter list; the inserted target is the mod's `HohmannMultiPassUI.DrawInline`. Presence probes
   (`HasCalculatedControlsAnchor` and friends) let the mod disable the UI at load when an anchor is gone:

```csharp
// AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs L20-L41
/// <summary>Whether stock still has the anchor beside the calculated transfer controls.</summary>
internal static bool HasCalculatedControlsAnchor => CalculatedControlsAnchor() != null;

/// <summary>Whether stock still has the fallback anchor that runs on every frame.</summary>
internal static bool HasFallbackControlsAnchor => FallbackControlsAnchor() != null;

private static MethodInfo? CalculatedControlsAnchor()
    => AccessTools.Method(typeof(TransferPlanner), "DrawCorrectionTransfer", Type.EmptyTypes);

private static MethodInfo? FallbackControlsAnchor()
    => AccessTools.Method(typeof(ConsoleStyle), nameof(ConsoleStyle.PopWidgetStyle), Type.EmptyTypes);

internal static bool HasCreateAnchor =>
    AccessTools.Method(typeof(Burn), nameof(Burn.Create), BurnCreateSignature) != null;

internal static IEnumerable<CodeInstruction> Rewrite(
    IEnumerable<CodeInstruction> instructions)
{
    IEnumerable<CodeInstruction> rewritten = InjectCalculatedControls(instructions);
    rewritten = InjectFallbackControls(rewritten);
    return InterceptCreate(rewritten);
}
```

Source: [AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs#L20-L41)[^ksa-advancedflightcomputer-planwindowpatchpipeline]

2. **Write each pass to return the list unchanged on a miss.** A pass copies the instructions, checks the anchor and the inject target, warns once
   (through a once-only logger because Harmony re-runs transpilers) and returns the list untouched:

```csharp
// AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs L43-L69
internal static IEnumerable<CodeInstruction> InjectCalculatedControls(
    IEnumerable<CodeInstruction> instructions)
{
    MethodInfo? anchor = CalculatedControlsAnchor();
    MethodInfo? injectTarget = AccessTools.Method(
        typeof(HohmannMultiPassUI), nameof(HohmannMultiPassUI.DrawInline));

    var code = new List<CodeInstruction>(instructions);
    if (anchor == null || injectTarget == null)
    {
        LogHelper.WarnOnce("transpiler-hohmann-multipass-missing",
            "[AFC] HohmannMultiPass transpiler: anchor or inject target missing " +
            "(anchor=" + (anchor != null ? "ok" : "MISSING") + ", " +
            "inject=" + (injectTarget != null ? "ok" : "MISSING") +
            "); leaving this DrawPlanWindow stage unmodified.");
        return code;
    }

    int index = code.FindIndex(instruction => instruction.Calls(anchor));
    if (index < 0)
    {
        LogHelper.WarnOnce("transpiler-hohmann-multipass-no-call",
            $"[AFC] HohmannMultiPass transpiler: no call to DrawCorrectionTransfer " +
            $"found in DrawPlanWindow IL ({code.Count} IL instructions scanned); " +
            "multi-pass section not injected.");
        return code;
    }
```

Source: [AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs#L43-L69)

3. **Insert, moving the entry markers.** `new CodeInstruction(OpCodes.Call, injectTarget)` is inserted at the anchor's index, and
   `MoveEntryMarkers` hands the anchor's labels and begin-exception markers to the new instruction so a jump to the anchor lands on the
   inserted call and the call stays inside the same exception region (details in [Move labels and begin-exception markers onto the inserted instruction](../insert-calls-into-a-game-method-at-an-il-anchor/insert-calls-into-a-game-method-at-an-il-anchor-1.md)):

```csharp
// AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs L71-L80
    var injected = new CodeInstruction(OpCodes.Call, injectTarget);
    TranspilerInsertion.MoveEntryMarkers(code[index], injected);
    code.Insert(index, injected);

    if (DebugConfig.MultiPass)
        LogHelper.DebugOnce("transpiler-hohmann-multipass",
            "[AFC] HohmannMultiPass transpiler: injected DrawInline before " +
            "DrawCorrectionTransfer.");
    return code;
}
```

Source: [AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs#L71-L80)

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

4. **Add a fallback anchor and make the call idempotent.** A second pass inserts the same call before `ConsoleStyle.PopWidgetStyle()`, which
   runs every frame the body is drawn. `DrawInline` remembers `ImGui.GetFrameCount()` and returns on a second call in the same frame, so the first
   injection wins; the pass warns if stock ever moves `PopWidgetStyle` before `DrawCorrectionTransfer`:

```csharp
// AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs L100-L122
int index = code.FindIndex(instruction => instruction.Calls(anchor));
if (index < 0)
{
    LogHelper.WarnOnce("transpiler-hohmann-fallback-noanchor",
        "[AFC] HohmannFallback transpiler: no ConsoleStyle.PopWidgetStyle() call " +
        "found in DrawPlanWindow IL; fallback render inactive.");
    return code;
}

// The first injection wins. Keep the fallback, but report if stock moves it first.
MethodInfo? calculated = CalculatedControlsAnchor();
int calculatedIndex = calculated == null
    ? -1
    : code.FindIndex(instruction => instruction.Calls(calculated));
if (calculatedIndex > index)
    LogHelper.WarnOnce("transpiler-hohmann-fallback-order",
        "[AFC] HohmannFallback transpiler: ConsoleStyle.PopWidgetStyle now runs before " +
        "DrawCorrectionTransfer in DrawPlanWindow, so the multi-pass section draws at " +
        "the end of the window body instead of next to the transfer controls.");

var injected = new CodeInstruction(OpCodes.Call, injectTarget);
TranspilerInsertion.MoveEntryMarkers(code[index], injected);
code.Insert(index, injected);
```

Source: [AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs#L100-L122)

5. **Chain the passes.** The `[HarmonyTranspiler]` delegates to one entry point that composes them in order:

```csharp
// AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs L35-L41
internal static IEnumerable<CodeInstruction> Rewrite(
    IEnumerable<CodeInstruction> instructions)
{
    IEnumerable<CodeInstruction> rewritten = InjectCalculatedControls(instructions);
    rewritten = InjectFallbackControls(rewritten);
    return InterceptCreate(rewritten);
}
```

Source: [AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs#L35-L41)

6. **Unit test over the original IL.** `PatchProcessor.GetOriginalInstructions(method)` returns the method's IL without any patches applied;
   running the transpiler over it needs no game process beyond the loaded assemblies. The test asserts both insertions, the swap and the gate:

```csharp
// tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/ManeuverTranspilerTest.cs L20-L42
protected override void Execute(TestContext t)
{
    MethodInfo draw = AccessTools.Method(typeof(TransferPlanner), nameof(TransferPlanner.DrawPlanWindow), [typeof(IGameViewport)]);
    List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(draw);
    List<CodeInstruction> patched = Run(typeof(Patch_DrawPlanWindow), original);
    MethodInfo inline = AccessTools.Method(typeof(HohmannMultiPassUI), nameof(HohmannMultiPassUI.DrawInline));
    MethodInfo correction = AccessTools.Method(typeof(TransferPlanner), "DrawCorrectionTransfer", Type.EmptyTypes);
    MethodInfo popStyle = AccessTools.Method(typeof(ConsoleStyle), nameof(ConsoleStyle.PopWidgetStyle), Type.EmptyTypes);
    int correctionIndex = patched.FindIndex(ins => ins.Calls(correction));
    int popStyleIndex = patched.FindIndex(ins => ins.Calls(popStyle));
    t.Check("one pipeline inserts both multi-pass draw callbacks",
        CountCalls(patched, inline) == 2
        && correctionIndex > 0 && patched[correctionIndex - 1].Calls(inline)
        && popStyleIndex > 0 && patched[popStyleIndex - 1].Calls(inline));

    // The first injection wins, so the correction controls must remain first.
    t.Check("stock draws the correction controls before it pops the widget style",
        correctionIndex > 0 && popStyleIndex > correctionIndex);

    MethodInfo replacement = AccessTools.Method(typeof(HohmannCreateInterceptor), nameof(HohmannCreateInterceptor.CreateMaybeMultiPass));
    MethodInfo gate = AccessTools.Method(typeof(HohmannCreateInterceptor), nameof(HohmannCreateInterceptor.ShouldAllowCreateClick));
    t.Check("the same pipeline installs create interception",
        CountCalls(patched, replacement) == 1 && CountCalls(patched, gate) == 1);
```

Source: [tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/ManeuverTranspilerTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/ManeuverTranspilerTest.cs#L20-L42)[^ksa-advancedflightcomputer-maneuvertranspilertest]

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs) | The three passes and the presence probes |
| [AdvancedFlightComputer/Core/TranspilerInsertion.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/TranspilerInsertion.cs) | `MoveEntryMarkers` |
| [tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/ManeuverTranspilerTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/ManeuverTranspilerTest.cs) | Headless transpiler test |

## Variations and gotchas

- **Warn once, not every re-run.** Success lines go through `DebugOnce`, because Harmony re-runs transpilers whenever another patch is added to or
  removed from the method.
- **The anchor is a position, not a guarantee.** The pass finds the first `DrawCorrectionTransfer` call; if stock adds another call earlier, the
  insertion silently lands there.
- **Insertion here and in the other passes of the same method.** The create-button swap and gate in the same pipeline are the subject of
  [Same-signature static replacement, operand-only edit](../swap-one-call-inside-a-game-method-with-a-transpiler/swap-one-call-inside-a-game-method-with-a-transpiler-1.md) and [Anchor on a unique neighbouring call, then walk to the target](../identify-a-call-by-its-argument-il/identify-a-call-by-its-argument-il-2.md).
- **Test transpilers.** Without a unit test, a transpiler's only failure signal is a log line at a player's load.

## See also

- [Retarget the operand of one call (same signature)](write-a-harmony-transpiler-1.md), [Replace an instruction run with a helper that takes a local by reference](write-a-harmony-transpiler-3.md), [Filter one value read through a registry check](write-a-harmony-transpiler-4.md)
- [Writing a Harmony transpiler for a KSA method](overview.md)
- [Inject UI and swap calls inside the stock plan window with anchored transpilers](../../../../mods/ksa-advancedflightcomputer/features/plan-window-transpiler-injection.md)
- [Inserting calls into a game method at an IL anchor](../insert-calls-into-a-game-method-at-an-il-anchor/overview.md); the topic test-harmony-patches-without-the-game covers the harness

[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
[^ksa-dll]: KSA.dll (build 2026.10.10.5554), IL read with ilspycmd
[^ksa-advancedflightcomputer-planwindowpatchpipeline]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs
[^ksa-advancedflightcomputer-transpilerinsertion]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Core/TranspilerInsertion.cs
[^ksa-advancedflightcomputer-maneuvertranspilertest]: KSA-AdvancedFlightComputer/tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/ManeuverTranspilerTest.cs
