---
type: How-To
title: Feed the registry from a KSArmory folder in every installed mod
description: Scan a convention folder in every manifest mod for XML definitions, read them with a strict schema-gated reader and audit them against the part library after load
category: vehicles-and-parts
group: craft-tracking
topic: give-inert-parts-behaviour-by-part-id
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/weapon-packs-from-mod-folders]
techniques: [convention-folder-scan, strict-xml-reader]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded, startup:asset-load]
game_types: [KSA.Part, KSA.PartTemplate, KSA.PartTree, KSA.Vehicle, KSA.ModLibrary, KSA.ModEntry]
tags: [packs, registry, manifest, audit, xml]
---
Outcome: other mods add parts as data. A weapon pack is an ordinary KSA content mod plus a `KSArmory/` folder of XML that KSA never reads; KSArmory keeps no list of packs, and at startup looks in that one folder inside **every** mod KSA knows about and registers what it finds.
Installing a pack is installing a mod, and uninstalling removes its content.

Prerequisite: StarMap. Demonstrated by [ksarmory/weapon-packs-from-mod-folders](../../../../mods/ksarmory/features/weapon-packs-from-mod-folders.md); the registry it feeds is [article 1](give-inert-parts-behaviour-by-part-id-1.md).

## Background: how the game does it

- `ModLibrary.Manifest` is the parsed `manifest.toml` (`ModManifest.Mods`, a `List<ModEntry>` with `Id` and `Enabled`). A disabled entry is not in the library at all, so `ModLibrary.Find(id)` cannot find its folder; the fallback is `Path.Combine(ModLibrary.LocalModsFolderPath, id)`.
- A newly discovered mod is written into the manifest with `Enabled = false`, so a pack that "does nothing" is often just disabled.
- Part templates are registered by the time of `ModLibrary.LoadAll`; their game data is merged later in `ModLibrary.AttachGameData`.
- **`ModLibrary.Get<PartTemplate>` throws on a miss; `Has<T>` / `TryGet<T>` have no `PartTemplate` branch in this build and answer false for every part.** A probe must use `Get` inside a `try`.

## Lifecycle and timing

`InstalledPacks.RegisterAll()` is the first call in KSArmory's `[StarMapAllModsLoaded]` method, followed by `Catalogue.Freeze()` and the audit. StarMap fires that attribute from a Harmony postfix on `ModLibrary.LoadAll`, after every mod's `mod.toml` assets were loaded, so the manifest is parsed, enabled
mods have directories and `PartTemplate`s exist (`startup:all-mods-loaded`; see [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)). Code packs that call `Armoury.Register` themselves did so earlier from their own entry points. Nothing here touches the universe or a vehicle; it is file IO and pure parsing, once at startup.

```csharp
// src/KSArmory/Ksa/KSArmoryMod.cs L149-L167
[StarMapAllModsLoaded]
public void OnFullyLoaded()
{
    // Every pack that was going to call Armoury.Register has done so by now: StarMap runs a
    // dependent mod's entry point before this hook, which is a postfix on ModLibrary.LoadAll.
    // Shutting the door here is what stops a registry growing under a crewed system.
    // Asset-only packs, found by convention in every mod's folder. A pack that ships code has
    // already called Armoury.Register from its own entry point by now; this is for the ones
    // with no assembly at all, which is most of them.
    InstalledPacks.RegisterAll();

    JitWarmup.Start();

    Catalogue.Freeze();
    ReportRegistrations();

    // Only now can this be asked. A pack registers before KSA has loaded a single asset
    // bundle, so at that point there is no part in the world to check a profile against.
    foreach (PackFault fault in Catalogue.Audit(new DeclaredParts())) Log.Warn(fault.ToString());
```

## Steps

1. **Fix a convention folder and file pattern.** The folder is named after your mod, inside the other mod; several files are allowed.

```csharp
// src/KSArmory/Sim/PackScan.cs L35-L45
    /// <summary>The folder inside another mod that KSArmory reads.</summary>
    public const string FolderName = "KSArmory";

    /// <summary>Definition files within it. Several are allowed; one per weapon is fine.</summary>
    public const string FilePattern = "*.xml";

    public static PackAvailability Of(bool enabled, int definitionFiles)
        => definitionFiles <= 0 ? PackAvailability.NothingToRead
           : enabled ? PackAvailability.Ready
           : PackAvailability.Disabled;
}
```

2. **Enumerate every installed mod, find its folder, classify.** Warn about a disabled pack rather than skipping silently:

```csharp
// src/KSArmory/Ksa/InstalledPacks.cs L23-L57
public static void RegisterAll()
{
    foreach (ModEntry entry in Installed())
    {
        string id = entry.Id;
        if (string.IsNullOrEmpty(id)) continue;

        string[] files;
        try
        {
            string folder = Path.Combine(FolderOf(entry), PackScan.FolderName);
            files = Directory.Exists(folder)
                        ? Directory.GetFiles(folder, PackScan.FilePattern)
                        : [];
        }
        catch (IOException e)
        {
            Log.Warn($"pack '{id}': cannot be read - {e.Message}");
            continue;
        }

        switch (PackScan.Of(entry.Enabled, files.Length))
        {
            case PackAvailability.NothingToRead:
                continue;

            case PackAvailability.Disabled:
                Log.Warn($"pack '{id}' carries definitions but the mod is disabled in manifest.toml, "
                         + "so its parts are not loaded and nothing in it is registered");
                continue;
        }

        foreach (string file in files) Read(id, file);
    }
}
```

```csharp
// src/KSArmory/Ksa/InstalledPacks.cs L84-L110
private static IEnumerable<ModEntry> Installed()
{
    try
    {
        return ModLibrary.Manifest?.Mods ?? [];
    }
    catch
    {
        return [];
    }
}

// A disabled mod is not in the library at all, and it is the one this most needs to find -- so
// the manifest entry's own folder is the fallback rather than the answer of last resort.
private static string FolderOf(ModEntry entry)
{
    try
    {
        if (ModLibrary.Find(entry.Id) is { DirectoryPath: { Length: > 0 } path }) return path;

        return Path.Combine(ModLibrary.LocalModsFolderPath, entry.Id);
    }
    catch
    {
        return "";
    }
}
```

3. **Parse strictly, text in and profiles or faults out.** Require a schema attribute, consume attributes as you read them and refuse leftovers, so a typo is reported instead of silently flying on the default. Use invariant culture for numbers, namespace keys by pack (`MyPack:AIM-9X`) so two packs never overwrite
   each other, and let one bad definition cost only itself:

```csharp
// src/KSArmory/Sim/PackReader.cs L497-L514
public bool Sound()
{
    foreach (XAttribute a in element.Attributes())
    {
        if (_taken.Contains(a.Name.LocalName)) continue;

        Fault($"'{a.Name.LocalName}' is not an attribute this build knows");
    }

    foreach (XElement child in element.Elements())
    {
        if (_taken.Contains(child.Name.LocalName)) continue;

        Fault($"<{child.Name.LocalName}> is not something a <{kind}> may contain");
    }

    return _sound;
}
```

4. **Freeze, then audit against the part library.** After `Freeze()`, check that every launcher and optic Id exists as a `PartTemplate` and every subpart marker matches exactly one subpart Id:

```csharp
// src/KSArmory/Ksa/DeclaredParts.cs L18-L49
public sealed class DeclaredParts : IPartCatalogue
{
    public bool Declares(string partId) => Template(partId) is not null;

    public IReadOnlyList<string> SubPartIdsOf(string partId)
    {
        if (Template(partId) is not { } part) return [];

        List<string> ids = [];
        foreach (PartInstance sub in part.SubPartInstances)
        {
            if (sub.Id is { Length: > 0 } id) ids.Add(id);
        }

        return ids;
    }

    private static PartTemplate? Template(string partId)
    {
        if (string.IsNullOrEmpty(partId)) return null;

        try
        {
            return ModLibrary.Get<PartTemplate>(partId);
        }
        catch
        {
            // A miss, which is the answer the audit wants -- and anything else the library does
            // with a bad Id, since this is a report and must not cost a frame.
            return null;
        }
    }
```

5. **Ship the pack.** Parts, meshes and textures go in the pack's own `mod.toml` `assets`; the XML goes in `KSArmory/` and must **not** be listed in `assets`, because KSA would hand it to its asset-bundle loader and parse it with the wrong reader.

## Complete example

- [src/KSArmory/Ksa/InstalledPacks.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/InstalledPacks.cs#L1-L110)[^ksarmory-installedpacks], [src/KSArmory/Sim/PackScan.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/PackScan.cs#L1-L45)[^ksarmory-packscan] and [src/KSArmory/Ksa/DeclaredParts.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/DeclaredParts.cs#L1-L50)[^ksarmory-declaredparts].
- KSArmory's own Pantsir ships the same way: [src/KSArmory/KSArmory/Weapons.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmory/Weapons.xml#L75-L76)[^ksarmory-weapons] is the 1RS1 sensor element.

## Variations and gotchas

- **A pack mod must be enabled** in `manifest.toml`; KSA itself says nothing about a disabled one.
- **Markers resolve by case-insensitive substring, first hit wins**; the audit flags zero or several matches because reordering subparts would otherwise change which assembly moves.
- **Part Ids must agree** between KSA's `Assets.xml` and the pack's launcher `PartId`; there is no other link.
- **`Has<PartTemplate>` / `TryGet<PartTemplate>` returning false** is a property of this build's `ModLibrary`; re-check on updates.
- **A later mod's `<Body>` entry replaces an earlier one's** (a second root type read from the same folder) and logs that it did.

## See also

- [Overview](overview.md); [Article 1](give-inert-parts-behaviour-by-part-id-1.md)
- [ksarmory/weapon-packs-from-mod-folders](../../../../mods/ksarmory/features/weapon-packs-from-mod-folders.md)
- [Load, validate and unload part mods in a running game](../../parts/load-and-unload-part-mods-at-runtime/overview.md)

[^ksarmory-installedpacks]: KSArmory: src/KSArmory/Ksa/InstalledPacks.cs
[^ksarmory-packscan]: KSArmory: src/KSArmory/Sim/PackScan.cs
[^ksarmory-declaredparts]: KSArmory: src/KSArmory/Ksa/DeclaredParts.cs
[^ksarmory-weapons]: KSArmory: src/KSArmory/KSArmory/Weapons.xml
