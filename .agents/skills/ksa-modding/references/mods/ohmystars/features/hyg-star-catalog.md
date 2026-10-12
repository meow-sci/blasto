---
type: Mod Feature
title: AT-HYG star catalog and star information
description: Parse the 330k-row AT-HYG CSV into Hipparcos-keyed ecliptic directions and star records, special-case the central star, and browse stars by constellation, proper name or Bayer designation in an ImGui tab
mod: ohmystars
category: celestials
techniques: [data-file-parsing, imgui-window, starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapBeforeGui, starmap StarMapAfterGui]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.Program, KSA.Camera, KSA.Vehicle, Brutal.ImGuiApi.ImGui]
tags: [stars, catalog, hyg, hipparcos, imgui, search, csv]
---

The mod bundles the AT-HYG v3.2 star database as a 69 MB CSV and uses it three ways: as the source
of every star direction the overlays draw, as the name source for star labels, and as the data
behind an Information tab where the player browses stars by constellation, proper name or Bayer
designation, reads their catalog values, and launches the star pointer, the navball marker or
"Orient to Star".[^oms-skyculturesrenderer-cs] [^oms-ohmystarswindow-cs]

## How it works

1. **The file.** `src/athyg_32_reduced_m10.csv` has an AT-HYG header (`id, tyc, gaia, hyg, hip, hd,
   ..., proper, ra, dec, ..., dist, ..., mag, absmag, ..., spect, ...`) with RA in hours and Dec in
   degrees ([src/athyg_32_reduced_m10.csv](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/athyg_32_reduced_m10.csv#L1-L3)). Counted from the shipped file: 330,327 rows,
   108,592 with a Hipparcos number, 460 with a proper name, 1,525 with a Bayer letter, and 12 named
   or Bayer rows without a Hipparcos number. NOTICE.txt credits David Nash, CC BY-SA 4.0
   ([NOTICE.txt](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/NOTICE.txt)).[^oms-notice-txt]
2. **Header-driven parse.** `LoadCatalog` reads the header, finds each needed column with
   `Array.IndexOf`, and gives up if any is missing; every row is split by a small quote-aware CSV
   splitter and parsed with `CultureInfo.InvariantCulture`
   ([src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L461-L580), [src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L668-L706)).
3. **Keys.** The key is the Hipparcos number, because that is what Stellarium sky cultures
   reference. Rows without one but with a proper name or Bayer letter get a synthetic key
   `10_000_000 + id` so they can still be selected (Wolf 359 is the comment's example); everything
   else is skipped ([src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L43-L45)).
4. **Per-star data.** For each kept row the mod stores the ecliptic unit vector
   (`StarDirectionConverter.RaDecToDirection`), a display name (proper name, else
   `"<con> <bayer>"`, else `"HIP n"`), and a `StarInformation` record with the expanded Bayer letter
   (`Alp` to `Alpha`), RA as `hh:mm:ss`, signed Dec, magnitudes, spectral type and the distance
   converted from parsecs to light years ([src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L582-L666)).
5. **The central star.** The row whose proper name is `Sol` is remembered as `_centralStarHip`. Its
   catalog direction is meaningless, so the label, the pointer and orientation use the live
   position of the system's root body instead: `camera.GetPositionEgo(centralStarBody)` for screen
   work and `-vehicle.GetPositionEcl()` for attitude
   ([src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L47-L53), [src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L304-L329)).
6. **Information tab.** Three selection modes: Constellations (active culture, then one of its
   stars), Proper (every named star) and Bayer (every Bayer star as "Cassiopeia Iota"), the last two
   as combos with an `ImGui.InputText` search filter inside the popup
   ([src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L713-L1012)). The selected star's values are listed with help
   tooltips, plus a spectral-class legend sub-view. A Proper or Bayer selection is also labelled on
   the sky by `DrawSelectedDropdownStarName`.
7. **Per-vehicle memory.** The tab keeps a `Dictionary<Vehicle, (ConstellationId, StarHip)>` and
   swaps the selection when `Program.ControlledVehicle` changes, so each vessel or kitten remembers
   its own star ([src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L77-L82), [src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L723-L741)).

## Lifecycle and timing

- **Load: `startup:all-mods-loaded`.** Parsing all 330k rows happens synchronously in
  `[StarMapAllModsLoaded]`, inside the `Program` constructor, before the first frame
  ([src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L194-L205)).
- **Browse: `frame:draw-ui`.** The Information tab is part of the Oh My Stars window drawn from
  `[StarMapBeforeGui]` (prefix on `Program.OnDrawUiFrame`); the sky label for the selected star is
  drawn later in the same frame from `[StarMapAfterGui]`. `Program.ControlledVehicle` is read on the
  main thread after this frame's solver results were applied.

## Key code

| Path | Role |
|---|---|
| [src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L461-L580) | Header-indexed CSV load, synthetic keys, Sol detection |
| [src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L582-L706) | Formatting helpers and CSV splitter |
| [src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L80-L171) | Query API for the window and renderers |
| [src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L713-L1012) | Information tab and selectors |

## Snippets

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

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `Program.ControlledVehicle` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L502-L516) | Per-vehicle selection memory |
| `Camera.GetPositionEgo(IPosition)` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L235-L250) | Live position of the central star |
| `Vehicle.GetPositionEcl()` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L954) | Direction to the central star for orientation |
| `ImGui.BeginCombo`, `Selectable`, `InputText(ImString, ImInputString, ...)` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L6396) | Selectors and search |

## Pitfalls and version notes

- **Startup cost.** Parsing a 69 MB CSV with string splitting on the main thread lengthens game
  start. A pre-filtered binary of just the needed columns would load much faster.
- **README drift.** The README's install tree lists `hyg_v42.csv`; the build actually ships
  `athyg_32_reduced_m10.csv` ([README.md](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/README.md), [src/OhMyStars.csproj](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStars.csproj#L44)).[^oms-readme-md]
- **Sol is matched by name.** Another system's central star would need its own catalog name; the
  comments acknowledge this.
- **Selections are keyed by `Vehicle` reference.** Entries for vehicles from an earlier save stay in
  the dictionary until the process exits.

## See also

- [Stellarium sky cultures](stellarium-sky-cultures.md)
- [Star pointer](star-pointer.md)
- [Navball star marker](navball-star-marker.md)
- [Orient a vessel to a star](orient-to-star.md)
- [Header-indexed CSV parse into HIP-keyed ecliptic directions with synthetic keys](../../../howto/celestials/systems-and-stars/bundle-and-query-a-star-catalog/bundle-and-query-a-star-catalog-1.md) - how-to article demonstrating this feature

[^oms-skyculturesrenderer-cs]: OhMyStars src/SkyCulturesRenderer.cs
[^oms-ohmystarswindow-cs]: OhMyStars src/OhMyStarsWindow.cs
[^oms-notice-txt]: OhMyStars NOTICE.txt
[^oms-readme-md]: OhMyStars README.md
