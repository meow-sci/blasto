---
type: Mod Feature
title: One reflection layer with a startup self-test that disables loading instead of crashing
description: Parts Now resolves every KSA internal it needs (six ModLibrary registries, SerializedCollection._collection, VehicleEditor._editorTagLookup) once in a non-throwing static constructor, sorts failures into fatal and degraded, and gates loading on the result
mod: unscience
submod: parts-now
category: patching
techniques: [reflection, starmap-lifecycle, imgui-window]
hooks:
  - starmap StarMapAllModsLoaded (PartsNowSubmod.Initialize runs GameRegistry.SelfTest)
  - starmap StarMapAfterGui (StatusPanel recomputes LoadingEnabled every UI frame)
  - reflection KSA.ModLibrary.AllParts, AllMeshes, AllFiles, AllMaterials, AllPartGameDataReferences, AllEditorTagDefinitions
  - reflection KSA.SerializedCollection`1._collection
  - reflection KSA.VehicleEditor._editorTagLookup
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types:
  - KSA.ModLibrary
  - KSA.SerializedCollection
  - KSA.VehicleEditor
  - KSA.PartTemplate
  - KSA.MeshReference
  - KSA.FileReference
  - KSA.PbrMaterialReference
  - KSA.PartGameDataReference
  - KSA.EditorTagDefinition
  - KSA.KeyHash
tags: [reflection, self-test, modlibrary, serialized-collection, editor-tags, game-update-safety, tripwire]
---

Parts Now has to reach into KSA internals that no public API exposes: the asset registries are
`internal static readonly` fields on `ModLibrary`, and the removal path goes through a private
dictionary. A rename in a future KSA build should not crash the game or corrupt a registry. Instead,
the Parts Now panel shows a red banner that names the member that moved, and the Load buttons stay
disabled.[^game-registry][^parts-now-readme]

Mechanism in one line: `GameRegistry` is the only file in the sub-mod that calls `GetField`. Its
static constructor resolves every member once, never throws, and writes each failure into a
**fatal** or a **degraded** list. `SelfTest()` runs from `[StarMapAllModsLoaded]` and reports both
lists. Loading is allowed only while `GameRegistry.IsHealthy && MeshBudget.IsUsable`.

## How it works

**What is resolved, and how bad a miss is** ([parts-now.lib/Runtime/GameRegistry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/GameRegistry.cs#L60-L88)):

| Member (5554) | How | On a miss | Used for |
|---|---|---|---|
| `ModLibrary.AllParts`, `AllMeshes`, `AllFiles`, `AllMaterials`, `AllPartGameDataReferences`, `AllEditorTagDefinitions` | `typeof(ModLibrary).GetField(name, Static\|NonPublic\|Public)`, then `GetValue(null) is SerializedCollection<T>` | Fatal | Registry marks and deltas, incremental game-data attach, purge, every "already registered" validation rule |
| `SerializedCollection<T>._collection` | `GetField("_collection", Instance\|NonPublic)` in a per-`T` generic holder, probed once on `PartTemplate` | Fatal ("unload and rollback are unavailable") | `Unregister<T>` removes from the list and from the hash dictionary behind `Find` (see [the purge](./parts-now-runtime-unload-purge.md)) |
| `VehicleEditor._editorTagLookup` | `GetField(..., Static\|NonPublic\|Public)`, type-checked as `Dictionary<uint, string>` | Degraded | V7 editor-tag validation; without it the known set narrows to six built-in tags plus `AllEditorTagDefinitions` ids |

All six registry fields, the private dictionary and the editor-tag lookup exist with these names and
types in build 5554.[^ksa-modlibrary][^ksa-serialized-collection][^ksa-vehicle-editor]

**Design rules the class follows:**

- **The static constructor never throws.** A throwing type initializer turns every later access
  into an opaque `TypeInitializationException`. Each lookup runs in its own `try/catch` and appends a
  readable sentence (`"ModLibrary.AllParts not found — KSA internals changed."`) instead.
- **Accessors fail loudly and specifically.** `GameRegistry.AllParts` is `PartsField ?? throw
  Missing("ModLibrary.AllParts")`, so code that reaches a missing member gets an
  `InvalidOperationException` naming it, not a `NullReferenceException` three frames later.
- **Typed re-exposure.** Callers get `SerializedCollection<PartTemplate>` and friends plus
  `FindPart(id)`, `FindMesh(id)`, `FindFile(id)`, `FindMaterial(id)`, `FindPartGameData(id)`, all built
  on `KeyHash.Make(id.AsSpan())`. `KeyHash.Make` lowercases the input before hashing (CRC32), which is
  why every Parts Now id index uses `OrdinalIgnoreCase`.[^ksa-keyhash]
- **Known editor tags** are the union of `_editorTagLookup.Values`, every `EditorTagDefinition.Id` and
  the six tags KSA hardcodes (`All`, `Hidden`, `Engines`, `Capsules`, `Interstage`, `Radial`), rebuilt
  on each call and compared case-insensitively.

**Where the result is enforced.** The same predicate, `GameRegistry.IsHealthy && MeshBudget.IsUsable`
(the second half is the [mesh headroom](./parts-now-mesh-headroom-reservation.md) check), appears in
three places so the banner, the buttons and an actual refusal cannot disagree:[^parts-now-submod][^status-panel][^loader-api]

| Place | Effect |
|---|---|
| `PartsNowSubmod.CanLoad` | Public property for other code; not read inside the sub-mod |
| `StatusPanel.Render` → `LoadingEnabled` | Recomputed at the top of every render; passed as `canLoad` to the paste and folder panels, which disable Install & Load, Load and Reload. Draws the red "Loading is DISABLED" banner with every problem, or an amber "reduced validation" notice when only degraded problems exist |
| `RuntimeModLoaderApi.CanStart` | Shared precondition of `StartInstall`, `StartLoad`, `StartReload`: refuses with a sentence pointing at the self-test in the log |

The paste panel's **Validate** button is not gated: it only needs XML and an idle loader.[^paste-panel]
If a fatal member were missing, rules that touch `GameRegistry` would throw the descriptive
`InvalidOperationException`, and `BundleValidator.RunRule` would turn that into a warning naming the
rule, so the user still sees the other rules' findings.[^bundle-validator] Unload is not gated on
`IsHealthy` either, but nothing can have been loaded in a session where the check failed.

## Lifecycle and timing

| When | Hook | What happens |
|---|---|---|
| Startup, after `ModLibrary.LoadAll` (`Program.cs` L942), before `ModLibrary.Bind` (L978) | `[StarMapAllModsLoaded]` (StarMap's postfix on `ModLibrary.LoadAll`) → `PartsNowSubmod.Initialize()` | First touch of `GameRegistry` runs its static constructor; `SelfTest()` logs `self-test passed` or one line per problem; the list is copied into `SelfTestProblems` |
| Every UI frame | `RenderContent`, i.e. `[StarMapAfterGui]` in unscience (postfix on `Program.OnDrawUiViewports`) | `StatusPanel` re-reads `IsHealthy`, which never changes after the static constructor |
| On a Load, Reload or Install click | Button handler in the same UI pass | `CanStart` refuses or the job starts |
| Validation | Loader `Validate` state from `ISubmod.Update` (`[StarMapBeforeGui]`), or the Validate button | `_editorTagLookup.GetValue(null)` is read here, not at startup |

In the supermod, `unscience/Mod.cs` calls `Initialize()` on every sub-mod in list order from its single
`[StarMapAllModsLoaded]` method ([unscience/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Mod.cs#L108-L113)). The standalone host calls it from its own
`[StarMapAllModsLoaded]` ([parts-now/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now/Mod.cs#L40-L54)).[^unscience-mod][^parts-now-mod][^ksa-program]

Why that point: the `ModLibrary` registries are static field initializers, so they exist long before
`LoadAll`; nothing about the self-test needs loaded content. Running it in `Initialize` puts it next
to `MeshBudget.Reserve()`, which must run at exactly this hook, so the whole "can Parts Now work in
this build?" answer is known before the first frame. Everything after that is game-thread only, the
same thread KSA uses for the collections, which is also why `Unregister` may skip the collection's
private `Lock`.

## Key code

| Path | Role |
|---|---|
| [parts-now.lib/Runtime/GameRegistry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/GameRegistry.cs) | The only reflection in Parts Now: static constructor, typed accessors, `Unregister`, known editor tags, `SelfTest` |
| [parts-now.lib/PartsNowSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/PartsNowSubmod.cs#L36-L57) | `SelfTestProblems`, `CanLoad`, `Initialize` (self-test, then mesh reservation) |
| [parts-now.lib/Ui/StatusPanel.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Ui/StatusPanel.cs#L29-L110) | `LoadingEnabled`, red banner, amber degraded notice |
| [parts-now.lib/Runtime/RuntimeModLoaderApi.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModLoaderApi.cs#L233-L258) | `CanStart`, the refusal shared by all job entry points |
| [parts-now.lib/Runtime/BundleValidator.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/BundleValidator.cs#L127-L145) | `RunRule` turns a throwing rule into a warning |
| [scope/part-editor-and-robotics.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/part-editor-and-robotics.md) | Authors' per-member watchlist (rows 1-9) with line numbers per build[^scope-part-editor] |

## Snippets

The non-throwing static constructor:

```csharp
// parts-now.lib/Runtime/GameRegistry.cs L69-L88
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

One registry lookup: name, then type, each miss a sentence:

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

The gate on the job API:

```csharp
// parts-now.lib/Runtime/RuntimeModLoaderApi.cs L233-L258
    private static bool CanStart(out string? refusal)
    {
        if (IsBusy)
        {
            refusal = "a parts-now job is already running (" + StatusText + ") — wait for it to finish.";
            return false;
        }

        if (!GameRegistry.IsHealthy)
        {
            refusal = "parts-now cannot reach KSA's asset registries (see the self-test in the log) — "
                + "loading is disabled until that is fixed.";
            return false;
        }

        if (!MeshBudget.IsUsable)
        {
            refusal = "no mesh headroom was reserved at startup"
                + (MeshBudget.FailureReason is null ? string.Empty : " (" + MeshBudget.FailureReason + ")")
                + " — runtime mesh loading would corrupt the shared vertex buffer.";
            return false;
        }

        refusal = null;
        return true;
    }
```

The targets in the game (build 5554):

```csharp
// current/decomp/KSA/SerializedCollection.cs L10-L16
public class SerializedCollection<T>(string name) where T : ILibraryData, IListable
{
	private Lock _lock = new Lock();

	private readonly ConcurrentDictionary<KeyHash, T> _collection = new ConcurrentDictionary<KeyHash, T>();

	private List<T> _all = new List<T>();
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `ModLibrary.AllFiles`, `AllMaterials`, `AllPartGameDataReferences`, `AllMeshes`, `AllParts`, `AllEditorTagDefinitions` (`internal static readonly`) | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L66-L146) | Resolved by name; fatal on a miss |
| `SerializedCollection<T>._collection`, `_lock`, `GetList`, `Find`, `Deregister` | [current/decomp/KSA/SerializedCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedCollection.cs#L10-L65) | Private dictionary behind `Find`; `GetList` returns the live list; `Deregister` exists but is not used |
| `VehicleEditor._editorTagLookup` (`private static Dictionary<uint, string>`), `_editorTags` | [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L537-L539) | Known-tag set for V7 (degraded on a miss) |
| `VehicleEditor.RegisterTag`, `MarkEditorTagDefinitionsLoaded` | [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L7175-L7200) | Why a tag added after boot gets no category button |
| `ModLibrary.LoadEditorTags` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L464-L476) | Locks the tag list at boot |
| `KeyHash.Make` | [current/decomp/KSA/KeyHash.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KeyHash.cs#L15-L45) | Lowercase CRC32 identity used by every `Find*` |
| `ModLibrary.LoadAll` / `Bind` call sites | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L942-L978) | Where `[StarMapAllModsLoaded]` falls in startup |

## Pitfalls and version notes

- The self-test only proves that names and types still match. Semantic drift is invisible to it.
  Example: 5482 added a public `SerializedCollection<T>.Deregister`, which swap-removes from the
  list. Parts Now keeps the reflection path so the part browser order is preserved. The authors keep
  a manual watchlist for this in `scope/part-editor-and-robotics.md`.[^scope-part-editor]
- Everything else Parts Now calls (`AssetBundle.OnDataLoad`, `ThumbnailCreator`, `PartTemplate`
  members) is compile-time typed. If one of those is renamed, the game reports a missing member when
  the calling method is first JIT-compiled, not in the self-test. The loader's per-state
  `try/catch` is what contains that (inferred from the code, not observed).
- `IsHealthy` is computed once, from lists filled in the static constructor. Nothing re-runs the
  checks, so the only way to recover is to update the mod and restart.
- `_collection` is probed on `SerializedCollection<PartTemplate>` only. Each other closed generic
  resolves its own field lazily in `CollectionFields<T>`. A per-`T` miss shows up as a descriptive
  exception in `Unregister`, which the purge logs per step.
- In 5554, `RegisterTag` still adds a post-boot tag to `_editorTagLookup` (it only skips `_editorTags`,
  the list behind the category buttons, and logs a warning). If another mod registers a tag after boot,
  V7 will treat it as known even though it has no button (inferred from the decomp).[^ksa-vehicle-editor]
- The project references `KSA.dll` without a publicizer. That is why this layer exists: an
  `internal static readonly` field cannot be referenced from C# source.

## See also

- [Bundle validation rules V1-V15](./parts-now-bundle-validation.md) (the main consumer of the registry lookups)
- [Runtime unload and purge](./parts-now-runtime-unload-purge.md) (`Unregister` in use)
- [Mesh headroom reservation](./parts-now-mesh-headroom-reservation.md) (the other half of `CanLoad`)
- [Runtime part loading state machine](./parts-now-runtime-part-loading.md)
- [Parts Now sub-mod](../submods/parts-now.md)
- [Mod overview](../overview.md)
- [Plain GetField on internal registries and a generic holder for instance fields](../../../howto/patching/internals/call-private-game-members-through-reflection-and-delegates/call-private-game-members-through-reflection-and-delegates-2.md) - how-to article demonstrating this feature
- [Non-throwing static constructor with fatal and degraded lists](../../../howto/patching/safety/validate-patch-targets-and-detect-game-update-breakage/validate-patch-targets-and-detect-game-update-breakage-2.md) - how-to article demonstrating this feature

[^game-registry]: parts-now.lib/Runtime/GameRegistry.cs
[^parts-now-readme]: parts-now.lib/README.md (authors' design notes)
[^parts-now-submod]: parts-now.lib/PartsNowSubmod.cs
[^status-panel]: parts-now.lib/Ui/StatusPanel.cs
[^loader-api]: parts-now.lib/Runtime/RuntimeModLoaderApi.cs
[^paste-panel]: parts-now.lib/Ui/PastePanel.cs
[^bundle-validator]: parts-now.lib/Runtime/BundleValidator.cs
[^unscience-mod]: unscience/Mod.cs
[^parts-now-mod]: parts-now/Mod.cs
[^scope-part-editor]: scope/part-editor-and-robotics.md (authors' integration map)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-serialized-collection]: KSA.SerializedCollection (decompiled, build 2026.10.10.5554)
[^ksa-vehicle-editor]: KSA.VehicleEditor (decompiled, build 2026.10.10.5554)
[^ksa-keyhash]: KSA.KeyHash (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
