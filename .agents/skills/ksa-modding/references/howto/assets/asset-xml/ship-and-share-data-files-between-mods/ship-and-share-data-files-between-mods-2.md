---
type: How-To
title: Let other mods add data with a marker key in JSON files scanned across mod folders
description: Scan every folder beside your own mod for JSON files whose root carries a marker key, load your own files first and sort the rest, record which keys came from other mods and resolve lookups from specific to generic
category: assets
group: asset-xml
topic: ship-and-share-data-files-between-mods
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/compendium/features/cross-mod-json-catalog]
techniques: [json-config, convention-folder-scan]
hooks: [starmap StarMapImmediateLoad, starmap StarMapAfterGui]
lifecycle: [startup:immediate-load, frame:draw-ui]
game_types: [KSA.Mod, KSA.ModLibrary, KSA.ModManifest, KSA.ModEntry]
tags: [json, marker-key, cross-mod, data-files, overrides, folder-scan]
---

Compendium keeps every description, factoid and category of its celestial info window in 25 JSON files, about 3,600 entries.
Its loader does not look only in its own folder: it scans every folder beside its own (the mods root it was installed in), so
another mod can ship a JSON file with a marker key and add bodies for its own star system or override Compendium's text, with
no reference to Compendium's assembly. This article shows that scan, the marker, the precedence rules and the lookup order.

Demonstrated by [Cross-mod JSON catalog](../../../../mods/compendium/features/cross-mod-json-catalog.md).

## Background: how the game does it

The game has no data-contribution mechanism; mods are folders. StarMap resolves a mod either under the game's `Content` folder
or under the user mods folder, so the parent of a mod's own folder is one of those two roots
([StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L40-L47), [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L176-L180)). Compendium therefore
treats "the parent of my folder" as the shared data space, skips the game's own `Core`, `Shaders` and `Versions` folders, and
requires a marker so unrelated JSON (other mods' settings) is ignored.

## Lifecycle and timing

`startup:immediate-load`. `[StarMapImmediateLoad]` is invoked from StarMap's prefix on `Mod.PrepareSystems`, which
`ModLibrary.PrepareAll` calls for each enabled mod in the `Program` constructor. The scan is plain file IO on the main thread, so
other mods' files are on disk regardless of their load order; there is no dependency on their code having run. Bodies and
systems do not exist yet, so matching keys to bodies waits for the first `[StarMapAfterGui]` frame (`frame:draw-ui`). See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
and [Choose and use the right StarMap lifecycle hook](../../../getting-started/lifecycle/choose-a-starmap-startup-hook/overview.md).

## Steps

1. **Define the marker and the file format.** A file is used only if its root object has `"CompendiumJson": "True"`. Every other
   root key is a prefix (`Compendium` for the Sol defaults, a game system id, or a star-system root id). Under a prefix each key is
   a body id mapping to an entry, plus an optional `ListGroupsData` for category text. This is a complete file for a second star
   system:

   ```jsonc
   // src/Compendium/AlphaCentauri.json L1-L14
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
   ```

2. **Find the data space from your own assembly.**

   ```csharp
   // src/CompendiumMain.cs L1235-L1255
           [StarMapImmediateLoad]
           public void OnImmediateLoad(Mod mod)
           {
               try
               {
                   // Gets the current working directory path (of the DLL)
                   dllDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                   LoadSettings();

                   // Enable orbit visibility and root navigation targeting patches.
                   Patcher.Patch();

                   // Loads fonts from the Fonts folder
                   LoadFonts(dllDir, fontSizeCurrent);

                   if (dllDir != null && Directory.Exists(dllDir))
                   {
                       // Load celestial body descriptions from JSON files - pass dllDir since LoadCompendiumJsonData will search subdirectories
                       LoadCompendiumJsonData(dllDir);
                   }
           // ...

   ```

3. **Collect every directory below the mods root that contains JSON, skipping the game's folders:**

   ```csharp
   // src/CdmLoadJsonInfo.cs L72-L113
           public void LoadCompendiumJsonData(string dataDir)
           {
               var options = new JsonSerializerOptions
               {
                   PropertyNameCaseInsensitive = true
               };

               try
               {
                   // Get parent directory of the DLL folder
                   parentDir = Directory.GetParent(dataDir)?.FullName ?? dataDir;
                   Console.WriteLine($"Compendium: Looking for Compendium JSON data - in folders of: {parentDir}");

                   // Get all subdirectories in parent folder
                   var allSubdirs = Directory.GetDirectories(parentDir, "*", SearchOption.AllDirectories).ToList();

                   // Filter out excluded folders at the parent level and only include directories with JSON files
                   var excludedFolders = new[] { "Core", "Shaders", "Versions" };
                   var searchDirs = new List<string>();

                   // Check parent directory for JSON files
                   if (Directory.GetFiles(parentDir, "*.json", SearchOption.TopDirectoryOnly).Length > 0)
                   {
                       searchDirs.Add(parentDir);
                   }

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
           // ...
   ```

4. **Order the files deterministically: own files first, then everyone else sorted by path.** Later writes replace earlier ones, so
   other mods override your defaults, and the result does not depend on directory listing order:

   ```csharp
   // src/CdmLoadJsonInfo.cs L122-L129
                   // This mod's own files are defaults: load them first so other mods' files deterministically override them
                   string defaultDir = Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                   bool IsDefaultFile(string path) => Path.GetFullPath(path).StartsWith(defaultDir, StringComparison.OrdinalIgnoreCase);
                   jsonFiles = jsonFiles
                       .OrderBy(path => IsDefaultFile(path) ? 0 : 1)
                       .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                       .ToArray();
                   overrideJsonKeys.Clear();
   ```

5. **Check the marker per file and ignore the rest:**

   ```csharp
   // src/CdmLoadJsonInfo.cs L136-L152
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
                           // ...


   ```

6. **Record overrides.** Every key that came from a file outside your own folder is added to an `overrideJsonKeys` set, so a lookup
   can prefer an override over a default, and each entry is deserialized inside its own `try` so one bad entry never drops the
   file.

7. **Look up from specific to generic.** `GetBodyJsonLookupKeys` tries `<rootId>.<path>`, `<rootId>.<id>`, `<systemId>.<path>`,
   `Compendium.<path>`, `<systemId>.<id>`, `Compendium.<id>`. The first key that is an override wins immediately; otherwise the first
   default found is used. See [src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs).

8. **Document the contract.** Compendium's contributor notes spell out the precedence rules so other modders can rely on them
   ([.github/copilot-instructions.md](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/.github/copilot-instructions.md)).

## Complete example

- [src/CdmLoadJsonInfo.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmLoadJsonInfo.cs), [src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs), [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L1235-L1265)
- A contributed file: [src/Compendium/AlphaCentauri.json](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Compendium/AlphaCentauri.json)

## Variations and gotchas

- **Marker, not file name.** A file is a contribution only if its root has the marker; this keeps the scan from reading
  other mods' config JSON and lets contributors name files freely.
- **The scan is recursive and wide.** It reads every sub-folder of the mods root that holds JSON; keep the exclusion list
  current (`Core`, `Shaders`, `Versions`) and make parse failures per file and per entry, with a single error entry instead of a
  throw if the whole scan fails.
- **Disabled mods still contribute.** The scan is by folder, not by manifest, so a disabled mod's JSON is read. Check
  `ModLibrary.Manifest` if that is not wanted (approach [Read a convention folder inside every installed mod with a strict reader](ship-and-share-data-files-between-mods-3.md) does).
- **Culture.** Use `JsonSerializerOptions.PropertyNameCaseInsensitive = true` as Compendium does, and invariant culture for any
  numbers you parse yourself.
- **Keep keys per star system.** Prefix by system so two mods that both add a body called `Bravo` do not collide
  ([Cache and key per-body data so it survives system reloads, repeated ids and extra star systems](../../../celestials/systems-and-stars/cache-and-key-body-data-per-loaded-system/overview.md)).

## See also

- [Ship data beside the DLL with an explicit copy list and resolve the mod root from the assembly](ship-and-share-data-files-between-mods-1.md), [Read a convention folder inside every installed mod with a strict reader](ship-and-share-data-files-between-mods-3.md), [Choose and use the right StarMap lifecycle hook](../../../getting-started/lifecycle/choose-a-starmap-startup-hook/overview.md)
- [Cross-mod JSON catalog](../../../../mods/compendium/features/cross-mod-json-catalog.md)
