# Game Feature

* [Play a sound at a world position and hold its spatial channel](overview.md) - How a mod starts a declared sound so it pans, attenuates, Doppler-shifts and is muffled by air as if it came from a point in the world, and how it keeps and moves the channel afterwards

# How-To

* [Fire a positional sound with GameAudio.PlaySound and SpatialAudio(Astronomical, offset)](play-a-sound-at-a-world-position-and-hold-its-channel-1.md) - Play a declared sound at an arbitrary world point by anchoring a SpatialAudio on a live vehicle near it, so pan, attenuation, Doppler and air muffling are the engine's job
* [Hold one channel per mod-simulated source and move it every frame](play-a-sound-at-a-world-position-and-hold-its-channel-2.md) - Build an Ego SpatialAudio by hand for sources the mod simulates, keep the returned IChannel, re-place it each frame after the source is stepped, and sweep the table against the live roster
* [Keep one looping channel per craft with guarded move and cut](play-a-sound-at-a-world-position-and-hold-its-channel-3.md) - Attach a looping sound to a vehicle for as long as it exists, with a per-craft channel table, TryMove and Cut wrappers around every call, and a dead channel replaced on the next frame
