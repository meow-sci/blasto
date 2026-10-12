# Game Feature

* [Teleport a vessel or EVA kitten to an orbit or state vector](overview.md) - How a mod moves a vessel, EVA kitten or debris to an arbitrary position and velocity by building an Orbit with Orbit.CreateFromStateCci and calling Vehicle.Teleport at a window where it survives the physics step

# How-To

* [Orbit solved backwards from a requested pass](teleport-a-vessel-by-building-a-new-orbit-2.md) - Spawn a drone on a computed pass by solving the state vector backwards from a closest-approach point, converting ecliptic to Cci, applying a ballistic correction and attaching the vessel to its parent
* [Teleport at the PrepareFrame handoff and fire at a clicked point](teleport-a-vessel-by-building-a-new-orbit-3.md) - Capture a target-relative click snapshot in the GUI pass and, at the PrepareFrame handoff, build an orbit around the target's parent and teleport the source there with a velocity relative to the target, handling kittens, hulls and terrain
* [Teleport to CCI state vectors in the solver-dispatch window](teleport-a-vessel-by-building-a-new-orbit-1.md) - Build an orbit from a Cci position and velocity with Orbit.CreateFromStateCci and call Vehicle.Teleport from a prefix on ExecuteNextVehicleSolvers, with refill and impulsive-kick variants and the nearest-orbit-job race noted
