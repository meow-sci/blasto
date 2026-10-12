# Game Feature

* [Change loaded game data at runtime and make the renderer rebuild from it](overview.md) - Many KSA renderers bake XML-backed reference data into GPU buffers in their constructors, so editing the reference object does nothing until the renderer is recreated; four patterns for mutate-and-recreate, transactional swap, template assignment and in-place buffer writes

# How-To

* [Assign to the template and call Program.RebuildRenderer](change-loaded-game-data-and-make-the-renderer-notice-3.md) - Add or remove data on a body template, dispose the one renderer that caches it, refresh its body list and ask the whole renderer to rebuild, accepting a short hitch
* [Reflect the mapped buffer and slot, clamp when the camera is near, restore on leaving](change-loaded-game-data-and-make-the-renderer-notice-4.md) - Change one biome material value in the planet renderer's persistently mapped storage buffer without a rebuild, only when the camera crosses a threshold, saving the original and restoring it afterwards
* [Snapshot, mutate the public tree and recreate one renderer by reflection](change-loaded-game-data-and-make-the-renderer-notice-1.md) - Change a body's loaded reference data, then dispose a single renderer and clear its created flag so the game's own rebuild constructs it again from the new data, with a snapshot for exact restore
* [Transactional apply and exact restore of a body's clutter data](change-loaded-game-data-and-make-the-renderer-notice-2.md) - Swap a body's clutter objects in one main-thread step at the solver handoff, retain the originals for exact restore, and hook GroundClutterRenderer rebuild and dispose so a renderer recreation rebuilds or releases the override
