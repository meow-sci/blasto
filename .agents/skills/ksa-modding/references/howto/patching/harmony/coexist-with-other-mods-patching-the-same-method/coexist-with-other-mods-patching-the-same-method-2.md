---
type: How-To
title: Anchors that one mod can consume (and what the next transpiler then sees)
description: Harmony re-runs every registered transpiler on the original IL whenever any mod patches a method, so a later transpiler sees the earlier one's output; choose anchors that survive earlier rewriting and fail by name when they do not
category: patching
group: harmony
topic: coexist-with-other-mods-patching-the-same-method
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/shaderextensions/features/rendergame-render-pass-transpiler
  - mods/ksaero/features/computedrag-call-site-transpiler
techniques: [harmony-transpiler]
hooks: [harmony-transpiler KSA.Program.RenderGame, harmony-transpiler KSA.PhysicsStates.ComputeDerivatives]
lifecycle: [startup:starmap-before-main, startup:all-mods-loaded, frame:render, physics:vehicle-solver]
game_types: [KSA.Program, KSA.PhysicsStates]
tags: [harmony, transpiler, coexistence, anchors, il]
---

Two transpilers on the same method do not simply apply one after another. Harmony rebuilds the patched method from
the *original* IL each time any patch on it is added or removed, running every registered transpiler in
priority order, each seeing the previous one's output. A transpiler that consumes its anchor (replaces the call it
matched) can leave the next mod with nothing to find. This article shows two KSA mods that each consume an anchor,
what a second mod then sees, and how to choose anchors and failure messages so coexistence fails loudly and by name.

Prerequisites: a StarMap mod with `0Harmony` and a working transpiler (see [Writing a Harmony transpiler for a KSA method](../../transpilers/write-a-harmony-transpiler/overview.md)).
Demonstrated by ShaderExtensions in [RenderGame transpiler around the swapchain render pass](../../../../mods/shaderextensions/features/rendergame-render-pass-transpiler.md) and KSAero in
[ComputeDrag call-site transpiler](../../../../mods/ksaero/features/computedrag-call-site-transpiler.md).

## Background: how the game does it

`Program.RenderGame` opens one legacy Vulkan render pass on the swapchain image, runs the final composite, draws the
ImGui data and ends the pass. The four calls sit in a row:

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

ShaderExtensions has to record work just before the pass opens and just after it closes, with no method boundary at
either point. KSAero retargets one `ComputeDrag` call inside `PhysicsStates.ComputeDerivatives`, which has two calls
to the same method (ocean and air).

## Lifecycle and timing

Transpilers run when the patch is applied, once for each patch added or removed afterwards. ShaderExtensions applies
`PatchAll` from `[StarMapBeforeMain]` (`startup:starmap-before-main`); KSAero patches from
`[StarMapAllModsLoaded]` (`startup:all-mods-loaded`). Load order therefore decides which transpiler is registered
first, and a transpiler that is registered earlier still runs again when a later mod patches the method. The rewritten
code runs every flight frame in `frame:render` (RenderGame) and on vehicle workers in `physics:vehicle-solver`
(ComputeDerivatives). See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Observe the re-run and the visibility rule.** The following is the output of a local test against Lib.Harmony
   2.4.2: transpiler A (Harmony id `a`) retargets a call from `Foo` to `Bar`; then transpiler B (id `b`) is added.

```text
// observed output of a local test against Lib.Harmony 2.4.2 (not part of any mod repository)
patch with A (Harmony id a):
  transpiler A call #1: replaced 1 Foo call(s) with Bar
then patch with B (Harmony id b), default priority:
  transpiler A call #2: replaced 1 Foo call(s) with Bar
  transpiler B call #1: sees Foo=0 Bar=1
then unpatch B by id:
  transpiler A call #3: replaced 1 Foo call(s) with Bar
```

   Three consequences: A runs again whenever any patch on the method changes (including a removal), B sees A's
   output, not the original IL, and a transpiler must therefore be idempotent over the *original* input.

2. **Consume an anchor knowingly (ShaderExtensions).** Its transpiler finds the one `RenderDrawData` call, walks backwards
   to the nearest `BeginRenderPass`, removes that call and inserts a call to its own `ImGuiPreRender`, which does the
   mod's work and then issues `BeginRenderPass` itself. It then finds the last `EndRenderPass` from the end and
   inserts a call after it:

```csharp
// ShaderExtensions/Patches.cs L151-L166, L185-L191
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
    matcher.Advance(1);
    matcher.InsertAndAdvance(
        new CodeInstruction(OpCodes.Ldloc_1), // commandBuffer2
        new CodeInstruction(OpCodes.Ldloc_0), // frameResources
        new CodeInstruction(OpCodes.Ldc_I4_0),
        new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GlobalPostShaderHandler), nameof(GlobalPostShaderHandler.RenderNow)))
    );
```

Source: [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L151-L191)[^shaderextensions-patches]

   After this runs, `RenderGame` no longer contains a `BeginRenderPass` call. A second mod whose transpiler searches
   backwards from `RenderDrawData` for that call finds nothing, and if it throws on a miss (as it should), the throw
   surfaces from whichever mod's `Patch` call triggers the rebuild.

3. **Count and fail by name (KSAero).** KSAero's transpiler rewrites a call only when it passes the atmospheric
   density and throws if the number of replacements is not exactly one. If another transpiler already rewrote that
   call before KSAero's ran, the count is zero and the message says so:

```csharp
// KSAero/AirForcePatch.cs L61-L81
private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
{
    List<CodeInstruction> code = instructions.ToList();
    MethodInfo replacement = AccessTools.Method(typeof(AirForcePatch), nameof(ComputeAirForces));
    int replaced = 0;

    for (int i = 0; i < code.Count; i++)
    {
        if (code[i].Calls(ComputeDrag!) && PassesAtmosphericDensity(code, i))
        {
            code[i].operand = replacement;
            replaced++;
        }
    }

    // Anything else means the method has changed shape, and stock aero is the safe answer.
    if (replaced != 1)
        throw new InvalidOperationException($"Expected one atmospheric ComputeDrag call in ComputeDerivatives, found {replaced}.");

    return code;
}
```

Source: [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L61-L81)[^ksaero-airforcepatch]

4. **Choose anchors that survive.** Prefer an anchor on something the other mod is unlikely to consume (a unique
   neighbouring call, a field load that feeds the argument) over the instruction you are going to rewrite. When two
   mods need the same seam, the second can search for the first mod's *output* (its own helper call) rather than the
   original instruction, or one of the mods can expose a registration API so there is one transpiler and many users (see
   [Idempotent shared installs with a registration API](coexist-with-other-mods-patching-the-same-method-3.md)).

5. **Make the failure message name the method, the anchor and the count.** This adapted helper (not from a mod,
   modelled on KSAero's count check) is enough to turn "my patch did nothing" into a log line a player can paste:

```csharp
// adapted from KSAero/AirForcePatch.cs (count check)
static int FindOnce(List<CodeInstruction> code, Func<CodeInstruction, bool> match, string method, string anchor)
{
    int found = -1, count = 0;
    for (int i = 0; i < code.Count; i++)
        if (match(code[i])) { found = i; count++; }
    if (count != 1)
        throw new InvalidOperationException($"{method}: expected exactly one {anchor}, found {count}. " +
                                            "The game changed or another mod rewrote this method first.");
    return found;
}
```

## Complete example

| File | Role |
|---|---|
| [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L151-L194) | `RenderGame` transpiler that consumes `BeginRenderPass` |
| [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L52-L93) | Apply and counted transpiler |

## Variations and gotchas

- **A throw in a transpiler aborts the `Patch` call that triggered the rebuild.** In `[StarMapBeforeMain]` that means
  the launch aborts, because StarMap does not catch (see [Let an anchor failure stop the launch (and why that can be right)](../../safety/refuse-a-patch-and-roll-back-when-the-game-changes/refuse-a-patch-and-roll-back-when-the-game-changes-3.md)).
- **Hard-coded local indices are an unseen coupling.** ShaderExtensions inserts `ldloc.1` and `ldloc.0` for
  the command buffer and frame resources; another mod adding a local earlier shifts them without failing the match.
  Capture the operands from matched instructions where possible.
- **Idempotence.** Because the transpiler is re-run, anything it logs prints again. AFC logs through
  once-only helpers for this reason ([CreateClassProcessor per patch class, required versus optional](../../safety/isolate-patch-failures-and-fault-proof-patch-bodies/isolate-patch-failures-and-fault-proof-patch-bodies-1.md)).
- **Only one mod can use this exact anchor.** The ShaderExtensions page states it plainly; a second mod must target
  something else or ask ShaderExtensions to do the work.

## See also

- [Priority.First prefixes and Priority.Last finalizers around shared seams](coexist-with-other-mods-patching-the-same-method-1.md), [Idempotent shared installs with a registration API](coexist-with-other-mods-patching-the-same-method-3.md), [One shared tick with per-feature enable flags set from TryApply results](coexist-with-other-mods-patching-the-same-method-4.md), [One worker-side postfix merging every writer](coexist-with-other-mods-patching-the-same-method-5.md)
- [Coexisting with other mods that patch the same method](overview.md)
- [RenderGame transpiler around the swapchain render pass](../../../../mods/shaderextensions/features/rendergame-render-pass-transpiler.md), [ComputeDrag call-site transpiler](../../../../mods/ksaero/features/computedrag-call-site-transpiler.md)
- [Writing a Harmony transpiler for a KSA method](../../transpilers/write-a-harmony-transpiler/overview.md), [Identifying the right call in a method that makes several similar calls](../../transpilers/identify-a-call-by-its-argument-il/overview.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^shaderextensions-patches]: ShaderExtensions/Patches.cs
[^ksaero-airforcepatch]: KSAero/AirForcePatch.cs
