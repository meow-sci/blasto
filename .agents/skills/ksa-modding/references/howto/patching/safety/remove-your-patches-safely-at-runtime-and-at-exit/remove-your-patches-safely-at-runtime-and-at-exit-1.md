---
type: How-To
title: UnpatchAll(ownId) from a main-thread hook after nulling delegates
description: Remove only your own Harmony patches by id from a main-thread hook, null the delegates the patches call first so a stale patch cannot reach a dead mod, and know that StarMap's id-less UnpatchAll has already run by the time StarMapUnload methods are called
category: patching
group: safety
topic: remove-your-patches-safely-at-runtime-and-at-exit
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksagolf/features/degrading-harmony-patches, mods/ksaero/features/patch-refusal-and-rollback]
techniques: [harmony-unpatch-by-id, harmony-prefix]
hooks: [starmap StarMapUnload]
lifecycle: [event:starmap-unload, frame:after-on-frame, frame:draw-ui]
game_types: [HarmonyLib.Harmony, KSA.Program]
tags: [harmony, unpatch, shutdown, starmap, delegate, remove]
---

`harmony.UnpatchAll()` with no id removes every mod's patches. Mods should remove only their own, by id, from a main-thread
hook, after nulling any delegate the patch calls. The second fact to know is about shutdown: StarMap calls an id-less
`UnpatchAll()` before it invokes any `[StarMapUnload]` method, so by the time your unload code runs there is usually nothing left
to remove. KSAGolf's `Remove()` and KSAero's `Unload()` both show the pattern, and both note the consequence.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by KSAGolf in [Degrading Harmony patches with per-feature ids and signature pins](../../../../mods/ksagolf/features/degrading-harmony-patches.md) and by KSAero in
[Patch refusal and rollback](../../../../mods/ksaero/features/patch-refusal-and-rollback.md).

## Background: what StarMap does at exit

`StarMapCore.DeInit` unpatches with no id and only then disposes the mod loader, which invokes the `[StarMapUnload]` methods:

```csharp
// StarMap.Core/StarMapCore.cs L31-L35
public void DeInit()
{
    _harmony.UnpatchAll();
    _loader.Dispose();
}
```

Source: [StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35)[^starmap-starmapcore]

```csharp
// StarMap.Core/ModRepository/ModLoader.cs L143-L151
public void Dispose()
{
    foreach (var (_, @object, method) in _modRegistry.Get<StarMapUnloadAttribute>())
    {
        method.Invoke(@object, []);
    }

    _modRegistry.Dispose();
}
```

Source: [StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L143-L151)[^starmap-modloader]

`DeInit` runs from `GameSurveyer.Dispose`, which runs when the `using` scope in StarMap's `Main` ends after the game's entry point
has returned:

```csharp
// StarMap/Program.cs L20-L29
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

Source: [StarMap/Program.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/Program.cs#L20-L29)[^starmap-program]

```csharp
// StarMap/GameSurveyer.cs L51-L61
public void RunGame()
{
    Debug.Assert(_game is not null, "Load needs to be called before running game");

    _game.EntryPoint!.Invoke(null, [_args]);
}

public void Dispose()
{
    _core?.DeInit();
}
```

Source: [StarMap/GameSurveyer.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/GameSurveyer.cs#L51-L61)[^starmap-gamesurveyer]

A local test against Lib.Harmony 2.4.2 shows what each form removes: with two owners on one method, `UnpatchAll("mod.a")` removed
only `mod.a`'s prefix, while a third instance calling `UnpatchAll()` with no id removed every owner's:

```text
// observed output of a local test against Lib.Harmony 2.4.2 (not part of any mod repository)
both installed:
  prefix from mod a
  prefix from mod b
  original
mod.a calls UnpatchAll("mod.a"):
  prefix from mod b
  original
mod.a re-installed; a third instance calls UnpatchAll() with no id:
  original
```

## Lifecycle and timing

- `[StarMapUnload]` (`event:starmap-unload`) runs at process exit, after `Program.Main` has returned and after the id-less
  `UnpatchAll()`. It is shutdown cleanup, not a hot unload while flying.
- Removal that matters happens during play, from a main-thread hook such as `[StarMapAfterOnFrame]`
  (`frame:after-on-frame`) or `[StarMapAfterGui]` (`frame:draw-ui`): a fault limit, a settings toggle, a feature switched off.
  See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Give each patch class its own Harmony id and an `Installed` flag.** `PreRenderHook` uses `com.ksagolf.prerender`; unpatching
   by that id limits the removal to this class's patches, so pulling the first-person patches leaves the ball drawn:

```csharp
// src/KSAGolf/Ksa/PreRenderHook.cs L38-L53
internal static class PreRenderHook
{
    private const string HarmonyId = "com.ksagolf.prerender";

    // The mod's own step. Static because Harmony patches a method, not an instance, and there is
    // one mod. Null while unloaded, which is what stops a stale patch reaching a dead mod.
    private static Action<double>? _step;

    private static Harmony? _harmony;
    private static bool _complained;

    /// <summary>Whether the step runs before the render on a frame that draws no UI.</summary>
    public static bool Installed { get; private set; }

    /// <summary>Why it is not installed. Empty when it is, or when nothing has tried.</summary>
    public static string Trouble { get; private set; } = "";
```

Source: [src/KSAGolf/Ksa/PreRenderHook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/PreRenderHook.cs#L38-L53)[^ksagolf-prerenderhook]

2. **Null the delegate first, then unpatch by id.** The patch is static and calls a delegate the mod handed in. Nulling it first
   is what stops a stale patch reaching a dead mod, even if the unpatch itself throws:

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

3. **Make the removal tolerant.** `CourseCollider.Remove` wraps `UnpatchAll` in `try`/`catch` ("Unloading anyway") and resets
   its state whatever happens:

```csharp
// src/KSAGolf/Ksa/CourseCollider.cs L103-L110
public static void Remove()
{
    try { _harmony?.UnpatchAll(HarmonyId); } catch { /* Unloading anyway. */ }
    _harmony = null;
    Installed = false;
    _wanted = null;
    _ready = null;
}
```

Source: [src/KSAGolf/Ksa/CourseCollider.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseCollider.cs#L103-L110)[^ksagolf-coursecollider]

4. **Call it from a main-thread hook.** The fault-limit path removes every class from inside `[StarMapAfterGui]` or
   `[StarMapAfterOnFrame]` ([Fault counter that pulls every patch after a limit](../isolate-patch-failures-and-fault-proof-patch-bodies/isolate-patch-failures-and-fault-proof-patch-bodies-5.md)). Never from a worker thread.

5. **Do not depend on `[StarMapUnload]` to remove patches.** KSAero's unload is explicit about it: the call is kept for hosts
   that unload mods while the game keeps running, such as a test harness, and is a harmless no-op under StarMap:

```csharp
// KSAero/Mod.cs L45-L51
[StarMapUnload]
public void Unload()
{
    _harmony?.UnpatchAll(HarmonyId);
    _harmony = null;
    DefaultCategory.Log.Info("[KSAero] Unloaded.");
}
```

Source: [KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs#L45-L51)[^ksaero-mod]

   KSA-AdvancedFlightComputer and purrTTY reach the same conclusion ([Feature-gated Harmony patch sets with rollback](../../../../mods/ksa-advancedflightcomputer/features/feature-gated-patch-sets.md),
   [Failure-isolated Harmony patching with reload-safe statics](../../../../mods/purrtty/features/failure-isolated-harmony-patching.md)).

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Ksa/PreRenderHook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/PreRenderHook.cs) | Install, `Remove` with delegate-first |
| [src/KSAGolf/Ksa/CourseCollider.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseCollider.cs#L103-L110) | Tolerant `Remove` |
| [KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs#L45-L51) | Unload that expects to find nothing |

## Variations and gotchas

- **Never call the id-less `UnpatchAll()` mid-session.** It removes patches of every mod in the process.
- **At exit, device and game objects are already torn down.** `[StarMapUnload]` code must do only managed cleanup and file
  writes; GPU and audio releases act on torn-down systems.
- **Unpatching a method that is mid-call.** KSAGolf's fault-limit removal runs on the main thread while `ConstraintSim` prefixes
  may be running on vehicle workers; whether Harmony tolerates that was not checked. Prefer removing between frames from the main
  thread, and keep the patch bodies safe to run against a nulled delegate.
- **Paths read at unload.** StarMap's id-less `UnpatchAll()` also removes its own patch on the documents folder getter, so paths
  read from it at unload point at the default folder (KSA-Public-Telemetry's final config save does this; see [Hook the frame with StarMap lifecycle attributes](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-1.md)).

## See also

- [Lazy install and removal of one patch while it is in use](remove-your-patches-safely-at-runtime-and-at-exit-2.md), [Unpatch by patch method on a shared Harmony instance](remove-your-patches-safely-at-runtime-and-at-exit-3.md)
- [Removing Harmony patches safely at runtime and at exit](overview.md)
- [Degrading Harmony patches with per-feature ids and signature pins](../../../../mods/ksagolf/features/degrading-harmony-patches.md), [Patch refusal and rollback](../../../../mods/ksaero/features/patch-refusal-and-rollback.md)
- [Throw on a bad match, UnpatchAll(own id), publish the instance on success](../refuse-a-patch-and-roll-back-when-the-game-changes/refuse-a-patch-and-roll-back-when-the-game-changes-1.md)
- Related topic: clean-up-on-game-exit

[^starmap-starmapcore]: StarMap/StarMap.Core/StarMapCore.cs
[^starmap-modloader]: StarMap/StarMap.Core/ModRepository/ModLoader.cs
[^starmap-program]: StarMap/Program.cs
[^starmap-gamesurveyer]: StarMap/GameSurveyer.cs
[^ksagolf-prerenderhook]: KSAGolf/src/KSAGolf/Ksa/PreRenderHook.cs
[^ksagolf-coursecollider]: KSAGolf/src/KSAGolf/Ksa/CourseCollider.cs
[^ksaero-mod]: KSAero/Mod.cs
