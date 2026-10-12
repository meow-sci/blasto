# Game Feature

* [Recreate mod render targets when KSA rebuilds its renderer](overview.md) - KSA recreates swapchain-sized targets on resize, MSAA changes and high-resolution screenshots, so mod pipelines and images tied to those extents must be rebuilt at the same point

# How-To

* [Postfix Program.RebuildRenderer plus a first-frame schedule and per-frame extent check](handle-renderer-rebuilds-1.md) - Rebuild extent-bound mod targets right after the game rebuilds its renderer, force one rebuild after startup, and add a per-frame extent check as a safety net
* [Re-create the private material pipeline on the renderer-rebuild event](handle-renderer-rebuilds-3.md) - Re-enter a thread-static construction scope around the game's RebuildFrameResources so pipelines the mod owns are recompiled with the mod's private material buffer and shader variant
* [Rebuild targets in place on a live size change](handle-renderer-rebuilds-2.md) - Stop recording, wait out the frames in flight, then rebuild the mod's own offscreen target and rewrite its descriptor under a device wait
