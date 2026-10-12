---
type: Mod Feature
title: Unload runtime-registered parts behind a fail-closed safety gate
description: Parts Now refuses to unload while anything live could reference a mod's parts, then purges in a fixed order (device idle, templates, caches, textures, meshes, registries) and defers the purge to the pre-GUI hook so freed ImGui textures are never drawn
mod: unscience
submod: parts-now
category: assets
techniques: [reflection, starmap-lifecycle, vulkan-texture]
hooks:
  - starmap StarMapBeforeGui (via unscience ISubmod.Update, deferred unload and reload purge)
  - reflection KSA.SerializedCollection._collection (private hash dictionary behind Find)
  - reflection KSA.ModLibrary.AllParts, AllPartGameDataReferences, AllMaterials, AllFiles, AllMeshes
lifecycle: [frame:draw-ui]
game_types:
  - KSA.ModLibrary
  - KSA.SerializedCollection
  - KSA.PartTemplate
  - KSA.PartModel
  - KSA.PartModelGlass
  - KSA.PartModelDynamic
  - KSA.PartModelModule
  - KSA.PartModelGlassModule
  - KSA.TextureReference
  - KSA.MeshReference
  - KSA.VehicleEditor
  - KSA.Vehicle
  - KSA.Part
  - KSA.Rendering.Thumbnails.ThumbnailReference
  - KSA.Rendering.Thumbnails.ThumbnailDynamic
tags: [unload, hot-reload, modlibrary, imgui-texture, use-after-free, registry, safety]
---

KSA never unloads content: its registries (`ModLibrary.AllParts`, `AllMeshes`, ...) only grow, and
several static caches (`PartModel.Instances`, ray-tracer lists) are never pruned. Parts Now still
offers Unload and Reload for mods it loaded itself. It does this by refusing whenever anything live
might still point at the mod, then removing exactly what the load registered, in an order that
never frees a GPU object something can still draw.[^runtime-unloader]

Mechanism in one line: a fail-closed gate walks live vehicles and the editor for the mod's part ids.
If it passes, a numbered purge (device idle → templates → game data → model caches → textures →
meshes → files → materials → loader/binder queues → editor cache) runs from the pre-GUI hook, each
step isolated in its own `try/catch`.

## How it works

**Ownership.** Only mods recorded in `RuntimeModRegistry` (loaded by Parts Now this session) can be
unloaded or reloaded. A mod KSA loaded at boot is refused outright.[^loader-api] The
`LoadedModRecord` ([parts-now.lib/Runtime/LoadedModRecord.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/LoadedModRecord.cs))
lists every object the load registered, plus the model-template objects and a `Purged` flag.[^loaded-mod-record]

**The gate** ([parts-now.lib/Runtime/RuntimeModUnloadGate.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModUnloadGate.cs)) returns a sentence or null:

| Check | Source of truth | Why |
|---|---|---|
| No Parts Now job in flight | `RuntimeModLoader.IsBusy` | `GameRegistry.Unregister` does not take the collection lock, so it needs exclusive access |
| No live vehicle uses one of the part ids | `VehicleProvider.GetAllVehicles(includeDebris: true)` and every part, recursing `SubParts` | A purged template under a live `Part` crashes the editor or browser later. Debris counts |
| The editor does not hold one | `Program.Editor.EditingSpace.AllParts` and each tree in `UnattachedPartTrees`, recursing `SubParts` | Same reason; an open but unrelated editor is fine |
| Any exception while checking | `catch` around everything | Treated as a refusal, never a pass |
| Reload only: the folder still exists | `Directory.Exists(record.ModDir)` | Nothing to load back |

A record with no part ids skips the vehicle and editor walks. The UI caches the gate result and
recomputes it only on selection change or rescan, because the vehicle walk is not free.[^unload-gate][^folder-panel]

**The purge** ([parts-now.lib/Runtime/RuntimeModUnloader.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModUnloader.cs#L60-L154)), with the bodies in [parts-now.lib/Runtime/RuntimeModPurgeSteps.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModPurgeSteps.cs):

| # | Step | Detail |
|---|---|---|
| 0 | Clear the hover preview | `editor.DynamicThumbnail.SetSelectedPart(null)`, so the browser's spinning preview stops referencing the template |
| 1 | `renderer.Device.WaitIdle()` | Nothing submitted may still use what is freed below |
| 2 | Dispose and unregister part templates | `PartTemplate.Dispose()` disposes the thumbnail (ImGui texture, image view, image). An XML-only `<Thumbnail>` without an image is detached first to avoid an NRE |
| 3 | Unregister game data | `AllPartGameDataReferences` |
| 4 | Prune model caches | `PartModel`/`PartModelGlass` `.Instances` and `.InstancesRayTrace`, `PartModelDynamic.Instances`, `PartModelModule.Template.RayTracers`, `PartModelGlassModule.Template.RayTracers`, matched by **object identity** of the template |
| 5 | Dispose and unregister textures | Only when `BindlessHandle > 0` and the texture objects exist; handle 0 is the shared empty texture |
| 6 | Measure, dispose and unregister meshes | Sizes recorded as leaked bytes first (see [mesh headroom](./parts-now-mesh-headroom-reservation.md)) |
| 7 | Unregister other files | Mesh atlases, mesh files |
| 8 | Unregister materials | `AllMaterials` |
| 9 | Remove loaders and binders | `ModLibrary.Loaders.RemoveAll`, `ModLibrary.Binders.RemoveAll` |
| 10 | `VehicleEditor.ResetPartDiameterCache()` | Diameter filter no longer offers purged sizes |
| 11 | Forget the record | `RuntimeModRegistry.Remove` |

Why identity in step 4: `PartModel.Get` reuses an existing `PartModel` whose `Template.Id` matches.
`TemplateDataBase.Id` is optional and not unique, so matching by id would miss id-less templates
(leaving a `PartModel` that the reloaded part would reuse, pointing at the purged mesh offsets) and
could evict another mod's models.[^purge-steps][^ksa-part-model]

`Purged` is set before step 0. The item-level disposals are not idempotent:
`ThumbnailReference.Dispose` and `TextureReference.Dispose(Device)` would double-free.[^ksa-thumbnail-reference][^ksa-texture-reference]

**Removing from a `SerializedCollection`.** `GameRegistry.Unregister<T>` removes the item from the
live `GetList()` list (preserving order) and, through the reflected private
`ConcurrentDictionary<KeyHash, T> _collection`, from the hash index behind `Find`. Removing from only
one leaves `Find` resolving a purged item, or the browser listing it.[^game-registry][^ksa-serialized-collection]

**Rollback** is the same purge on a partially filled record, with leak accounting off and the mesh
cursors rewound afterwards. It is only used before the first bind (see
[runtime loading](./parts-now-runtime-part-loading.md)).

## Lifecycle and timing

Every purge runs inside `PartsNowSubmod.Update(dt)`, the `[StarMapBeforeGui]` prefix on
`Program.OnDrawUiFrame` (`Program.cs` L2267, def L3131):[^parts-now-submod][^ksa-program]

- A reload purges at the end of its `Validate` state, which runs from `Update`.
- A failed load unwinds from inside `Step()`, also in `Update`.
- An Unload confirmed in the modal is **not** run there. The modal is drawn from `RenderContent`,
  i.e. `[StarMapAfterGui]` (postfix on `OnDrawUiViewports`). By then `OnDrawUiFrame` has already run
  `ImGuiWindow.DrawAllStaticWindows` (L3140), which drew the editor part browser with `ImageButton`s
  bound to the mod's thumbnail descriptor sets. `ImGui.Render` (L2286) has not run yet, so freeing
  them would be a use-after-free in this frame's draw data, and `WaitIdle` does not help because the
  frame is not submitted. The confirm handler stores the mod id. The next `Update`, before anything
  draws, calls `ProcessPendingActions()`, which runs the unload.[^folder-panel-actions]

`Device.WaitIdle()` in step 1 drains GPU work from earlier frames, including the 5554 hover preview,
which now submits asynchronously and retires its fence on a later frame.[^ksa-thumbnail-dynamic]
Vehicle state is only read (by the gate), never written, so the solver race after
`ExecuteNext*Solvers` does not matter here.

## Key code

| Path | Role |
|---|---|
| [parts-now.lib/Runtime/RuntimeModUnloadGate.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModUnloadGate.cs#L34-L137) | Fail-closed gate: job, vehicles, editor |
| [parts-now.lib/Runtime/RuntimeModUnloader.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModUnloader.cs) | Purge driver, `Rollback`, per-step isolation |
| [parts-now.lib/Runtime/RuntimeModPurgeSteps.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModPurgeSteps.cs) | Step bodies: templates, game data, model caches, textures, meshes, files, materials, queues |
| [parts-now.lib/Runtime/GameRegistry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/GameRegistry.cs#L144-L165) | `Unregister<T>` over list and private dictionary |
| [parts-now.lib/Ui/ModFolderPanelActions.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Ui/ModFolderPanelActions.cs#L186-L237) | Confirm modal defers the unload; `ProcessPendingActions` runs it |
| [ksa-abstractions.lib/VehicleProvider.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/VehicleProvider.cs) | Vehicle enumeration (with debris) used by the gate[^vehicle-provider] |

## Snippets

The gate fails closed:

```csharp
// parts-now.lib/Runtime/RuntimeModUnloadGate.cs L41-L62
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
```

The ordered purge, each step isolated:

```csharp
// parts-now.lib/Runtime/RuntimeModUnloader.cs L119-L151
        Step(log, 1, "wait for the device to go idle", () =>
        {
            // Nothing in an in-flight frame may still reference the images and buffers freed below.
            renderer = Program.GetRenderer();
            renderer.Device.WaitIdle();
            return "device idle";
        });

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
// ...
        Step(log, 11, "forget the mod record", () =>
            RuntimeModRegistry.Remove(record.ModId) ? "record removed" : "record was not registered");
```

Removing from both halves of a `SerializedCollection`:

```csharp
// parts-now.lib/Runtime/GameRegistry.cs L144-L165
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

Deferring a UI-confirmed unload to the next pre-GUI hook:

```csharp
// parts-now.lib/Ui/ModFolderPanelActions.cs L186-L205
    private void Confirm()
    {
        if (_confirmIsUnload)
        {
            // Deferred to the next Update(dt), NEVER run here. Purging disposes ThumbnailReferences,
            // which calls ImGuiBackend.Vulkan.RemoveTexture (vkFreeDescriptorSets) and destroys the
            // image view immediately. This method runs from [StarMapAfterGui], a postfix on
            // Program.OnDrawUiViewports — but the vehicle editor's part browser is an IStaticWindow
            // drawn by ImGuiWindow.DrawAllStaticWindows inside OnDrawUiFrame (Program.cs:2898),
            // EARLIER in this same frame. Its ImageButton draw commands already hold those descriptor
            // sets, and ImGui.Render() has not run yet, so freeing them here is a use-after-free.
            // Device.WaitIdle() in the purge does not help: it drains submitted frames, and this one
            // has not been submitted.
            //
            // Update(dt) runs from [StarMapBeforeGui], a prefix on OnDrawUiFrame — before anything
            // has drawn this frame. That is where every other purge in parts-now already runs.
            _pendingUnloadModId = _confirmModId;
            SetMessage($"Unloading '{_confirmModId}'...", isError: false);
            return;
        }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `SerializedCollection<T>._collection`, `GetList`, `Find`, `Deregister` | [current/decomp/KSA/SerializedCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedCollection.cs#L12-L65) | Private dictionary removed via reflection; live list removed directly; `Deregister` exists but is not used |
| `PartModel.Instances` / `InstancesRayTrace` / `Get` | [current/decomp/KSA/PartModel.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModel.cs#L409-L443) | Static caches that KSA appends to and never prunes |
| `TextureReference.Dispose(Device)` | [current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs#L77-L82) | Frees the bindless slot, then the texture, with no null checks |
| `ThumbnailReference.Dispose` / `DestroyImGuiThumbnail` | [current/decomp/KSA.Rendering.Thumbnails/ThumbnailReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Thumbnails/ThumbnailReference.cs#L45-L59) | `ImGuiBackend.Vulkan.RemoveTexture`, then image view and image |
| `ThumbnailDynamic.SetSelectedPart` | [current/decomp/KSA.Rendering.Thumbnails/ThumbnailDynamic.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Thumbnails/ThumbnailDynamic.cs#L89-L99) | Drops the hover-preview request (step 0) |
| `VehicleEditor.EditingSpace`, `UnattachedPartTrees`, `DynamicThumbnail`, `ResetPartDiameterCache` | [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L545-L707) | Editor walk for the gate, preview reset, cache reset (L7215) |
| `Program.OnDrawUiFrame` / `DrawAllStaticWindows` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3131-L3141) | Why the purge must run before the part browser draws |

## Pitfalls and version notes

- 5554 has `SerializedCollection<T>.Deregister(T)`. It takes the lock, removes by hash with no
  identity check and swap-removes from the list, which reorders the part browser. Parts Now keeps
  its reflection path; adopting `Deregister` would need an identity guard first.[^scope-part-editor]
- `PartArchetypes.FindOrBuild` caches one `Part` built from the last inspected template. The gate
  does not see that cache, so it can keep a purged template alive until the next inspection
  (noted by the authors; the cache is unchanged in 5554).[^ksa-part-archetypes]
- The `Confirm` comment cites `Program.cs:2898` from an older build. In 5554 the static windows are
  drawn at L3140.
- A failed purge step is logged and the rest continue. A half-failed purge is reported, not
  retried: `Purged` stays true so the non-idempotent disposals never run twice.
- Unloading leaks the mod's slice of the shared mesh buffer until restart.

## See also

- [Runtime part loading state machine](./parts-now-runtime-part-loading.md) (reload, rollback)
- [Mesh headroom reservation](./parts-now-mesh-headroom-reservation.md) (leak counter)
- [Reflection self-test](./parts-now-reflection-self-test.md) (how `_collection` is resolved)
- [Thumbnail generation](./parts-now-thumbnail-generation.md)
- [Parts Now sub-mod](../submods/parts-now.md)
- [Mod overview](../overview.md)
- [Plain GetField on internal registries and a generic holder for instance fields](../../../howto/patching/internals/call-private-game-members-through-reflection-and-delegates/call-private-game-members-through-reflection-and-delegates-2.md) - how-to article demonstrating this feature
- [Unload runtime-registered parts with a fail-closed gate and an ordered purge](../../../howto/vehicles-and-parts/parts/load-and-unload-part-mods-at-runtime/load-and-unload-part-mods-at-runtime-2.md) - how-to article demonstrating this feature

[^runtime-unloader]: parts-now.lib/Runtime/RuntimeModUnloader.cs
[^loader-api]: parts-now.lib/Runtime/RuntimeModLoaderApi.cs
[^loaded-mod-record]: parts-now.lib/Runtime/LoadedModRecord.cs
[^unload-gate]: parts-now.lib/Runtime/RuntimeModUnloadGate.cs
[^folder-panel]: parts-now.lib/Ui/ModFolderPanel.cs
[^purge-steps]: parts-now.lib/Runtime/RuntimeModPurgeSteps.cs
[^game-registry]: parts-now.lib/Runtime/GameRegistry.cs
[^folder-panel-actions]: parts-now.lib/Ui/ModFolderPanelActions.cs
[^parts-now-submod]: parts-now.lib/PartsNowSubmod.cs
[^vehicle-provider]: ksa-abstractions.lib/VehicleProvider.cs
[^scope-part-editor]: scope/part-editor-and-robotics.md (authors' integration map)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-serialized-collection]: KSA.SerializedCollection (decompiled, build 2026.10.10.5554)
[^ksa-part-model]: KSA.PartModel (decompiled, build 2026.10.10.5554)
[^ksa-texture-reference]: KSA.TextureReference (decompiled, build 2026.10.10.5554)
[^ksa-thumbnail-reference]: KSA.Rendering.Thumbnails.ThumbnailReference (decompiled, build 2026.10.10.5554)
[^ksa-thumbnail-dynamic]: KSA.Rendering.Thumbnails.ThumbnailDynamic (decompiled, build 2026.10.10.5554)
[^ksa-part-archetypes]: KSA.PartArchetypes (decompiled, build 2026.10.10.5554)
