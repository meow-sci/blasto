---
type: Game Feature
title: Clean up on game exit and relaunch the game from a mod
description: What runs, and what is already gone, when KSA shuts down under StarMap, which exit paths skip StarMapUnload, and how a mod relaunches the game process
category: getting-started
group: lifecycle
topic: clean-up-on-game-exit
demonstrated_by:
  - mods/starmap/features/unload-and-shutdown
  - mods/ksa-measuretools/features/resilient-patching-and-diagnostics
  - mods/starmap/features/new-mods-restart-flow
  - mods/starmap/features/startup-modal-popup
lifecycle: [event:shutdown, thread:background, startup:asset-load]
game_types: [KSA.Program, KSA.ModLibrary, KSA.ModEntry, KSA.Popup]
tags: [shutdown, unload, cleanup, restart, relaunch, process-exit]
---

A mod that owns files, threads, child processes or static caches has to release them when the game ends. Under
StarMap the shutdown path is unusual: by the time `[StarMapUnload]` runs, the game and every Harmony patch are
gone. The opposite problem, restarting the process after a startup-only change, is solved by the same loader with a
prefix on `ModLibrary.PrepareAll`.

## Engine background

| Step | What happens | Source |
|---|---|---|
| `App.Run` returns | the loop exits when an exit code is set | [current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L39-L75) |
| KSA `Main` leaves its `using` | `Program.Dispose` waits for jobs, disposes Vulkan, ImGui, GLFW, audio | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1693-L1703) |
| `StarMapCore.DeInit` | `UnpatchAll()` with no id (every patch in the process), then `ModLoader.Dispose` runs each `[StarMapUnload]` | [StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L25-L35) |
| `ModLibrary.PrepareAll` | the prefix point for the relaunch check; entries found this launch are `New` and disabled | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L373-L447) |
| `Environment.Exit` callers | `Loading`, `ConfirmMods`, `SelectSystem` windows exit the process directly | skips `StarMapUnload` |

Lifecycle value: `event:shutdown` for clean-up; the relaunch happens at `startup:asset-load` (constructor, before
any asset loads). Frame and startup tables: [Game loop and mod lifecycle](../game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [StarMapUnload plus ProcessExit for exit paths that skip it](./clean-up-on-game-exit-1.md) | StarMap, KSA-MeasureTools | `starmap-lifecycle` | `event:shutdown` | Simple and safe for managed clean-up; cannot touch the game; skipped on `Environment.Exit` and after non-main-thread crashes, so pair it with `ProcessExit` and save-on-change |
| [Harmony prefix on ModLibrary.PrepareAll plus Process.Start --restarted](./clean-up-on-game-exit-2.md) | StarMap | `harmony-prefix` | `startup:asset-load` | Gives a clean fresh process cheaply at the only moment nothing is loaded; needs a self-pumped popup and drops CLI flags unless forwarded |

## Rules of thumb

- Never touch rendering, ImGui or audio in Unload; their native handles are gone.
- Save player data when it changes; do not depend on Unload.
- Resolve paths that depend on `Constants.DocumentsFolderPath` early; after the instance-path patch is removed it
  returns the default folder.
- If an exit path matters, subscribe to `AppDomain.ProcessExit` as well.

## See also

- [Keep mod exceptions out of KSA's frame loop](../contain-exceptions-in-starmap-hooks/overview.md), [How StarMap launches KSA and loads your mod](../../loader/how-starmap-launches-ksa/overview.md), [Choose and use the right StarMap lifecycle hook](../choose-a-starmap-startup-hook/overview.md),
  [Log from a mod: per-mod log file, game log category and log-once](../write-a-per-mod-log-file/overview.md)
- [Unload hook and shutdown order](../../../../mods/starmap/features/unload-and-shutdown.md), [Per-patch Harmony application, drift checks, once-only logging and debug perf scopes](../../../../mods/ksa-measuretools/features/resilient-patching-and-diagnostics.md),
  [Restart when new mods are enabled](../../../../mods/starmap/features/new-mods-restart-flow.md), [Startup modal popup with a self-pumped frame](../../../../mods/starmap/features/startup-modal-popup.md)
