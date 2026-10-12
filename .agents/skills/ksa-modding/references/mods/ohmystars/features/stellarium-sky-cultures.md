---
type: Mod Feature
title: Stellarium sky cultures
description: Load 53 legacy Stellarium sky cultures from constellationship.fab and constellation_names files, switch between them at runtime, and keep only segments whose stars match the stock star binary bit for bit
mod: ohmystars
category: celestials
techniques: [data-file-parsing, starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapBeforeGui]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.ModLibrary, KSA.Mod]
tags: [sky, constellations, stellarium, data, sky-culture, star-binary]
---

Oh My Stars ships 53 Stellarium "sky cultures" (Modern/IAU variants, Chinese, Norse, Maori, Navajo,
Babylonian, Egyptian and many more) and lets the player switch between them at runtime from a
combo box. Each culture is just two legacy Stellarium text files per folder; the mod parses them at
load, joins them to the star catalog by Hipparcos number, and keeps only segments whose endpoints
are stars the game actually renders.[^oms-skyculturesrenderer-cs] [^oms-notice-txt]

## How it works

1. **Folder layout.** `skycultures/<name>/constellationship.fab` and
   `skycultures/<name>/constellation_names.eng.fab`; NOTICE.txt says every other Stellarium file was
   removed and that the data is GPL-2.0 ([NOTICE.txt](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/NOTICE.txt)).
2. **Discovery.** `LoadSkyCultures` enumerates sub-directories, skips any without a
   `constellationship.fab`, turns the folder name into a display name (`_` to space, first letter
   upper-cased), and falls back to any `constellation_names.*.fab` when the English file is missing
   ([src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L708-L743)).
3. **Line file.** Each non-comment line is `<id> <N> <hip> <hip> ...` with `2N` Hipparcos numbers
   forming `N` independent segments, for example
   `And 5  677 3092 3092 5447 9640 5447 5447 4436 4436 3881`
   ([skycultures/modern/constellationship.fab](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/skycultures/modern/constellationship.fab#L2),
   [src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L745-L804)). Lines whose count does not match are skipped.
4. **Names file.** A compiled regex reads `<id> "<native>" _("<english>")` with escaped quotes,
   e.g. `001 "Bi Xiu" _("Net")` in the Chinese culture
   ([src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L806-L841), [skycultures/chinese/constellation_names.eng.fab](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/skycultures/chinese/constellation_names.eng.fab#L1)).
   The English name is shown when present, otherwise the native one.
5. **Resolve on switch.** Setting `ActiveSkyCultureIndex` calls `ResolveSegments`, which turns every
   segment into a pair of precomputed ecliptic unit vectors from the catalog, collects the star
   names used, and computes one label direction per constellation as the normalised sum of its
   stars' directions ([src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L59-L73),
   [src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L333-L413)). Drawing then only iterates flat lists.
6. **Keep only rendered stars.** At init `LoadRenderedStarDirections` opens the Core mod's
   `briars_binary_dimmer_better_99k_stars.bin` (the file `Content/Core/mod.toml` lists under
   `starBinaries`), reads the `int32` count and the 16-byte records (three `float`s plus four
   bytes), and stores the raw bit patterns of each `(x, y, z)` in a hash set
   ([src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L415-L459),
   [current/Content/Core/mod.toml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/mod.toml#L90-L91)).[^ksa-content-core-mod-toml] `IsRenderedStar`
   casts the mod's own `double` direction to `float` and looks up the exact bits; a segment is kept
   only if both ends match. If the file is missing the filter is disabled and every segment is kept.
   The record layout matches what `Mod.LoadStarBinaries` reads
   ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L231-L285)).[^ksa-mod-cs]
7. **Default and picker.** `Init` hard-codes `_activeSkyCultureIndex = 29`; with the 53 shipped
   folders in ordinal order that index is `modern` ([src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L194-L205)).
   The Settings tab's combo pins a duplicate "Modern" entry at the top using a `##PinnedModern` ID
   suffix so both rows show the same text ([src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L334-L369)).
8. **Information tab.** `GetActiveConstellationInformation` lists the active culture's
   constellations, each with the catalog stars its segments use, sorted by name
   ([src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L80-L111)).

## Lifecycle and timing

- **Load: `startup:all-mods-loaded`.** `StellariumRenderer.Init` (from `OnFullyLoaded`) calls
  `SkyCulturesRenderer.Init`, which reads the star binary, all culture folders and the catalog, then
  resolves the default culture ([src/OhMyStarsEntryPoint.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsEntryPoint.cs#L13-L26)). StarMap fires
  `[StarMapAllModsLoaded]` as a postfix on `ModLibrary.LoadAll`
  ([StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L17-L28)),[^starmap-patches-modlibrarypatches-cs]
  which the `Program` constructor calls at line 942, so `ModLibrary.Find("Core")` already returns
  the Core mod with its `DirectoryPath` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L942)).[^ksa-program-cs]
- **Switch: `frame:draw-ui`.** The combo runs in the Oh My Stars window from `[StarMapBeforeGui]`;
  the setter re-resolves synchronously on the main thread, and the overlay drawn later in the same
  frame (`[StarMapAfterGui]`) already uses the new culture.

## Key code

| Path | Role |
|---|---|
| [src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L708-L841) | Folder discovery, `.fab` parsers |
| [src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L333-L413) | Segment and label resolution |
| [src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L415-L459) | Rendered-star bit-exact filter |
| [src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L334-L369) | Sky culture combo |

## Snippets

```csharp
// src/SkyCulturesRenderer.cs L422-L449, L451-L459
        string starBinaryPath = Path.Combine(
            coreMod.DirectoryPath,
            "briars_binary_dimmer_better_99k_stars.bin");
        if(!File.Exists(starBinaryPath))
            return;

        using FileStream stream = File.OpenRead(starBinaryPath);
        using BinaryReader reader = new(stream);

        if(stream.Length < sizeof(int))
            return;

        int starCount = reader.ReadInt32();
        long availableStarCount = (stream.Length - stream.Position) / 16;
        int recordsToRead = (int)Math.Min(Math.Max(starCount, 0), availableStarCount);

        for(int index = 0; index < recordsToRead; index++) {
            float x = reader.ReadSingle();
            float y = reader.ReadSingle();
            float z = reader.ReadSingle();
            stream.Position += 4;

            renderedStarDirections.Add(new StarDirectionKey(
                BitConverter.SingleToInt32Bits(x),
                BitConverter.SingleToInt32Bits(y),
                BitConverter.SingleToInt32Bits(z)));
        }
    }
// ...
    private static bool IsRenderedStar(double3 direction) {
        if(renderedStarDirections.Count == 0)
            return true;

        return renderedStarDirections.Contains(new StarDirectionKey(
            BitConverter.SingleToInt32Bits((float)direction.X),
            BitConverter.SingleToInt32Bits((float)direction.Y),
            BitConverter.SingleToInt32Bits((float)direction.Z)));
    }
```

```csharp
// src/SkyCulturesRenderer.cs L806-L833
    private static readonly Regex _constellationNameRegex = new(
        @"^\s*(\S+)\s+""((?:[^""\\]|\\.)*)""\s+_\(""((?:[^""\\]|\\.)*)""\)",
        RegexOptions.Compiled);

    private static void LoadConstellationNamesFile(string file, SkyCulture culture) {
        foreach(string rawLine in File.ReadLines(file)) {
            string line = rawLine.Trim();

            if(line.Length == 0)
                continue;

            if(line.StartsWith("#"))
                continue;

            Match match = _constellationNameRegex.Match(line);

            if(!match.Success)
                continue;

            string id = match.Groups[1].Value;
            string nativeName = UnescapeFabString(match.Groups[2].Value);
            string englishName = UnescapeFabString(match.Groups[3].Value);

            culture.ConstellationNames[id] = new ConstellationName(
                nativeName,
                englishName);
        }
    }
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `ModLibrary.Find(string)` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L187) | Locate the Core mod folder |
| `Mod.Empty`, `Mod.DirectoryPath` | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L24-L91) | Guard and path |
| `Mod.LoadStarBinaries` (format reference) | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L231-L285) | Same record layout the filter reads |
| `starBinaries` in Core `mod.toml` | [current/Content/Core/mod.toml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/mod.toml#L90-L91) | Name of the stock star file |

## Pitfalls and version notes

- **The filter depends on bit-identical math (unverified).** It works only if the stock binary
  stores exactly `(float)` of the mod's `double` J2000-ecliptic vector. The shipped `.bin` is not in
  the assemblies checkout, so this could not be checked. The current `generatestarbinary` terminal
  command writes `(cos dec cos ra, sin dec, cos dec sin ra)` with no obliquity rotation
  ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1798)); a binary regenerated with that command (or any
  change to the stock file) would match nothing, and every asterism would disappear silently.
  A tolerance (nearest neighbour within a few arcseconds) would be sturdier.
- **The filter ignores the star editor.** It always reads the stock Core file, even after the
  Star Editor switches binaries.
- **Index-based default, not persisted.** The default is index 29, which depends on directory
  enumeration order and on the folder set; adding or removing a culture shifts it. The chosen
  culture is not saved in `settings.ini`.
- **Legacy format.** These are pre-JSON Stellarium files (`.fab`); current Stellarium releases
  ship `index.json` sky cultures, which this parser does not read.
- **Licensing.** The culture data is GPL-2.0, and the repository's own LICENSE is GPL-2.0.

## See also

- [AT-HYG star catalog and star information](hyg-star-catalog.md)
- [Constellation lines and labels on the sky](sky-overlay-lines-and-labels.md)
- [Star binary switcher](star-binary-switcher.md)
- [Parse .fab and names files and filter to the stars present in the stock binary](../../../howto/celestials/systems-and-stars/bundle-and-query-a-star-catalog/bundle-and-query-a-star-catalog-2.md) - how-to article demonstrating this feature
- [Mirror Mod.LoadStarBinaries when parsing](../../../howto/celestials/systems-and-stars/read-and-replace-star-binaries/read-and-replace-star-binaries-1.md) - how-to article demonstrating this feature

[^oms-skyculturesrenderer-cs]: OhMyStars src/SkyCulturesRenderer.cs
[^oms-notice-txt]: OhMyStars NOTICE.txt
[^ksa-content-core-mod-toml]: Content/Core/mod.toml (game content, build 2026.10.10.5554)
[^ksa-mod-cs]: KSA.Mod (decompiled, build 2026.10.10.5554)
[^starmap-patches-modlibrarypatches-cs]: StarMap StarMap.Core/Patches/ModLibraryPatches.cs
[^ksa-program-cs]: KSA.Program (decompiled, build 2026.10.10.5554)
