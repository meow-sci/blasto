---
type: How-To
title: Let an anchor failure stop the launch (and why that can be right)
description: ShaderExtensions applies its RenderGame transpiler with PatchAll inside StarMapBeforeMain and lets a missing anchor abort startup, because StarMap does not catch it; compare that with rolling back and note what a shifted local index does not trigger
category: patching
group: safety
topic: refuse-a-patch-and-roll-back-when-the-game-changes
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/shaderextensions/features/rendergame-render-pass-transpiler]
techniques: [harmony-transpiler, starmap-lifecycle]
hooks: [starmap StarMapBeforeMain, harmony-transpiler KSA.Program.RenderGame]
lifecycle: [startup:starmap-before-main]
game_types: [KSA.Program]
tags: [harmony, transpiler, startup, abort, refusal, safety, patchall]
---

Rolling back and running stock is not always the right answer. ShaderExtensions rewrites `Program.RenderGame` so
that code runs just before and just after the swapchain render pass; every pre- and post-ImGui pass depends on it.
It calls `PatchAll()` inside `[StarMapBeforeMain]` with no `try`/`catch`; if an anchor is missing, `ThrowIfInvalid`
throws, Harmony rethrows, StarMap does not catch, and the launch aborts. The mod chooses that over running half-patched.
This article shows the chain, when that choice is defensible, and what it does not protect against.

Prerequisites: a StarMap mod with `0Harmony` and a transpiler. Demonstrated by ShaderExtensions in
[RenderGame transpiler around the swapchain render pass](../../../../mods/shaderextensions/features/rendergame-render-pass-transpiler.md). Compare with [Throw on a bad match, UnpatchAll(own id), publish the instance on success](refuse-a-patch-and-roll-back-when-the-game-changes-1.md) and [One Harmony owner per feature with TryApply, validated handles and runtime flags](refuse-a-patch-and-roll-back-when-the-game-changes-2.md).

## Background: how the game and StarMap behave

`Program.RenderGame` has exactly one `RenderDrawData` call, preceded by a `BeginRenderPass`, and followed by
`EndRenderPass` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4947-L4952)[^ksa-program]). StarMap invokes `[StarMapBeforeMain]` from `RuntimeMod.InitializeMod`
with a bare reflection call, and `Main` has no handler either:

```csharp
// StarMap.Core/ModRepository/RuntimeMod.cs L156-L159
if (modRegistry.TryGetMod(ModId, out var modInfo) && modInfo.BeforeMainAction is MethodInfo action)
{
    action.Invoke(modInfo.ModInstance, []);
}
```

Source: [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L156-L159)[^starmap-runtimemod]

```csharp
// StarMap/Program.cs L9-L29
static async Task Main(string[] args)
{
    var gameConfig = new LoaderConfig();

    if (!gameConfig.TryLoadConfig())
    {
        return;
    }

    AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath("./0Harmony.dll"));

    var gameAssemblyContext = new CoreAssemblyLoadContext(gameConfig.GameLocation);
    using var gameSurveyer = new GameSurveyer(gameAssemblyContext, gameConfig.GameLocation, [.. args, ..gameConfig.GameArguments]);
    if (!gameSurveyer.TryLoadCoreAndGame())
    {
        Console.WriteLine("StarMap - Unable to load mod manager and game.");
        return;
    }

    gameSurveyer.RunGame();
}
```

Source: [StarMap/Program.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/Program.cs#L9-L29)[^starmap-program]

Local tests against Lib.Harmony 2.4.2 show what an anchor failure does inside `PatchAll`: a throwing transpiler makes
`PatchAll` throw `HarmonyException` (base exception: the transpiler's own), classes are applied in declaration order and
`PatchAll` stops at the first failure, so earlier classes stay applied and later ones are never applied.

## Lifecycle and timing

The patch is installed at `startup:starmap-before-main`: StarMap calls it before the game's entry point, so no game object
exists yet and nothing has rendered. A throw here propagates up `InitializeMod` and `Main` and ends the process before
`Program` is constructed; the player sees StarMap exit instead of a game with missing post-processing. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and
[CreateClassProcessor per patch class, required versus optional](../isolate-patch-failures-and-fault-proof-patch-bodies/isolate-patch-failures-and-fault-proof-patch-bodies-1.md).

## Steps

1. **Install in `StarMapBeforeMain`, unguarded.** The entry class is three lines:

```csharp
// ShaderExtensions/ShaderExtensions.cs L6-L15
[StarMapMod]
public class ShaderExtensions
{
    [StarMapBeforeMain]
    public void Setup()
    {
        var harmony = new Harmony("ShaderExtensions");
        harmony.PatchAll();
    }
}
```

Source: [ShaderExtensions/ShaderExtensions.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderExtensions.cs#L6-L15)[^shaderextensions-shaderextensions]

2. **Name every anchor and throw on a miss.** `CodeMatcher.ThrowIfInvalid` turns "no match" into an exception with a
   message that names the missing call:

```csharp
// ShaderExtensions/Patches.cs L151-L166
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
```

Source: [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L151-L166)[^shaderextensions-patches]

3. **Decide on purpose.** Choose abort over rollback when running without the patch would present the user with a
   broken, not merely reduced, experience: here the pre- and post-ImGui passes, the per-window ImGui shaders and every
   dependent shader asset would silently not run. Choose rollback (approaches 1 and 2) when stock behaviour is a
   complete, correct fallback.

4. **Know what is not detected.** A shifted local index is not detected at all. The post-pass insertion hard-codes
   `ldloc.1` and `ldloc.0` for the command buffer and frame resources, and those are only right while the compiler keeps
   `FrameResources` and `CommandBuffer` as the first two locals:

```csharp
// ShaderExtensions/Patches.cs L168-L191
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

matcher.Advance(1);
matcher.InsertAndAdvance(
    new CodeInstruction(OpCodes.Ldloc_1), // commandBuffer2
    new CodeInstruction(OpCodes.Ldloc_0), // frameResources
    new CodeInstruction(OpCodes.Ldc_I4_0),
    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GlobalPostShaderHandler), nameof(GlobalPostShaderHandler.RenderNow)))
);
```

Source: [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L168-L191)

   Capture the operands from matched instructions where possible. The mod's compatibility plan proposes matching the
   `RenderFinalComposite`, `RenderDrawData`, `EndRenderPass` sequence and requiring each anchor exactly once.

5. **Make the message useful.** A mod that aborts should make its exception message say which anchor and which method;
   `ThrowIfInvalid("could not find ... in Program.RenderGame")` does.

## Complete example

| File | Role |
|---|---|
| [ShaderExtensions/ShaderExtensions.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderExtensions.cs) | `PatchAll` from `StarMapBeforeMain` |
| [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L151-L194) | The transpiler and its anchor checks |

## Variations and gotchas

- **Only one mod can use this exact anchor.** After the transpiler runs, `RenderGame` has no `BeginRenderPass` call
  (it moved into `ImGuiPreRender`), so a second mod searching backwards from `RenderDrawData` finds nothing and throws if
  its transpiler runs later in the chain ([Anchors that one mod can consume (and what the next transpiler then sees)](../../harmony/coexist-with-other-mods-patching-the-same-method/coexist-with-other-mods-patching-the-same-method-2.md)).
- **`[HarmonyDebug]` is left on the transpiler,** which enables Harmony's debug log for that patch.
- **A class that fails stops the rest of `PatchAll`.** If you want the rest to apply, use `CreateClassProcessor` per class
  (approach 1 of [CreateClassProcessor per patch class, required versus optional](../isolate-patch-failures-and-fault-proof-patch-bodies/isolate-patch-failures-and-fault-proof-patch-bodies-1.md)).
- **The editor path is not patched.** `Program.RenderEditor` has its own copy of the same four calls.

## See also

- [Throw on a bad match, UnpatchAll(own id), publish the instance on success](refuse-a-patch-and-roll-back-when-the-game-changes-1.md), [One Harmony owner per feature with TryApply, validated handles and runtime flags](refuse-a-patch-and-roll-back-when-the-game-changes-2.md)
- [Refusing a patch and rolling back when the game changes](overview.md)
- [RenderGame transpiler around the swapchain render pass](../../../../mods/shaderextensions/features/rendergame-render-pass-transpiler.md)
- [Writing a Harmony transpiler for a KSA method](../../transpilers/write-a-harmony-transpiler/overview.md), [Identifying the right call in a method that makes several similar calls](../../transpilers/identify-a-call-by-its-argument-il/overview.md)
- Related topic: how-starmap-launches-ksa in the getting-started category

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^starmap-runtimemod]: StarMap/StarMap.Core/ModRepository/RuntimeMod.cs
[^starmap-program]: StarMap/Program.cs
[^shaderextensions-shaderextensions]: ShaderExtensions/ShaderExtensions.cs
[^shaderextensions-patches]: ShaderExtensions/Patches.cs
