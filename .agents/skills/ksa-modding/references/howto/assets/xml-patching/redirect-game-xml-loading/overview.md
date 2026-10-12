---
type: Game Feature
title: Redirect the game to patched copies of its XML and coordinate edits between mods
description: Two ways to substitute the XML the game reads without editing the install, how to edit the copies safely, and how two mods share one patched copy
category: assets
group: xml-patching
topic: redirect-game-xml-loading
demonstrated_by:
  - mods/realatmospheres/features/core-xml-entry-redirect
  - mods/realatmospheres/features/atmosphere-optics-in-system-xml
  - mods/kittenextensions/features/xmlloader-call-site-redirect
  - mods/realatmospheres/features/shadow-tree-anchor-patching
  - mods/ksastructures/features/generated-deep-ramp
  - mods/realatmospheres/features/cross-mod-shared-shadow
lifecycle: [startup:starmap-before-main, startup:immediate-load, startup:asset-load]
game_types:
  - KSA.Mod
  - KSA.ModLibrary
  - KSA.XmlLoader
  - KSA.SystemInfo
  - KSA.AssetBundle
  - KSA.SystemTemplate
  - KSA.AtmosphericBodyTemplate
  - KSA.SerializedCollection
  - KSA.AstronomicalTemplate
  - KSA.PlanetaryBodyTemplate
tags: [xml, redirect, harmony, shadow-copy, core, transpiler]
---

Core's bodies, systems and assets are plain XML files named in Core's `mod.toml`. A mod that must change them in
ways declarative patches cannot express (promoting a body type, rewriting hundreds of values, deriving values
from a table) can make the game read a different file. This topic covers the two ways to do that, the editing
techniques that keep a copy robust against game updates, and the coordination needed when two mods want the same
file. Declarative patches ([Patch Core or another mod's XML data without copying files](../patch-game-xml-data/overview.md)) are simpler when they are enough.

## Engine background

- `KSA.Mod` holds the file lists: a private `string[] Assets` and a public `string[] SystemTemplates`
  ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L56-L72)). Each loader (`LoadAssetBundles`, `LoadEditorTagDefinitions`, `LoadSystems`,
  `PrepareSystems`, `LoadPlanetMeshes`, `LoadPlanetRandomHeightmaps`) builds `Path.Combine(DirectoryPath, entry)` and
  passes it to the generic `XmlLoader.Load<T>` ([current/decomp/KSA/XmlLoader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlLoader.cs#L1-L52)).
- `Path.Combine` returns a rooted second argument unchanged, so an absolute path in the list escapes the mod
  folder. `Mod.GetPath` does the same for asset paths ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L389-L392)).
- `PrepareSystems` stores the entry string as `SystemInfo.XmlName`; `LoadSystems` loads only the entry equal to
  the selected system's `XmlName` ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L128-L137), [current/decomp/KSA/SystemInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SystemInfo.cs#L32-L32)).
- Registration is first-wins: `SerializedCollection.Register` ignores a second item with the same hash
  ([current/decomp/KSA/SerializedCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedCollection.cs#L20-L33)).
- Phases: the swap or transpiler must exist at `startup:starmap-before-main`; the loaders run at
  `startup:immediate-load` (`PrepareSystems`) and `startup:asset-load` ([Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md), [Understand when and in which order KSA loads mod XML](../../asset-xml/mod-xml-load-order/overview.md)).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Swap Core's XML entries for patched copies with prefixes on the Mod loaders](redirect-game-xml-loading-1.md) | Real Atmospheres | Harmony prefixes on the non-generic `Mod` loaders; swap list entries for absolute shadow paths | `startup:starmap-before-main`, `startup:immediate-load`, `startup:asset-load` | No IL, no generic matching; only helps when the mod owns the whole file; one redirect per file |
| [Transpile the Mod loaders so XmlLoader.Load calls read from an in-memory document](redirect-game-xml-loading-2.md) | KittenExtensions | Transpile four `Mod` loaders so `XmlLoader.Load<T>` becomes a loader reading an in-memory document | same | Redirects everything and supports patches; fragile call-shape match; two callers stay stock |
| [Edit shadow copies with anchor-verified text patches and generated XML between markers](redirect-game-xml-loading-3.md) | Real Atmospheres, KSAStructures | Anchor-verified, marker-guarded text edits of copies; generated XML spliced between markers | `startup:starmap-before-main`, `startup:asset-load` | Survives game updates when anchors fail soft; no schema check on payloads |
| [Let two mods edit the same Core file with a shared shadow folder and a named mutex](redirect-game-xml-loading-4.md) | Real Atmospheres | Shared shadow folder, `Global` named mutex and a process-start launch token | `startup:starmap-before-main` | Two mods can edit one file; load order is manifest order, edits must tolerate either order |

The first and second approaches are alternatives and must not be combined: the prefixes swap entries for paths
KittenExtensions never imported (see the gotchas in approach 1).

## See also

- [Patch Core or another mod's XML data without copying files](../patch-game-xml-data/overview.md), [Write XPath patch operations for game XML](../xpath-xml-patch-recipes/overview.md), [Understand when and in which order KSA loads mod XML](../../asset-xml/mod-xml-load-order/overview.md),
  [Resolve asset ids and file paths correctly across mods](../../asset-xml/resolve-asset-ids-and-mod-relative-paths/overview.md)
- [Replace or text-patch the game's GLSL without editing game files](../../../shaders/stock-shaders/replace-or-patch-game-glsl-without-editing-game-files/overview.md), [Define atmospheres, scattering and cloud layers for a body in system XML](../../../celestials/atmospheres-and-rings/define-atmospheres-and-cloud-layers-in-system-xml/overview.md)
- [Core XML entry redirect](../../../../mods/realatmospheres/features/core-xml-entry-redirect.md), [XmlLoader call-site redirect](../../../../mods/kittenextensions/features/xmlloader-call-site-redirect.md),
  [Shared shadow for two cooperating mods](../../../../mods/realatmospheres/features/cross-mod-shared-shadow.md)
- [Share types and design an inter-mod API between StarMap mods](../../../getting-started/dependencies/share-types-between-mods/overview.md) - related topic
- [Declare a dependency on another StarMap mod, required or optional](../../../getting-started/dependencies/declare-a-mod-dependency/overview.md) - related topic
- [Writing a Harmony transpiler for a KSA method](../../../patching/transpilers/write-a-harmony-transpiler/overview.md) - related topic
- [Swapping one call inside a game method with a transpiler](../../../patching/transpilers/swap-one-call-inside-a-game-method-with-a-transpiler/overview.md) - related topic
- [Reading, writing and calling private game members](../../../patching/internals/call-private-game-members-through-reflection-and-delegates/overview.md) - related topic
- [How StarMap launches KSA and loads your mod](../../../getting-started/loader/how-starmap-launches-ksa/overview.md) - related topic

