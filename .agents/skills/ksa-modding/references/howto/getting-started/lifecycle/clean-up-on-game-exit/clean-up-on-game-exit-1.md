---
type: How-To
title: Clean up with StarMapUnload and ProcessExit, and reset static state
description: Release what your mod owns at shutdown, know what is already gone when StarMapUnload runs, cover the Environment.Exit paths it skips, and reset statics so a second init starts clean
category: getting-started
group: lifecycle
topic: clean-up-on-game-exit
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/starmap/features/unload-and-shutdown
  - mods/ksa-measuretools/features/resilient-patching-and-diagnostics
techniques: [starmap-lifecycle]
hooks: [starmap StarMapUnload, reflection System.AppDomain.ProcessExit]
lifecycle: [event:shutdown, thread:background]
game_types: [KSA.Program, KSA.App, KSA.ModLibrary]
tags: [starmap, unload, shutdown, cleanup, process-exit, statics]
---

`[StarMapUnload]` is documented as "called when KSA is unloaded". In practice it runs from the launcher's `using`
cleanup **after the game has completely shut itself down**, so it is a place for managed clean-up (flush files, stop
threads and child processes, close sockets), not for touching the game. This article shows what runs before it, what
it cannot do, how to cover the exit paths that skip it, and how KSA-MeasureTools resets every static so a second
init would start clean.

## Background: how the game does it

On a normal quit (window close, anything that calls `RequestExit`):[current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L39-L75)[StarMap/GameSurveyer.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/GameSurveyer.cs#L58-L61)

1. `App.Run` sees the exit code at the top of its loop and returns.
2. KSA's `Main` leaves its `using Program` scope: `Program.Dispose` waits for the job systems, idles the Vulkan
   device, disposes renderers, viewports, textures and meshes, shuts down ImGui, calls `Glfw.Terminate` and releases
   audio.
3. StarMap's `RunGame` returns; the `using var gameSurveyer` scope ends and `GameSurveyer.Dispose` calls
   `IStarMapCore.DeInit`.
4. `StarMapCore.DeInit` calls `_harmony.UnpatchAll()` with **no id**, then `ModLoader.Dispose`, which invokes every
   `[StarMapUnload]` method in initialization order and clears the registry:[StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L25-L35)[StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L143-L151)

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

The signature is `public void Name()` with no parameters.[StarMap.API/BaseAttributes.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.API/BaseAttributes.cs#L10-L132)

## Lifecycle and timing

`event:shutdown`, main thread, after the last frame. At that point:

- Window, Vulkan device and swapchain, the ImGui backend and the audio system are **gone**. Job systems were drained
  but not torn down.
- `UnpatchAll(null)` already removed the patches of **every** Harmony instance in the process, so your own
  `UnpatchAll(yourId)` finds nothing (KSAero's still runs, harmlessly).
- StarMap's instance-path override is one of those patches, so `Constants.DocumentsFolderPath` already returns the
  default documents folder; resolve save paths earlier and keep them ([Manual Harmony prefix on Constants.DocumentsFolderPath](../../loader/how-starmap-launches-ksa/how-starmap-launches-ksa-3.md)).
- Your mod's load context and statics still exist (contexts are never unloaded).

**It does not run** when the process ends through `Environment.Exit`: closing the window while the loading screen,
the "MOD FOUND" popups or the system picker is up (each calls `Environment.Exit(0)`), or choosing Restart or closing
the window in StarMap's restart popup.[current/decomp/KSA/Loading.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Loading.cs#L91-L110)[current/decomp/KSA/ConfirmMods.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConfirmMods.cs#L60-L75)[current/decomp/KSA/SelectSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SelectSystem.cs#L50-L62) An unhandled exception on a
non-main thread also terminates the process without it. An exception thrown on the main thread is caught further up
the stack by the reflection call and StarMap's async `Main`, so the `using` blocks still dispose (derived from .NET
exception semantics, not tested).

## Steps

1. **Add a public parameterless `[StarMapUnload]` method** and keep it to managed work:

   ```csharp
   // KSAero/KSAero/Mod.cs L45-L51
       [StarMapUnload]
       public void Unload()
       {
           _harmony?.UnpatchAll(HarmonyId);
           _harmony = null;
           DefaultCategory.Log.Info("[KSAero] Unloaded.");
       }
   ```

2. **Do not use it to restore game state through your patches** (already removed), and do not touch rendering,
   ImGui or audio handles (disposed). Flush files, stop your own threads and child processes, close sockets, write
   player data.
3. **Reset every static** so a future in-process reload (or a second `Initialize`) starts clean. MeasureTools does
   it in one method, with no ImGui calls:[MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs#L80-L99)

   ```csharp
   // MeasureTools/Mod.cs L80-L99
       [StarMapUnload]
       public void Unload()
       {
           _harmony?.UnpatchAll(_harmony.Id);
           _harmony = null;

           MeasureColors.SaveIfDirty();
           MeasureColors.Reset();
           MeasureState.Reset();
           MeasureWindow.ResetStatic();
           MeasureOverlay.Reset();
           MeshFeatureCache.Reset();
           Patch_MouseButton.Reset();
           DebugConfig.Reset();
           LogHelper.Reset();
   #if DEBUG
           PerfTracker.Reset();
   #endif
           DefaultCategory.Log.Info("[MeasureTools] Unloaded.");
       }
   ```

   Under StarMap 4dc60f8 the only unload is the one at process exit, so the resets matter only for a loader that
   unloads mid-session; the habit is cheap.
4. **Cover the paths that skip it.** Save data when it changes; for anything that must run on every exit, also
   subscribe to .NET's process-exit event, which is raised on `Environment.Exit` too. No mod in the corpus does
   this; the form is plain .NET (adapted):

   ```csharp
   // adapted: standard .NET, not from a mod repo
   [StarMapAllModsLoaded]
   public void OnFullyLoaded()
   {
       AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushLogsAndConfig();
   }
   ```

   Make `FlushLogsAndConfig` idempotent, because both it and `StarMapUnload` may run on a normal exit.
5. **Keep each Unload short and exception-proof.** An exception from one Unload method propagates out of StarMap's
   `Main` and skips later mods' Unload methods. Wrap the body in `try`/`catch` ([Keep mod exceptions out of KSA's frame loop](../contain-exceptions-in-starmap-hooks/overview.md)).
6. **Flush last.** If your logger batches writes, flush it at the very end of Unload (KSACars does this with
   `Log.Shutdown()`; see [Log from a mod: per-mod log file, game log category and log-once](../write-a-per-mod-log-file/overview.md)).

## Complete example

| File | Role |
|---|---|
| [MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs) | Unload that saves dirty colours, then resets every static |
| [KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs) | Minimal Unload |
| [StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs), [StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs) | Order of unpatch and unload |

## Variations and gotchas

- **Do not rely on it for player data.** Save when data changes; Unload is skipped on `Environment.Exit` paths and
  after a crash on another thread.
- **The 'unload' naming misleads.** Nothing is unloaded: assemblies and statics live until the process ends.
- **Native resources owned by the game are gone.** Fine to release your own native handles (QEMU child processes,
  sockets), not the game's.
- **A restart skips Unload.** See [Harmony prefix on ModLibrary.PrepareAll plus Process.Start --restarted](./clean-up-on-game-exit-2.md) for the relaunch path.

## See also

- [Harmony prefix on ModLibrary.PrepareAll plus Process.Start --restarted](./clean-up-on-game-exit-2.md), [Clean up on game exit and relaunch the game from a mod](./overview.md)
- [Unload hook and shutdown order](../../../../mods/starmap/features/unload-and-shutdown.md), [Per-patch Harmony application, drift checks, once-only logging and debug perf scopes](../../../../mods/ksa-measuretools/features/resilient-patching-and-diagnostics.md)
- [Keep mod exceptions out of KSA's frame loop](../contain-exceptions-in-starmap-hooks/overview.md), [How StarMap launches KSA and loads your mod](../../loader/how-starmap-launches-ksa/overview.md), [Game loop and mod lifecycle](../game-loop-and-lifecycle/overview.md)
