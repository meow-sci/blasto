# Game Feature

* [Reserve or grow game GPU buffers for content added at runtime](overview.md) - The game sizes its shared mesh buffer and star instance buffer at load, so runtime meshes or more stars need headroom reserved before the buffer exists or the buffer re-created while frames are in flight

# How-To

* [Inflate DeviceMeshInterleaved.Shared before the first bind allocates the buffer](grow-game-gpu-buffers-for-runtime-content-1.md) - Reserve headroom in the game's shared interleaved mesh buffer by adding to its size counters at StarMapAllModsLoaded and rewinding the bump cursor on the first UI frame, so meshes created later get valid ranges
* [Re-create the star instance buffer by reflection and restyle stars with a Harmony prefix](grow-game-gpu-buffers-for-runtime-content-2.md) - Grow the background star instance buffer at runtime by writing readonly fields and invoking the protected CreateInstanceBuffer, and rewrite every star's size and colour from a prefix on InstancedStarTechnique.AddInstance
