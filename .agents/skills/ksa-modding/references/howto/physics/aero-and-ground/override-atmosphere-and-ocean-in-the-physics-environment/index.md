# Game Feature

* [Override a body's atmosphere profile or ocean in the physics environment](overview.md) - How a mod makes the solver read its own air density and pressure profile for a body, or zeroes the ocean in a region so a basement can sit below the waterline

# How-To

* [Postfix PhysicsEnvironment.RecomputePositionalValues on the worker](override-atmosphere-and-ocean-in-the-physics-environment-2.md) - Remove the ocean from a cylinder of a body by zeroing OceanRadius, OceanVolume and OceanSurfaceArea after the game fills the environment, so a basement below sea level is dry to physics, with a volatile region swap and matching render patches
* [Prefix the density and pressure functions and move the private boundary height](override-atmosphere-and-ocean-in-the-physics-environment-1.md) - Replace a body's exponential atmosphere with a measured density and pressure profile for drag, engines and everything else that reads it, by prefixing the two lookup functions and rewriting the private boundary height after the template loads
