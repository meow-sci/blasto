---
type: Mod Feature
title: Unload hook and shutdown order
description: StarMapUnload runs after KSA's Main has returned and Program.Dispose has destroyed the renderer, and after StarMap has removed every Harmony patch in the process; Environment.Exit paths skip it
mod: starmap
category: getting-started
techniques: [starmap-lifecycle]
hooks: [starmap StarMapUnload, call IStarMapCore.DeInit (GameSurveyer.Dispose)]
lifecycle: [event:shutdown]
game_types: [KSA.Program, KSA.App, KSA.ConfirmMods, KSA.Loading, KSA.SelectSystem]
tags: [starmap, lifecycle, shutdown, unload, harmony, cleanup]
---

`[StarMapUnload]` is documented as "called when KSA is unloaded". Concretely it runs from the
launcher's `using` cleanup after the game has completely shut itself down, so it is a place for
managed cleanup (flush files, stop threads and child processes, close sockets), not for touching
the game.

## How it works

Normal quit (window close button, or anything that calls `RequestExit`):[^ksa-app][^ksa-program]

1. `App.Run` sees the exit code at the top of its loop and returns.
2. KSA's `Main` leaves its `using Program program` scope: `Program.Dispose` waits for all job
   systems, idles the Vulkan device, disposes every renderer, viewport, texture and mesh, shuts
   down the ImGui backend, disposes the renderer, calls `Glfw.Terminate` and releases audio.
3. `Main` returns; StarMap's `RunGame` returns.
4. StarMap's `Main` leaves the scope of `using var gameSurveyer`; `GameSurveyer.Dispose` calls
   `IStarMapCore.DeInit`.[^sm-program][^sm-gamesurveyer]
5. `StarMapCore.DeInit` calls `_harmony.UnpatchAll()` with no id, then `ModLoader.Dispose`.[^sm-starmapcore]
6. `ModLoader.Dispose` invokes every `[StarMapUnload]` method in initialization order, then clears
   the registry.[^sm-modloader]

The signature is `public void Name()` with no parameters.[^sm-baseattributes]

## Lifecycle and timing

`event:shutdown`, on the main thread, after the last frame. At that point:

- The window (`Glfw.Terminate`), Vulkan device and swapchain, the ImGui backend and ImPlot context,
  and the audio system are gone. The job systems were drained with `JobSystems.WaitAll` but are not
  torn down.
- Harmony's `UnpatchAll(null)` removes the patches of every Harmony instance in the process, so a
  mod's own patches are already gone; calling `UnpatchAll("my.id")` again, as KSAero does, is a
  harmless no-op.[^ksaero-mod]
- StarMap's own [instance path override](instance-path-override.md) is one of those patches, so with
  `-InstancePath` or `STARMAP_INSTANCE_PATH` set, `Constants.DocumentsFolderPath` already returns the
  default documents folder here; a final save that reads it then lands in the wrong folder
  (KSA-Public-Telemetry's last config save does). Resolve such paths earlier and keep them.
- The mod's load context and statics still exist; load contexts are never unloaded and are released
  only with the process.
- Game data objects (`ModLibrary` collections, `Universe` statics) are still reachable as managed
  objects, but anything that wraps a GPU or native resource has been disposed.

It does not run when the process ends through `Environment.Exit`: closing the window while the
loading screen, the "MOD FOUND" popups or the system picker is up (each calls
`Environment.Exit(0)`), or choosing Restart or closing the window in StarMap's restart
popup.[^ksa-loading][^ksa-confirmmods][^ksa-selectsystem] An exception that escapes the frame loop on the main thread is caught further up the stack (the
reflection call in `RunGame` wraps it, and StarMap's `Main` is `async Task`, whose
compiler-generated state machine catches everything), so the `using` blocks it unwinds through
still dispose: KSA's `Program.Dispose`, then StarMap's `gameSurveyer`, which means `DeInit` and
`StarMapUnload` run before the rethrown error terminates the process (derived from C# and .NET
exception semantics, not tested). An unhandled exception on any other thread terminates the process
without it. Code that must run on every exit can additionally subscribe to
`AppDomain.CurrentDomain.ProcessExit`, which .NET raises on `Environment.Exit`.

## Key code

| Path | Role |
|---|---|
| [StarMap/Program.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/Program.cs#L20-L28) | `using var gameSurveyer` scope around the game |
| [StarMap/GameSurveyer.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/GameSurveyer.cs#L58-L61) | `Dispose` -> `DeInit` |
| [StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35) | Unpatch everything, then dispose the loader |
| [StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L143-L151) | Invoke `StarMapUnload` methods |

## Snippets

```csharp
// StarMap.Core/StarMapCore.cs L25-L35
public void Init()
{
    _loader.Init();
    _harmony.PatchAll(typeof(StarMapCore).Assembly);
}

public void DeInit()
{
    _harmony.UnpatchAll();
    _loader.Dispose();
}
```

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

## Game APIs touched

| Member | File | Use |
|---|---|---|
| `Program.Dispose` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1362-L1487) | Runs before `StarMapUnload`; destroys GPU, window and audio state |
| `App.Run` / `RequestExit` | [current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L39-L88) | Normal exit path |
| `Loading.OnFrame` | [current/decomp/KSA/Loading.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Loading.cs#L91-L110) | `Environment.Exit(0)` if the window closes during loading |
| `ConfirmMods.OnFrame` | [current/decomp/KSA/ConfirmMods.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConfirmMods.cs#L62-L74) | `Environment.Exit(0)` if the window closes on the "MOD FOUND" popup |
| `SelectSystem` | [current/decomp/KSA/SelectSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SelectSystem.cs#L59) | `Environment.Exit(0)` if the window closes on the system picker |

## Pitfalls and version notes

- **Do not touch rendering, ImGui or audio** in `StarMapUnload`; their native handles are gone.
- **Do not rely on it for saving player data** that must survive every exit path; save when the
  data changes, or also hook `ProcessExit`.
- **Your patches are already removed** by StarMap's global unpatch, so a mod cannot use Unload to
  "restore" game state through its own patches.
- **Exceptions** from an Unload method propagate out of StarMap's `Main` like any other
  uncaught exception; later mods' Unload methods are then skipped.

## See also

- [Host KSA in-process from a launcher executable](host-process-launch.md)
- [Frame lifecycle hooks](frame-lifecycle-hooks.md)
- [Restart when new mods are enabled](new-mods-restart-flow.md)
- [Clean up with StarMapUnload and ProcessExit, and reset static state](../../../howto/getting-started/lifecycle/clean-up-on-game-exit/clean-up-on-game-exit-1.md) - how-to article demonstrating this feature
- [Hook the frame with StarMap lifecycle attributes](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-1.md) - how-to article demonstrating this feature

[^sm-program]: StarMap/Program.cs
[^sm-gamesurveyer]: StarMap/GameSurveyer.cs
[^sm-starmapcore]: StarMap.Core/StarMapCore.cs
[^sm-modloader]: StarMap.Core/ModRepository/ModLoader.cs
[^sm-baseattributes]: StarMap.API/BaseAttributes.cs
[^ksaero-mod]: KSAero/Mod.cs
[^ksa-program]: KSA.Program (decompiled)
[^ksa-app]: KSA.App (decompiled)
[^ksa-confirmmods]: KSA.ConfirmMods (decompiled)
[^ksa-loading]: KSA.Loading (decompiled)
[^ksa-selectsystem]: KSA.SelectSystem (decompiled)
