---
type: Mod Feature
title: Asset-only weapon packs read from a KSArmory folder in every installed mod
description: At StarMapAllModsLoaded KSArmory walks KSA's mod manifest, reads every XML file in a KSArmory/ folder inside each mod (its own included), and registers weapon definitions through a strict, culture-invariant reader that refuses unknown attributes by name
mod: ksarmory
category: assets
techniques:
  - starmap-lifecycle
  - xml-asset
  - convention-folder-scan
  - strict-xml-reader
  - namespaced-keys
  - post-load-audit
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded]
game_types: [KSA.ModLibrary, KSA.ModManifest, KSA.ModEntry, KSA.Mod, KSA.PartTemplate]
tags: [weapon-packs, xml, manifest, modding-api, data-driven, validation]
---

A KSArmory weapon pack is an ordinary KSA content mod: parts, meshes and textures listed in its
`mod.toml` like any other, plus a `KSArmory/` folder holding XML weapon definitions that KSA never
reads. KSArmory keeps no list of packs; at startup it looks in that one folder inside **every**
mod KSA knows about and registers whatever it finds, so installing a pack is just installing a
mod.[^weapon-packs-doc][^installed-packs] KSArmory's own Pantsir round, sensor and launcher ship
the same way, in `src/KSArmory/KSArmory/Weapons.xml`, deliberately left out of `mod.toml`'s
`assets` array.[^weapons-xml][^csproj]

The reusable idea is a **convention folder as an inter-mod data contract**: no code dependency,
no load-order coupling, no registry to edit, and uninstalling the pack removes its content.

## How it works

| Step | Code | What happens |
|---|---|---|
| Enumerate mods | `InstalledPacks.Installed()` | Reads `ModLibrary.Manifest.Mods`, KSA's parsed `manifest.toml` (`ModManifest.Mods`, a `List<ModEntry>` with `Id` and `Enabled`)[^installed-packs][^ksa-modlibrary][^ksa-modmanifest] |
| Locate the folder | `FolderOf(entry)` | `ModLibrary.Find(id).DirectoryPath` for a loaded mod; otherwise `Path.Combine(ModLibrary.LocalModsFolderPath, id)`. The fallback exists because `ModLibrary.PrepareAll` skips disabled entries, so a disabled mod is not in the library at all[^installed-packs][^ksa-modlibrary][^ksa-mod] |
| Classify | `PackScan.Of(enabled, files)` | `NothingToRead` (no `KSArmory/*.xml`, almost every mod), `Ready`, or `Disabled`, which is logged as a warning rather than silently skipped, because KSA writes a newly discovered mod into the manifest with `Enabled = false`[^pack-scan][^ksa-modentry] |
| Dispatch by root | `BodyReader.IsBodies(text)` | A `<Bodies>` root goes to `BodyCatalogue.Register` (per-planet data for nuclear effects); anything else to `Armoury.Register(text, modId)`[^installed-packs][^body-reader][^body-catalogue] |
| Parse | `PackReader.Read` | Text in, profiles or `PackFault`s out, no file access; `Schema="1"` required and must match exactly; `<Munition>`/`<Sensor>` read before `<Launcher>`/`<Optic>` so references resolve while reading[^pack-reader] |
| Merge | `Catalogue.Register` | Refuses key collisions per definition and any launcher naming a refused key; every result is kept in `Catalogue.Registrations` and logged[^catalogue][^entry] |
| Audit | `Catalogue.Audit(new DeclaredParts())` | After `Freeze()`, checks every launcher and optic `PartId` exists as a `PartTemplate` and every subpart marker matches exactly one subpart Id[^pack-audit][^declared-parts] |

### Reader rules that make a data contract safe

- **Attributes are consumed.** Each `Reader` records the attributes it read; anything left over
  when the element is built refuses that definition with "'X' is not an attribute this build
  knows", so a typo such as `NavConstnat` is reported instead of silently flying on the default.[^pack-reader]
- **Defaults come from the profile**, so a pack states only what differs, and a field added in a
  later KSArmory build cannot change an existing pack's behaviour.[^weapon-packs-doc]
- **Invariant culture** for every number (`float.TryParse(..., CultureInfo.InvariantCulture)`),
  otherwise a comma-decimal locale reads `2.4` as 24.[^pack-reader]
- **Namespaced keys.** `Qualify(source, name)` files `Name="AIM-9X"` from pack `MyWeaponPack` as
  `MyWeaponPack:AIM-9X`; bare names inside a file mean "mine", `KSArmory:20MM` reaches a built-in
  (the one prefix that is stripped), so two packs never overwrite each other.[^pack-reader]
- **One bad definition costs only itself**, never the whole file, and a bad file root costs only
  that file.[^pack-reader]

### The post-load audit and a `ModLibrary` trap

The audit has to wait until part templates exist, and `DeclaredParts` asks through
`ModLibrary.Get<PartTemplate>` inside a `try`, not `Has`/`TryGet`. In this build `TryGet<T>`
dispatches on `typeof(T)` through a chain that has no `PartTemplate` branch and falls through to
`false`, so `Has<PartTemplate>` answers "missing" for every part, Core's included; only `Get<T>`
reaches `AllParts`, reporting a miss by throwing `NullReferenceException`.[^declared-parts][^ksa-modlibrary]
Subpart Ids come from `PartTemplate.SubPartInstances`.[^ksa-parttemplate]

## Lifecycle and timing

- **`startup:all-mods-loaded`.** `InstalledPacks.RegisterAll()` is the first call in KSArmory's
  `[StarMapAllModsLoaded]` method (KSArmoryMod.cs L158), followed by `Catalogue.Freeze()` (L162)
  and the audit (L167).[^entry] StarMap fires that attribute from a Harmony postfix on
  `ModLibrary.LoadAll`, after every mod's `mod.toml` assets were loaded, so the manifest is parsed,
  enabled mods have directories, and `PartTemplate`s are registered (their game data is merged
  later, in `ModLibrary.AttachGameData`; the audit only needs Ids and subpart instances).
- Code packs that call `Armoury.Register` themselves did so earlier, from their own entry points;
  scanning folders in the same hook as the freeze closes registration in one place.
- **Freeze before crewing.** No weapon system exists yet; the roster is built later in flight, so
  the registry never grows under a live magazine.[^catalogue]
- Nothing here touches the universe or a vehicle; it is file IO plus pure parsing, safe to run on
  the main thread once at startup.

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/InstalledPacks.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/InstalledPacks.cs#L23-L110) | Manifest walk, folder lookup, bodies/weapons dispatch |
| [src/KSArmory/Sim/PackScan.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/PackScan.cs#L33-L45) | Folder name, file pattern, availability |
| [src/KSArmory/Sim/PackReader.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/PackReader.cs#L35-L122) | Schema gate and two-pass element read |
| [src/KSArmory/Sim/PackReader.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/PackReader.cs#L481-L540) | Consuming attribute reader |
| [src/KSArmory/Sim/PackAudit.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/PackAudit.cs) | Part and marker audit after load |
| [src/KSArmory/Ksa/DeclaredParts.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/DeclaredParts.cs) | `ModLibrary.Get<PartTemplate>` probe |
| [src/KSArmory/KSArmory/Weapons.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmory/Weapons.xml) | KSArmory's own pack (the Pantsir) |
| [src/KSArmory/KSArmory/Bodies.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmory/Bodies.xml) | Per-body data read from the same folder |
| [tests/KSArmory.Tests/PackReaderTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/PackReaderTests.cs) | Headless tests of every refusal |

## Snippets

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

```xml
<!-- src/KSArmory/KSArmory/Weapons.xml L75-L76 -->
  <Sensor Name="1RS1" DisplayName="1RS1-1E search radar" Emits="true" Scope="Search"
          ChaffNotchMps="30" ChaffReacquireSeconds="3" OpticalBackup="true" />
```

## Game APIs touched

| Member | Use |
|---|---|
| `ModLibrary.Manifest` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L160)) | Every mod in `manifest.toml`, enabled or not |
| `ModLibrary.Find(string)` (ModLibrary.cs L187), `Mod.DirectoryPath` ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L91)) | Folder of a loaded mod |
| `ModLibrary.LocalModsFolderPath` (ModLibrary.cs L178) | `<Documents>/mods`, fallback for disabled mods |
| `ModEntry.Id`, `ModEntry.Enabled` ([current/decomp/KSA/ModEntry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModEntry.cs)) | Manifest entry; the `(id, count)` constructor creates new entries with `Enabled = false` |
| `ModLibrary.Get<PartTemplate>(string)` (ModLibrary.cs L1059, `PartTemplate` branch L1119) | Part existence; throws on a miss |
| `ModLibrary.Has<T>` / `TryGet<T>` (ModLibrary.cs L575, L776) | Deliberately not used: no `PartTemplate` branch |
| `PartTemplate.SubPartInstances` ([current/decomp/KSA/PartTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTemplate.cs#L21)) | Subpart Ids for the marker audit |

## Pitfalls and version notes

- **Never list the pack file in `mod.toml` `assets`.** KSA hands every entry to its own asset-bundle
  loader, which would parse the weapon file with the wrong reader.[^weapon-packs-doc]
- **A pack mod must be enabled** in `manifest.toml`; KSArmory warns about a disabled one it can
  see, but KSA itself says nothing.[^installed-packs][^ksa-modentry]
- **Markers resolve by case-insensitive substring, first hit wins.** The audit flags a marker that
  matches zero or more than one subpart, because reordering subparts would otherwise change which
  assembly moves.[^pack-audit]
- **Part Ids must agree between the two worlds** (KSA's `Assets.xml` and the pack's launcher
  `PartId`); KSA has no part-module hook, so the Id is the only link.[^weapon-packs-doc]
- `Has<PartTemplate>` / `TryGet<PartTemplate>` returning false for real parts is a property of
  build 2026.10.10.5554's `ModLibrary`; re-check if a later build adds the branch.[^ksa-modlibrary]
- A later mod's `<Body>` entry replaces an earlier one's and logs that it did.[^body-catalogue]

## See also

- [Armoury pack registration API](./armoury-pack-registration-api.md)
- [Weapon registry and profiles](./weapon-registry-and-profiles.md)
- [Weapon part definitions in XML](./weapon-part-definitions-xml.md)
- [StarMap entry and fault containment](./starmap-entry-and-fault-containment.md)
- [Startup save repair](./startup-save-repair.md)
- [Sim boundary headless tests](./sim-boundary-headless-tests.md)
- [Read a convention folder inside every installed mod with a strict reader](../../../howto/assets/asset-xml/ship-and-share-data-files-between-mods/ship-and-share-data-files-between-mods-3.md) - how-to article demonstrating this feature
- [Feed the registry from a KSArmory folder in every installed mod](../../../howto/vehicles-and-parts/craft-tracking/give-inert-parts-behaviour-by-part-id/give-inert-parts-behaviour-by-part-id-3.md) - how-to article demonstrating this feature

[^installed-packs]: src/KSArmory/Ksa/InstalledPacks.cs
[^pack-scan]: src/KSArmory/Sim/PackScan.cs
[^pack-reader]: src/KSArmory/Sim/PackReader.cs
[^pack-audit]: src/KSArmory/Sim/PackAudit.cs
[^declared-parts]: src/KSArmory/Ksa/DeclaredParts.cs
[^catalogue]: src/KSArmory/Sim/Catalogue.cs
[^body-reader]: src/KSArmory/Sim/BodyReader.cs
[^body-catalogue]: src/KSArmory/Sim/BodyCatalogue.cs
[^entry]: src/KSArmory/Ksa/KSArmoryMod.cs
[^weapons-xml]: src/KSArmory/KSArmory/Weapons.xml
[^csproj]: src/KSArmory/KSArmory.csproj
[^weapon-packs-doc]: docs/WEAPON-PACKS.md
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-modentry]: KSA.ModEntry (decompiled, build 2026.10.10.5554)
[^ksa-modmanifest]: KSA.ModManifest (decompiled, build 2026.10.10.5554)
[^ksa-mod]: KSA.Mod (decompiled, build 2026.10.10.5554)
[^ksa-parttemplate]: KSA.PartTemplate (decompiled, build 2026.10.10.5554)
