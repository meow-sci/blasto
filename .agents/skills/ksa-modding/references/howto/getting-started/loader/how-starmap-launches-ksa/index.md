# Game Feature

* [How StarMap launches KSA and loads your mod](overview.md) - End-to-end reference for StarMap.exe - config, load contexts, working-directory trick, manifest discovery, dependency ordering and hook binding - with the instance-path and lazy-assembly sub-topics

# How-To

* [Follow the boot sequence: host process, load contexts and hook binding](how-starmap-launches-ksa-1.md) - A numbered walkthrough of StarMap.exe from StarMapConfig.json to the first mod hook, with the three load contexts, what resolves where and how to observe it from a mod
* [Preload a lazily loaded game assembly into the core load context before patching it](how-starmap-launches-ksa-4.md) - Patch code in a game DLL the game has not loaded yet by loading it into the context that holds KSA.dll from StarMapBeforeMain, so the patched copy is the one the game calls
* [Run separate KSA instances by redirecting the documents folder](how-starmap-launches-ksa-3.md) - Point every per-user KSA path at a different folder with a Harmony prefix on Constants.DocumentsFolderPath, driven by -InstancePath or STARMAP_INSTANCE_PATH, and keep your own mod data instance-aware
* [Understand the mod folder, manifest and enable/ordering rules](how-starmap-launches-ksa-2.md) - Where KSA and StarMap look for mods, how manifest.toml enables and orders them, why a mod can seem ignored, and how enabling a new mod leads to StarMap's restart prompt
