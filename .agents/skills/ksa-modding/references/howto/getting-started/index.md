# Subdirectories

* [build](build/index.md) - Project setup, build, deploy and release: game references, deploy on every build, generated mod.toml, reference-free and CI builds, and packaging.
* [dependencies](dependencies/index.md) - Depending on other mods and shipping what a mod needs at run time: dependency declarations, shared contracts and inter-mod APIs, third-party and native libraries.
* [lifecycle](lifecycle/index.md) - When mod code runs and how it stays well behaved: startup hooks, fault containment, shutdown, logging and version checks; the existing game-loop-and-lifecycle reference also lives here.
* [loader](loader/index.md) - How StarMap finds, loads and binds a mod: the minimal project, the launch and discovery sequence, mod.toml and the mod folder layout.
