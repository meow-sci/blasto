---
type: How-To
title: Replacement that does mod work, then calls the original
description: Swap the generic BeginRenderPass call in Program.RenderGame for a method with the same stack effect that records the mod's passes and then issues the original BeginRenderPass itself, because no method boundary exists at that point for a prefix
category: patching
group: transpilers
topic: swap-one-call-inside-a-game-method-with-a-transpiler
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/shaderextensions/features/rendergame-render-pass-transpiler]
techniques: [harmony-transpiler, call-site-rewrite, generic-method]
hooks: [starmap StarMapBeforeMain, harmony-transpiler KSA.Program.RenderGame]
lifecycle: [startup:starmap-before-main, frame:render]
game_types: [KSA.Program]
tags: [harmony, transpiler, call-site, render-pass, vulkan, wrap-the-original]
---

KSA draws its final frame in one legacy Vulkan render pass on the swapchain image: the tonemap composite then the ImGui draw. Everything ShaderExtensions
records (dynamic-rendering passes, image copies) is illegal inside a render pass instance, so the mod needs code to run just *before* that pass opens. There is
no method boundary at that point, so a prefix or postfix cannot do it. The transpiler removes the generic static extension call
`BeginRenderPass<CommandBuffer>(...)` and calls `ImGuiPreRender(CommandBuffer, in VkRenderPassBeginInfo, VkSubpassContents)` instead, which records the mod's
passes and then issues the original `BeginRenderPass` itself.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by ShaderExtensions in
[RenderGame transpiler around the swapchain render pass](../../../../mods/shaderextensions/features/rendergame-render-pass-transpiler.md).

## Background: how the game does it

The relevant stretch of `Program.RenderGame`:

```csharp
// current/decomp/KSA/Program.cs L4947-L4952
RenderAaPreparePasses(commandBuffer2, shaderSlot, resourceFrameIndex);
commandBuffer2.BeginRenderPass(in pRenderPassBegin, contents);
RenderFinalComposite(commandBuffer2, shaderSlot, resourceFrameIndex);
ImGuiBackend.Vulkan.RenderDrawData(commandBuffer2);
commandBuffer2.EndRenderPass();
_screenshotCapture.OnRenderGameSwapchainGrab(commandBuffer2, frameResources.ColorImage);
```

Source: [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4947-L4952)[^ksa-program]

`BeginRenderPass` is a generic static extension with `AggressiveInlining`, so the IL calls the closed generic over `CommandBuffer`:

```csharp
// current/decomp/Brutal.VulkanApi/VkDeviceExtensions.cs L1421-L1428
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public unsafe static void BeginRenderPass<T>(this T handle, in VkRenderPassBeginInfo pRenderPassBegin, VkSubpassContents contents) where T : VulkanHandleWrapper<VkCommandBuffer, VkDevice.FunctionTable>
{
	fixed (VkRenderPassBeginInfo* ptr = &pRenderPassBegin)
	{
		handle.PInvoke.vkCmdBeginRenderPass.Invoke(handle.Handle, ptr, contents);
	}
}
```

Source: [current/decomp/Brutal.VulkanApi/VkDeviceExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi/VkDeviceExtensions.cs#L1421-L1428)[^ksa-vkdeviceextensions]

The IL, build 2026.10.10.5554. Three values are on the stack when the call executes (`ldloc.1` the command buffer, `ldloca.s 4` the begin info by reference,
`ldloc.3` the contents); the locals are `[1] CommandBuffer`, `[3] VkSubpassContents`, `[4] VkRenderPassBeginInfo`:

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
```

Source: [current/dll/KSA.dll](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/dll/KSA.dll)[^ksa-dll]

## Lifecycle and timing

- **Install: `startup:starmap-before-main`.** ShaderExtensions calls `new Harmony("ShaderExtensions").PatchAll()` from `[StarMapBeforeMain]`, before the game's
  entry point ([ShaderExtensions/ShaderExtensions.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderExtensions.cs#L6-L15)[^shaderextensions-shaderextensions]).
- **Run: every flight frame in `frame:render`,** during command recording on the main thread. The editor path, `Program.RenderEditor`, has its own copy of the same
  calls and is not patched. A prefix on `RenderFinalComposite` would already be *inside* the pass, where `vkCmdBeginRendering` and `vkCmdCopyImage` are not allowed.
  See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Anchor on a unique neighbour, then walk to the call.** Find the single `RenderDrawData` call, then walk backwards to the nearest `BeginRenderPass`
   ([Anchor on a unique neighbouring call, then walk to the target](../identify-a-call-by-its-argument-il/identify-a-call-by-its-argument-il-2.md)):

```csharp
// ShaderExtensions/Patches.cs L151-L163
[HarmonyPatch(typeof(Program), "RenderGame"), HarmonyTranspiler, HarmonyDebug]
internal static IEnumerable<CodeInstruction> Program_RenderGame_Transpile(
  IEnumerable<CodeInstruction> instructions)
{
    var matcher = new CodeMatcher(instructions);

    matcher.MatchStartForward(
      CodeMatch.Calls(() => default(ImGuiBackendVulkanImpl).RenderDrawData(default)));
    matcher.ThrowIfInvalid("could not find ImGuiBackendVulkanImpl.RenderDrawData call in Program.RenderGame");

    matcher.MatchStartBackwards(
      CodeMatch.Calls(() => default(CommandBuffer).BeginRenderPass(default, default)));
    matcher.ThrowIfInvalid("could not find CommandBuffer.BeginRenderPass call in Program.RenderGame");
```

Source: [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L151-L163)[^shaderextensions-patches]

2. **Remove the call and insert yours.** The three stack values stay in place, so `ImGuiPreRender` consumes the same three:

```csharp
// ShaderExtensions/Patches.cs L165-L166
matcher.RemoveInstruction();
matcher.InsertAndAdvance(CodeInstruction.Call(() => ImGuiPreRender(default, default, default)));
```

Source: [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L165-L166)

3. **Write the replacement with the same stack effect and make it call the original.** It consumes `(CommandBuffer, in VkRenderPassBeginInfo, VkSubpassContents)`,
   does the mod's work, then calls `commandBuffer.BeginRenderPass(in beginInfo, contents)` so the pass opens exactly as before. The order inside: schedule a
   renderer rebuild on the first call, rebuild both chains if the source target is missing or the extent changed, run the per-window ImGui shaders, run the
   pre-ImGui chain, open the pass:

```csharp
// ShaderExtensions/Patches.cs L196-L225
private static bool rebuilt = false;
private static unsafe void ImGuiPreRender(
  CommandBuffer commandBuffer, in VkRenderPassBeginInfo beginInfo, VkSubpassContents contents)
{
    Renderer renderer = Program.GetRenderer();

    if (!rebuilt)
    {
        Program.ScheduleRendererRebuild();
        rebuilt = true;
    }

    bool needsRebuild =
        GlobalPostShaderHandler.SourceTarget == null ||
        GlobalPostShaderHandler.SourceTarget.Extent.Width != renderer.Extent.Width ||
        GlobalPostShaderHandler.SourceTarget.Extent.Height != renderer.Extent.Height;

    if (needsRebuild)
    {
        PostProcessingHandler.Rebuild();
        GlobalPostShaderHandler.Rebuild();
    }

    ImGuiRenderers.Render(renderer, commandBuffer);

    // Post process the game image before the UI pass composites it onto the swapchain.
    PostProcessingHandler.RenderNow(commandBuffer);

    commandBuffer.BeginRenderPass(in beginInfo, contents);
}
```

Source: [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L196-L225)

4. **Throw on a missing anchor,** with a message that names the call (`ThrowIfInvalid`). See [Let an anchor failure stop the launch (and why that can be right)](../../safety/refuse-a-patch-and-roll-back-when-the-game-changes/refuse-a-patch-and-roll-back-when-the-game-changes-3.md).

## Complete example

| File | Role |
|---|---|
| [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L151-L225) | Transpiler and `ImGuiPreRender` |
| [ShaderExtensions/ShaderExtensions.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderExtensions.cs) | `PatchAll` from `StarMapBeforeMain` |

## Variations and gotchas

- **Why not a prefix.** The code must run between two instructions in the middle of `RenderGame`, after `RenderAaPreparePasses` and before the pass opens; there
  is no method boundary there.
- **Hard-coded locals.** The companion insertion after `EndRenderPass` uses `ldloc.1` and `ldloc.0`; those indices are only right while the compiler keeps
  `FrameResources` and `CommandBuffer` as the first two locals ([Insert before the first and after the last instance of a pass boundary](../insert-calls-into-a-game-method-at-an-il-anchor/insert-calls-into-a-game-method-at-an-il-anchor-4.md)).
- **Only one mod can use this anchor.** After the swap, `RenderGame` contains no `BeginRenderPass` call, so a second mod searching backwards from `RenderDrawData`
  finds nothing ([Anchors that one mod can consume (and what the next transpiler then sees)](../../harmony/coexist-with-other-mods-patching-the-same-method/coexist-with-other-mods-patching-the-same-method-2.md)).
- **Generic arguments.** The IL calls the closed generic over `CommandBuffer`; the mod matches it with
  `CodeMatch.Calls(() => default(CommandBuffer).BeginRenderPass(default, default))`.

## See also

- [Same-signature static replacement, operand-only edit](swap-one-call-inside-a-game-method-with-a-transpiler-1.md), [Wider signature with an extra ldarg.0 appended](swap-one-call-inside-a-game-method-with-a-transpiler-2.md), [Gate helper that skips the call for the mod's own condition](swap-one-call-inside-a-game-method-with-a-transpiler-4.md), [Adapter that fixes up arguments and converts the result back](swap-one-call-inside-a-game-method-with-a-transpiler-5.md)
- [Swapping one call inside a game method with a transpiler](overview.md)
- [RenderGame transpiler around the swapchain render pass](../../../../mods/shaderextensions/features/rendergame-render-pass-transpiler.md)
- Related task: insert-a-custom-pass-into-render-game

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-vkdeviceextensions]: Brutal.VulkanApi.VkDeviceExtensions (decompiled, build 2026.10.10.5554)
[^ksa-dll]: KSA.dll (build 2026.10.10.5554), IL read with ilspycmd
[^shaderextensions-shaderextensions]: ShaderExtensions/ShaderExtensions.cs
[^shaderextensions-patches]: ShaderExtensions/Patches.cs
