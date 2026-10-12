---
type: How-To
title: Validate part XML against the game's silent failure modes before loading it
description: Parse bundles with KSA's own XmlSerializer without side effects, then run fifteen pure rules over the parsed set so a bad id, path or material never reaches the registries
category: vehicles-and-parts
group: parts
topic: load-and-unload-part-mods-at-runtime
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/parts-now-bundle-validation]
techniques: [xml-serializer, reflection]
hooks: [starmap StarMapBeforeGui, XmlHelper.Serializers]
lifecycle: [frame:draw-ui, startup:all-mods-loaded]
game_types:
  - KSA.AssetBundle
  - KSA.XmlHelper
  - KSA.PartTemplate
  - KSA.PbrMaterialReference
  - KSA.ModLibrary
  - KSA.SerializedCollection
  - KSA.MeshAtlasFileReference
tags: [validation, xml, glb, materials, ids, runtime-loading]
---
Outcome: before anything is written, registered or bound, every submitted document set passes a list of rules, and
any error stops the load with a message naming the rule, the element id and usually the line. KSA handles bad asset
XML quietly: a duplicate id is dropped, a missing file is only logged, an unknown sub-part or mesh id throws the first
time the part is drawn, and a material missing one texture channel takes the whole game down. That is survivable at
boot (edit and restart) and not for a running game.

Prerequisite: StarMap; the same ideas work as a build-time script (KSArmory's and KSAStructures' `validate-parts.py`).
Demonstrated by [unscience/parts-now-bundle-validation](../../../../mods/unscience/features/parts-now-bundle-validation.md).

## Background: how the game does it

- `XmlLoader.Load<T>` deserialises through `XmlHelper.Serializers[typeof(T)]` and then calls `OnDataLoad`, which
  registers everything; a parse failure is only logged [current/decomp/KSA/XmlLoader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlLoader.cs#L5-L33)[^ksa-xmlloader].
- The serializer instance for `AssetBundle` is built with `XmlAttributeOverrides` that map every
  `ModuleBase.TemplateDataBase` subtype (`<PartModel>`, `<Tank>`, ...) onto `PartTemplate.Components`
  [current/decomp/KSA/XmlHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlHelper.cs#L12-L45)[^ksa-xmlhelper], so a hand-built `new XmlSerializer(typeof(AssetBundle))` silently drops every
  component. Reuse KSA's.
- Before `OnDataLoad`, `SerializedId.Hash` is default, `IsReferenceable` is false and `PartTemplate.EditorTags` is
  empty: rules must work from `Id` strings, `EditorTagsStrings` and `KeyHash.Make(id)`.
- `Register` returns false for an existing key and the declaration silently becomes a reference
  [current/decomp/KSA/SerializedCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedCollection.cs#L20-L35)[^ksa-serializedcollection] [current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L67-L73)[^ksa-filereference]; `PartInstance.GetTemplate()` is
  `ModLibrary.Get<PartTemplate>`, which throws on a miss [current/decomp/KSA/PartInstance.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartInstance.cs#L113-L116)[^ksa-partinstance].
- A mesh atlas registers one mesh Id per glTF mesh name [current/decomp/KSA/MeshAtlasFileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshAtlasFileReference.cs#L21-L45)[^ksa-meshatlasfilereference], so mesh ids come
  from the GLB's JSON chunk without loading geometry.
- Part-drawing code reads a material's `Diffuse`, `Normal` and `AoRoughMetal` bindless handles with no null check; a
  missing channel throws on the first thumbnail render or the first visible frame (per Parts Now's analysis of
  `PartModel.WriteInstancesToGpu` and the thumbnail renderer).
- `PbrMaterialReference.OnDataLoad` treats a material declaring none of the three channels as a *reference*.

## Lifecycle and timing

Validation runs on the game thread, bounded and synchronous (file reads and object walks), in one frame:

| Caller | Hook | Mod folder passed | Effect of an error |
|---|---|---|---|
| paste panel **Validate** | `StarMapAfterGui` | the target folder (does not exist yet) | install stays disabled; nothing written |
| loader `Validate` state | `StarMapBeforeGui` (or the hidden-HUD replay) | the target or existing folder | the job fails: "nothing was written, registered or bound" |

It is the first state, so a reload purges its previous load only after the rules pass. Every live-registry read
happens on the main thread while no loader worker runs (see article 1). It touches no simulated state, so running
after `ExecuteNext*Solvers` is harmless ([game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)).

## Steps

1. **Parse without side effects**: `XDocument.Parse(..., LoadOptions.SetLineInfo)` for line numbers, KSA's serializer
   for the object graph, and never call `OnDataLoad`:

```csharp
// parts-now.lib/Runtime/BundleParser.cs L85-117
            return false;
        }

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
    }
```

2. **Build one context** per run with case-insensitive indexes of declared parts and sub-parts (one id space, as in
   `ModLibrary.AllParts`), game data, materials, file ids and mesh ids. Read atlas mesh names straight from the GLB
   JSON chunk:

```csharp
// parts-now.lib/Runtime/GlbMeshNames.cs L47-78
    public static List<string> Read(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        byte[] json = IsBinaryContainer(filePath) ? ReadJsonChunk(filePath) : File.ReadAllBytes(filePath);

        List<string> names = new List<string>();
        using JsonDocument document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("meshes", out JsonElement meshes)
            || meshes.ValueKind != JsonValueKind.Array)
        {
            return names;
        }

        foreach (JsonElement mesh in meshes.EnumerateArray())
        {
            if (!mesh.TryGetProperty("name", out JsonElement name)
                || name.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? value = name.GetString();
            if (!string.IsNullOrEmpty(value) && !value.StartsWith('_'))
            {
                names.Add(value);
            }
        }

        return names;
    }
```

3. **Run the rules in isolation**, each in a `try/catch` that turns a thrown rule into a Warning so the others still
   report, and validate all documents as one set because they cross-reference:

```csharp
// parts-now.lib/Runtime/BundleValidator.cs L68-94
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

4. **Write the rules that matter most for parts** (the table below), starting with the crash guard: every model
   component needs a `<Material>` declaring all three of `<Diffuse>`, `<Normal>`, `<AoRoughMetal>`, resolving a
   pure-reference material against the set and then the live registry:

```csharp
// parts-now.lib/Runtime/BundleValidatorRulesSchema.cs L86-107
    private static void RuleV9MaterialChannels(ValidationContext context)
    {
        foreach (ParsedBundle bundle in context.Bundles)
        {
            foreach (PartTemplate template in BundleParser.AllPartTemplates(bundle))
            {
                foreach (BundleParser.ModelComponent model in BundleParser.ModelComponents(template))
                {
                    if (model.Material is null)
                    {
                        AddError(context, "V9", bundle.SourceName, template.Id,
                            "<" + model.ElementName + " Id=\"" + model.Id + "\"> in '" + template.Id
                            + "' has no <Material>. KSA dereferences the material's texture handles "
                            + "without a null check and would crash the game when the part is drawn.");
                        continue;
                    }

                    CheckMaterialChannels(context, bundle, template, model, model.Material);
                }
            }
        }
    }
```

```csharp
// parts-now.lib/Runtime/BundleValidatorRulesSchema.cs L232-267
    private static void CheckMaterialChannels(
        ValidationContext context,
        ParsedBundle bundle,
        PartTemplate template,
        BundleParser.ModelComponent model,
        PbrMaterialReference material)
    {
        string origin = "<" + model.ElementName + " Id=\"" + model.Id + "\"> in '" + template.Id + "'";
        PbrMaterialReference definition = material;

        if (IsPureReference(material))
        {
            if (string.IsNullOrWhiteSpace(material.Id))
            {
                AddError(context, "V9", bundle.SourceName, template.Id,
                    origin + " has a <Material> with neither an Id nor any texture channel, so it "
                    + "names no material at all.");
                return;
            }

            if (context.DeclaredMaterials.TryGetValue(material.Id, out PbrMaterialReference? declared))
            {
                definition = declared;
            }
            else
            {
                PbrMaterialReference? registered = GameRegistry.FindMaterial(material.Id);
                if (registered is null)
                {
                    AddError(context, "V9", bundle.SourceName, material.Id,
                        origin + " references material '" + material.Id
                        + "', which is neither declared in this set nor already registered.");
                    return;
                }

                definition = registered;
```

5. **Report findings** as `(Severity, Rule, Message, ElementId, SourceName)` and block the load on any Error;
   offer a "copy issues" button.

| Rule | Severity | What it prevents |
|---|---|---|
| V1 | Error | root element is not `<Assets>` (parse failures are only logged by `XmlLoader`) |
| V2 | Error | missing `Id`: not referenceable, never registered |
| V3 | Error | id already registered: silently dropped, a file never read |
| V4 | Error | id declared twice in the set |
| V5 | Error | `<SubPart InstanceOf>` resolves nowhere: `GetTemplate` throws at spawn or thumbnail time |
| V6 | Error / Warning | `<Mesh Id>` resolves to no mesh the set creates or registered mesh |
| V7 | Error | editor tag unknown: after the editor loaded its tags, `RegisterTag` no longer adds categories |
| V8 | Error | top-level kinds whose registries are startup-only or outside rollback and purge (`<Substance>`, reactions, `<EditorTagDef>`, `<Situation>`, `<Explosion>`...) |
| V9 | Error | missing material or channel: crash at first draw |
| V10 | Error | `<Reaction>`, `<Grain>`, `<VolumetricExhaust>`, `<SoundEvent>` references that cannot be extended at runtime |
| V11 | Error | `Path=` empty, absolute, containing `..`, escaping the mod folder or non-existent |
| V12 | Warning | a `<SubPart>` with no `<MeshView>` has no editor pick geometry |
| V13 | Warning | a top-level `<Part>` with no same-id `<PartGameData>` loads with no mass or connectors |
| V14 | Error | `<PartGameData>` id already registered: merges additively and permanently |
| V15 | Error | new textures overflow the bindless texture pool (no resize) |

## Complete example

- [parts-now.lib/Runtime/BundleParser.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/BundleParser.cs)[^unscience-bundleparser], [parts-now.lib/Runtime/BundleValidator.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/BundleValidator.cs)[^unscience-bundlevalidator]
- [parts-now.lib/Runtime/BundleValidatorRulesSchema.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/BundleValidatorRulesSchema.cs)[^unscience-bundlevalidatorrulesschema], [parts-now.lib/Runtime/BundleValidatorRulesIdentity.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/BundleValidatorRulesIdentity.cs)[^unscience-bundlevalidatorrulesidentity], [parts-now.lib/Runtime/BundleValidatorRulesReferences.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/BundleValidatorRulesReferences.cs)[^unscience-bundlevalidatorrulesreferences]
- [parts-now.lib/Runtime/GlbMeshNames.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/GlbMeshNames.cs)[^unscience-glbmeshnames]

## Variations and gotchas

- **As a build step.** The same rules work offline: KSAStructures' `tools/validate-parts.py` resolves every
  `InstanceOf`, `Material` and mesh Id, checks every path exists with exact case (Linux is case-sensitive), and
  flags a texture file named by two materials.
- **Case.** `File.Exists` on Windows or macOS accepts a wrong-case path that fails on Linux; compare against the
  directory listing.
- **Warning vs Error.** A rule that cannot read its input (an unreadable atlas, an unreadable pool) should degrade to
  a Warning rather than block.
- **Updates.** The rules mirror engine behaviour (null checks that are not there, registries that are not extensible);
  re-verify V8 and V9 when KSA adds asset kinds or changes the model renderers.

## See also

- [Overview](overview.md), [Article 1](load-and-unload-part-mods-at-runtime-1.md), [Article 2](load-and-unload-part-mods-at-runtime-2.md)
- [unscience/parts-now-bundle-validation](../../../../mods/unscience/features/parts-now-bundle-validation.md)
- [Define a part with its own game data and GLB model](../define-a-part-with-game-data/overview.md)

[^ksa-meshatlasfilereference]: current/decomp/KSA/MeshAtlasFileReference.cs (decompiled, build 2026.10.10.5554)
[^unscience-bundlevalidatorrulesidentity]: unscience: parts-now.lib/Runtime/BundleValidatorRulesIdentity.cs
[^ksa-xmlhelper]: current/decomp/KSA/XmlHelper.cs (decompiled, build 2026.10.10.5554)
[^unscience-bundleparser]: unscience: parts-now.lib/Runtime/BundleParser.cs
[^unscience-bundlevalidator]: unscience: parts-now.lib/Runtime/BundleValidator.cs
[^unscience-bundlevalidatorrulesreferences]: unscience: parts-now.lib/Runtime/BundleValidatorRulesReferences.cs
[^unscience-bundlevalidatorrulesschema]: unscience: parts-now.lib/Runtime/BundleValidatorRulesSchema.cs
[^ksa-xmlloader]: current/decomp/KSA/XmlLoader.cs (decompiled, build 2026.10.10.5554)
[^unscience-glbmeshnames]: unscience: parts-now.lib/Runtime/GlbMeshNames.cs
[^ksa-partinstance]: current/decomp/KSA/PartInstance.cs (decompiled, build 2026.10.10.5554)
[^ksa-serializedcollection]: current/decomp/KSA/SerializedCollection.cs (decompiled, build 2026.10.10.5554)
[^ksa-filereference]: current/decomp/KSA/FileReference.cs (decompiled, build 2026.10.10.5554)
