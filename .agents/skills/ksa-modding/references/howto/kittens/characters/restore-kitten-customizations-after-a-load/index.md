# Game Feature

* [Restore kitten colours, clips and tuning after a save load](overview.md) - Why every mod-held reference to a kitten is dead after a native load and three ways unscience captures per-kitten state against vehicle ids and rebinds it to the restored KittenEva objects

# How-To

* [Rebind cloned kitten material sets to the restored kittens by vehicle id](restore-kitten-customizations-after-a-load-1.md) - doh keeps its detached GPU material sets in a cache across a native load, never despawns during reset, then claims one set per restored KittenEva by vehicle id and character, releases the unclaimed ones and rejects duplicate ids before the world is reset
* [Restore kitten colours by material name, only where the asset object is still the one written](restore-kitten-customizations-after-a-load-2.md) - Humble Arteest records the original colour and the exact asset object before its first write to a material handle, saves colours by material name, and on reset or restore touches only handles whose AssetMap entry is still that object because material slots are reused
* [Save the forced clip with its label, source and phase, plus the walking tuning, and rebind them to the restored kitten](restore-kitten-customizations-after-a-load-3.md) - Kitten Animations captures twenty tuning fields, the forced clip by exact source and label, its playback clock and any latched expression, validates them before the reset, then re-matches the clip after the load and samples the restored phase on the next render update
