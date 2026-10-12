---
type: How-To
title: Persist feature state as ISaveParticipant records through a coordinator that retains what it cannot restore
description: Define a participant contract with capture, reset and prepare-restore, drive it from a game-independent coordinator, store versioned records in one hash-bound atomic JSON document, and keep unknown or failed records so older builds round-trip them
category: persistence
group: saves
topic: save-mod-state-with-the-scene
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/unscience/features/lib-scene-save-coordinator
  - mods/unscience/features/camera-controller-override-keyframe-sequences
  - mods/unscience/features/kitten-animations-locomotion-tuning
techniques: [save-sidecar, json-config]
hooks: [unscience ISaveParticipantSource]
lifecycle: [event:save, event:load, frame:prepare]
game_types: [KSA.GameSave, KSA.UncompressedSave, KSA.Universe]
tags: [saves, scene, participant, coordinator, versioning, retention, json, atomic-write]
---

Outcome: dozens of independent features each persist their own state with a few lines, and the shared core guarantees that a bad record never reaches a half-torn world, an unknown record is not lost, and the file on disk is the one the save belongs to. The core (`SceneSaveCoordinator`, `SaveStorage`, `SaveJson`) references no KSA types, so it is compiled into native-free test executables. How it is connected to the game is in [Pair each scene capture with its native write by object identity](save-mod-state-with-the-scene-2.md) and [Hook native save and load with five patches, eight callbacks and a deferred world change](save-mod-state-with-the-scene-3.md). Demonstrated by [Scene save coordinator with ordered reset, deferred replay and retained records](../../../../mods/unscience/features/lib-scene-save-coordinator.md), with participants shown from [Keyframe camera sequences with parallel groups, return-to-start and saved recipes](../../../../mods/unscience/features/camera-controller-override-keyframe-sequences.md) and [Live animation tuning on KittenLocomotionTuning.Current with a scoped reset and a scene-save record](../../../../mods/unscience/features/kitten-animations-locomotion-tuning.md).

## Background: how the game does it

KSA saves are written in one step (`GameSave.Populate` then `UncompressedSave.Write`) and loaded in one (`UncompressedSave.Load`, which rebuilds the world in `Universe.DeserializeSave`). Neither has a place for mod data, and a load destroys every vehicle and keeps the scene ([current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L45-L83), [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2430-L2478)). A mod with native objects (lights, decals, cameras) therefore needs three phases around the native rebuild: capture before the save, release before the world dies, re-create after the world exists. Native-object ownership belongs in `ResetState`; data in `CaptureState`; a validated closure in `PrepareRestore`.

## Lifecycle and timing

The coordinator has no hooks of its own; it runs inside the callbacks of [Hook native save and load with five patches, eight callbacks and a deferred world change](save-mod-state-with-the-scene-3.md), all on the main thread:

| Coordinator call | Native moment | Frame phase |
|---|---|---|
| `Capture()` | Postfix of `GameSave.Populate`, right after `UniverseData.Create()` | Wherever the save was issued: `frame:input` (console) or `frame:draw-ui` (save window, auto save) |
| `PrepareLoad(doc)` | Prefix of `UncompressedSave.Load`, during the deferred replay | `frame:prepare`, before `Universe.GetJobSimStep` |
| `ResetWorld()` | Prefix of `Universe.DeserializeSave`, after all worker joins | `frame:prepare` |
| `RestoreWorld()` | Postfix of `Universe.DeserializeSave`, after native vehicles, roster and camera exist | `frame:prepare`; the next step is computed and snapshotted right after |
| `FinishLoad()` | Finalizer of `UncompressedSave.Load` | `frame:prepare` |

Consequences for participants: `CaptureState` can run while vehicle workers integrate the next step (a UI-issued save), so it must read only main-thread, mod-owned data and must not mutate the world; `ResetState` runs while the old native objects still exist and no worker owns them; replay closures run after reconstruction and before the next solver dispatch (a restore that edits vessels does not race the workers) and before `PrepareImGui` (they can create or release UI textures safely); `PrepareRestore` must be side-effect free because KSA has not yet decided whether the native load will succeed. Frame order is in the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Implement the participant contract.** `SaveId` is a stable record key, `SaveVersion` defaults to 1, `RestoreOrder` defaults to 100 (reset runs in the reverse order). `SaveParticipant<T>` adapts a feature that already has typed capture and apply methods:

   ```csharp
   // ksa-abstractions.lib/Persistence/ISaveParticipant.cs L8-L39
   public interface ISaveParticipant
   {
       string SaveId { get; }
       int SaveVersion => 1;
       int RestoreOrder => 100;
       JsonElement CaptureState();
       /// <summary>Release old-world ownership synchronously, before native objects are destroyed.</summary>
       void ResetState();
       /// <summary>Validate/deserialize without modifying the world; return replay to run after native reconstruction.</summary>
       Action PrepareRestore(JsonElement state, SaveRestoreContext context);
   }

   public interface ISaveParticipantSource
   {
       IEnumerable<ISaveParticipant> SaveParticipants { get; }
   }

   /// <summary>Adapter for controllers which already have explicit capture/apply APIs.</summary>
   public sealed class SaveParticipant<T>(string id, Func<T> capture, Action reset,
       Action<T, SaveRestoreContext> restore, int order = 100, Action<T>? validate = null) : ISaveParticipant
   {
       public string SaveId => id;
       public int RestoreOrder => order;
       public JsonElement CaptureState() => SaveJson.ToElement(capture());
       public void ResetState() => reset();
       public Action PrepareRestore(JsonElement state, SaveRestoreContext context)
       {
           T value = SaveJson.FromElement<T>(state);
           validate?.Invoke(value);
           return () => restore(value, context);
       }
   }
   ```

   `SaveRestoreContext` gives the replay `Warn` (partial restore: the record is kept), `Info`, and `Require` (throws, marks the restore failed) ([ksa-abstractions.lib/Persistence/ISaveParticipant.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/ISaveParticipant.cs#L41-L52)).

2. **Expose participants from the sub-mod.** A feature implements `ISaveParticipantSource` and returns any number of participants. The camera sequence player returns one with an explicit recipe DTO, a validate delegate that builds every recipe before anything is touched, and order 210 so it restores after the native camera pose is loaded:

   ```csharp
   // camera-controller-override.lib/CameraControllerOverrideSubmod.Saves.cs L23-L52
   public IEnumerable<ISaveParticipant> SaveParticipants => new[]
   {
       new SaveParticipant<SavedCameraSequence>("camera-controller-override", () => new()
       {
           Keyframes = _sequencePlayer.Keyframes.Select(k => SavedAnimation.Capture(k.Animation)).ToArray(),
           PendingGroup = _pendingGroupAnimations.Select(a => SavedAnimation.Capture(a)).ToArray(),
           ReturnToStart = _sequencePlayer.ReturnToStartEnabled, ReturnDuration = _sequencePlayer.ReturnToStartDuration,
           ReturnEasing = _sequencePlayer.ReturnToStartEasing, PowerStart = _sequencePlayer.ReturnToStartEasingPowerStart,
           PowerEnd = _sequencePlayer.ReturnToStartEasingPowerEnd
       }, () => { _sequencePlayer.Clear(); _pendingGroupAnimations.Clear(); _groupMode = false; },
       (state, _) =>
       {
           foreach (var recipe in state.Keyframes) _sequencePlayer.AddKeyframe(recipe.Create());
           foreach (var recipe in state.PendingGroup) _pendingGroupAnimations.Add(recipe.Create());
           _groupMode = state.PendingGroup.Length > 0;
           _sequencePlayer.ReturnToStartEnabled = state.ReturnToStart;
           _sequencePlayer.ReturnToStartDuration = state.ReturnDuration;
           _sequencePlayer.ReturnToStartEasing = state.ReturnEasing;
           _sequencePlayer.ReturnToStartEasingPowerStart = state.PowerStart;
           _sequencePlayer.ReturnToStartEasingPowerEnd = state.PowerEnd;
       }, order: 210, validate: state =>
       {
           if (state.Keyframes == null || state.PendingGroup == null || state.Keyframes.Length > 1024 || state.PendingGroup.Length > 128
               || !double.IsFinite(state.ReturnDuration) || state.ReturnDuration < 1 || state.ReturnDuration > 10
               || !Enum.IsDefined(state.ReturnEasing) || !double.IsFinite(state.PowerStart) || state.PowerStart <= 0
               || !double.IsFinite(state.PowerEnd) || state.PowerEnd <= 0)
               throw new InvalidOperationException("Invalid camera sequence settings.");
           foreach (var recipe in state.Keyframes.Concat(state.PendingGroup)) _ = recipe.Create();
       })
   };
   ```

   Kitten Animations shows the other half: `ResetState` returns the feature to a baseline captured at startup (`_originalTuning?.Apply()`), and the restore re-resolves clips by exact (source, label) and warns on an ambiguous match:

   ```csharp
   // kitten-animations.lib/KittenAnimationsSubmod.Persistence.cs L60-L83
   private void ResetAnimations()
   {
       Unbind();
       _driver.Reset();
       _selectedKittenId = null;
       _originalTuning?.Apply();
       ApplyAnimationSettings(new SavedAnimations());
   }

   private void RestoreAnimations(SavedAnimations saved, SaveRestoreContext context)
   {
       context.Require(!saved.ClipPhase.HasValue || (saved.ClipPhase.Value >= 0 && KittenPlaybackPhase.IsAvailable),
           "Saved animation phase or native playback fields are unavailable.");
       _selectedKittenId = saved.SelectedKittenId;
       saved.Tuning?.Apply();
       ApplyAnimationSettings(saved);
       Update(0);
       if (_context != null) _context.ExpressionVariant = saved.ExpressionVariant;
       if (_context != null && saved.ClipSource != null)
       {
           var matches = _context.Catalog.Groups.SelectMany(g => g.Entries)
               .Where(e => e.Source == saved.ClipSource && e.Label == saved.ClipLabel).ToArray();
           if (matches.Length == 1) _driver.RestorePlayback(matches[0], saved.ClipActive, saved.ClipPaused, saved.ClipPhase);
           else context.Warn($"Saved animation clip unavailable or ambiguous: {saved.ClipLabel}.");
   ```

3. **Capture** each participant into a `SaveDocument` inside its own `try`/`catch`. Records that could not be restored on the last load (`_retained`) are copied through unchanged and their participants skipped, so a feature's live edits cannot overwrite the unrestored original; a failing capture falls back to the last known good record:

   ```csharp
   // ksa-abstractions.lib/Persistence/SceneSaveCoordinator.cs L33-L59
   public SaveDocument Capture()
   {
       _messages.Clear();
       var document = new SaveDocument();
       foreach (var (id, record) in _retained) document.Features.Add(id, record);
       foreach (var participant in _participants)
       {
           if (_retained.ContainsKey(participant.SaveId))
           {
               Report($"{participant.SaveId}: preserving unrestored saved state; current edits for this feature cannot replace it.");
               continue;
           }
           try
           {
               document.Features[participant.SaveId] = new() { Version = participant.SaveVersion, State = participant.CaptureState() };
               _lastKnown[participant.SaveId] = document.Features[participant.SaveId];
           }
           catch (Exception ex)
           {
               if (_lastKnown.TryGetValue(participant.SaveId, out var previous)) document.Features[participant.SaveId] = previous;
               Report($"{participant.SaveId}: capture failed; {(previous == null ? "no earlier state available" : "earlier state retained")}: {ex.Message}");
           }
       }
       document.Warnings.AddRange(_messages);
       Status = $"Captured {document.Features.Count} feature records" + (_messages.Count == 0 ? "." : $" with {_messages.Count} warnings.");
       return document;
   }
   ```

4. **Prepare the load before the world is touched.** Validate the document, compare each record's id and version with the participant, retain unknown or version-mismatched records (never guess a migration), and turn the others into restore closures. A missing sidecar (`document == null`) means "reset everything, restore nothing":

   ```csharp
   // ksa-abstractions.lib/Persistence/SceneSaveCoordinator.cs L61-L94
   public void PrepareLoad(SaveDocument? document)
   {
       if (document != null) SaveStorage.Validate(document);
       _previousRetained ??= new(_retained, StringComparer.Ordinal);
       _worldReset = false;
       _restoreCompleted = false;
       _messages.Clear();
       _prepared.Clear();
       _retained.Clear();
       _incoming = document;
       _loadPending = true;
       if (document == null) { Status = "Native save has no Unscience state; scene setups will be cleared."; return; }
       foreach (string warning in document.Warnings) Report($"Saved warning: {warning}");
       foreach (var (id, record) in document.Features)
       {
           var participant = _participants.FirstOrDefault(p => p.SaveId == id);
           if (participant == null || participant.SaveVersion != record.Version)
           {
               _retained[id] = record;
               Report($"{id}: unsupported feature/version {record.Version}; retained for a compatible build.");
               continue;
           }
           try
           {
               var context = new SaveRestoreContext(message => Report($"{id}: {message}"));
               _prepared.Add((participant, participant.PrepareRestore(record.State, context), context));
           }
           catch (Exception ex)
           {
               _retained[id] = record;
               Report($"{id}: invalid saved state retained: {ex.Message}");
           }
       }
   }
   ```

5. **Reset in reverse order, restore in order.** A participant whose reset throws is remembered and excluded from restore; a restore that warns or throws keeps its original record in the retained set. The prepared list is cleared so closures replay at most once:

   ```csharp
   // ksa-abstractions.lib/Persistence/SceneSaveCoordinator.cs L96-L146
   public void ResetWorld()
   {
       if (!_loadPending)
       {
           _messages.Clear();
           _prepared.Clear();
           _retained.Clear();
           _incoming = null;
       }
       _worldReset = true;
       _lastKnown.Clear();
       _failedReset.Clear();
       foreach (var participant in _participants.AsEnumerable().Reverse())
       {
           try { participant.ResetState(); }
           catch (Exception ex)
           {
               _failedReset.Add(participant.SaveId);
               Report($"{participant.SaveId}: cleanup failed; restore skipped: {ex.Message}");
           }
       }
       Status = "Old scene setup cleared.";
   }

   public void RestoreWorld()
   {
       int restored = 0;
       foreach (var (participant, restore, context) in _prepared.OrderBy(p => p.Participant.RestoreOrder))
       {
           try
           {
               if (_failedReset.Contains(participant.SaveId)) throw new InvalidOperationException("Old ownership could not be released.");
               restore();
               if (_incoming?.Features.TryGetValue(participant.SaveId, out var record) == true)
               {
                   _lastKnown[participant.SaveId] = record;
                   if (context.HasWarnings) _retained[participant.SaveId] = record;
               }
               restored++;
           }
           catch (Exception ex)
           {
               if (_incoming?.Features.TryGetValue(participant.SaveId, out var original) == true)
                   _retained[participant.SaveId] = original;
               Report($"{participant.SaveId}: restore failed; saved state retained: {ex.Message}");
           }
       }
       _restoreCompleted = true;
       _prepared.Clear(); // Replay at most once, even for direct DeserializeSave callers.
       Status = $"Restored {restored} feature records" + (_messages.Count == 0 ? "." : $" with {_messages.Count} warnings; see details.");
   }
   ```

6. **Finish the load.** If the load aborted before reset, the previous retained set is restored; if reset ran but the native rebuild never completed, all incoming records are retained for recovery:

   ```csharp
   // ksa-abstractions.lib/Persistence/SceneSaveCoordinator.cs L148-L164
   public void FinishLoad()
   {
       if (_loadPending && !_worldReset && _previousRetained != null)
       {
           _retained.Clear();
           foreach (var item in _previousRetained) _retained.Add(item.Key, item.Value);
       }
       else if (_loadPending && _worldReset && !_restoreCompleted && _incoming != null)
       {
           foreach (var item in _incoming.Features) _retained[item.Key] = item.Value;
           Report("Native load did not complete; incoming feature records retained for recovery.");
       }
       _previousRetained = null;
       _prepared.Clear();
       _incoming = null;
       _loadPending = false;
   }
   ```

7. **Store one document, atomically, bound to its universe.** `SaveStorage` stamps the SHA-256 of the sibling `universe.xml`, enforces limits (32 MiB, 128 records, 1024 warnings, ids at most 128 characters, JSON depth 64), rejects duplicate JSON property names with a recursive walk, writes a uniquely named temp file with `FileMode.CreateNew`, flushes to disk and moves it over `unscience.json`. `Read` recomputes the hash and refuses a sidecar copied next to a different `universe.xml`:

   ```csharp
   // ksa-abstractions.lib/Persistence/SaveStorage.cs L15-L28
   public static SaveDocument? Read(string directory)
   {
       string path = Path.Combine(directory, FileName);
       if (!File.Exists(path)) return null;
       using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
       if (stream.Length > MaximumBytes) throw new InvalidDataException("Unscience save exceeds the 32 MiB limit.");
       using var json = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = SaveJson.Options.MaxDepth });
       ValidateUniqueProperties(json.RootElement);
       var document = SaveJson.FromElement<SaveDocument>(json.RootElement);
       Validate(document);
       if (!string.Equals(document.UniverseSha256, HashUniverse(directory), StringComparison.OrdinalIgnoreCase))
           throw new InvalidDataException("Unscience state belongs to a different universe.xml; it was not restored.");
       return document;
   }
   ```

   ```csharp
   // ksa-abstractions.lib/Persistence/SaveStorage.cs L30-L51
   public static void Write(string directory, SaveDocument document)
   {
       document.UniverseSha256 = HashUniverse(directory);
       Validate(document);
       byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, SaveJson.Options);
       if (bytes.Length > MaximumBytes) throw new InvalidDataException("Unscience save exceeds the 32 MiB limit.");
       string path = Path.Combine(directory, FileName);
       string temporary = Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
       try
       {
           using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
           {
               stream.Write(bytes);
               stream.Flush(flushToDisk: true);
           }
           File.Move(temporary, path, overwrite: true);
       }
       finally
       {
           if (File.Exists(temporary)) File.Delete(temporary);
       }
   }
   ```

   The document shape:

   ```csharp
   // ksa-abstractions.lib/Persistence/SaveDocument.cs L7-L20
   public sealed class SaveDocument
   {
       public int SchemaVersion { get; set; } = 1;
       public string UniverseSha256 { get; set; } = "";
       public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
       public Dictionary<string, SaveFeature> Features { get; set; } = new(StringComparer.Ordinal);
       public List<string> Warnings { get; set; } = new();
   }

   public sealed class SaveFeature
   {
       public int Version { get; set; } = 1;
       public JsonElement State { get; set; }
   }
   ```

8. **Serialize with options built for Brutal vector types.** Brutal vectors expose writable swizzle properties (`XY`, `XYZ`, ...) that recurse; the options strip every property of a `Brutal.Numerics` object type except the scalar `X`/`Y`/`Z`/`W`, throw for a numeric type that has no scalar axes (it needs an explicit DTO), and reject non-finite numbers:

   ```csharp
   // ksa-abstractions.lib/Persistence/SaveJson.cs L13-L45
       private static JsonSerializerOptions CreateOptions()
       {
           var resolver = new DefaultJsonTypeInfoResolver();
           resolver.Modifiers.Add(info =>
           {
               if (info.Type.Namespace != "Brutal.Numerics" || info.Kind != JsonTypeInfoKind.Object) return;
               // Brutal vectors expose writable swizzles (XY/XYZ/etc.). Those recursively expose
               // more swizzles even with IgnoreReadOnlyProperties, so allow only scalar axes.
               for (int i = info.Properties.Count - 1; i >= 0; i--)
               {
                   var property = info.Properties[i];
                   if (property.Name is not ("X" or "Y" or "Z" or "W") || !property.PropertyType.IsPrimitive)
                       info.Properties.RemoveAt(i);
               }
               if (info.Properties.Count == 0)
                   throw new NotSupportedException($"Numeric type {info.Type.Name} needs an explicit save representation.");
           });
           return new()
           {
               IncludeFields = true,
               IgnoreReadOnlyProperties = true,
               WriteIndented = true,
               MaxDepth = 64,
               NumberHandling = JsonNumberHandling.Strict,
               PropertyNameCaseInsensitive = false,
               TypeInfoResolver = resolver,
               Converters = { new FiniteSingleConverter(), new FiniteDoubleConverter() }
           };
       }
       public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
       public static T FromElement<T>(JsonElement value) => value.Deserialize<T>(Options)
           ?? throw new InvalidOperationException($"Missing {typeof(T).Name} state.");
   }
   ```

## Complete example

| File | Role |
|---|---|
| [ksa-abstractions.lib/Persistence/ISaveParticipant.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/ISaveParticipant.cs) | Contract, adapter, restore context |
| [ksa-abstractions.lib/Persistence/SceneSaveCoordinator.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/SceneSaveCoordinator.cs) | Capture, prepare, reset, restore, finish, retention |
| [ksa-abstractions.lib/Persistence/SaveStorage.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/SaveStorage.cs) and [ksa-abstractions.lib/Persistence/SaveDocument.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/SaveDocument.cs) | Bounded, hash-bound, atomic file I/O and document shape |
| [ksa-abstractions.lib/Persistence/SaveJson.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/SaveJson.cs) | Serializer options and the Brutal vector modifier |
| [camera-controller-override.lib/CameraControllerOverrideSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/CameraControllerOverrideSubmod.Saves.cs) | A participant with an explicit recipe DTO |
| [kitten-animations.lib/KittenAnimationsSubmod.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitten-animations.lib/KittenAnimationsSubmod.Persistence.cs) | A participant with a startup baseline and ambiguity handling |

## Variations and gotchas

- **Ids and versions are public contracts.** A payload change needs a new record id or an explicit migration; a version mismatch is retained, not migrated.
- **Retention is sticky.** A retained record is copied into every later save; the user has to choose "Use current setup" (`UseCurrentSetup`) to drop retained records that this build understands.
- **A stale fallback.** A capture failure falls back to the last known record, which may be stale; the warning is stored in the document.
- **Only `float` and `double` get finite-number converters**; other numeric types use the defaults.
- **The coordinator never deletes a sidecar.** KSA's save wipes the folder anyway; see [Store per-save mod data next to a KSA save and notice when the player saves](../save-mod-state-with-the-game-save/overview.md) for what that means for placement.
- **References across saves.** Records that point at a vehicle or part should use a stable address, not an object reference or a runtime counter (see [Identify vessels and parts stably across saves](../identify-vessels-and-parts-across-saves/overview.md)).

## See also

- [Pair each scene capture with its native write by object identity](save-mod-state-with-the-scene-2.md), [Hook native save and load with five patches, eight callbacks and a deferred world change](save-mod-state-with-the-scene-3.md), [overview](./overview.md)
- [Scene save coordinator with ordered reset, deferred replay and retained records](../../../../mods/unscience/features/lib-scene-save-coordinator.md), [Keyframe camera sequences with parallel groups, return-to-start and saved recipes](../../../../mods/unscience/features/camera-controller-override-keyframe-sequences.md), [Live animation tuning on KittenLocomotionTuning.Current with a scoped reset and a scene-save record](../../../../mods/unscience/features/kitten-animations-locomotion-tuning.md)
- [Identify vessels and parts stably across saves](../identify-vessels-and-parts-across-saves/overview.md), [Animate the flight camera with eased moves, keyframe sequences and saved recipes](../../../camera/special-views/chain-camera-moves-into-keyframe-sequences/overview.md), [Restore kitten colours, clips and tuning after a save load](../../../kittens/characters/restore-kitten-customizations-after-a-load/overview.md)
