---
type: How-To
title: Repair saves and library craft at StarMapAllModsLoaded
description: Trim surplus SubPartRef entries from universe.xml and vehicle.xml before any load is possible, with a backup, a pure tested repair function and an offline twin script
category: vehicles-and-parts
group: parts
topic: evolve-a-shipped-part-without-breaking-saves
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/startup-save-repair]
techniques: [regex-xml-surgery, starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded]
game_types:
  - KSA.PartTree
  - KSA.PartTemplate
  - KSA.ModLibrary
  - KSA.VehicleSaves
  - KSA.GameSaves
  - KSA.VehicleSaveData
  - KSA.PartInstance
tags: [saves, repair, subparts, startup, backup, regex]
---
Outcome: when a shipped part has already lost subparts and old saves would crash the game, a one-release repair
realigns every save and library craft before any of them can be opened, keeps a backup, and logs what it did.
KSArmory ships this for the release that removed twenty shell subparts from its Mk 42; it is removed in the one
after. The authoring rule that avoids needing it is in [article 1](evolve-a-shipped-part-without-breaking-saves-1.md).

Prerequisites: StarMap. Demonstrated by [ksarmory/startup-save-repair](../../../../mods/ksarmory/features/startup-save-repair.md).

## Background: how the game does it

- The bug: `PartTree.Deserialize` indexes the rebuilt part's `SubParts` with the save's `SubPartInstances` index, so
  a save listing more subparts than the template declares throws `IndexOutOfRangeException`
  [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L338-L346)[^ksa-parttree]. Dropping the surplus saved entries is the whole repair; fewer saved than declared
  loads as it is.
- The files: `<Documents>/saves/<name>/universe.xml` (`GameSaves.SaveFolderPath`) and
  `<Documents>/Vehicles/<name>/vehicle.xml` (`VehicleSaves.SaveFolderPath`) [current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L124)[^ksa-gamesaves]
  [current/decomp/KSA/VehicleSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleSaves.cs#L167)[^ksa-vehiclesaves]. A part block opens as `<PartRef InstanceOf="...">` or `<RootPartRef InstanceOf="...">`
  and its subparts are `<SubPartRef InstanceOf=... />` lines, as in Core's craft
  ([current/Content/Core/defaultvehicles/Rocket/vehicle.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/defaultvehicles/Rocket/vehicle.xml#L1-L30)[^content-vehicle]).
- What a part declares now: `ModLibrary.Get<PartTemplate>(id).SubPartInstances` [current/decomp/KSA/PartTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTemplate.cs#L19-L20)[^ksa-parttemplate], which is
  available once every part XML is loaded.

## Lifecycle and timing

`StarMapAllModsLoaded` is a postfix on `ModLibrary.LoadAll` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L935-L990)[^ksa-program]), so every mod's part XML is
loaded, so `ModLibrary.Get<PartTemplate>` answers; no save has been deserialised yet (saves load only on a
player's request later), so rewriting the files now is race-free. It runs on the main thread during the loading
screen and blocks it for its duration; KSArmory logs the milliseconds. It must be this early because the
crash site is reached from a confirm popup in the draw pass and nothing catches it. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Keep the repair **pure**: text in, text out, so every case is unit-testable with no game. The function takes
   the file text and a `declaredFor(partId)` callback returning the part's current subparts in declaration order
   (or `null` for parts it should leave alone):

```csharp
// src/KSArmory/Sim/SaveRepair.cs L55-78, L93-117
    public static Result Repair(string save, Func<string, IReadOnlyList<string>?> declaredFor)
    {
        List<string> lines = SplitKeepingEndings(save);
        HashSet<int> dropped = [];
        List<string> report = [];
        int repaired = 0, refused = 0;

        int index = 0;
        while (index < lines.Count)
        {
            Match opened = PartOpen.Match(lines[index]);
            if (!opened.Success)
            {
                index++;
                continue;
            }

            string partId = opened.Groups[2].Value;
            int indent = lines[index].Length - lines[index].TrimStart().Length;

            // The part's own subparts are the SubPartRef lines one level inside it; a nested part's
            // are further in, and the block ends at the next part or the closing tag.
            List<(int Line, string Name)> saved = [];
            int stateful = 0;
// ...
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

            index = cursor;
        }

        string? text = dropped.Count == 0
                           ? null
                           : string.Concat(lines.Where((_, n) => !dropped.Contains(n)));
        return new Result(text, repaired, dropped.Count, refused, report);
    }
```

2. Align by name, tolerant of past Id renames, greedily: the lists differ by whole assemblies added or dropped,
   never by reordering. Strip the era-specific decoration with a canonicaliser:

```csharp
// src/KSArmory/Sim/SaveRepair.cs L33-48
    // Every way one assembly has been spelled across the Id renames.
    private static readonly Regex Noise = new(@"(?i)(^KSArmory_|_?Sub_?[Pp]art_?|^Prefab_|^Launcher_|\d+$|_$)",
                                              RegexOptions.Compiled);

    /// <summary>A subpart name with the era-specific decoration taken off.</summary>
    public static string Canonical(string name)
    {
        string previous;
        do
        {
            previous = name;
            name = Noise.Replace(name, "");
        } while (name != previous);

        return name.ToLowerInvariant();
    }
```

```csharp
// src/KSArmory/Sim/SaveRepair.cs L119-138
    // Which saved entries have no counterpart, walking the two lists together. Greedy rather than a
    // full alignment: they differ by whole assemblies added or dropped, never by a reordering.
    private static List<int> Surplus(List<(int Line, string Name)> saved, List<string> declared)
    {
        List<int> surplus = [];
        int want = 0;

        foreach ((int line, string name) in saved)
        {
            if (want < declared.Count && name == declared[want])
            {
                want++;
                continue;
            }

            surplus.Add(line);
        }

        return surplus;
    }
```

3. Find the files and ask the loaded templates what each part declares now:

```csharp
// src/KSArmory/Ksa/SaveRepairs.cs L24-61
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
// src/KSArmory/Ksa/SaveRepairs.cs L98-124
    // What a part declares now, as the template Ids its subparts instance -- for this mod's parts and
    // any pack's, and null for everything else, which this has no business rewriting.
    private static IReadOnlyList<string>? DeclaredFor(string partId)
    {
        if (_declared.TryGetValue(partId, out IReadOnlyList<string>? cached)) return cached;

        IReadOnlyList<string>? declared = null;
        if (partId.StartsWith("KSArmory_", StringComparison.Ordinal) || Catalogue.LauncherForPart(partId) is not null)
        {
            try
            {
                if (ModLibrary.Get<PartTemplate>(partId) is { } template)
                {
                    declared = template.SubPartInstances
                                       .Select(s => string.IsNullOrEmpty(s.InstanceOf) ? s.Id : s.InstanceOf)
                                       .ToList();
                }
            }
            catch
            {
                // A part nothing declares any more; KSA refuses that save by another route.
            }
        }

        _declared[partId] = declared;
        return declared;
    }
```

4. Rewrite safely: preserve the BOM choice and line endings, write a `.bak` beside the file the first time only
   (KSA keeps its own `universe.xml.bak`), and skip files that do not contain your prefix:

```csharp
// src/KSArmory/Ksa/SaveRepairs.cs L63-96
    private static bool TryRepair(string file)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(file);

            // Written back as it was read: with its byte-order mark if it had one.
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            UTF8Encoding encoding = new(bom);
            string text = encoding.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));

            // Most saves hold no part of ours, and those are not worth parsing.
            if (!text.Contains("KSArmory", StringComparison.Ordinal)) return false;

            SaveRepair.Result result = SaveRepair.Repair(text, DeclaredFor);
            string where = Path.GetFileName(Path.GetDirectoryName(file)) ?? file;
            foreach (string line in result.Report) Log.Info($"  save repair, '{where}': {line}");

            if (result.Text is null) return false;

            string backup = file + BackupSuffix;
            if (!File.Exists(backup)) File.Copy(file, backup);

            File.WriteAllText(file, result.Text, encoding);
            Log.Info($"save repair: '{where}' rewritten, {result.EntriesDropped} subpart entries dropped; "
                     + $"the original is {Path.GetFileName(backup)}");
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"save repair: {file} left as it was ({e.Message})");
            return false;
        }
    }
```

5. Call `SaveRepairs.RunAll()` from the all-mods-loaded hook, after pack registration and before anything that
   might load a save:

```csharp
// src/KSArmory/Ksa/KSArmoryMod.cs L168-171

        // Before anything can load a save: one listing more subparts than a part now declares closes
        // the game on load. The part templates are loaded by now, which is what it asks.
        SaveRepairs.RunAll();
```

6. Offer an **offline twin** for authors and players who cannot run the game: KSArmory's `tools/repair-saves.py`
   applies the same rules, reads current definitions from the mod's own asset XML, defaults to report-only
   (`--fix` rewrites) and also re-dresses kittens whose saved `Character` no longer exists.
7. Expected log: `save repair: <n> of <m> save(s) and craft realigned with the parts as declared now, <ms> ms` and
   per-file lines such as `'<save>' rewritten, <k> subpart entries dropped; the original is universe.xml.ksarmory-repair.bak`.

## Complete example

- [src/KSArmory/Sim/SaveRepair.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/SaveRepair.cs)[^ksarmory-saverepair] and [src/KSArmory/Ksa/SaveRepairs.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/SaveRepairs.cs)[^ksarmory-saverepairs]
- [tools/repair-saves.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/repair-saves.py)[^ksarmory-repair-saves] - the offline twin

## Variations and gotchas

- **Never drop stateful entries.** A `SubPartRef` that is not self-closing carries module save data; the repair
  refuses a part whose surplus would drop one, and reports it.
- **Pair by name, not by count alone.** Dropping the last N entries would apply state to the wrong subparts; the
  greedy walk drops the entries that have no counterpart.
- **Only touch your own parts.** `DeclaredFor` answers `null` for ids it does not own, so a save full of other
  mods' parts is read, found unchanged and left byte-for-byte alone.
- **It is temporary.** Ship the repair for one release, then delete it; keep the authoring rule forever.
- **Line-based surgery depends on indentation.** The repair expects `SubPartRef` lines exactly two spaces deeper
  than their part; a game update that changes the writer's formatting breaks it silently.
- **Backups.** Do not overwrite an existing backup: the first original is the one worth keeping.

## See also

- [Overview](overview.md), [Article 1](evolve-a-shipped-part-without-breaking-saves-1.md)
- [ksarmory/startup-save-repair](../../../../mods/ksarmory/features/startup-save-repair.md)
- `choose-where-a-mod-stores-its-files`, [Load, validate and unload part mods in a running game](../load-and-unload-part-mods-at-runtime/overview.md)

[^content-vehicle]: current/Content/Core/defaultvehicles/Rocket/vehicle.xml (game content, build 2026.10.10.5554)
[^ksa-program]: current/decomp/KSA/Program.cs (decompiled, build 2026.10.10.5554)
[^ksarmory-repair-saves]: KSArmory: tools/repair-saves.py
[^ksa-parttemplate]: current/decomp/KSA/PartTemplate.cs (decompiled, build 2026.10.10.5554)
[^ksa-gamesaves]: current/decomp/KSA/GameSaves.cs (decompiled, build 2026.10.10.5554)
[^ksa-parttree]: current/decomp/KSA/PartTree.cs (decompiled, build 2026.10.10.5554)
[^ksarmory-saverepairs]: KSArmory: src/KSArmory/Ksa/SaveRepairs.cs
[^ksa-vehiclesaves]: current/decomp/KSA/VehicleSaves.cs (decompiled, build 2026.10.10.5554)
[^ksarmory-saverepair]: KSArmory: src/KSArmory/Sim/SaveRepair.cs
