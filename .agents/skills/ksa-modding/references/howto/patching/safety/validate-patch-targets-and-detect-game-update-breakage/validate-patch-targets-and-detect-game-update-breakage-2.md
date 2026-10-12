---
type: How-To
title: Non-throwing static constructor with fatal and degraded lists
description: Parts Now resolves its private ModLibrary registries in one static constructor that never throws, records each miss as a readable sentence in a fatal or degraded list and gates its Load buttons on the combined health
category: patching
group: safety
topic: validate-patch-targets-and-detect-game-update-breakage
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/parts-now-reflection-self-test]
techniques: [reflection]
hooks:
  - starmap StarMapAllModsLoaded
  - reflection KSA.ModLibrary.AllParts
  - reflection KSA.SerializedCollection._collection
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.ModLibrary, KSA.SerializedCollection, KSA.VehicleEditor]
tags: [reflection, validation, self-test, game-update, static-constructor, degraded]
---

Parts Now needs KSA internals that no public API exposes: the asset registries are `internal static readonly` fields on
`ModLibrary`, and the removal path goes through a private dictionary. A rename in a future KSA build must not crash the game or
corrupt a registry. Instead the Parts Now panel shows a red banner that names the member that moved, and the Load buttons stay
disabled. The mechanism: `GameRegistry` is the only file that calls `GetField`; its static constructor resolves every member
once, never throws, and writes each failure into a **fatal** or a **degraded** list; `SelfTest()` reports both lists.

Prerequisites: a StarMap mod. Demonstrated by unscience's Parts Now in
[One reflection layer with a startup self-test that disables loading instead of crashing](../../../../mods/unscience/features/parts-now-reflection-self-test.md).

## Background: what is reached

`ModLibrary` keeps the registries as `internal static readonly SerializedCollection<T>` fields, and
`SerializedCollection<T>` keeps its items in a private `ConcurrentDictionary<KeyHash, T> _collection`:

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

Because they are static field initialisers, the registries exist long before `LoadAll`; nothing about the self-test needs loaded
content.

## Lifecycle and timing

| When | Hook | What happens |
|---|---|---|
| Startup, after `ModLibrary.LoadAll` | `[StarMapAllModsLoaded]` (StarMap's postfix on `ModLibrary.LoadAll`) then `PartsNowSubmod.Initialize()` | First touch of `GameRegistry` runs the static constructor; `SelfTest()` logs `self-test passed` or one line per problem |
| Every UI frame | `RenderContent` (`frame:draw-ui`) | The status panel re-reads `IsHealthy`, which never changes after the constructor |
| A Load, Reload or Install click | button handler in the same UI pass | `CanStart` refuses or the job starts |

Everything after startup is game-thread only, the same thread KSA uses for the collections. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Keep reflection in one file.** The class comment states the rule: no other file calls `GetField`, `GetMethod` or
   `GetProperty`. The static constructor resolves every member and re-exposes it as a typed accessor:

```csharp
// parts-now.lib/Runtime/GameRegistry.cs L14-L29
/// <summary>
/// The one and only place in parts-now that uses reflection against KSA internals.
/// <para>
/// KSA keeps its asset registries as <c>internal static readonly SerializedCollection&lt;T&gt;</c>
/// fields on <see cref="ModLibrary"/>, and <c>SerializedCollection&lt;T&gt;</c> offers no removal API.
/// This class resolves every member it needs exactly once in its static constructor and re-exposes
/// them as typed accessors, so no other file in the mod ever calls
/// <c>GetField</c>/<c>GetMethod</c>/<c>GetProperty</c>.
/// </para>
/// <para>
/// The static constructor never throws. Failed lookups are recorded as human-readable strings and
/// reported by <see cref="SelfTest"/>; the accessor for a member that failed to resolve throws a
/// descriptive <see cref="InvalidOperationException"/> instead. A throwing static constructor would
/// turn every later access into an opaque <c>TypeInitializationException</c>.
/// </para>
/// </summary>
```

Source: [parts-now.lib/Runtime/GameRegistry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/GameRegistry.cs#L14-L29)[^unscience-gameregistry]

2. **Two problem lists with different weight.** Fatal problems make loading impossible; degraded ones only narrow a feature:

```csharp
// parts-now.lib/Runtime/GameRegistry.cs L47-L88
/// <summary>
/// Problems that make loading impossible: the six registries and the removal path. Anything in
/// here means parts-now cannot register or purge safely, so <see cref="IsHealthy" /> is false.
/// </summary>
private static readonly List<string> FatalProblems = new List<string>();

/// <summary>
/// Problems that only degrade a feature. Reported by <see cref="SelfTest" /> but deliberately
/// NOT fatal — losing <c>VehicleEditor._editorTagLookup</c> just narrows editor-tag validation to
/// the built-ins plus the registered tag definitions, which is still a usable check.
/// </summary>
private static readonly List<string> DegradedProblems = new List<string>();

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

Source: [parts-now.lib/Runtime/GameRegistry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/GameRegistry.cs#L47-L88)

3. **Each lookup gets its own `try`/`catch` and a readable sentence.** The helper checks both the presence and the type of the
   field:

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

Source: [parts-now.lib/Runtime/GameRegistry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/GameRegistry.cs#L286-L313)

4. **Accessors fail loudly and specifically.** `AllParts` is `PartsField ?? throw Missing("ModLibrary.AllParts")`, so code that
   reaches a missing member gets an `InvalidOperationException` naming it, not a `NullReferenceException` three frames later:

```csharp
// parts-now.lib/Runtime/GameRegistry.cs L90-L92, L114-L126
/// <summary>All registered <see cref="PartTemplate"/>s (includes SubParts and game-data refs).</summary>
public static SerializedCollection<PartTemplate> AllParts =>
    PartsField ?? throw Missing("ModLibrary.AllParts");
// ...
/// <summary>
/// True when everything parts-now NEEDS resolved — the six <c>ModLibrary</c> registries and the
/// removal path. Degraded-only problems (see <see cref="EditorTagLookupAvailable" />) do not
/// clear this flag: they narrow one validation rule, they do not make loading unsafe.
/// </summary>
public static bool IsHealthy => FatalProblems.Count == 0;

/// <summary>
/// True when <c>VehicleEditor._editorTagLookup</c> resolved. When false,
/// <see cref="GetKnownEditorTags"/> still works but only sees the built-in tags plus
/// <see cref="AllEditorTagDefs"/> ids, so tag validation is degraded rather than broken.
/// </summary>
public static bool EditorTagLookupAvailable => EditorTagLookupField is not null;
```

Source: [parts-now.lib/Runtime/GameRegistry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/GameRegistry.cs#L90-L126)

5. **Probe the private removal path once, up front.** A rename of `SerializedCollection<T>._collection` shows up in `SelfTest()`
   rather than at the first `Unregister` during a rollback (the probe is in the constructor above, lines 80-87).

6. **Enforce the same predicate everywhere.** `GameRegistry.IsHealthy && MeshBudget.IsUsable` appears in three places so the
   banner, the buttons and an actual refusal cannot disagree: `PartsNowSubmod.CanLoad`, `StatusPanel.Render` (which disables the
   Install and Load buttons) and `RuntimeModLoaderApi.CanStart` (which refuses with a sentence pointing at the self-test in the
   log).

## Complete example

| File | Role |
|---|---|
| [parts-now.lib/Runtime/GameRegistry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/GameRegistry.cs) | The only reflection file: fatal and degraded lists, accessors, removal path |

## Variations and gotchas

- **The self-test only proves names and types still match.** Semantic drift is invisible to it. Example: 5482 added a public
  `SerializedCollection<T>.Deregister`; Parts Now keeps the reflection path so the part browser order is preserved, and the authors
  keep a manual watchlist.
- **A throwing type initialiser is worse than a failed lookup.** It turns every later access into an opaque
  `TypeInitializationException`; that is why the constructor never throws.
- **Everything else is compile-time typed.** If a typed member is renamed, the game reports a missing member when the calling
  method is first JIT-compiled, not in the self-test; the loader's per-state `try`/`catch` contains that (inferred from the code,
  not observed).
- **`IsHealthy` is computed once.** Nothing re-runs the checks.

## See also

- [Feature-tagged handle registry with a Validate per feature](validate-patch-targets-and-detect-game-update-breakage-1.md), [Pin patched members with a never-called method that really calls them](validate-patch-targets-and-detect-game-update-breakage-3.md)
- [Validating patch targets and detecting game-update breakage](overview.md)
- [One reflection layer with a startup self-test that disables loading instead of crashing](../../../../mods/unscience/features/parts-now-reflection-self-test.md)
- [Plain GetField on internal registries and a generic holder for instance fields](../../internals/call-private-game-members-through-reflection-and-delegates/call-private-game-members-through-reflection-and-delegates-2.md) for the reflection forms used
- Related topic: load-and-unload-part-mods-at-runtime

[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-serializedcollection]: KSA.SerializedCollection (decompiled, build 2026.10.10.5554)
[^unscience-gameregistry]: unscience/parts-now.lib/Runtime/GameRegistry.cs
