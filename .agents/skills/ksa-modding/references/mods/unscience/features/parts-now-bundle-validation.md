---
type: Mod Feature
title: Validate Part asset bundles with fifteen rules before anything is written or registered
description: Parts Now deserializes submitted XML with KSA's own AssetBundle serializer but never calls OnDataLoad, then runs rules V1-V15 as isolated pure functions over the set, blocking the load on any error, including the PbrMaterial channel crash guard and the V8 registry rule
mod: unscience
submod: parts-now
category: assets
techniques: [xml-asset, reflection, glb-model, xml-serializer]
hooks:
  - starmap StarMapBeforeGui (loader Validate state via ISubmod.Update)
  - starmap StarMapAfterGui (paste panel Validate button via ISubmod.RenderContent)
  - reflection KSA.ModLibrary registries and KSA.VehicleEditor._editorTagLookup (read-only, through GameRegistry)
lifecycle: [frame:draw-ui]
game_types:
  - KSA.AssetBundle
  - KSA.XmlHelper
  - KSA.XmlLoader
  - KSA.SerializedId
  - KSA.PartTemplate
  - KSA.SubPartTemplate
  - KSA.PartGameDataReference
  - KSA.SubPartGameDataReference
  - KSA.PartInstance
  - KSA.PbrMaterialReference
  - KSA.TextureReference
  - KSA.FileReference
  - KSA.MeshAtlasFileReference
  - KSA.MeshFileReference
  - KSA.PartModelModule
  - KSA.PartModelGlassModule
  - KSA.PartModelDynamicModule
  - KSA.MeshViewModule
  - KSA.VehicleEditor
  - KSA.SubstanceLibrary
  - KSA.GrainGeometryLibrary
  - KSA.VolumetricExhaustTemplate
  - KSA.SoundBehavior
  - KSA.ModLibrary
  - KSA.Program
  - KSA.Rendering.Thumbnails.ThumbnailRenderResources
  - RenderCore.Systems.BindlessTextureLibrary
tags:
  - validation
  - xml
  - asset-bundle
  - serializer
  - part-xml
  - crash-guard
  - pbr-material
  - glb
  - editor-tags
  - bindless-textures
---

KSA handles bad asset XML quietly. A duplicate id is dropped, a missing file is only logged, an
unknown sub-part or mesh id throws the first time the part is drawn, and a material missing one
texture channel takes the whole game down. That is survivable at boot, where the player edits the
XML and restarts. It is not survivable for [Parts Now](./parts-now-runtime-part-loading.md), which
registers content into a running game. So every submitted document set passes fifteen rules before
anything is written, registered or bound, and any error stops the load with a message that names
the rule, the element id and usually the line.[^parts-now-readme]

Mechanism in one line: deserialize each document with the exact `XmlSerializer` KSA uses for
`<Assets>` (from `XmlHelper.Serializers`) and with `XDocument` (with line info), deliberately skip
`OnDataLoad`, then run V1-V15 as isolated pure functions over the parsed set plus read-only lookups
into the live registries.

## How it works

**Parsing without side effects** ([parts-now.lib/Runtime/BundleParser.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/BundleParser.cs#L65-L117)).
KSA's own path is `XmlLoader.Load<T>`: deserialize through `XmlHelper.Serializers[typeof(T)]`, then
call `OnDataLoad(mod)`, which registers everything into `ModLibrary`. Parse exceptions are logged and
turned into `null`.[^ksa-xml-loader] Parts Now splits the two halves. `TryParse` performs only the
deserialize, into a `ParsedBundle(SourceName, Xml, Bundle, Document)`; `AssetBundle.OnDataLoad` runs
later in the loader's `RegisterBundles` state. The serializer has to be KSA's instance, because the
`XmlHelper` static constructor builds it with `XmlAttributeOverrides` that map every
`ModuleBase.TemplateDataBase` subtype (`<PartModel>`, `<Tank>`, ...) onto `PartTemplate.Components`.
A hand-built `new XmlSerializer(typeof(AssetBundle))` lacks those overrides and silently drops every
component.[^ksa-xml-helper] Since `OnDataLoad` has not run, every `SerializedId.Hash` is still
default, `IsReferenceable` is false and `PartTemplate.EditorTags` is empty. The rules therefore work
from `Id` strings, from `EditorTagsStrings`, and from `KeyHash.Make` when they need a hash.[^bundle-parser]

**Classifying assets** ([parts-now.lib/Runtime/BundleParserQueries.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/BundleParserQueries.cs)).
`AssetBundle.Assets` is a `List<SerializedId>`, and the part types form one chain
(`SubPartGameDataReference : PartGameDataReference : PartTemplate`, `SubPartTemplate : PartTemplate`),
so every helper tests the most derived type first. `ModelComponents` normalizes `PartModelModule`,
`PartModelGlassModule` and `PartModelDynamicModule` templates into `(ElementName, Id, Mesh,
Material)`. `AllTextureFiles` walks top-level `<Texture>`s, top-level `<PbrMaterial>` channels and
inline materials inside model components.

**The context** ([parts-now.lib/Runtime/BundleValidatorContext.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/BundleValidatorContext.cs)) is built once per run.
It holds case-insensitive indexes of declared parts and sub-parts (one id space, as in
`ModLibrary.AllParts`), game data, materials, file ids (declared `Id` or the mod path `FileReference`
falls back to), and mesh ids. A `<MeshFile>` mesh takes the file's id. A `<MeshAtlas>` declares one
mesh per glTF mesh name, read by `GlbMeshNames` straight from the GLB's JSON chunk without loading
geometry. The context also keeps a flat `(Kind, Id, SourceName)` declaration list for duplicate
reporting and the known editor tags from [GameRegistry](./parts-now-reflection-self-test.md).[^validator-context][^glb-mesh-names]

**The rules.** `Validate(bundles, reloadingModId, modDirectory)` runs them in order, each wrapped in
`RunRule`. A rule that throws becomes a **Warning** naming the rule, so the other fourteen still
report.[^bundle-validator] "XDoc" means the rule reads the `XDocument`, "graph" the deserialized objects.

| Rule | Severity | Reads | Check | Game behavior it prevents |
|---|---|---|---|---|
| V1 | Error | XDoc | Root element is `<Assets>`; a parse failure is reported as V1 by the caller | `AssetBundle` is `[XmlRoot("Assets")]`; `XmlLoader` only logs a failed parse[^ksa-asset-bundle] |
| V2 | Error | graph | `<Part>`, `<SubPart>`, game data and `<PbrMaterial>` have an `Id`; file references have an `Id` or a `Path` | `SerializedId.OnDataLoad` sets `IsReferenceable = !string.IsNullOrEmpty(Id)` and `PartTemplate` registers only when referenceable; `FileReference` falls back to `Id = ModPath`[^ksa-serialized-id][^ksa-part-template][^ksa-file-reference] |
| V3 | Error | graph | No declared part, material, mesh or file id is already registered (the reloading mod's own ids are exempt) | `SerializedCollection.Register` returns false and callers treat the declaration as a reference, so it is silently dropped and a file is never read |
| V4 | Error | graph | No id declared twice in the set (per id space, case-insensitive) | Same silent drop; message names every document |
| V5 | Error | graph | Every `<SubPart InstanceOf>` (also inside game data) resolves in the set or `AllParts` | `PartInstance.GetTemplate()` is `ModLibrary.Get<PartTemplate>`, which throws on a miss at spawn or thumbnail time[^ksa-part-instance] |
| V6 | Error, or Warning when an atlas cannot be read | XDoc | Every `<Mesh Id>` anywhere resolves to a mesh the set creates or one already registered | Lazy failure at model warm-up or draw time |
| V7 | Error (Warning if tags unreadable) | graph | Every `EditorTagsStrings` value is a known tag | After `MarkEditorTagDefinitionsLoaded`, `RegisterTag` no longer adds to the category list[^ksa-vehicle-editor] |
| V8 | Error | XDoc, root children only | No top-level `<Substance>`, `<MixtureReaction>`, `<FixedReaction>`, `<ThermalReaction>`, `<GrainGeometry>`, `<Situation>`, `<EditorTagDef>`, `<Explosion>`, `<ExplosionVolume>` | Their registries are startup-only or outside the loader's snapshot, rollback and purge (see below) |
| V9 | Error | graph | Every model component has a `<Material>` whose definition (inline, declared in the set, or registered) has `<Diffuse>`, `<Normal>` and `<AoRoughMetal>` | The crash guard (see below) |
| V10 | Error (Warning if the library is empty) | XDoc | `<Reaction Id>`, `<Grain Id>`, `<VolumetricExhaust Id>`, `<SoundEvent SoundId>` resolve | These libraries cannot be extended at runtime; a miss throws at spawn |
| V11 | Error (existence half degrades to one Warning if the folder does not exist yet) | XDoc, every element | Every `Path=` is non-empty, relative, has no `..` or drive letter, stays inside the mod folder and exists | `FileReference.Load` only logs a missing file, leaving a half-loaded mod |
| V12 | Warning | graph | A `<SubPart>` has a `<MeshView>` | No geometry for editor picking |
| V13 | Warning | graph | A top-level `<Part>` has `<PartGameData>` with the same id | The part loads with no mass, connectors or game-data modules |
| V14 | Error | graph | No `<PartGameData>` id already registered (reloading mod exempt) | `PartGameDataReference.OnDataLoad` merges into the existing entry when `Register` fails, additively and permanently[^ksa-part-game-data] |
| V15 | Error (Warning if the pool is unreadable) | graph | New textures, de-duplicated by path, fit in `MaxTextures - 16` minus `TextureCount` | `BindlessTextureLibrary` uses `FreeListIndexPool(MaxTextures, allowResize: false)`; `Program` requests 1024 slots, clamped to device limits[^ksa-bindless-textures][^ksa-program] |

**V9, the crash guard.** `ThumbnailRenderResources.AddDraw` and the `WriteInstancesToGpu` of
`PartModel`, `PartModelGlass` and `PartModelDynamic` read `Material.DiffuseReference.BindlessHandle`,
`.NormalReference.BindlessHandle` and `.PBRMap.BindlessHandle` with no null check. Only `EmissiveMap`
and `ThinFilmMap` use `?.`. A part with a missing channel throws out of the first thumbnail render or
the first frame it is visible.[^ksa-thumbnail-render-resources][^ksa-part-model] There is a subtlety:
a `<PbrMaterial>` that declares none of the three channels is a *reference*.
`PbrMaterialReference.OnDataLoad` sets `_isReference = Diffuse == null && Normal == null && PBRMap == null`.
V9 mirrors that test: it resolves the id against the set, then `GameRegistry.FindMaterial`, and judges
the resolved definition's channels.[^ksa-pbr-material][^rules-schema]

**V8 and the explosion registries.** KSA 5438 added top-level `<Explosion>` (`ExplosionReference`,
registered in `ModLibrary.AllExplosions`) and `<ExplosionVolume>` (`ExplosionVolumeTemplate`, with
its own `internal static` `References` collection) to `AssetBundle`.[^ksa-asset-bundle][^ksa-modlibrary][^ksa-explosion-volume]
The loader's registry marks cover only parts, game data, materials, files, meshes, loaders and
binders, so these definitions could be registered but never rolled back or purged. V8 rejects them
alongside the seven older startup-only kinds. It inspects only direct children of `<Assets>`, so
same-named *references* nested inside a part stay legal.[^rules-schema]

**Where results go.** Each finding is a `ValidationIssue(Severity, Rule, Message, ElementId, SourceName)`.
`ValidationIssueView.Render` groups them under "Errors (these block the load)" and "Warnings", prefixes
each line with `rule  element [source]`, and has a **Copy issues** button that flattens them to
`ERROR`/`WARN` lines. The paste panel shows its own Validate results, and the Results panel shows the
loader's `RuntimeModLoader.Issues`.[^validation-issue][^issue-view][^results-panel]

## Lifecycle and timing

Validation runs on the game thread from two places:

| Caller | Hook | Mod folder passed | Effect of an error |
|---|---|---|---|
| Paste panel **Validate** button (`PastePanelActions.Validate`) | `RenderContent`, i.e. `[StarMapAfterGui]` (postfix on `Program.OnDrawUiViewports`) | The resolved target folder, which does not exist yet | Install & Load stays disabled; nothing written[^paste-panel-actions] |
| Loader `Validate` state (`StateValidate`) | `ISubmod.Update`, i.e. `[StarMapBeforeGui]` (prefix on `Program.OnDrawUiFrame`), or unscience's hidden-HUD replay | Install: the target folder (not written yet). Load and Reload: the existing folder | `Fail(job, "... nothing was written, registered or bound.")`[^loader-states] |

Why at the start of the job, in a UI-phase hook:

- It is the first state. A reload purges its previous load **after** the rules pass, so XML that no
  longer validates leaves the loaded mod untouched.
- Every live-registry read (`Find*`, the editor tag lookup, `BindlessTextures.TextureCount`,
  `SubstanceLibrary`, `GrainGeometryLibrary`) happens on the main thread while no Parts Now loader
  worker is running. Validation precedes `RunLoaders`, which is the only off-thread step.
- The work is file reads (GLB JSON chunks, `File.Exists`) and object walks: bounded, synchronous, one
  frame. It touches no simulated state, so running after `ExecuteNext*Solvers` is harmless.

## Key code

| Path | Role |
|---|---|
| [parts-now.lib/Runtime/BundleParser.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/BundleParser.cs) | `TryParse` with KSA's serializer and `XDocument`; `ParsedBundle` |
| [parts-now.lib/Runtime/BundleParserQueries.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/BundleParserQueries.cs) | Most-derived-first classification, model components, texture walk, effective file id |
| [parts-now.lib/Runtime/BundleValidator.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/BundleValidator.cs#L68-L145) | Rule order, `HasErrors`, `ParseFailure`, `RunRule` isolation |
| [parts-now.lib/Runtime/BundleValidatorContext.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/BundleValidatorContext.cs) | Shared indexes, atlas mesh-name harvesting |
| [parts-now.lib/Runtime/BundleValidatorRulesIdentity.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/BundleValidatorRulesIdentity.cs) | V1, V2, V3, V4, V14, V15 |
| [parts-now.lib/Runtime/BundleValidatorRulesReferences.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/BundleValidatorRulesReferences.cs) | V5, V6, V7, V10, V13[^rules-references] |
| [parts-now.lib/Runtime/BundleValidatorRulesSchema.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/BundleValidatorRulesSchema.cs) | V8, V9, V11, V12 |
| [parts-now.lib/Runtime/BundleValidationIssue.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/BundleValidationIssue.cs) | `IssueSeverity`, `ValidationIssue` record |
| [parts-now.lib/Runtime/GlbMeshNames.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/GlbMeshNames.cs) | GLB/glTF mesh names from the JSON chunk only |
| [parts-now.lib/Ui/ValidationIssueView.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Ui/ValidationIssueView.cs) | Grouped ImGui list and clipboard export |
| [ksa-upgrade.tests/BundleValidatorChecks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-upgrade.tests/BundleValidatorChecks.cs) | Managed V8 regression checks |

## Snippets

Parsing with the game's serializer, not a new one:

```csharp
// parts-now.lib/Runtime/BundleParser.cs L88-L116
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
        }

        parsed = new ParsedBundle(sourceName, xml, bundle, document);
        return true;
```

V8: rejected kinds, matched on root children only:

```csharp
// parts-now.lib/Runtime/BundleValidatorRulesSchema.cs L25-L60
    private static readonly string[] UnsupportedElements =
    {
        "Substance", "MixtureReaction", "FixedReaction", "ThermalReaction",
        "GrainGeometry", "Situation", "EditorTagDef",
        "Explosion", "ExplosionVolume",
    };
// ...
    private static void RuleV8UnsupportedElements(ValidationContext context)
    {
        HashSet<string> unsupported = new HashSet<string>(UnsupportedElements, StringComparer.Ordinal);

        foreach (ParsedBundle bundle in context.Bundles)
        {
            XElement? root = bundle.Document.Root;
            if (root is null)
            {
                continue;
            }

            foreach (XElement element in root.Elements())
            {
                string name = element.Name.LocalName;
                if (!unsupported.Contains(name))
                {
                    continue;
                }

                AddError(context, "V8", bundle.SourceName,
                    element.Attribute("Id")?.Value ?? string.Empty,
                    "<" + name + "> (line " + BundleParser.LineNumber(element)
                    + ") is out of scope for parts-now: its registry is not managed by this runtime loader. "
// ...
```

V9: judging the resolved material's three mandatory channels:

```csharp
// parts-now.lib/Runtime/BundleValidatorRulesSchema.cs L271-L297
        List<string> missing = new List<string>(3);
        if (definition.DiffuseReference is null)
        {
            missing.Add("<Diffuse>");
        }

        if (definition.NormalReference is null)
        {
            missing.Add("<Normal>");
        }

        if (definition.PBRMap is null)
        {
            missing.Add("<AoRoughMetal>");
        }

        if (missing.Count == 0)
        {
            return;
        }

        string materialId = string.IsNullOrEmpty(definition.Id) ? "(inline)" : definition.Id;
        AddError(context, "V9", bundle.SourceName, materialId,
            "material '" + materialId + "' used by " + origin + " is missing "
            + string.Join(" and ", missing)
            + ". All three of <Diffuse>, <Normal> and <AoRoughMetal> are mandatory — KSA reads their "
            + "bindless handles without a null check and would crash the game.");
```

The game code V9 protects (build 5554):

```csharp
// current/decomp/KSA.Rendering.Thumbnails/ThumbnailRenderResources.cs L156-L162
		ThumbnailPerDrawData reference2 = new ThumbnailPerDrawData
		{
			DiffuseTextureIndex = inTemplate.Material.DiffuseReference.BindlessHandle,
			NormalTextureIndex = inTemplate.Material.NormalReference.BindlessHandle,
			PbrTextureIndex = inTemplate.Material.PBRMap.BindlessHandle,
			EmissiveTextureIndex = (inTemplate.Material?.EmissiveMap?.BindlessHandle ?? (-1))
		};
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `XmlHelper.Serializers` and its static constructor | [current/decomp/KSA/XmlHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlHelper.cs#L13-L47) | The `AssetBundle` serializer with component overrides |
| `XmlLoader.Load<T>` | [current/decomp/KSA/XmlLoader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlLoader.cs#L10-L52) | KSA's deserialize-then-`OnDataLoad` path that Parts Now splits |
| `AssetBundle` (`[XmlRoot("Assets")]`, `Assets`, element list) | [current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L9-L77) | Root and the element kinds V8 filters (`Explosion`, `ExplosionVolume` at L71-L72) |
| `SerializedId.OnDataLoad` / `IsReferenceable` | [current/decomp/KSA/SerializedId.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedId.cs#L25-L31) | V2 |
| `PartTemplate.SubPartInstances`, `EditorTagsStrings`, registration gate | [current/decomp/KSA/PartTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTemplate.cs#L20-L30) | V2, V5, V7 (registers only when referenceable, L244-L246) |
| `FileReference.OnDataLoad` (`Id = ModPath`) | [current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L39-L46) | V2, effective file ids for V3 |
| `PbrMaterialReference` channels, `_isReference` | [current/decomp/KSA/PbrMaterialReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PbrMaterialReference.cs#L12-L93) | V9, V15 |
| `PartGameDataReference.OnDataLoad` | [current/decomp/KSA/PartGameDataReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartGameDataReference.cs#L12-L19) | V14 (merge on failed register) |
| `PartInstance.GetTemplate` | [current/decomp/KSA/PartInstance.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartInstance.cs#L113-L116) | V5 |
| `MeshAtlasFileReference.DoLoad` | [current/decomp/KSA/MeshAtlasFileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshAtlasFileReference.cs#L25-L48) | One mesh per glTF mesh name, the ids V6 predicts |
| `VehicleEditor.RegisterTag`, `_editorTagLookup` | [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L7175-L7200) | V7 |
| `ModLibrary.AllExplosions`, `Get<T>`, `LoadEditorTags` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L142) | V8 registry, V10 sound lookup (`Get<T>` throws on a miss, L1059) |
| `ExplosionVolumeTemplate.References` | [current/decomp/KSA/ExplosionVolumeTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ExplosionVolumeTemplate.cs#L85) | The second 5438 registry V8 keeps out |
| `ThumbnailRenderResources.AddDraw` | [current/decomp/KSA.Rendering.Thumbnails/ThumbnailRenderResources.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Thumbnails/ThumbnailRenderResources.cs#L145-L174) | Unguarded channel reads (V9) |
| `PartModel.WriteInstancesToGpu` (and Glass L575-L577, Dynamic L506-L508) | [current/decomp/KSA/PartModel.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModel.cs#L518-L520) | Unguarded channel reads every frame (V9) |
| `BindlessTextureLibrary.MaxTextures`, `TextureCount`, `FreeListIndexPool(..., allowResize: false)`; `Program.BindlessTextures` | [current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs#L24-L61) | V15 (1024 slots requested at `Program.cs` L830, clamped by `ClampToDeviceLimits`) |

`SubstanceLibrary.AllReactions` / `TryGetReaction`, `GrainGeometryLibrary.All` / `TryGet` and
`VolumetricExhaustTemplate.Get` (V10) are public static lookups in `KSA/SubstanceLibrary.cs`,
`KSA/GrainGeometryLibrary.cs` and `KSA/VolumetricExhaustTemplate.cs`.

## Pitfalls and version notes

- A rule that throws is reported as a **warning**, so it does not block the load. That keeps the
  other findings visible, but a rule broken by a KSA rename stops protecting until someone reads the
  warning.
- **Underscore mesh names.** `GlbMeshNames` skips glTF mesh names starting with `_`, following the
  authors' reading of `MeshAtlasFileReference.DoLoad`. The 5554 `DoLoad` registers every mesh name
  with no such skip. A `<Mesh Id="_x">` that points at such a mesh in the same set would therefore be
  a false V6 error, and an underscore-named collision would escape V3 (inferred from the decomp, not
  tested).[^ksa-mesh-atlas]
- **Alpha channel.** 5554 `PbrMaterialReference` also has `<Alpha>` (`AlphaMap`). `MaterialChannels`
  enumerates only Diffuse, Normal, AoRoughMetal, Emissive and ThinFilm, so V15 does not count alpha
  maps toward the bindless budget. V11 still checks their `Path`.[^ksa-pbr-material]
- **V8 is a deny list.** `AssetBundle` accepts many more top-level kinds (`Vehicle`, celestial bodies,
  `StaticObject`, `Character*`, `Sound*`, `ParticleEmitter`, `GltfFile`, `KeyframeAnimation`,
  `GaugeCanvas`, ...). They pass V8, `OnDataLoad` registers them, and the loader neither snapshots nor
  purges them (inferred from the rule and the tracked registries, not tested).
- **Pasted XML cannot bring files.** The paste flow writes only XML. During its validation the
  folder does not exist yet, so V11 existence and V6 atlas inspection degrade to warnings. Any
  `Path=` in pasted XML then fails at load. Pasted parts should reference meshes and textures that
  are already registered.
- **V7 after boot.** 5554 `RegisterTag` still inserts a late tag into `_editorTagLookup` and only
  skips the category list, so a tag some other code registered after boot counts as known.
- The authors' upgrade note: if KSA ever null-guards the three channel reads, V9 becomes an
  unnecessary restriction. Re-check `AddDraw` and `WriteInstancesToGpu` on each update.[^scope-part-editor]
- **Test coverage.** `ksa-upgrade.tests` compiles the production `BundleValidatorRulesSchema.cs` and
  `BundleValidationIssue.cs` against small fixtures. It exercises only V8: all nine rejected kinds
  with element ids and line numbers (`Explosion` line 9, `ExplosionVolume` line 10), and nested
  references allowed. It runs no other rule and loads no game DLL (the supplied DLL is Windows x64,
  the runner macOS ARM64).[^upgrade-tests-checks][^upgrade-tests-harness][^upgrade-tests-csproj][^upgrade-tests-readme]

## See also

- [Runtime part loading state machine](./parts-now-runtime-part-loading.md) (the `Validate` state and reload purge)
- [Paste XML into a new mod folder](./parts-now-paste-xml-mod-writer.md) (the other caller)
- [Reflection self-test](./parts-now-reflection-self-test.md) (registry lookups and known editor tags)
- [Thumbnail generation](./parts-now-thumbnail-generation.md) (the first code that would hit a V9 crash)
- [Runtime unload and purge](./parts-now-runtime-unload-purge.md) (what V8 protects)
- [Parts Now sub-mod](../submods/parts-now.md)
- [Mod overview](../overview.md)
- [Bake diffuse, normal, ORM and emissive maps from Blender and dilate the atlas](../../../howto/assets/models-and-textures/declare-gltf-and-pbr-material-assets/declare-gltf-and-pbr-material-assets-2.md) - how-to article demonstrating this feature
- [Enumerate the game's loaded meshes, textures and glTFs for pickers and id checks](../../../howto/assets/models-and-textures/import-glb-models-at-runtime/import-glb-models-at-runtime-3.md) - how-to article demonstrating this feature
- [Parse the GLB JSON chunk in C# to validate mesh references](../../../howto/assets/validation/find-z-fighting-and-degenerate-uvs-in-glb/find-z-fighting-and-degenerate-uvs-in-glb-3.md) - how-to article demonstrating this feature
- [Parse an AssetBundle with the game serializer, without OnDataLoad, and apply pure rules](../../../howto/assets/validation/validate-asset-xml-references/validate-asset-xml-references-3.md) - how-to article demonstrating this feature
- [Validate part XML against the game's silent failure modes before loading it](../../../howto/vehicles-and-parts/parts/load-and-unload-part-mods-at-runtime/load-and-unload-part-mods-at-runtime-3.md) - how-to article demonstrating this feature

[^parts-now-readme]: parts-now.lib/README.md (authors' design notes)
[^ksa-xml-loader]: KSA.XmlLoader (decompiled, build 2026.10.10.5554)
[^ksa-xml-helper]: KSA.XmlHelper (decompiled, build 2026.10.10.5554)
[^bundle-parser]: parts-now.lib/Runtime/BundleParser.cs
[^validator-context]: parts-now.lib/Runtime/BundleValidatorContext.cs
[^glb-mesh-names]: parts-now.lib/Runtime/GlbMeshNames.cs
[^bundle-validator]: parts-now.lib/Runtime/BundleValidator.cs
[^ksa-asset-bundle]: KSA.AssetBundle (decompiled, build 2026.10.10.5554)
[^ksa-serialized-id]: KSA.SerializedId (decompiled, build 2026.10.10.5554)
[^ksa-part-template]: KSA.PartTemplate (decompiled, build 2026.10.10.5554)
[^ksa-file-reference]: KSA.FileReference (decompiled, build 2026.10.10.5554)
[^ksa-part-instance]: KSA.PartInstance (decompiled, build 2026.10.10.5554)
[^ksa-vehicle-editor]: KSA.VehicleEditor (decompiled, build 2026.10.10.5554)
[^ksa-part-game-data]: KSA.PartGameDataReference (decompiled, build 2026.10.10.5554)
[^ksa-bindless-textures]: RenderCore.Systems.BindlessTextureLibrary (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-thumbnail-render-resources]: KSA.Rendering.Thumbnails.ThumbnailRenderResources (decompiled, build 2026.10.10.5554)
[^ksa-part-model]: KSA.PartModel (decompiled, build 2026.10.10.5554)
[^ksa-pbr-material]: KSA.PbrMaterialReference (decompiled, build 2026.10.10.5554)
[^rules-schema]: parts-now.lib/Runtime/BundleValidatorRulesSchema.cs
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-explosion-volume]: KSA.ExplosionVolumeTemplate (decompiled, build 2026.10.10.5554)
[^validation-issue]: parts-now.lib/Runtime/BundleValidationIssue.cs
[^issue-view]: parts-now.lib/Ui/ValidationIssueView.cs
[^results-panel]: parts-now.lib/Ui/ResultsPanel.cs
[^paste-panel-actions]: parts-now.lib/Ui/PastePanelActions.cs
[^loader-states]: parts-now.lib/Runtime/RuntimeModLoaderStates.cs
[^rules-references]: parts-now.lib/Runtime/BundleValidatorRulesReferences.cs
[^ksa-mesh-atlas]: KSA.MeshAtlasFileReference (decompiled, build 2026.10.10.5554)
[^scope-part-editor]: scope/part-editor-and-robotics.md (authors' integration map)
[^upgrade-tests-checks]: ksa-upgrade.tests/BundleValidatorChecks.cs
[^upgrade-tests-harness]: ksa-upgrade.tests/BundleValidatorHarness.cs
[^upgrade-tests-csproj]: ksa-upgrade.tests/ksa-upgrade.tests.csproj
[^upgrade-tests-readme]: ksa-upgrade.tests/README.md
