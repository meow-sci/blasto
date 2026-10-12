# Game Feature

* [Measure ground slope and surface normal, and detect when a mod-simulated point hits the ground](overview.md) - Derive the local gradient or surface normal from neighbouring height samples and test ground contact for bodies the engine does not simulate, with interpolation and epoch corrections

# How-To

* [Gradient from centred east, west, north and south differences](measure-slope-and-detect-ground-contact-on-terrain-1.md) - Measure local ground slope as the magnitude of a centred gradient on a body-fixed east and north frame, with a ring of spokes kept for roughness and an aim-loop gain derived from the arrival angle
* [Per-frame centre and radius, crossing test, interpolation and near-surface resample](measure-slope-and-detect-ground-contact-on-terrain-3.md) - Stop a mod-simulated round at the terrain by testing a was and now height each sub-step against a per-frame sphere, interpolating the contact point and re-reading the ground near impact
* [Surface normal from four one-metre samples and a cross product](measure-slope-and-detect-ground-contact-on-terrain-2.md) - Compute a unit surface normal by sampling the height field one metre east, west, north and south of a point, crossing the two chords, flipping outward and falling back to radial
