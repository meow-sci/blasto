---
type: Game Feature
title: Register a custom XML asset type or inject new child elements into game XML
description: How KittenExtensions teaches the game's XmlSerializers new elements from attributes in a mod assembly, and three ways mods use it to ship shader assets and data profiles
category: assets
group: asset-xml
topic: register-custom-xml-asset-type
demonstrated_by:
  - mods/kittenextensions/features/custom-xml-asset-types
  - mods/shaderextensions/features/xml-asset-types-and-element-injection
  - mods/mpfx/features/xml-effect-profiles
lifecycle: [startup:starmap-before-main, startup:asset-load, frame:draw-ui]
game_types:
  - KSA.XmlHelper
  - KSA.AssetBundle
  - KSA.SerializedId
  - KSA.ModLibrary
  - KSA.Program
  - KSA.GameSaves
  - KSA.VehicleSaves
  - KSA.UniverseData
  - KSA.VehicleSaveData
tags: [xml, serializer, custom-asset, kxasset, kittenextensions]
---

The game's asset XML is a closed schema: `AssetBundle.Assets` lists the element names it knows and a shared
`XmlAttributeOverrides` extends it for part modules. A mod that wants its own element (a shader asset, a data profile) or a new
child element type under an existing game element has to change those overrides before any XML is read, and rebuild every
cached `XmlSerializer`, because a serializer captures its overrides when it is constructed.

## Engine background

- `XmlHelper`: `Serializers` cache and `AttributeOverrides`, with the static constructor that builds the `AssetBundle`
  serializer ([current/decomp/KSA/XmlHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlHelper.cs#L13-L47)).
- `AssetBundle.Assets` element list and `OnDataLoad` ([current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L9-L109)); `SerializedId.OnDataLoad`
  ([current/decomp/KSA/SerializedId.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedId.cs#L26-L31)).
- `GameSaves.UniverseSerializer` and `VehicleSaves.VehicleSerializer`: get-only statics built in static constructors, so
  they must be replaced through their backing fields ([current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L126-L155), [current/decomp/KSA/VehicleSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleSaves.cs#L169-L198)).
- `Program.Main`, the private entry point where the setup prefix goes ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1693-L1702)), and
  `XmlLoader.Deserialize<T>`, which builds a plain serializer when none is cached ([current/decomp/KSA/XmlLoader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlLoader.cs#L32-L45)).
- Phases: `startup:starmap-before-main` for setup, `startup:asset-load` for use. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Add an XML element with KxAsset and inject child elements with KxAssetInject](register-custom-xml-asset-type-1.md) | KittenExtensions | `KxAsset` and `KxAssetInject` attributes copied into your assembly, discovered by name | `startup:starmap-before-main`, `startup:asset-load` | Least code; requires KX; warnings only on mistakes |
| [Ship shader asset elements through KxAsset and a copied injector](register-custom-xml-asset-type-2.md) | ShaderExtensions | KxAsset-tagged asset classes plus a copied injector for child elements and `UnsafeAccessor` swap of save serializers | `startup:starmap-before-main`, `startup:asset-load` | Full control and append-on-existing; you own the code and its upkeep |
| [Ship a data-only profile type that loads from ordinary asset XML](register-custom-xml-asset-type-3.md) | MPFX | A data-only `SerializedId` profile with public fields, `DefaultValue` trimming and a static registry | `startup:asset-load`, `frame:draw-ui` | Simplest consumer; no persistence, KX required |

## See also

- [Know which asset XML elements the game accepts and what they load](../asset-xml-element-reference/overview.md), [Declare ShaderEx, PostProcessingShader, GlobalPostShader and ImGuiShader assets](../../../shaders/post-processing/declare-post-processing-shader-assets-in-xml/overview.md),
  [Write a fullscreen post-processing fragment shader for KSA](../../../shaders/post-processing/write-a-post-processing-fragment-shader/overview.md), [Choose and use the right StarMap lifecycle hook](../../../getting-started/lifecycle/choose-a-starmap-startup-hook/overview.md), [Declare a dependency on another StarMap mod, required or optional](../../../getting-started/dependencies/declare-a-mod-dependency/overview.md),
  [Reading, writing and calling private game members](../../../patching/internals/call-private-game-members-through-reflection-and-delegates/overview.md), [Patch Core or another mod's XML data without copying files](../../xml-patching/patch-game-xml-data/overview.md)
- [Custom XML asset types via KxAsset attributes](../../../../mods/kittenextensions/features/custom-xml-asset-types.md), [Custom XML asset types and injected child elements](../../../../mods/shaderextensions/features/xml-asset-types-and-element-injection.md),
  [Effect profiles as XML assets with clipboard export](../../../../mods/mpfx/features/xml-effect-profiles.md)
