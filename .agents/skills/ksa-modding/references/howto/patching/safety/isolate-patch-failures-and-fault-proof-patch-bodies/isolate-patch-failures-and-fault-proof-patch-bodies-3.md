---
type: How-To
title: Static patch classes that look up the target, log once and stand down
description: KSAGolf keeps each Harmony patch in its own static class with its own id, an Installed flag and a written consequence for being absent; the body catches everything and nulls its delegate on a repeating fault so the patch stands down
category: patching
group: safety
topic: isolate-patch-failures-and-fault-proof-patch-bodies
approach: 3
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksagolf/features/degrading-harmony-patches, mods/ksagolf/features/dual-hook-frame-stepping]
techniques: [harmony-prefix, harmony-postfix, delegate-handoff]
hooks:
  - harmony-prefix KSA.Program.OnFrameCelestials
  - harmony-prefix KSA.Universe.ExecuteNextVehicleSolvers
  - harmony-prefix KSA.ConstraintSim.DetectCollisions
lifecycle: [startup:all-mods-loaded, frame:celestials, frame:after-on-frame]
game_types: [KSA.Program, KSA.Universe, KSA.ConstraintSim]
tags: [harmony, degrade, isolation, fault-proof, delegate, installed-flag]
---

KSAGolf patches eleven engine methods, one of them private and three called on vehicle worker threads, in a game with no
modding API that changes every few weeks. It treats every patch as optional. Each lives in its own static class with its own
Harmony id; each looks its target up and logs once when the target is gone, then runs without it; each body catches
everything; and what a missing patch costs is written down per class. None of them can crash the game.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by KSAGolf in
[Degrading Harmony patches with per-feature ids and signature pins](../../../../mods/ksagolf/features/degrading-harmony-patches.md) and [Dual-hook frame stepping on simulated time](../../../../mods/ksagolf/features/dual-hook-frame-stepping.md).

## Background: why the hook has a fallback

StarMap offers three per-frame hooks. `StarMapBeforeGui` and `StarMapAfterGui` are a prefix and a postfix on
`OnDrawUiFrame` and `OnDrawUiViewports`, which `Program.OnFrame` wraps in `if (DrawUI)` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2265-L2269)[^ksa-program]),
and `DrawUI` is toggled by a player key (F2). The third, `StarMapAfterOnFrame`, lands after the render. KSAGolf therefore adds
a prefix on the private `Program.OnFrameCelestials(double)`, the last phase before `OnPreRender` that is not behind the
`DrawUI` guard ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2319-L2323)). If that patch is absent, the fallback is exactly the old behaviour: the frame postfix keeps
stepping, one frame late on hidden-HUD frames.

## Lifecycle and timing

| Step | Where it runs | Lifecycle |
|---|---|---|
| `Install()` for all six classes | `[StarMapAllModsLoaded]`, the postfix StarMap puts on `ModLibrary.LoadAll` | `startup:all-mods-loaded` |
| Patch bodies | at their targets: `ExecuteNextVehicleSolvers` (`frame:dispatch-solvers`), `ConstraintSim` prefixes on vehicle workers (`physics:vehicle-solver`, `physics:bepu`), `OnFrameCelestials` (`frame:celestials`) | various |
| `Remove()` from `[StarMapUnload]` | process exit, after StarMap has already unpatched everything | shutdown |

Worker-thread prefixes follow the same rule as the main-thread ones; their one log line comes from a vehicle worker, and the
mod's `Log` takes a lock. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Patching game methods that run on worker and pool threads](../../harmony/patch-methods-that-run-on-background-threads/overview.md).

## Steps

1. **One static class per feature, with its own id, state and consequence.** The six classes and what a missing patch costs:

   | Class | Target | Lost if missing |
   |---|---|---|
   | `PreRenderHook` | private `Program.OnFrameCelestials(double)` (prefix) | Hidden-HUD frames step one frame late |
   | `WorldReloadHook` | `Program.OnGameLoaded()` (postfix) | Mod state outlives a save load |
   | `ViewDrawHook` | `SuperMeshRenderSystem.ClearBuckets(IViewport)` (postfix) | Ball, club and course meshes not drawn |
   | `FirstPersonHook` | `KittenEva.UpdateRenderData` and draw methods | First person looks out through the kitten's head |
   | `PadSurface` | `Universe.ExecuteNextVehicleSolvers` (prefix) | The ball rests on terrain, not launch pads |
   | `CourseCollider` | `Universe.ExecuteNextVehicleSolvers`, `ConstraintSim.DetectCollisions`, `Simulate`, `TryResetForPool` | Kittens walk through courses |

2. **Look the target up; do not assume it.** A null result logs one warning naming what is lost and returns with
   `Installed` false. The patch is installed through a delegate the mod hands in, since patches are static:

```csharp
// src/KSAGolf/Ksa/PreRenderHook.cs L44-L47, L55-L91
private static Action<double>? _step;

private static Harmony? _harmony;
private static bool _complained;
// ...
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

Source: [src/KSAGolf/Ksa/PreRenderHook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/PreRenderHook.cs#L44-L91)[^ksagolf-prerenderhook]

3. **Roll back a half-made install under your own id.** `CourseCollider` throws `MissingMethodException` from a helper,
   catches it, logs, and unpatches whatever it had already applied:

```csharp
// src/KSAGolf/Ksa/CourseCollider.cs L63-L101
public static void Install()
{
    if (Installed) return;

    try
    {
        _clutterStatics = AccessTools.Field(typeof(BubbleClutterStatics), "_statics");
        Type? info = _clutterStatics?.FieldType.GetGenericArguments() is [_, var value] ? value : null;
        ConstructorInfo? make = info?.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).FirstOrDefault(c => c.GetParameters().Length == 2);
        if (_clutterStatics is null || make is null)
        {
            Log.Warn("kittens walk through courses: KSA's ground clutter no longer has the _statics a course registers in");
            return;
        }

        ParameterInfo[] args = make.GetParameters();
        _solidClutter = make.Invoke([Activator.CreateInstance(args[0].ParameterType), float.PositiveInfinity]);

        _harmony = new Harmony(HarmonyId);
        Patch(typeof(Universe), nameof(Universe.ExecuteNextVehicleSolvers), nameof(BetweenVehicleSteps));
        Patch(typeof(ConstraintSim), nameof(ConstraintSim.DetectCollisions), nameof(BeforeCollisions));
        Patch(typeof(ConstraintSim), nameof(ConstraintSim.Simulate), nameof(BeforeCollisions));
        Patch(typeof(ConstraintSim), nameof(ConstraintSim.TryResetForPool), nameof(BeforeRecycle));
        Installed = true;
        Log.Info("courses are ground a kitten stands on, as clutter statics in each physics bubble");
    }
    catch (Exception e)
    {
        Log.Warn($"kittens walk through courses: could not hook the physics ({e.GetBaseException().Message})");
        try { _harmony?.UnpatchAll(HarmonyId); } catch { /* Unhooking a half-made patch. */ }
        _harmony = null;
    }
}

private static void Patch(Type type, string method, string prefix)
{
    MethodInfo target = AccessTools.Method(type, method) ?? throw new MissingMethodException(type.Name, method);
    _harmony!.Patch(target, prefix: new HarmonyMethod(typeof(CourseCollider).GetMethod(prefix, BindingFlags.NonPublic | BindingFlags.Static)));
}
```

Source: [src/KSAGolf/Ksa/CourseCollider.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseCollider.cs#L63-L101)[^ksagolf-coursecollider]

4. **Nothing in a patch body may throw, and a repeating fault stands the patch down.** The prefix wraps its work in
   `try`/`catch`; on a failure that would repeat every frame it nulls the delegate and logs once, which is the same outcome as
   a target that never existed:

```csharp
// src/KSAGolf/Ksa/PreRenderHook.cs L110-L130
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

Source: [src/KSAGolf/Ksa/PreRenderHook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/PreRenderHook.cs#L110-L130)

5. **`Remove()` nulls the delegate first.** That stops a stale patch reaching a dead mod; then the class unpatches by its own
   id:

```csharp
// src/KSAGolf/Ksa/PreRenderHook.cs L93-L108
public static void Remove()
{
    _step = null;

    try
    {
        _harmony?.UnpatchAll(HarmonyId);
    }
    catch (Exception e)
    {
        Log.Warn($"could not remove the pre-render hook: {e.Message}");
    }

    _harmony = null;
    Installed = false;
}
```

Source: [src/KSAGolf/Ksa/PreRenderHook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/PreRenderHook.cs#L93-L108)

6. **Pin the public targets so a signature change is a build error.** Each class with a public target has a never-called
   method that really calls it; the private `OnFrameCelestials` cannot be pinned this way
   ([Pin patched members with a never-called method that really calls them](../validate-patch-targets-and-detect-game-update-breakage/validate-patch-targets-and-detect-game-update-breakage-3.md)).

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Ksa/PreRenderHook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/PreRenderHook.cs) | Private-target prefix with fallback and stand-down |
| [src/KSAGolf/Ksa/CourseCollider.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseCollider.cs) | Four-target install with rollback |
| [src/KSAGolf/Ksa/KSAGolfMod.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KSAGolfMod.cs#L74-L108)[^ksagolf-ksagolfmod] | The frame postfix with latch release in `finally` |

## Variations and gotchas

- **A swallowed exception is a silent feature.** The degrade is logged once and then nothing shows it; `Installed` and
  `Trouble` exist for a UI to report, but nothing reads them at the pinned commit.
- **The pin is only as good as the call.** `nameof` compiles to a string and emits no member reference; the call must really
  be a call.
- **Unpatching mid-flight.** `Remove()` from inside the frame runs on the main thread while the `ConstraintSim` prefixes may be
  executing on vehicle workers; whether Harmony tolerates unpatching a method that is mid-call was not checked here.
- **Version.** All eleven targets keep their names and signatures in build 2026.10.10.5554.

## See also

- [CreateClassProcessor per patch class, required versus optional](isolate-patch-failures-and-fault-proof-patch-bodies-1.md), [Consolidated patcher with TryApply and ordered shared seams](isolate-patch-failures-and-fault-proof-patch-bodies-2.md), [Reflection-only mod with parameter-name and field-type pre-checks](isolate-patch-failures-and-fault-proof-patch-bodies-4.md), [Fault counter that pulls every patch after a limit](isolate-patch-failures-and-fault-proof-patch-bodies-5.md)
- [Isolating patch failures and keeping patch bodies from throwing](overview.md)
- [Degrading Harmony patches with per-feature ids and signature pins](../../../../mods/ksagolf/features/degrading-harmony-patches.md), [Dual-hook frame stepping on simulated time](../../../../mods/ksagolf/features/dual-hook-frame-stepping.md)
- [Manual AccessTools lookup with a degrade path when the method is gone](../../internals/patch-a-private-game-method-by-name/patch-a-private-game-method-by-name-2.md) for the `OnFrameCelestials` lookup
- [Removing Harmony patches safely at runtime and at exit](../remove-your-patches-safely-at-runtime-and-at-exit/overview.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksagolf-prerenderhook]: KSAGolf/src/KSAGolf/Ksa/PreRenderHook.cs
[^ksagolf-coursecollider]: KSAGolf/src/KSAGolf/Ksa/CourseCollider.cs
[^ksagolf-ksagolfmod]: KSAGolf/src/KSAGolf/Ksa/KSAGolfMod.cs
