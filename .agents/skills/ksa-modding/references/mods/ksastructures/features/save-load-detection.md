---
type: Mod Feature
title: Noticing a save load with a Program.OnGameLoaded postfix
description: A Harmony postfix on Program.OnGameLoaded latches a flag that the mod's next frame step consumes to drop state from the replaced world, because StarMap has no load hook and the flight scene never ends across a load
mod: ksastructures
category: persistence
techniques: [harmony-postfix, starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded, harmony-postfix KSA.Program.OnGameLoaded]
lifecycle: [startup:all-mods-loaded, startup:game-loaded, event:save, frame:draw-ui]
game_types: [KSA.Program, KSA.UncompressedSave, KSA.Universe, KSA.GameSaves, KSA.SimStep]
tags: [save, load, harmony, starmap, lifecycle, latch]
---

KSAStructures holds world state between frames: the lift's riders, queued moves, the vehicles it
has looked up, and the engine's last simulated step it has already integrated. Loading a save
replaces every vehicle and resets the universe clock, but nothing tells a StarMap mod that it
happened. The mod patches the one method the game calls after a load, latches a flag there, and lets
its next frame step do the reset.[^mod-reload]

## How it works

1. **No StarMap hook, no scene change.** StarMap's attributes are `StarMapBeforeMain`,
   `StarMapImmediateLoad`, `StarMapAllModsLoaded`, `StarMapUnload`, `StarMapAfterOnFrame`,
   `StarMapBeforeGui` and `StarMapAfterGui`; none fires on a load.[^starmap-baseattributes][^starmap-onframeattributes][^starmap-onguiattributes] The flight
   scene does not end either: `Universe.LoadSystem` runs once at startup, and a save only replaces what
   is inside the current system, so a test like "editor closed and a system exists" stays true across
   the load.[^mod-reload][^ksa-universe]
2. **What a load does.** `UncompressedSave.Load` reads the save, calls `Universe.DeserializeSave`
   and then `Program.OnGameLoaded()`, its only caller.[^ksa-uncompressedsave] `DeserializeSave` sets
   `SimulationSpeed` to 0, joins every job system with `JobSystems.WaitAll`, destroys all vehicles and
   physics bubbles, unfollows every camera and resets the last and next sim steps to the save's time
   with `DeltaTime` 0. `OnGameLoaded` itself only closes the Esc menu and the settings, save and
   vehicle-save windows and clears the menu pause.[^ksa-universe][^ksa-program]
3. **A postfix that only sets a flag.** `WorldReloadHook.Install` patches
   `Program.OnGameLoaded()` from `[StarMapAllModsLoaded]` under its own Harmony id. The postfix sets
   `_pending = true` inside a `try`; nothing is torn down from inside the engine's load path.[^mod-reload][^mod-entry]
4. **Consumed once by the next step.** `ConsumeReload()` returns true once per load to whichever
   caller asks first. The mod's `StepOnce` asks at its top and calls
   `KsaWorld.ResetSimStepTracking()`, which forgets the last consumed universe time so the jump to the
   save's time is not integrated as one enormous step.[^mod-entry][^mod-ksaworld]
5. **Pinning a public target.** `PinTheSignature() => Program.OnGameLoaded()` is never called; it
   exists so the compiler emits a reference to the patched method, turning a signature change in a
   later KSA build into a build error instead of a patch that silently stops applying.[^mod-reload]
6. **Degrades, never breaks.** A missing target or a refused patch is logged once
   (`Trouble`), and the cost is one load's worth of stale state.[^mod-reload]

## Lifecycle and timing

| When | Where | What |
|---|---|---|
| `startup:all-mods-loaded` | `[StarMapAllModsLoaded]` -> `WorldReloadHook.Install` | Patch `Program.OnGameLoaded` |
| `event:save` / `startup:game-loaded` | wherever the load is triggered, after `Universe.DeserializeSave` | Postfix latches `_pending` |
| the mod's next step (normally `frame:draw-ui`) | `StepOnce` -> `ConsumeReload` | Reset sim-step tracking |
| `[StarMapUnload]` | `WorldReloadHook.Remove` | Clear the flag and unpatch |

The load runs on the main thread from whoever triggers it. The in-game Save/Load window loads
through a `ConfirmActionPopup` (`GameSave.LoadOnConfirm`), and popups are drawn by `Popup.DrawAll` at
the top of `Program.OnDrawUiFrame`, so a UI load is seen by the same frame's `[StarMapAfterGui]`
step.[^ksa-gamesave][^ksa-program] A load from the console (drawn in `OnDrawUiConsole`) or from
the mod's own bridge (in `[StarMapAfterOnFrame]`) happens after that frame's step and is seen on the
next one. Because `DeserializeSave` joins the solvers and destroys every bubble itself, the world the
next step sees is consistent; the step only has to stop trusting what it cached.

## Key code

| Path | Role |
|---|---|
| [src/KSAStructures/Ksa/WorldReloadHook.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/WorldReloadHook.cs#L31-L123) | Install, postfix, consume, pin |
| [src/KSAStructures/Ksa/KSAStructuresMod.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KSAStructuresMod.cs#L106-L117) | The step that consumes it |
| [src/KSAStructures/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KsaWorld.cs#L110-L114) | `ResetSimStepTracking` |

## Snippets

The latch, its consumer and the compile-time pin:

```csharp
// src/KSAStructures/Ksa/WorldReloadHook.cs L52-L58, L112-L122
    public static bool ConsumeReload()
    {
        if (!_pending) return false;

        _pending = false;
        return true;
    }
    // ...
    private static void AfterGameLoaded()
    {
        // Setting a bool cannot fail, but this runs in the engine's load path where an exception
        // is the game rather than a log line, and the guard costs nothing.
        try { _pending = true; }
        catch { }
    }

    // Never called. It exists so the compiler emits a reference to the patched method, which is
    // what turns a signature change in KSA into a build error here rather than a reload that silently stops being noticed.
    private static void PinTheSignature() => Program.OnGameLoaded();
```

Where the step acts on it:

```csharp
// src/KSAStructures/Ksa/KSAStructuresMod.cs L111-L117
        KsaWorld.InvalidateCensus();

        if (WorldReloadHook.ConsumeReload())
        {
            Log.Info("a save was loaded - forgetting the previous world");
            KsaWorld.ResetSimStepTracking();
        }
```

The game's only call to `OnGameLoaded`:

```csharp
// current/decomp/KSA/UncompressedSave.cs L80-L81
		Universe.DeserializeSave(UniverseData);
		Program.OnGameLoaded();
```

## Game APIs touched

| API | Decomp | Use |
|---|---|---|
| `Program.OnGameLoaded` (public static) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2599-L2606) | Patched; closes menus only |
| `UncompressedSave.Load` | [current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L80-L81) | Its only caller |
| `Universe.DeserializeSave` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2430-L2462) | Pauses, joins jobs, destroys vehicles, resets the sim step |
| `GameSave.LoadOnConfirm` | [current/decomp/KSA/GameSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSave.cs#L53-L56) | UI loads go through a confirm popup |

## Pitfalls and version notes

- **Latch, do not act, in the postfix.** The postfix runs inside the engine's load path, sometimes
  in the middle of a UI draw. Clearing mod state that a later part of the same frame still uses is
  the failure the latch avoids.
- **A load pauses the game.** `DeserializeSave` sets the simulation speed to 0, so the step after a
  load usually integrates nothing; anything keyed to "the world moved" sees no motion until the
  player unpauses.
- **`Universe.DeserializeSave` is the alternative target** (KSA-MeasureTools patches it; see the
  [lifecycle reference](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-2.md)).
  `OnGameLoaded` runs after it, so a postfix there sees the finished world in both cases.
- **Built against 2026.10.7.5541**; `OnGameLoaded` and its caller are unchanged in 2026.10.10.5554.

## See also

- [One step per frame across three hooks](./frame-step-plumbing.md) - the step that consumes the flag
- [Office furniture as single-part craft](./office-furniture-craft.md) - the craft a load removes and the mod sets down again
- [Pin the OnGameLoaded signature at compile time so a game update breaks the build, not the patch](../../../howto/persistence/saves/detect-save-load/detect-save-load-2.md) - how-to article demonstrating this feature

[^mod-reload]: src/KSAStructures/Ksa/WorldReloadHook.cs
[^mod-entry]: src/KSAStructures/Ksa/KSAStructuresMod.cs
[^mod-ksaworld]: src/KSAStructures/Ksa/KsaWorld.cs
[^starmap-baseattributes]: StarMap: StarMap.API/BaseAttributes.cs
[^starmap-onframeattributes]: StarMap: StarMap.API/OnFrameAttributes.cs
[^starmap-onguiattributes]: StarMap: StarMap.API/OnGuiAttributes.cs
[^ksa-uncompressedsave]: KSA.UncompressedSave (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-gamesave]: KSA.GameSave (decompiled, build 2026.10.10.5554)
