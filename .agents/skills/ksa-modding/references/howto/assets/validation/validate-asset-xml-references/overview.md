---
type: Game Feature
title: Validate asset XML ids and paths before launching the game (offline)
description: Why bad asset XML fails silently in KSA and three levels of validator, from a well-formedness and reference script to extended generated-geometry checks to a game-serializer parse for runtime loaders
category: assets
group: validation
topic: validate-asset-xml-references
demonstrated_by:
  - mods/ksacars/features/art-and-audio-pipeline-tooling
  - mods/ksagolf/features/glb-authoring-and-mesh-checks
  - mods/ksarmory/features/blender-model-pipeline-and-checks
  - mods/ksastructures/features/asset-and-mesh-validation-tools
  - mods/unscience/features/parts-now-bundle-validation
lifecycle: [startup:asset-load, frame:draw-ui]
game_types:
  - KSA.AssetBundle
  - KSA.MeshAtlasFileReference
  - KSA.ModLibrary
  - KSA.XmlHelper
  - KSA.XmlLoader
  - KSA.SerializedId
  - KSA.PartTemplate
tags: [validation, xml, ids, paths, offline]
---

KSA reads asset XML quietly. A missing file returns null without a log, a malformed file logs the exception and the assets simply do not exist, a mistyped path logs one line, a duplicate id is dropped, an unknown mesh or part id throws the first time the part is built, and a part or static-object material missing one of its Diffuse, Normal or AoRoughMetal channels takes the game down (those render paths read the handles without a null check; code-side material lookups fall back to default textures). None of it needs to be found in game: the XML can be
cross-checked against the mod's own files and Core's ids with a script.

## Engine background

- `XmlLoader.Load<T>` (deserialize, then `OnDataLoad`), `TryDeserialize` (logs and returns null), `Deserialize<T>` (null for a missing file; plain `XmlSerializer` when none is cached) ([current/decomp/KSA/XmlLoader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlLoader.cs#L1-L52)).
- `Mod.LoadAssetBundles` reads only the files in `mod.toml` `assets` ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L157-L169)).
- `FileReference.Load`: registration, error logs for missing files ([current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L67-L146)); `SerializedCollection.Register` first-wins ([current/decomp/KSA/SerializedCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedCollection.cs#L20-L33)); `ModLibrary.Get<T>` throws, `TryGet<T>` does not ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L776-L800), [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1059-L1085)).
- `XmlHelper.Serializers` / `AttributeOverrides` for the real `AssetBundle` serializer ([current/decomp/KSA/XmlHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlHelper.cs#L17-L47)).
- Phase: `startup:asset-load` is where the failures start; validation runs at development time or before a runtime load. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Check asset XML ids and paths offline with check-xml.sh and validate-parts.py](validate-asset-xml-references-1.md) | KSACars (and KSAGolf) | `check-xml.sh` and `validate-parts.py` with an `--offline` mode, in deploy and CI | `startup:asset-load` | Cheap and portable; references and paths only |
| [Extend the validator for generated geometry, ids named in C# and shared planes](validate-asset-xml-references-2.md) | KSArmory, KSAStructures | Extra checks: manifest, duplicate ids, shared textures, ids in C#, generated geometry, planes, connectors | `startup:asset-load` | Encodes real incidents; more code to maintain |
| [Parse an AssetBundle with the game serializer, without OnDataLoad, and apply pure rules](validate-asset-xml-references-3.md) | unscience (Parts Now) | `XmlHelper.Serializers` parse without `OnDataLoad`, fifteen pure rules over the object tree | `frame:draw-ui`, `startup:asset-load` | Safe runtime loading of untrusted XML; depends on game internals |

## See also

- [Check GLB meshes offline: z-fighting, degenerate UVs, names and bounds](../find-z-fighting-and-degenerate-uvs-in-glb/overview.md), [Resolve asset ids and file paths correctly across mods](../../asset-xml/resolve-asset-ids-and-mod-relative-paths/overview.md), [Register a custom XML asset type or inject new child elements into game XML](../../asset-xml/register-custom-xml-asset-type/overview.md), [Know which asset XML elements the game accepts and what they load](../../asset-xml/asset-xml-element-reference/overview.md),
  [Add a part's art with its own GLB model and PBR textures](../../models-and-textures/add-a-part-model-with-glb-and-pbr-textures/overview.md), [Define a part with its own game data and GLB model](../../../vehicles-and-parts/parts/define-a-part-with-game-data/overview.md), [Load, validate and unload part mods in a running game](../../../vehicles-and-parts/parts/load-and-unload-part-mods-at-runtime/overview.md), [Run one check script from the pre-push hook and from CI](../../../testing-and-tooling/offline-tests/run-repo-checks-from-a-pre-push-hook-and-ci/overview.md),
  [Generate measured atmosphere tables from reference profiles](../../../celestials/atmospheres-and-rings/generate-measured-atmosphere-tables-offline/overview.md), [Branch per body inside shared game shaders](../../../shaders/stock-shaders/branch-per-body-inside-shared-game-shaders/overview.md), [Compile-check a mod's GLSL without launching the game](../../../shaders/glsl-workflow/check-mod-shaders-offline-with-glslang/overview.md),
  [Define atmospheres, scattering and cloud layers for a body in system XML](../../../celestials/atmospheres-and-rings/define-atmospheres-and-cloud-layers-in-system-xml/overview.md), [Take clean screenshots from code, with a composed camera shot held for the capture](../../../testing-and-tooling/in-game-testing/take-screenshots-from-code/overview.md), [Extend KSA's sky and cloud shaders: per-pixel haze and cloud lighting fixes](../../../shaders/stock-shaders/extend-stock-sky-and-cloud-shaders/overview.md)
- [Art and audio pipeline tooling](../../../../mods/ksacars/features/art-and-audio-pipeline-tooling.md), [Headless Blender model generation and offline GLB checks](../../../../mods/ksarmory/features/blender-model-pipeline-and-checks.md), [Asset XML, mesh and shader checks that run without the game](../../../../mods/ksastructures/features/asset-and-mesh-validation-tools.md),
  [Validate Part asset bundles with fifteen rules before anything is written or registered](../../../../mods/unscience/features/parts-now-bundle-validation.md)
