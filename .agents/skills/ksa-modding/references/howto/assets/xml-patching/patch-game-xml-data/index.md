# Game Feature

* [Patch Core or another mod's XML data without copying files](overview.md) - How a mod changes stock or other mods' XML through KittenExtensions patch files listed under a patches key in mod.toml, and how to depend on KittenExtensions optionally

# How-To

* [Patch Core or another mod's XML with a patches array in mod.toml](patch-game-xml-data-1.md) - List patch files under patches in mod.toml and KittenExtensions applies them to one in-memory document of every manifest mod's XML before the game deserializes anything
* [Ship XML patches that apply only when KittenExtensions is installed](patch-game-xml-data-2.md) - Declare KittenExtensions as an optional StarMap dependency, ship patch files beside the DLL, make the code side degrade cleanly when the patch did not run, and package the patch files in the csproj
