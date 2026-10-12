# Game Feature

* [Ship third-party DLLs next to your mod](overview.md) - Which assemblies belong in a StarMap mod folder, which never do, and how the per-mod load context finds the ones that do

# How-To

* [Copy a vendored DLL beside the mod and let AssemblyDependencyResolver find it](ship-third-party-dlls-with-a-mod-1.md) - Ship an attribute or helper assembly such as ModMenu.Attributes.dll next to your mod DLL without a mod.toml dependency, and know what StarMap resolves from the game instead
* [Package NuGet dependencies with deps.json and ship exactly the reference closure](ship-third-party-dlls-with-a-mod-2.md) - Deploy only the assemblies your mod references (project libraries and a NuGet package such as Tomlyn) together with the entry assembly's deps.json, and never the DLLs StarMap or the game already supply
