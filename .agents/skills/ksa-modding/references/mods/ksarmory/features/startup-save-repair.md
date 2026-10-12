---
type: Mod Feature
title: Repair saves at startup after removing a part's subparts
description: Before any save can be loaded, the mod rewrites every save and library craft that lists more subparts for one of its parts than the part now declares, because KSA pairs saved subparts with the definition by position and a surplus kills the process on load
mod: ksarmory
category: persistence
techniques: [save-rewrite, regex-xml-surgery, starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded]
game_types:
  - KSA.PartTree
  - KSA.PartInstance
  - KSA.PartTemplate
  - KSA.ModLibrary
  - KSA.VehicleSaveData
  - KSA.VehicleSaves
  - KSA.GameSaves
  - KSA.SaveDirectory
tags: [save-migration, subparts, xml, startup, backup, part-templates, compatibility]
---

KSA restores a saved part's subparts by position: the loop is bounded by the save's list and indexes
the part's current definition. A part that has *lost* subparts since the save was written throws
`IndexOutOfRangeException` inside the load, and nothing up the stack catches it, so the game closes.
KSArmory removed twenty shell subparts from one of its guns and ships a one-release repair: during
`[StarMapAllModsLoaded]`, before any save can be opened, it scans every save and library craft,
drops the saved subpart entries that no longer line up with what each of its parts declares now,
and keeps a backup of the original file.[^ksarmory-saverepairs][^ksa-parttree]

## How it works

1. **Find files.** Root = parent of the mod's log folder (the guessed KSA user folder).[^ksarmory-log] Candidates
   are `<root>/saves/*/universe.xml` and `<root>/vehicles/*/vehicle.xml`.
2. **Cheap reject.** Read bytes, note a UTF-8 BOM, decode; skip files that do not contain the
   string `KSArmory`.
3. **Repair text** (`SaveRepair.Repair`, pure and unit-tested):[^ksarmory-saverepair]
   - A part block opens at `<PartRef InstanceOf="...">` or `<RootPartRef InstanceOf="...">` and ends
     at the next part open or close tag.
   - Its own subparts are `<SubPartRef InstanceOf="..." .../>` lines indented exactly two spaces
     deeper. A non-self-closing `SubPartRef` carries save data of its own and is never dropped.
   - `declaredFor(partId)` gives the part's current subparts in order; `null` means "not ours, leave
     it". If saved + stateful exceeds declared and any entry is stateful, the part is refused and
     reported. If only self-closing entries exceed, the surplus is found by walking both lists with a
     greedy match on names canonicalised against past Id renames (prefixes `KSArmory_`, `Prefab_`,
     `Launcher_`, `SubPart` decorations, trailing digits).
   - Lines are split keeping their endings, so the output differs only by the dropped lines.
4. **What a part declares now** (`DeclaredFor`): only for ids starting `KSArmory_` or registered as a
   launcher by a weapon pack: `ModLibrary.Get<PartTemplate>(id).SubPartInstances`, each as
   `InstanceOf` (or `Id`), cached per id.
5. **Write.** Copy the original to `<file>.ksarmory-repair.bak` if no such backup exists, then write
   the repaired text with the same BOM choice. Each part repaired and the total time are logged.

`tools/repair-saves.py` is the offline twin: same rules, but it reads the current definitions from
the mod's own asset XML, defaults to report-only (`--fix` rewrites) and additionally re-dresses
kittens whose saved `Character` is a KSArmory character that no longer exists.[^ksarmory-repairpy]

## Lifecycle and timing

`SaveRepairs.RunAll()` is called from `OnFullyLoaded` (KSArmoryMod.cs L169-L171), after pack
registration and the pack audit, before `AttitudeHook.Install()`.[^ksarmory-mod] That hook is
StarMap's postfix on `ModLibrary.LoadAll`, which the `Program` constructor calls at Program.cs L942:
every mod's part XML is loaded, so `ModLibrary.Get<PartTemplate>` answers, and no save has been
deserialized (the startup scene is `Universe.LoadDefaultSystem` at L1069; saves load only on a
player's request later).[^ksa-program] It runs on the main thread during the loading screen and
blocks it for the duration; the log line reports milliseconds.

Why it must be this early: the crash site is `PartTree.Deserialize` (PartTree.cs L298-L341), reached
from `Vehicle` deserialization (Vehicle.cs L1008) during `Universe.DeserializeSave`, which a confirm
popup runs from `Popup.DrawAll` in `OnDrawUiFrame` (Program.cs L3134). `Popup.DrawAll` only guards
its modal check (Popup.cs L46-L64), and neither `App.Run` nor `Program.Main` has a handler.[^ksa-popup]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/SaveRepairs.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/SaveRepairs.cs) | File discovery, BOM-preserving read/write, backup, `DeclaredFor` from live templates |
| [src/KSArmory/Sim/SaveRepair.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/SaveRepair.cs) | Text-in, text-out repair rules (no KSA types) |
| [tools/repair-saves.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/repair-saves.py) | Offline report/fix tool, plus kitten re-dressing |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L169-L171) | Called from `[StarMapAllModsLoaded]` |

## Snippets

```csharp
// current/decomp/KSA/PartTree.cs L338-L341
			for (int i = 0; i < nextNode2.SubPartInstances?.Count; i++)
			{
				Part part2 = nextNode.SubParts[i];
				PartInstance partInstance2 = nextNode2.SubPartInstances[i];
```

```csharp
// src/KSArmory/Ksa/SaveRepairs.cs L24-L61
    public static void RunAll()
    {
        try
        {
            if (Directory.GetParent(Log.Folder)?.FullName is not { } root) return;

            Stopwatch clock = Stopwatch.StartNew();
            int files = 0, repaired = 0;

            foreach (string file in Candidates(root))
            {
                files++;
                if (TryRepair(file)) repaired++;
            }

            Log.Info($"save repair: {repaired} of {files} save(s) and craft realigned with the parts as declared now, "
                     + $"{clock.Elapsed.TotalMilliseconds:F0} ms");
        }
        catch (Exception e)
        {
            Log.Warn($"save repair did not run: {e.Message}");
        }
    }

    private static IEnumerable<string> Candidates(string root)
    {
        foreach ((string folder, string name) in new[] { ("saves", "universe.xml"), ("vehicles", "vehicle.xml") })
        {
            string dir = Path.Combine(root, folder);
            if (!Directory.Exists(dir)) continue;

            foreach (string entry in Directory.EnumerateDirectories(dir))
            {
                string file = Path.Combine(entry, name);
                if (File.Exists(file)) yield return file;
            }
        }
    }
```

```csharp
// src/KSArmory/Sim/SaveRepair.cs L93-L108
            // Only a part that does not fit is a problem; one whose subparts carry state and still fit
            // loads as it is.
            IReadOnlyList<string>? declared = declaredFor(partId);
            if (declared is not null && saved.Count + stateful > declared.Count && stateful > 0)
            {
                refused++;
                report.Add($"{partId}: {saved.Count + stateful} saved against {declared.Count} declared, "
                           + "and a subpart carries save data of its own; left alone");
            }
            else if (declared is not null && saved.Count > declared.Count)
            {
                List<int> surplus = Surplus(saved, declared.Select(Canonical).ToList());
                repaired++;
                dropped.UnionWith(surplus);
                report.Add($"{partId}: {saved.Count} saved against {declared.Count} declared, dropped {surplus.Count}");
            }
```

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `PartTree.Deserialize(PartInstance)` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L298-L341) | The positional pairing that crashes |
| `PartInstance.SubPartInstances` (`[XmlElement("SubPartRef")]`), `Children` (`PartRef`), `InstanceOf` | [current/decomp/KSA/PartInstance.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartInstance.cs#L16-L46) | The XML shape the regexes match[^ksa-partinstance] |
| `VehicleSaveData.RootPartInstance` (`RootPartRef`), `vehicle.xml` | [current/decomp/KSA/VehicleSaveData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleSaveData.cs#L10-L28) | Library craft format[^ksa-vehiclesavedata] |
| `PartTemplate.SubPartInstances` | [current/decomp/KSA/PartTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTemplate.cs#L21) | What a part declares now[^ksa-parttemplate] |
| `ModLibrary.Get<T>(string)` | (KSA.ModLibrary) | Template lookup after `LoadAll` |
| `GameSaves.SaveFolderPath`, `VehicleSaves.SaveFolderPath` | [current/decomp/KSA/VehicleSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleSaves.cs#L167) | KSA's own folders: `<docs>/saves` and `<docs>/Vehicles`[^ksa-vehiclesaves][^ksa-gamesaves] |

## Pitfalls and version notes

- **A shipped part's subpart list is append-only.** Adding is safe (the loop stops at the save's
  shorter count); renaming is safe (`InstanceOf` is never matched back); removing kills every save
  holding the part; reordering silently applies one subpart's saved state to another.[^ksarmory-moddingnotes]
- **Folder names are guessed, not asked.** The root comes from the log-folder search, not from
  `Constants.DocumentsFolderPath`, so a StarMap `-InstancePath` or a temp-dir log fallback points the
  scan at the wrong place. KSA's vehicle library is `Vehicles` (VehicleSaves.cs L167); the scan looks
  for `vehicles`, which only matches on a case-insensitive file system.
- **The backup does not outlive the next save.** It sits inside the save folder, and KSA deletes and
  recreates that folder whenever it writes the save (SaveDirectory.cs L21-L24, L95-L109).[^ksa-savedirectory]
- **Docs vs 5554:** the `BackupSuffix` comment says KSA keeps a `universe.xml.bak` there; there is no
  `.bak` handling anywhere in the 5554 decomp. KSA-MODDING-NOTES says library craft "store no
  `<SubPartRef>` at all", yet the repair scans them; in 5554 `VehicleSaveData.RootPartRef` is a
  `PartInstance`, whose `SubPartInstances` serialize as `SubPartRef`, so the element can appear
  (whether KSA populates it for library craft is unverified).
- **Line-based matching** assumes `XmlSerializer`'s layout: one element per line, two-space indent per
  level. A reformatted file is left untouched.
- **Temporary by design**: the class comment ties it to the one release after the subparts were removed.[^ksarmory-saverepairs]

## See also

- [Per-craft settings JSON sidecar](./per-craft-settings-json-sidecar.md)
- [Own log file and developer switch](./own-log-file-and-developer-switch.md)
- [Rounds as hidden launcher subparts](./rounds-as-hidden-launcher-subparts.md)
- [Weapon part definitions (XML)](./weapon-part-definitions-xml.md)
- [Weapon packs from mod folders](./weapon-packs-from-mod-folders.md)
- [StarMap entry and fault containment](./starmap-entry-and-fault-containment.md)
- [Repair saves and library craft at StarMapAllModsLoaded](../../../howto/vehicles-and-parts/parts/evolve-a-shipped-part-without-breaking-saves/evolve-a-shipped-part-without-breaking-saves-2.md) - how-to article demonstrating this feature

[^ksarmory-saverepairs]: src/KSArmory/Ksa/SaveRepairs.cs
[^ksarmory-saverepair]: src/KSArmory/Sim/SaveRepair.cs
[^ksarmory-repairpy]: tools/repair-saves.py
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-log]: src/KSArmory/Ksa/Log.cs
[^ksarmory-moddingnotes]: docs/KSA-MODDING-NOTES.md
[^ksa-parttree]: KSA.PartTree (decompiled)
[^ksa-partinstance]: KSA.PartInstance (decompiled)
[^ksa-parttemplate]: KSA.PartTemplate (decompiled)
[^ksa-vehiclesavedata]: KSA.VehicleSaveData (decompiled)
[^ksa-vehiclesaves]: KSA.VehicleSaves (decompiled)
[^ksa-gamesaves]: KSA.GameSaves (decompiled)
[^ksa-savedirectory]: KSA.SaveDirectory (decompiled)
[^ksa-program]: KSA.Program (decompiled)
[^ksa-popup]: KSA.Popup (decompiled)
