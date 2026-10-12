# Game Feature

* [Capture rendered frames from KSA](overview.md) - Grab the finished scene for streaming or automated visual testing without stalling the GPU by recording a copy in the frame command buffer and reading it back when the staging slot is reused, and render part thumbnails at runtime with the game thumbnail pipeline

# How-To

* [ThumbnailCreator framing plus ThumbnailDynamic per-frame submit, two parts per frame](capture-rendered-frames-2.md) - Render runtime part thumbnails with the game's thumbnail viewport framing, a private transient command pool and a fence wait, submitted before OnPreRender
* [Transpiler before the final End, blit into per-slot staging buffers, readback on slot reuse](capture-rendered-frames-1.md) - Record a downscaling blit and a buffer copy into the frame command buffer just before it ends, then read each slot back when the engine reuses it, with no stall and no second submit
