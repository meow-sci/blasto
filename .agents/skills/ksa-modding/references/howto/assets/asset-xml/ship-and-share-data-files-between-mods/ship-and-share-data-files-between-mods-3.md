---
type: How-To
title: Read a convention folder inside every installed mod with a strict reader
description: Walk the manifest at StarMapAllModsLoaded, read every XML in a KSArmory folder of each mod, refuse unknown attributes by name, namespace keys per pack and audit unresolved parts after load
category: assets
group: asset-xml
topic: ship-and-share-data-files-between-mods
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/weapon-packs-from-mod-folders]
techniques: [convention-folder-scan, xml-asset]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded]
game_types: [KSA.Mod, KSA.ModLibrary, KSA.ModManifest, KSA.ModEntry, KSA.PartTemplate]
tags: [convention-folder, packs, xml, manifest, strict-reader, audit, cross-mod]
---

KSArmory has weapon packs without a registry. A pack is an ordinary KSA content mod (parts, meshes, textures in its `mod.toml`)
plus a `KSArmory/` folder of XML weapon definitions that KSA never reads. KSArmory keeps no list; at startup it looks in that one
folder inside every mod in the manifest and registers whatever it finds, so installing a pack is just installing a mod, and
uninstalling removes its content. The idea is a convention folder as an inter-mod data contract: no code dependency, no
load-order coupling, no registry to edit.

Demonstrated by [Asset-only weapon packs read from a KSArmory folder in every installed mod](../../../../mods/ksarmory/features/weapon-packs-from-mod-folders.md).

## Background: how the game does it

`ModLibrary.Manifest.Mods` is KSA's parsed `manifest.toml` (`ModEntry` with `Id` and `Enabled`); `ModLibrary.Find(id)` returns a
loaded `Mod` with `DirectoryPath`; and `ModLibrary.PrepareAll` skips disabled entries, so a disabled mod is not in the library at
all and its folder must be derived from `LocalModsFolderPath`. KSA writes a newly discovered mod into the manifest with
`Enabled = false` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L328-L371), [current/decomp/KSA/ModEntry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModEntry.cs#L8-L40)).

## Lifecycle and timing

`startup:all-mods-loaded`. `InstalledPacks.RegisterAll()` is the first call in KSArmory's `[StarMapAllModsLoaded]` method, followed
by `Catalogue.Freeze()` and the audit. StarMap fires that attribute from a postfix on `ModLibrary.LoadAll`, after every mod's
`mod.toml` assets were loaded, so the manifest is parsed, enabled mods have directories, and `PartTemplate`s are registered (their
game data is merged later, in `ModLibrary.AttachGameData`; the audit only needs Ids and subpart instances). It is file IO plus pure
parsing, safe on the main thread once at startup. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L155-L170).

## Steps

1. **Pick a folder name and file pattern as the contract.** Another mod's KSArmory content sits in a folder named after the
   consumer inside it, which lets several mods contribute without agreeing on file names:

   ```csharp
   // src/KSArmory/Sim/PackScan.cs L33-L45
   public static class PackScan
   {
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

2. **Enumerate the manifest and locate each folder**, with the manifest entry's own folder as the fallback for disabled mods:

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

3. **Classify and read.** Nothing to read is skipped silently, but a pack in a disabled mod is a warning, because KSA writes a
   new mod into the manifest disabled without saying so:

   ```csharp
   // src/KSArmory/Ksa/InstalledPacks.cs L23-L58
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

4. **Parse strictly.** Text in, profiles or faults out, no file access. Attributes are consumed: anything left when the element is
   built is something this build does not know, and that element is refused by name, so a typo such as `NavConstnat` is reported
   instead of flying on the default:

   ```csharp
   // src/KSArmory/Sim/PackReader.cs L496-L514
           /// <summary>True when the element may be registered: nothing wrong, nothing left over.</summary>
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

5. **Namespace the keys.** A definition named `AIM-9X` in pack `MyWeaponPack` is filed as `MyWeaponPack:AIM-9X`; bare names inside a
   file mean "mine", `KSArmory:20MM` reaches a built-in, so two packs never overwrite each other:

   ```csharp
   // src/KSArmory/Sim/PackReader.cs L442-L443
       private static string Qualify(string source, string name)
           => source == BuiltInSource ? name : $"{source}:{name}";
   ```

6. **Use the invariant culture** for every number, so a comma-decimal locale does not read `2.4` as 24, and let one bad definition
   cost only itself.

7. **Audit after load.** After `Freeze()`, check that each launcher and optic `PartId` exists as a `PartTemplate` and each subpart
   marker matches exactly one subpart Id; report what is unresolved with a `PackAudit`. Use `ModLibrary.Get<PartTemplate>` inside a
   `try`: in this game build `TryGet<T>` has no `PartTemplate` branch and answers false for every part, so `Has<PartTemplate>` is
   unreliable ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L776-L800)).

## Complete example

- [src/KSArmory/Ksa/InstalledPacks.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/InstalledPacks.cs), [src/KSArmory/Sim/PackScan.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/PackScan.cs), [src/KSArmory/Sim/PackReader.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/PackReader.cs)
- [src/KSArmory/Ksa/DeclaredParts.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/DeclaredParts.cs), [src/KSArmory/Sim/PackAudit.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/PackAudit.cs)

## Variations and gotchas

- **Do not list the pack XML in `mod.toml` `assets`.** KSA would try to read it as an `AssetBundle`; KSArmory's own Pantsir round
  ships the same way, deliberately left out of the array.
- **Defaults come from your profile**, so a pack states only what differs and a field added later cannot change an existing pack's
  behaviour; require a `Schema` attribute and match it exactly.
- **Freeze before use.** No weapon system exists yet at this moment; the roster is built later in flight so the registry never
  grows under a live magazine.
- **Disabled mods:** log them (as above) rather than registering them, since KSA has not loaded their parts.

## See also

- [Let other mods add data with a marker key in JSON files scanned across mod folders](ship-and-share-data-files-between-mods-2.md), [Validate asset XML ids and paths before launching the game (offline)](../../validation/validate-asset-xml-references/overview.md), [Declare a dependency on another StarMap mod, required or optional](../../../getting-started/dependencies/declare-a-mod-dependency/overview.md),
  [Ship assets and code in one mod folder and find it at runtime](../../../getting-started/loader/ship-content-and-code-in-one-mod/overview.md)
- [Asset-only weapon packs read from a KSArmory folder in every installed mod](../../../../mods/ksarmory/features/weapon-packs-from-mod-folders.md)
