# Game Feature

* [Redirect the game to patched copies of its XML and coordinate edits between mods](overview.md) - Two ways to substitute the XML the game reads without editing the install, how to edit the copies safely, and how two mods share one patched copy

# How-To

* [Edit shadow copies with anchor-verified text patches and generated XML between markers](redirect-game-xml-loading-3.md) - Make stock XML edits that survive game updates by requiring each stock text anchor to occur exactly once, guarding with idempotent markers, formatting numbers invariantly, and splicing generated XML between begin and end markers
* [Let two mods edit the same Core file with a shared shadow folder and a named mutex](redirect-game-xml-loading-4.md) - Coordinate two mods that patch the same Core XML by building one shared shadow copy under a named mutex, resetting it once per launch with a process-start token, and compiling the coordinator into both mods
* [Swap Core's XML entries for patched copies with prefixes on the Mod loaders](redirect-game-xml-loading-1.md) - Write a patched copy of a Core XML file at StarMapBeforeMain and Harmony-prefix the non-generic Mod loader methods to replace the file name in Core's own lists with the copy's absolute path
* [Transpile the Mod loaders so XmlLoader.Load calls read from an in-memory document](redirect-game-xml-loading-2.md) - Use a Harmony transpiler on four KSA.Mod methods to swap every constructed XmlLoader.Load<T> call for your own generic loader, keeping the OnDataLoad contract and the override-aware serializer
