# Game Feature

* [Loading, validating and unloading part mods in a running game](overview.md) - How KSA's boot asset pipeline can be replayed for one mod after startup, what it takes to roll back or purge it, and the validation rules that stop the game's silent failures

# How-To

* [Load a part mod into a running game, one loader state per frame](load-and-unload-part-mods-at-runtime-1.md) - Replay KSA's boot asset pipeline for a single mod folder from a pre-GUI hook, reserving mesh-buffer headroom at AllModsLoaded and writing the mod folder atomically
* [Unload runtime-registered parts with a fail-closed gate and an ordered purge](load-and-unload-part-mods-at-runtime-2.md) - Refuse to unload while anything live might reference the mod, then remove exactly what the load registered in an order that never frees a GPU object something can still draw
* [Validate part XML against the game's silent failure modes before loading it](load-and-unload-part-mods-at-runtime-3.md) - Parse bundles with KSA's own XmlSerializer without side effects, then run fifteen pure rules over the parsed set so a bad id, path or material never reaches the registries
