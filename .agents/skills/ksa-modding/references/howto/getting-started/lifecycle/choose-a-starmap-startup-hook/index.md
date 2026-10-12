# Game Feature

* [Choose and use the right StarMap lifecycle hook](overview.md) - What game state exists at StarMapBeforeMain, StarMapImmediateLoad and StarMapAllModsLoaded, how to pick between them, and how to verify a hook is bound and firing

# How-To

* [Diagnose a StarMap hook that never fires with a signature checklist](choose-a-starmap-startup-hook-4.md) - Check a hook against StarMap's name-based binding rules, exact parameter lists and firing-point table, and prove it ran with a one-line probe per hook
* [StarMapAllModsLoaded: work that needs the full mod set](choose-a-starmap-startup-hook-3.md) - Use the postfix on ModLibrary.LoadAll to install the bulk of your Harmony patches, check the game version, set up logging and start background warm-ups once every mod and asset bundle is loaded
* [StarMapBeforeMain: install patches on code that runs during startup](choose-a-starmap-startup-hook-1.md) - Use StarMapBeforeMain for loader-level work only - Harmony patches on code that runs before AllModsLoaded, looked up by name, with a fail-soft install
* [StarMapImmediateLoad: register data before assets deserialize](choose-a-starmap-startup-hook-2.md) - Use the per-mod prefix on Mod.PrepareSystems to write files, inject enum values or capture your KSA.Mod before asset XML is read, and avoid the parameterless-signature trap
