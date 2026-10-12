---
type: How-To
title: Copied injector in a consumer mod with the same accessor
description: A consumer that cannot reference KittenExtensions copies the XML element injector into its own namespace, adds child element types to existing XML list members through XmlHelper.AttributeOverrides keyed by the declaring type, rebuilds the serializers and swaps the save serializers the same way
category: patching
group: internals
topic: call-private-game-members-through-reflection-and-delegates
approach: 7
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/shaderextensions/features/xml-asset-types-and-element-injection]
techniques: [unsafe-accessor, reflection, xml-asset]
hooks:
  - starmap StarMapBeforeMain
  - harmony-prefix KSA.Program.Main
  - unsafe-accessor KSA.GameSaves.<UniverseSerializer>k__BackingField
lifecycle: [startup:starmap-before-main, startup:asset-load]
game_types: [KSA.XmlHelper, KSA.GameSaves, KSA.VehicleSaves, KSA.AssetBundle]
tags: [unsafe-accessor, reflection, xml, serializer, injector, shaderextensions]
---

ShaderExtensions has asset types with child elements the game has never heard of: its `ShaderEx` accepts `<PostProcessingInputReference>` and every consumer buffer element in `XmlBindings`. Declaring the
asset types is done by KittenExtensions' `[KxAsset]`; adding *child* element types to members of those types, and of ShaderExtensions' own attributes, is done by ShaderExtensions' **own copy** of the injector, in its own
namespace. It is the same mechanism as [UnsafeAccessor StaticField on the k__BackingField with UnsafeAccessorType](call-private-game-members-through-reflection-and-delegates-6.md): add to `XmlHelper.AttributeOverrides`, rebuild every cached `XmlSerializer`, and overwrite the two save serializers through `UnsafeAccessor`. The new points
are the differences a copy forces: keying the override on the member's declaring type, and tolerating an override that already exists.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by ShaderExtensions in [Custom XML asset types and injected child elements](../../../../mods/shaderextensions/features/xml-asset-types-and-element-injection.md).

## Background: how the game does it

`XmlHelper` owns the shared overrides and the serializer cache, and `AssetBundle.OnDataLoad` calls `OnDataLoad` only on top-level assets, so a container forwards it to its children
([current/decomp/KSA/XmlHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlHelper.cs#L15-L47)[^ksa-xmlhelper], [current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L84-L109)[^ksa-assetbundle]). The `XmlBindings` member of `ShaderEx` is a `List<SerializedId>`:

```csharp
// ShaderExtensions/ShaderEx.cs L18-L19
[XmlElement("TextureBinding", typeof(TextureBindingReference))]
public List<SerializedId> XmlBindings = [];
```

Source: [ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L18-L19)[^shaderextensions-shaderex]

## Lifecycle and timing

Both mods install a Harmony prefix on `Program.Main` from `[StarMapBeforeMain]` (`startup:starmap-before-main`). The prefix runs after every mod assembly is loaded and before the `Program` constructor reaches
`ModLibrary.PrepareAll` and `LoadAll` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L899-L942)[^ksa-program]). StarMap defers a mod until its required dependencies are initialised and runs `[StarMapBeforeMain]` inside `InitializeMod`, so
KittenExtensions patches first; Harmony runs equal-priority prefixes in registration order (a local test with two default-priority prefixes ran the first registered first), so KittenExtensions' overrides
exist when ShaderExtensions' run. Why so early: the mod's compatibility plan records that `[StarMapAllModsLoaded]` may be too late for XML serializer overrides. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Install a `Program.Main` prefix.** The comment explains the timing:

```csharp
// ShaderExtensions/Patches.cs L20-L25
[HarmonyPatch(typeof(Program), "Main"), HarmonyPrefix]
internal static void Program_Main_Prefix()
{
    // We want to load before main, but after all mod assemblies are loaded in
    AssetEx.Init();
}
```

Source: [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L20-L25)[^shaderextensions-patches]

2. **Queue the injections, scan for attributes, then build and rebuild.** `Init` first adds its static injections, then scans every assembly in `AssemblyLoadContext.All` for its attribute types, builds overrides,
   re-creates every cached serializer, and writes the two save serializers with the same `UnsafeAccessor` pattern as [UnsafeAccessor StaticField on the k__BackingField with UnsafeAccessorType](call-private-game-members-through-reflection-and-delegates-6.md):

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

Source: [ShaderExtensions/AssetEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/AssetEx.cs#L17-L47)[^shaderextensions-assetex]

3. **Key the override on the member's declaring type.** `XmlSerializer` looks member overrides up by declaring type, so `member.DeclaringType ?? type` is the key; injecting into
   `PostProcessingShaderAsset.XmlBindings` therefore extends `ShaderEx.XmlBindings` (KittenExtensions keys on the extended type instead). Copy the member's existing `[XmlElement]` attributes first, then add the
   injected ones, deduplicating by name and type:

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

Source: [ShaderExtensions/AssetEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/AssetEx.cs#L142-L173)

4. **Handle an existing override.** If the member already has an override (typically `AssetBundle.Assets` from KittenExtensions), `Add` throws; the `catch` appends to the existing `XmlAttributes` and skips duplicates
   (`AddXmlElement`, lines 175-184).

5. **Validate each injection and log, never throw.** `Add` checks that the member exists, that it is a list or field the child type fits, that it is not `[XmlIgnore]` or `[XmlAttribute]`, and that the element name
   is not already mapped:

```csharp
// ShaderExtensions/AssetEx.cs L186-L199
public bool Add(string memberName, Type childType, string xmlElement)
{
    var member = (MemberInfo)type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public) ??
      type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public);

    var inj = new XmlElementInjection(member, childType, xmlElement);
    if (injections.Contains(inj))
        return false;

    if (!Validate(memberName, member, childType, xmlElement))
        return false;

    return injections.Add(inj);
}
```

Source: [ShaderExtensions/AssetEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/AssetEx.cs#L186-L199)

## Complete example

| File | Role |
|---|---|
| [ShaderExtensions/AssetEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/AssetEx.cs) | The copied injector, `XmlExtension` and the `UnsafeAccessor` writes |
| [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L20-L25) | The `Program.Main` prefix |

## Variations and gotchas

- **Order-dependent with KittenExtensions.** KittenExtensions' `BuildOverrides` calls `overrides.Add` with no `catch`; it works only because its prefix runs first. A third mod with its own copy of the injector and
  no `catch` would break whichever runs second.
- **Serializers are rebuilt twice** (once per prefix), and `XmlSerializer` construction with overrides generates code, so each mod doing this adds startup time.
- **Duplicated code drifts.** `XmlExtension` is a modified copy living in the `ShaderExtensions` namespace; fixes in one do not reach the other.
- **Pass references are legal on every `ShaderEx`.** Because the override is keyed on the declaring type, `<PostProcessingInputReference>` parses inside plain `<ShaderEx>` and ImGui fragments too, where nothing
  resolves it.
- **Last writer wins on the save serializers,** but both mods write serializers built from the same shared `XmlHelper.AttributeOverrides`, so whichever prefix runs last leaves serializers with both sets of
  extensions.

## See also

- [AccessTools static field refs bound once, replay stock's writes](call-private-game-members-through-reflection-and-delegates-1.md), [Plain GetField on internal registries and a generic holder for instance fields](call-private-game-members-through-reflection-and-delegates-2.md), [Typed FieldRefAccess, boxed private enum members and by-ref lookups](call-private-game-members-through-reflection-and-delegates-3.md), [AccessTools.MethodDelegate with a delegate type that mirrors in and ref modifiers](call-private-game-members-through-reflection-and-delegates-4.md), [Delegates over private editor helpers reused in a replacement drag](call-private-game-members-through-reflection-and-delegates-5.md), [UnsafeAccessor StaticField on the k__BackingField with UnsafeAccessorType](call-private-game-members-through-reflection-and-delegates-6.md)
- [Reading, writing and calling private game members](overview.md)
- [Custom XML asset types and injected child elements](../../../../mods/shaderextensions/features/xml-asset-types-and-element-injection.md), [Custom XML asset types via KxAsset attributes](../../../../mods/kittenextensions/features/custom-xml-asset-types.md)
- Related tasks: register-custom-xml-asset-type, redirect-game-xml-loading, feed-uniform-buffers-to-a-shaderextensions-shader

[^ksa-xmlhelper]: KSA.XmlHelper (decompiled, build 2026.10.10.5554)
[^ksa-assetbundle]: KSA.AssetBundle (decompiled, build 2026.10.10.5554)
[^shaderextensions-shaderex]: ShaderExtensions/ShaderEx.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^shaderextensions-patches]: ShaderExtensions/Patches.cs
[^shaderextensions-assetex]: ShaderExtensions/AssetEx.cs
