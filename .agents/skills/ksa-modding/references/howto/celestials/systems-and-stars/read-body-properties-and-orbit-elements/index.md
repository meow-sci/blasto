# Game Feature

* [Read a body's atmosphere, ocean, ring, rotation, tilt, tidal-lock and orbit-element data](overview.md) - Where each per-body number lives in KSA (IParentBody, the body template, transforms and the orbit), how to read atmosphere and ocean data that may be null, sample density and pressure by altitude, and the corrections moons need for tilt and inclination

# How-To

* [Atmosphere height, density and ring templates feeding classification decisions, and angular velocity to a stationary orbit radius](read-body-properties-and-orbit-elements-2.md) - Use a body's atmosphere height and radius to classify gas giants, derive a low-orbit radius, mark ringed bodies and compute the synchronous orbit radius from the angular velocity, all from live data with no per-body config
* [IParentBody.GetAtmosphereReference and GetOceanReference projected into a snapshot](read-body-properties-and-orbit-elements-1.md) - Read mass, radius, spin, atmosphere and ocean data of any body through the IParentBody interface into an immutable snapshot, treating a missing atmosphere or ocean as null and caching constants per body
* [Sample the live density and pressure model by altitude](read-body-properties-and-orbit-elements-3.md) - Plot a body's atmospheric density and pressure against altitude by sampling the game's own PhysicalAtmosphereReference functions at 101 points, so the chart follows any mod that replaces the profile
* [Template and transform reads for tilt, periods and inclination](read-body-properties-and-orbit-elements-4.md) - Read axial tilt, sidereal period, tidal lock, retrograde flag and orbit inclination from the body template, the orbit and the orbital-plane transform, with the corrections the Compendium mod applies for moons
