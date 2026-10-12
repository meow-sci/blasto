---
type: How-To
title: Pair each scene capture with its native write by object identity
description: Connect the coordinator to the save hooks so a Populate postfix stores the document in a ConditionalWeakTable keyed by the GameSave, the Write postfix publishes it only after a successful native write, and a missing sidecar means reset everything and restore nothing
category: persistence
group: saves
topic: save-mod-state-with-the-scene
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/unscience-scene-save-sidecar]
techniques: [save-sidecar]
hooks: [harmony-postfix KSA.GameSave.Populate, harmony-postfix KSA.UncompressedSave.Write]
lifecycle: [startup:all-mods-loaded, event:save, event:load]
game_types: [KSA.GameSave, KSA.UncompressedSave, KSA.GameSaves, KSA.SaveDirectory]
tags: [saves, scene, sidecar, host-wiring, conditional-weak-table, unscience-json]
---

Outcome: the host side of scene persistence. A save made by any route (console, save window, auto save, a mod calling `UncompressedSave.Make`) gets an `unscience.json` next to `universe.xml`, written only when KSA's own write succeeded and only with state captured for that very save. Prerequisite: the coordinator of [Persist feature state as ISaveParticipant records through a coordinator that retains what it cannot restore](save-mod-state-with-the-scene-1.md) and the hooks of [Hook native save and load with five patches, eight callbacks and a deferred world change](save-mod-state-with-the-scene-3.md). Demonstrated by [Per-save scene sidecar wiring and the participant inventory](../../../../mods/unscience/features/unscience-scene-save-sidecar.md).

## Background: how the game does it

`UncompressedSave.Make` runs `Populate()` and then `Write()` on the same `UncompressedSave` instance ([current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L141-L146)). `Populate` is the virtual `GameSave.Populate`, which `UncompressedSave` does not override ([current/decomp/KSA/GameSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSave.cs#L37-L40)). `Write` replaces the save folder (`SaveDirectory.TryReplace`), writes metadata and `universe.xml`, and returns `bool` ([current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L108-L139), [current/decomp/KSA/SaveDirectory.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SaveDirectory.cs#L21-L24)). Since build 5482 a failed save returns `false` instead of throwing, and the previous save folder can survive intact. Pairing a capture with the write of the same instance is the only way to be sure the sidecar describes the universe next to it.

## Lifecycle and timing

| Callback | Raised from | Lifecycle |
|---|---|---|
| `Capturing(GameSave)` | postfix of `GameSave.Populate` | `event:save` (during the save's snapshot) |
| `Written(UncompressedSave)` | postfix of `UncompressedSave.Write` when it returned `true` | `event:save` (the save folder holds exactly the new native files) |
| `WriteFailed(UncompressedSave)` | same postfix when it returned `false` | `event:save` |
| `Loading(UncompressedSave)` | prefix of `UncompressedSave.Load` during the replay | `event:load`, `frame:prepare` |

The patches are installed in `Patcher` (`TryApply("scene saves", () => NativeSaveHooks.Apply(_harmony!))`, [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L67)), inside a per-feature isolation wrapper so one failing patch does not stop the others. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Collect participants from all sub-mods and build the coordinator.** A sub-mod is either an `ISaveParticipantSource` (any number of records) or an `ISaveParticipant`. Then plug all eight callbacks:

   ```csharp
   // unscience/UnscienceSaves.cs L14-L33
   internal sealed class UnscienceSaves : IDisposable
   {
       private readonly SceneSaveCoordinator _coordinator;
       private readonly ConditionalWeakTable<GameSave, SaveDocument> _captures = new();
       private bool _details;

       public UnscienceSaves(IEnumerable<ISubmod> submods)
       {
           var participants = submods.SelectMany(submod => submod is ISaveParticipantSource source
               ? source.SaveParticipants : submod is ISaveParticipant participant ? new[] { participant } : Array.Empty<ISaveParticipant>());
           _coordinator = new(participants);
           NativeSaveHooks.Capturing = Capture;
           NativeSaveHooks.Written = Write;
           NativeSaveHooks.WriteFailed = WriteFailed;
           NativeSaveHooks.Loading = Load;
           NativeSaveHooks.Resetting = _coordinator.ResetWorld;
           NativeSaveHooks.Restoring = Restore;
           NativeSaveHooks.LoadFinished = _coordinator.FinishLoad;
           NativeSaveHooks.LoadFailed = error => _coordinator.MarkFailure($"Load failed: {error.Message}");
       }
   ```

2. **Capture, keyed by the save instance.** A `ConditionalWeakTable<GameSave, SaveDocument>` holds the capture so an abandoned save (a throw between `Populate` and `Write`) does not leak and a stale capture can never be matched to a different save. `SavedPartReference.BeginOperation()` opens a hashing scope shared by all participants of this capture (see [Identify vessels and parts stably across saves](../identify-vessels-and-parts-across-saves/overview.md)):

   ```csharp
   // unscience/UnscienceSaves.cs L35-L40
   private void Capture(GameSave save)
   {
       _captures.Remove(save);
       using var identities = SavedPartReference.BeginOperation();
       _captures.Add(save, _coordinator.Capture());
   }
   ```

3. **Write only on success and only with a matching capture.** A write without a capture is reported and nothing is written - the host never fills the gap with fresh state, because a fresh capture would pair live state with a universe it was not taken with:

   ```csharp
   // unscience/UnscienceSaves.cs L48-L68
   private void Write(UncompressedSave save)
   {
       if (!_captures.TryGetValue(save, out var document))
       {
           _coordinator.Report("Native save had no matching state capture; Unscience state was not written.");
           return;
       }
       try
       {
           SaveStorage.Write(save.Directory.FullName, document);
           _coordinator.MarkWritten(save.Id);
       }
       catch (Exception ex) { _coordinator.MarkFailure($"KSA saved '{save.Id}', but Unscience state could not be written: {ex.Message}"); }
       finally { _captures.Remove(save); }
   }

   private void WriteFailed(UncompressedSave save)
   {
       _captures.Remove(save);
       _coordinator.MarkFailure($"KSA could not write save '{save.Id}'; Unscience state was not written.");
   }
   ```

4. **Read on load, falling back to "no state".** An unreadable, corrupt or mismatched document is not fatal: the native load proceeds, every feature is reset, nothing is restored, and the reason is reported:

   ```csharp
   // unscience/UnscienceSaves.cs L70-L78
   private void Load(UncompressedSave save)
   {
       try { _coordinator.PrepareLoad(SaveStorage.Read(save.Directory.FullName)); }
       catch (Exception ex)
       {
           _coordinator.PrepareLoad(null);
           _coordinator.Report($"Loading native save without Unscience state: {ex.Message}");
       }
   }
   ```

5. **Restore inside a part-identity scope** so that part addresses resolve against one hashing pass per vehicle:

   ```csharp
   // unscience/UnscienceSaves.cs L42-L46
   private void Restore()
   {
       using var identities = SavedPartReference.BeginOperation();
       _coordinator.RestoreWorld();
   }
   ```

6. **Show status and give the user an escape hatch.** `RenderStatus` prints the coordinator status, the warning count, and (when records are retained) the "Use current setup for future saves" button that calls `UseCurrentSetup()` ([unscience/UnscienceSaves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/UnscienceSaves.cs#L80-L107)).

7. **Detach on dispose.** Null every callback and call `FinishLoad()` so a half-finished transaction does not survive the mod ([unscience/UnscienceSaves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/UnscienceSaves.cs#L109-L121)).

## Complete example

| File | Role |
|---|---|
| [unscience/UnscienceSaves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/UnscienceSaves.cs) | Host: callbacks, capture table, write, load, status |
| [ksa-abstractions.lib/Persistence/NativeSaveHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/NativeSaveHooks.cs#L28-L52) | The patches the callbacks hang on |
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L55-L80) | Where they are applied |

## Variations and gotchas

- **Why not capture in the Write postfix?** At that point the universe snapshot was taken earlier; capturing later pairs a later state with the file. Capturing in the `Populate` postfix ties both to the same moment.
- **Where the sidecar lands.** `save.Directory` is `saves/<Id>`; the sidecar is written after `Write` recreated the folder, so it survives (compare the placement problem in [Store per-save mod data next to a KSA save and notice when the player saves](../save-mod-state-with-the-game-save/overview.md)).
- **Missing sidecar semantics.** A save made without the mod loads with every feature reset. That is deliberate: a scene from the previous save must not leak into this one.
- **The hash binding** means a hand-edited or copied `universe.xml` invalidates the sidecar for that save (the load reports it and continues without scene state).
- **Copy dependencies with the save.** The status text tells players to keep imported PNG, GLB and sound libraries and runtime part mods installed when moving saves to another computer.

## See also

- [Persist feature state as ISaveParticipant records through a coordinator that retains what it cannot restore](save-mod-state-with-the-scene-1.md), [Hook native save and load with five patches, eight callbacks and a deferred world change](save-mod-state-with-the-scene-3.md), [overview](./overview.md)
- [Per-save scene sidecar wiring and the participant inventory](../../../../mods/unscience/features/unscience-scene-save-sidecar.md)
- [Store per-save mod data next to a KSA save and notice when the player saves](../save-mod-state-with-the-game-save/overview.md), [Detect a save load or quickload and reset mod state safely](../detect-save-load/overview.md)
