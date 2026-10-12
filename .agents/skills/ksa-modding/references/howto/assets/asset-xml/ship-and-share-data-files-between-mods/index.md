# Game Feature

* [Ship data files with a mod and let other mods contribute more](overview.md) - How a code mod packages and finds its own data files, scans other mods for contributions, reads a convention folder inside every installed mod, and writes a new mod folder from the game

# How-To

* [Let other mods add data with a marker key in JSON files scanned across mod folders](ship-and-share-data-files-between-mods-2.md) - Scan every folder beside your own mod for JSON files whose root carries a marker key, load your own files first and sort the rest, record which keys came from other mods and resolve lookups from specific to generic
* [Read a convention folder inside every installed mod with a strict reader](ship-and-share-data-files-between-mods-3.md) - Walk the manifest at StarMapAllModsLoaded, read every XML in a KSArmory folder of each mod, refuse unknown attributes by name, namespace keys per pack and audit unresolved parts after load
* [Ship data beside the DLL with an explicit copy list and resolve the mod root from the assembly](ship-and-share-data-files-between-mods-1.md) - Write a mod.toml StarMap understands without a StarMap table, deploy exactly the data files you ship with an overridable game path, and find your own folder from Assembly.Location with fallbacks
* [Write a new content mod folder and manifest entry from inside the game](ship-and-share-data-files-between-mods-4.md) - Validate a mod id against every place KSA knows it, write XML files and then mod.toml through temp files and moves, and append an enabled manifest entry so the folder loads at the next launch
