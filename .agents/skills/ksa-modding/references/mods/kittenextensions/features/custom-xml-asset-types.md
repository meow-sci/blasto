---
type: Mod Feature
title: Custom XML asset types via KxAsset attributes
description: Any loaded assembly that declares its own KittenExtensions.KxAsset or KxAssetInject attribute gets its tagged classes added as new XML element names through XmlAttributeOverrides and a rebuild of every cached game serializer
mod: kittenextensions
category: assets
techniques: [xml-asset, harmony-prefix, reflection, unsafe-accessor, assembly-load-context, inter-mod-api]
hooks:
  - harmony-prefix KSA.Program.Main
  - reflection KSA.GameSaves.<UniverseSerializer>k__BackingField
  - reflection KSA.VehicleSaves.<VehicleSerializer>k__BackingField
lifecycle: [startup:starmap-before-main, startup:asset-load]
game_types:
  - KSA.XmlHelper
  - KSA.AssetBundle
  - KSA.SerializedId
  - KSA.GameSaves
  - KSA.VehicleSaves
  - KSA.UniverseData
  - KSA.VehicleSaveData
  - KSA.Program
  - KSA.PartTemplate
  - KSA.PartInstance
tags:
  - xml
  - xmlserializer
  - xml-attribute-overrides
  - custom-assets
  - unsafe-accessor
  - assembly-load-context
  - inter-mod-api
---

The second half of KittenExtensions is an extension point for code mods: a class in any mod assembly
can become a new XML element that every mod's asset files may use. A dependent declares two tiny
attribute classes in the `KittenExtensions` namespace inside its own assembly, tags its asset classes,
and KittenExtensions teaches the game's `XmlSerializer`s the new element names before any XML is
read. This is how ShaderExtensions adds `<ShaderEx>`, `<PostProcessingShader>` and `<ImGuiShader>`,
and how MPFX adds `<MPFXProfile>` to ordinary `<Assets>` files.[^kx-readme] [^sx-shaderex] [^mpfx-profile]

## How it works

1. **The contract.** Two attributes, identical in KittenExtensions and in each dependent
   ([KittenExtensions/Attributes.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Attributes.cs#L6-L10), [MPFX/Attributes.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Attributes.cs#L9-L16)):
   `KxAsset(string xmlElement)` adds the element to the root `<Assets>` list, and
   `KxAssetInject(Type parent, string member, string xmlElement)` (multiple allowed) adds it to any
   public field or property of `parent`. The README's rule: they must be in namespace
   `KittenExtensions`, and at least one must be defined in the same assembly as the tagged classes
   ([README.md](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/README.md#L268-L283)).[^kx-attributes] [^mpfx-attributes]
2. **Discover by name.** `AssetEx.Init` walks `AssemblyLoadContext.All` and every assembly in each
   context. An assembly qualifies if `asm.GetType("KittenExtensions.KxAssetAttribute")` or the inject
   attribute's full name resolves inside it. Every type's `GetCustomAttributesData()` is then compared
   by `AttributeType.FullName` and constructor arguments are read from `CustomAttributeData`
   ([KittenExtensions/AssetEx.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/AssetEx.cs#L18-L23), [KittenExtensions/AssetEx.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/AssetEx.cs#L54-L150)).
   Because matching is by name, each mod's private `internal` copy works without referencing
   KittenExtensions and without sharing a `Type` across load contexts. `ReflectionTypeLoadException`
   is tolerated so one bad type does not hide the rest.
3. **Validate.** `XmlExtension.Add` finds the public instance property or field and rejects (with a
   `Log.Warning`, never an exception) a missing member, a class that is not assignable to the member
   type or to `T` of a `List<T>` member, a member marked `[XmlIgnore]` or `[XmlAttribute]`, an element
   name the member already maps with `[XmlElement]`, and duplicate injections
   ([KittenExtensions/AssetEx.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/AssetEx.cs#L176-L238)). `KxAsset` is shorthand for an injection into
   `AssetBundle.Assets` ([KittenExtensions/AssetEx.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/AssetEx.cs#L69-L78)).
4. **Build overrides.** For each injected member, `BuildOverrides` creates an `XmlAttributes` that
   re-adds the member's own `[XmlElement]` attributes (overrides replace a member's attributes, so the
   stock mappings must be copied) plus one `XmlElementAttribute(element, childType)` per injection,
   and adds it to the game's shared `XmlHelper.AttributeOverrides`
   ([KittenExtensions/AssetEx.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/AssetEx.cs#L158-L174)). For `AssetBundle.Assets` that is the 65 stock
   mappings from `<Vehicle>` to `<EditorTagDef>` plus the custom ones
   ([current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L9-L77)).[^ksa-assetbundle]
5. **Rebuild cached serializers.** An `XmlSerializer` captures its overrides when it is constructed,
   so `Init` copies the keys of `XmlHelper.Serializers`, clears it and re-creates every entry with the
   overrides. At that moment the dictionary holds at least the `AssetBundle` serializer that the
   `XmlHelper` static constructor built ([current/decomp/KSA/XmlHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlHelper.cs#L17-L47)).[^ksa-xmlhelper]
6. **Swap the save serializers.** `GameSaves.UniverseSerializer` and `VehicleSaves.VehicleSerializer`
   are get-only static auto-properties assigned in static constructors
   ([current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L126-L155), [current/decomp/KSA/VehicleSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleSaves.cs#L169-L198)).
   `Init` overwrites their compiler-generated backing fields through local `extern` functions marked
   `[UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "<UniverseSerializer>k__BackingField")]`
   whose dummy parameter carries `[UnsafeAccessorType("KSA.GameSaves, KSA")]`, so the type is named
   by string and the read-only field is written through a `ref` return without `FieldInfo.SetValue`
   ([KittenExtensions/AssetEx.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/AssetEx.cs#L34-L42)).[^ksa-gamesaves] [^ksa-vehiclesaves]
7. **Deserialize and register.** `XmlPatcher.Load<T>` uses `AssetEx.GetSerializer<T>()`
   ([KittenExtensions/AssetEx.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/AssetEx.cs#L45-L52)), so a `<MPFXProfile>` element in a patched asset file
   becomes an `MPFXProfile` instance in `AssetBundle.Assets`. `AssetBundle.OnDataLoad` then calls
   `OnDataLoad(mod)` on every non-astronomical asset during `LoadAll` (only vehicles and situations
   during preload) ([current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L84-L108)). That override is where a
   custom type registers itself, as the README's `HexColor` example shows
   ([README.md](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/README.md#L297-L314)).

### Consumers in the reference set

| Mod | Declares | Element names | Code |
|---|---|---|---|
| MPFX | `[KxAsset("MPFXProfile")] class MPFXProfile : SerializedId` | `<MPFXProfile>` | [MPFX/MPFXProfile.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXProfile.cs#L10-L13) |
| ShaderExtensions | `[KxAsset("ShaderEx")] class ShaderEx : ShaderReference` and others | `<ShaderEx>`, `<PostProcessingShader>`, `<GlobalPostShader>`, `<ImGuiShader>` | [ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L12-L14) |
| KSA_AircraftHUD | no code; uses ShaderExtensions' elements in its asset file | `<PostProcessingShader>`, `<ImGuiShader>` | [HudAssests.xml](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/HudAssests.xml#L7-L21) |

## Lifecycle and timing

- **When: a Harmony prefix on `Program.Main`** ([KittenExtensions/Patches.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patches.cs#L13-L18)),
  the closest vocabulary value being `startup:starmap-before-main`. StarMap loads every enabled mod's
  assembly and runs each `[StarMapBeforeMain]` inside its loader, in manifest and dependency order
  ([StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L52-L104),
  [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L156-L159)); a dependent waits until its dependency
  has initialized, so when KittenExtensions' own `BeforeMain` runs its dependents are not initialized
  yet (a dependent listed earlier in the manifest already has its assembly loaded, but its constructor
  and `BeforeMain` wait).
  StarMap invokes the game's entry point only after that loop
  ([StarMap/GameSurveyer.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/GameSurveyer.cs#L47-L55)), so the `Main` prefix sees every mod assembly. The source
  comment says exactly this: "load before main, but after all mod assemblies are loaded in".[^kx-patches] [^starmap-modloader] [^starmap-runtimemod] [^starmap-gamesurveyer]
- **Before any XML:** `Program.Main` constructs `Program`, whose constructor later calls
  `ModLibrary.PrepareAll` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1693-L1703)), so the overrides are in
  place before the first `XmlLoader` or `XmlPatcher` call.[^ksa-program]
- **When the work pays off:** `startup:asset-load` (asset files deserialize with the new element
  names) and later every save or vehicle (de)serialization through the swapped save serializers.
- **Thread:** main thread; no game state exists yet besides static constructors that `Init` itself
  triggers.

## Key code

| Path | Role |
|---|---|
| [KittenExtensions/Attributes.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Attributes.cs#L6-L10) | The attribute contract dependents copy |
| [KittenExtensions/Patches.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patches.cs#L13-L18) | `Program.Main` prefix calling `AssetEx.Init` |
| [KittenExtensions/AssetEx.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/AssetEx.cs#L18-L43) | `Init`: discovery, overrides, serializer rebuild, save serializers |
| [KittenExtensions/AssetEx.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/AssetEx.cs#L61-L95) | Attribute matching by full name |
| [KittenExtensions/AssetEx.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/AssetEx.cs#L153-L239) | `XmlExtension`: validation and override building |

## Snippets

```csharp
// KittenExtensions/AssetEx.cs L18-L43
    public static void Init()
    {
        foreach (var alc in AssemblyLoadContext.All)
            foreach (var asm in alc.Assemblies)
                if (HasAnyAssetAttribute(asm))
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
// KittenExtensions/AssetEx.cs L158-L174
    public void BuildOverrides(XmlAttributeOverrides overrides)
    {
        foreach (var group in injections.GroupBy(inj => inj.Member))
        {
            var member = group.Key;
            var attrs = new XmlAttributes();
            foreach (var attr in member.GetCustomAttributes())
            {
                if (attr is XmlElementAttribute elAttr)
                    attrs.XmlElements.Add(elAttr);
            }
            foreach (var inj in group)
                attrs.XmlElements.Add(new(inj.XmlElement, inj.ChildType));

            overrides.Add(type, member.Name, attrs);
        }
    }
```

```csharp
// MPFX/Attributes.cs L9-L16
namespace KittenExtensions
{
    [AttributeUsage(AttributeTargets.Class)]
    internal class KxAssetAttribute(string xmlElement) : Attribute;

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    internal class KxAssetInjectAttribute(Type parent, string member, string xmlElement) : Attribute;
}
```

```csharp
// MPFX/MPFXProfile.cs L10-L13
namespace MPFX
{
    [KxAsset("MPFXProfile")]
    public class MPFXProfile : SerializedId, IKeyed, IComparer<MPFXProfile>
```

## Game APIs touched

| Member | What it is for |
|---|---|
| `XmlHelper.AttributeOverrides`, `XmlHelper.Serializers` ([current/decomp/KSA/XmlHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlHelper.cs#L11-L47)) | Shared overrides object and serializer cache, both public static fields |
| `AssetBundle.Assets` ([current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L9-L77)) | `List<SerializedId>` behind `<Assets>`; target of every `KxAsset` |
| `AssetBundle.OnDataLoad` ([current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L84-L108)) | Calls each asset's `OnDataLoad(mod)` |
| `SerializedId` ([current/decomp/KSA/SerializedId.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedId.cs#L7-L60)) | Base class for top-level asset types (`Id` attribute, `OnDataLoad`) |
| `GameSaves.UniverseSerializer`, `VehicleSaves.VehicleSerializer` | Save serializers replaced through their backing fields |
| `Program.Main` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1693-L1703)) | Prefixed as the "all mods loaded, game not started" moment |

## Pitfalls and version notes

- **Only assemblies loaded before `Program.Main`** are scanned. StarMap loads every enabled mod's
  entry assembly up front, but assemblies a mod loads lazily later are missed.
- **Uninitialized mods are scanned too.** `RuntimeMod.TryCreateMod` loads a mod's entry assembly into
  its load context before StarMap checks its dependencies
  ([StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L36-L76)), and `Init` walks
  `AssemblyLoadContext.All`, so a mod that StarMap never initialized because a required dependency
  is missing still gets its `KxAsset` types registered.
- **Failures are warnings.** A wrong member name or incompatible type logs
  `failed to inject ... into ...` to the game log and the element is simply unknown.
- **The README's C# examples are stale for build 2026.10.10.5554.** `GaugeComponent.FragmentShader`
  is a `GaugeShaderReference` ([current/decomp/KSA/GaugeComponent.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeComponent.cs#L37-L38)), so a
  `ShaderEx : ShaderReference` fails the compatibility check; ShaderExtensions keeps that inject
  commented out ([ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L13)). `GaugeBoxReference` has a `ColorRgb` field
  of type `ColorRgbReference` mapped to `<Color>` ([current/decomp/KSA/GaugeBoxReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeBoxReference.cs#L9-L10)),
  and no `ColorReference` type exists, so the `HexColor` example does not compile as written.[^ksa-gaugecomponent] [^ksa-gaugeboxreference]
- **Members that already have overrides.** The game's `XmlHelper` static constructor registers
  overrides for `PartInstance.Components` and `PartTemplate.Components` (part module types, scanned
  only from the game assembly). Injecting into those members would make `BuildOverrides` add a second
  entry for the same type and member, which .NET's `XmlAttributeOverrides.Add` rejects (unverified at
  run time).
- **Only `[XmlElement]` survives.** `BuildOverrides` copies the member's `[XmlElement]` attributes and
  nothing else, so a member that relies on `[XmlArray]`, `[XmlArrayItem]` or similar would lose that
  mapping once injected.
- **The pattern composes.** ShaderExtensions ships its own near-identical `AssetEx` in namespace
  `ShaderExtensions` for its own attributes, also called from a `Program.Main` prefix
  ([ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L20-L25), [ShaderExtensions/AssetEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/AssetEx.cs#L17-L45)). Each
  `Init` adds to the shared `XmlHelper.AttributeOverrides` and rebuilds every cached serializer, so
  whichever prefix runs last leaves serializers with both sets of extensions.[^sx-patches] [^sx-assetex]
- **Startup cost.** Every rebuild re-creates every cached serializer.

## See also

- [XmlLoader call-site redirect](xmlloader-call-site-redirect.md)
- [Continued fork with a new mod id and the old assembly name](continued-fork-identity.md)
- [StarMap shared contract assemblies](../../starmap/features/shared-contract-assemblies.md)
- [StarMap per-mod assembly load context](../../starmap/features/per-mod-assembly-load-context.md)
- [Add an XML element with KxAsset and inject child elements with KxAssetInject](../../../howto/assets/asset-xml/register-custom-xml-asset-type/register-custom-xml-asset-type-1.md) - how-to article demonstrating this feature
- [UnsafeAccessor StaticField on the k__BackingField with UnsafeAccessorType](../../../howto/patching/internals/call-private-game-members-through-reflection-and-delegates/call-private-game-members-through-reflection-and-delegates-6.md) - how-to article demonstrating this feature

[^kx-readme]: KittenExtensions README.md
[^sx-shaderex]: ShaderExtensions/ShaderEx.cs
[^mpfx-profile]: MPFX/MPFXProfile.cs
[^kx-attributes]: KittenExtensions/Attributes.cs
[^mpfx-attributes]: MPFX/Attributes.cs
[^ksa-assetbundle]: KSA.AssetBundle (decompiled, build 2026.10.10.5554)
[^ksa-xmlhelper]: KSA.XmlHelper (decompiled, build 2026.10.10.5554)
[^ksa-gamesaves]: KSA.GameSaves (decompiled, build 2026.10.10.5554)
[^ksa-vehiclesaves]: KSA.VehicleSaves (decompiled, build 2026.10.10.5554)
[^kx-patches]: KittenExtensions/Patches.cs
[^starmap-modloader]: StarMap.Core/ModRepository/ModLoader.cs
[^starmap-runtimemod]: StarMap.Core/ModRepository/RuntimeMod.cs
[^starmap-gamesurveyer]: StarMap/GameSurveyer.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-gaugecomponent]: KSA.GaugeComponent (decompiled, build 2026.10.10.5554)
[^ksa-gaugeboxreference]: KSA.GaugeBoxReference (decompiled, build 2026.10.10.5554)
[^sx-patches]: ShaderExtensions/Patches.cs
[^sx-assetex]: ShaderExtensions/AssetEx.cs
