# Subdirectories

* [aero-and-ground](aero-and-ground/index.md) - The environment a craft flies and drives in: aerodynamic force models, atmosphere and ocean overrides, terrain height overrides, ground sampling and wheel suspension.
* [colliders](colliders/index.md) - Bepu colliders and contacts: the shared shape registry, static colliders, clutter colliders, collision suppression, collider scaling, moving platforms and a private Bepu world.
* [kinematics](kinematics/index.md) - Moving whole vessels by fiat: teleporting to an orbit or state vector, placing on a surface and welding one vessel to another.
* [mod-simulation](mod-simulation/index.md) - Bodies the mod simulates itself: sub-stepped integration, tunnelling-free hits, ray casts against hulls and terrain, and drag and gravity.
* [solver](solver/index.md) - Writing simulated state safely: the physics-safe windows, worker-thread patches, injected forces and impulses, and keeping pushed craft off rails.
