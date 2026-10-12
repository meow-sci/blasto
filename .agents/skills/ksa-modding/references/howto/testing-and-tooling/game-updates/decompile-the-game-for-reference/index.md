# Game Feature

* [Keep a searchable decompiled copy of the game next to your mod](overview.md) - Decompile the game's assemblies with ilspycmd into a gitignored folder stamped with the game version, or into a private repository where two builds produce a readable diff

# How-To

* [Decompile every build into a private git repo with one ilspycmd version kept across updates and diff only what your mod touches](decompile-the-game-for-reference-2.md) - Decompile the game assemblies deterministically into a private repository so two builds produce a readable git diff, and narrow that diff to the files and members your mod's API surface names
* [Refresh a gitignored decompiled tree with ilspycmd, stamp its version and point your assistant at it](decompile-the-game-for-reference-1.md) - Decompile the game assemblies you depend on into a gitignored folder with one command, write a version stamp beside it and tell tools and AI assistants to search it instead of decompiling ad hoc
