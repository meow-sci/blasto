---
type: Mod Feature
title: XmlLoader call-site redirect
description: A Harmony transpiler swaps every XmlLoader.Load<T> call in four Mod loader methods for XmlPatcher.Load<T>, which deserializes the patched in-memory node with an override-aware serializer instead of reading the file
mod: kittenextensions
category: patching
techniques: [harmony-transpiler, generic-call-rewrite, xml-asset, xml-patch]
hooks:
  - starmap StarMapBeforeMain
  - harmony-transpiler KSA.Mod.PrepareSystems
  - harmony-transpiler KSA.Mod.LoadSystems
  - harmony-transpiler KSA.Mod.LoadAssetBundles
  - harmony-transpiler KSA.Mod.LoadPlanetMeshes
lifecycle: [startup:starmap-before-main, startup:immediate-load, startup:asset-load]
game_types:
  - KSA.XmlLoader
  - KSA.Mod
  - KSA.ModLibrary
  - KSA.XmlHelper
  - KSA.ILibraryData
  - KSA.AssetBundle
  - KSA.SystemInfo
  - KSA.SystemTemplate
  - KSA.PlanetMeshCollection
tags: [harmony, transpiler, generics, xml, xmlserializer, asset-loading]
---

Patching the document is only half of the job: the game still has to read the patched data. Every
XML file a mod lists in its `mod.toml` goes through one generic helper,
`XmlLoader.Load<T>(string filePath, Mod mod)`, whose six call sites in this build are all in `KSA.Mod`;
it opens the file, deserializes it and calls `OnDataLoad(mod)` on the result
([current/decomp/KSA/XmlLoader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlLoader.cs#L10-L18)).[^ksa-xmlloader] KittenExtensions leaves that
helper alone and instead rewrites its callers: in four `Mod` methods every `call
XmlLoader.Load<T>` instruction gets a new operand, `XmlPatcher.Load<T>` with the same `T`, the same
parameters and the same return type. The replacement reads the file's node out of the
[combined GameData document](gamedata-document.md) instead of the disk.[^kx-patches]

## How it works

1. **Install.** `KxMod.Setup` is the mod's `[StarMapBeforeMain]` method and calls
   `new Harmony("KittenExtensions").PatchAll()`
   ([KittenExtensions/Mod.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Mod.cs#L11-L22)).[^kx-mod]
2. **Pick the callers.** `XmlLoaderPatch` uses `[HarmonyTargetMethods]` to return
   `Mod.LoadAssetBundles`, `Mod.LoadPlanetMeshes`, `Mod.LoadSystems` and `Mod.PrepareSystems`, looked
   up by name ([KittenExtensions/Patches.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patches.cs#L35-L42)).
3. **Match the generic call.** The source is the open generic definition
   `typeof(XmlLoader).GetMethod("Load")`. For each instruction whose operand is a `MethodInfo`,
   `IsDeserializeCall` checks `IsConstructedGenericMethod` and compares
   `GetGenericMethodDefinition()` with that definition; on a match it reads `T` from
   `GetGenericArguments()[0]` ([KittenExtensions/Patches.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patches.cs#L68-L79)).
4. **Swap the operand.** The transpiler sets `inst.operand =
   XmlPatcher.Load.MakeGenericMethod(T)` and leaves the opcode, labels and stack untouched
   ([KittenExtensions/Patches.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patches.cs#L50-L66)). Both methods are `static`, take
   `(string, Mod)` and constrain `T : ILibraryData`.
5. **Read from the document.** `XmlPatcher.Load<T>` finds the `<Mod>` with the caller's `mod.Id`,
   then the file element whose path equals `Path.GetFullPath(filePath)`. If that node is valid it
   deserializes it with `AssetEx.GetSerializer<T>()` through `new XPDocReader(node)`, an `XmlReader`
   over the XPathPatch document. Exceptions are logged with `DefaultCategory.Log.Error`, and
   `result?.OnDataLoad(mod)` keeps the stock contract
   ([KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L28-L54)).[^kx-patcher]
6. **Override-aware serializer.** `AssetEx.GetSerializer<T>()` returns the cached serializer from
   `XmlHelper.Serializers` or builds one with `XmlHelper.AttributeOverrides`
   ([KittenExtensions/AssetEx.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/AssetEx.cs#L45-L52)), so custom element types registered by other mods
   deserialize (see [Custom XML asset types](custom-xml-asset-types.md)). The stock helper caches
   `new XmlSerializer(typeof(T))` without overrides for any type it has not seen
   ([current/decomp/KSA/XmlLoader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlLoader.cs#L35-L52)).[^kx-assetex]

### The redirected call sites in build 2026.10.10.5554

| Caller | `T` | Called from | Decomp |
|---|---|---|---|
| `Mod.PrepareSystems` | `SystemInfo` | `ModLibrary.PrepareAll`, per enabled mod (system picker headers) | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L417-L441) |
| `Mod.LoadAssetBundles` | `AssetBundle` | `ModLibrary.PreloadAssetBundles` and `ModLibrary.LoadAll` | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L157-L169) |
| `Mod.LoadSystems` | `SystemTemplate` | `PreloadAssetBundles` (every system file, because `SystemLibrary.Default` is still null; [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L900-L939)) and `LoadAll` (only the selected system's file) | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L128-L155) |
| `Mod.LoadPlanetMeshes` | `PlanetMeshCollection` | `ModLibrary.LoadAll` | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L338-L362) |

Two more `XmlLoader.Load<T>` callers exist in this build and are **not** redirected:
`Mod.LoadEditorTagDefinitions` (`AssetBundle`, [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L171-L190)) and
`Mod.LoadPlanetRandomHeightmaps` (`RandomHeightmapCollection`,
[current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L364-L387)).[^ksa-mod]

## Lifecycle and timing

- **Install: `startup:starmap-before-main`.** StarMap runs `[StarMapBeforeMain]` while it loads mods,
  before it invokes the game's entry point, so the transpiled bodies are in place before the
  `Program` constructor runs any of them.
- **`startup:immediate-load`.** `ModLibrary.PrepareAll` calls `Mod.PrepareSystems` for each enabled
  mod ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L429-L445)). StarMap's `StarMapImmediateLoad` is a
  prefix on that same method ([StarMap.Core/Patches/ModPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModPatches.cs#L11-L22)), so a mod's
  immediate-load code runs first and the transpiled body then reads that mod's system headers from the
  already patched document.[^ksa-modlibrary] [^starmap-modpatches]
- **`startup:asset-load`.** `PreloadAssetBundles` (right after `PrepareAll`) and `LoadAll` (after the
  system picker and `LoadEditorTags`) call the other three methods
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L899-L942)). `StarMapAllModsLoaded` fires in StarMap's
  postfix on `LoadAll` ([StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L17-L28)), after every
  redirected load has finished but before `ModLibrary.AttachGameData`
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L985)).[^ksa-program] [^starmap-modlibrarypatches]
- **Thread:** main thread only.
- **Why callers, not `XmlLoader.Load<T>`:** rewriting four non-generic callers needs no patch on a
  generic method and scopes the redirect to the mod-loading paths. Any other `XmlLoader.Load<T>` call
  keeps stock behaviour.

## Key code

| Path | Role |
|---|---|
| [KittenExtensions/Mod.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Mod.cs#L11-L22) | `[StarMapBeforeMain]`: `PatchAll` |
| [KittenExtensions/Patches.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patches.cs#L32-L80) | `XmlLoaderPatch`: targets, generic-call match, operand swap |
| [KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L28-L54) | `XmlPatcher.Load<T>`: the replacement loader |
| [KittenExtensions/AssetEx.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/AssetEx.cs#L45-L52) | `AssetEx.GetSerializer<T>` |

## Snippets

```csharp
// KittenExtensions/Patches.cs L35-L48
  [HarmonyTargetMethods]
  internal static IEnumerable<MethodBase> TargetMethods()
  {
    yield return typeof(Mod).GetMethod("LoadAssetBundles");
    yield return typeof(Mod).GetMethod("LoadPlanetMeshes");
    yield return typeof(Mod).GetMethod("LoadSystems");
    yield return typeof(Mod).GetMethod("PrepareSystems");
  }

  private static readonly MethodInfo SourceMethod = typeof(XmlLoader).GetMethod(nameof(XmlLoader.Load)) ??
    throw new InvalidOperationException($"Could not find XmlLoader source method");

  private static readonly MethodInfo ReplacementMethod = typeof(XmlPatcher).GetMethod(nameof(XmlPatcher.Load)) ??
    throw new InvalidOperationException("Could not find XmlPatcher replacement method");
```

```csharp
// KittenExtensions/Patches.cs L50-L79
  [HarmonyTranspiler]
  internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
  {
    var matcher = new CodeMatcher(instructions);

    matcher.Start();
    while (matcher.IsValid)
    {
      var inst = matcher.Instruction;
      if (IsDeserializeCall(inst, out var type))
        inst.operand = ReplacementMethod.MakeGenericMethod(type);

      matcher.Advance();
    }

    return matcher.Instructions();
  }

  private static bool IsDeserializeCall(CodeInstruction inst, out Type type)
  {
    type = default;
    if (inst.operand is not MethodInfo method)
      return false;
    if (!method.IsConstructedGenericMethod)
      return false;
    if (method.GetGenericMethodDefinition() != SourceMethod)
      return false;
    type = method.GetGenericArguments()[0];
    return true;
  }
```

## Game APIs touched

| Member | What it is for |
|---|---|
| `XmlLoader.Load<T>` ([current/decomp/KSA/XmlLoader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlLoader.cs#L10-L18)) | The call being replaced; its generic definition is the match key |
| `Mod.PrepareSystems`, `LoadSystems`, `LoadAssetBundles`, `LoadPlanetMeshes` ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs)) | Transpiled callers |
| `XmlHelper.Serializers`, `XmlHelper.AttributeOverrides` ([current/decomp/KSA/XmlHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlHelper.cs#L11-L47)) | Shared serializer cache and overrides used by the replacement |
| `ILibraryData.OnDataLoad` ([current/decomp/KSA/ILibraryData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ILibraryData.cs)) | Called on the result, as the stock loader does |

## Pitfalls and version notes

- **Editor tags come from disk.** `ModLibrary.LoadEditorTags` runs between `PreloadAssetBundles` and
  `LoadAll` and still uses the stock loader
  ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L464-L476)). The preload pass never calls
  `EditorTagDefinition.OnDataLoad` (with `Preload` set, `AssetBundle.OnDataLoad` only reaches
  celestials, vehicles and situations, [current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L84-L108)),
  so this unpatched pass is the first to register every `<EditorTagDef>` through
  `ModLibrary.Register`, and `SerializedCollection.Register` keeps the first entry per id
  ([current/decomp/KSA/SerializedCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedCollection.cs#L20-L35)). The face-snap and
  root-part lists that `VehicleEditor.MarkEditorTagDefinitionsLoaded` builds right after
  ([current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L7086-L7094)) see unpatched
  attributes. `LoadAll` later deserializes the patched bundles and calls
  `EditorTagDefinition.OnDataLoad` again ([current/decomp/KSA/EditorTagDefinition.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EditorTagDefinition.cs#L25-L33)),
  but the duplicate registration is rejected, and a tag that only a patch adds is registered after
  the flags were read. The stock path still parses custom `KxAsset` elements, because it shares the
  `AssetBundle` serializer that `AssetEx.Init` rebuilt.[^ksa-vehicleeditor] [^ksa-editortagdefinition] [^ksa-serializedcollection] [^ksa-assetbundle]
- **Random heightmap collections bypass patches** entirely (not imported, not redirected).
- **Files must be in the document.** `XmlPatcher.Load<T>` only finds files that some `mod.toml`
  lists under `planetMeshes`, `systems` or `assets`. When the node is not found the method checks
  `node.Valid`, skips deserialization and returns `null` without logging anything.
- **Name-based targets.** The four methods are found with `typeof(Mod).GetMethod(name)`; a rename in a
  future build makes `GetMethod` return `null` and the transpiler cannot be applied to that caller.
- **One redirect wins.** Once a transpiler has rewritten the operand, a second transpiler looking for
  `XmlLoader.Load<T>` in the same methods finds nothing. This is one reason the original
  KittenExtensions and KittenExtensions Continued cannot be installed together (an inference from the
  code; the README only states the rule).
- **`XmlHelper.Serializers` is shared.** The stock helper and `AssetEx.GetSerializer` use the same
  dictionary; whichever creates a type's entry first decides whether it has overrides. KittenExtensions
  rebuilds the existing entries in `AssetEx.Init` before any load runs.

## See also

- [Combined GameData XML document](gamedata-document.md)
- [Custom XML asset types via KxAsset attributes](custom-xml-asset-types.md)
- [Continued fork with a new mod id and the old assembly name](continued-fork-identity.md)
- [StarMap startup lifecycle hooks](../../starmap/features/startup-lifecycle-hooks.md)
- [Game loop and lifecycle](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Transpile the Mod loaders so XmlLoader.Load calls read from an in-memory document](../../../howto/assets/xml-patching/redirect-game-xml-loading/redirect-game-xml-loading-2.md) - how-to article demonstrating this feature

[^ksa-xmlloader]: KSA.XmlLoader (decompiled, build 2026.10.10.5554)
[^kx-patches]: KittenExtensions/Patches.cs
[^kx-mod]: KittenExtensions/Mod.cs
[^kx-patcher]: KittenExtensions/Patch/Patcher.cs
[^kx-assetex]: KittenExtensions/AssetEx.cs
[^ksa-mod]: KSA.Mod (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^starmap-modpatches]: StarMap.Core/Patches/ModPatches.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^starmap-modlibrarypatches]: StarMap.Core/Patches/ModLibraryPatches.cs
[^ksa-vehicleeditor]: KSA.VehicleEditor (decompiled, build 2026.10.10.5554)
[^ksa-editortagdefinition]: KSA.EditorTagDefinition (decompiled, build 2026.10.10.5554)
[^ksa-serializedcollection]: KSA.SerializedCollection (decompiled, build 2026.10.10.5554)
[^ksa-assetbundle]: KSA.AssetBundle (decompiled, build 2026.10.10.5554)
