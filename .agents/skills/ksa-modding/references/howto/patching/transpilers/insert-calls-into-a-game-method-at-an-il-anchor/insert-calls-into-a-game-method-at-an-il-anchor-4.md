---
type: How-To
title: Insert before the first and after the last instance of a pass boundary
description: Append four instructions (ldloc.1, ldloc.0, ldc.i4.0, call) after the last EndRenderPass in Program.RenderGame to run the post-ImGui shader chain outside the render pass, and why hard-coded local indices are the weak point
category: patching
group: transpilers
topic: insert-calls-into-a-game-method-at-an-il-anchor
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/shaderextensions/features/rendergame-render-pass-transpiler]
techniques: [harmony-transpiler, codematcher, il-insert]
hooks: [harmony-transpiler KSA.Program.RenderGame]
lifecycle: [startup:starmap-before-main, frame:render]
game_types: [KSA.Program]
tags: [harmony, transpiler, il, insert, render-pass, codematcher, locals]
---

ShaderExtensions needs mod code to run on both sides of the swapchain render pass in `Program.RenderGame`. The "before" side replaces the `BeginRenderPass` call
([Replacement that does mod work, then calls the original](../swap-one-call-inside-a-game-method-with-a-transpiler/swap-one-call-inside-a-game-method-with-a-transpiler-3.md)). The "after" side is a pure *insertion*: four instructions appended right after the last `EndRenderPass` call. This article is about that second shape and
about its weak point: it pushes locals by index, which the compiler decides.

Prerequisites: a StarMap mod with `0Harmony` and the recipe from [Retarget the operand of one call (same signature)](../write-a-harmony-transpiler/write-a-harmony-transpiler-1.md). Demonstrated by ShaderExtensions in
[RenderGame transpiler around the swapchain render pass](../../../../mods/shaderextensions/features/rendergame-render-pass-transpiler.md).

## Background: how the game does it

The end of the swapchain pass in the 5554 IL. The locals of `RenderGame` are `[0] FrameResources` and `[1] CommandBuffer`; `EndRenderPass<CommandBuffer>` takes the command buffer:

```text
// KSA.dll IL via ilspycmd -il: KSA.Program::RenderGame (parameter lists shortened)
IL_0fa4: callvirt instance void KSA.ImGuiBackendVulkanImpl::RenderDrawData(...)
IL_0fa9: nop
IL_0faa: nop
IL_0fab: ldloc.1
IL_0fac: call void [Brutal.Vulkan]Brutal.VulkanApi.VkDeviceExtensions::EndRenderPass<valuetype [Brutal.Vulkan]Brutal.VulkanApi.CommandBuffer>(...)
IL_0fb1: nop
IL_0fb2: ldarg.0
```

Source: [current/dll/KSA.dll](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/dll/KSA.dll)[^ksa-dll]

and in the C#:

```csharp
// current/decomp/KSA/Program.cs L4948-L4953
commandBuffer2.BeginRenderPass(in pRenderPassBegin, contents);
RenderFinalComposite(commandBuffer2, shaderSlot, resourceFrameIndex);
ImGuiBackend.Vulkan.RenderDrawData(commandBuffer2);
commandBuffer2.EndRenderPass();
_screenshotCapture.OnRenderGameSwapchainGrab(commandBuffer2, frameResources.ColorImage);
Profiler.Gpu.EndFrame(commandBuffer2);
```

Source: [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4948-L4953)[^ksa-program]

## Lifecycle and timing

Installed at `startup:starmap-before-main`; the inserted call runs every flight frame in `frame:render` during command recording on the main thread, after the pass has ended and before the screenshot grab
and `Profiler.Gpu.EndFrame`. It is outside the render pass, where dynamic-rendering passes and image copies are legal. The editor path, `Program.RenderEditor`, has its own copy of the calls and is not patched.
See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Search from the end for the last boundary.** `matcher.End()` then `MatchStartBackwards` for a call whose method name is `EndRenderPass`; fail loudly if it is not found:

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

Source: [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L168-L183)[^shaderextensions-patches]

2. **Advance past the boundary and insert.** `Advance(1)` moves to the instruction after the call, then `InsertAndAdvance` inserts the four instructions: `ldloc.1` (the command buffer), `ldloc.0` (the frame
   resources), `ldc.i4.0`, and a call to the mod's `GlobalPostShaderHandler.RenderNow`:

```csharp
// ShaderExtensions/Patches.cs L185-L193
matcher.Advance(1);
matcher.InsertAndAdvance(
    new CodeInstruction(OpCodes.Ldloc_1), // commandBuffer2
    new CodeInstruction(OpCodes.Ldloc_0), // frameResources
    new CodeInstruction(OpCodes.Ldc_I4_0),
    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GlobalPostShaderHandler), nameof(GlobalPostShaderHandler.RenderNow)))
);

return matcher.Instructions();
```

Source: [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L185-L193)

   The call is stack-neutral for the surrounding code: the three loads feed `RenderNow` and it returns void.

3. **Check the indices against the IL.** `ldloc.1` is `CommandBuffer` and `ldloc.0` is `FrameResources` in 5554 (see the `.locals` block of the IL); the comment in the source says so.

4. **Capture operands from matched instructions where you can.** The instruction before the matched `EndRenderPass` call is the `ldloc.1` that feeds it; reusing that instruction removes one hard-coded index. A sketch
   of the idea, not code from the mod and not run in game:

```csharp
// adapted from ShaderExtensions/Patches.cs L176-L191 (untested sketch)
matcher.MatchStartBackwards(new CodeMatch(i => i.opcode == OpCodes.Call
                                               && i.operand is MethodInfo mi && mi.Name == "EndRenderPass"));
matcher.ThrowIfInvalid("EndRenderPass call not found");
CodeInstruction loadCommandBuffer = matcher.InstructionAt(-1).Clone();   // the load that feeds EndRenderPass
if (!loadCommandBuffer.IsLdloc())
    throw new InvalidOperationException("EndRenderPass is no longer fed by a local load");
```

   The frame-resources local has no such neighbour to copy; it needs a match on the instruction that first loads it, or an assertion about the `.locals` signature.

5. **Require each anchor exactly once.** The mod's compatibility notes propose matching the `RenderFinalComposite`, `RenderDrawData`, `EndRenderPass` sequence and requiring each anchor exactly once. In 5554 the
   method has one `BeginRenderPass` and one `EndRenderPass` call, so "last" and "only" coincide today.

## Complete example

| File | Role |
|---|---|
| [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L151-L194) | Both insertions in one transpiler |

## Variations and gotchas

- **Hard-coded locals.** `ldloc.0` and `ldloc.1` are right only while the compiler keeps `FrameResources` and `CommandBuffer` as the first two locals; a game update that adds a local earlier shifts them *without
  failing the patch*. Nothing in the count check detects this.
- **"Last occurrence" anchors.** Any later `EndRenderPass` added to `RenderGame` would move the insertion point.
- **Unused code.** `EndRenderPassMethod` is resolved but never used and the `0` passed as `dynamicOffset` is ignored by `RenderNow`.
- **Labels.** The instruction after `EndRenderPass` is not a branch target in 5554, so the inserted call does not need `MoveEntryMarkers`; if it were, see [Move labels and begin-exception markers onto the inserted instruction](insert-calls-into-a-game-method-at-an-il-anchor-1.md).
- **Failures stop the game from starting** when the anchor is missing, because the throw happens in `PatchAll` in `[StarMapBeforeMain]` ([Let an anchor failure stop the launch (and why that can be right)](../../safety/refuse-a-patch-and-roll-back-when-the-game-changes/refuse-a-patch-and-roll-back-when-the-game-changes-3.md)).

## See also

- [Move labels and begin-exception markers onto the inserted instruction](insert-calls-into-a-game-method-at-an-il-anchor-1.md), [Leave labels on the anchor so a jump skips the addition](insert-calls-into-a-game-method-at-an-il-anchor-2.md), [Snapshot state before a stock call, restore it in a paired prefix](insert-calls-into-a-game-method-at-an-il-anchor-3.md)
- [Inserting calls into a game method at an IL anchor](overview.md)
- [RenderGame transpiler around the swapchain render pass](../../../../mods/shaderextensions/features/rendergame-render-pass-transpiler.md)
- [Replacement that does mod work, then calls the original](../swap-one-call-inside-a-game-method-with-a-transpiler/swap-one-call-inside-a-game-method-with-a-transpiler-3.md), [Anchor on a unique neighbouring call, then walk to the target](../identify-a-call-by-its-argument-il/identify-a-call-by-its-argument-il-2.md)
- Related task: insert-a-custom-pass-into-render-game

[^ksa-dll]: KSA.dll (build 2026.10.10.5554), IL read with ilspycmd
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^shaderextensions-patches]: ShaderExtensions/Patches.cs
