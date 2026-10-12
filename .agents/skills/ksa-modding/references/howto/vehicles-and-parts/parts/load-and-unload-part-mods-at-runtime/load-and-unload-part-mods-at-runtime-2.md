---
type: How-To
title: Unload runtime-registered parts with a fail-closed gate and an ordered purge
description: Refuse to unload while anything live might reference the mod, then remove exactly what the load registered in an order that never frees a GPU object something can still draw
category: vehicles-and-parts
group: parts
topic: load-and-unload-part-mods-at-runtime
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/parts-now-runtime-unload-purge]
techniques: [reflection, starmap-lifecycle]
hooks: [starmap StarMapBeforeGui, reflection KSA.SerializedCollection._collection]
lifecycle: [frame:draw-ui, startup:all-mods-loaded]
game_types:
  - KSA.ModLibrary
  - KSA.PartTemplate
  - KSA.SerializedCollection
  - KSA.PartModel
  - KSA.VehicleEditor
  - KSA.Vehicle
  - KSA.TextureReference
  - KSA.MeshReference
tags: [unload, purge, gate, registries, gpu, rollback, thumbnails]
---
Outcome: you can unload or reload a part mod that your own code loaded at runtime, without leaving dangling
references or freeing something the renderer is about to draw. KSA never unloads content, so this is entirely the
mod's job: refuse when anything live might still point at the mod, then run a numbered purge, each step isolated.

Prerequisites: the loader of [article 1](load-and-unload-part-mods-at-runtime-1.md) (you need a record of what a
load registered). Demonstrated by [unscience/parts-now-runtime-unload-purge](../../../../mods/unscience/features/parts-now-runtime-unload-purge.md). Only mods the loader itself
loaded this session can be unloaded; boot mods are refused outright.

## Background: how the game does it

- The registries only grow: no API unloads a mod. `SerializedCollection<T>.Deregister(item)` is public but
  swap-removes (last element moved into the freed slot) [current/decomp/KSA/SerializedCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedCollection.cs#L37-L55)[^ksa-serializedcollection]; Parts Now removes by
  `List.Remove` (order preserved) plus `TryRemove` on the private `_collection` dictionary by reflection
  [current/decomp/KSA/SerializedCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedCollection.cs#L12-L16). Removing from only one leaves `Find` resolving a purged item, or the
  browser listing it.
- Static caches never prune: `PartModel.Instances`, ray-tracer lists, and so on.
- `PartInstance.GetTemplate()` is `ModLibrary.Get<PartTemplate>`, which throws when the template is gone, so a
  live `Part` or an editor holding a purged template crashes later [current/decomp/KSA/PartInstance.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartInstance.cs#L113-L116)[^ksa-partinstance].
- Disposals (`ThumbnailReference.Dispose`, `TextureReference.Dispose(Device)`) free GPU objects and are not
  idempotent.

## Lifecycle and timing

Every purge runs in the pre-GUI hook (`StarMapBeforeGui`, a prefix on `Program.OnDrawUiFrame`). A reload purges at
the end of its `Validate` state; a failed load unwinds inside `Step()`. An unload **confirmed in a modal** is not
run where the modal is drawn: that is `StarMapAfterGui`, after `OnDrawUiFrame` already drew the editor's part
browser with `ImageButton`s bound to the mod's thumbnail descriptor sets, and before `ImGui.Render`. Freeing them
there is a use-after-free in this frame's draw data, and `WaitIdle` cannot help because the frame is not yet
submitted. So the confirm handler stores the mod id and the next pre-GUI `Update` runs it before anything draws.
Vehicle state is only read by the gate, never written, so the solver race (see [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md))
does not matter. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Gate, fail closed.** Return a refusal sentence or null. Refuse while a load job is running, if any live vehicle
   (debris included) holds one of the mod's part ids, if the open editor holds one, and on any exception while
   checking:

```csharp
// parts-now.lib/Runtime/RuntimeModUnloadGate.cs L34-63
    internal static string? Check(LoadedModRecord record, bool loadJobInFlight)
    {
        if (record is null)
        {
            return "there is no loaded-mod record to unload.";
        }

        try
        {
            if (loadJobInFlight)
            {
                return "a parts-now load job is still running — wait for it to finish, then unload '"
                    + record.ModId + "'.";
            }

            if (record.PartIds.Count == 0)
            {
                // Nothing part-shaped was registered, so no live object can be referencing it.
                return null;
            }

            return CheckLiveVehicles(record) ?? CheckVehicleEditor(record);
        }
        catch (Exception ex)
        {
            // Fail closed. A gate that cannot see the game state must never conclude "safe".
            return "could not verify that '" + record.ModId + "' is unused (" + ex.Message
                + ") — refusing to unload.";
        }
    }
```

2. **Walk every live part including subparts**, because a purged template under a live `Part` crashes the editor
   or browser later:

```csharp
// parts-now.lib/Runtime/RuntimeModUnloadGate.cs L69-91
    private static string? CheckLiveVehicles(LoadedModRecord record)
    {
        // VehicleProvider resolves Universe.CurrentSystem and returns an empty list when there is no
        // system loaded; PartHelpers walks vehicle.Parts.Parts and recurses Part.SubParts.
        // Debris counts: a fragment shed by KSA 5402's part failure is a live vehicle holding real
        // parts, so a runtime template it still references must keep the mod pinned. This gate fails
        // closed, and a list that hides debris could wrongly conclude "safe".
        foreach (Vehicle vehicle in VehicleProvider.GetAllVehicles(includeDebris: true))
        {
            foreach (Part part in PartHelpers.GetAllParts(vehicle))
            {
                PartTemplate template = part.Template;
                if (template is not null && record.PartIds.Contains(template.Id))
                {
                    return "vehicle '" + vehicle.Id + "' is flying part '" + template.Id
                        + "' from '" + record.ModId + "' — unloading it would leave that vehicle "
                        + "holding a freed part template.";
                }
            }
        }

        return null;
    }
```

3. **Mark the record purged before any work**, since the GPU disposals double-free if repeated, then clear the
   hover preview, wait for the device to go idle, and run the numbered steps:

```csharp
// parts-now.lib/Runtime/RuntimeModUnloader.cs L88-94, L126-164
        List<FileReference> otherFiles = new List<FileReference>();
        foreach (FileReference file in record.NewFiles)
        {
            if (file is TextureReference texture)
            {
                textures.Add(texture);
            }
// ...

        Step(log, 2, "dispose + unregister part templates",
            () => RuntimeModPurgeSteps.PurgeParts(record));
        Step(log, 3, "unregister part game data",
            () => RuntimeModPurgeSteps.PurgeGameData(record));
        Step(log, 4, "purge model instances and ray tracers",
            () => RuntimeModPurgeSteps.PurgeModelInstances(record));
        Step(log, 5, "dispose + unregister textures",
            () => RuntimeModPurgeSteps.PurgeTextures(textures, renderer));
        Step(log, 6, "measure, dispose + unregister meshes",
            () => RuntimeModPurgeSteps.PurgeMeshes(record, recordLeak));
        Step(log, 7, "unregister remaining file references",
            () => RuntimeModPurgeSteps.PurgeFiles(otherFiles));
        Step(log, 8, "unregister materials",
            () => RuntimeModPurgeSteps.PurgeMaterials(record));
        Step(log, 9, "drop loaders and binders",
            () => RuntimeModPurgeSteps.PurgeLoadersAndBinders(record));
        Step(log, 10, "refresh the vehicle editor", () =>
        {
            // Same nudge a load needs: PartWindow._diameterCache is built lazily and would otherwise
            // still offer diameters that only the purged parts provided.
            EditorRefresh.AfterLoad();
            return "part diameter cache reset";
        });
        Step(log, 11, "forget the mod record", () =>
            RuntimeModRegistry.Remove(record.ModId) ? "record removed" : "record was not registered");

        return log;
    }

    /// <summary>
    /// T11.4 — unwinds a load that failed part-way through.
    /// </summary>
    /// <param name="record">The record, populated with whatever the load managed to register.</param>
    /// <returns>The purge log plus the cursor-restore line.</returns>
    /// <remarks>
    /// This is the same purge over a partially populated record, followed by rewinding the shared
    /// interleaved buffer's bump cursors to <see cref="LoadedModRecord.CursorsBefore" />. Rewinding
    /// is only valid because a rollback aborts <b>before</b> anything was bound: a bound mesh has
```

4. **Remove from a `SerializedCollection` completely** (list and hash index):

```csharp
// parts-now.lib/Runtime/GameRegistry.cs L144-165
    public static bool Unregister<T>(SerializedCollection<T> collection, T item)
        where T : ILibraryData, IListable
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(item);

        // GetList() hands back the live backing List<T>, so this really does remove it.
        bool removedFromList = collection.GetList().Remove(item);

        FieldInfo field = CollectionFields<T>.Collection
            ?? throw Missing("SerializedCollection<" + typeof(T).Name + ">._collection");

        if (field.GetValue(collection) is not ConcurrentDictionary<KeyHash, T> dictionary)
        {
            throw new InvalidOperationException(
                "parts-now: SerializedCollection<" + typeof(T).Name
                + ">._collection is not a ConcurrentDictionary<KeyHash, T> — KSA internals changed.");
        }

        bool removedFromDictionary = dictionary.TryRemove(item.Hash, out _);
        return removedFromList || removedFromDictionary;
    }
```

5. **Prune the model caches by object identity** of the template, not by id: `PartModel.Get` reuses an existing
   `PartModel` whose `Template.Id` matches, and `TemplateDataBase.Id` is optional and not unique, so matching by id
   would miss id-less templates (leaving a model that a reloaded part would reuse, pointing at purged mesh
   offsets) and could evict another mod's models.
6. **Rollback is the same purge** on a partially filled record, with leak accounting off and the mesh cursors
   rewound afterwards. It is valid only before the first bind:

```csharp
// parts-now.lib/Runtime/RuntimeModUnloader.cs L169-200
    public static List<string> Rollback(LoadedModRecord record)
    {
        // recordLeak: false — the cursor rewind below reclaims those bytes, so charging them to the
        // leak counter as well would double-count them against the headroom.
        List<string> log = Purge(record, recordLeak: false);

        if (record is null)
        {
            return log;
        }

        try
        {
            if (MeshBudget.RestoreCursors(record.CursorsBefore))
            {
                Log(log, "rollback: mesh allocation cursors restored to "
                    + record.CursorsBefore.VertexBytes + " vtx / "
                    + record.CursorsBefore.IndexBytes + " idx bytes (nothing was bound, so no leak).");
            }
            else
            {
                Log(log, "rollback: the mesh allocation cursors were NOT restored — the snapshot was "
                    + "below the startup watermark. Those bytes stay spent for this session.");
            }
        }
        catch (Exception ex)
        {
            Log(log, "rollback: FAILED to restore mesh allocation cursors — " + ex.Message);
        }

        return log;
    }
```

7. Reset the editor's diameter cache so the filter stops offering sizes only the purged parts provided.

## Complete example

- [parts-now.lib/Runtime/RuntimeModUnloader.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModUnloader.cs)[^unscience-runtimemodunloader], [parts-now.lib/Runtime/RuntimeModPurgeSteps.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModPurgeSteps.cs)[^unscience-runtimemodpurgesteps]
- [parts-now.lib/Runtime/RuntimeModUnloadGate.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModUnloadGate.cs)[^unscience-runtimemodunloadgate], [parts-now.lib/Runtime/LoadedModRecord.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/LoadedModRecord.cs)[^unscience-loadedmodrecord]

## Variations and gotchas

- **Leaks are real.** The shared mesh allocator has no free list: an unload or reload orphans the mesh bytes. Parts Now
  measures each mesh before disposing it and warns once leaks pass half the headroom.
- **Only your own loads.** Refuse to unload mods KSA loaded at boot; nothing records what they registered.
- **A reload must validate first** and purge only after the rules pass, so XML that no longer validates leaves the
  loaded mod untouched.
- **Texture handle 0 is the shared empty texture**; never dispose it.
- **Save integration.** unscience records runtime mod ids and part ids in a save participant and warns on load when a
  template no longer resolves; it never loads or unloads from a save.
- **Updates.** The reflection into `_collection` and the registries is checked by a self-test at startup; if it fails
  unload and rollback are disabled.

## See also

- [Overview](overview.md), [Article 1](load-and-unload-part-mods-at-runtime-1.md), [Article 3](load-and-unload-part-mods-at-runtime-3.md)
- [unscience/parts-now-runtime-unload-purge](../../../../mods/unscience/features/parts-now-runtime-unload-purge.md)
- [Change a shipped part without breaking saves](../evolve-a-shipped-part-without-breaking-saves/overview.md)

[^unscience-runtimemodpurgesteps]: unscience: parts-now.lib/Runtime/RuntimeModPurgeSteps.cs
[^unscience-loadedmodrecord]: unscience: parts-now.lib/Runtime/LoadedModRecord.cs
[^unscience-runtimemodunloader]: unscience: parts-now.lib/Runtime/RuntimeModUnloader.cs
[^ksa-partinstance]: current/decomp/KSA/PartInstance.cs (decompiled, build 2026.10.10.5554)
[^unscience-runtimemodunloadgate]: unscience: parts-now.lib/Runtime/RuntimeModUnloadGate.cs
[^ksa-serializedcollection]: current/decomp/KSA/SerializedCollection.cs (decompiled, build 2026.10.10.5554)
