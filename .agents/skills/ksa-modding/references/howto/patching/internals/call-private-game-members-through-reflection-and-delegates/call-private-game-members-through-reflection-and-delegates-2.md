---
type: How-To
title: Plain GetField on internal registries and a generic holder for instance fields
description: Reach KSA's internal static registries on ModLibrary with a plain GetField and a typed cast, and probe the private _collection dictionary of SerializedCollection<T> once per closed generic type in a static generic holder, to unregister entries the game offers no API to remove
category: patching
group: internals
topic: call-private-game-members-through-reflection-and-delegates
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/unscience/features/parts-now-reflection-self-test
  - mods/unscience/features/parts-now-runtime-unload-purge
techniques: [reflection, generic-holder]
hooks: [reflection KSA.ModLibrary.AllParts, reflection KSA.SerializedCollection._collection]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.ModLibrary, KSA.SerializedCollection, KSA.VehicleEditor]
tags: [reflection, getfield, internal-static, generic, serializedcollection, unregister]
---

KSA keeps its asset registries as `internal static readonly SerializedCollection<T>` fields on `ModLibrary`. In build 2026.10.10.5554 `SerializedCollection<T>` has a public `Deregister`, but it takes the
collection's lock and removes by moving the last item into the freed slot, which reorders the list; Parts Now wants the part browser order preserved, so it removes from the list and the private dictionary
itself. It reaches the registries with a plain `typeof(ModLibrary).GetField(name, ...)` and a typed cast, and reaches the private
`_collection` dictionary of each closed generic through a small static generic holder. It needs no Harmony patch. The self-test and fatal/degraded design around this is in
[Non-throwing static constructor with fatal and degraded lists](../../safety/validate-patch-targets-and-detect-game-update-breakage/validate-patch-targets-and-detect-game-update-breakage-2.md); this article is about the reflection forms.

Prerequisites: a StarMap mod (no Harmony is needed for this technique). Demonstrated by unscience's Parts Now in [One reflection layer with a startup self-test that disables loading instead of crashing](../../../../mods/unscience/features/parts-now-reflection-self-test.md) and
[Unload runtime-registered parts behind a fail-closed safety gate](../../../../mods/unscience/features/parts-now-runtime-unload-purge.md).

## Background: how the game does it

Registries are `internal static readonly` fields on `ModLibrary`, so they exist from type initialisation, long before `LoadAll`. `SerializedCollection<T>` keeps its items in a private
`ConcurrentDictionary<KeyHash, T> _collection` behind `Find`:

```csharp
// current/decomp/KSA/ModLibrary.cs L68, L70, L80, L86
internal static readonly SerializedCollection<FileReference> AllFiles = new SerializedCollection<FileReference>("files");
// ...
internal static readonly SerializedCollection<PbrMaterialReference> AllMaterials = new SerializedCollection<PbrMaterialReference>("materials");
// ...
internal static readonly SerializedCollection<MeshReference> AllMeshes = new SerializedCollection<MeshReference>("meshes");
// ...
internal static readonly SerializedCollection<PartTemplate> AllParts = new SerializedCollection<PartTemplate>("Part templates");
```

Source: [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L68-L86)[^ksa-modlibrary]

```csharp
// current/decomp/KSA/SerializedCollection.cs L14
private readonly ConcurrentDictionary<KeyHash, T> _collection = new ConcurrentDictionary<KeyHash, T>();
```

Source: [current/decomp/KSA/SerializedCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedCollection.cs#L14)[^ksa-serializedcollection]

```csharp
// current/decomp/KSA/SerializedCollection.cs L37-L55
public bool Deregister(T item)
{
	using (_lock.EnterScope())
	{
		if (!_collection.TryRemove(item.Hash, out var _))
		{
			return false;
		}
		int num = _all.LastIndexOf(item);
		if (num >= 0)
		{
			List<T> all = _all;
			List<T> all2 = _all;
			all[num] = all2[all2.Count - 1];
			_all.RemoveAt(_all.Count - 1);
		}
		return true;
	}
}
```

Source: [current/decomp/KSA/SerializedCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedCollection.cs#L37-L55)

## Lifecycle and timing

The class initialiser runs at the first touch, which Parts Now makes in `[StarMapAllModsLoaded]` (`startup:all-mods-loaded`); every access afterwards is on the game thread (UI in `frame:draw-ui`, purge
from the `[StarMapBeforeGui]` hook). `SerializedCollection` has a private `Lock` that `Unregister` deliberately does not take, so removal must happen on the game thread with no load in flight. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Reach an internal static field with `GetField` and check its runtime type.** Include `NonPublic` and `Static`; test `info.GetValue(null) is SerializedCollection<T>` so a retyped field is reported
   instead of cast blindly:

```csharp
// parts-now.lib/Runtime/GameRegistry.cs L286-L313
private static SerializedCollection<T>? Collection<T>(string field)
    where T : ILibraryData, IListable
{
    try
    {
        FieldInfo? info = typeof(ModLibrary).GetField(field, StaticFieldFlags);
        if (info is null)
        {
            FatalProblems.Add("ModLibrary." + field + " not found — KSA internals changed.");
            return null;
        }

        if (info.GetValue(null) is not SerializedCollection<T> collection)
        {
            FatalProblems.Add(
                "ModLibrary." + field + " is not a SerializedCollection<" + typeof(T).Name
                + "> — KSA internals changed.");
            return null;
        }

        return collection;
    }
    catch (Exception ex)
    {
        FatalProblems.Add("ModLibrary." + field + " could not be read — " + ex.Message);
        return null;
    }
}
```

Source: [parts-now.lib/Runtime/GameRegistry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/GameRegistry.cs#L286-L313)[^unscience-gameregistry]

2. **Resolve everything in the static constructor.** One lookup per registry, in its own `try`/`catch`, appending a readable sentence on failure:

```csharp
// parts-now.lib/Runtime/GameRegistry.cs L60-L88
private static readonly SerializedCollection<PartTemplate>? PartsField;
private static readonly SerializedCollection<MeshReference>? MeshesField;
private static readonly SerializedCollection<FileReference>? FilesField;
private static readonly SerializedCollection<PbrMaterialReference>? MaterialsField;
private static readonly SerializedCollection<PartGameDataReference>? PartGameDataField;
private static readonly SerializedCollection<EditorTagDefinition>? EditorTagDefsField;

private static readonly FieldInfo? EditorTagLookupField;

static GameRegistry()
{
    PartsField = Collection<PartTemplate>("AllParts");
    MeshesField = Collection<MeshReference>("AllMeshes");
    FilesField = Collection<FileReference>("AllFiles");
    MaterialsField = Collection<PbrMaterialReference>("AllMaterials");
    PartGameDataField = Collection<PartGameDataReference>("AllPartGameDataReferences");
    EditorTagDefsField = Collection<EditorTagDefinition>("AllEditorTagDefinitions");

    EditorTagLookupField = ResolveEditorTagLookup();

    // Probe the removal path once, on an arbitrary closed generic, so a rename shows up in
    // SelfTest() rather than at the first Unregister() call during a rollback.
    if (CollectionFields<PartTemplate>.Collection is null)
    {
        FatalProblems.Add(
            "SerializedCollection<T>._collection not found — KSA internals changed "
            + "(unload and rollback are unavailable).");
    }
}
```

Source: [parts-now.lib/Runtime/GameRegistry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/GameRegistry.cs#L60-L88)

3. **Use a static generic holder for an instance field of a generic type.** `CollectionFields<T>.Collection` is a `static readonly FieldInfo?` initialised once per closed generic `T` by the CLR, so
   there is no dictionary, no lock and no per-call lookup:

```csharp
// parts-now.lib/Runtime/GameRegistry.cs L353-L357
private static class CollectionFields<T> where T : ILibraryData, IListable
{
    internal static readonly FieldInfo? Collection =
        typeof(SerializedCollection<T>).GetField("_collection", CollectionFieldFlags);
}
```

Source: [parts-now.lib/Runtime/GameRegistry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/GameRegistry.cs#L353-L357)

4. **Perform the paired removal.** The item must leave both the live list (`GetList()` returns the live backing `List<T>`) and the private dictionary behind `Find`, or `Find` still resolves a purged item
   or the browser still lists it:

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

Source: [parts-now.lib/Runtime/GameRegistry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/GameRegistry.cs#L144-L165)

5. **Reflect a private static field of another type the same way.** `VehicleEditor._editorTagLookup` is read with `GetField(..., Static | NonPublic | Public)` and a type check as `Dictionary<uint, string>`;
   a miss is a *degraded* problem, not a fatal one ([parts-now.lib/Runtime/GameRegistry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/GameRegistry.cs#L315-L345)).

## Complete example

| File | Role |
|---|---|
| [parts-now.lib/Runtime/GameRegistry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/GameRegistry.cs) | The only reflection file: registries, holder, unregister, self-test |
| [parts-now.lib/Runtime/RuntimeModPurgeSteps.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModPurgeSteps.cs)[^unscience-runtimemodpurgesteps] | Purge steps that call `Unregister` |

## Variations and gotchas

- **Prefer the public API when its behaviour is acceptable.** `Deregister` is the supported route; Parts Now reflects only because of ordering and lock behaviour.
- **Use `GetField` (reflection) when the call is rare.** Registry reads here are per load or per validation, not per frame; for a hot path bind a `FieldRef` ([AccessTools static field refs bound once, replay stock's writes](call-private-game-members-through-reflection-and-delegates-1.md), [Typed FieldRefAccess, boxed private enum members and by-ref lookups](call-private-game-members-through-reflection-and-delegates-3.md)).
- **A cast is not a check.** `GetValue(null) as SerializedCollection<T>` hides a retyped field as `null`; this code distinguishes "not found" from "found but not what we expected".
- **Identity, not id, for models.** In the purge, `PartModel` caches are pruned by object identity of the template because `TemplateDataBase.Id` is optional and not unique.
- **Mind the GPU.** Removing from a registry does not free what the item holds; the purge disposes items in an order that never frees an object something can still draw.
- **`readonly` static fields hold the same instance for the whole session,** so caching the `SerializedCollection<T>` reference once is safe.

## See also

- [AccessTools static field refs bound once, replay stock's writes](call-private-game-members-through-reflection-and-delegates-1.md), [Typed FieldRefAccess, boxed private enum members and by-ref lookups](call-private-game-members-through-reflection-and-delegates-3.md), [AccessTools.MethodDelegate with a delegate type that mirrors in and ref modifiers](call-private-game-members-through-reflection-and-delegates-4.md), [Delegates over private editor helpers reused in a replacement drag](call-private-game-members-through-reflection-and-delegates-5.md), [UnsafeAccessor StaticField on the k__BackingField with UnsafeAccessorType](call-private-game-members-through-reflection-and-delegates-6.md), [Copied injector in a consumer mod with the same accessor](call-private-game-members-through-reflection-and-delegates-7.md)
- [Reading, writing and calling private game members](overview.md)
- [One reflection layer with a startup self-test that disables loading instead of crashing](../../../../mods/unscience/features/parts-now-reflection-self-test.md), [Unload runtime-registered parts behind a fail-closed safety gate](../../../../mods/unscience/features/parts-now-runtime-unload-purge.md)
- [Non-throwing static constructor with fatal and degraded lists](../../safety/validate-patch-targets-and-detect-game-update-breakage/validate-patch-targets-and-detect-game-update-breakage-2.md)
- Related topic: load-and-unload-part-mods-at-runtime

[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-serializedcollection]: KSA.SerializedCollection (decompiled, build 2026.10.10.5554)
[^unscience-gameregistry]: unscience/parts-now.lib/Runtime/GameRegistry.cs
[^unscience-runtimemodpurgesteps]: unscience/parts-now.lib/Runtime/RuntimeModPurgeSteps.cs
