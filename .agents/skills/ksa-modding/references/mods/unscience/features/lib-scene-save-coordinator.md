---
type: Mod Feature
title: Scene save coordinator with ordered reset, deferred replay and retained records
description: A game-independent coordinator captures versioned JSON records from feature participants, validates incoming records before the world is touched, resets old ownership in reverse order and replays restores in order after KSA rebuilds the world
mod: unscience
category: persistence
techniques: [save-sidecar, json-config]
hooks:
  - harmony-postfix KSA.GameSave.Populate (via NativeSaveHooks.Capturing)
  - harmony-prefix KSA.UncompressedSave.Load (via NativeSaveHooks.Loading)
  - harmony-prefix KSA.Universe.DeserializeSave (via NativeSaveHooks.Resetting)
  - harmony-postfix KSA.Universe.DeserializeSave (via NativeSaveHooks.Restoring)
  - harmony-finalizer KSA.UncompressedSave.Load (via NativeSaveHooks.LoadFinished)
lifecycle: [event:save, event:load, frame:prepare]
game_types: [Brutal.Numerics.float3, Brutal.Numerics.double3]
tags: [saves, json, versioning, ordering, failure-isolation, dto, sidecar]
---

Thirty-five feature records from thirty sub-mods have to survive a KSA save and come back on
load: some own native objects (lights, grids, cameras), some only remember settings, and any of them
can be missing, newer than this build, or fail halfway. `SceneSaveCoordinator` is the
engine-independent core that makes that safe. It owns no KSA types; it talks to features only
through `ISaveParticipant` and to disk through `SaveStorage`, which is why the same file links into
native-free test executables.[^scene-save-coordinator][^isave-participant]

Mechanism in one line: on save, each participant returns a detached `JsonElement` that is stored as
a versioned record in one `SaveDocument`; on load, every record is deserialized and validated into a
ready-to-run restore closure before the world is touched, old ownership is released in reverse order
while the old world still exists, and the closures run in ascending order after KSA has rebuilt the
new world.

## How it works

**Participant contract** (`ISaveParticipant`):

| Member | Contract |
|---|---|
| `SaveId` | Stable record key (`"graffiti"`, `"pebbles.clutter-state"`); duplicates throw in the coordinator constructor |
| `SaveVersion` (default 1) | Record version; a mismatch is retained, never guessed |
| `RestoreOrder` (default 100) | Replay order; reset runs in the reverse order |
| `CaptureState()` | Detached JSON of the feature's durable state |
| `ResetState()` | Synchronously release old-world ownership before native objects are destroyed |
| `PrepareRestore(state, context)` | Deserialize and validate without touching the world; return the replay `Action` |

`SaveParticipant<T>` adapts a feature that already has typed capture/apply methods: it serializes a
DTO with `SaveJson`, runs an optional `validate` delegate in `PrepareRestore`, and closes over the
typed value for replay. A sub-mod exposes records by implementing `ISaveParticipantSource` (any number
of records) or `ISaveParticipant` directly (Dent Wizard registers an empty record so that its reset
still runs on every load).[^dent-wizard-submod]
`SaveRestoreContext` gives the replay `Warn` (partial restore, keeps the record), `Info` and
`Require` (throws, marks the restore failed).

**Coordinator state machine:**

| Step | Called from | Behavior |
|---|---|---|
| `Capture()` | save, after native `Populate` | Copies retained (unrestored) records into the document and skips their participants; captures every other participant in its own `try/catch`; a failing capture falls back to the last known good record for that id (last capture or last successful restore) and adds a warning |
| `PrepareLoad(doc)` | load replay, before native read | Validates the document; snapshots the currently retained set for rollback; unknown ids or version mismatches are retained; each known record is turned into a restore closure, invalid ones are retained; a missing sidecar means "reset everything, restore nothing" |
| `ResetWorld()` | prefix of `DeserializeSave`, after worker joins | Calls every participant's `ResetState` in reverse `RestoreOrder`; a participant whose reset throws is excluded from restore |
| `RestoreWorld()` | postfix of `DeserializeSave` | Runs prepared closures in ascending `RestoreOrder`; a restore with warnings or an exception keeps its original record in the retained set; prepared closures are then cleared so they replay at most once |
| `FinishLoad()` | finalizer of `Load` | If the load aborted before reset, restores the previous retained set; if reset ran but restore never did (native reconstruction threw), retains all incoming records for recovery |
| `UseCurrentSetup()` | toolbox button | Drops retained records this build understands so the next save captures the live setup instead |

Retention is the key policy: a record that could not be restored is written back unchanged into
every later save, and the feature's current edits do not replace it until the user chooses "Use
current setup". Unknown or newer records survive an older build round-trip.

**Document and file** (`SaveDocument`, `SaveStorage`):[^save-document][^save-storage]

- `SchemaVersion` (must be 1), `UniverseSha256`, `CreatedUtc`, `Features` (id -> `{ Version, State }`),
  `Warnings`.
- Fixed name `unscience.json` in the save directory; reads and writes are capped at 32 MiB, 128
  records, 1024 warnings, ids of at most 128 characters, JSON depth 64; duplicate JSON property names
  are rejected by a recursive walk because `System.Text.Json` would otherwise keep the last one.
- `Write` stamps the SHA-256 of the sibling `universe.xml`, serializes, writes a uniquely named
  temporary file with `FileMode.CreateNew`, flushes to disk, then `File.Move(..., overwrite: true)`.
  `Read` recomputes the hash and refuses a sidecar copied next to a different `universe.xml`.

**Serializer** (`SaveJson`, `FiniteNumberConverters`):[^save-json][^finite-number-converters]

- `IncludeFields`, `IgnoreReadOnlyProperties`, strict number handling, case-sensitive names, indented.
- A type-info modifier strips every property of `Brutal.Numerics` object types except primitive
  `X`/`Y`/`Z`/`W`, because Brutal vectors expose writable swizzle properties (`XY`, `XYZ`, ...) that
  would otherwise recurse; a numeric type with no scalar axes throws `NotSupportedException` so it needs
  an explicit DTO.
- `float`/`double` converters reject values that parse to infinity (for example `1e999`), which strict
  JSON syntax still allows; writing NaN fails in `Utf8JsonWriter`.

Related runtime helper: `MaterialColorState` records the last known albedo per mod-owned GPU material
handle so adapters can capture effective colors; handles are explicitly not save identities and are
forgotten when a slot is released.[^material-color-state]

**A representative adapter.** Thug Life saves its textured quads as `SavedQuad` DTOs that address the
host part with a [stable part reference](./lib-save-part-identity.md); reset removes entries but keeps
shared renderer resources; replay re-resolves the part and warns (retaining the record) when it is
missing. The same shape (DTO, capture, reset, restore, validate, order) repeats in every
`*.Saves.cs` / `*.Persistence.cs` file; the full list is in
[the sidecar feature](./unscience-scene-save-sidecar.md).[^thug-life-persistence]

The design separates desired configuration, durable bindings, native resources (rebuilt, never
serialized), ownership baselines and progress, as laid out in the authors' plans.[^plans-saves][^plans-saves-runtime-models]

## Lifecycle and timing

The coordinator itself has no hooks; it runs inside [NativeSaveHooks](./lib-native-save-hooks.md)
callbacks, all on the main thread:

| Coordinator call | Native moment | Frame phase |
|---|---|---|
| `Capture` | Postfix of `GameSave.Populate`, after `UniverseData.Create()` | Wherever the save was issued: `frame:input` (console) or `frame:draw-ui` (GAME SAVES window, auto save) |
| `PrepareLoad` | Prefix of `UncompressedSave.Load`, during the deferred replay | `frame:prepare`, before `Universe.GetJobSimStep` |
| `ResetWorld` | Prefix of `Universe.DeserializeSave`, after all job systems were waited on | `frame:prepare` |
| `RestoreWorld` | Postfix of `Universe.DeserializeSave`, after native vehicles, roster and camera exist | `frame:prepare`; the next physics step is computed and snapshotted right after |
| `FinishLoad` | Finalizer of `UncompressedSave.Load` | `frame:prepare` |

Consequences for participants:

- `CaptureState` may run while vehicle workers are integrating the next step (UI-issued saves), so it
  must read only main-thread, mod-owned state and must not mutate the world.
- `ResetState` runs while the old native objects still exist and no worker owns them: the right moment
  to unpatch per-vehicle registrations, release GPU materials or FMOD streams, and drop references.
- Replay closures run after reconstruction and before the next solver dispatch, so a restore that edits
  vessels (scale, welds) does not race the workers and is included in the next snapshot. They also run
  before `PrepareImGui`, so they can create or release UI textures safely.
- `PrepareRestore` must be side-effect free: it runs before KSA has decided whether the native load
  will succeed.

## Key code

| Path | Role |
|---|---|
| [ksa-abstractions.lib/Persistence/SceneSaveCoordinator.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/SceneSaveCoordinator.cs) | Capture, prepare, reset, restore, finish, retention and status messages |
| [ksa-abstractions.lib/Persistence/ISaveParticipant.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/ISaveParticipant.cs) | Participant contract, `SaveParticipant<T>` adapter, `SaveRestoreContext` |
| [ksa-abstractions.lib/Persistence/SaveDocument.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/SaveDocument.cs) | Versioned document and record DTOs |
| [ksa-abstractions.lib/Persistence/SaveStorage.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/SaveStorage.cs) | Bounded, hash-bound, atomic file I/O |
| [ksa-abstractions.lib/Persistence/SaveJson.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/SaveJson.cs) | Serializer options and Brutal vector modifier |
| [ksa-abstractions.lib/Persistence/FiniteNumberConverters.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/FiniteNumberConverters.cs) | Overflow-rejecting float/double converters |
| [thug-life.lib/ThugLifeSubmod.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/thug-life.lib/ThugLifeSubmod.Persistence.cs) | Representative feature adapter |
| [unscience/UnscienceSaves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/UnscienceSaves.cs#L20-L33) | Builds the coordinator from all sub-mods |
| [saves.tests/StorageTests.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/saves.tests/StorageTests.cs) | Managed checks for storage and coordinator behavior |

## Snippets

The participant contract and its typed adapter:

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

Ordered replay with per-participant isolation and retention:

```csharp
// ksa-abstractions.lib/Persistence/SceneSaveCoordinator.cs L120-L146
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

Hash-bound atomic write:

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

A representative feature adapter (Thug Life):

```csharp
// thug-life.lib/ThugLifeSubmod.Persistence.cs L12-L68
    public IEnumerable<ISaveParticipant> SaveParticipants
    {
        get
        {
            yield return new SaveParticipant<List<SavedQuad>>("thug-life", CaptureQuads, ResetQuads, RestoreQuads,
                validate: saved =>
                {
                    if (saved.Count > 100000 || saved.Any(q => q == null || q.Anchor == null || q.Width <= 0 || q.Height <= 0))
                        throw new InvalidOperationException("Invalid saved sunglasses quad records.");
                });
        }
    }
// ...
    private void RestoreQuads(List<SavedQuad> entries, SaveRestoreContext context)
    {
        foreach (var item in entries)
        {
            var part = item.Anchor.Resolve();
            var vehicle = VehicleProvider.FindVehicle(item.Anchor.VehicleId);
            if (part == null || vehicle == null) { context.Warn($"Sunglasses anchor missing: {item.Anchor.VehicleId}."); continue; }
            context.Require(item.Width > 0 && item.Height > 0, "Sunglasses dimensions must be positive.");
// ...
            if (_manager == null || !_manager.Add(entry)) context.Warn(_manager?.LastError ?? "Sunglasses renderer unavailable.");
        }
    }
```

## Game APIs touched

The coordinator, document, storage and serializer reference no KSA types. The only engine-defined
types they handle are Brutal numeric structs inside feature DTOs:

| Type | Decomp | Use |
|---|---|---|
| `Brutal.Numerics.float3`, `float4` (and the other `Brutal.Numerics` vectors) | [current/decomp/Brutal.Numerics/float3.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Numerics/float3.cs) | Serialized through their scalar `X`/`Y`/`Z`/`W` members only; swizzle properties are removed by the `SaveJson` modifier |

Game objects reached by adapters (vehicles, parts) are resolved through
[stable identities](./lib-save-part-identity.md), and native timing comes from
[NativeSaveHooks](./lib-native-save-hooks.md).

## Pitfalls and version notes

- `SaveParticipants` getters can capture baselines when they are first enumerated (Skittles captures
  the startup theme there); the host enumerates them once, when it constructs the coordinator during
  `[StarMapAllModsLoaded]`.
- Record ids and versions are public contracts: the repo rules require a new record or an explicit
  migration for payload changes, because a version mismatch is retained rather than migrated.
- Retained records are copied into new saves verbatim. A feature that keeps failing to restore keeps
  re-saving its old payload until the user presses "Use current setup for future saves".
- A capture failure falls back to the last known record, which may be stale; the warning is stored in
  the document's `Warnings`.
- Only `float` and `double` get finite-number converters; other numeric types use the defaults.
- The coordinator never deletes a sidecar. A native save without Unscience state loads with
  `PrepareLoad(null)`, which resets every feature and restores nothing.

## See also

- [Native save and load hooks](./lib-native-save-hooks.md) (when each call runs)
- [Unscience scene save sidecar](./unscience-scene-save-sidecar.md) (host wiring, participant table, status UI)
- [Stable part identity across reloads](./lib-save-part-identity.md)
- [PrepareFrame handoff](./lib-physics-frame-hook.md)
- [Managed test harnesses](./unscience-managed-test-harnesses.md)
- [Mod overview](../overview.md)
- [Persist feature state as ISaveParticipant records through a coordinator that retains what it cannot restore](../../../howto/persistence/saves/save-mod-state-with-the-scene/save-mod-state-with-the-scene-1.md) - how-to article demonstrating this feature

[^scene-save-coordinator]: ksa-abstractions.lib/Persistence/SceneSaveCoordinator.cs
[^isave-participant]: ksa-abstractions.lib/Persistence/ISaveParticipant.cs
[^dent-wizard-submod]: dent-wizard.lib/DentWizardSubmod.cs
[^save-document]: ksa-abstractions.lib/Persistence/SaveDocument.cs
[^save-storage]: ksa-abstractions.lib/Persistence/SaveStorage.cs
[^save-json]: ksa-abstractions.lib/Persistence/SaveJson.cs
[^finite-number-converters]: ksa-abstractions.lib/Persistence/FiniteNumberConverters.cs
[^material-color-state]: ksa-abstractions.lib/Persistence/MaterialColorState.cs
[^thug-life-persistence]: thug-life.lib/ThugLifeSubmod.Persistence.cs
[^plans-saves]: plans/SAVES.md (authors' save design plan)
[^plans-saves-runtime-models]: plans/saves-runtime-models.md (authors' runtime-state taxonomy)
