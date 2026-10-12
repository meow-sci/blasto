# Game Feature

* [Ship assets and code in one mod folder and find it at runtime](overview.md) - How a single folder serves KSA as a content mod and StarMap as a code mod, how asset paths resolve against the mod root, and how code locates that folder at runtime

# How-To

* [Find your mod's install folder at runtime: Assembly.Location, ModLibrary.Find, Documents](ship-content-and-code-in-one-mod-3.md) - Locate files shipped beside the mod DLL reliably, even under instance paths and stale copies, using the loaded assembly's location first and the game's mod library as a fallback
* [Lay out a flat mod folder with an explicit assets list](ship-content-and-code-in-one-mod-1.md) - One folder that is both a KSA content mod and a StarMap code mod - asset XML beside Meshes, Textures and Sounds, every file listed in mod.toml, paths resolved against the mod root
* [Ship mixed content with a csproj content copy, an assets guard and a build stamp](ship-content-and-code-in-one-mod-2.md) - KSAGolf's variant of the one-folder content-and-code mod - assets XML at the root, glb/png/wav copied beside the DLL, a script that checks the assets list, and the KSA build stamped into the assembly
