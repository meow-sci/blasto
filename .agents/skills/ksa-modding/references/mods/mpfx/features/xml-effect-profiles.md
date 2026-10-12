---
type: Mod Feature
title: Effect profiles as XML assets with clipboard export
description: MPFXProfile is a KittenExtensions KxAsset type, so profiles load from any mod's asset XML, while profiles created in game live only in memory and the Export tab serializes the current one with XmlSerializer, DefaultValue and ShouldSerialize trimming, for the user to paste into an asset file by hand
mod: mpfx
category: persistence
techniques: [xml-asset, imgui-window]
hooks: [xml-asset MPFXProfile (KittenExtensions KxAsset), starmap StarMapAfterGui]
lifecycle: [startup:asset-load, frame:draw-ui]
game_types: [KSA.SerializedId, KSA.IKeyed, KSA.ModLibrary, Brutal.Numerics.float4x4, Brutal.ImGuiApi.ImGui]
tags: [persistence, xml, xmlserializer, kittenextensions, profiles, clipboard, imgui]
---

MPFX does not save anything. The README says so plainly: "Currently all settings are lost when closing
the game", and the Profiles tab shows in red that new profiles "will NOT be saved" and "need to be
manually added to an asset xml file".[^mpfx-readme] [^mpfx-main] What it does have is a profile type
that loads from XML, so it can ship presets as data, plus an export that prints the current settings as
that XML. A `ProfileSaveDirectory` property points at `mods/MPFX/Profiles`, but nothing in the repo uses
it. It looks like the start of the "profiles in the next release" that the README mentions.

## How it works

1. **Profile as an asset type.** `MPFXProfile : SerializedId, IKeyed` carries
   `[KxAsset("MPFXProfile")]`, using MPFX's own copy of the KittenExtensions attribute
   ([MPFX/Attributes.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Attributes.cs#L9-L16)). KittenExtensions therefore teaches `AssetBundle.Assets` a
   `<MPFXProfile Id="...">` element. Any mod's asset file can declare one, but MPFX's own
   `MPFXAssests.xml` ships none.[^mpfx-attributes] [^mpfx-assets]
2. **Registration.** `OnDataLoad` adds the instance to the static `HashSet<MPFXProfile> Profiles` unless
   one with the same `Id` exists. `Equals` and `GetHashCode` use `Id`
   ([MPFX/MPFXProfile.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXProfile.cs#L217-L246)). `Populate` and `ToRow` throw `NotImplementedException`.
   That is enough for a data-only `SerializedId` that is never listed in a game table.[^mpfx-profile] [^ksa-serializedid]
3. **Every setting is a public field.** The pre/post enable flags, the floats, two `float4x4` color
   balance matrices, `float4` colors and a `double` blur radius are all public fields. Runtime-only film
   grain clocks are `[XmlIgnore]`.
4. **Trimmed export.** Scalars carry `[DefaultValue(...)]`, and struct fields have
   `ShouldSerialize<Name>()` methods comparing against the default. .NET `XmlSerializer` omits members
   equal to their `DefaultValue` and calls `ShouldSerializeX` to decide on the rest, so an exported
   profile contains only what the user changed. `float4x4` serializes as `M11`..`M44` XML attributes,
   because its row fields are `[XmlIgnore]` and its `M11`-style properties are `[XmlAttribute]`
   ([current/decomp/Brutal.Numerics/float4x4.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Numerics/float4x4.cs#L69-L80),
   [current/decomp/Brutal.Numerics/float4x4.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Numerics/float4x4.cs#L158-L169)).[^ksa-float4x4]
5. **Serializer helper.** `Serializer.SerializeToXml<T>` uses an indented UTF-8 writer without an XML
   declaration and with empty namespaces, so no `xsi`/`xsd` attributes appear. A `StringWriter`
   subclass overrides `Encoding` to UTF-8 ([MPFX/Serializer.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Serializer.cs#L11-L44)). `DeserializeFromXml<T>`
   exists but is unused.[^mpfx-serializer]
6. **Profiles tab.** An `InputText` over a 256-byte UTF-8 buffer edits the id. "Apply" renames the
   current profile. "Create" calls `GetOrCreateProfile` and `ApplyProfile`. A table lists every profile
   as a select button, disabled for the current one, plus a "Duplicate" button that copies all fields
   into `new MPFXProfile(item, item.Id + "1")` ([MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L255-L306)).
7. **Export tab.** Each frame it serializes `CurrentProfile`, shows the XML with `ImGui.Text` in a child
   region, and offers "Copy to clipboard" through `ImGui.SetClipboardText`
   ([MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L1229-L1244)).[^imgui-api]

## Lifecycle and timing

- **`startup:asset-load`.** `<MPFXProfile>` elements deserialize with the other asset bundles, and
  `OnDataLoad(Mod)` runs during `ModLibrary.LoadAll`. A built-in `Default` profile is created lazily on
  first access to `MPFX.CurrentProfile`.
- **`frame:draw-ui`.** Profile switching, duplication and export all happen inside the
  `[StarMapAfterGui]` window code. The export re-runs `XmlSerializer` on every frame while the tab is
  open, which is wasteful but harmless at this size.
- No save or quit hook exists. There is nothing to restore at the next start, so the default profile is
  active on every launch.

## Key code

| Path | Role |
|---|---|
| [MPFX/MPFXProfile.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXProfile.cs) | Asset type, defaults, `ShouldSerialize` methods, registry, copy constructor |
| [MPFX/Serializer.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Serializer.cs) | Namespace-free UTF-8 XML serialization |
| [MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L255-L306) | Profiles tab |
| [MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L1229-L1244) | Export tab |

## Snippets

```csharp
// MPFX/MPFXProfile.cs L12-L49
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

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `KSA.SerializedId` (`Id`, `OnDataLoad`, `Populate`, `ToRow`) | [current/decomp/KSA/SerializedId.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedId.cs) | Base class of the profile asset |
| `ModLibrary.LocalModsFolderPath` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L178) | Base of the unused `ProfileSaveDirectory` (`Documents/.../mods/MPFX/Profiles`)[^ksa-modlibrary] |
| `ImGui.SetClipboardText(ImString)` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8299) | Export to clipboard |
| `ImGui.InputText(ImString, ReadOnlySpan<byte>, ...)` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L6880) | Profile id field over a fixed byte buffer |

## Pitfalls and version notes

- The copy constructor checks `Profiles.Any(p => p.Id == this.Id)` before `this.Id` is assigned, so the
  collision loop never runs ([MPFX/MPFXProfile.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXProfile.cs#L247-L260)). Duplicating the same profile twice
  produces a second `Default1` that the `HashSet` (keyed by `Id`) silently refuses to add, but it still
  becomes the current profile.
- "Apply" renames the current profile in place while it is inside a `HashSet` whose hash is `Id`. That
  breaks the set's invariants for that entry.
- Switching profiles does not raise `UpdateValues`. See
  [Per-frame effect parameter upload](per-frame-parameter-upload.md).
- To persist presets today, paste the exported XML into an `<Assets>` file that some mod lists under
  `assets` in its `mod.toml`. The `<MPFXProfile>` element resolves once MPFX's assembly is loaded.

## See also

- [Effect settings window and ModMenu entry](effect-settings-window.md)
- [Custom XML asset types via KxAsset attributes](../../kittenextensions/features/custom-xml-asset-types.md)
- [Ship a data-only profile type that loads from ordinary asset XML](../../../howto/assets/asset-xml/register-custom-xml-asset-type/register-custom-xml-asset-type-3.md) - how-to article demonstrating this feature
- [Export effect presets as trimmed XML and load them back as a mod's own asset type](../../../howto/persistence/user-data/persist-user-created-presets-and-content/persist-user-created-presets-and-content-4.md) - how-to article demonstrating this feature

[^mpfx-readme]: MPFX README.md
[^mpfx-main]: MPFX/MPFX.cs
[^mpfx-attributes]: MPFX/Attributes.cs
[^mpfx-assets]: MPFX/MPFXAssests.xml
[^mpfx-profile]: MPFX/MPFXProfile.cs
[^ksa-serializedid]: KSA.SerializedId (decompiled, build 2026.10.10.5554)
[^ksa-float4x4]: Brutal.Numerics.float4x4 (decompiled, build 2026.10.10.5554)
[^mpfx-serializer]: MPFX/Serializer.cs
[^imgui-api]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
