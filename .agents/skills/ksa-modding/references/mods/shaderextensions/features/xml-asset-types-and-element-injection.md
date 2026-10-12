---
type: Mod Feature
title: Custom XML asset types and injected child elements
description: Registers the four asset types through KittenExtensions attributes and runs its own copy of KittenExtensions' XmlAttributeOverrides injector in a Program.Main prefix to add buffer, push constant and pass-reference child elements before any game XML is parsed
mod: shaderextensions
category: assets
techniques: [xml-asset, harmony-prefix, reflection, unsafe-accessor, inter-mod-api]
hooks:
  - harmony-prefix KSA.Program.Main
  - kittenextensions KxAsset
  - reflection KSA.GameSaves.<UniverseSerializer>k__BackingField
  - reflection KSA.VehicleSaves.<VehicleSerializer>k__BackingField
lifecycle: [startup:starmap-before-main, startup:asset-load]
game_types:
  - KSA.XmlHelper
  - KSA.AssetBundle
  - KSA.GameSaves
  - KSA.VehicleSaves
  - KSA.UniverseData
  - KSA.VehicleSaveData
  - KSA.Program
tags: [xml, assets, xmlserializer, kittenextensions, harmony, unsafe-accessor]
---

All four ShaderExtensions asset types are plain XML elements inside a mod's `<Assets>` file, and
each of them accepts child elements that the game has never heard of.[^sx-shaderex] [^sx-imguishaderref] [^sx-preasset] [^sx-postasset]
Two mechanisms make that work:

| Mechanism | What it registers | Where |
|---|---|---|
| `[KxAsset("...")]` attributes, consumed by KittenExtensions | `<ShaderEx>`, `<ImGuiShader>`, `<PostProcessingShader>`, `<GlobalPostShader>` as children of `AssetBundle.Assets` | [ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L12), [ShaderExtensions/ImGuiShader/ImGuiShaderReference.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiShaderReference.cs#L8), [ShaderExtensions/PostProcessing/PreImgui/PostProcessingShaderAsset.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PreImgui/PostProcessingShaderAsset.cs#L8), [ShaderExtensions/PostProcessing/PostImgui/GlobalPostShaderAsset.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostImgui/GlobalPostShaderAsset.cs#L8) |
| ShaderExtensions' own `AssetEx` (a copy of KittenExtensions' injector) | `<PostProcessingInputReference>`, `<PostProcessingOutputReference>` and every consumer buffer or push constant element, as children of `ShaderEx.XmlBindings` (and of `AssetBundle.Assets` for consumer elements) | [ShaderExtensions/AssetEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/AssetEx.cs#L17-L47) |


## How it works

1. **Asset types via KittenExtensions.** KittenExtensions' `Program.Main` prefix scans all load
   contexts for classes carrying an attribute named `KittenExtensions.KxAssetAttribute` and adds each
   one as an `XmlElement` choice on `AssetBundle.Assets`
   ([KittenExtensions/Patches.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patches.cs#L13-L18),
   [KittenExtensions/AssetEx.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/AssetEx.cs#L69-L78)). ShaderExtensions declares
   that attribute class itself, internally
   ([ShaderExtensions/Attributes.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Attributes.cs#L8-L15)).[^kx-patches] [^kx-assetex] [^sx-attributes]
2. **Child elements via ShaderExtensions' injector.** `AssetEx.Init` queues injections with
   `AddExtension(parentType, memberName, childType, elementName)`. `XmlExtension.Add` checks that the
   member exists, that it is a `List<T>` (or field) the child type fits, that it is not
   `[XmlIgnore]` or `[XmlAttribute]`, and that the element name is not already mapped
   ([ShaderExtensions/AssetEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/AssetEx.cs#L128-L248)).[^sx-assetex]
3. **Build the overrides.** For each member, `BuildOverrides` creates an `XmlAttributes` holding the
   member's existing `[XmlElement]` attributes plus the injected ones and adds it to the game's shared
   `XmlHelper.AttributeOverrides`. It keys the override on `member.DeclaringType`, which is what
   `XmlSerializer` uses when it looks up member overrides, so injecting into
   `PostProcessingShaderAsset.XmlBindings` really extends `ShaderEx.XmlBindings` (KittenExtensions keys
   on the extended type instead). If the member already has an override, typically
   `AssetBundle.Assets` from KittenExtensions, `Add` throws and the `catch` appends to the existing
   `XmlAttributes`, skipping duplicates
   ([ShaderExtensions/AssetEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/AssetEx.cs#L142-L184),
   [KittenExtensions/AssetEx.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/AssetEx.cs#L158-L174)).
4. **Rebuild every serializer.** It copies the keys of `XmlHelper.Serializers`, clears the dictionary
   and creates a new `XmlSerializer(type, overrides)` for each, then replaces the two save serializers
   held in static auto-property backing fields with `[UnsafeAccessor(UnsafeAccessorKind.StaticField)]`
   and `[UnsafeAccessorType("KSA.GameSaves, KSA")]`, no reflection lookup needed
   ([ShaderExtensions/AssetEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/AssetEx.cs#L29-L46),
   [current/decomp/KSA/XmlHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlHelper.cs#L13-L32),
   [current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L126),
   [current/decomp/KSA/VehicleSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleSaves.cs#L169)).[^ksa-xmlhelper] [^ksa-gamesaves] [^ksa-vehiclesaves]
5. **Nested `OnDataLoad`.** The game calls `OnDataLoad` only on top-level assets
   ([current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L84-L109)), so each container
   forwards it: `ShaderEx` to its bindings, `ImGuiShaderReference` to its `Vertex` and `Fragment`, the
   post-processing assets to their pass references
   ([ShaderExtensions/ImGuiShader/ImGuiShaderReference.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiShaderReference.cs#L20-L29)).[^ksa-assetbundle]

## Lifecycle and timing

- **Hook: Harmony prefix on `Program.Main` (`startup:starmap-before-main`).** Both mods install
  their prefix from `[StarMapBeforeMain]`
  ([ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L20-L25),
  [KittenExtensions/Mod.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Mod.cs#L8-L22)). The prefix runs after every mod
  assembly is loaded and before the `Program` constructor calls `ModLibrary.PrepareAll` and
  `LoadAll` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L899-L942)).[^sx-patches] [^kx-mod] [^ksa-program]
- **Order between the two prefixes.** StarMap defers a mod until its required dependencies are
  initialized and runs `[StarMapBeforeMain]` inside `InitializeMod`, so KittenExtensions Continued
  patches first
  ([StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L78-L106),
  [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L129-L161),
  [StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L61-L103)). Harmony runs
  equal-priority prefixes in registration order, so KittenExtensions' overrides exist when
  ShaderExtensions' run, which is the case its `catch` branch handles.[^sm-runtimemod] [^sm-modloader]
- **Why so early.** The compatibility plan records that `[StarMapAllModsLoaded]` fires after the mod
  library load and "may be too late for XML serializer overrides", and keeps the `Program.Main`
  prefix on purpose.[^sx-compatplan]

## Key code

| Path | Role |
|---|---|
| [ShaderExtensions/AssetEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/AssetEx.cs#L17-L47) | `Init`: queue, scan, build overrides, rebuild serializers, swap save serializers |
| [ShaderExtensions/AssetEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/AssetEx.cs#L137-L251) | `XmlExtension`: validation and override construction |
| [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L20-L25) | `Program.Main` prefix |
| [ShaderExtensions/Attributes.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Attributes.cs#L8-L15) | Local copies of the KittenExtensions attributes |

## Snippets

```csharp
// ShaderExtensions/AssetEx.cs L17-L47
        public static void Init()
        {
            AddExtension(typeof(PostProcessingShaderAsset), nameof(ShaderEx.XmlBindings), typeof(PostProcessingInputReference), "PostProcessingInputReference");
            AddExtension(typeof(PostProcessingShaderAsset), nameof(ShaderEx.XmlBindings), typeof(PostProcessingOutputReference), "PostProcessingOutputReference");
            AddExtension(typeof(GlobalPostShaderAsset), nameof(ShaderEx.XmlBindings), typeof(PostProcessingInputReference), "PostProcessingInputReference");
            AddExtension(typeof(GlobalPostShaderAsset), nameof(ShaderEx.XmlBindings), typeof(PostProcessingOutputReference), "PostProcessingOutputReference");

            foreach (var alc in AssemblyLoadContext.All)
                foreach (var asm in alc.Assemblies)
                    if (asm.GetType(typeof(SxUniformBufferAttribute).FullName) != null)
                        RegisterAll(asm);

            var overrides = XmlHelper.AttributeOverrides;
            foreach (var ext in xmlExtensions.Values)
                ext.BuildOverrides(overrides);

            var existing = new List<Type>(XmlHelper.Serializers.Keys);
            XmlHelper.Serializers.Clear();
            foreach (var type in existing)
                XmlHelper.Serializers.Add(type, new(type, overrides));

            [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "<UniverseSerializer>k__BackingField")]
            extern static ref XmlSerializer UniverseSerializer(
                [UnsafeAccessorType("KSA.GameSaves, KSA")] object instance = null);
            [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "<VehicleSerializer>k__BackingField")]
            extern static ref XmlSerializer VehicleSerializer(
                [UnsafeAccessorType("KSA.VehicleSaves, KSA")] object instance = null);

            UniverseSerializer() = GetSerializer<UniverseData>();
            VehicleSerializer() = GetSerializer<VehicleSaveData>();
        }
```

```csharp
// ShaderExtensions/AssetEx.cs L142-L173
        public void BuildOverrides(XmlAttributeOverrides overrides)
        {
            foreach (var group in injections.GroupBy(inj => inj.Member))
            {
                var member = group.Key;
                var overrideType = member.DeclaringType ?? type;
                var attrs = new XmlAttributes();
                foreach (var attr in member.GetCustomAttributes())
                {
                    if (attr is XmlElementAttribute elAttr)
                        AddXmlElement(attrs, elAttr);
                }
                foreach (var inj in group)
                    AddXmlElement(attrs, new(inj.XmlElement, inj.ChildType));

                try
                {
                    overrides.Add(overrideType, member.Name, attrs);
                }
                catch
                {
                    XmlAttributes attrs2 = overrides[overrideType, member.Name];
                    foreach (var attr in member.GetCustomAttributes())
                    {
                        if (attr is XmlElementAttribute elAttr)
                            AddXmlElement(attrs2, elAttr);
                    }
                    foreach (var inj in group)
                        AddXmlElement(attrs2, new(inj.XmlElement, inj.ChildType));
                }
            }
        }
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `XmlHelper.AttributeOverrides`, `XmlHelper.Serializers` | [current/decomp/KSA/XmlHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlHelper.cs#L13-L32) | Shared override set and serializer cache for all game XML |
| `AssetBundle.Assets` | [current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L77) | `<Assets>` child list extended with new element types |
| `GameSaves.UniverseSerializer` | [current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L126) | Save serializer rebuilt with the overrides |
| `VehicleSaves.VehicleSerializer` | [current/decomp/KSA/VehicleSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleSaves.cs#L169) | Vehicle serializer rebuilt with the overrides |
| `Program.Main` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1693-L1703) | Prefix target |

## Pitfalls and version notes

- **Order-dependent with KittenExtensions.** KittenExtensions' `BuildOverrides` calls
  `overrides.Add` with no `catch`; it works only because its prefix runs first. A third mod with its
  own copy of the injector and no `catch` would break whichever runs second.
- **Serializers are rebuilt twice** (once per prefix). `XmlSerializer` construction with overrides
  generates code, so each mod doing this adds startup time.
- **Duplicated code drifts.** `XmlExtension` is a modified copy of KittenExtensions' class living in
  the `ShaderExtensions` namespace; fixes in one do not reach the other.
- **Pass references are legal on every `ShaderEx`.** Because the override is keyed on the declaring
  type, `<PostProcessingInputReference>` parses inside plain `<ShaderEx>` and ImGui fragments too,
  where nothing resolves it and `WriteDescriptors` throws "has not been resolved".

## See also

- [Attribute duck typing as an inter-mod API](duck-typed-attribute-api.md)
- [ShaderEx asset with extra descriptor bindings](shaderex-extra-bindings.md)
- [Cross-pass image references](cross-pass-image-references.md)
- [mod.toml generated by MSBuild with a StarMap dependency](generated-mod-toml.md)
- [Ship shader asset elements through KxAsset and a copied injector](../../../howto/assets/asset-xml/register-custom-xml-asset-type/register-custom-xml-asset-type-2.md) - how-to article demonstrating this feature
- [Copied injector in a consumer mod with the same accessor](../../../howto/patching/internals/call-private-game-members-through-reflection-and-delegates/call-private-game-members-through-reflection-and-delegates-7.md) - how-to article demonstrating this feature

[^sx-shaderex]: ShaderExtensions/ShaderEx.cs
[^sx-imguishaderref]: ShaderExtensions/ImGuiShader/ImGuiShaderReference.cs
[^sx-preasset]: ShaderExtensions/PostProcessing/PreImgui/PostProcessingShaderAsset.cs
[^sx-postasset]: ShaderExtensions/PostProcessing/PostImgui/GlobalPostShaderAsset.cs
[^kx-patches]: KittenExtensions/Patches.cs
[^kx-assetex]: KittenExtensions/AssetEx.cs
[^sx-attributes]: ShaderExtensions/Attributes.cs
[^sx-assetex]: ShaderExtensions/AssetEx.cs
[^ksa-xmlhelper]: KSA.XmlHelper (decompiled, build 2026.10.10.5554)
[^ksa-gamesaves]: KSA.GameSaves (decompiled, build 2026.10.10.5554)
[^ksa-vehiclesaves]: KSA.VehicleSaves (decompiled, build 2026.10.10.5554)
[^ksa-assetbundle]: KSA.AssetBundle (decompiled, build 2026.10.10.5554)
[^sx-patches]: ShaderExtensions/Patches.cs
[^kx-mod]: KittenExtensions/Mod.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^sm-runtimemod]: StarMap.Core/ModRepository/RuntimeMod.cs
[^sm-modloader]: StarMap.Core/ModRepository/ModLoader.cs
[^sx-compatplan]: ShaderExtensions/KSA-2026.9.7-compatibility-plan.md
