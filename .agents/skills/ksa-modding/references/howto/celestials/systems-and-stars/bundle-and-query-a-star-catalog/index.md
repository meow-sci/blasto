# Game Feature

* [Bundle a star catalog and constellation data and join them to the game's sky](overview.md) - Ship a reduced AT-HYG CSV keyed by Hipparcos number, convert equatorial J2000 to the game's ecliptic directions, and optionally load Stellarium constellation files filtered to the stars present in the stock star binary

# How-To

* [Header-indexed CSV parse into HIP-keyed ecliptic directions with synthetic keys](bundle-and-query-a-star-catalog-1.md) - Parse a bundled AT-HYG star catalog by header name into Hipparcos-keyed ecliptic unit vectors, names and information records, giving named stars without a Hipparcos number a synthetic key and the Sun a live position
* [Parse .fab and names files and filter to the stars present in the stock binary](bundle-and-query-a-star-catalog-2.md) - Load Stellarium legacy constellationship and constellation_names files for many sky cultures, join them to the catalog by Hipparcos number and keep only segments whose both ends match a star direction in the stock star binary
