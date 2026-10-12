# Game Feature

* [Set, limit and request simulation speed from a mod](overview.md) - Change the time-warp rate from code, request a warp to a time through the game's own auto-warp buffer, and clamp the speed during phases that cannot tolerate warp

# How-To

* [Hold the speed down with a feedback controller over SetSimulationSpeed, with player yield and restore](set-simulation-speed-from-a-mod-3.md) - Clamp the simulation speed only while a feature needs short steps, settle across the two-frame actuator delay, yield to the player and restore the speed when the need passes
* [Request a warp-to-time through InputEvents.AutoWarpBuffer](set-simulation-speed-from-a-mod-2.md) - Ask the game's own auto-warp to warp to a UniverseTime by adding an AutoWarpData to the input buffer that PrepareFrame drains, so the stock warp UI, limits and stop behaviour apply
* [Set the speed with Universe.SetSimulationSpeed and suppress the on-screen alert](set-simulation-speed-from-a-mod-1.md) - Write the simulation speed directly from a drained command, why alert false matters, and what stock auto-warp and the menu pause do to the value
