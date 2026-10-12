# Game Feature

* [Ship custom sounds with a mod as XML sound assets](overview.md) - How a mod declares its own wav files in an asset XML listed in mod.toml so KSA registers them as sounds you can look up by id, with channel group, 3D range, loop flag, priority and simulation-speed rule

# How-To

* [Declare looping layers that share one SpatialSoundData asset](ship-custom-sounds-as-xml-assets-2.md) - Declare a named SpatialSoundData once, reference it from several Sound assets, turn on FMOD loop mode with a Loop element and ship mono wav files so the engine can spatialise them
* [Declare one-shot sounds with random variants and a simulation-speed rule](ship-custom-sounds-as-xml-assets-1.md) - Ship wav files as Sound assets with two variants each, a spatial range and SimSpeed OnlyLower, list the XML in mod.toml and look the sounds up by id
* [Tune spatial data, priority and pitch variation per weapon class](ship-custom-sounds-as-xml-assets-3.md) - Declare one SpatialSoundData per class of sound so a big gun carries further than a small one, set Priority below Core's explosions so gun channels survive FMOD's voice limit, and add a Pitch range so a repeated shot is not one sample
