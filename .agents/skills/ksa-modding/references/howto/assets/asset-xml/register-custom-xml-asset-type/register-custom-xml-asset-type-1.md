---
type: How-To
title: Add an XML element with KxAsset and inject child elements with KxAssetInject
description: Copy two attribute classes into your assembly, tag a SerializedId subclass with KxAsset and KittenExtensions teaches the game's XmlSerializers the new element; KxAssetInject adds a child element type to any public list member of game XML
category: assets
group: asset-xml
topic: register-custom-xml-asset-type
approach: 1
difficulty: advanced
prerequisites: [starmap, kittenextensions]
demonstrated_by: [mods/kittenextensions/features/custom-xml-asset-types]
techniques: [xml-asset, reflection, harmony-prefix]
hooks: [harmony-prefix KSA.Program.Main, reflection KittenExtensions.KxAssetAttribute]
lifecycle: [startup:starmap-before-main, startup:asset-load]
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
tags: [xml, serializer, custom-asset, kxasset, kittenextensions, attributes, injection]
---

KSA's XML schema is closed: `AssetBundle.Assets` knows about 65 element names and nothing else
([Know which asset XML elements the game accepts and what they load](../asset-xml-element-reference/overview.md)). KittenExtensions (KX) opens it. A class in any mod assembly tagged with a
`KxAsset("Name")` attribute becomes a new `<Name>` element that every mod's `<Assets>` file can use, and
`KxAssetInject(parent, member, element)` adds a new child element type to any public list member of any game XML class.
No game file changes and no reference to KX is needed. ShaderExtensions uses it to ship shader assets, and MPFX uses it
to ship data profiles ([Ship shader asset elements through KxAsset and a copied injector](register-custom-xml-asset-type-2.md), [Ship a data-only profile type that loads from ordinary asset XML](register-custom-xml-asset-type-3.md)).

Demonstrated by [Custom XML asset types via KxAsset attributes](../../../../mods/kittenextensions/features/custom-xml-asset-types.md). Prerequisites: StarMap, KX installed (or KX as a
declared dependency, [Declare a dependency on another StarMap mod, required or optional](../../../getting-started/dependencies/declare-a-mod-dependency/overview.md)), and knowledge of the element table
([Know which asset XML elements the game accepts and what they load](../asset-xml-element-reference/overview.md)).

## Background: how the game does it

The game deserializes asset XML with `XmlSerializer`s built from a shared `XmlAttributeOverrides`. `XmlHelper`'s static
constructor registers part module types and builds the `AssetBundle` serializer with those overrides, then caches it
in `XmlHelper.Serializers`:

```csharp
// current/decomp/KSA/XmlHelper.cs L13-L47
	public static Dictionary<Type, XmlSerializer> Serializers;

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

An `XmlSerializer` captures its overrides when it is constructed, so adding to `AttributeOverrides` later does nothing
for serializers that already exist. The two save serializers are built in static constructors and exposed as
get-only static properties ([current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L126-L126), [current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L155-L155), [current/decomp/KSA/VehicleSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleSaves.cs#L169-L169),
[current/decomp/KSA/VehicleSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleSaves.cs#L198-L198)). The stock loader only caches a plain `new XmlSerializer(typeof(T))` without overrides
for any type it has not seen ([current/decomp/KSA/XmlLoader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlLoader.cs#L32-L45)). After deserialization `AssetBundle.OnDataLoad` calls
`OnDataLoad(mod)` on every asset, which is where a custom type registers itself ([current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L84-L109)).

## Lifecycle and timing

`startup:starmap-before-main` for the setup, `startup:asset-load` for the use. KX registers a Harmony prefix on
`Program.Main` from its `[StarMapBeforeMain]`; that prefix runs after every mod assembly is loaded and before the
`Program` constructor reads any XML:

```csharp
// KittenExtensions/Patches.cs L13-L18
  [HarmonyPatch(typeof(Program), "Main"), HarmonyPrefix]
  internal static void Program_Main_Prefix()
  {
    // We want to load before main, but after all mod assemblies are loaded in
    AssetEx.Init();
  }
```

`Program.Main` is the private entry point ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1693-L1702)). `StarMapAllModsLoaded` would be too late: it
fires after `LoadAll`. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Copy the two attribute classes into your assembly.** They must be in namespace `KittenExtensions`, and KX finds
   them by full name across `AssemblyLoadContext.All`, so you need no assembly reference. At least one must be defined
   in the same assembly as the classes you tag:

   ```csharp
   // KittenExtensions/Attributes.cs L1-L10
   using System;
   #pragma warning disable CS9113

   namespace KittenExtensions;

   [AttributeUsage(AttributeTargets.Class)]
   internal class KxAssetAttribute(string xmlElement) : Attribute;

   [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
   internal class KxAssetInjectAttribute(Type parent, string member, string xmlElement) : Attribute;
   ```

   (MPFX and ShaderExtensions each carry their own `Attributes.cs` that declares the same two classes in namespace `KittenExtensions`; ShaderExtensions' also holds its `Sx*` attributes.)

2. **Write the type.** For a top-level asset, extend `SerializedId` (or a more specific game type). Public fields and
   properties are the XML schema; `OnDataLoad(Mod)` is where you register it. Tag it with `KxAsset`:

   ```csharp
   // ShaderExtensions/ShaderEx.cs L12-L24
       [KxAsset("ShaderEx")]
       //[KxAssetInject(typeof(GaugeComponent), nameof(GaugeComponent.FragmentShader), "FragmentEx")]
       public class ShaderEx : ShaderReference, IBinder
       {
           private const int MIN_PUSH_CONSTANT_SIZE_BYTES = 128;

           [XmlElement("TextureBinding", typeof(TextureBindingReference))]
           public List<SerializedId> XmlBindings = [];

           [XmlIgnore]
           public List<IShaderBinding> Bindings;

           [XmlIgnore]
       // ...
   ```

3. **Use the element in any asset file.** Any mod's `<Assets>` file may now contain `<ShaderEx Id="...">`. KX handles
   `KxAsset` as an injection into `AssetBundle.Assets`:

   ```csharp
   // KittenExtensions/AssetEx.cs L61-L95
       private static bool RegisterAttr(Type type, CustomAttributeData attr)
       {
           bool fail(string message)
           {
               DefaultCategory.Log.Warning($"invalid attribute {attr.AttributeType} on {type}: {message}");
               return false;
           }

           if (attr.AttributeType.FullName == typeof(KxAssetAttribute).FullName)
           {
               if (attr.ConstructorArguments.Count < 1)
                   return fail("not enough arguments");

               if (!ValidateArg(attr, 0, out string elName, out var err))
                   return fail(err);

               AddExtension(typeof(AssetBundle), nameof(AssetBundle.Assets), type, elName);
           }
           else if (attr.AttributeType.FullName == typeof(KxAssetInjectAttribute).FullName)
           {
               if (attr.ConstructorArguments.Count < 3)
                   return fail("not enough arguments");

               if (!ValidateArg(attr, 0, out Type parent, out var err))
                   return fail(err);
               if (!ValidateArg(attr, 1, out string member, out err))
                   return fail(err);
               if (!ValidateArg(attr, 2, out string xmlElement, out err))
                   return fail(err);

               AddExtension(parent, member, type, xmlElement);
           }

           return true;
       }
   ```

4. **Inject into game XML with `KxAssetInject`.** Give the parent type, the public member name and the new element
   name. The member must be a field or property whose type the class is assignable to (or whose `List<T>` element type
   it is assignable to). KX validates and logs a warning, never an exception. The README's own examples no longer match
   this game build (`GaugeComponent.FragmentShader` is a `GaugeShaderReference`, and no `ColorReference` type exists), so
   here is a form checked against the types:

   ```csharp
   // adapted from KittenExtensions/README.md; not run by any mod. Checked against GaugeComponent.Rects (List<GaugeRectReference>)
   [KxAssetInject(typeof(GaugeComponent), nameof(GaugeComponent.Rects), "MyRect")]
   public class MyRect : GaugeRectReference
   {
   }
   ```

   The validation rules, from KX:

   ```csharp
   // KittenExtensions/AssetEx.cs L191-L223
       private bool Validate(string memberName, MemberInfo member, Type childType, string xmlElement)
       {
           bool fail(string message)
           {
               DefaultCategory.Log.Warning($"failed to inject {childType} as <{xmlElement}> into {type}.{memberName}: {message}");
               return false;
           }

           if (member == null)
               return fail($"member {memberName} not found");

           var mtype = (member as PropertyInfo)?.PropertyType ?? (member as FieldInfo)?.FieldType;
           if (!TypesCompatible(mtype, childType))
               return fail($"{childType} incompatible with member type {mtype}");

           var attrs = member.GetCustomAttributes();

           if (attrs.Any(attr => attr is XmlIgnoreAttribute))
               return fail($"member {memberName} is ignored for xml serialization");

           if (attrs.Any(attr => attr is XmlAttributeAttribute))
               return fail($"member {memberName} is an xml attribute field");

           var attrMatch = attrs.OfType<XmlElementAttribute>().FirstOrDefault(attr => attr.ElementName == xmlElement);
           if (attrMatch != null)
               return fail($"<{xmlElement}> is already mapped by default as {attrMatch.Type}");

           var injMatch = injections.FirstOrDefault(inj => inj.Member == member && inj.XmlElement == xmlElement);
           if (injMatch != null)
               return fail($"<{xmlElement}> is already injected as {injMatch.ChildType}");

           return true;
       }
   ```

5. **Understand the rebuild.** KX collects the injections, builds an `XmlAttributes` per member that re-adds the member's
   own `[XmlElement]` attributes (an override replaces them) plus the new ones, adds it to
   `XmlHelper.AttributeOverrides`, then clears and recreates every cached serializer and overwrites the two save
   serializers through `UnsafeAccessor`:

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
   // KittenExtensions/AssetEx.cs L153-L174
   public class XmlExtension(Type type)
   {
       private readonly Type type = type;
       private readonly HashSet<XmlElementInjection> injections = [];

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
   // ...
   ```

6. **Make the dependency real.** If your mod needs the elements, declare KX as a required StarMap dependency (an
   optional one means your elements may silently not exist). Start the game and look for
   `failed to inject ... into ...` warnings in the game log.

## Complete example

- [KittenExtensions/AssetEx.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/AssetEx.cs) (discovery, validation, overrides, serializer rebuild)
- [KittenExtensions/Attributes.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Attributes.cs) (the two attribute classes to copy)
- [ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs) and [MPFX/MPFXProfile.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXProfile.cs) (real `KxAsset` types)

## Variations and gotchas

- **Only assemblies loaded before `Program.Main` are scanned.** StarMap loads every enabled mod's entry assembly up
  front; assemblies loaded lazily later are missed. A mod StarMap never initialised (missing required dependency) still gets
  its `KxAsset` types registered, because its assembly is already in a load context.
- **Failures are warnings.** A wrong member name or an incompatible type logs `failed to inject ...` and the element is
  simply unknown (the XML then loads without it).
- **Members that already have overrides.** The game registers overrides for `PartInstance.Components` and
  `PartTemplate.Components`; injecting into those members would make a second `Add` for the same type and member, which
  .NET's `XmlAttributeOverrides` rejects (not verified at run time). New part module elements therefore cannot be added
  this way ([Know which asset XML elements the game accepts and what they load](../asset-xml-element-reference/overview.md)).
- **Only `[XmlElement]` survives** the copy in `BuildOverrides`; a member that relies on `[XmlArray]` or `[XmlArrayItem]` would
  lose it once injected.
- **The pattern composes.** Several mods can each run an injector; each adds to the shared overrides and rebuilds every
  cached serializer, so whichever runs last leaves serializers with all the extensions. Startup cost is one rebuild per
  injector.
- **Nested `OnDataLoad`.** The game calls `OnDataLoad` only on top-level assets, so a container must forward it to the
  injected children it owns.

## See also

- [Ship shader asset elements through KxAsset and a copied injector](register-custom-xml-asset-type-2.md), [Ship a data-only profile type that loads from ordinary asset XML](register-custom-xml-asset-type-3.md), [Know which asset XML elements the game accepts and what they load](../asset-xml-element-reference/overview.md),
  [Reading, writing and calling private game members](../../../patching/internals/call-private-game-members-through-reflection-and-delegates/overview.md)
- [Declare ShaderEx, PostProcessingShader, GlobalPostShader and ImGuiShader assets](../../../shaders/post-processing/declare-post-processing-shader-assets-in-xml/overview.md), [Write a fullscreen post-processing fragment shader for KSA](../../../shaders/post-processing/write-a-post-processing-fragment-shader/overview.md),
  [Choose and use the right StarMap lifecycle hook](../../../getting-started/lifecycle/choose-a-starmap-startup-hook/overview.md), [Patch Core or another mod's XML data without copying files](../../xml-patching/patch-game-xml-data/overview.md)
- [Custom XML asset types via KxAsset attributes](../../../../mods/kittenextensions/features/custom-xml-asset-types.md)
