---
type: How-To
title: Ship shader asset elements through KxAsset and a copied injector
description: Tag four classes with KxAsset so ShaderEx, ImGuiShader, PostProcessingShader and GlobalPostShader become Assets elements, and copy KittenExtensions' injector into your own assembly to add child elements and swap the save serializers
category: assets
group: asset-xml
topic: register-custom-xml-asset-type
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony, kittenextensions]
demonstrated_by: [mods/shaderextensions/features/xml-asset-types-and-element-injection]
techniques: [xml-asset, harmony-prefix, reflection]
hooks:
  - starmap StarMapBeforeMain
  - harmony-prefix KSA.Program.Main
  - reflection KSA.GameSaves.UniverseSerializer
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
tags: [xml, serializer, shaderextensions, injector, unsafeaccessor, kxasset]
---

ShaderExtensions turns shaders into data: four asset elements (`ShaderEx`, `ImGuiShader`, `PostProcessingShader`,
`GlobalPostShader`) that any mod can put in an `<Assets>` file, and each accepts child elements the game has never heard
of. It does this with two layers. Its asset classes are tagged `KxAsset` and discovered by KittenExtensions
(approach [Add an XML element with KxAsset and inject child elements with KxAssetInject](register-custom-xml-asset-type-1.md)). For the child elements and for consumer-defined buffer elements it
carries its own copy of the injector, installed from its own `Program.Main` prefix. This article walks through the second
layer, because it shows how to write the injector yourself, including the case where another injector already added
overrides, and how to swap the get-only save serializers with `UnsafeAccessor`. What the assets do is the shaders
category ([Declare ShaderEx, PostProcessingShader, GlobalPostShader and ImGuiShader assets](../../../shaders/post-processing/declare-post-processing-shader-assets-in-xml/overview.md)).

Demonstrated by [Custom XML asset types and injected child elements](../../../../mods/shaderextensions/features/xml-asset-types-and-element-injection.md).

## Background: how the game does it

Both layers extend the game's shared `XmlHelper.AttributeOverrides` and rebuild `XmlHelper.Serializers`
([current/decomp/KSA/XmlHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlHelper.cs#L13-L47)). The save serializers are separate get-only properties built in static constructors:

```csharp
// current/decomp/KSA/GameSaves.cs L126-L126
	public static XmlSerializer UniverseSerializer { get; }
```

so they must be replaced through their compiler-generated backing fields. The game calls `OnDataLoad` only on top-level
assets ([current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L84-L109)); a container asset forwards it to its children.

## Lifecycle and timing

`startup:starmap-before-main`: both mods install their Harmony instance from `[StarMapBeforeMain]`, whose
`Program.Main` prefix runs after every mod assembly is loaded and before the `Program` constructor. ShaderExtensions declares
KittenExtensionsContinued as a StarMap mod dependency in its csproj (step 1), and StarMap defers a mod until its required
dependencies are initialised, so KittenExtensions installs first; Harmony runs equal-priority prefixes in registration
order, so KX's overrides exist when ShaderExtensions' run and ShaderExtensions' rebuild of the serializers contains both sets of extensions.
`[StarMapAllModsLoaded]` would be too late (it fires after `LoadAll`). See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Declare the dependency.** The csproj generates the `mod.toml` dependency lines and also references the KX assembly:

   ```xml
   <!-- ShaderExtensions/ShaderExtensions.csproj L62-L71 -->
   		<!-- ... -->
   		<Reference Include="KittenExtensions">
   			<HintPath>$(KSAModsDir)KittenExtensionsContinued/KittenExtensions.dll</HintPath>
   			<Private>False</Private>
   		</Reference>
   	</ItemGroup>

   	<ItemGroup>
   		<StarMapModDependency Include="KittenExtensionsContinued">
   			<ImportedAssemblies>KittenExtensions</ImportedAssemblies>
   		</StarMapModDependency>
   		<!-- ... -->
   ```

2. **Install the prefix from `StarMapBeforeMain`:**

   ```csharp
   // ShaderExtensions/ShaderExtensions.cs L7-L16
       // ...
       public class ShaderExtensions
       {
           [StarMapBeforeMain]
           public void Setup()
           {
               var harmony = new Harmony("ShaderExtensions");
               harmony.PatchAll();
           }
       }
   }
   ```

   ```csharp
   // ShaderExtensions/Patches.cs L20-L25
           [HarmonyPatch(typeof(Program), "Main"), HarmonyPrefix]
           internal static void Program_Main_Prefix()
           {
               // We want to load before main, but after all mod assemblies are loaded in
               AssetEx.Init();
           }
   ```

3. **Tag the asset classes with `KxAsset`** (using the local copy of the attribute, declared internally):

   ```csharp
   // ShaderExtensions/Attributes.cs L9-L16
   {
       [AttributeUsage(AttributeTargets.Class)]
       internal class KxAssetAttribute(string xmlElement) : Attribute;

       [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
       internal class KxAssetInjectAttribute(Type parent, string member, string xmlElement) : Attribute;
   }

   ```

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

4. **Queue the injections you own and scan for consumer attributes.** The same `Init` registers child elements of the
   post-processing assets, scans every load context for assemblies that declare ShaderExtensions' own attributes
   (`SxUniformBuffer`, `SxPushConstant`), builds overrides, rebuilds all serializers and swaps the save serializers:

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

5. **Append when the member is already overridden.** `XmlAttributeOverrides.Add` throws if the (type, member) pair
   already has an override (the code relies on this; it was not exercised here). Several ShaderExtensions asset types
   (`PostProcessingShaderAsset`, `GlobalPostShaderAsset`, ...) inject into `ShaderEx.XmlBindings`, a member declared once
   on `ShaderEx`, so the second `Add` for that member would throw. ShaderExtensions therefore keys the override on
   `member.DeclaringType` (what `XmlSerializer` uses when it looks up member overrides), catches the exception, fetches the
   existing `XmlAttributes` and adds the missing `[XmlElement]` entries without duplicates. The same branch would absorb
   an override that another injector had already added for that member:

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

6. **Overwrite the get-only save serializers with `UnsafeAccessor`.** The `[UnsafeAccessor(UnsafeAccessorKind.StaticField)]`
   local functions name the backing field and return a `ref`; the dummy parameter's `[UnsafeAccessorType]` names the type
   by string so no assembly reference is needed (lines 38-46 of the `Init` above). Without this swap, saves written or
   read while your elements are present would use serializers without the overrides.

7. **Forward `OnDataLoad` to children** in each container asset so injected child elements initialise:

   ```csharp
   // ShaderExtensions/ShaderEx.cs L27-L34
           public override void OnDataLoad(Mod mod)
           {
               base.OnDataLoad(mod);
               foreach (var binding in XmlBindings)
                   binding.OnDataLoad(mod);

               ModLibrary.RegisterBinder(this);
           }
   ```

8. **Write the asset XML.** A consumer mod then uses the new elements like any other:

   ```xml
   <!-- MPFX/MPFXAssests.xml L1-L10 -->
   <?xml version="1.0" encoding="utf-8"?>
   <Assets>
   	<PostProcessingShader Id="MPFXColorBalanceShaderPre" Path="Shaders/ColorBalancePre.frag" RenderPassId="128">
   		<MPFXMat3BufferAsset Id="MPFXColorBalanceBuffer" Size="1" />
   	</PostProcessingShader>
   	<GlobalPostShader Id="MPFXColorBalanceShaderPost" Path="Shaders/ColorBalancePost.frag" RenderPassId="128">
   		<MPFXDefaultBufferAsset Id="MPFXColorBalanceBuffer" />
   	</GlobalPostShader>

   	<PostProcessingShader Id="MPFXColorTempShaderPre" Path="Shaders/ColorTempPre.frag" RenderPassId="129">
   <!-- ... -->
   ```

## Complete example

- [ShaderExtensions/AssetEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/AssetEx.cs), [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L19-L25)
- [ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs), [ShaderExtensions/Attributes.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Attributes.cs)
- [MPFX/MPFXAssests.xml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXAssests.xml) (a consumer)

## Variations and gotchas

- **It still depends on KittenExtensions for the top-level elements.** ShaderExtensions declares `KxAsset` itself but the
  scanner that acts on it is KX's; the copied injector only handles child elements and consumer attributes. Use the
  copied-injector pattern when you need no KX at all, or the shorter `KxAssetInject` path when KX is already required.
- **README example is commented out.** The `[KxAssetInject(typeof(GaugeComponent), nameof(GaugeComponent.FragmentShader), "FragmentEx")]`
  line is commented out in `ShaderEx.cs` because `FragmentShader` is a `GaugeShaderReference`, not a `ShaderReference`.
- **Order matters.** Whichever injector runs last leaves serializers containing every extension; a rebuild is a startup
  cost per injector.
- **Failures are warnings** (`failed to inject ... into ...`); a wrong parent/member leaves the element unknown.
- **The game's own cached serializers are replaced.** Any code that holds an `XmlSerializer` obtained earlier keeps the old
  one; fetch from `XmlHelper.Serializers`.

## See also

- [Add an XML element with KxAsset and inject child elements with KxAssetInject](register-custom-xml-asset-type-1.md), [Ship a data-only profile type that loads from ordinary asset XML](register-custom-xml-asset-type-3.md), [Declare ShaderEx, PostProcessingShader, GlobalPostShader and ImGuiShader assets](../../../shaders/post-processing/declare-post-processing-shader-assets-in-xml/overview.md),
  [Write a fullscreen post-processing fragment shader for KSA](../../../shaders/post-processing/write-a-post-processing-fragment-shader/overview.md)
- [Choose and use the right StarMap lifecycle hook](../../../getting-started/lifecycle/choose-a-starmap-startup-hook/overview.md), [Reading, writing and calling private game members](../../../patching/internals/call-private-game-members-through-reflection-and-delegates/overview.md)
- [Custom XML asset types and injected child elements](../../../../mods/shaderextensions/features/xml-asset-types-and-element-injection.md)
