---
type: How-To
title: Manual AccessTools lookup with a degrade path when the method is gone
description: Look up the private Program.OnFrameCelestials by name with AccessTools.Method; a null result records a Trouble string and logs the consequence while another hook keeps the mod working, and the patch class stays installable even if the target is absent
category: patching
group: internals
topic: patch-a-private-game-method-by-name
approach: 2
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksarmory/features/dual-hook-simulation-step
  - mods/ksagolf/features/dual-hook-frame-stepping
  - mods/ksagolf/features/degrading-harmony-patches
techniques: [harmony-prefix, accesstools-lookup, degrade-path]
hooks: [harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterGui, starmap StarMapAfterOnFrame]
lifecycle: [startup:all-mods-loaded, frame:celestials, frame:draw-ui, frame:after-on-frame]
game_types: [KSA.Program]
tags: [harmony, prefix, private-method, accesstools, degrade, frame-hook]
---

When the private method is an optional hook with a fallback, do not let Harmony's attribute resolution decide what happens if it disappears. Look it up yourself with `AccessTools.Method`, treat a null result
as an expected outcome (record a `Trouble` string, log the consequence once, return), and keep the other hook working. KSArmory and KSAGolf both hook the private `Program.OnFrameCelestials(double)` this way so a
step runs before the render on frames with the UI hidden, and both fall back to a StarMap hook if the method is ever renamed.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated in [One simulation step per frame from whichever hook runs first](../../../../mods/ksarmory/features/dual-hook-simulation-step.md), [Dual-hook frame stepping on simulated time](../../../../mods/ksagolf/features/dual-hook-frame-stepping.md) and [Degrading Harmony patches with per-feature ids and signature pins](../../../../mods/ksagolf/features/degrading-harmony-patches.md).

## Background: how the game does it

StarMap's three per-frame hooks do not cover a hidden-UI frame before the render. `StarMapBeforeGui` and `StarMapAfterGui` are a prefix and postfix on `OnDrawUiFrame` and `OnDrawUiViewports`, which
`Program.OnFrame` wraps in `if (DrawUI)`; `StarMapAfterOnFrame` runs after the render. `OnFrameCelestials` is the last phase before `OnPreRender` that is not behind that guard:

```csharp
// current/decomp/KSA/Program.cs L2263-L2276
OnFrameViewports(dtPlayer);
Profiler.MainThread.Begin(Profiler.Tag.DrawUi);
if (DrawUI)
{
	OnDrawUiFrame(dtPlayer);
	OnDrawUiViewports(dtPlayer);
}
if (DrawUI)
{
	OnDrawUiThreadSafe(dtPlayer);
}
DrawFps();
OnDrawUiConsole(dtPlayer);
Profiler.MainThread.End();
```

Source: [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2263-L2276)[^ksa-program]

```csharp
// current/decomp/KSA/Program.cs L2319-L2323
Profiler.MainThread.Begin(Profiler.Tag.OnFrameCelestials);
OnFrameCelestials(dtPlayer);
Profiler.MainThread.End();
Profiler.MainThread.Begin(Profiler.Tag.Render);
var (result, acquiredFrame2) = (FrameAcquireResult)(ref OnPreRender(dtPlayer));
```

Source: [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2319-L2323)

It resolves the camera's nearby body and updates the planet's shader data for the frame, so a step taken *after* it prepares the planet against a camera the mod is about to move. That is why the hook is a **prefix**
on it; a postfix would not put a hidden-UI frame on the same side as a UI frame.

## Lifecycle and timing

Install: `[StarMapAllModsLoaded]` (`startup:all-mods-loaded`). Run: `frame:celestials`, on the main thread, every frame (including frames with the HUD hidden). With the UI shown the mod steps at `[StarMapAfterGui]`
(`frame:draw-ui`), which is ahead of `OnFrameCelestials`; a per-frame latch makes the prefix a no-op on every frame the GUI pass already claimed. `[StarMapAfterOnFrame]` (`frame:after-on-frame`) is the fallback. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
and [Hook any frame point with Harmony patches on Program and Universe](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-2.md).

## Steps

1. **Look the method up, specifying the parameter list.** `AccessTools.Method(typeof(Program), "OnFrameCelestials", [typeof(double)])` returns null if the method is gone or has a different signature:

```csharp
// src/KSArmory/Ksa/PreRenderHook.cs L56-L92
public static void Install(Action<double> step)
{
    _step = step;

    if (Installed) return;

    try
    {
        // Private, so by name: there is no way to write this that a compiler would check, which
        // is exactly why the failure has to degrade rather than break.
        MethodInfo? target = AccessTools.Method(typeof(Program), "OnFrameCelestials",
                                                [typeof(double)]);

        if (target is null)
        {
            Trouble = "KSA has no Program.OnFrameCelestials(double) to hook";
            Log.Warn($"stepping after the render on frames with no UI: {Trouble}");
            return;
        }

        MethodInfo prefix = typeof(PreRenderHook).GetMethod(
            nameof(BeforeOnFrameCelestials), BindingFlags.NonPublic | BindingFlags.Static)!;

        _harmony = new Harmony(HarmonyId);
        _harmony.Patch(target, prefix: new HarmonyMethod(prefix));

        Installed = true;
        Trouble = "";
        Log.Info("hidden-UI frames step before the render, via Program.OnFrameCelestials");
    }
    catch (Exception e)
    {
        Trouble = e.Message;
        Log.Warn($"could not hook the pre-render step; frames with no UI will be drawn one "
                 + $"frame late ({e.Message})");
    }
}
```

Source: [src/KSArmory/Ksa/PreRenderHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/PreRenderHook.cs#L56-L92)[^ksarmory-prerenderhook]

2. **On null, record the reason and degrade.** `Trouble` is a public string for a UI to show; the log line states what the player loses ("stepping after the render on frames with no UI"). `Installed` stays false and the
   StarMap hook keeps running, so the fallback is the behaviour without the patch.

3. **Keep the body fault-proof.** Nothing in the prefix may throw: it runs inside the engine's frame loop. On a failure that would repeat each frame the prefix nulls its delegate and logs once
   ([Static patch classes that look up the target, log once and stand down](../../safety/isolate-patch-failures-and-fault-proof-patch-bodies/isolate-patch-failures-and-fault-proof-patch-bodies-3.md)):

```csharp
// src/KSArmory/Ksa/PreRenderHook.cs L111-L131
// A no-op on every frame the GUI pass already claimed, which is every frame with a UI. The
// latch is what makes that true rather than a condition here: asking whether the UI drew would
// be a second answer to a question one place already owns.
private static void BeforeOnFrameCelestials(double deltaTime)
{
    try
    {
        _step?.Invoke(deltaTime);
    }
    catch (Exception e)
    {
        // Stand down rather than throw again next frame from inside the engine's loop. The
        // frame postfix has the mod's own fault handling, and it is one hook later -- so
        // giving up here is the same degradation a refused patch gives, arrived at differently.
        _step = null;

        if (_complained) return;
        _complained = true;
        Log.Error("the pre-render step failed; the frame postfix takes it from here", e);
    }
}
```

Source: [src/KSArmory/Ksa/PreRenderHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/PreRenderHook.cs#L111-L131)

4. **Hand state in with a delegate, because patches are static.** `Install(Action<double> step)` stores the step the prefix will invoke; `Remove()` nulls it first
   ([UnpatchAll(ownId) from a main-thread hook after nulling delegates](../../safety/remove-your-patches-safely-at-runtime-and-at-exit/remove-your-patches-safely-at-runtime-and-at-exit-1.md)).

5. **Choose the hook with a latch.** KSAGolf gives the same `StepOnce` to three hooks (`AfterGui`, the `OnFrameCelestials` prefix, `AfterOnFrame`) and lets the first one that fires each frame claim it through a latch,
   releasing it in a `finally` in the one hook that always runs ([Fault counter that pulls every patch after a limit](../../safety/isolate-patch-failures-and-fault-proof-patch-bodies/isolate-patch-failures-and-fault-proof-patch-bodies-5.md)).

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/PreRenderHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/PreRenderHook.cs) | Lookup, install, degrade, stand-down |
| [src/KSAGolf/Ksa/PreRenderHook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/PreRenderHook.cs)[^ksagolf-prerenderhook] | The same class in KSAGolf |

## Variations and gotchas

- **Private targets cannot be pinned at compile time.** The public patches in these mods each have a never-called method that really calls the target ([Pin patched members with a never-called method that really calls them](../../safety/validate-patch-targets-and-detect-game-update-breakage/validate-patch-targets-and-detect-game-update-breakage-3.md)); a private
  target has none, which is why this one degrades instead.
- **The fallback must be real.** If the prefix is missing the step runs one frame late on hidden-UI frames; that is acceptable and stated in the log.
- **Match the parameter list,** not just the name, so a new overload with the same name is not picked.
- **Not `PatchAll`.** A missing target with an attribute patch throws; with a manual lookup it is a null you handle.

## See also

- [HarmonyPatch attribute with a string method name, applied by PatchAll](patch-a-private-game-method-by-name-1.md), [Explicit parameter arrays with MakeByRefType for overloaded and by-reference targets](patch-a-private-game-method-by-name-3.md), [Attribute argument types that bind by assignability](patch-a-private-game-method-by-name-4.md), [TargetMethods with per-name AccessTools lookups and the __args array](patch-a-private-game-method-by-name-5.md), [Reading a private instance field with a triple-underscore parameter](patch-a-private-game-method-by-name-6.md), [Prefer __instance and a public member over a field injector that matches nothing](patch-a-private-game-method-by-name-7.md)
- [Patching private and overloaded game methods by name](overview.md)
- [One simulation step per frame from whichever hook runs first](../../../../mods/ksarmory/features/dual-hook-simulation-step.md), [Dual-hook frame stepping on simulated time](../../../../mods/ksagolf/features/dual-hook-frame-stepping.md), [Degrading Harmony patches with per-feature ids and signature pins](../../../../mods/ksagolf/features/degrading-harmony-patches.md)
- [Isolating patch failures and keeping patch bodies from throwing](../../safety/isolate-patch-failures-and-fault-proof-patch-bodies/overview.md)
- Related topic: draw-ui-while-the-hud-is-hidden

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksarmory-prerenderhook]: KSArmory/src/KSArmory/Ksa/PreRenderHook.cs
[^ksagolf-prerenderhook]: KSAGolf/src/KSAGolf/Ksa/PreRenderHook.cs
