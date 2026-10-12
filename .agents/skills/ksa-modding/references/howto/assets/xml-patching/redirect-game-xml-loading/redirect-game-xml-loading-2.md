---
type: How-To
title: Transpile the Mod loaders so XmlLoader.Load calls read from an in-memory document
description: Use a Harmony transpiler on four KSA.Mod methods to swap every constructed XmlLoader.Load<T> call for your own generic loader, keeping the OnDataLoad contract and the override-aware serializer
category: assets
group: xml-patching
topic: redirect-game-xml-loading
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/kittenextensions/features/xmlloader-call-site-redirect]
techniques: [harmony-transpiler, xml-patch]
hooks:
  - starmap StarMapBeforeMain
  - harmony-transpiler KSA.Mod.LoadAssetBundles
  - harmony-transpiler KSA.Mod.LoadPlanetMeshes
  - harmony-transpiler KSA.Mod.LoadSystems
  - harmony-transpiler KSA.Mod.PrepareSystems
lifecycle: [startup:starmap-before-main, startup:immediate-load, startup:asset-load]
game_types:
  - KSA.Mod
  - KSA.ModLibrary
  - KSA.XmlLoader
  - KSA.SystemInfo
  - KSA.AssetBundle
  - KSA.SystemTemplate
  - KSA.SerializedCollection
tags: [xml, redirect, transpiler, harmony, xmlloader, call-site]
---

KittenExtensions (KX) patches XML in memory ([Patch Core or another mod's XML data without copying files](../patch-game-xml-data/overview.md)), but the game still has to read the
patched data. Instead of touching the generic `XmlLoader.Load<T>` helper it rewrites its callers: in four
`Mod` methods every `call XmlLoader.Load<T>` instruction gets a new operand, `XmlPatcher.Load<T>` with the
same `T`, the same parameters and the same return type. This article shows the transpiler so you can redirect
mod XML loading to any source (a database, an in-memory document, a decrypted file) without swapping file
names. It is the heavy tool; approach [Swap Core's XML entries for patched copies with prefixes on the Mod loaders](redirect-game-xml-loading-1.md) is simpler when a file copy suffices.

Demonstrated by [XmlLoader call-site redirect](../../../../mods/kittenextensions/features/xmlloader-call-site-redirect.md).

## Background: how the game does it

Every XML file a mod lists in `mod.toml` goes through one generic helper. It opens the file, deserializes it
with a cached serializer and calls `OnDataLoad(mod)` on the result:

```csharp
// current/decomp/KSA/XmlLoader.cs L8-L18
public static class XmlLoader
{
	public static T? Load<T>(string filePath, Mod mod) where T : ILibraryData
	{
		if (!TryDeserialize<T>(filePath, out T result))
		{
			return default(T);
		}
		result?.OnDataLoad(mod);
		return result;
	}
// ...
```

Six call sites in `KSA.Mod` use it in this build. KX redirects four and leaves two stock:

| Caller | `T` | Called from |
|---|---|---|
| `Mod.PrepareSystems` | `SystemInfo` | `ModLibrary.PrepareAll`, per enabled mod (system picker headers) |
| `Mod.LoadAssetBundles` | `AssetBundle` | `ModLibrary.PreloadAssetBundles` and `ModLibrary.LoadAll` |
| `Mod.LoadSystems` | `SystemTemplate` | `PreloadAssetBundles` (every system file, because `SystemLibrary.Default` is still null) and `LoadAll` (only the selected system's file) |
| `Mod.LoadPlanetMeshes` | `PlanetMeshCollection` | `ModLibrary.LoadAll` |
| `Mod.LoadEditorTagDefinitions` | `AssetBundle` | `ModLibrary.LoadEditorTags` (not redirected) |
| `Mod.LoadPlanetRandomHeightmaps` | `RandomHeightmapCollection` | `ModLibrary.LoadAll` (not redirected) |

([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L128-L180), [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L338-L390), [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L417-L440)). The stock helper also caches
`new XmlSerializer(typeof(T))` without the game's attribute overrides for any type it has not seen
([current/decomp/KSA/XmlLoader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlLoader.cs#L1-L52)), which matters when mods register custom element types
([Register a custom XML asset type or inject new child elements into game XML](../../asset-xml/register-custom-xml-asset-type/overview.md)).

Asset registration is first-wins: `SerializedCollection.Register` uses `TryAdd` and returns false for a
second item with the same hash, so duplicate ids across files silently keep the first
([current/decomp/KSA/SerializedCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedCollection.cs#L20-L33)).

## Lifecycle and timing

- **Install: `startup:starmap-before-main`.** KX's `[StarMapBeforeMain]` method calls `PatchAll`, so the
  transpiled bodies are in place before the `Program` constructor runs any of them.
- **`startup:immediate-load`.** `ModLibrary.PrepareAll` calls `Mod.PrepareSystems` for each enabled mod;
  StarMap's `StarMapImmediateLoad` is a prefix on that same method, so a mod's immediate-load code runs
  first and the transpiled body then reads that mod's system headers from the already patched document.
- **`startup:asset-load`.** `PreloadAssetBundles` and `LoadAll` call the other three methods
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L899-L942)). `StarMapAllModsLoaded` fires after every redirected load has finished but
  before `ModLibrary.AttachGameData`. Main thread only. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Install at `BeforeMain` with a Harmony instance.** KX does it in its mod class:

   ```csharp
   // KittenExtensions/Mod.cs L8-L23
   [StarMapMod]
   public class KxMod
   {
       [StarMapBeforeMain]
       public void Setup()
       {
           var logFile = PatchLogger.LogPath;
           if (File.Exists(logFile))
           {
               File.Delete(logFile);
           }

           var harmony = new Harmony("KittenExtensions");
           harmony.PatchAll();
       }
   }
   ```

2. **Pick the callers by name** with `[HarmonyTargetMethods]`, and look up the open generic source and your
   replacement:

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

3. **Match constructed generic calls and swap the operand.** `IsConstructedGenericMethod` plus
   `GetGenericMethodDefinition()` identify `XmlLoader.Load<T>` for any `T`; read `T` from
   `GetGenericArguments()` and build the replacement with `MakeGenericMethod`. The opcode, labels and stack
   are untouched because both methods have the same signature `(string, Mod)` and return `T`:

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

4. **Write the replacement loader with the same contract.** It must be static, take `(string, Mod)`,
   constrain `T : ILibraryData`, return `T` or `default`, and call `OnDataLoad(mod)` on the result as the stock
   helper does. KX finds the `<Mod>` with `mod.Id`, then the file node whose path equals
   `Path.GetFullPath(filePath)`, and deserializes it through an `XmlReader` over its document:

   ```csharp
   // KittenExtensions/Patch/Patcher.cs L28-L54
       public static T Load<T>(string filePath, Mod mod) where T : ILibraryData
       {
           if (!XmlRootName.TryGetValue(typeof(T), out var rootName))
           {
               rootName = typeof(T).GetCustomAttribute<XmlRootAttribute>()?.ElementName;
               if (string.IsNullOrEmpty(rootName))
                   rootName = typeof(T).Name;
               XmlRootName[typeof(T)] = rootName;
           }
           var result = default(T);
           try
           {
               var node = Domain.Mods.FirstOrDefault(m => m.Id == mod.Id).File(Path.GetFullPath(filePath));
               if (node.Valid)
               {
                   var serializer = AssetEx.GetSerializer<T>();
                   using var reader = new XPDocReader(node);
                   result = serializer.Deserialize(reader) is T val ? val : default;
               }
           }
           catch (Exception ex)
           {
               DefaultCategory.Log.Error(ex);
           }
           result?.OnDataLoad(mod);
           return result;
       }
   ```

5. **Use an override-aware serializer.** The replacement uses `XmlHelper.Serializers` or builds one with
   `XmlHelper.AttributeOverrides`, so custom element types registered by other mods still deserialize:

   ```csharp
   // KittenExtensions/AssetEx.cs L45-L52
       public static XmlSerializer GetSerializer<T>()
       {
           var type = typeof(T);
           if (XmlHelper.Serializers.TryGetValue(type, out var serializer))
               return serializer;

           return XmlHelper.Serializers[type] = new XmlSerializer(type, XmlHelper.AttributeOverrides);
       }
   ```

6. **Make the source of truth ready before the first redirected loader runs.** `Mod.PrepareSystems` is the
   first of the four to be called (from `ModLibrary.PrepareAll`), so whatever your replacement reads must exist
   by then. KX builds its document in a prefix on `ModLibrary.PrepareAll` itself
   ([Patch Core or another mod's XML with a patches array in mod.toml](../patch-game-xml-data/patch-game-xml-data-1.md)).

## Complete example

- [KittenExtensions/Patches.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patches.cs) (`XmlLoaderPatch`)
- [KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs) (`XmlPatcher.Load<T>`)
- [KittenExtensions/AssetEx.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/AssetEx.cs) and [KittenExtensions/Mod.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Mod.cs)

## Variations and gotchas

- **Why callers, not `XmlLoader.Load<T>`.** Rewriting four non-generic callers needs no patch on a generic
  method and scopes the redirect to mod loading; the lesson from [Swap Core's XML entries for patched copies with prefixes on the Mod loaders](redirect-game-xml-loading-1.md) is that a
  patch on one reference-type instantiation of shared generic code hijacks every instantiation. Any other
  `XmlLoader.Load<T>` call (outside these four methods) keeps stock behaviour.
- **Two callers stay stock.** `LoadEditorTagDefinitions` and `LoadPlanetRandomHeightmaps` read from disk, so
  a file-name swap ([Swap Core's XML entries for patched copies with prefixes on the Mod loaders](redirect-game-xml-loading-1.md)) is visible there but not to the transpiled path, and a
  patch of an editor tag definition in an `assets` file is not seen by `LoadEditorTags`.
- **Fragile by construction.** The method list and the call shape are hard-coded. A game update that moves an
  `XmlLoader.Load<T>` call into a helper makes the transpiler match nothing silently.
- **Stricter than stock.** KX imports every manifest mod's XML up front; one malformed file anywhere shows
  the error popup and exits, whereas the stock loader logs and returns null. Read ([current/decomp/KSA/XmlLoader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlLoader.cs#L20-L52)).
- **Duplicate ids** resolve first-wins at registration ([current/decomp/KSA/SerializedCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedCollection.cs#L20-L33)), so patching
  the id of an asset to collide with another does not replace it.
- **Incompatible with file swaps** ([Swap Core's XML entries for patched copies with prefixes on the Mod loaders](redirect-game-xml-loading-1.md)): do not combine the two techniques.

## See also

- [Swap Core's XML entries for patched copies with prefixes on the Mod loaders](redirect-game-xml-loading-1.md), [Patch Core or another mod's XML data without copying files](../patch-game-xml-data/overview.md), [Understand when and in which order KSA loads mod XML](../../asset-xml/mod-xml-load-order/overview.md),
  [Register a custom XML asset type or inject new child elements into game XML](../../asset-xml/register-custom-xml-asset-type/overview.md)
- [Write a Harmony transpiler for a game method](../../../patching/transpilers/write-a-harmony-transpiler/overview.md), [Swap one call inside a game method with a transpiler](../../../patching/transpilers/swap-one-call-inside-a-game-method-with-a-transpiler/overview.md)
- [XmlLoader call-site redirect](../../../../mods/kittenextensions/features/xmlloader-call-site-redirect.md), [Combined GameData XML document](../../../../mods/kittenextensions/features/gamedata-document.md)
