---
type: How-To
title: Mirror Mod.LoadStarBinaries when parsing
description: Read a star binary with the same count-then-16-byte-record layout the game uses, keep the float direction bits as identity for joining catalog entries, and cap the read by the file length
category: celestials
group: systems-and-stars
topic: read-and-replace-star-binaries
approach: 1
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ohmystars/features/stellarium-sky-cultures, mods/ohmystars/features/star-binary-switcher]
techniques: [data-file-parsing]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded]
game_types: [KSA.Mod, KSA.ModLibrary]
tags: [star-binary, parsing, bits, identity, starfield]
---
Outcome: the directions (and optionally scales and colours) of the stars the game actually renders, available to join against a catalog, count, or sample. Demonstrated by Oh My Stars ([Stellarium sky cultures](../../../../mods/ohmystars/features/stellarium-sky-cultures.md), [Star binary switcher](../../../../mods/ohmystars/features/star-binary-switcher.md)).

## Background: how the game does it

`Mod.LoadStarBinaries` opens each listed file under the mod's `DirectoryPath`, reads an `int32` count and then `count` records: `float3` direction (normalised), `byte` scale, `byte r, g, b` ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L231-L285)):

```csharp
// current/decomp/KSA/Mod.cs L262-L282
using FileStream input = File.OpenRead(text2);
using BinaryReader binaryReader = new BinaryReader(input);
int num = binaryReader.ReadInt32();
LogCategory log3 = DefaultCategory.Log;
LogCategory logCategory3 = log3;
LogString<Debug> message3 = new LogString<Debug>(30, 2, log3, out shouldAppend);
if (shouldAppend)
{
	message3.AppendLiteral("loading ");
	message3.AppendFormatted(num);
	message3.AppendLiteral(" star binaries from '");
	message3.AppendFormatted(text2);
	message3.AppendLiteral("'");
}
logCategory3.Debug(message3, "LoadStarBinaries", "C:\\prototype-planet\\KSA\\ModLibrary.cs", 342);
for (int j = 0; j < num; j++)
{
	float3 value = new float3(binaryReader.ReadSingle(), binaryReader.ReadSingle(), binaryReader.ReadSingle());
	value = float3.Normalize(value);
	byte scale = binaryReader.ReadByte();
	starTechnique.AddInstance(color: new byte4(binaryReader.ReadByte(), binaryReader.ReadByte(), binaryReader.ReadByte(), byte.MaxValue), viewport: Program.MainViewport, forward: value, scale: scale, distance: 19.5f);
```

Core's file is named in [current/Content/Core/mod.toml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/mod.toml#L90-L91); the game does not tell you the star's catalog number, only its direction, so a join is made by the exact float values.

## Lifecycle and timing

The read is plain file IO. Oh My Stars does it in `[StarMapAllModsLoaded]` (`startup:all-mods-loaded`, after `ModLibrary.LoadAll`, so `ModLibrary.Find("Core")` returns the Core mod with its `DirectoryPath`). Nothing needs the universe or ImGui. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Locate the file** through the owning mod: `ModLibrary.Find("Core")` and `Path.Combine(coreMod.DirectoryPath, "<file>.bin")`; if it is missing, disable the dependent feature rather than fail.

2. **Read the count and clamp it by file length** (a corrupt header must not overrun), then read `x, y, z` and skip the 4 trailing bytes:

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

3. **Use the float bits as the identity.** The game normalises the direction after reading, and the catalog conversion in the mod produces the same float values; `BitConverter.SingleToInt32Bits` of `(x, y, z)` is a hash key:

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

4. **Read your own copy when sampling.** The switcher's loader opens a file with `BinaryReader`, caps the count by `(length - 4) / 16`, and re-reads the fields in the same order ([src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L963-L998)).

## Complete example

| File | Role |
|---|---|
| [src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L415-L458) | Rendered-star bit-exact filter |
| [src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L963-L998) | Count and record reader for sampling |

## Variations and gotchas

- **The record layout is a de facto format;** the header count is trusted by the game, not validated.
- **Other mods' binaries** are listed in their own `starBinaries`; enumerate mods through `ModLibrary` (see approach 2).
- **Do not assume the order** of stars in a file means anything.

## See also

- [Overview](overview.md); [approach 2](read-and-replace-star-binaries-2.md)
- [Stellarium sky cultures](../../../../mods/ohmystars/features/stellarium-sky-cultures.md)
- [Bundle a star catalog and join it to the sky](../../../celestials/systems-and-stars/bundle-and-query-a-star-catalog/overview.md)
