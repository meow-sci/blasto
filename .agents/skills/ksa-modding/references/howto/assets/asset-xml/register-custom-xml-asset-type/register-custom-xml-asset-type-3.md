---
type: How-To
title: Ship a data-only profile type that loads from ordinary asset XML
description: Subclass SerializedId with public fields as the schema, register it in a static set in OnDataLoad, and trim defaults so an exported profile is compact XML to paste back into an Assets file
category: assets
group: asset-xml
topic: register-custom-xml-asset-type
approach: 3
difficulty: intermediate
prerequisites: [starmap, kittenextensions]
demonstrated_by: [mods/mpfx/features/xml-effect-profiles]
techniques: [xml-asset, xml-serializer]
hooks: [kx KxAsset MPFXProfile]
lifecycle: [startup:asset-load, frame:draw-ui]
game_types: [KSA.XmlHelper, KSA.AssetBundle, KSA.SerializedId, KSA.ModLibrary]
tags: [xml, custom-asset, profile, defaultvalue, shouldserialize, mpfx, export]
---

Some mods need a data type that players and other mods can write as XML: presets, tuning tables, effect chains. MPFX does
this with `MPFXProfile`, a `SerializedId` subclass tagged `KxAsset("MPFXProfile")`. Its public fields are the schema,
`OnDataLoad` registers each loaded profile in a static set, and `DefaultValue`/`ShouldSerialize*` trimming means an exported
profile is compact XML a user can paste into any `<Assets>` file. This is the lightest use of
[Add an XML element with KxAsset and inject child elements with KxAssetInject](register-custom-xml-asset-type-1.md): no behaviour in XML, just data that code reads from a registry.

Demonstrated by [Effect profiles as XML assets with clipboard export](../../../../mods/mpfx/features/xml-effect-profiles.md).

## Background: how the game does it

`SerializedId` is the base of everything in the asset table. It supplies the `Id` attribute, hashes it and records the
owning `Mod` in `OnDataLoad`; `Populate` and `ToRow` are abstract-ish members a data-only type can leave unimplemented
because it is never listed in a game table:

```csharp
// current/decomp/KSA/SerializedId.cs L6-L31
[XmlRoot]
public abstract class SerializedId : ILibraryData, IListable
{
	[XmlIgnore]
	public bool IsReferenceable { get; set; }

	[XmlAttribute]
	public string Id { get; set; } = string.Empty;

	[XmlIgnore]
	public Mod? Mod { get; private set; }

	[XmlIgnore]
	public KeyHash Hash { get; private set; }

	public string GetRealId()
	{
		return IsReferenceable ? Id : string.Empty;
	}

	public virtual void OnDataLoad(Mod mod)
	{
		Mod = mod;
		IsReferenceable = !string.IsNullOrEmpty(Id);
		SetHash();
	}
// ...
```

`AssetBundle.OnDataLoad` calls `OnDataLoad(mod)` on every non-astronomical asset during `LoadAll` ([current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L84-L109)).
.NET's `XmlSerializer` omits a member equal to its `[DefaultValue]` and calls `ShouldSerialize<Name>()` to decide on the rest.

## Lifecycle and timing

- `startup:asset-load`: `<MPFXProfile>` elements deserialize with the other asset bundles and `OnDataLoad(Mod)` runs during
  `ModLibrary.LoadAll`. A built-in `Default` profile is created lazily on first access.
- `frame:draw-ui`: profile switching, duplication and export happen inside the mod's `[StarMapAfterGui]` window code.
  See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Declare the class.** `SerializedId` plus `IKeyed` if you want lookups by key; tag it `KxAsset` (needs the attribute
   copy from [Add an XML element with KxAsset and inject child elements with KxAssetInject](register-custom-xml-asset-type-1.md)):

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
   // MPFX/MPFXProfile.cs L12-L31
       [KxAsset("MPFXProfile")]
       public class MPFXProfile : SerializedId, IKeyed, IComparer<MPFXProfile>
       {
           public static string ProfileSaveDirectory => Path.Combine(ModLibrary.LocalModsFolderPath, "MPFX", "Profiles");

           private static MPFXProfile defaultProfile;
           public static MPFXProfile DefaultProfile
           {
               get
               {
                   if (defaultProfile == null)
                   {
                       defaultProfile = new MPFXProfile("Default");
                   }
                   return defaultProfile;
               }
           }

           public static HashSet<MPFXProfile> Profiles { get; set; } = new HashSet<MPFXProfile>() { };
           public int LookupIndex { get; set; }
       // ...
   ```

2. **Make every setting a public field with a default.** The `DefaultValue` attribute means "omit when equal"; struct
   fields use `ShouldSerialize<Name>()`:

   ```csharp
   // MPFX/MPFXProfile.cs L33-L49
           #region variables
           [DefaultValue(false)]
           public bool ColorBalancePreImgui = false;
           [DefaultValue(false)]
           public bool ColorBalancePreImguiSeparateRGB = false;
           public float4x4 ColorBalanceMatPreImgui = new float4x4(
               0, 0, 0, 0, // Lift
               1, 1, 1, 0, // Gamma
               1, 1, 1, 0, // Gain
               0, 0, 0, 1  // last value used as on/off toggle, 3rd value used in xml for separate RGB
           );
           public bool ShouldSerializeColorBalanceMatPreImgui() => ColorBalanceMatPreImgui != new float4x4(
               0, 0, 0, 0,
               1, 1, 1, 0,
               1, 1, 1, 0,
               0, 0, 0, 1
           );
   ```

3. **Register on load.** `OnDataLoad` adds the instance to a static `HashSet<MPFXProfile>` unless an `Id` is present;
   `Equals` and `GetHashCode` use `Id`. `Populate` and `ToRow` throw `NotImplementedException`, which is fine for a type that
   is never listed in a game table:

   ```csharp
   // MPFX/MPFXProfile.cs L217-L240
           public override void OnDataLoad(Mod mod)
           {
               base.OnDataLoad(mod);
               if (!Profiles.Any(p => p.Id == this.Id)) Profiles.Add(this);
           }

           public override SerializedId Populate()
           {
               throw new System.NotImplementedException();
           }

           public override TableString.Row ToRow()
           {
               throw new System.NotImplementedException();
           }

           public int Compare(MPFXProfile x, MPFXProfile y) => x.Id.CompareTo(y.Id);

           public override int GetHashCode() => Id.GetHashCode();

           public override bool Equals(object obj) => obj is MPFXProfile profile && profile.Id == this.Id;

           public static MPFXProfile GetOrCreateProfile(string id) => Profiles.FirstOrDefault(p => p.Id == id) ?? new MPFXProfile(id);

   ```

4. **Ship presets as data.** Any mod's Assets file may contain `<MPFXProfile Id="MyPreset">` with only the non-default
   fields. MPFX's own `MPFXAssests.xml` ships none (only shader assets), but the type accepts them.

5. **Export for users to paste back.** Serialise the current profile with a namespace-free, declaration-free writer so
   no `xsi` or `xsd` attributes appear:

   ```csharp
   // MPFX/Serializer.cs L11-L36
           public static string SerializeToXml<T>(T obj)
           {
               var serializer = new XmlSerializer(typeof(T));

               var settings = new System.Xml.XmlWriterSettings
               {
                   Encoding = Encoding.UTF8,
                   Indent = true,
                   OmitXmlDeclaration = true
               };

               var namespaces = new XmlSerializerNamespaces();
               namespaces.Add("", ""); // removes xsi and xsd namespaces

               using var stringWriter = new Utf8StringWriter();
               using var writer = System.Xml.XmlWriter.Create(stringWriter, settings);

               serializer.Serialize(writer, obj, namespaces);
               return stringWriter.ToString();
           }

           // Ensures UTF-8 instead of UTF-16
           public class Utf8StringWriter : StringWriter
           {
               public override Encoding Encoding => Encoding.UTF8;
           }
   ```

6. **Read it from code** with the static set (`MPFXProfile.GetOrCreateProfile(id)`), not from `ModLibrary`, since the type
   is not in a game registry.

## Complete example

- [MPFX/MPFXProfile.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXProfile.cs), [MPFX/Serializer.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Serializer.cs), [MPFX/Attributes.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Attributes.cs)

## Variations and gotchas

- **MPFX does not save anything.** Its README says settings are lost when the game closes; the Profiles tab says new
  profiles "will NOT be saved" and must be added to an asset XML by hand. A `ProfileSaveDirectory` property exists but
  nothing uses it. Persisting is a different problem ([Add an XML element with KxAsset and inject child elements with KxAssetInject](register-custom-xml-asset-type-1.md) only gets the type loaded).
- **`float4x4` serialises as `M11`..`M44` attributes**, because its row fields are `[XmlIgnore]` and the indexed members are
  `[XmlAttribute]`; check how your struct fields serialise before relying on the default format.
- **Duplicates are first-wins** by `Id` in the static set; a second profile with the same `Id` is ignored.
- **A plain `new XmlSerializer(typeof(T))` for the export** works here because the export only needs the profile's own fields;
  loading goes through the game's overridden serializers.
- **KX required.** Without KittenExtensions the `<MPFXProfile>` element is an unknown element in `Assets` and is ignored.

## See also

- [Add an XML element with KxAsset and inject child elements with KxAssetInject](register-custom-xml-asset-type-1.md), [Ship shader asset elements through KxAsset and a copied injector](register-custom-xml-asset-type-2.md), [Know which asset XML elements the game accepts and what they load](../asset-xml-element-reference/overview.md),
  [Declare a dependency on another StarMap mod, required or optional](../../../getting-started/dependencies/declare-a-mod-dependency/overview.md)
- [Effect profiles as XML assets with clipboard export](../../../../mods/mpfx/features/xml-effect-profiles.md)
