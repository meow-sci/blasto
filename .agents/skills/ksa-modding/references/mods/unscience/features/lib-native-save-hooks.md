---
type: Mod Feature
title: Native save and load hooks that bracket every KSA save transaction
description: Harmony patches on GameSave.Populate, UncompressedSave.Write and Load, and Universe.DeserializeSave and LoadSystem publish capture, write, reset and restore callbacks and defer every world replacement to the PrepareFrame handoff
mod: unscience
category: persistence
techniques: [harmony-prefix, harmony-postfix, harmony-finalizer, save-sidecar]
hooks:
  - harmony-postfix KSA.GameSave.Populate
  - harmony-postfix KSA.UncompressedSave.Write
  - harmony-prefix KSA.UncompressedSave.Load
  - harmony-finalizer KSA.UncompressedSave.Load
  - harmony-prefix KSA.Universe.DeserializeSave
  - harmony-postfix KSA.Universe.DeserializeSave
  - harmony-finalizer KSA.Universe.DeserializeSave
  - harmony-prefix KSA.Universe.LoadSystem
  - harmony-finalizer KSA.Universe.LoadSystem
  - harmony-transpiler KSA.Program.PrepareFrame (installed first through PhysicsFrameHook.Apply)
lifecycle: [event:save, event:load, frame:input, frame:draw-ui, frame:prepare]
game_types:
  - KSA.GameSave
  - KSA.UncompressedSave
  - KSA.Universe
  - KSA.UniverseData
  - KSA.GameSaves
  - KSA.JobSystems
  - KSA.SystemLibrary
  - KSA.ModLibrary
  - KSA.Program
tags: [saves, load, sidecar, harmony, world-replacement, threading, preflight]
---

Unscience stores per-save scene state (welds, decals, lights, scaled craft, cameras and so on)
beside KSA's own save and rebuilds it on load, without a second save browser. `NativeSaveHooks`
is the game-facing half of that system: a static class in `ksa-abstractions.lib` that patches the
five native methods a KSA save or load passes through and turns them into eight plain callbacks
(`Capturing`, `Written`, `WriteFailed`, `Loading`, `Resetting`, `Restoring`, `LoadFinished`,
`LoadFailed`). It knows nothing about JSON or features; the host plugs the
[scene save coordinator](./lib-scene-save-coordinator.md) into those callbacks.[^native-save-hooks][^unscience-saves]

Mechanism in one line: postfixes observe the save path where it already runs; prefixes on the load
path refuse to run outside the [PrepareFrame handoff](./lib-physics-frame-hook.md), re-queue the
same native call with `PhysicsFrameHook.EnqueueWorldChange`, and on replay join every physics worker,
publish `Resetting` before KSA destroys the old world and `Restoring` after it rebuilds the new one.

## How it works

| Native target (5554) | Patch | Callback raised | Purpose |
|---|---|---|---|
| `GameSave.Populate()` (virtual, not overridden by `UncompressedSave`) | postfix `Populated` | `Capturing(GameSave)` | Capture feature state right after `UniverseData.Create()` snapshotted the universe, only if the original ran |
| `UncompressedSave.Write()` returning `bool` | postfix `Saved` | `Written` or `WriteFailed` | Publish the sidecar only when the native write returned `true` |
| `UncompressedSave.Load()` | prefix `LoadingSave`, finalizer `FinishedLoading` | `Loading`, `LoadFinished`, `LoadFailed` | Defer, then scope a file-load transaction; turn a silent native read failure into an exception |
| `Universe.DeserializeSave(UniverseData)` | prefix `ResetBeforeLoad`, postfix `RestoreAfterLoad`, finalizer `FinishedDirectLoad` | `Resetting`, `Restoring`, `LoadFailed` | Preflight, join workers, reset old ownership, replay after native reconstruction |
| `Universe.LoadSystem(string)` | prefix `ResetBeforeSystem`, finalizer `FinishedDirectLoad` | `Resetting` | New system: reset only, nothing to restore |

Installation (`Apply`) first installs [PhysicsFrameHook](./lib-physics-frame-hook.md) (idempotent),
then binds each target by exact name and parameter list through `AccessTools.Method` and throws
`MissingMethodException` if one is missing. Every `(target, patch method)` pair is recorded, so
`Remove` unpatches exactly those methods (plus any pending world change) and leaves other Harmony
owners alone; a failure halfway through `Apply` calls `Remove` and rethrows. `IsApplied` is true only
when both the save patches and the frame hook are installed. In the supermod, `Patcher.Patch` calls it
inside its per-feature `TryApply` isolation.[^unscience-patcher]

**Deferral.** When a load prefix runs while `PhysicsFrameHook.IsReplayingWorldChange` is false, it
calls `EnqueueWorldChange` with the same native call (`__instance.Load`, a lambda re-invoking
`Universe.DeserializeSave(universeData)`, or `Universe.LoadSystem(id)`) and returns `false`, so the
original does not run now. Only one world change is held; a newer request replaces an older one. At
the next handoff the frame hook joins the orbit readers, sets `IsReplayingWorldChange`, invokes the
action, and the prefixes now let the native method through.[^physics-frame-hook] Requests that KSA itself
would refuse are passed straight to native code so its own error handling and alerts run: vehicle
editor open, no current system, null data, unknown system id, or frame hook not installed (logged as
"native load continues without scene restore").

**Replayed file load, step by step:**

1. `LoadingSave` increments the file-load depth, clears `LastLoadError`, and raises `Loading` with the
   `UncompressedSave`; the host reads and validates `unscience.json` from `save.Directory` here.
2. Native `Load` reads `universe.xml`. Since 5482 an unreadable file is logged and `Load` returns
   without calling `DeserializeSave`.[^ksa-uncompressedsave]
3. `ResetBeforeLoad` marks the native load as reached and runs `NativeSavePreflight.Validate`, which
   throws `InvalidDataException` before anything is destroyed (details below). It then waits on
   `JobSystems.OrbitSolvers`, `VehicleSolver`, `ClothSolvers` and `NearestOrbitAndPerformanceWorker`,
   clears queued `PhysicsFrameHook` edits, raises `Resetting`, and clears the queue again because
   cleanup code may enqueue edits against the dying world. A failed join rethrows: the world is never
   destroyed while a worker could still own it.
4. Native `DeserializeSave` destroys vehicles and bubbles and rebuilds the system, roster and camera
   from the data.[^ksa-universe]
5. `RestoreAfterLoad` raises `Restoring` only if the reset ran and the original executed.
6. Native `Load` calls `Program.OnGameLoaded()`, which closes the menus.
7. `FinishedLoading` (finalizer) decrements the depth, records either the native exception or a
   synthesized `InvalidDataException("KSA could not read save ...")` when `DeserializeSave` was never
   reached, raises `LoadFinished` then `LoadFailed`, and returns the exception so it is never swallowed.

A direct `Universe.DeserializeSave` call (no file transaction) follows steps 3 to 5, and
`FinishedDirectLoad` reports its failure only when no file load is in progress, so a nested failure
is reported once.

**Preflight** ([ksa-abstractions.lib/Persistence/NativeSavePreflight.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/NativeSavePreflight.cs)) rejects
known reconstruction failures while the current scene is still intact:[^native-save-preflight]

- game time, camera reference fields, raw camera transforms and `CameraMode` must be valid and the
  kitten roster present (it deliberately avoids `UniverseData.IsValid`, whose camera check the code
  comment says rejects the unfollowed camera that native saves support);
- exactly one `CelestialSystemData`, whose id must equal `Universe.CurrentSystem.Id`, so a save from
  another system is refused instead of half-loaded;
- at most 100000 vehicles with unique, case-insensitive ids, each with an existing `IParentBody`;
- every referenced character (`ModLibrary.Get<CharacterReference>`) and every `PartInstance.InstanceOf`
  template (`ModLibrary.Get<PartTemplate>`) must be installed, walked iteratively with depth, size and
  cycle limits.

**Callback isolation.** Each delegate in a callback's invocation list runs in its own `try/catch` and
logs `unscience saves: <operation> callback failed`, so one feature cannot block the native save or
load or the other features.

**Where the sidecar goes.** The hooks hand the real `UncompressedSave` object to the host, which writes
next to `universe.xml` in `save.Directory`. For a new save that folder is
`Path.Combine(GameSaves.SaveFolderPath, Id)` with `SaveFolderPath` = `<Documents folder>/saves`.
`Write` first calls `SaveDirectory.TryReplace` (delete and recreate the same folder), then writes the
metadata and `universe.xml`, so when the `Saved` postfix runs the folder contains exactly the new native
files.[^ksa-gamesaves][^ksa-savedirectory] The capture and the write are paired by object identity in the host (see
[the sidecar feature](./unscience-scene-save-sidecar.md)).

## Lifecycle and timing

Everything runs on the main thread. Saves are synchronous wherever the caller is; loads are always
moved to the handoff. Where the native calls originate in build 5554:[^ksa-program][^ksa-gamesaves]

| Entry point | Native call chain | Frame phase | What the hooks do |
|---|---|---|---|
| Console `save <name>` | GLFW key callback inside `Glfw.PollEvents()` (`Program.cs` L2179) -> `Program.OnKey` (L1745) -> `ConsoleWindowEx.OnKey` -> `ConsoleWindow.HandleInputAction` -> `Submit` -> `TerminalInterface.Execute` -> `GameSaves.MakeUncompressedSave` (L273-L280) -> `UncompressedSave.Make` | `frame:input`, after `Apply*Solvers`, before the handoff | Capture and write run immediately on settled state |
| GAME SAVES window New / Overwrite | `StringInputPopup` / `ConfirmActionPopup` callback in `Popup.DrawAll` (`OnDrawUiFrame`, L3134) -> `GameSaves.MakeSave` or `GameSave.Overwrite` -> `UncompressedSave.Make` | `frame:draw-ui`; the next physics step is already running on workers | Capture and write run immediately |
| [Save Yourself](./save-yourself-timed-autosave.md) / `GameSaveProvider.TryMakeSave` | `ISubmod.Update` from `[StarMapBeforeGui]` or the hidden-HUD replay | `frame:draw-ui` | Same as a manual save |
| Console `load <name>` | Same key path -> `GameSaves.LoadSaveGame` (L282-L288) -> `GameSave.Load` | `frame:input` | Deferred; replayed at L2216 in the same frame |
| GAME SAVES double-click or Load menu | `GameSave.LoadOnConfirm` -> `ConfirmActionPopup` -> `Load` in `Popup.DrawAll` | `frame:draw-ui` | Deferred; replayed at the next frame's handoff |
| Startup `Universe.LoadDefaultSystem` (`Program.cs` L1069) | `Universe.LoadSystem` with no current system | startup | Passed through synchronously |
| Multiplayer join (`NetworkClient.cs` L128) | Direct `Universe.DeserializeSave` | not traced further | Direct-load path: deferred, reset and restore without a file transaction |

Why the handoff is the right place to replace a world:

- All four job systems were waited on at the start of `PrepareFrame` (L2152-L2161) and their results
  applied; the only worker still running is the nearest-orbit job queued at L2198, which the frame
  hook and the reset both join before touching the old world.[^ksa-program]
- The old world has not been snapshotted for the next step yet. After the replay, the real
  `Universe.GetJobSimStep` computes the next step from the `_lastSimStep` that `DeserializeSave` just
  set to the saved game time (`Universe.cs` L2456-L2462), and `ExecuteNext*Solvers` snapshot the
  restored state.[^ksa-universe]
- No ImGui frame is being built (`PrepareImGui` runs after `PrepareFrame`), so participants can release
  textures and other GPU-backed resources that UI code might otherwise still reference.
- Native `DeserializeSave` does call `JobSystems.WaitAll()` (L2437), but only inside the original, after
  the prefix; mod cleanup that runs in the prefix must therefore join explicitly.

Saves are not deferred. When a save comes from `frame:draw-ui`, worker threads are integrating the
next step while the postfix captures; native `Populate` has already read the published main-thread
state, and the hook comment says joining workers would not change that snapshot's capture time, so
participants must capture only detached, mod-owned data.

## Key code

| Path | Role |
|---|---|
| [ksa-abstractions.lib/Persistence/NativeSaveHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/NativeSaveHooks.cs) | All patches, deferral, worker joins, transaction bookkeeping, callback isolation |
| [ksa-abstractions.lib/Persistence/NativeSavePreflight.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/NativeSavePreflight.cs) | Dependency and identity validation of incoming `UniverseData` before reset |
| [ksa-abstractions.lib/PhysicsFrameHook.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/PhysicsFrameHook.cs#L42-L48) | `EnqueueWorldChange` and the guarded replay in `GetStepAndDispatch` |
| [unscience/UnscienceSaves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/UnscienceSaves.cs#L20-L33) | Host that assigns the callbacks |
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L67) | `TryApply("scene saves", ...)` in the consolidated patcher |
| [saves.tests/Program.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/saves.tests/Program.cs#L13-L42) | Managed checks of ordering, failure paths and removal against fixtures |

## Snippets

Installation binds exact signatures and installs the frame hook first:

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

The write gate (`bool` result since 5482) and the load deferral:

```csharp
// ksa-abstractions.lib/Persistence/NativeSaveHooks.cs L85-L110
    private static void Saved(UncompressedSave __instance, bool __result, bool __runOriginal)
    {
        if (!__runOriginal) return;
        // Since KSA 5482 a failed save returns false instead of throwing, and the previous save
        // folder can survive intact. Writing the sidecar there would pair this session's feature
        // state with the old universe, so only a successful native write publishes it.
        if (__result) Invoke(Written, __instance, "write");
        else Invoke(WriteFailed, __instance, "write failure");
    }

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

Preflight, then reset only at a joined boundary:

```csharp
// ksa-abstractions.lib/Persistence/NativeSaveHooks.cs L137-L203
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
// ...
    private static bool ResetAtJoinedBoundary()
    {
        try
        {
            JobSystems.OrbitSolvers.Wait();
            JobSystems.VehicleSolver.Wait();
            JobSystems.ClothSolvers.Wait();
            // PrepareFrame queues nearest-orbit work before this handoff; it traverses
            // the current universe and must finish before vehicle destruction as well.
            JobSystems.NearestOrbitAndPerformanceWorker.Wait();
            PhysicsFrameHook.ClearPending();
            Invoke(Resetting, "reset");
            // Cleanup callbacks can themselves enqueue old-world edits. They cannot survive
            // the replacement, even when a participant throws halfway through cleanup.
            PhysicsFrameHook.ClearPending();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"unscience saves: cannot join reset boundary: {ex}");
            throw; // Never destroy a world while a worker could still own it.
        }
    }
```

The native code being bracketed (build 5554): `Make` runs `Populate` then `Write`, and `Load` only
reaches `DeserializeSave` when the file parsed:

```csharp
// current/decomp/KSA/UncompressedSave.cs L61-L146
		try
		{
			UniverseData = UniverseData.LoadFrom(this);
		}
// ...
			return;
		}
		Universe.DeserializeSave(UniverseData);
		Program.OnGameLoaded();
// ...
	public static GameSave? Make(string name)
	{
		UncompressedSave uncompressedSave = new UncompressedSave(name);
		uncompressedSave.Populate();
		return uncompressedSave.Write() ? uncompressedSave : null;
	}
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `GameSave.Populate()` | [current/decomp/KSA/GameSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSave.cs#L37-L40) | Postfix target; body is `UniverseData = UniverseData.Create()` |
| `UncompressedSave.Write()`, `Load()`, `Make(string)`, `Directory` | [current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L45-L146) | Postfix, prefix and finalizer targets; save folder for the sidecar |
| `UniverseData.Create()`, `LoadFrom`, `WriteTo` | [current/decomp/KSA/UniverseData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UniverseData.cs#L40-L83) | What `Populate`, `Load` and `Write` do with `universe.xml` |
| `Universe.DeserializeSave(UniverseData)`, `LoadSystem(string)`, `LoadDefaultSystem()`, `CurrentSystem` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2430-L2478) | World replacement targets; `LoadSystem` is at L232 |
| `GameSaves.MakeUncompressedSave`, `LoadSaveGame`, `MakeSave`, `SaveFolderPath`, `RefusedInEditor` | [current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L250-L288) | Console and window entry points traced above |
| `SaveDirectory.TryReplace` | [current/decomp/KSA/SaveDirectory.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SaveDirectory.cs#L21-L24) | Deletes and recreates the save folder inside `Write` |
| `JobSystems.OrbitSolvers`, `VehicleSolver`, `ClothSolvers`, `NearestOrbitAndPerformanceWorker` | [current/decomp/KSA/JobSystems.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/JobSystems.cs#L10-L16) | `JobScheduler.Wait()` before reset |
| `Program.IsEditorOpen`, `Program.OnKey`, `Program.PrepareFrame`, `Program.OnGameLoaded` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1745-L1754) | Editor refusal; console command dispatch; replay point (L2216) |
| `ConsoleWindowEx.OnKey`, `ConsoleWindow.HandleInputAction` / `Submit` | [current/decomp/KSA/ConsoleWindowEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleWindowEx.cs#L14-L31), [current/decomp/Brutal.ImGuiApi.Abstractions/ConsoleWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi.Abstractions/ConsoleWindow.cs#L541-L548) | Shows console commands execute from the Enter key callback |
| `Popup.DrawAll` | [current/decomp/KSA/Popup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Popup.cs#L46-L64) | Where window save/load confirmations execute |
| `NetworkClient` join response | [current/decomp/KSA.Networking/NetworkClient.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Networking/NetworkClient.cs#L128) | Another direct `DeserializeSave` caller the prefix intercepts |
| `SystemLibrary.Find`, `ModLibrary.Get<T>` | (called from the prefixes and preflight) | Validate system id, characters and part templates |

## Pitfalls and version notes

- The repo's own notes say a console load "can originate late in UI rendering". In 5554 the KSA console
  executes commands from the Enter key callback during `Glfw.PollEvents` (before the handoff), while the
  GAME SAVES window and its confirmation popups run inside `OnDrawUiFrame`. The deferral is unconditional,
  so both paths are handled; the difference is only whether the replay happens this frame or the next.
- Since 5482 `UncompressedSave.Write()` returns `bool` and a failed `SaveDirectory.TryReplace` can leave the
  old save folder and its old sidecar intact; the postfix must read `__result` or it would pair new feature
  state with the old `universe.xml`. Since 5482 `Load` also swallows read errors; the finalizer synthesizes
  the failure. Both behaviors are unchanged in 5554.[^scope-saves]
- KSA's overwrite is not transactional: the folder is deleted before native files are written. The hooks
  do not change that.
- Only the latest deferred request survives; a load clicked twice in one frame runs once.
- `LoadSystem` gets a reset but no restore. In 5554 the only native caller is startup (no current system),
  so the deferred `LoadSystem` path is reached only by mods calling it.
- `PrepareFrame` is found by name in `PhysicsFrameHook`; if that transpiler fails, `NativeSaveHooks.Apply`
  fails with it and native save and load run unpatched (no sidecar), which the toolbox reports.
- Standalone feature hosts (for example the `save-yourself` development mod) never install these hooks, so
  saves made there carry no sidecar.
- Preflight cannot predict every native reconstruction fault; a later exception inside `DeserializeSave`
  happens after reset, and the coordinator then retains the incoming records for recovery.

## See also

- [Physics-safe vessel mutation at the PrepareFrame handoff](./lib-physics-frame-hook.md) (the replay point)
- [Scene save coordinator](./lib-scene-save-coordinator.md) (what the callbacks drive)
- [Unscience scene save sidecar](./unscience-scene-save-sidecar.md) (host wiring and file location)
- [Stable part identity across reloads](./lib-save-part-identity.md)
- [Timed auto saves](./save-yourself-timed-autosave.md) (a programmatic producer of native saves)
- [Managed test harnesses](./unscience-managed-test-harnesses.md) (how these hooks are tested without KSA)
- [Consolidated patcher](./unscience-consolidated-patcher.md)
- [Mod overview](../overview.md)
- [Prefix, postfix and finalizer as a transaction around loads](../../../howto/patching/harmony/run-cleanup-or-recover-from-exceptions-with-a-finalizer/run-cleanup-or-recover-from-exceptions-with-a-finalizer-4.md) - how-to article demonstrating this feature
- [Hook native save and load with five patches, eight callbacks and a deferred world change](../../../howto/persistence/saves/save-mod-state-with-the-scene/save-mod-state-with-the-scene-3.md) - how-to article demonstrating this feature

[^native-save-hooks]: ksa-abstractions.lib/Persistence/NativeSaveHooks.cs
[^native-save-preflight]: ksa-abstractions.lib/Persistence/NativeSavePreflight.cs
[^physics-frame-hook]: ksa-abstractions.lib/PhysicsFrameHook.cs
[^unscience-saves]: unscience/UnscienceSaves.cs
[^unscience-patcher]: unscience/Patcher.cs
[^scope-saves]: scope/saves.md (authors' save/load lifecycle analysis, 5482)
[^ksa-uncompressedsave]: KSA.UncompressedSave (decompiled, build 2026.10.10.5554)
[^ksa-gamesaves]: KSA.GameSaves (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-savedirectory]: KSA.SaveDirectory (decompiled, build 2026.10.10.5554)
