---
type: How-To
title: Parse an AssetBundle with the game serializer, without OnDataLoad, and apply pure rules
description: Fetch the AssetBundle serializer from XmlHelper.Serializers, parse an XDocument with line info, never call OnDataLoad, and hand the object tree to isolated validation rules so a runtime loader can reject a document set before anything is registered
category: assets
group: validation
topic: validate-asset-xml-references
approach: 3
difficulty: advanced
prerequisites: [starmap, reflection]
demonstrated_by: [mods/unscience/features/parts-now-bundle-validation]
techniques: [xml-serializer, offline-asset-validation]
hooks: [starmap StarMapAfterGui, starmap StarMapBeforeGui]
lifecycle: [frame:draw-ui, startup:asset-load]
game_types:
  - KSA.AssetBundle
  - KSA.MeshAtlasFileReference
  - KSA.ModLibrary
  - KSA.XmlHelper
  - KSA.XmlLoader
  - KSA.SerializedId
  - KSA.PartTemplate
tags: [validation, serializer, xmlhelper, assetbundle, parts-now, rules, runtime]
---

Offline scripts guard a mod you build. A mod that registers XML into a running game (unscience's Parts Now) cannot trust its input and cannot restart to recover. It parses each document with the exact `XmlSerializer` the game uses for `<Assets>`, takes the object tree, deliberately skips `OnDataLoad` (the call that registers everything), and runs fifteen isolated rules over the tree and read-only
lookups into the live registries before anything is written, registered or bound. This article is the parse half: how to deserialize an `AssetBundle` with the game's own serializer and without side effects, and the rule shape that sits on top.

Demonstrated by [Validate Part asset bundles with fifteen rules before anything is written or registered](../../../../mods/unscience/features/parts-now-bundle-validation.md). The runtime loader that uses it is [Load, validate and unload part mods in a running game](../../../vehicles-and-parts/parts/load-and-unload-part-mods-at-runtime/overview.md).

## Background: how the game does it

KSA's own path is `XmlLoader.Load<T>`: deserialize through `XmlHelper.Serializers[typeof(T)]`, then call `OnDataLoad(mod)`, which registers everything into `ModLibrary`; parse exceptions are logged and become null. `Deserialize<T>` builds a plain `new XmlSerializer(typeof(T))` when none is cached (the `Load` helper is the only place `OnDataLoad` runs):

```csharp
// current/decomp/KSA/XmlLoader.cs L10-L18
	public static T? Load<T>(string filePath, Mod mod) where T : ILibraryData
	{
		if (!TryDeserialize<T>(filePath, out T result))
		{
			return default(T);
		}
		result?.OnDataLoad(mod);
		return result;
	}
```

```csharp
// current/decomp/KSA/XmlLoader.cs L35-L52
	private static T? Deserialize<T>(string filePath)
	{
		if (!File.Exists(filePath))
		{
			return default(T);
		}
		using FileStream stream = File.OpenRead(filePath);
		if (!XmlHelper.Serializers.TryGetValue(typeof(T), out XmlSerializer value))
		{
			value = new XmlSerializer(typeof(T));
			XmlHelper.Serializers.Add(typeof(T), value);
		}
		if (!(value.Deserialize(stream) is T result) || 1 == 0)
		{
			return default(T);
		}
		return result;
	}
```

The `AssetBundle` serializer in `XmlHelper.Serializers` is built with `XmlAttributeOverrides` that map every `ModuleBase.TemplateDataBase` subclass (`PartModel`, `Tank`, `Collider`, `Light`...) onto `PartTemplate.Components` ([current/decomp/KSA/XmlHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlHelper.cs#L17-L47)). **A hand-built `new XmlSerializer(typeof(AssetBundle))` lacks them and silently drops every part module.**

## Lifecycle and timing

Validation runs on the game thread from the paste panel's Validate button (`[StarMapAfterGui]`, `frame:draw-ui`) and from the loader's Validate state in `Update` (`[StarMapBeforeGui]`). An error stops the load with "nothing was written, registered or bound". See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Parse into a record that carries the text, the bundle and an `XDocument`.** Reject empty text and malformed XML with line and position:

   ```csharp
   // parts-now.lib/Runtime/BundleParser.cs L65-L86
       public static bool TryParse(string sourceName, string xml, out ParsedBundle? parsed, out string? error)
       {
           parsed = null;
           error = null;

           if (string.IsNullOrWhiteSpace(xml))
           {
               error = "the document is empty.";
               return false;
           }

           XDocument document;
           try
           {
               document = XDocument.Parse(xml, LoadOptions.SetLineInfo);
           }
           catch (XmlException ex)
           {
               error = "the XML is not well formed (line " + ex.LineNumber + ", position "
                   + ex.LinePosition + "): " + ex.Message;
               return false;
           }
       // ...
   ```

2. **Fetch the game's serializer, never build one.** Fail with a message that says the game's internals changed if it is missing:

   ```csharp
   // parts-now.lib/Runtime/BundleParser.cs L88-L112
           if (XmlHelper.Serializers is null
               || !XmlHelper.Serializers.TryGetValue(typeof(AssetBundle), out XmlSerializer? serializer)
               || serializer is null)
           {
               error = "KSA has not registered an AssetBundle serializer in XmlHelper.Serializers — "
                   + "the game's internals changed and parts-now cannot parse bundles safely.";
               return false;
           }

           AssetBundle? bundle;
           try
           {
               using StringReader reader = new StringReader(xml);
               bundle = serializer.Deserialize(reader) as AssetBundle;
           }
           catch (Exception ex)
           {
               error = "the document could not be deserialized as an <Assets> bundle: " + Innermost(ex);
               return false;
           }

           if (bundle is null)
           {
               error = "the document deserialized to something that is not an <Assets> bundle.";
               return false;
           // ...
   ```

3. **Do not call `OnDataLoad`.** Because it has not run, every `SerializedId.Hash` is default, `IsReferenceable` is false, `Mod` is null and `PartTemplate.EditorTags` is empty. Rules work from `Id` strings, `EditorTagsStrings` and `KeyHash.Make(id.AsSpan())` when they need a hash ([current/decomp/KSA/SerializedId.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedId.cs#L6-L31)).

4. **Classify through the type chain, most derived first.** `SubPartGameDataReference : PartGameDataReference : PartTemplate` and `SubPartTemplate : PartTemplate`; model components normalise `PartModelModule`, `PartModelGlassModule` and `PartModelDynamicModule` templates into `(ElementName, Id, Mesh, Material)`.

5. **Build one context per run**: case-insensitive indexes of declared parts and sub-parts (one id space, as in `AllParts`), game data, materials, file ids and mesh ids (a `MeshAtlas` declares one per glTF mesh name, read from the GLB JSON chunk, [Parse the GLB JSON chunk in C# to validate mesh references](../find-z-fighting-and-degenerate-uvs-in-glb/find-z-fighting-and-degenerate-uvs-in-glb-3.md)), plus known editor tags.

6. **Run the rules as isolated pure functions, each wrapped so a throwing rule becomes a Warning**, and collect findings in rule order:

   ```csharp
   // parts-now.lib/Runtime/BundleValidator.cs L68-L94
       public static List<ValidationIssue> Validate(
           IReadOnlyList<ParsedBundle> bundles,
           string? reloadingModId,
           string modDirectory)
       {
           ArgumentNullException.ThrowIfNull(bundles);

           ValidationContext context = BuildContext(bundles, reloadingModId, modDirectory);

           RunRule(context, "V1", RuleV1RootElement);
           RunRule(context, "V2", RuleV2NonEmptyIds);
           RunRule(context, "V3", RuleV3RegistryCollisions);
           RunRule(context, "V4", RuleV4DuplicateIdsInSet);
           RunRule(context, "V5", RuleV5SubPartInstances);
           RunRule(context, "V6", RuleV6MeshReferences);
           RunRule(context, "V7", RuleV7EditorTags);
           RunRule(context, "V8", RuleV8UnsupportedElements);
           RunRule(context, "V9", RuleV9MaterialChannels);
           RunRule(context, "V10", RuleV10GameDataReferences);
           RunRule(context, "V11", RuleV11Paths);
           RunRule(context, "V12", RuleV12SubPartMeshView);
           RunRule(context, "V13", RuleV13MissingGameData);
           RunRule(context, "V14", RuleV14GameDataCollisions);
           RunRule(context, "V15", RuleV15TextureBudget);

           return context.Issues;
       }
   ```

7. **Name the game behaviour each rule prevents.** The fifteen rules:

   | Rule | Check | Game behaviour prevented |
   |---|---|---|
   | V1, V2 | Root is `<Assets>`; ids present | `XmlLoader` only logs a failed parse; unnamed parts never register |
   | V3, V4 | No id already registered or declared twice | `Register` returns false and the declaration is silently dropped |
   | V5 | `SubPart InstanceOf` resolves | `ModLibrary.Get<PartTemplate>` throws at spawn or thumbnail time |
   | V6 | `Mesh Id` resolves to a declared atlas mesh | Lazy failure at warm-up or draw |
   | V7 | Editor tags are known | After the editor loads tag definitions, `RegisterTag` no longer adds categories |
   | V8 | No startup-only top-level kinds (substances, reactions, situations, explosions...) | Their registries cannot be extended at run time |
   | V9 | Every model component has a material with Diffuse, Normal and AoRoughMetal | No null check on the three handles: a missing channel takes the game down |
   | V10 | Reaction, grain, exhaust, sound ids resolve | These libraries cannot be extended at run time |
   | V11 | Every `Path` is relative, has no `..` or drive letter, stays inside the mod folder and exists | `FileReference.Load` only logs a missing file |
   | V12, V13 | Warn on missing `MeshView` / `PartGameData` | No picking geometry; no mass or connectors |
   | V14 | No `PartGameData` id already registered | `OnDataLoad` merges into the existing entry permanently |
   | V15 | New textures fit the bindless pool | The pool does not resize |

8. **Report with rule, element id, source and line.** The `XDocument` retains line info; findings carry `Severity`, `Rule`, `Message`, `ElementId`, `SourceName`.

## Complete example

- [parts-now.lib/Runtime/BundleParser.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/BundleParser.cs), [parts-now.lib/Runtime/BundleValidator.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/BundleValidator.cs), [parts-now.lib/Runtime/BundleValidatorRulesSchema.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/BundleValidatorRulesSchema.cs)
- [parts-now.lib/Runtime/BundleValidatorContext.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/BundleValidatorContext.cs), [parts-now.lib/Runtime/GlbMeshNames.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/GlbMeshNames.cs)

## Variations and gotchas

- **Id-only `<PbrMaterial>` is a reference.** `OnDataLoad` sets `_isReference` when all three main channels are null; V9 resolves such a material against the set and the live registry before judging its channels ([current/decomp/KSA/PbrMaterialReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PbrMaterialReference.cs#L75-L94)).
- **Direct children only for V8.** Same-named references nested inside a part stay legal.
- **Registry access is reflection** and lives in one class that records failures for a self-test ([Enumerate the game's loaded meshes, textures and glTFs for pickers and id checks](../../models-and-textures/import-glb-models-at-runtime/import-glb-models-at-runtime-3.md)).
- **A new game build adds element kinds** (explosions in build 5438); revisit the unsupported-kind list on updates.
- **Not a substitute for the offline scripts** in CI; this is for content you do not control at build time.

## See also

- [Check asset XML ids and paths offline with check-xml.sh and validate-parts.py](validate-asset-xml-references-1.md), [Extend the validator for generated geometry, ids named in C# and shared planes](validate-asset-xml-references-2.md), [Register a custom XML asset type or inject new child elements into game XML](../../asset-xml/register-custom-xml-asset-type/overview.md), [Know which asset XML elements the game accepts and what they load](../../asset-xml/asset-xml-element-reference/overview.md),
  [Load, validate and unload part mods in a running game](../../../vehicles-and-parts/parts/load-and-unload-part-mods-at-runtime/overview.md), [Define a part with its own game data and GLB model](../../../vehicles-and-parts/parts/define-a-part-with-game-data/overview.md)
- [Validate Part asset bundles with fifteen rules before anything is written or registered](../../../../mods/unscience/features/parts-now-bundle-validation.md)
