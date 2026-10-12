---
type: Mod Feature
title: Cross-mod JSON catalog
description: At immediate load Compendium reads every JSON file in the mod folders beside its own that carries the CompendiumJson marker, keys entries by system or root prefix and body path, and lets other mods' entries override its defaults
mod: compendium
category: assets
techniques: [json-config, inter-mod-data]
hooks: [starmap StarMapImmediateLoad, starmap StarMapAfterGui]
lifecycle: [startup:immediate-load, frame:draw-ui]
game_types: [KSA.ModLibrary, KSA.Universe, KSA.CelestialSystem, KSA.Celestial]
tags: [json, data, inter-mod, catalog, celestials, precedence]
---

All of Compendium's descriptive data (category membership, display names, orbit groups, text,
factoids, "visited by" lists, Wikipedia links, category descriptions) lives in 25 JSON files under
`src/Compendium/`, about 3,600 entries in total. The loader does not look only in its own folder: it
scans every folder next to its own (the mods root it was installed in), so another mod in the same
root can ship a JSON file with the marker key and add bodies for its own star system, or override
Compendium's text, without referencing Compendium's assembly.[^cmp-copilot-instructions-md]

## How it works

1. **File format.** A file is used only if its root object has `"CompendiumJson": "True"`. Every other
   root key is a prefix: `Compendium` for the Sol defaults, a game system id, or a star-system root id
   such as `AlphaCentauri`. Under a prefix, each key is a body id (optionally a dotted path) mapping to
   an entry, plus an optional `ListGroupsData` object of category or subgroup descriptions
   ([src/Compendium/AlphaCentauri.json](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Compendium/AlphaCentauri.json#L1-L31), [src/Compendium/Stars.json](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Compendium/Stars.json#L1-L14)).[^cmp-alphacentauri-json]
   [^cmp-stars-json] Entries deserialize case-insensitively into `CompendiumData`; the properties with
   `internal set` are filled later from the game ([src/CdmLoadJsonInfo.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmLoadJsonInfo.cs#L7-L40)).[^cmp-cdmloadjsoninfo]
2. **Where it looks.** `OnImmediateLoad` takes the folder of the executing assembly (StarMap loads the
   entry assembly from its path, so `Assembly.Location` is the mod folder) and calls
   `LoadCompendiumJsonData(dllDir)` ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L1235-L1254),
   [StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L68-L72)).[^cmp-compendiummain] [^starmap-modassemblyloadcontext]
   The loader goes one level up, to the folder that holds all mods, and collects every directory below
   it that contains `*.json`, skipping top-level `Core`, `Shaders` and `Versions`
   ([src/CdmLoadJsonInfo.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmLoadJsonInfo.cs#L81-L121)). StarMap resolves a mod either under the game's `Content`
   folder or under the user mods folder, so the parent is one of those two
   ([StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L38-L46), [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L178)); `Core`, the
   first excluded name, is the game's own content folder under `Content`.[^starmap-runtimemod] [^ksa-modlibrary]
3. **Deterministic precedence.** Files inside Compendium's own folder load first, all others after,
   each group sorted by path, case-insensitively. Later writes replace earlier ones in `bodyJsonDict`,
   and every key that came from another mod's file is also added to `overrideJsonKeys`
   ([src/CdmLoadJsonInfo.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmLoadJsonInfo.cs#L122-L131)).
4. **Parsing.** Each file is parsed with `JsonDocument`; each prefix is deserialized into a dictionary of
   `JsonElement`s; `ListGroupsData` categories are deserialized one by one (non-objects are skipped with
   a log line) and merged into a `"<prefix>.ListGroupsData"` container; each body entry is deserialized
   in its own `try` so one bad entry does not drop the file ([src/CdmLoadJsonInfo.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmLoadJsonInfo.cs#L133-L256)). A
   failure of the whole scan stores a single `ERROR` entry instead of throwing.
5. **Parent hints from file names.** Moon files are named `Moons<Parent>.json`; for those, a bare body
   id is stored as `<prefix>.<Parent>.<id>` (with special cases for the Earth and Mars file) so moons get
   the same dotted-path keys the body tree produces ([src/CdmLoadJsonInfo.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmLoadJsonInfo.cs#L50-L70),
   [src/CdmLoadJsonInfo.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmLoadJsonInfo.cs#L233-L246)).
6. **Lookup order.** For a body, `GetBodyJsonLookupKeys` tries `<rootId>.<path>`, `<rootId>.<id>`,
   `<systemId>.<path>`, `Compendium.<path>`, `<systemId>.<id>`, `Compendium.<id>`. The first key that
   is an override wins immediately; otherwise the first default found is used. Category text uses the
   same idea over the root id, system id and `Compendium` ([src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L199-L233),
   [src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L678-L701)).[^cmp-cdmhelpers]
7. **First-frame aliasing.** Once a universe exists, every celestial of every root gets
   `<systemId>.<path>` and `<systemId>.<id>` aliases to its `Compendium` entry (carrying override status
   along), or an empty placeholder so it still appears under Other
   ([src/CdmAttachInfoBodyJsonData.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmAttachInfoBodyJsonData.cs#L21-L79)).[^cmp-cdmattachinfobodyjsondata]

## Lifecycle and timing

- **`startup:immediate-load`.** `[StarMapImmediateLoad]` is invoked from StarMap's prefix on
  `Mod.PrepareSystems`, which `ModLibrary.PrepareAll` calls for each enabled mod in the `Program`
  constructor ([StarMap.Core/Patches/ModPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModPatches.cs#L11-L22), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L899)).[^starmap-modpatches]
  [^ksa-program] Scanning is plain file IO on the main thread during loading, so other mods' files are
  on disk regardless of their load order; there is no dependency on the other mods' code having run.
- **No universe yet.** Bodies, systems and roots do not exist at this point, so matching keys to
  bodies is postponed to the first `[StarMapAfterGui]` frame after `Universe.WorldSun` is set (see
  [the window](celestial-info-window.md)).
- **Reads during UI.** Lookups during drawing only read the dictionaries, which are not modified after
  that first frame.

## Key code

| Path | Role |
|---|---|
| [src/CdmLoadJsonInfo.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmLoadJsonInfo.cs#L72-L131) | Folder scan, exclusion list, default-first ordering |
| [src/CdmLoadJsonInfo.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmLoadJsonInfo.cs#L133-L256) | Marker check, per-category and per-entry deserialization, override bookkeeping |
| [src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L199-L233) | Lookup keys and precedence |
| [src/CdmAttachInfoBodyJsonData.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmAttachInfoBodyJsonData.cs#L21-L79) | System-key aliases and placeholders |
| [.github/copilot-instructions.md](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/.github/copilot-instructions.md#L3-L11) | The precedence rules written down for contributors |

## Snippets

```jsonc
// src/Compendium/AlphaCentauri.json L1-L21
{
  "CompendiumJson": "True",
  "AlphaCentauri": {
    "ListGroupsData": {
      "Planets": {
        "CatText": "Planets known in the Alpha Centauri system. Both confirmed planets orbit the red dwarf Proxima Centauri, very close in - well inside the distance of Mercury from the Sun.",
        "CatWikipediaUrl": "Proxima_Centauri"
      }
    },
    "ProximaCentaurib": {
      "ListGroups": [
        "Planets"
      ],
      "OrbitLineGroup": "Planets",
      "Text": "Proxima Centauri b is a rocky planet of at least about 1.07 Earth masses orbiting within the habitable zone of Proxima Centauri, with an orbital period of about 11.2 days. Discovered in 2016, it is the closest known exoplanet to the Solar System.",
      "Factoids": [
        "Likely tidally locked to its star.",
        "Receives frequent flares from its active red dwarf host, which may strip any atmosphere."
      ],
      "WikipediaUrl": "Proxima_Centauri_b"
    },
```

```csharp
// src/CdmLoadJsonInfo.cs L98-L128
foreach (var subdir in allSubdirs)
{
    // Get the immediate child folder name under parentDir
    string relativePath = Path.GetRelativePath(parentDir, subdir);
    string topLevelFolder = relativePath.Split(Path.DirectorySeparatorChar)[0];

    // Skip if the top-level folder is in excluded list
    if (!excludedFolders.Contains(topLevelFolder))
    {
        // Only add if directory contains JSON files
        if (Directory.GetFiles(subdir, "*.json", SearchOption.TopDirectoryOnly).Length > 0)
        {
            searchDirs.Add(subdir);
        }
    }
}

// Collect all JSON files from allowed directories
string[] jsonFiles = Array.Empty<string>();
foreach (var dir in searchDirs)
{
    var files = Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly);
    jsonFiles = jsonFiles.Concat(files).ToArray();
}
// This mod's own files are defaults: load them first so other mods' files deterministically override them
string defaultDir = Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
bool IsDefaultFile(string path) => Path.GetFullPath(path).StartsWith(defaultDir, StringComparison.OrdinalIgnoreCase);
jsonFiles = jsonFiles
    .OrderBy(path => IsDefaultFile(path) ? 0 : 1)
    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
    .ToArray();
```

```csharp
// src/CdmLoadJsonInfo.cs L219-L247
foreach (var kvp in mainData)
{
    if (kvp.Key != "ListGroupsData")
    {
        CompendiumData? bodyData;
        try
        {
            bodyData = kvp.Value.Deserialize<CompendiumData>(options);
        }
        catch (Exception bodyEx)
        {
            Console.WriteLine($"Compendium: Skipping '{property.Name}.{kvp.Key}' in {Path.GetFileName(file)}: {bodyEx.Message}");
            continue;
        }
        if (bodyData != null)
        {
            string fullKey = $"{property.Name}.{kvp.Key}";
            string? parentHint = GetParentHintFromJsonFile(file, kvp.Key);

            string storedKey = !string.IsNullOrWhiteSpace(parentHint) && !kvp.Key.Contains('.')
                ? $"{property.Name}.{parentHint}.{kvp.Key}"
                : fullKey;
            bodyJsonDict[storedKey] = bodyData;
            if (!isDefaultFile)
            {
                overrideJsonKeys.Add(storedKey);
            }
        }
    }
```

```csharp
// src/CdmHelpers.cs L199-L233
private static IEnumerable<string> GetBodyJsonLookupKeys(Astronomical celestial)
{
    string currentSystem = Universe.CurrentSystem?.Id ?? "Dummy";
    string celestialPath = GetCelestialPath(celestial);
    string? rootId = GetRootOf(celestial)?.Id;

    // Star-system JSON files use the system root Id as their top-level key
    if (rootId != null)
    {
        yield return $"{rootId}.{celestialPath}";
        yield return $"{rootId}.{celestial.Id}";
    }
    yield return $"{currentSystem}.{celestialPath}";
    yield return $"Compendium.{celestialPath}";
    yield return $"{currentSystem}.{celestial.Id}";
    yield return $"Compendium.{celestial.Id}";
}

private static CompendiumData? GetBodyJsonData(Astronomical celestial)
{
    CompendiumData? defaultData = null;
    foreach (string key in GetBodyJsonLookupKeys(celestial))
    {
        if (bodyJsonDict.TryGetValue(key, out var data))
        {
            if (overrideJsonKeys.Contains(key))
            {
                return data;
            }
            defaultData ??= data;
        }
    }

    return defaultData;
}
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `ModLibrary.LocalModsFolderPath` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L178) | One of the two folders StarMap loads mods from (the scan's parent) |
| `Universe.CurrentSystem.Id`, `Universe.Roots` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L106-L114) | Key prefixes |
| `Celestial.Parent`, `Celestial.Id` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L19) | Dotted paths |

## Pitfalls and version notes

- **Every JSON in every mod folder is read.** Each file is read and parsed in full just to check the
  marker; startup cost grows with the number and size of JSON files across all installed mods.
- **Disabled mods count.** The scan walks folders on disk, not `ModLibrary`'s enabled list, so a
  disabled mod's marked JSON still overrides Compendium's data.
- **Only one mods root is scanned.** StarMap loads mods from the game's `Content` folder and from the
  user mods folder, but the scan only walks the parent of Compendium's own folder. With Compendium in
  the user mods folder, a data mod installed under `Content` is never read, and the reverse.
- **Override is per exact key.** A mod overrides an entry only under a key the lookup tries; a
  different prefix or path style adds a new entry instead of replacing one.
- **No schema validation.** Unknown properties are ignored by `System.Text.Json`; misspelled field
  names silently do nothing.
- **Duplicate ids across files are intentional.** The project's instructions forbid deduplicating ids
  across files or systems.[^cmp-copilot-instructions-md]

## See also

- [Celestial compendium window](celestial-info-window.md)
- [Body statistics derived from live game data](derived-body-properties.md)
- [Star systems and the body tree](star-systems-and-body-tree.md)
- [Let other mods add data with a marker key in JSON files scanned across mod folders](../../../howto/assets/asset-xml/ship-and-share-data-files-between-mods/ship-and-share-data-files-between-mods-2.md) - how-to article demonstrating this feature
- [Prefix-keyed JSON files discovered in every installed mod folder](../../../howto/celestials/systems-and-stars/cache-and-key-body-data-per-loaded-system/cache-and-key-body-data-per-loaded-system-5.md) - how-to article demonstrating this feature
- [StarMapImmediateLoad: register data before assets deserialize](../../../howto/getting-started/lifecycle/choose-a-starmap-startup-hook/choose-a-starmap-startup-hook-2.md) - how-to article demonstrating this feature

[^cmp-copilot-instructions-md]: Compendium: .github/copilot-instructions.md
[^cmp-alphacentauri-json]: Compendium: src/Compendium/AlphaCentauri.json
[^cmp-stars-json]: Compendium: src/Compendium/Stars.json
[^cmp-cdmloadjsoninfo]: Compendium: src/CdmLoadJsonInfo.cs
[^cmp-compendiummain]: Compendium: src/CompendiumMain.cs
[^starmap-modassemblyloadcontext]: StarMap: StarMap.Core/ModRepository/ModAssemblyLoadContext.cs
[^starmap-runtimemod]: StarMap: StarMap.Core/ModRepository/RuntimeMod.cs
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^cmp-cdmhelpers]: Compendium: src/CdmHelpers.cs
[^cmp-cdmattachinfobodyjsondata]: Compendium: src/CdmAttachInfoBodyJsonData.cs
[^starmap-modpatches]: StarMap: StarMap.Core/Patches/ModPatches.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
