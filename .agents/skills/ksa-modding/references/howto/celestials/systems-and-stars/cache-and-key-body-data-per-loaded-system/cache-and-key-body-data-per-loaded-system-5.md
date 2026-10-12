---
type: How-To
title: Prefix-keyed JSON files discovered in every installed mod folder
description: Let any installed mod add or override per-body data by dropping a JSON file with a marker key and system-prefixed body keys beside it, with deterministic precedence and a lookup key order that survives repeated ids
category: celestials
group: systems-and-stars
topic: cache-and-key-body-data-per-loaded-system
approach: 5
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/compendium/features/cross-mod-json-catalog, mods/compendium/features/derived-body-properties]
techniques: [json-config]
hooks: [starmap StarMapImmediateLoad, starmap StarMapAfterGui]
lifecycle: [startup:immediate-load, frame:draw-ui]
game_types: [KSA.Universe, KSA.Celestial, KSA.IIndependentRoot]
tags: [json, catalog, precedence, override, keys, cross-mod]
---
Outcome: another mod in the same mods root can ship a JSON file that adds bodies for its own star system or overrides your text, without referencing your assembly. Sub-step of the cache topic: keying body data so it resolves back to the live object. Demonstrated by Compendium ([Cross-mod JSON catalog](../../../../mods/compendium/features/cross-mod-json-catalog.md), [Derived body properties](../../../../mods/compendium/features/derived-body-properties.md)).

## Background: how the game does it

Mod folders sit next to each other under the game's `Content` folder or the user mods folder, and `[StarMapImmediateLoad]` receives the mod before any systems load. Data keyed by bodies therefore has to use ids only: no body objects exist yet. Ids repeat between systems, so keys carry a system or root prefix and, for moons, a parent path ([approach 4](cache-and-key-body-data-per-loaded-system-4.md)).

## Lifecycle and timing

- `startup:immediate-load`: `[StarMapImmediateLoad]` takes the folder of the executing assembly, goes one level up to the mods folder, and scans every directory below it that holds `*.json`, skipping the top-level `Core`, `Shaders` and `Versions` folders. Plain file IO on the main thread; other mods' files are on disk regardless of their load order.
- `frame:draw-ui`: bodies, systems and roots do not exist until `Universe.LoadSystem`; matching keys to bodies is postponed to the first `[StarMapAfterGui]` frame after `Universe.WorldSun` is set (see [approach 3](cache-and-key-body-data-per-loaded-system-3.md)). Lookups during drawing only read the dictionaries.

See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Define a file format with a marker.** A file counts only if its root has `"CompendiumJson": "True"`; every other root key is a prefix (a system id, a star-root id such as `AlphaCentauri`), under which each key is a body id (optionally dotted) plus an optional `ListGroupsData` object for category descriptions:

```json
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

2. **Scan every folder next to yours,** collecting directories that contain JSON and excluding the game's own content folders:

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

3. **Load defaults first, overrides after:** the mod's own files load first, then all others, each group sorted by path, so another mod's file deterministically replaces a default; every key that came from another mod is also added to an override set (`overrideJsonKeys`).

4. **Check the marker, deserialize each entry in its own `try`** so one bad entry does not drop the file, and merge category descriptions:

```csharp
// src/CdmLoadJsonInfo.cs L133-L150
foreach (var file in jsonFiles)
{
        bool isDefaultFile = IsDefaultFile(file);
        // Read and deserialize each JSON file - only include to bodyJsonDict if the json contains the kvp "CompendiumJson":"True"
        var json = File.ReadAllText(file);
        var jsonDoc = JsonDocument.Parse(json);
        //Console.WriteLine($"Processing JSON file: {Path.GetFileName(file)}");

        // Check for "CompendiumJson": "True" at the root level, otherwise this json file is ignored
        if (jsonDoc.RootElement.TryGetProperty("CompendiumJson", out var compendiumValue) && compendiumValue.GetString() == "True")
        {
           // Console.WriteLine($"Compendium: File has CompendiumJson marker - {Path.GetFileName(file)}");
            // Iterate through all properties at the root level (except CompendiumJson) to find all keys containing body data
            foreach (var property in jsonDoc.RootElement.EnumerateObject())
            {
                // Skip the CompendiumJson marker property
                if (property.Name == "CompendiumJson")
                    continue;
```

5. **Look up with a fixed key order.** For a body, try `<rootId>.<path>`, `<rootId>.<id>`, `<systemId>.<path>`, `Compendium.<path>`, `<systemId>.<id>`, `Compendium.<id>`; the first key that is an override wins immediately, otherwise the first default found is used:

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

6. **Alias on the first frame with a universe:** every celestial of every root gets `<systemId>.<path>` and `<systemId>.<id>` aliases to its default entry (carrying override status along), or an empty placeholder so it still appears under Other ([src/CdmAttachInfoBodyJsonData.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmAttachInfoBodyJsonData.cs#L21-L79)).

## Complete example

| File | Role |
|---|---|
| [src/CdmLoadJsonInfo.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmLoadJsonInfo.cs#L72-L131) | Folder scan, exclusion list, default-first ordering |
| [src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L199-L233) | Lookup keys and precedence |
| [src/Compendium/AlphaCentauri.json](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Compendium/AlphaCentauri.json#L1-L31) | An example file for a second star system |

## Variations and gotchas

- **Moon files** named `Moons<Parent>.json` store a bare body id as `<prefix>.<Parent>.<id>` so moons get the same dotted keys the body tree produces.
- **A failure of the whole scan** stores a single `ERROR` entry instead of throwing.
- **Case:** entries deserialize case-insensitively.
- **Document the precedence for authors,** since there is no tooling to show it.

## See also

- [Overview](overview.md); [approach 4](cache-and-key-body-data-per-loaded-system-4.md)
- [Cross-mod JSON catalog](../../../../mods/compendium/features/cross-mod-json-catalog.md)
- [Ship content and code in one mod](../../../getting-started/loader/ship-content-and-code-in-one-mod/overview.md), [Ship and share data files between mods](../../../assets/asset-xml/ship-and-share-data-files-between-mods/overview.md)
