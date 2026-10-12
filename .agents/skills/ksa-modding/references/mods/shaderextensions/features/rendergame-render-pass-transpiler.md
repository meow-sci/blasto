---
type: Mod Feature
title: RenderGame transpiler around the swapchain render pass
description: A Harmony transpiler on Program.RenderGame swaps the swapchain BeginRenderPass call for a static method with the same stack signature that records mod passes first, and inserts a call after the last EndRenderPass, giving the mod two command-recording slots outside any render pass
mod: shaderextensions
category: patching
techniques: [harmony-transpiler, starmap-lifecycle]
hooks: [starmap StarMapBeforeMain, harmony-transpiler KSA.Program.RenderGame]
lifecycle: [startup:starmap-before-main, frame:render, render:post-process, render:imgui]
game_types:
  - KSA.Program
  - KSA.ImGuiBackendVulkanImpl
  - Brutal.VulkanApi.VkDeviceExtensions
  - Brutal.VulkanApi.CommandBuffer
  - Brutal.VulkanApi.Abstractions.FrameResources
tags: [harmony, transpiler, vulkan, render-pass, il, rendering]
---

KSA draws its final frame in one legacy Vulkan render pass on the swapchain image: the tonemap
composite (`RenderFinalComposite`) and then the ImGui draw (`RenderDrawData`). Everything
ShaderExtensions records (dynamic-rendering passes and image copies) is illegal inside a render
pass instance, so the mod needs code to run just before that pass opens and just after it closes.
No method boundary exists at either point, so it rewrites the IL of `Program.RenderGame`.[^sx-patches]

## How it works

1. **Patch installation.** The entry class calls `new Harmony("ShaderExtensions").PatchAll()` from
   `[StarMapBeforeMain]`, which applies every `[HarmonyPatch]` in `Patches`
   ([ShaderExtensions/ShaderExtensions.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderExtensions.cs#L6-L15)).[^sx-entry]
2. **Find the swapchain pass.** The transpiler matches the call to
   `ImGuiBackendVulkanImpl.RenderDrawData(CommandBuffer)`, then walks backwards to the nearest
   `BeginRenderPass` call. `Program.RenderGame` has exactly one `RenderDrawData` call
   ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4948-L4951)).[^ksa-program]
3. **Swap the call, keep the stack.** In IL the call is to the generic static extension
   `VkDeviceExtensions.BeginRenderPass<CommandBuffer>(CommandBuffer, in VkRenderPassBeginInfo,
   VkSubpassContents)`, with `ldloc.1`, `ldloca.s 4`, `ldloc.3` already pushed (IL_0f88 to IL_0f8c in
   build 5554). The transpiler removes the call and inserts a call to
   `ImGuiPreRender(CommandBuffer, in VkRenderPassBeginInfo, VkSubpassContents)`, which consumes the same
   three stack values, does the mod's work and then calls `BeginRenderPass` itself
   ([ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L157-L166),
   [current/decomp/Brutal.VulkanApi/VkDeviceExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi/VkDeviceExtensions.cs#L1422-L1440)).[^ksa-dll] [^brutal-vkdeviceext]
4. **Append after the pass.** From the end of the method it searches backwards for the last `call`
   whose method is named `EndRenderPass` (IL_0fac), steps past it and inserts `ldloc.1`, `ldloc.0`,
   `ldc.i4.0`, `call GlobalPostShaderHandler.RenderNow`. In build 5554 the method's locals are
   `[0] FrameResources` and `[1] CommandBuffer`, so the hard-coded indices are right
   ([ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L168-L191)).
5. **What `ImGuiPreRender` does**, in order: on its first call `Program.ScheduleRendererRebuild()`;
   rebuild both chains if the post-ImGui source target is missing or the renderer extent changed;
   `ImGuiRenderers.Render` (per-window ImGui shaders); `PostProcessingHandler.RenderNow` (pre-ImGui
   chain); finally the original `commandBuffer.BeginRenderPass(in beginInfo, contents)`
   ([ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L196-L225)).
6. **Companion patches** on the same class: a `Program.Main` prefix for XML registration, a
   `RenderGame` prefix that updates the time buffer and a `RebuildRenderer` postfix
   ([ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L20-L43),
   [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L227-L236)).

| Slot | Runs | Contents |
|---|---|---|
| Before `BeginRenderPass` (line 4948) | after `RenderAaPreparePasses` and composite-mode screenshot capture | ImGui window shaders, pre-ImGui chain |
| After `EndRenderPass` (line 4951) | before swapchain-grab screenshot, profiler end, `End` | post-ImGui chain |

## Lifecycle and timing

- **Installed** at `startup:starmap-before-main`: StarMap invokes `[StarMapBeforeMain]` from
  `RuntimeMod.InitializeMod` while loading mods, before it calls the game's entry point
  ([StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L156-L159)).[^sm-runtimemod]
- **Runs** every flight frame in `frame:render`, during command recording on the main thread. The
  editor path, `Program.RenderEditor`, has its own copy of the same four calls
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5071-L5075)) and is not patched.
- **Why not prefix and postfix on a method?** `RenderFinalComposite` is called inside the pass, and
  `RenderDrawData` likewise; a prefix on `RenderFinalComposite` would already be inside the render
  pass, where `vkCmdBeginRendering` (used by `RenderTarget.BeginRendering`) and `vkCmdCopyImage` are
  not allowed
  ([current/decomp/KSA.Rendering/RenderTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderTarget.cs#L201-L234)).[^ksa-rendertarget]

## Key code

| Path | Role |
|---|---|
| [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L151-L194) | The transpiler |
| [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L196-L225) | `ImGuiPreRender`, the replacement for `BeginRenderPass` |
| [ShaderExtensions/ShaderExtensions.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderExtensions.cs#L6-L15) | `PatchAll` from `[StarMapBeforeMain]` |

## Snippets

```csharp
// ShaderExtensions/Patches.cs L151-L166, L176-L193
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

            matcher.RemoveInstruction();
            matcher.InsertAndAdvance(CodeInstruction.Call(() => ImGuiPreRender(default, default, default)));
// ...
            matcher.MatchStartBackwards(
                new CodeMatch(
                    instr => instr.opcode == OpCodes.Call &&
                             instr.operand is MethodInfo mi &&
                             mi.Name == "EndRenderPass"
                )
            );
            matcher.ThrowIfInvalid("EndRenderPass call not found");

            matcher.Advance(1);
            matcher.InsertAndAdvance(
                new CodeInstruction(OpCodes.Ldloc_1), // commandBuffer2
                new CodeInstruction(OpCodes.Ldloc_0), // frameResources
                new CodeInstruction(OpCodes.Ldc_I4_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GlobalPostShaderHandler), nameof(GlobalPostShaderHandler.RenderNow)))
            );

            return matcher.Instructions();
```

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

```csharp
// current/decomp/KSA/Program.cs L4646-L4651, L4947-L4952
	private unsafe void RenderGame(AcquiredFrame frame, double dt)
	{
		AcquiredFrame acquiredFrame = frame;
		acquiredFrame.Deconstruct(out FrameResources resources, out CommandBuffer commandBuffer);
		FrameResources frameResources = resources;
		CommandBuffer commandBuffer2 = commandBuffer;
// ...
		RenderAaPreparePasses(commandBuffer2, shaderSlot, resourceFrameIndex);
		commandBuffer2.BeginRenderPass(in pRenderPassBegin, contents);
		RenderFinalComposite(commandBuffer2, shaderSlot, resourceFrameIndex);
		ImGuiBackend.Vulkan.RenderDrawData(commandBuffer2);
		commandBuffer2.EndRenderPass();
		_screenshotCapture.OnRenderGameSwapchainGrab(commandBuffer2, frameResources.ColorImage);
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `Program.RenderGame(AcquiredFrame, double)` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4646-L4955) | Transpiler and prefix target |
| `Program.RenderEditor` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4957-L5079) | Same pattern, not patched |
| `Program.ScheduleRendererRebuild` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5248-L5251) | Forced rebuild on the first frame |
| `ImGuiBackendVulkanImpl.RenderDrawData(CommandBuffer)` | [current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L681-L687) | Forward anchor |
| `VkDeviceExtensions.BeginRenderPass<T>`, `EndRenderPass<T>` | [current/decomp/Brutal.VulkanApi/VkDeviceExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi/VkDeviceExtensions.cs#L1422-L1440) | Replaced call and backward anchor |
| `CommandBuffer` (struct) | [current/decomp/Brutal.VulkanApi/CommandBuffer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi/CommandBuffer.cs#L5) | Passed by value on the IL stack |

## Pitfalls and version notes

- **Hard-coded locals.** `ldloc.0` and `ldloc.1` are only right while the compiler keeps
  `FrameResources` and `CommandBuffer` as the first two locals; a game update that adds a local
  earlier shifts them without failing the patch. The compatibility plan proposes capturing the
  operands from the matched instructions instead.[^sx-compatplan]
- **"Last `EndRenderPass`" anchor.** Any later `EndRenderPass` added to `RenderGame` would move the
  insertion point. The plan suggests matching the `RenderFinalComposite`, `RenderDrawData`,
  `EndRenderPass` sequence and requiring each anchor exactly once.
- **Unused code.** `EndRenderPassMethod` is resolved but never used; the `0` passed as
  `dynamicOffset` is ignored by `RenderNow`. The `MatcherFindReplace` helper at the end of the file
  (find a call, swap its operand, insert extra argument loads) is only referenced from the
  commented-out gauge patches
  ([ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L297-L317)).
- **`[HarmonyDebug]` is left on** the transpiler, which turns on Harmony's debug log for that patch.
- **Failures stop the game from starting.** `ThrowIfInvalid` throws during `PatchAll` in
  `[StarMapBeforeMain]` when an anchor is missing, and Harmony rethrows it. StarMap invokes
  `[StarMapBeforeMain]` without a `try`/`catch`, and nothing up to its own `Main` catches either, so the
  launch aborts instead of running the game unpatched
  ([StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L156-L159),
  [StarMap/Program.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/Program.cs#L9-L29)).
  A shifted local index, by contrast, is not detected at all.[^sm-program]
- **Only one mod can use this anchor.** After this transpiler runs, `RenderGame` no longer contains a
  `BeginRenderPass` call (it moved into `ImGuiPreRender`), so a second mod that searches backwards
  from `RenderDrawData` for it finds nothing and throws if its transpiler runs later in the chain.

## See also

- [Pre-ImGui post-processing on the HDR scene image](pre-imgui-post-processing.md)
- [Post-ImGui global post shaders on the swapchain image](post-imgui-global-post-shaders.md)
- [Per-window ImGui post shader](imgui-window-post-shader.md)
- [Rebuilding mod GPU resources with the game renderer](renderer-rebuild-hook.md)
- [Shared shader time uniform buffer](shared-time-uniform-buffer.md)
- [Hook any frame point with Harmony patches on Program and Universe](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-2.md) - how-to article demonstrating this feature
- [Anchors that one mod can consume (and what the next transpiler then sees)](../../../howto/patching/harmony/coexist-with-other-mods-patching-the-same-method/coexist-with-other-mods-patching-the-same-method-2.md) - how-to article demonstrating this feature
- [Let an anchor failure stop the launch (and why that can be right)](../../../howto/patching/safety/refuse-a-patch-and-roll-back-when-the-game-changes/refuse-a-patch-and-roll-back-when-the-game-changes-3.md) - how-to article demonstrating this feature
- [Anchor on a unique neighbouring call, then walk to the target](../../../howto/patching/transpilers/identify-a-call-by-its-argument-il/identify-a-call-by-its-argument-il-2.md) - how-to article demonstrating this feature
- [Insert before the first and after the last instance of a pass boundary](../../../howto/patching/transpilers/insert-calls-into-a-game-method-at-an-il-anchor/insert-calls-into-a-game-method-at-an-il-anchor-4.md) - how-to article demonstrating this feature
- [Replacement that does mod work, then calls the original](../../../howto/patching/transpilers/swap-one-call-inside-a-game-method-with-a-transpiler/swap-one-call-inside-a-game-method-with-a-transpiler-3.md) - how-to article demonstrating this feature
- [Transpile Program.RenderGame around the swapchain render pass](../../../howto/rendering/vulkan-passes/insert-a-custom-pass-into-render-game/insert-a-custom-pass-into-render-game-5.md) - how-to article demonstrating this feature

[^sx-patches]: ShaderExtensions/Patches.cs
[^sx-entry]: ShaderExtensions/ShaderExtensions.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-dll]: KSA.dll IL (build 2026.10.10.5554, disassembled with ilspycmd)
[^brutal-vkdeviceext]: Brutal.VulkanApi.VkDeviceExtensions (decompiled, build 2026.10.10.5554)
[^ksa-rendertarget]: KSA.Rendering.RenderTarget (decompiled, build 2026.10.10.5554)
[^sx-compatplan]: ShaderExtensions/KSA-2026.9.7-compatibility-plan.md
[^sm-runtimemod]: StarMap.Core/ModRepository/RuntimeMod.cs
[^sm-program]: StarMap/Program.cs
