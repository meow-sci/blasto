# Game Feature

* [Reference the game's assemblies from a mod project without shipping them](overview.md) - How a mod project compiles against the installed KSA DLLs with Private=false, and three ways to locate them - a KSADir property, tiered discovery and a shared props file

# How-To

* [Reference game DLLs with a KSADir property and wildcard Reference items](reference-game-assemblies-from-a-mod-project-1.md) - Point a props file at the installed KSA folder and turn a DLL glob into References with Private=False so builds compile against the real API and never copy game files
* [Resolve the game folder in tiers from Directory.Build.props](reference-game-assemblies-from-a-mod-project-2.md) - Try an environment variable, a local override, a sibling assemblies checkout and well-known install paths in order, and fail the build with one readable message, so contributors and CI share a project file
* [Share one KSADLLs.props across a family of mods and point CI at a private DLL checkout](reference-game-assemblies-from-a-mod-project-3.md) - Keep the DLL glob in a small props file imported by each project, pass KSADir and KSAModsDir on the command line, and let CI supply a private checkout without changing the project
