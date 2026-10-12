---
type: How-To
title: Parse .fab and names files and filter to the stars present in the stock binary
description: Load Stellarium legacy constellationship and constellation_names files for many sky cultures, join them to the catalog by Hipparcos number and keep only segments whose both ends match a star direction in the stock star binary
category: celestials
group: systems-and-stars
topic: bundle-and-query-a-star-catalog
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ohmystars/features/stellarium-sky-cultures]
techniques: [data-file-parsing]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapBeforeGui]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.Mod, KSA.ModLibrary, KSA.Program]
tags: [stellarium, sky-cultures, constellations, fab, star-binary, filter]
---
Outcome: 53 Stellarium sky cultures (Modern/IAU variants, Chinese, Norse, Maori, Navajo, Babylonian, Egyptian and more) selectable at runtime, with segments and labels resolved to ecliptic vectors. Sub-step of the catalog topic: Stellarium sky cultures layered on the catalog join. Demonstrated by Oh My Stars ([Stellarium sky cultures](../../../../mods/ohmystars/features/stellarium-sky-cultures.md)).

## Background: how the game does it

A culture is two legacy Stellarium text files in a folder: `constellationship.fab` and `constellation_names.eng.fab`. Both reference stars by Hipparcos number, which is why the catalog in [approach 1](bundle-and-query-a-star-catalog-1.md) is keyed by it. The stock star binary (listed by Core's `mod.toml`, [current/Content/Core/mod.toml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/mod.toml#L90-L91)) holds only some of those stars, so a segment is drawable only if both ends exist in the game's own file. The record layout is the one `Mod.LoadStarBinaries` reads ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L231-L285)).

## Lifecycle and timing

`StellariumRenderer.Init` (from `OnFullyLoaded`) calls `SkyCulturesRenderer.Init`, which reads the star binary, all culture folders and the catalog, then resolves the default culture; StarMap fires `[StarMapAllModsLoaded]` as a postfix on `ModLibrary.LoadAll`, called from the `Program` constructor ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L942)), so `ModLibrary.Find("Core")` already returns the Core mod with its `DirectoryPath`. Switching a culture from the combo (in `[StarMapBeforeGui]`, `frame:draw-ui`) re-resolves synchronously; the overlay drawn later in the same frame already uses the new one. Resolution happens on a switch, never every frame. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Discover cultures:** enumerate sub-directories, skip any without a `constellationship.fab`, turn the folder name into a display name, and fall back to any `constellation_names.*.fab` if the English file is missing.

2. **Parse the line file.** Each non-comment line is `<id> <N> <hip> <hip> ...` with `2N` numbers forming `N` independent segments (for example `And 5  677 3092 3092 5447 ...`); lines whose count does not match are skipped:

```csharp
// src/SkyCulturesRenderer.cs L769-L802
    continue;
}

int expectedStarIdCount = segmentCount * 2;

if(parts.Length < 2 + expectedStarIdCount)
    continue;

List<ConstellationSegment> segments = new();

for(int i = 0; i < segmentCount; i++) {
    int fromIndex = 2 + i * 2;
    int toIndex = fromIndex + 1;

    if(!int.TryParse(
        parts[fromIndex],
        NumberStyles.Integer,
        CultureInfo.InvariantCulture,
        out int fromHip)) {
        continue;
    }

    if(!int.TryParse(
        parts[toIndex],
        NumberStyles.Integer,
        CultureInfo.InvariantCulture,
        out int toHip)) {
        continue;
    }

    segments.Add(new ConstellationSegment(fromHip, toHip));
}

culture.Constellations[abbrev] = segments;
```

3. **Parse the names file** with a compiled regex for `<id> "<native>" _("<english>")` with escaped quotes; show the English name when present, otherwise the native one:

```csharp
// src/SkyCulturesRenderer.cs L806-L828
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

```

4. **Read the stock star binary and keep the bit patterns** of every `(x, y, z)` in a hash set (the record is 16 bytes: skip the 4 trailing bytes):

```csharp
// src/SkyCulturesRenderer.cs L415-L449
private static void LoadRenderedStarDirections() {
    renderedStarDirections.Clear();

    Mod? coreMod = ModLibrary.Find("Core");
    if(coreMod is null || coreMod == Mod.Empty || string.IsNullOrWhiteSpace(coreMod.DirectoryPath))
        return;

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
```

5. **Filter by exact float bits:** cast the mod's own `double` direction to `float` and look up the bits; a segment is kept only if both ends match. If the file is missing the filter is disabled and every segment is kept:

```csharp
// src/SkyCulturesRenderer.cs L451-L458
private static bool IsRenderedStar(double3 direction) {
    if(renderedStarDirections.Count == 0)
        return true;

    return renderedStarDirections.Contains(new StarDirectionKey(
        BitConverter.SingleToInt32Bits((float)direction.X),
        BitConverter.SingleToInt32Bits((float)direction.Y),
        BitConverter.SingleToInt32Bits((float)direction.Z)));
```

6. **Resolve on switch.** Turn every segment into a pair of precomputed ecliptic unit vectors, collect the star names used, and compute one label direction per constellation as the normalised sum of its stars' directions; drawing then only iterates flat lists ([src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L333-L413)).

## Complete example

| File | Role |
|---|---|
| [src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L708-L841) | Folder discovery, `.fab` parsers |
| [src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L333-L413) | Segment and label resolution |
| [src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L415-L459) | Rendered-star bit-exact filter |
| [skycultures/modern/constellationship.fab](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/skycultures/modern/constellationship.fab) | An example line file |

## Variations and gotchas

- **Bit-exact matching only works if both sides derive from the same conversion.** A catalog built another way would match nothing; the mod's fallback keeps every segment, which draws lines to stars the game does not render.
- **The default culture index is hard-coded** (29, `modern` with the shipped folders in ordinal order); adding folders shifts it.
- **Licensing.** The sky-culture data is GPL-2.0 and the repo's `NOTICE.txt` records that every other Stellarium file was removed.

## See also

- [Overview](overview.md); [approach 1](bundle-and-query-a-star-catalog-1.md)
- [Stellarium sky cultures](../../../../mods/ohmystars/features/stellarium-sky-cultures.md)
- [Draw lines and labels on the sky](../../../rendering/overlays/draw-lines-on-the-sky/overview.md), [Read and replace star binaries](../../../celestials/systems-and-stars/read-and-replace-star-binaries/overview.md)
