# Game Feature

* [Crossfade engine loops and play spin-up, loop and tail sounds](overview.md) - How a mod makes a simulated engine or weapon sound alive, either by crossfading two looping layers against RPM and load or by declaring a CompoundSound whose start, loop and stop sounds the engine sequences

# How-To

* [Crossfade an idle loop and a load loop by RPM, with loops cut from recordings](play-a-spin-up-loop-tail-sound-and-crossfade-engine-loops-1.md) - Play two looping channels per car and set their volume and pitch every frame from the simulated RPM and load, using loops cut from CC0 recordings with the recorded RPM kept as a profile number
* [Declare a CompoundSound with spin-up, loop and tail cut from one recording](play-a-spin-up-loop-tail-sound-and-crossfade-engine-loops-2.md) - Cut one recording into spin, loop and tail at envelope-measured points, declare them as a CompoundSound, and let the engine sequence them while the mod only starts, holds and stops one channel
