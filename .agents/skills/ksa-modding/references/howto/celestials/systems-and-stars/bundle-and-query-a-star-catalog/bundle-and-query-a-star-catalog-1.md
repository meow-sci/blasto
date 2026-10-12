---
type: How-To
title: Header-indexed CSV parse into HIP-keyed ecliptic directions with synthetic keys
description: Parse a bundled AT-HYG star catalog by header name into Hipparcos-keyed ecliptic unit vectors, names and information records, giving named stars without a Hipparcos number a synthetic key and the Sun a live position
category: celestials
group: systems-and-stars
topic: bundle-and-query-a-star-catalog
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ohmystars/features/hyg-star-catalog]
techniques: [data-file-parsing]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapBeforeGui, starmap StarMapAfterGui]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.Program, KSA.Camera, KSA.Vehicle, KSA.ModLibrary, KSA.Mod, KSA.Universe]
tags: [star-catalog, athyg, csv, hipparcos, ecliptic, search]
---
Outcome: a dictionary from Hipparcos number to ecliptic direction, display name and catalog values, plus an Information tab to browse stars by constellation, proper name or Bayer designation. Demonstrated by Oh My Stars ([HYG star catalog](../../../../mods/ohmystars/features/hyg-star-catalog.md)). The catalog is the AT-HYG v3.2 database (David Nash, CC BY-SA 4.0), reduced to `athyg_32_reduced_m10.csv`.

## Background: how the game does it

The game offers no star names. The stock star binary has directions only ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L231-L285)); a mod joins its own catalog to the sky by direction. The Hipparcos number is the key because Stellarium constellation files reference it. Catalog RA is in hours and Dec in degrees (J2000 equatorial); the game's frame is ecliptic, so the conversion rotates about X by the obliquity.

## Lifecycle and timing

- **Load: `startup:all-mods-loaded`.** Parsing all 330k rows happens synchronously in `[StarMapAllModsLoaded]`, inside the `Program` constructor, before the first frame.
- **Browse: `frame:draw-ui`.** The Information tab is drawn from `[StarMapBeforeGui]`; the sky label for the selected star from `[StarMapAfterGui]`. `Program.ControlledVehicle` is read on the main thread after this frame's solver results were applied.

See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Read the header and find each column by name,** giving up if any is missing (never by column number, so a reordered file still works). Reduce the file first: the mod ships 330,327 rows, 108,592 of them with a Hipparcos number:

```csharp
// src/SkyCulturesRenderer.cs L461-L496
private static void LoadCatalog(string path) {
    hipToDirection.Clear();
    hipToName.Clear();
    hipToInformation.Clear();
    _centralStarHip = 0;

    if(!File.Exists(path))
        return;

    using StreamReader reader = new StreamReader(path);

    string? headerLine = reader.ReadLine();

    if(headerLine == null)
        return;

    string[] headers = SplitCsvLine(headerLine);

    int idIndex = Array.IndexOf(headers, "id");
    int hipIndex = Array.IndexOf(headers, "hip");
    int raIndex = Array.IndexOf(headers, "ra");
    int decIndex = Array.IndexOf(headers, "dec");
    int bayerIndex = Array.IndexOf(headers, "bayer");
    int constellationIndex = Array.IndexOf(headers, "con");
    int properIndex = Array.IndexOf(headers, "proper");
    int magnitudeIndex = Array.IndexOf(headers, "mag");
    int absoluteMagnitudeIndex = Array.IndexOf(headers, "absmag");
    int spectralTypeIndex = Array.IndexOf(headers, "spect");
    int distanceIndex = Array.IndexOf(headers, "dist");


    if(hipIndex < 0 || raIndex < 0 || decIndex < 0 ||
        bayerIndex < 0 || constellationIndex < 0 || properIndex < 0 ||
        magnitudeIndex < 0 || absoluteMagnitudeIndex < 0 || spectralTypeIndex < 0 || distanceIndex < 0)
        return;

```

2. **Key by Hipparcos number;** rows without one but with a proper name or Bayer letter get a synthetic key `10_000_000 + id` (Wolf 359 is the example), everything else is skipped:

```csharp
// src/SkyCulturesRenderer.cs L512-L536
string hipText = parts[hipIndex];
string properName = parts[properIndex];
string bayerRaw = parts[bayerIndex];
bool hasProperName = !string.IsNullOrWhiteSpace(properName);
bool hasBayer = !string.IsNullOrWhiteSpace(bayerRaw);

bool hasRealHip = int.TryParse(
    hipText,
    NumberStyles.Integer,
    CultureInfo.InvariantCulture,
    out int parsedHip) && parsedHip > 0;

int hip;
if(hasRealHip) {
    hip = parsedHip;
} else if((hasProperName || hasBayer) &&
    idIndex >= 0 && idIndex < parts.Length &&
    int.TryParse(parts[idIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rowId)) {
    // Some named/Bayer catalog stars (e.g. Wolf 359) have no Hipparcos number at all. Synthesize
    // a stable id from the catalog's own row id so they can still be selected, oriented to, and
    // labeled - offset keeps synthetic ids clear of the real HIP range (max around 120,416).
    hip = SyntheticHipOffset + rowId;
} else {
    continue;
}
```

3. **Convert J2000 equatorial to the game's ecliptic direction** (rotate by minus the obliquity about X):

```csharp
// src/Math.cs L148-L168
public static class StarDirectionConverter {
    public static double3 RaDecToDirection(double raHours, double decDegrees) {
        double ra = raHours * System.Math.PI / 12.0;
        double dec = decDegrees * System.Math.PI / 180.0;

        double cosDec = System.Math.Cos(dec);

        // J2000 equatorial direction
        double x = System.Math.Cos(ra) * cosDec;
        double y = System.Math.Sin(ra) * cosDec;
        double z = System.Math.Sin(dec);

        // Rotate by -obliquity around X, matching the star catalog's equatorial-to-ecliptic conversion
        const double obliquityRadians = -23.439281 * System.Math.PI / 180.0;
        double cosObliquity = System.Math.Cos(obliquityRadians);
        double sinObliquity = System.Math.Sin(obliquityRadians);
        double yRotated = y * cosObliquity - z * sinObliquity;
        double zRotated = y * sinObliquity + z * cosObliquity;

        return new double3(x, yRotated, zRotated);
    }
```

4. **Store per star** the direction, a display name (proper name, else `"<con> <bayer>"`, else `"HIP n"`) and a record with the expanded Bayer letter, RA as `hh:mm:ss`, signed Dec, magnitudes, spectral type and the distance converted from parsecs to light years:

```csharp
// src/SkyCulturesRenderer.cs L554-L579
    hipToDirection[hip] = StarDirectionConverter.RaDecToDirection(raHours, decDegrees);

    string displayName = GetDisplayName(
        properName,
        parts[constellationIndex],
        bayerRaw,
        hip);
    hipToName[hip] = displayName;
    hipToInformation[hip] = new StarInformation(
        hip,
        displayName,
        ExpandBayerDesignation(bayerRaw),
        parts[constellationIndex],
        hasProperName,
        hasRealHip ? hip.ToString(CultureInfo.InvariantCulture) : string.Empty,
        FormatRightAscension(raHours),
        FormatSignedDeclination(parts[decIndex]),
        parts[magnitudeIndex],
        parts[absoluteMagnitudeIndex],
        parts[spectralTypeIndex],
        FormatDistanceLightYears(parts[distanceIndex]));

    if(string.Equals(properName.Trim(), "Sol", StringComparison.OrdinalIgnoreCase)) {
        _centralStarHip = hip;
    }
}
```

5. **Treat the Sun specially.** The row whose proper name is `Sol` is remembered; its catalog direction is meaningless, so labels and pointers use the live position of the system's root body: `camera.GetPositionEgo(centralStarBody)` for screen work and `-vehicle.GetPositionEcl()` for attitude.

6. **Browse with searchable combos.** Constellations (active culture, then one of its stars), Proper and Bayer, the last two with an `ImGui.InputText` filter inside the popup:

```csharp
// src/OhMyStarsWindow.cs L946-L964
StarInformation star = stars[selectedIndex];
if(ImGui.BeginCombo("Star", star.DisplayName)) {
    ImGui.InputText(new ImString("Search"), _properSearchBuffer);

    // An empty search shows every star; otherwise only names containing the search text.
    string properSearch = _properSearchBuffer.Value;
    IEnumerable<StarInformation> filteredStars = properSearch.Length == 0
        ? stars
        : stars.Where(s => s.DisplayName.Contains(properSearch, StringComparison.OrdinalIgnoreCase));

    foreach(StarInformation candidate in filteredStars) {
        bool selected = candidate.Hip == _selectedStarHip;

        if(ImGui.Selectable(candidate.DisplayName, selected)) {
            _selectedStarHip = candidate.Hip;
        }
    }
    ImGui.EndCombo();
}
```

7. **Remember a selection per vehicle** in a `Dictionary<Vehicle, (ConstellationId, StarHip)>` and swap when `Program.ControlledVehicle` changes.

## Complete example

| File | Role |
|---|---|
| [src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L461-L580) | Header-indexed CSV load, synthetic keys, Sol detection |
| [src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L582-L706) | Formatting helpers and CSV splitter |
| [src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L713-L1012) | Information tab and selectors |
| [src/Math.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/Math.cs#L148-L168) | RA/Dec to ecliptic direction |

## Variations and gotchas

- **A 69 MB CSV is too large to ship raw;** the mod uses a magnitude-reduced file. Parsing cost is paid once at load.
- **Use `CultureInfo.InvariantCulture`** for every number; the quote-aware CSV splitter handles quoted names.
- **Attribution.** The catalog is CC BY-SA 4.0 and the sky-culture data GPL-2.0; carry `NOTICE.txt`.
- **Nothing here changes the stars drawn.** To change the field itself see [read and replace star binaries](../read-and-replace-star-binaries/overview.md).

## See also

- [Overview](overview.md); [approach 2](bundle-and-query-a-star-catalog-2.md)
- [HYG star catalog](../../../../mods/ohmystars/features/hyg-star-catalog.md)
- [Read and replace star binaries](../../../celestials/systems-and-stars/read-and-replace-star-binaries/overview.md), [Draw lines and labels on the sky](../../../rendering/overlays/draw-lines-on-the-sky/overview.md)
