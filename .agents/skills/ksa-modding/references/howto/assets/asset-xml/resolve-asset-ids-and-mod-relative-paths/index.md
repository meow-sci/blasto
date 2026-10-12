# Game Feature

* [Resolve asset ids and file paths correctly across mods](overview.md) - How the global asset registry, first-wins Id registration and Mod.GetPath path resolution behave, with a layout for your own assets and a way to reach another mod's or Core's files

# How-To

* [Lay out mod-folder-relative paths and prefixed global asset ids](resolve-asset-ids-and-mod-relative-paths-1.md) - Keep Path attributes relative to the mod folder, prefix every Id with the mod name, know that a second registration of an Id is ignored and that glTF mesh names are Ids, and keep paths case-exact for Linux
* [Reach another mod's or Core's files with absolute paths and Id-only references](resolve-asset-ids-and-mod-relative-paths-2.md) - Use rooted paths that Path.Combine passes through to point XML at files outside the mod folder, gate insertions on File.Exists, and reference Core's textures and meshes by Id alone
