---
type: Mod Feature
title: Inject UI and swap calls inside the stock plan window with anchored transpilers
description: One transpiler pipeline on TransferPlanner.DrawPlanWindow inserts a draw call before two IL anchors, filters the Create button's result with a click gate, and swaps the Burn.Create call site for a same-signature replacement, moving labels and exception markers correctly
mod: ksa-advancedflightcomputer
category: patching
techniques: [harmony-transpiler, reflection]
hooks: [harmony-transpiler KSA.TransferPlanner.DrawPlanWindow]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.TransferPlanner, KSA.Burn, KSA.ConsoleWidgets, KSA.ConsoleStyle]
tags: [transpiler, il, transfer-planner, call-site-swap, labels, exception-blocks]
---

The flyby and multi-pass controls for stock Hohmann transfers live inside the stock Transfer Planning
window, next to stock's own transfer controls, and the stock CREATE button creates AFC's flyby or
first-pass burn instead of the center-aimed one. AFC does this without replacing the window: a
transpiler rewrites `TransferPlanner.DrawPlanWindow` in three anchored steps.

## How it works

`Patch_DrawPlanWindow`'s `[HarmonyTranspiler]` delegates to `PlanWindowPatchPipeline.Rewrite`, which
chains three passes over the instruction list ([AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs#L35-L41)):

1. **Insert before the transfer controls.** Find the first `call TransferPlanner.DrawCorrectionTransfer()`
   (a private method resolved with `AccessTools.Method`) and insert `call HohmannMultiPassUI.DrawInline()`
   before it, so AFC's section renders right above stock's correction controls.
2. **Fallback anchor.** Also insert the same call before the first `call ConsoleStyle.PopWidgetStyle()`,
   which runs every frame the body is drawn, for frames where the first anchor is not reached.
   `DrawInline` remembers `ImGui.GetFrameCount()` and returns on a second call in the same frame, so
   "the first injection wins". The pass warns if stock ever moves `PopWidgetStyle` before
   `DrawCorrectionTransfer`.
3. **Swap the create call and gate the click.**
   - Replace the `call Burn.Create(OrbitPointCce, double, double3, PatchedConic, Vehicle)` operand with
     `HohmannCreateInterceptor.CreateMaybeMultiPass`, a static method with the identical signature, so
     the arguments already on the stack stay valid; the labels and blocks of the original instruction
     are carried over to the new one.
   - Walk back from that call to the nearest `call ConsoleWidgets.PrimaryButton` (within 160
     instructions) and insert `call HohmannCreateInterceptor.ShouldAllowCreateClick(bool) -> bool`
     right after it. The button's `bool` result is on the stack, so the injected call filters it
     without touching anything else ([AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs#L131-L199)).

### Moving entry markers

Inserting before an anchor that is a branch target would let the jump skip the new call, and
inserting at the first instruction of a `try` would leave the call outside the exception region.
`TranspilerInsertion.MoveEntryMarkers` moves the anchor's labels and its begin-exception markers to the
inserted instruction while keeping end markers on the anchor, so the inserted call stays inside the
same region ([AdvancedFlightComputer/Core/TranspilerInsertion.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/TranspilerInsertion.cs)). The settings page uses the
opposite choice on purpose: it leaves labels on the anchor so a jump to it skips the sections (see
[Mods settings page sections](mods-settings-page-sections.md)).

### Robustness

- Each pass returns the list unchanged and warns once when its anchor or target is missing; the
  presence probes (`HasCalculatedControlsAnchor`, `HasCreateAnchor`, `HasFallbackControlsAnchor`) gate
  whether multi-pass and flyby UI are enabled at load ([AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L160-L174)).
- Success lines go through `LogHelper.DebugOnce`, because Harmony re-runs transpilers whenever another
  patch is added to or removed from the method.
- The headless test `afc-maneuver-transpilers` runs the transpiler over
  `PatchProcessor.GetOriginalInstructions(DrawPlanWindow)` and asserts both insertions, the swap and
  the gate positions ([tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/ManeuverTranspilerTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/ManeuverTranspilerTest.cs)).

## Lifecycle and timing

The transpiler runs once when the PlanWindow block is patched at `[StarMapAllModsLoaded]` (and again
whenever Harmony re-patches the method). The injected calls run where stock's code runs: inside
`DrawPlanWindow`, from `Program.OnDrawUiThreadSafe` on the main thread (`frame:draw-ui`), inside the
open console window with the widget style pushed. `CreateMaybeMultiPass` therefore runs exactly when
stock would create its burn, and stock then queues the returned burn into
`InputEvents.BurnUpdateBuffer` as usual.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs) | The three passes and anchor probes |
| [AdvancedFlightComputer/Core/TranspilerInsertion.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/TranspilerInsertion.cs) | Label and exception-marker hand-over |
| [AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.cs) | Transpiler entry on the patch class |
| [AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs) | Same-signature `Burn.Create` replacement and click gate |
| [AdvancedFlightComputer/Features/MultiPass/HohmannMultiPassUI.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/HohmannMultiPassUI.cs) | `DrawInline` with its once-per-frame guard |
| [tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/ManeuverTranspilerTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/ManeuverTranspilerTest.cs) | IL-level test |

## Snippets

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

## Game APIs touched

| Member | Use |
|---|---|
| `TransferPlanner.DrawPlanWindow(IGameViewport)` ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L305)) | Transpiled method |
| `TransferPlanner.DrawCorrectionTransfer()` (private, [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs)) | Primary anchor |
| `ConsoleStyle.PopWidgetStyle()` ([current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L641)) | Fallback anchor |
| `Burn.Create(OrbitPointCce, double, double3, PatchedConic, Vehicle)` ([current/decomp/KSA/Burn.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Burn.cs#L185)) | Swapped call site |
| `ConsoleWidgets.PrimaryButton` ([current/decomp/KSA/ConsoleWidgets.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleWidgets.cs#L157)) | Create button whose result is gated |

## Pitfalls and version notes

- A call-site swap only works when the replacement has exactly the same parameter list and return
  type (static, or with the instance as first parameter for an instance call).
- Bound the backwards search for the button (here 160 instructions) so a refactor that moves the
  button far away disables the gate instead of gating the wrong button.
- `FindIndex` swaps the first `Burn.Create` call in the method. In the analyzed build
  `DrawPlanWindow` has one direct call (the interstellar path goes through `CreateInterstellarBurns`);
  if stock adds another create path earlier in the method, the swap would silently land on it.

## See also

- [Transfer Planner custom plan types](transfer-planner-custom-plan-types.md)
- [Multi-pass Hohmann schedule](multi-pass-hohmann-schedule.md)
- [Flyby periapsis targeting](flyby-periapsis-targeting.md)
- [Mods settings page sections](mods-settings-page-sections.md)
- [Inject UI and swap a call in DrawPlanWindow with a transpiler that validates its IL anchors](../../../howto/flight-and-orbits/transfers/add-plan-type-to-the-transfer-planner/add-plan-type-to-the-transfer-planner-2.md) - how-to article demonstrating this feature
- [Anchor on a unique neighbouring call, then walk to the target](../../../howto/patching/transpilers/identify-a-call-by-its-argument-il/identify-a-call-by-its-argument-il-2.md) - how-to article demonstrating this feature
- [Move labels and begin-exception markers onto the inserted instruction](../../../howto/patching/transpilers/insert-calls-into-a-game-method-at-an-il-anchor/insert-calls-into-a-game-method-at-an-il-anchor-1.md) - how-to article demonstrating this feature
- [Same-signature static replacement, operand-only edit](../../../howto/patching/transpilers/swap-one-call-inside-a-game-method-with-a-transpiler/swap-one-call-inside-a-game-method-with-a-transpiler-1.md) - how-to article demonstrating this feature
- [Insert a call before a validated anchor](../../../howto/patching/transpilers/write-a-harmony-transpiler/write-a-harmony-transpiler-2.md) - how-to article demonstrating this feature
- [Take over what CREATE does in the Transfer Planner with a same-signature call swap and a click gate](../../../howto/ui/menus/inject-ui-into-a-stock-window/inject-ui-into-a-stock-window-3.md) - how-to article demonstrating this feature

