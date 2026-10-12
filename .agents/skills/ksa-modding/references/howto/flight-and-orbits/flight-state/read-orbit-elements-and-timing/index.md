# Game Feature

* [Read orbit elements, apsides and event countdowns safely](overview.md) - Read a vehicle's or celestial's orbit elements, apsides, periods and event countdowns, and choose the right Mu for transfer maths, without tripping on hyperbolic orbits, NaN or UniverseTime sentinels

# How-To

* [Choose Orbit.Mu or Parent.Mu for transfer maths and cache per loaded system](read-orbit-elements-and-timing-3.md) - Pick the gravitational parameter that matches the question (the hub's Parent.Mu for a transfer ellipse, the orbit's own Orbit.Mu for a body's speed) and cache results keyed by the CelestialSystem instance, not its Id
* [Compute orbit readouts and event countdowns from state vectors in CCI](read-orbit-elements-and-timing-1.md) - Derive apsis altitudes, period, time to apsides and nodes, the eccentricity vector and surface velocity components from Orbit and its CCI state vectors
* [Read orbit and encounter data without leaking NaN or UniverseTime sentinels](read-orbit-elements-and-timing-2.md) - Convert apsides, patch events and SOI encounters to finite numbers by gating on IsBound and IsSaturated, so an escape or non-intersecting orbit never produces a bogus timestamp
