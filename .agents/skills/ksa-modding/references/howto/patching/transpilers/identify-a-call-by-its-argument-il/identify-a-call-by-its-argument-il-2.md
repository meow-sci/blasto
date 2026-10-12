---
type: How-To
title: Anchor on a unique neighbouring call, then walk to the target
description: "When the target call is not distinctive, anchor on a call that occurs exactly once nearby and walk to it: from RenderDrawData back to the nearest BeginRenderPass, from the end to the last EndRenderPass, and from Burn.Create back at most 160 instructions to the PrimaryButton call"
category: patching
group: transpilers
topic: identify-a-call-by-its-argument-il
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/shaderextensions/features/rendergame-render-pass-transpiler
  - mods/ksa-advancedflightcomputer/features/plan-window-transpiler-injection
techniques: [harmony-transpiler, il-anchor, codematcher]
hooks: [harmony-transpiler KSA.Program.RenderGame, harmony-transpiler KSA.TransferPlanner.DrawPlanWindow]
lifecycle: [startup:starmap-before-main, startup:all-mods-loaded, frame:render, frame:draw-ui]
game_types: [KSA.Program, KSA.TransferPlanner]
tags: [harmony, transpiler, il, anchor, walk, codematcher, bounded-search]
---

Sometimes the call you need to change is the same as other calls and its arguments do not distinguish it. The pattern then is to anchor on something that
occurs exactly once nearby and walk, in a bounded way, to the target. ShaderExtensions finds the single `RenderDrawData` call and walks back to the nearest `BeginRenderPass`; it
finds the last `EndRenderPass` from the end of the method. KSA-AdvancedFlightComputer starts from `Burn.Create` and walks back at most 160 instructions to the
`PrimaryButton` call whose bool result it wants to filter.

Prerequisites: a StarMap mod with `0Harmony` and the recipe from [Retarget the operand of one call (same signature)](../write-a-harmony-transpiler/write-a-harmony-transpiler-1.md). Demonstrated in
[RenderGame transpiler around the swapchain render pass](../../../../mods/shaderextensions/features/rendergame-render-pass-transpiler.md) and [Inject UI and swap calls inside the stock plan window with anchored transpilers](../../../../mods/ksa-advancedflightcomputer/features/plan-window-transpiler-injection.md).

## Background: how the game does it

`Program.RenderGame` has exactly one `RenderDrawData` call, preceded by `RenderFinalComposite` and a `BeginRenderPass`, and followed by `EndRenderPass`. In build
2026.10.10.5554 the method contains one `BeginRenderPass` and one `EndRenderPass` call:

```text
// KSA.dll IL via ilspycmd -il: KSA.Program::RenderGame (parameter lists shortened)
IL_0f88: ldloc.1
IL_0f89: ldloca.s 4
IL_0f8b: ldloc.3
IL_0f8c: call void [Brutal.Vulkan]Brutal.VulkanApi.VkDeviceExtensions::BeginRenderPass<valuetype [Brutal.Vulkan]Brutal.VulkanApi.CommandBuffer>(...)
IL_0f91: nop
IL_0f92: nop
IL_0f93: ldarg.0
IL_0f94: ldloc.1
IL_0f95: ldloc.s 10
IL_0f97: ldloc.2
IL_0f98: call instance void KSA.Program::RenderFinalComposite(...)
IL_0f9d: nop
IL_0f9e: call class KSA.ImGuiBackendVulkanImpl KSA.ImGuiBackend::get_Vulkan(...)
IL_0fa3: ldloc.1
IL_0fa4: callvirt instance void KSA.ImGuiBackendVulkanImpl::RenderDrawData(...)
IL_0fa9: nop
IL_0faa: nop
IL_0fab: ldloc.1
IL_0fac: call void [Brutal.Vulkan]Brutal.VulkanApi.VkDeviceExtensions::EndRenderPass<valuetype [Brutal.Vulkan]Brutal.VulkanApi.CommandBuffer>(...)
IL_0fb1: nop
```

Source: [current/dll/KSA.dll](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/dll/KSA.dll)[^ksa-dll]

`TransferPlanner.DrawPlanWindow` draws the CREATE button and, ninety-odd instructions later, creates the burn; the button only sets a local, and the
creation sits in a later block that tests it:

```csharp
// current/decomp/KSA/TransferPlanner.cs L745-L779
	if (flag)
	{
		ConsoleStyle.FooterRightAlign(ConsoleWidgets.ButtonWidth("CREATE".AsSpan()));
		if (ConsoleWidgets.PrimaryButton("CREATE".AsSpan()))
		{
			flag3 = true;
		}
	}
}
ConsoleStyle.EndFooter();
if (flag)
{
	if (flag3)
	{
		if ((object)_transferBurn == null && !_transferBeingRefined && Source != null && _selectedEntry != null)
		{
			PatchedConic patchedConic = Source.FlightPlan.TryFindPatch(_selectedEntry.TransferData.Start);
			InterstellarBurns? selectedInterstellarBurns = SelectedInterstellarBurns;
			if (selectedInterstellarBurns.HasValue)
			{
				InterstellarBurns valueOrDefault3 = selectedInterstellarBurns.GetValueOrDefault();
				if (true)
				{
					if (patchedConic != null)
					{
						_transferBurn = CreateInterstellarBurns(Source, patchedConic, _selectedEntry.TransferData, valueOrDefault3);
					}
					goto IL_168a;
				}
			}
			if (patchedConic != null)
			{
				OrbitPointCce pointAt = patchedConic.Orbit.GetPointAt(_selectedEntry.TransferData.Start);
				_transferBurn = Burn.Create(pointAt, _selectedEntry.TransferData.Start.Seconds(), _selectedEntry.TransferData.TransferDvVlf, patchedConic, Source);
				_transferBurn.IsGizmoActive = false;
```

Source: [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L745-L779)[^ksa-transferplanner]

The IL: `PrimaryButton` is at `IL_14c5` and `Burn.Create` at `IL_15e9`, 98 instructions apart:

```text
// KSA.dll IL via ilspycmd -il: KSA.TransferPlanner::DrawPlanWindow (parameter lists shortened)
IL_14bb: ldstr "CREATE"
IL_14c0: call valuetype [System.Runtime]System.ReadOnlySpan`1<char> [System.Memory]System.MemoryExtensions::AsSpan(...)
IL_14c5: call bool KSA.ConsoleWidgets::PrimaryButton(...)
IL_14ca: stloc.s 140
IL_14cc: ldloc.s 140
IL_14ce: brfalse.s IL_14d3

IL_14d0: ldc.i4.1
IL_14d1: stloc.s 5

IL_14d3: nop
```

Source: [current/dll/KSA.dll](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/dll/KSA.dll)

## Lifecycle and timing

Both searches run once at patch time. ShaderExtensions installs from `[StarMapBeforeMain]` (`startup:starmap-before-main`); AFC from `[StarMapAllModsLoaded]`
(`startup:all-mods-loaded`). The patched code runs in `frame:render` and `frame:draw-ui`. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Pick an anchor that occurs exactly once.** `CodeMatch.Calls(() => default(ImGuiBackendVulkanImpl).RenderDrawData(default))` matches the one draw call; `ThrowIfInvalid`
   names what was not found:

```csharp
// ShaderExtensions/Patches.cs L155-L159
var matcher = new CodeMatcher(instructions);

matcher.MatchStartForward(
  CodeMatch.Calls(() => default(ImGuiBackendVulkanImpl).RenderDrawData(default)));
matcher.ThrowIfInvalid("could not find ImGuiBackendVulkanImpl.RenderDrawData call in Program.RenderGame");
```

Source: [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L155-L159)[^shaderextensions-patches]

2. **Walk backwards to the nearest target.** `MatchStartBackwards` from the anchor finds the nearest preceding `BeginRenderPass`, which is the one that opens the swapchain pass
   (not a render pass recorded earlier in the method):

```csharp
// ShaderExtensions/Patches.cs L161-L163
matcher.MatchStartBackwards(
  CodeMatch.Calls(() => default(CommandBuffer).BeginRenderPass(default, default)));
matcher.ThrowIfInvalid("could not find CommandBuffer.BeginRenderPass call in Program.RenderGame");
```

Source: [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L161-L163)

3. **Search from the end for a last occurrence.** For the post-pass insertion, `matcher.End()` then `MatchStartBackwards` finds the last call named `EndRenderPass`:

```csharp
// ShaderExtensions/Patches.cs L168-L183
matcher.End();

MethodInfo EndRenderPassMethod =
    AccessTools.Method(
        typeof(Brutal.VulkanApi.VkDeviceExtensions),
        "EndRenderPass"
    );

matcher.MatchStartBackwards(
    new CodeMatch(
        instr => instr.opcode == OpCodes.Call &&
                 instr.operand is MethodInfo mi &&
                 mi.Name == "EndRenderPass"
    )
);
matcher.ThrowIfInvalid("EndRenderPass call not found");
```

Source: [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L168-L183)

   The fragility is stated in the mod's notes: any later `EndRenderPass` added to `RenderGame` would move the insertion point. The suggested hardening is to match the
   `RenderFinalComposite`, `RenderDrawData`, `EndRenderPass` sequence and require each anchor exactly once.

4. **Bound the walk.** AFC finds `Burn.Create`, then walks back to the nearest `ConsoleWidgets.PrimaryButton` call, but only within 160 instructions. If the nearest button is
   farther away, the gate is not installed instead of gating the wrong button:

```csharp
// AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs L173-L200
private static int FindCreateButton(
    List<CodeInstruction> code, int burnCreateIndex,
    MethodInfo? clickGate, out int gapTooLarge)
{
    gapTooLarge = 0;
    if (clickGate == null || burnCreateIndex < 0)
        return -1;

    for (int i = burnCreateIndex - 1; i >= 0; i--)
    {
        if (!IsPrimaryButtonCall(code[i]))
            continue;

        int gap = burnCreateIndex - i;
        if (gap <= MaxIlGapPrimaryButtonToBurnCreate)
            return i;

        gapTooLarge = gap;
        break;
    }
    return -1;
}

private static bool IsPrimaryButtonCall(CodeInstruction instruction)
    => (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
       && instruction.operand is MethodInfo method
       && method.DeclaringType == typeof(ConsoleWidgets)
       && method.Name == nameof(ConsoleWidgets.PrimaryButton);
```

Source: [AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs#L173-L200)[^ksa-advancedflightcomputer-planwindowpatchpipeline]

5. **Report why it failed.** The pass records `gapTooLarge` and warns once with the gap, so a stock change that moves the button reads as "button 212 instructions away", not "patch
   did nothing".

6. **Insert relative to the walked-to instruction.** AFC inserts `ShouldAllowCreateClick(bool)` right after `PrimaryButton`, filtering the bool left on the stack
   ([Move labels and begin-exception markers onto the inserted instruction](../insert-calls-into-a-game-method-at-an-il-anchor/insert-calls-into-a-game-method-at-an-il-anchor-1.md)):

```csharp
// AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs L165-L169
bool gateInjected = createButtonIndex >= 0 && clickGate != null;
if (gateInjected)
    code.Insert(createButtonIndex + 1, new CodeInstruction(OpCodes.Call, clickGate));

ReportCreateInterception(burnCreateReplaced, gateInjected, gapTooLarge);
```

Source: [AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs#L165-L169)

## Complete example

| File | Role |
|---|---|
| [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L151-L194) | Anchor on `RenderDrawData`, walk back, search from the end |
| [AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs#L131-L200) | `InterceptCreate`, bounded `FindCreateButton` |

## Variations and gotchas

- **Require each anchor exactly once.** An anchor that happens to be unique today is a weaker guarantee than one the transpiler checks.
- **Prefer a bounded walk.** An unbounded search will happily find a target in a different block after a refactor.
- **A walk finds the nearest, not the right, instruction.** If another `BeginRenderPass` were added between the anchor and the swapchain pass, the nearest would be the wrong one.
- **The first `Burn.Create` swap lands on the first match** in `DrawPlanWindow`; `CreateInterstellarBurns` is a separate method with its own calls, so it is not touched
  ([Same-signature static replacement, operand-only edit](../swap-one-call-inside-a-game-method-with-a-transpiler/swap-one-call-inside-a-game-method-with-a-transpiler-1.md)).
- **Another mod's output is part of the input.** After ShaderExtensions has run, a second mod searching backwards from `RenderDrawData` finds `ImGuiPreRender` rather than
  `BeginRenderPass` ([Anchors that one mod can consume (and what the next transpiler then sees)](../../harmony/coexist-with-other-mods-patching-the-same-method/coexist-with-other-mods-patching-the-same-method-2.md)).

## See also

- [Scan backwards through the argument loads to the previous call](identify-a-call-by-its-argument-il-1.md), [Anchor on a UTF-8 label literal in the method](identify-a-call-by-its-argument-il-3.md), [Match the instruction that consumes the result](identify-a-call-by-its-argument-il-4.md)
- [Identifying the right call in a method that makes several similar calls](overview.md)
- [RenderGame transpiler around the swapchain render pass](../../../../mods/shaderextensions/features/rendergame-render-pass-transpiler.md), [Inject UI and swap calls inside the stock plan window with anchored transpilers](../../../../mods/ksa-advancedflightcomputer/features/plan-window-transpiler-injection.md)
- [Replacement that does mod work, then calls the original](../swap-one-call-inside-a-game-method-with-a-transpiler/swap-one-call-inside-a-game-method-with-a-transpiler-3.md)

[^ksa-dll]: KSA.dll (build 2026.10.10.5554), IL read with ilspycmd
[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
[^shaderextensions-patches]: ShaderExtensions/Patches.cs
[^ksa-advancedflightcomputer-planwindowpatchpipeline]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs
