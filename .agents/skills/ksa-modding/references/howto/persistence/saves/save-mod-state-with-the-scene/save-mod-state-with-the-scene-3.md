---
type: How-To
title: Hook native save and load with five patches, eight callbacks and a deferred world change
description: Patch GameSave.Populate, UncompressedSave.Write and Load, and Universe.DeserializeSave and LoadSystem with exact-signature binding, defer every load to the PrepareFrame handoff, join the workers before reset, validate incoming data first and turn silent read failures into errors
category: persistence
group: saves
topic: save-mod-state-with-the-scene
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/lib-native-save-hooks]
techniques: [harmony-prefix, harmony-postfix, harmony-finalizer, harmony-transpiler]
hooks:
  - harmony-postfix KSA.GameSave.Populate
  - harmony-postfix KSA.UncompressedSave.Write
  - harmony-prefix KSA.UncompressedSave.Load
  - harmony-finalizer KSA.UncompressedSave.Load
  - harmony-prefix KSA.Universe.DeserializeSave
  - harmony-postfix KSA.Universe.DeserializeSave
  - harmony-prefix KSA.Universe.LoadSystem
  - harmony-transpiler KSA.Program.PrepareFrame
lifecycle: [startup:all-mods-loaded, event:save, event:load, frame:prepare, frame:draw-ui, frame:input]
game_types:
  - KSA.GameSave
  - KSA.UncompressedSave
  - KSA.Universe
  - KSA.UniverseData
  - KSA.GameSaves
  - KSA.JobSystems
  - KSA.Program
tags: [saves, harmony, deferral, preflight, finalizer, prepare-frame, transactional-load]
---

Outcome: the game-facing half of scene persistence. Five native methods are patched and turned into eight plain callbacks (`Capturing`, `Written`, `WriteFailed`, `Loading`, `Resetting`, `Restoring`, `LoadFinished`, `LoadFailed`). The class knows nothing about JSON or features; a host plugs the coordinator of [Persist feature state as ISaveParticipant records through a coordinator that retains what it cannot restore](save-mod-state-with-the-scene-1.md) into the callbacks as in [Pair each scene capture with its native write by object identity](save-mod-state-with-the-scene-2.md). Demonstrated by [Native save and load hooks that bracket every KSA save transaction](../../../../mods/unscience/features/lib-native-save-hooks.md). This is the most invasive approach in the topic: it changes when every load takes effect.

## Background: how the game does it

| Native target (build 5554) | Facts that shape the patch |
|---|---|
| `GameSave.Populate()` (virtual, not overridden by `UncompressedSave`) | Calls `UniverseData.Create()` and stores it; the snapshot of the universe ([current/decomp/KSA/GameSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSave.cs#L37-L40)) |
| `UncompressedSave.Write()` | Returns `bool`; on failure the previous save folder can survive ([current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L108-L139)) |
| `UncompressedSave.Load()` | Reads the file, logs and returns when unreadable (without calling `DeserializeSave`), then `DeserializeSave` and `Program.OnGameLoaded()` ([current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L45-L83)) |
| `Universe.DeserializeSave(UniverseData)` | Zero speed, `JobSystems.WaitAll()` (L2437), destroys vehicles and bubbles, rebuilds from the data ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2430-L2478)) |
| `Universe.LoadSystem(string)` | Builds a new system; also used by startup ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L232-L260)) |
| `Program.PrepareFrame` | Waits on all job systems, applies results, then `GetJobSimStep` (L2216) and `ExecuteNext*Solvers` (L2220-L2221) ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141-L2225)) |

Where the native calls originate (all main thread):

| Entry point | Call chain | Frame phase | What the hooks do |
|---|---|---|---|
| Console `save <name>` | key handler inside `Glfw.PollEvents` (L2179) -> console submit -> `GameSaves.MakeUncompressedSave` ([current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L273-L280)) -> `UncompressedSave.Make` | `frame:input`, after the solver results were applied | Capture and write run immediately on settled state |
| Save window New / Overwrite | confirm popup callback in `Popup.DrawAll` (`OnDrawUiFrame`) -> `GameSaves.MakeSave` / `GameSave.Overwrite` -> `Make` | `frame:draw-ui`; workers are integrating the next step | Capture and write run immediately |
| `GameSaveProvider.TryMakeSave` (Save Yourself) | from a `[StarMapBeforeGui]` update | `frame:draw-ui` | Same as a manual save |
| Console `load <name>` | same key path -> `GameSaves.LoadSaveGame` ([current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L282-L304)) -> `GameSave.Load` | `frame:input` | Deferred; replayed in the same frame's `PrepareFrame` |
| Save window double-click or Load | `GameSave.LoadOnConfirm` -> confirm popup -> `Load` in `Popup.DrawAll` | `frame:draw-ui` | Deferred; replayed at the next frame's handoff |
| Startup `Universe.LoadDefaultSystem` | `Universe.LoadSystem` with no current system | startup | Passed through synchronously |
| Multiplayer join | direct `Universe.DeserializeSave` ([current/decomp/KSA.Networking/NetworkClient.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Networking/NetworkClient.cs#L127-L129)) | not traced | Direct-load path: deferred, reset and restore without a file transaction |

## Lifecycle and timing

Saves are not deferred: they run wherever the caller is, so `Capturing` can run while workers integrate the next step (use detached data only). Loads are always moved to the `PrepareFrame` handoff (`frame:prepare`), between result application and the next step's dispatch. At that point all four job systems have been waited on at the start of `PrepareFrame`; the only worker still running is the nearest-orbit job queued just before the handoff, which both the frame hook and the reset join before touching the old world. No ImGui frame is being built yet (`PrepareImGui` runs after `PrepareFrame`), so participants can release textures. After the replay, the real `Universe.GetJobSimStep` computes the next step from the `_lastSimStep` that `DeserializeSave` just set. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for the full order.

The consequence for other mods: while this hook is installed, `GameSaves.LoadSaveGame` and `UncompressedSave.Load` return before the world has changed; the load is a request that completes at the next handoff. Mods that call them (see [Create and load native KSA saves from mod code](../save-and-load-the-game-from-code/overview.md)) should wait for the result rather than read the world on return.

## Steps

1. **Install first the frame hook, then each target by exact name and parameters.** A target that cannot be found throws `MissingMethodException`; a failure halfway through calls `Remove` and rethrows. Every `(target, patch)` pair is recorded so `Remove` unpatches exactly those:

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

   The binding helper ([ksa-abstractions.lib/Persistence/NativeSaveHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/NativeSaveHooks.cs#L62-L76)) resolves each patch method by name, records the pair and calls `harmony.Patch` with prefix, postfix and finalizer.

2. **Observe the save path with postfixes.** `Populated` raises `Capturing` only if the original ran; `Saved` publishes only when the native write returned `true`. Since KSA 5482 a failed save returns `false` and the previous folder can survive, so publishing there would pair this session's state with the old universe:

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

3. **Defer every load to the handoff.** The prefix on `UncompressedSave.Load` refuses to run outside the replay, re-queues the same native call with `PhysicsFrameHook.EnqueueWorldChange` and returns `false`. Only one world change is held; a newer request replaces an older one. Requests that KSA itself would refuse (editor open, frame hook not installed) pass straight through so KSA's own errors and alerts run:

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

   The frame hook replaces the single call to `Universe.GetJobSimStep` in `Program.PrepareFrame` with its own method via a Harmony transpiler. It checks that all seven solver calls exist exactly once and in the expected order and refuses to patch otherwise:

   ```csharp
   // ksa-abstractions.lib/PhysicsFrameHook.cs L73-L104
   internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
   {
       var codes = instructions.ToList();
       // Patch the caller: a prefix on a small solver method can miss a call already inlined
       // into PrepareFrame. Replacing this one call preserves its stack, labels and blocks.
       // Reject missing, duplicate or reordered seams instead of falling back to UI teleports.
       string[] seams =
       {
           nameof(Universe.ApplyOrbitSolvers),
           nameof(Universe.ApplyVehicleSolvers),
           nameof(Universe.ApplyClothSolvers),
           nameof(Universe.GetJobSimStep),
           nameof(Universe.ExecuteNextClothSolvers),
           nameof(Universe.ExecuteNextVehicleSolvers),
           nameof(Universe.ExecuteNextOrbitSolvers)
       };
       int previousIndex = -1;
       int stepIndex = -1;
       foreach (string seam in seams)
       {
           var method = AccessTools.Method(typeof(Universe), seam)
               ?? throw new MissingMethodException(typeof(Universe).FullName, seam);
           int index = codes.FindIndex(code => code.Calls(method));
           if (index <= previousIndex || codes.Count(code => code.Calls(method)) != 1)
               throw new InvalidOperationException($"unscience physics: unexpected PrepareFrame solver sequence at {seam}");
           previousIndex = index;
           if (seam == nameof(Universe.GetJobSimStep)) stepIndex = index;
       }

       codes[stepIndex].operand = AccessTools.Method(typeof(PhysicsFrameHook), nameof(GetStepAndDispatch));
       return codes;
   }
   ```

   and the replacement runs the pending world change before computing the step, with a flag so the load prefixes let it through:

   ```csharp
   // ksa-abstractions.lib/PhysicsFrameHook.cs L106-L123
   private static SimStep GetStepAndDispatch(double dtPlayer)
   {
       // The previous ImGui frame has finished and worker results have committed. A
       // console load requested after ImGui.Image must never dispose its textures inline.
       // Replay the whole transaction here, before computing any time from the old world.
       _orbitReadersJoined = false;
       Action? worldChange = _pendingWorldChange;
       _pendingWorldChange = null;
       if (worldChange != null)
       {
           JoinOrbitReaders();
           IsReplayingWorldChange = true;
           try { worldChange(); }
           catch (Exception ex) { Console.WriteLine($"unscience saves: deferred world change failed: {ex}"); }
           finally { IsReplayingWorldChange = false; }
       }
       SimStep step = Universe.GetJobSimStep(dtPlayer);
       if (Universe.CurrentSystem == null) { Pending.Clear(); return step; }
   ```

4. **Preflight, join, reset.** `ResetBeforeLoad` marks that the native load was reached, validates the incoming `UniverseData` (see below), waits on all four schedulers, clears queued edits, raises `Resetting`, and clears the queue again because cleanup code may enqueue edits against the dying world. A failed join rethrows: the world is never destroyed while a worker could still own it:

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

   ```csharp
   // ksa-abstractions.lib/Persistence/NativeSaveHooks.cs L181-L203
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

5. **Validate before releasing anything.** `NativeSavePreflight` rejects known reconstruction failures while the current scene is still intact: invalid game time, camera or kitten roster data; a save for a different celestial system (exactly one system, id equal to `Universe.CurrentSystem.Id`); more than 100000 vehicles or duplicate ids (case-insensitive, because `KeyHash.Make` lowercases); a vehicle whose parent body is missing; and any character or part template that is not installed (walked iteratively with depth, size and cycle limits):

   ```csharp
   // ksa-abstractions.lib/Persistence/NativeSavePreflight.cs L11-L33
   public static void Validate(UniverseData data)
   {
       try
       {
           // UniverseData.IsValid rejects an unfollowed/free camera although native saves
           // explicitly support it. Validate the actual reconstruction inputs instead.
           if (data.GameTime == null || !data.GameTime.IsValid() || data.Camera == null || data.KittenRoster == null
               || data.Camera.Following == null || data.Camera.TidalLocking == null
               || data.Camera.MapInverted == null || data.Camera.MapPreviouslyControlled == null
               || data.Camera._positionRaw?.IsValid() == false || data.Camera._rotationRaw?.IsValid() == false
               || data.Camera._scaleRaw?.IsValid() == false || !Enum.IsDefined(data.Camera.CameraMode))
               throw new InvalidDataException("This save has invalid game time, camera, or kitten roster data.");
       }
       catch (Exception ex) when (ex is not InvalidDataException)
       { throw new InvalidDataException("This save has invalid game time or camera data.", ex); }
       if (data.CelestialSystems == null || data.CelestialSystems.Count != 1)
           throw new InvalidDataException("This save must contain exactly one celestial system.");
       var system = data.CelestialSystems[0];
       if (system?.Id == null || Universe.CurrentSystem == null
           || !string.Equals(system.Id.Id, Universe.CurrentSystem.Id, StringComparison.OrdinalIgnoreCase))
           throw new InvalidDataException("This save belongs to a different celestial system. Load the matching system before this save.");
       if (system.Vehicles == null || system.Vehicles.Count > 100000)
           throw new InvalidDataException("Invalid native vehicle table.");
   ```

6. **Turn silent native failures into errors.** The finalizer on `Load` decrements the file-load depth and, when `DeserializeSave` was never reached, synthesises an `InvalidDataException` (since 5482 an unreadable save is only logged). It returns the original exception, so nothing is swallowed. `FinishedDirectLoad` reports a failure only when no file load is in progress, so a nested failure is reported once:

   ```csharp
   // ksa-abstractions.lib/Persistence/NativeSaveHooks.cs L112-L135
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

7. **Isolate callbacks.** Every delegate in an invocation list runs in its own `try`/`catch`, so one feature cannot block the native save or load or the other features ([ksa-abstractions.lib/Persistence/NativeSaveHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/NativeSaveHooks.cs#L205-L223)).

8. **Apply under isolation.** unscience applies the hooks inside a per-feature try wrapper so a missing target disables scene saving without stopping the mod ([unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L67)); `IsApplied` is true only when both the save patches and the frame hook are installed, and the host shows "Scene saving unavailable" otherwise.

## Complete example

| File | Role |
|---|---|
| [ksa-abstractions.lib/Persistence/NativeSaveHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/NativeSaveHooks.cs) | All patches, deferral, worker joins, transaction bookkeeping, callback isolation |
| [ksa-abstractions.lib/Persistence/NativeSavePreflight.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/NativeSavePreflight.cs) | Dependency and identity validation of incoming `UniverseData` before reset |
| [ksa-abstractions.lib/PhysicsFrameHook.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/PhysicsFrameHook.cs) | `EnqueueWorldChange`, the transpiler and the guarded replay in `GetStepAndDispatch` |
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L55-L80) | Where `Apply` is called |

## Variations and gotchas

- **Loads are asynchronous to the caller.** With the hook installed, a mod that calls `LoadSaveGame` sees the world change one frame phase later; the unscience bridge-style commands wait for a craft (see [Load a save from a command file and wait until a craft is flown again](../save-and-load-the-game-from-code/save-and-load-the-game-from-code-2.md)).
- **The transpiler is a version risk.** It rejects missing, duplicate or reordered solver calls instead of falling back, so a restructured `PrepareFrame` in a game update disables scene saving (with a log line) rather than corrupting a load.
- **`Universe.LoadSystem` is reset-only.** There is nothing to restore for a new system.
- **Join blocks.** `JobSystems.NearestOrbitAndPerformanceWorker` must be joined too: it traverses the current universe and must finish before vehicle destruction.
- **Handoff only.** `PhysicsFrameHook.JoinOrbitReaders` is meant for the handoff; later in the frame the same worker may run unrelated editor jobs that it does not wait for.

## See also

- [Persist feature state as ISaveParticipant records through a coordinator that retains what it cannot restore](save-mod-state-with-the-scene-1.md), [Pair each scene capture with its native write by object identity](save-mod-state-with-the-scene-2.md), [overview](./overview.md)
- [Native save and load hooks that bracket every KSA save transaction](../../../../mods/unscience/features/lib-native-save-hooks.md), [Physics-safe vessel mutation at the PrepareFrame solver handoff](../../../../mods/unscience/features/lib-physics-frame-hook.md)
- [Mutate vehicles at a physics-safe point](../../../physics/solver/mutate-vehicles-at-a-physics-safe-point/overview.md), [Detect a save load or quickload and reset mod state safely](../detect-save-load/overview.md), [Create and load native KSA saves from mod code](../save-and-load-the-game-from-code/overview.md)
