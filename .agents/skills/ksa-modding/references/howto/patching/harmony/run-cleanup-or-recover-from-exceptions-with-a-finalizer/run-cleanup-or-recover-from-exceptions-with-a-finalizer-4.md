---
type: How-To
title: Prefix, postfix and finalizer as a transaction around loads
description: Wrap UncompressedSave.Load, Universe.DeserializeSave and Universe.LoadSystem in prefixes that defer and open a transaction, postfixes that restore, and finalizers that close it and turn a silent native read failure into a reported exception
category: patching
group: harmony
topic: run-cleanup-or-recover-from-exceptions-with-a-finalizer
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/lib-native-save-hooks]
techniques: [harmony-prefix, harmony-postfix, harmony-finalizer]
hooks:
  - harmony-postfix KSA.GameSave.Populate
  - harmony-postfix KSA.UncompressedSave.Write
  - harmony-prefix KSA.UncompressedSave.Load
  - harmony-finalizer KSA.UncompressedSave.Load
  - harmony-prefix KSA.Universe.DeserializeSave
  - harmony-prefix KSA.Universe.LoadSystem
lifecycle: [event:save, frame:input, frame:draw-ui]
game_types: [KSA.UncompressedSave, KSA.GameSave, KSA.Universe, KSA.JobSystems]
tags: [harmony, finalizer, prefix, postfix, save, load, transaction, runoriginal]
---

Unscience stores per-save scene state beside KSA's own save and has to know when a save was written and when
a load started, finished or failed. It cannot rely on the game to report failures: since build 5482
`UncompressedSave.Load` logs an unreadable file and returns, and `Write` returns `false` instead of throwing.
The mod's `NativeSaveHooks` therefore treats each native call as a transaction. A prefix opens it (and may
defer the call), a postfix observes success, and a finalizer closes it and reports the failure the game
swallowed.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by unscience in
[Native save and load hooks that bracket every KSA save transaction](../../../../mods/unscience/features/lib-native-save-hooks.md).

## Background: how the game does it

| Native call | What matters for a hook |
|---|---|
| `GameSave.Populate()` (virtual) | `UniverseData.Create()` snapshots the universe ([current/decomp/KSA/GameSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSave.cs#L37-L40)[^ksa-gamesave]) |
| `UncompressedSave.Write()` returns `bool` | `false` if the folder could not be replaced or the files could not be written ([current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L108-L135)[^ksa-uncompressedsave]) |
| `UncompressedSave.Load()` returns `void` | A read failure is caught, logged, and the method returns before `DeserializeSave` ([current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L45-L81)) |
| `Universe.DeserializeSave(UniverseData)` | Waits on all job systems and rebuilds the world from the data ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2430-L2440)[^ksa-universe]) |

The read-failure branch is the reason a postfix cannot report a failed load: the original returned normally.

```csharp
// current/decomp/KSA/UncompressedSave.cs L61-L81
try
{
	UniverseData = UniverseData.LoadFrom(this);
}
catch (Exception exception)
{
	LogCategory log2 = DefaultCategory.Log;
	LogCategory logCategory2 = log2;
	LogString<Error> message2 = new LogString<Error>(26, 1, log2, out shouldAppend);
	if (shouldAppend)
	{
		message2.AppendLiteral("save '");
		message2.AppendFormatted(base.Id);
		message2.AppendLiteral("' could not be read:");
	}
	logCategory2.Error(message2, "Load", "C:\\prototype-planet\\KSA\\UncompressedSave.cs", 55);
	DefaultCategory.Log.Error(exception, "Load", "C:\\prototype-planet\\KSA\\UncompressedSave.cs", 56);
	return;
}
Universe.DeserializeSave(UniverseData);
Program.OnGameLoaded();
```

Source: [current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L61-L81)

## Lifecycle and timing

Everything runs on the main thread. Saves are synchronous wherever the caller is: a console `save` runs
inside `Glfw.PollEvents` (`frame:input`), the GAME SAVES window runs inside `OnDrawUiFrame`
(`frame:draw-ui`); the hooks capture and write immediately. Loads are *always moved* to the `PrepareFrame`
handoff, after the `Apply*Solvers` calls and before the next dispatch, by re-queueing the same native call and
returning `false` from the prefix. At that point the old world can be reset without a worker still owning it;
see [Mutate simulation state safely relative to the physics step](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md) and [Physics-safe vessel mutation at the PrepareFrame solver handoff](../../../../mods/unscience/features/lib-physics-frame-hook.md). Native `DeserializeSave` itself joins all workers
(`JobSystems.WaitAll()`), but only inside the original, after the prefix, so mod cleanup that runs in the
prefix has to join explicitly.

## Steps

1. **Bind each target by exact name and parameter list and record every pair.** `Apply` installs the frame hook
   first (idempotently), then patches. The `Patch` helper records `(target, patch method)` for each patch so
   `Remove` can unpatch exactly those methods and leave other owners alone. A failure halfway through calls
   `Remove` and rethrows:

```csharp
// ksa-abstractions.lib/Persistence/NativeSaveHooks.cs L28-L52
public static void Apply(Harmony harmony)
{
    if (Installed.Count != 0) return;
    try
    {
        // A load can originate late in UI rendering. Without the early-frame seam,
        // installing synchronous reset hooks would free resources still referenced by UI.
        PhysicsFrameHook.Apply(harmony);
        Patch(harmony, typeof(GameSave), nameof(GameSave.Populate), Type.EmptyTypes,
            postfix: nameof(Populated));
        Patch(harmony, typeof(UncompressedSave), nameof(UncompressedSave.Write), Type.EmptyTypes,
            postfix: nameof(Saved));
        Patch(harmony, typeof(UncompressedSave), nameof(UncompressedSave.Load), Type.EmptyTypes,
            prefix: nameof(LoadingSave), finalizer: nameof(FinishedLoading));
        Patch(harmony, typeof(Universe), nameof(Universe.DeserializeSave), new[] { typeof(UniverseData) },
            prefix: nameof(ResetBeforeLoad), postfix: nameof(RestoreAfterLoad), finalizer: nameof(FinishedDirectLoad));
        Patch(harmony, typeof(Universe), nameof(Universe.LoadSystem), new[] { typeof(string) },
            prefix: nameof(ResetBeforeSystem), finalizer: nameof(FinishedDirectLoad));
    }
    catch
    {
        Remove(harmony);
        throw;
    }
}
```

Source: [ksa-abstractions.lib/Persistence/NativeSaveHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/NativeSaveHooks.cs#L28-L52)[^unscience-nativesavehooks]

2. **Use `__runOriginal` in a postfix.** A postfix can ask whether the original ran. After a skipping prefix it is
   `false` (checked against Lib.Harmony 2.4.2), so the hook raises `Capturing` or `Written` only when the game
   actually ran:

```csharp
// ksa-abstractions.lib/Persistence/NativeSaveHooks.cs L78-L93
private static void Populated(GameSave __instance, bool __runOriginal)
{
    // Capture only detached data. Native Populate already read the published state;
    // joining workers here would not change the native snapshot's capture time.
    if (__runOriginal) Invoke(Capturing, __instance, "capture");
}

private static void Saved(UncompressedSave __instance, bool __result, bool __runOriginal)
{
    if (!__runOriginal) return;
    // Since KSA 5482 a failed save returns false instead of throwing, and the previous save
    // folder can survive intact. Writing the sidecar there would pair this session's feature
    // state with the old universe, so only a successful native write publishes it.
    if (__result) Invoke(Written, __instance, "write");
    else Invoke(WriteFailed, __instance, "write failure");
}
```

Source: [ksa-abstractions.lib/Persistence/NativeSaveHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/NativeSaveHooks.cs#L78-L93)

3. **Open the transaction in the prefix; defer if you are not at the safe point.** `LoadingSave` returns `true`
   (let the game proceed) when the editor is open or the frame hook is unavailable, so KSA's own refusal runs. When
   not replaying a deferred change it queues `__instance.Load` and returns `false`. On replay it sets `__state =
   true`, bumps the file-load depth, clears the last error and raises `Loading`:

```csharp
// ksa-abstractions.lib/Persistence/NativeSaveHooks.cs L95-L110
private static bool LoadingSave(UncompressedSave __instance, out bool __state)
{
    __state = false;
    if (KSA.Program.IsEditorOpen || !FrameHookAvailable()) return true;
    if (!PhysicsFrameHook.IsReplayingWorldChange)
    {
        PhysicsFrameHook.EnqueueWorldChange(__instance.Load);
        return false;
    }
    __state = true;
    _fileLoadDepth++;
    _nativeLoadReached = false;
    LastLoadError = null;
    Invoke(Loading, __instance, "preflight");
    return true;
}
```

Source: [ksa-abstractions.lib/Persistence/NativeSaveHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/NativeSaveHooks.cs#L95-L110)

4. **Close it in a finalizer and report what the game swallowed.** The finalizer decrements the depth and builds
   the failure: the native exception if there was one, otherwise a synthesized `InvalidDataException` when
   `DeserializeSave` was never reached (the silent read failure). It returns `__exception` so it never swallows:

```csharp
// ksa-abstractions.lib/Persistence/NativeSaveHooks.cs L112-L125
private static Exception? FinishedLoading(UncompressedSave __instance, Exception? __exception, bool __state)
{
    if (__state)
    {
        _fileLoadDepth--;
        // Since KSA 5482 an unreadable save is logged and Load returns before replacing the world.
        Exception? failure = __exception ?? (_nativeLoadReached ? null
            : new InvalidDataException($"KSA could not read save '{__instance.Id}'; the current scene was left unchanged."));
        LastLoadError = failure;
        Invoke(LoadFinished, "finish load");
        if (failure != null) Invoke(LoadFailed, failure, "load failure");
    }
    return __exception;
}
```

Source: [ksa-abstractions.lib/Persistence/NativeSaveHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/NativeSaveHooks.cs#L112-L125)

5. **Mark progress from the nested call.** `Universe.DeserializeSave` sets `_nativeLoadReached = true` in its
   prefix; that flag is what step 4 reads. The same prefix validates, joins the workers and resets old
   ownership before the native rebuild, and the postfix raises `Restoring` only when both the reset ran and the
   original executed:

```csharp
// ksa-abstractions.lib/Persistence/NativeSaveHooks.cs L137-L157
private static bool ResetBeforeLoad(UniverseData universeData, out bool __state)
{
    __state = false;
    _nativeLoadReached = true;
    // Match the original's no-world early failure without touching the current session.
    if (Universe.CurrentSystem == null || universeData == null || !FrameHookAvailable()) return true;
    if (!PhysicsFrameHook.IsReplayingWorldChange)
    {
        PhysicsFrameHook.EnqueueWorldChange(() => Universe.DeserializeSave(universeData));
        return false;
    }
    if (_fileLoadDepth == 0) LastLoadError = null;
    NativeSavePreflight.Validate(universeData);
    __state = ResetAtJoinedBoundary();
    return true;
}

private static void RestoreAfterLoad(bool __state, bool __runOriginal)
{
    if (__state && __runOriginal) Invoke(Restoring, "restore");
}
```

Source: [ksa-abstractions.lib/Persistence/NativeSaveHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/NativeSaveHooks.cs#L137-L157)

6. **Report a direct load failure once.** `FinishedDirectLoad` is the finalizer for `DeserializeSave` and
   `LoadSystem`; it reports only when no file-load transaction is in progress, so a nested failure is reported
   once:

```csharp
// ksa-abstractions.lib/Persistence/NativeSaveHooks.cs L127-L135
private static Exception? FinishedDirectLoad(Exception? __exception)
{
    if (__exception != null && _fileLoadDepth == 0)
    {
        LastLoadError = __exception;
        Invoke(LoadFailed, __exception, "direct load failure");
    }
    return __exception;
}
```

Source: [ksa-abstractions.lib/Persistence/NativeSaveHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/NativeSaveHooks.cs#L127-L135)

7. **Isolate callbacks.** Each delegate runs in its own `try`/`catch`, so one feature cannot block the native
   save or load or the other features ([ksa-abstractions.lib/Persistence/NativeSaveHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/NativeSaveHooks.cs#L205-L223)).

## Complete example

| File | Role |
|---|---|
| [ksa-abstractions.lib/Persistence/NativeSaveHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/NativeSaveHooks.cs) | All prefixes, postfixes and finalizers |
| [ksa-abstractions.lib/Persistence/NativeSavePreflight.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/NativeSavePreflight.cs)[^unscience-nativesavepreflight] | Validation before the old world is destroyed |

## Variations and gotchas

- **A postfix cannot see a swallowed failure.** Report failures from the finalizer and a flag set by a nested
  patch, not from a postfix.
- **Only the latest deferred request survives.** A load clicked twice in one frame runs once.
- **`LoadSystem` gets a reset but no restore.** In this build its only native caller is startup, with no current
  system, so the deferred path is reached only by mods that call it.
- **Standalone hosts never install these hooks.** Saves made there carry no sidecar.
- **Preflight cannot predict every fault.** A later exception inside `DeserializeSave` happens after reset;
  the coordinator then keeps the incoming records for recovery.
- **If the frame hook cannot install, loads run unpatched.** `FrameHookAvailable()` logs "native load continues
  without scene restore".

## See also

- [Restore what a prefix changed around one game call](run-cleanup-or-recover-from-exceptions-with-a-finalizer-1.md), [Swallow an exception and finish the method's remaining work](run-cleanup-or-recover-from-exceptions-with-a-finalizer-2.md), [Prefix with __state and a finalizer that restores an edited input](run-cleanup-or-recover-from-exceptions-with-a-finalizer-3.md), [ThreadStatic override set in try/finally around the mod's own submission](run-cleanup-or-recover-from-exceptions-with-a-finalizer-5.md), [Thread-static construction context scoping a transpiler and a prefix](run-cleanup-or-recover-from-exceptions-with-a-finalizer-6.md), [Mark the asker in a Priority.First prefix, clear it in a Priority.Last finalizer](run-cleanup-or-recover-from-exceptions-with-a-finalizer-7.md)
- [Harmony finalizers, cleanup and scoped overrides](overview.md)
- [Native save and load hooks that bracket every KSA save transaction](../../../../mods/unscience/features/lib-native-save-hooks.md)
- Related task: save-mod-state-with-the-game-save

[^ksa-gamesave]: KSA.GameSave (decompiled, build 2026.10.10.5554)
[^ksa-uncompressedsave]: KSA.UncompressedSave (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^unscience-nativesavehooks]: unscience/ksa-abstractions.lib/Persistence/NativeSaveHooks.cs
[^unscience-nativesavepreflight]: unscience/ksa-abstractions.lib/Persistence/NativeSavePreflight.cs
