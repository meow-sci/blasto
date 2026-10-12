---
type: How-To
title: Export effect presets as trimmed XML and load them back as a mod's own asset type
description: Define a preset as a data-only SerializedId asset with public fields, register it in OnDataLoad so any mod's asset XML can ship one, and export the live settings through XmlSerializer trimmed by DefaultValue and ShouldSerialize methods so the pasted XML contains only what the player changed
category: persistence
group: user-data
topic: persist-user-created-presets-and-content
approach: 4
difficulty: advanced
prerequisites: [starmap, kittenextensions]
demonstrated_by: [mods/mpfx/features/xml-effect-profiles]
techniques: [xml-asset, imgui-window]
hooks: [kittenextensions KxAsset MPFXProfile, starmap StarMapAfterGui]
lifecycle: [startup:asset-load, frame:draw-ui]
game_types: [KSA.SerializedId, KSA.IKeyed, KSA.ModLibrary, Brutal.ImGuiApi.ImGui]
tags: [presets, xml, xmlserializer, profiles, export, clipboard, defaultvalue, shouldserialize]
---
Outcome: effect presets that ship as data, plus an export button that turns the current in-game settings into the same XML. MPFX does not persist anything by itself: its README says settings are lost when the game closes, and the Profiles tab tells the player in red that new profiles "will NOT be saved" and "need to be manually added to an asset xml file". What it demonstrates is the preset half of persistence: a profile type that loads from XML, and an exporter whose output stays short enough to read and paste. Prerequisites: StarMap and KittenExtensions (for the `KxAsset` attribute). Demonstrated by [Profiles as XML assets with a trimmed export](../../../../mods/mpfx/features/xml-effect-profiles.md). How `KxAsset` teaches the game's XML reader a new element is the subject of [Ship a data-only profile type that loads from ordinary asset XML](../../../assets/asset-xml/register-custom-xml-asset-type/register-custom-xml-asset-type-3.md); this article follows the profile through persistence.

## Background: how the game does it

Game data is XML read into `SerializedId` subclasses. `SerializedId` carries the `Id` attribute and a virtual `OnDataLoad(Mod)`, which the loader calls for every deserialized asset, so a type can register itself when its XML is read ([current/decomp/KSA/SerializedId.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedId.cs#L6-L31)). `IKeyed` adds `Id`, `Hash` and `LookupIndex` ([current/decomp/KSA/IKeyed.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IKeyed.cs)). The game has no preset-saving mechanism for mods, so the write side is the mod's own: MPFX derives a never-used `ProfileSaveDirectory` from `ModLibrary.LocalModsFolderPath` (`<docs>/mods`, [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L178)) and exports through `XmlSerializer` instead.

## Lifecycle and timing

| Moment | What | Notes |
|---|---|---|
| `startup:asset-load` | `<MPFXProfile>` elements deserialize with the other asset bundles; `OnDataLoad(Mod)` adds each to the static set | Runs during `ModLibrary.LoadAll`, before the scene exists; the built-in `Default` profile is created lazily on first access to `MPFX.CurrentProfile` |
| `frame:draw-ui` | Profiles tab (create, apply, duplicate) and Export tab, inside the mod's `[StarMapAfterGui]` window | Export re-runs `XmlSerializer` every frame while the tab is open: harmless at this size, wasteful if you copy it |

There is no save or quit hook, so every launch starts with the default profile. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Declare the preset as an asset type.** MPFX carries its own copy of the `KxAsset` attribute declarations (an `internal` attribute class whose name KittenExtensions matches), and the profile derives from `SerializedId` and `IKeyed`:

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
   ```

2. **Make every setting a public field.** `XmlSerializer` writes public fields and read/write properties, so the field list is the schema. Add `[DefaultValue(...)]` to scalars so default values are omitted, and a `ShouldSerialize<Name>()` method for structs that `DefaultValue` cannot describe:

   ```csharp
   // MPFX/MPFXProfile.cs L33-L49, L68-L75, L113-L121
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

   [DefaultValue(false)]
   public bool ColorTempPreImgui = false;
   [DefaultValue(6500f)]
   public float ColorTempFloatPreImgui = 6500f; // neutral
   [DefaultValue(false)]
   public bool ColorTempPostImgui = false;
   [DefaultValue(6500f)]
   public float ColorTempFloatPostImgui = 6500f;

   [DefaultValue(false)]
   public bool ColorOverlayPreImgui = false;
   public float4 ColorOverlayFloatPreImgui = float4.Zero;
   public bool ShouldSerializeColorOverlayFloatPreImgui() => ColorOverlayFloatPreImgui != float4.Zero;

   [DefaultValue(false)]
   public bool ColorOverlayPostImgui = false;
   public float4 ColorOverlayFloatPostImgui = float4.Zero;
   public bool ShouldSerializeColorOverlayFloatPostImgui() => ColorOverlayFloatPostImgui != float4.Zero;
   ```

   `float4x4` serializes as `M11`..`M44` attributes, because its row fields are `[XmlIgnore]` and its `M11`-style properties are `[XmlAttribute]` ([current/decomp/Brutal.Numerics/float4x4.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Numerics/float4x4.cs#L69-L80) for the row fields and [current/decomp/Brutal.Numerics/float4x4.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Numerics/float4x4.cs#L158-L169) for the attribute properties).

3. **Register on load.** `OnDataLoad` adds the instance to a static set unless one with the same `Id` exists; `Equals` and `GetHashCode` use `Id`. `Populate` and `ToRow` throw `NotImplementedException`, which is acceptable for a data-only type that is never listed in a game table:

   ```csharp
   // MPFX/MPFXProfile.cs L217-L246
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

   public MPFXProfile() { }
   private MPFXProfile(string id)
   {
       this.Id = id;
       if (!Profiles.Any(p => p.Id == this.Id)) Profiles.Add(this);
   }
   ```

4. **Serialize without the noise.** A helper that drops the XML declaration, the `xsi`/`xsd` namespaces and the UTF-16 default produces text that reads like hand-written asset XML:

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

5. **Offer the export in the UI.** The Export tab shows the live XML and copies it with `ImGui.SetClipboardText` ([current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8299)):

   ```csharp
   // MPFX/MPFX.cs L1229-L1244
   case WindowTabs.ExportProfile:
       ImGui.Text("Current Profile XML:");
       ImGui.Separator();

       string xml = Serializer.SerializeToXml(CurrentProfile);

       ImGui.BeginChild("ExportProfileChild", new float2(0f, -32f), ImGuiChildFlags.AlwaysAutoResize);
       ImGui.Text(xml);
       ImGui.EndChild();

       if (ImGui.Button("Copy to clipboard"))
       {
           ImGui.SetClipboardText(xml);
       }

       break;
   ```

6. **Close the loop by hand.** To make an exported preset permanent, paste it into an `<Assets>` file that some mod lists under `assets` in its `mod.toml`; the `<MPFXProfile>` element then resolves once MPFX's assembly is loaded and the profile appears in the Profiles tab at the next launch. Say so in the UI: MPFX prints the instruction in red under the profile list:

   ```csharp
   // MPFX/MPFX.cs L304-L305
   ImGui.TextColored(Color.Red, "Warning: new profiles will NOT be saved.");
   ImGui.TextColored(Color.Red, "They need to be manually added to an asset xml file.");
   ```

## Complete example

| File | Role |
|---|---|
| [MPFX/MPFXProfile.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXProfile.cs) | Asset type, defaults, `ShouldSerialize` methods, registry, copy constructor |
| [MPFX/Serializer.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Serializer.cs) | Namespace-free UTF-8 XML serialization |
| [MPFX/Attributes.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Attributes.cs) | `KxAsset` declarations |
| [MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L255-L306) | Profiles tab (create, apply, duplicate) |
| [MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L1229-L1244) | Export tab |

## Variations and gotchas

- **Nothing is written back.** `ProfileSaveDirectory` points at `<docs>/mods/MPFX/Profiles` but nothing in the repository uses it. Treat in-game profiles as session state unless you add the write yourself (the file and atomic-write techniques in [Write config files atomically and load hand-edited ones without losing them](../../settings-formats/write-and-load-config-files-safely/overview.md) apply).
- **The copy constructor never detects a collision.** It checks `Profiles.Any(p => p.Id == this.Id)` before assigning `this.Id`, so the rename loop never runs. Duplicating the same profile twice yields a second `Default1` that the set silently refuses to add but that still becomes the current profile ([MPFX/MPFXProfile.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXProfile.cs#L247-L260)).
- **Renaming in a hash set.** *Apply* renames the current profile in place while it sits in a `HashSet` hashed by `Id`, which breaks the set's invariants for that entry.
- **Switching profiles does not raise a values update** in this version; effects read the new profile on their next upload.
- **Attribute copies.** Because the attribute classes are per-mod `internal` copies matched by name, a renamed attribute in a future KittenExtensions breaks the type registration silently; the profile elements would then be ignored.
- **`ShouldSerialize` must mirror the default.** A struct field whose method compares against a different value than the field initialiser writes a spurious element or drops a real one.

## See also

- Sibling approaches: [Named palettes in a sectioned ini](./persist-user-created-presets-and-content-1.md), [Snake_case JSON course file](./persist-user-created-presets-and-content-5.md), [overview](./overview.md)
- [Profiles as XML assets with a trimmed export](../../../../mods/mpfx/features/xml-effect-profiles.md)
- [Register a custom XML asset type or inject new child elements into game XML](../../../assets/asset-xml/register-custom-xml-asset-type/overview.md), [Ship data files with a mod and let other mods contribute more](../../../assets/asset-xml/ship-and-share-data-files-between-mods/overview.md)
