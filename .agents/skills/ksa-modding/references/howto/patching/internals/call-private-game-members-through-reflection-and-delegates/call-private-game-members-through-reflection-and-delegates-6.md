---
type: How-To
title: UnsafeAccessor StaticField on the k__BackingField with UnsafeAccessorType
description: Overwrite the get-only statics GameSaves.UniverseSerializer and VehicleSerializer through UnsafeAccessor on their compiler-generated backing fields, naming the game type by string with UnsafeAccessorType
category: patching
group: internals
topic: call-private-game-members-through-reflection-and-delegates
approach: 6
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/kittenextensions/features/custom-xml-asset-types]
techniques: [unsafe-accessor, reflection]
hooks:
  - starmap StarMapBeforeMain
  - harmony-prefix KSA.Program.Main
  - unsafe-accessor KSA.GameSaves.<UniverseSerializer>k__BackingField
lifecycle: [startup:starmap-before-main, startup:asset-load]
game_types: [KSA.XmlHelper, KSA.GameSaves, KSA.VehicleSaves, KSA.AssetBundle]
tags: [unsafe-accessor, reflection, backing-field, get-only-static, xml, serializer]
---

A get-only static auto-property that is assigned in a static constructor is stored in a compiler-generated `initonly` static field named `<Name>k__BackingField`. After the type has initialised,
`FieldInfo.SetValue` refuses to write it. KittenExtensions needs to replace two such properties, `GameSaves.UniverseSerializer` and `VehicleSaves.VehicleSerializer`, with `XmlSerializer`s that know the
element names mods have injected. It does it with `[UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "<UniverseSerializer>k__BackingField")]` on a local `extern` function that returns `ref XmlSerializer`,
with `[UnsafeAccessorType("KSA.GameSaves, KSA")]` naming the type by string so the mod needs no compile-time reference. An assignment through the ref return overwrites the field.

Prerequisites: a StarMap mod; the game runs .NET 10, which has `UnsafeAccessorType` (see Variations). Demonstrated by KittenExtensions in [Custom XML asset types via KxAsset attributes](../../../../mods/kittenextensions/features/custom-xml-asset-types.md).

## Background: how the game does it

`XmlHelper` builds a shared `XmlAttributeOverrides` and a dictionary of cached serializers in its static constructor; the two save serializers are get-only static auto-properties assigned in the static
constructors of `GameSaves` and `VehicleSaves`:

```csharp
// current/decomp/KSA/XmlHelper.cs L15-L47
public static XmlAttributeOverrides AttributeOverrides;

static XmlHelper()
{
	Serializers = new Dictionary<Type, XmlSerializer>();
	AttributeOverrides = new XmlAttributeOverrides();
	IEnumerable<Type> source = from t in Assembly.GetExecutingAssembly().GetTypes()
		where t != typeof(ModuleBase.SaveDataBase) && typeof(ModuleBase.SaveDataBase).IsAssignableFrom(t)
		select t;
	XmlAttributes xmlAttributes = new XmlAttributes();
	Type[] array = source.ToArray();
	Type[] array2 = array;
	foreach (Type type in array2)
	{
		XmlElementAttribute attribute = new XmlElementAttribute(type.GetCustomAttribute<XmlTypeAttribute>()?.TypeName ?? type.Name, type);
		xmlAttributes.XmlElements.Add(attribute);
	}
	AttributeOverrides.Add(typeof(PartInstance), "Components", xmlAttributes);
	IEnumerable<Type> source2 = from t in Assembly.GetExecutingAssembly().GetTypes()
		where t != typeof(ModuleBase.TemplateDataBase) && typeof(ModuleBase.TemplateDataBase).IsAssignableFrom(t)
		select t;
	XmlAttributes xmlAttributes2 = new XmlAttributes();
	Type[] array3 = source2.ToArray();
	Type[] array4 = array3;
	foreach (Type type2 in array4)
	{
		XmlElementAttribute attribute2 = new XmlElementAttribute(type2.GetCustomAttribute<XmlTypeAttribute>()?.TypeName ?? type2.Name, type2);
		xmlAttributes2.XmlElements.Add(attribute2);
	}
	AttributeOverrides.Add(typeof(PartTemplate), "Components", xmlAttributes2);
	XmlSerializer value = new XmlSerializer(typeof(AssetBundle), AttributeOverrides);
	Serializers.Add(typeof(AssetBundle), value);
}
```

Source: [current/decomp/KSA/XmlHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlHelper.cs#L15-L47)[^ksa-xmlhelper]

```csharp
// current/decomp/KSA/GameSaves.cs L126
public static XmlSerializer UniverseSerializer { get; }
```

Source: [current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L126)[^ksa-gamesaves]

```csharp
// current/decomp/KSA/VehicleSaves.cs L169
public static XmlSerializer VehicleSerializer { get; }
```

Source: [current/decomp/KSA/VehicleSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleSaves.cs#L169)[^ksa-vehiclesaves]

An `XmlSerializer` captures its overrides when it is constructed, so adding element names after the fact needs every cached serializer rebuilt, and the two save serializers replaced.

## Lifecycle and timing

KittenExtensions installs a Harmony prefix on `Program.Main` from `[StarMapBeforeMain]` (`startup:starmap-before-main`); StarMap loads every enabled mod's assembly and runs each `[StarMapBeforeMain]` before invoking the game's
entry point, so the prefix sees every mod assembly and runs before `Program`'s constructor reaches `ModLibrary.PrepareAll` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L899)[^ksa-program]). The rebuild therefore completes before the first `XmlLoader` or
`XmlPatcher` call (`startup:asset-load`) and before any save or vehicle (de)serialization. Main thread; no game state exists yet beyond static constructors that the rebuild itself triggers. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Add the overrides first, then rebuild.** Build the merged `XmlAttributeOverrides` from the discovered injections, copy the keys of `XmlHelper.Serializers`, clear it and re-create each entry with the overrides:

```csharp
// KittenExtensions/AssetEx.cs L18-L32
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
```

Source: [KittenExtensions/AssetEx.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/AssetEx.cs#L18-L32)[^kittenextensions-assetex]

2. **Declare the accessors as local `extern` functions.** Each is `[UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "<Property>k__BackingField")]`, returns `ref XmlSerializer`, and takes a dummy parameter
   carrying `[UnsafeAccessorType("Namespace.Type, Assembly")]`; the optional `= null` makes the call site `UniverseSerializer()`:

```csharp
// KittenExtensions/AssetEx.cs L34-L43
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

Source: [KittenExtensions/AssetEx.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/AssetEx.cs#L34-L43)

3. **Assign through the ref return.** `UniverseSerializer() = GetSerializer<UniverseData>();` writes the field. The mod resolves the new serializer from the (now rebuilt) dictionary:

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

Source: [KittenExtensions/AssetEx.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/AssetEx.cs#L45-L52)

4. **Know why `FieldInfo.SetValue` is not an option.** A local test on .NET 10 with a minimal `static class` having a get-only static property confirmed both sides:

```text
// observed output of a local test on .NET 10 (not part of any mod repository)
backing field: <Serializer>k__BackingField IsInitOnly=True IsStatic=True
SetValue threw FieldAccessException: Cannot set initonly static field '<Serializer>k__BackingField' after type 'GameSaves' is initialized.
UnsafeAccessor write: via UnsafeAccessor ref
wrong field name threw MissingFieldException
```

5. **Do it only once the type has been initialised, and before anything caches the old instance.** In KSA the earlier XML consumers run later than this prefix, so nothing holds the stale serializer.

## Complete example

| File | Role |
|---|---|
| [KittenExtensions/AssetEx.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/AssetEx.cs#L14-L52) | `Init`: rebuild and write the two backing fields |
| [KittenExtensions/Patches.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patches.cs#L13-L18)[^kittenextensions-patches] | The `Program.Main` prefix that calls `Init` |

## Variations and gotchas

- **Name the backing field exactly.** `<UniverseSerializer>k__BackingField` and `<VehicleSerializer>k__BackingField` are compiler output; a game change to a different property shape (explicit field, instance
  property) breaks the accessor. The accessor throws `MissingFieldException` at the first call (checked in the same local test).
- **`UnsafeAccessorType` is what removes the compile-time reference.** Without it the accessor's first parameter would have to be the actual type. The assembly-qualified string is resolved at run time.
- **Only `[XmlElement]` survives** in the merged overrides (see the feature page); members using `[XmlArray]` lose their mapping once injected.
- **Startup cost.** Every rebuild re-creates every cached serializer.
- **Idempotence.** If the type has been re-initialised or the field replaced by another mod's rebuild, the last writer wins ([Copied injector in a consumer mod with the same accessor](call-private-game-members-through-reflection-and-delegates-7.md)).

## See also

- [AccessTools static field refs bound once, replay stock's writes](call-private-game-members-through-reflection-and-delegates-1.md), [Plain GetField on internal registries and a generic holder for instance fields](call-private-game-members-through-reflection-and-delegates-2.md), [Typed FieldRefAccess, boxed private enum members and by-ref lookups](call-private-game-members-through-reflection-and-delegates-3.md), [AccessTools.MethodDelegate with a delegate type that mirrors in and ref modifiers](call-private-game-members-through-reflection-and-delegates-4.md), [Delegates over private editor helpers reused in a replacement drag](call-private-game-members-through-reflection-and-delegates-5.md), [Copied injector in a consumer mod with the same accessor](call-private-game-members-through-reflection-and-delegates-7.md)
- [Reading, writing and calling private game members](overview.md)
- [Custom XML asset types via KxAsset attributes](../../../../mods/kittenextensions/features/custom-xml-asset-types.md)
- Related topics: write-readonly-game-statics-with-unsafeaccessor (merged here), register-custom-xml-asset-type, redirect-game-xml-loading

[^ksa-xmlhelper]: KSA.XmlHelper (decompiled, build 2026.10.10.5554)
[^ksa-gamesaves]: KSA.GameSaves (decompiled, build 2026.10.10.5554)
[^ksa-vehiclesaves]: KSA.VehicleSaves (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^kittenextensions-assetex]: KittenExtensions/AssetEx.cs
[^kittenextensions-patches]: KittenExtensions/Patches.cs
