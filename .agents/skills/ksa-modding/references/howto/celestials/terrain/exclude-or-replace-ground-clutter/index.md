# Game Feature

* [Keep grass, rocks and trees off a custom site, and replace or preserve a planet's ground clutter](overview.md) - Refill the four launch-pad exclusion zones the engine bakes at load, swap a body's clutter types for imported models at the solver handoff, and carry knocked-out or displaced instances across swaps and saves

# How-To

* [Harmony postfix on the private PopulateLaunchPadExclusionZones with the celestial injected](exclude-or-replace-ground-clutter-1.md) - Refill the four ground-clutter exclusion zones with the launch pads nearest the camera by postfixing the private PopulateLaunchPadExclusionZones, using the engine's own formula
* [Per-grid memory with AND-merge, public-API snapshot and replay, native plus sidecar saves](exclude-or-replace-ground-clutter-3.md) - Remember knocked-out and displaced clutter instances per ecotype and grid spacing, replay them into whichever placement is installed, and save stock grids natively and override-only grids in a sidecar
* [Prefix on ExecuteNextClothSolvers builds private clutter bundles with originals kept for exact restore](exclude-or-replace-ground-clutter-2.md) - Replace the models of chosen ground-clutter types on one body by queueing a recipe and committing it in a first-priority prefix at the solver handoff, with renderer hooks so a renderer recreation keeps the override coherent
